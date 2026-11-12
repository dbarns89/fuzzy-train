namespace VcxDiag.Core.J2534;

/// <summary>Protocol IDs from SAE J2534-1 (v04.04) and J2534-2.</summary>
public enum ProtocolId : uint
{
    J1850VPW = 0x01,
    J1850PWM = 0x02,
    ISO9141 = 0x03,
    ISO14230 = 0x04,
    CAN = 0x05,
    ISO15765 = 0x06,
    // J2534-2 "pin switched" protocols
    CAN_PS = 0x8004,
    ISO15765_PS = 0x8005,
    SW_ISO15765_PS = 0x8007,
    SW_CAN_PS = 0x8008,
}

public static class J2534Status
{
    public const uint NoError = 0x00;
    public const uint NotSupported = 0x01;
    public const uint InvalidChannelId = 0x02;
    public const uint InvalidProtocolId = 0x03;
    public const uint NullParameter = 0x04;
    public const uint InvalidIoctlValue = 0x05;
    public const uint InvalidFlags = 0x06;
    public const uint Failed = 0x07;
    public const uint DeviceNotConnected = 0x08;
    public const uint Timeout = 0x09;
    public const uint InvalidMsg = 0x0A;
    public const uint InvalidTimeInterval = 0x0B;
    public const uint ExceededLimit = 0x0C;
    public const uint InvalidMsgId = 0x0D;
    public const uint DeviceInUse = 0x0E;
    public const uint InvalidIoctlId = 0x0F;
    public const uint BufferEmpty = 0x10;
    public const uint BufferFull = 0x11;
    public const uint BufferOverflow = 0x12;
    public const uint PinInvalid = 0x13;
    public const uint ChannelInUse = 0x14;
    public const uint MsgProtocolId = 0x15;
    public const uint InvalidFilterId = 0x16;
    public const uint NoFlowControl = 0x17;
    public const uint NotUnique = 0x18;
    public const uint InvalidBaudrate = 0x19;
    public const uint InvalidDeviceId = 0x1A;

    public static string Describe(uint code) => code switch
    {
        NoError => "no error",
        NotSupported => "not supported by this device",
        InvalidChannelId => "invalid channel",
        InvalidProtocolId => "protocol not supported by this device",
        NullParameter => "null parameter",
        InvalidIoctlValue => "invalid ioctl value",
        InvalidFlags => "invalid flags",
        Failed => "device reported a failure",
        DeviceNotConnected => "device not connected (check USB and VX Manager)",
        Timeout => "timeout",
        InvalidMsg => "invalid message",
        ExceededLimit => "device limit exceeded (too many filters/channels)",
        DeviceInUse => "device is in use by another program (close Tech2Win/GDS2/VX Manager diagnostics)",
        BufferEmpty => "no messages",
        BufferFull => "transmit buffer full",
        BufferOverflow => "receive buffer overflow",
        PinInvalid => "invalid pin selection",
        ChannelInUse => "channel already in use",
        InvalidBaudrate => "baud rate not supported",
        InvalidDeviceId => "invalid device",
        _ => $"error 0x{code:X2}",
    };
}

public static class J2534Ioctl
{
    public const uint GetConfig = 0x01;
    public const uint SetConfig = 0x02;
    public const uint ReadVbatt = 0x03;
    public const uint ClearTxBuffer = 0x07;
    public const uint ClearRxBuffer = 0x08;
    public const uint ClearMsgFilters = 0x0A;
}

public static class J2534Config
{
    public const uint DataRate = 0x01;
    public const uint Loopback = 0x03;
    /// <summary>J2534-2 pin selection for _PS protocols: 0xPPSS (primary, secondary pin).</summary>
    public const uint J1962Pins = 0x8001;
}

public static class J2534Flags
{
    public const uint Can29BitId = 0x0100;
    public const uint TxMsgType = 0x01;      // RxStatus: echo of our own transmit
    public const uint StartOfMessage = 0x02; // RxStatus: ISO15765 first frame indication
    public const uint PassFilter = 0x01;
    public const uint BlockFilter = 0x02;
}
