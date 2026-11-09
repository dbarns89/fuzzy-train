"""
VCX Diag bridge: exposes a J2534 pass-thru device (e.g. VXDIAG VCX Nano) to the
iPhone app over the local network.

Runs on Windows with the VX Manager driver installed. The VCX J2534 DLL is
32-bit, so run this with a 32-bit Python 3.9+ build.

Wire protocol: newline-delimited JSON over TCP (default port 7534).
  -> {"id": 1, "op": "open"}
  -> {"id": 2, "op": "connect", "protocol": "J1850VPW", "baud": 10400}
  -> {"id": 3, "op": "filter", "channel": 1, "mask": "000000", "pattern": "000000"}
  -> {"id": 4, "op": "request", "channel": 1, "data": "686AF10100", "timeout_ms": 300}
  <- {"id": 4, "ok": true, "frames": ["486B104100BE3FB813"]}
  -> {"id": 5, "op": "disconnect", "channel": 1}
  -> {"id": 6, "op": "close"}
  -> {"id": 7, "op": "info"}
Errors come back as {"id": n, "ok": false, "error": "..."}.

Usage:
  python vcx_bridge.py                 # real hardware, first J2534 device found
  python vcx_bridge.py --list          # list installed J2534 drivers
  python vcx_bridge.py --dll PATH      # use a specific J2534 DLL
  python vcx_bridge.py --simulate      # fake vehicle, no hardware (any OS)
"""

from __future__ import annotations

import argparse
import ctypes
import json
import socket
import socketserver
import sys
import threading
import time

PORT = 7534

# --- J2534 constants (SAE J2534-1 v04.04 and J2534-2) -----------------------
PROTOCOLS = {
    "J1850VPW": 0x01,
    "J1850PWM": 0x02,
    "ISO9141": 0x03,
    "ISO14230": 0x04,
    "CAN": 0x05,
    "ISO15765": 0x06,
    "SW_CAN_PS": 0x8004,       # GM single-wire CAN (GMLAN low speed), raw
    "SW_ISO15765_PS": 0x8007,  # GM single-wire CAN with ISO-TP
}
PASS_FILTER = 0x01
FLOW_CONTROL_FILTER = 0x03
CAN_29BIT_ID = 0x100
ISO15765_FRAME_PAD = 0x40
STATUS_NOERROR = 0
ERR_BUFFER_EMPTY = 0x10
ERR_TIMEOUT = 0x09
TX_MSG_TYPE = 0x01       # RxStatus bit: echo of our own transmission
START_OF_MESSAGE = 0x02  # RxStatus bit: ISO15765 first-frame indication
READ_VBATT = 0x03


class PASSTHRU_MSG(ctypes.Structure):
    _fields_ = [
        ("ProtocolID", ctypes.c_ulong),
        ("RxStatus", ctypes.c_ulong),
        ("TxFlags", ctypes.c_ulong),
        ("Timestamp", ctypes.c_ulong),
        ("DataSize", ctypes.c_ulong),
        ("ExtraDataIndex", ctypes.c_ulong),
        ("Data", ctypes.c_ubyte * 4128),
    ]


def make_msg(protocol: int, data: bytes, tx_flags: int = 0) -> PASSTHRU_MSG:
    m = PASSTHRU_MSG()
    m.ProtocolID = protocol
    m.TxFlags = tx_flags
    m.DataSize = len(data)
    for i, b in enumerate(data):
        m.Data[i] = b
    return m


def list_drivers() -> list[dict]:
    """Read installed J2534 drivers from the Windows registry."""
    try:
        import winreg
    except ImportError:
        return []
    found = []
    for root in (r"SOFTWARE\WOW6432Node\PassThruSupport.04.04", r"SOFTWARE\PassThruSupport.04.04"):
        try:
            key = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, root)
        except OSError:
            continue
        i = 0
        while True:
            try:
                name = winreg.EnumKey(key, i)
            except OSError:
                break
            i += 1
            sub = winreg.OpenKey(key, name)
            try:
                dll = winreg.QueryValueEx(sub, "FunctionLibrary")[0]
            except OSError:
                continue
            found.append({"name": name, "dll": dll})
    return found


