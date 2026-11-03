using System.Text.Json;
using Studio5000.Extensibility;

namespace Studio5000.Core;

// Explicit manifest opt-in only: extension instances must not depend on private in-process state.
public sealed class ExtensionWorkerPool : IExtensionWorker
{
    private readonly Func<IExtensionWorker> factory;
    private readonly bool releaseIdleWorkers;
    private readonly SemaphoreSlim slots, describeGate = new(1);
    private readonly object sync = new();
    private readonly Queue<IExtensionWorker> idle = new();
    private readonly HashSet<IExtensionWorker> workers = [];
    private readonly CancellationTokenSource shutdown = new();
    private ExtensionOperation[]? operations;
    private string? catalog;
    private bool disposed;
    public ExtensionWorkerPool(int maximumWorkers, Func<IExtensionWorker> factory, bool releaseIdleWorkers = false)
    {
        if (maximumWorkers is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(maximumWorkers));
        this.factory = factory; this.releaseIdleWorkers = releaseIdleWorkers; slots = new(maximumWorkers, maximumWorkers);
        if (!releaseIdleWorkers) { var worker = factory(); workers.Add(worker); idle.Enqueue(worker); }
    }
    public async Task<ExtensionOperation[]> DescribeAsync(CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
        await describeGate.WaitAsync(linked.Token);
        try
        {
            lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
            if (operations is not null) return operations.ToArray();
            var worker = releaseIdleWorkers ? CreateWorker() : idle.Peek();
            try
            {
                var result = await worker.DescribeAsync(linked.Token);
                lock (sync)
                {
                    ObjectDisposedException.ThrowIf(disposed, this);
                    catalog = JsonSerializer.Serialize(result, ExtensionProtocol.Json); operations = result;
                }
                return result.ToArray();
            }
            finally { if (releaseIdleWorkers) ReleaseWorker(worker); }
        }
        finally { describeGate.Release(); }
    }
    public async Task<JsonElement> InvokeAsync(string operation, JsonElement arguments, CancellationToken ct)
    {
        await DescribeAsync(ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
        await slots.WaitAsync(linked.Token); IExtensionWorker? worker = null; var reusable = true;
        try
        {
            var fresh = false;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (idle.Count != 0) worker = idle.Dequeue();
            }
            if (worker is null) { worker = CreateWorker(); fresh = true; }
            if (fresh && JsonSerializer.Serialize(await worker.DescribeAsync(linked.Token), ExtensionProtocol.Json) != catalog)
            { reusable = false; throw new OperationException("invalid_extension", "Parallel worker catalog differs from the validated generation."); }
            return await worker.InvokeAsync(operation, arguments, linked.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException || ex is OperationException { Code: "extension_indeterminate" })
        { reusable = false; throw; }
        finally
        {
            try
            {
                if (worker is not null)
                {
                    var release = false;
                    lock (sync)
                    {
                        if (disposed || !reusable || releaseIdleWorkers) release = workers.Remove(worker);
                        else idle.Enqueue(worker);
                    }
                    if (release) worker.Dispose();
                }
            }
            finally { slots.Release(); }
        }
    }
    private IExtensionWorker CreateWorker()
    {
        // Process creation/admission stays outside the pool lock. If shutdown
        // wins the race, dispose only the worker this factory just returned.
        var worker = factory();
        lock (sync) { if (!disposed) { workers.Add(worker); return worker; } }
        worker.Dispose(); throw new ObjectDisposedException(nameof(ExtensionWorkerPool));
    }
    private void ReleaseWorker(IExtensionWorker worker)
    {
        bool owned; lock (sync) owned = workers.Remove(worker);
        if (owned) worker.Dispose();
    }
    public void Dispose()
    {
        IExtensionWorker[] owned;
        lock (sync) { if (disposed) return; disposed = true; owned = workers.ToArray(); workers.Clear(); idle.Clear(); }
        List<Exception> errors = [];
        try { shutdown.Cancel(); } catch (Exception error) { errors.Add(error); }
        foreach (var worker in owned)
            try { worker.Dispose(); } catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("One or more extension workers could not be disposed; inspect retained ownership.", errors);
    }
}
