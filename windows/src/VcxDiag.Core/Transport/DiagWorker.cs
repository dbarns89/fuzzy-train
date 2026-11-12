using System.Collections.Concurrent;

namespace VcxDiag.Core.Transport;

/// <summary>
/// Runs every interface call on one dedicated background thread. Most J2534 drivers are
/// not thread safe, and this keeps the UI responsive while the bus is busy.
/// </summary>
public sealed class DiagWorker : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public DiagWorker()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "VCX bus worker" };
        _thread.Start();
    }

    private void Loop()
    {
        foreach (var work in _queue.GetConsumingEnumerable()) work();
    }

    public Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception e) { tcs.SetException(e); }
        });
        return tcs.Task;
    }

    public Task Run(Action work) => Run(() => { work(); return true; });

    public void Dispose()
    {
        _queue.CompleteAdding();
        _thread.Join(2000);
    }
}
