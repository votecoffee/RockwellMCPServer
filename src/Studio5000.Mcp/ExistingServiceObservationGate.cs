using Studio5000.Core;
using Studio5000.Core.Resources;

namespace Studio5000.Mcp;

// Frontend-only readiness. The production delegate can only observe an existing
// genuine service; it never ensures, starts or recovers one.
internal sealed class ExistingServiceObservationGate(Func<Task<OfflineServiceReady>> observe, IResourceProcessDriver driver)
{
    private readonly object sync = new();
    private Task<OfflineServiceReady>? current;
    private bool bound, unavailable;

    public async Task<OfflineServiceReady> Ready(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        Task<OfflineServiceReady> observation;
        lock (sync)
        {
            ct.ThrowIfCancellationRequested();
            if (unavailable) throw BoundUnavailable();
            // Only a later caller may replace an ACTUALLY FAULTED, never-bound
            // observation. In-flight, canceled and successful tasks stay intact.
            if (current is null || !bound && current.IsFaulted)
                current = Task.Run(ObserveAndBind);
            observation = current;
        }
        OfflineServiceReady existing;
        try { existing = await observation.WaitAsync(ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            bool later;
            lock (sync) later = !bound && observation.IsFaulted;
            throw new ExistingServiceObservationException("unbound-discovery", later,
                "No genuine owner was verified by this observation; no engineering request was dispatched. " +
                (later ? "A later explicit request may check the existing service once; this request was not retried."
                    : "This request was not retried; this connection cannot observe a replacement owner."), error);
        }
        lock (sync)
        {
            if (unavailable) throw BoundUnavailable();
            try
            {
                if (driver.State(existing.Owner) == "alive") return existing;
            }
            catch (Exception error)
            {
                unavailable = true;
                throw BoundUnavailable(error);
            }
            unavailable = true;
            throw BoundUnavailable();
        }
    }

    private async Task<OfflineServiceReady> ObserveAndBind()
    {
        var existing = await observe();
        // Pin before this task completes, even if every waiting caller canceled.
        lock (sync) bound = true;
        return existing;
    }

    private static ExistingServiceObservationException BoundUnavailable(Exception? inner = null) => new(
        "bound-owner-unavailable", false,
        "The verified genuine owner is unavailable or unverified. This connection remains pinned to that owner " +
        "and cannot observe or adopt a replacement. No engineering request was dispatched; reconnect after operator recovery.", inner);
}

internal sealed class ExistingServiceObservationException(string phase, bool laterExplicitRequestMayObserve,
    string message, Exception? inner = null) : Exception(message, inner)
{
    public string Phase { get; } = phase;
    public bool LaterExplicitRequestMayObserve { get; } = laterExplicitRequestMayObserve;
}