class J2534Device:
    """Thin ctypes wrapper over a vendor J2534 DLL."""

    def __init__(self, dll_path: str):
        self.dll = ctypes.WinDLL(dll_path)  # J2534 uses __stdcall
        self.device_id = None
        self.protocol_of = {}

    def _check(self, rc: int, what: str):
        if rc != STATUS_NOERROR:
            buf = ctypes.create_string_buffer(80)
            try:
                self.dll.PassThruGetLastError(buf)
                detail = buf.value.decode(errors="replace")
            except Exception:
                detail = ""
            raise RuntimeError(f"{what} failed (0x{rc:02X}) {detail}".strip())

    def open(self):
        dev = ctypes.c_ulong()
        self._check(self.dll.PassThruOpen(None, ctypes.byref(dev)), "PassThruOpen")
        self.device_id = dev.value

    def close(self):
        if self.device_id is not None:
            self.dll.PassThruClose(self.device_id)
            self.device_id = None

    def info(self) -> dict:
        fw, dll, api = (ctypes.create_string_buffer(80) for _ in range(3))
        self._check(self.dll.PassThruReadVersion(self.device_id, fw, dll, api), "PassThruReadVersion")
        volts = ctypes.c_ulong()
        self.dll.PassThruIoctl(self.device_id, READ_VBATT, None, ctypes.byref(volts))
        return {"firmware": fw.value.decode(), "dll": dll.value.decode(),
                "api": api.value.decode(), "battery_v": volts.value / 1000.0}

    def connect(self, protocol: str, baud: int, flags: int = 0) -> int:
        pid = PROTOCOLS[protocol]
        ch = ctypes.c_ulong()
        self._check(self.dll.PassThruConnect(self.device_id, pid, flags, baud, ctypes.byref(ch)),
                    "PassThruConnect")
        self.protocol_of[ch.value] = pid
        return ch.value

    def disconnect(self, channel: int):
        self.dll.PassThruDisconnect(channel)
        self.protocol_of.pop(channel, None)

    def filter(self, channel: int, mask: bytes, pattern: bytes, flow: bytes | None = None,
               tx_flags: int = 0) -> int:
        pid = self.protocol_of[channel]
        m, p = make_msg(pid, mask, tx_flags), make_msg(pid, pattern, tx_flags)
        fid = ctypes.c_ulong()
        if flow is not None:
            f = make_msg(pid, flow, tx_flags)
            rc = self.dll.PassThruStartMsgFilter(channel, FLOW_CONTROL_FILTER, ctypes.byref(m),
                                                 ctypes.byref(p), ctypes.byref(f), ctypes.byref(fid))
        else:
            rc = self.dll.PassThruStartMsgFilter(channel, PASS_FILTER, ctypes.byref(m),
                                                 ctypes.byref(p), None, ctypes.byref(fid))
        self._check(rc, "PassThruStartMsgFilter")
        return fid.value

    def request(self, channel: int, data: bytes, timeout_ms: int, tx_flags: int = 0) -> list[bytes]:
        pid = self.protocol_of[channel]
        self.dll.PassThruIoctl(channel, 0x08, None, None)  # CLEAR_RX_BUFFER
        msg = make_msg(pid, data, tx_flags)
        n = ctypes.c_ulong(1)
        self._check(self.dll.PassThruWriteMsgs(channel, ctypes.byref(msg), ctypes.byref(n), 100),
                    "PassThruWriteMsgs")
        frames = []
        deadline = time.monotonic() + timeout_ms / 1000.0
        while time.monotonic() < deadline:
            rx = PASSTHRU_MSG()
            n = ctypes.c_ulong(1)
            rc = self.dll.PassThruReadMsgs(channel, ctypes.byref(rx), ctypes.byref(n), 50)
            if rc in (ERR_BUFFER_EMPTY, ERR_TIMEOUT) or n.value == 0:
                continue
            self._check(rc, "PassThruReadMsgs")
            if rx.RxStatus & (TX_MSG_TYPE | START_OF_MESSAGE):
                continue
            frames.append(bytes(rx.Data[: rx.DataSize]))
        return frames


class SimulatedDevice:
    """A fake 2004 GM truck on J1850 VPW so the app can be tried without hardware."""

    def __init__(self):
        self.protocol_of = {}

    def open(self): pass
    def close(self): pass

    def info(self):
        return {"firmware": "SIM 1.0", "dll": "simulated", "api": "04.04", "battery_v": 12.6}

    def connect(self, protocol, baud, flags=0):
        ch = len(self.protocol_of) + 1
        self.protocol_of[ch] = PROTOCOLS[protocol]
        return ch

    def disconnect(self, channel): self.protocol_of.pop(channel, None)
    def filter(self, *a, **k): return 1

    def request(self, channel, data, timeout_ms, tx_flags=0):
        time.sleep(0.05)
        hdr_ecm = bytes([0x48, 0x6B, 0x10])
        mode = data[3] if len(data) > 3 else None
        if mode == 0x01:
            pid = data[4]
            table = {0x00: b"\xBE\x3F\xB8\x13", 0x05: bytes([0x7B]), 0x0C: bytes([0x0B, 0xB8]),
                     0x0D: bytes([0x00]), 0x11: bytes([0x26]), 0x0B: bytes([0x21])}
            if pid in table:
                return [hdr_ecm + bytes([0x41, pid]) + table[pid]]
            return []
        if mode == 0x03:
            return [hdr_ecm + bytes([0x43, 0x01, 0x71, 0x04, 0x20, 0x00, 0x00])]  # P0171, P0420
        if mode == 0x04:
            return [hdr_ecm + bytes([0x44])]
        if mode == 0x09 and data[4] == 0x02:
            padded = b"\x00\x00\x00" + b"1GCEK19T04Z000000"
            return [hdr_ecm + bytes([0x49, 0x02, i + 1]) + padded[i * 4:(i + 1) * 4] for i in range(5)]
        # GM Class 2 physical "read DTCs by status" ($19) to one module
        if data[0] == 0x6C and mode == 0x19:
            codes = {0x10: [(0x01, 0x71, 0x81), (0x04, 0x20, 0x01)], 0x18: [],
                     0x28: [(0x40, 0x35, 0x80)], 0x40: [(0xD0, 0x73, 0x00)], 0x58: []}
            target = data[1]
            if target not in codes:
                return []
            hdr = bytes([0x6C, 0xF1, target])
            return [hdr + bytes([0x59, hi, lo, st]) for hi, lo, st in codes[target]] + \
                   [hdr + bytes([0x59, 0x00, 0x00, 0xFF])]
        if data[0] == 0x6C and mode == 0x14:
            return [bytes([0x6C, 0xF1, data[1], 0x54])]
        return []


class Handler(socketserver.StreamRequestHandler):
    device = None
    lock = threading.Lock()

    def handle(self):
        peer = self.client_address[0]
        print(f"[bridge] client connected: {peer}")
        for line in self.rfile:
            try:
                req = json.loads(line)
            except json.JSONDecodeError:
                continue
            resp = {"id": req.get("id")}
            try:
                with self.lock:
                    resp.update(self.dispatch(req))
                resp["ok"] = True
            except Exception as e:  # report every failure back to the app
                resp.update(ok=False, error=str(e))
            self.wfile.write((json.dumps(resp) + "\n").encode())
        print(f"[bridge] client disconnected: {peer}")

    def dispatch(self, req: dict) -> dict:
        d, op = self.device, req.get("op")
        hx = lambda k: bytes.fromhex(req[k]) if req.get(k) is not None else None
        if op == "hello":
            return {"bridge": "vcx-diag", "version": 1, "protocols": list(PROTOCOLS)}
        if op == "open":
            d.open(); return {}
        if op == "close":
            d.close(); return {}
        if op == "info":
            return d.info()
        if op == "connect":
            flags = CAN_29BIT_ID if req.get("can29") else 0
            return {"channel": d.connect(req["protocol"], int(req["baud"]), flags)}
        if op == "disconnect":
            d.disconnect(int(req["channel"])); return {}
        if op == "filter":
            tx = ISO15765_FRAME_PAD if req.get("pad") else 0
            return {"filter": d.filter(int(req["channel"]), hx("mask"), hx("pattern"), hx("flow"), tx)}
        if op == "request":
            tx = ISO15765_FRAME_PAD if req.get("pad") else 0
            frames = d.request(int(req["channel"]), hx("data"), int(req.get("timeout_ms", 300)), tx)
            return {"frames": [f.hex().upper() for f in frames]}
        raise ValueError(f"unknown op {op!r}")


class Server(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", type=int, default=PORT)
    ap.add_argument("--dll", help="path to a J2534 DLL (default: first registered driver)")
    ap.add_argument("--list", action="store_true", help="list installed J2534 drivers and exit")
    ap.add_argument("--simulate", action="store_true", help="fake vehicle, no hardware")
    args = ap.parse_args()

    if args.list:
        for drv in list_drivers():
            print(f"{drv['name']}: {drv['dll']}")
        return

    if args.simulate:
        Handler.device = SimulatedDevice()
        print("[bridge] SIMULATION mode")
    else:
        dll = args.dll
        if not dll:
            drivers = list_drivers()
            vx = [x for x in drivers if "vx" in x["name"].lower() or "vcx" in x["name"].lower()]
            pick = (vx or drivers or [None])[0]
            if not pick:
                sys.exit("No J2534 driver found. Install VX Manager, or pass --dll.")
            dll = pick["dll"]
            print(f"[bridge] using {pick['name']}: {dll}")
        Handler.device = J2534Device(dll)

    host_ip = socket.gethostbyname(socket.gethostname())
    with Server(("0.0.0.0", args.port), Handler) as srv:
        print(f"[bridge] listening on {host_ip}:{args.port}  (enter this in the iPhone app)")
        srv.serve_forever()


if __name__ == "__main__":
    main()
