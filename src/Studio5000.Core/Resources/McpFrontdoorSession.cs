namespace Studio5000.Core.Resources;

// No native connection, worker or private child is owned here. The actual SDK
// process owns only a typed metadata observation and OS admission handles.
public sealed class McpFrontdoorSession : IDisposable
{
    public ResourceLedger Ledger { get; }
    public ResourceLedger.ResourceOwner Owner { get; }
    public ResourceRecord Record { get; private set; }
    private McpClientLease? lease;
    private bool closed;
    private McpFrontdoorSession(ResourceLedger ledger, ResourceLedger.ResourceOwner owner, ResourceRecord record, McpClientLease lease)
    { Ledger = ledger; Owner = owner; Record = record; this.lease = lease; }

    public static McpFrontdoorSession Open(string root, string registry, McpFrontdoorPolicy policy, CancellationToken ct = default)
        => Open(root, registry, policy, ct, null);
    internal static McpFrontdoorSession Open(string root, string registry, McpFrontdoorPolicy policy, CancellationToken ct, ResourceLedger? suppliedLedger,
        Action<HashSet<string>>? reviewPredecessors = null)
    {
        var lease = McpClientLease.AcquireFrontdoor(root, policy, ct);
        try
        {
            var ledger = suppliedLedger ?? new ResourceLedger(registry, new WindowsResourceProcessDriver(), policy.MaximumMetadataClients);
            var owner = ledger.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId);
            var images = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { owner.Identity.Executable };
            var admitted = owner.TryAdmitFrontdoor(images.ToArray());
            if (admitted is not null) return new(ledger, owner, admitted, lease);
            // Only a full metadata budget needs cross-version recovery. Validate
            // predecessor provenance outside the lock, then inspect a fresh corpus.
            try
            {
                if (reviewPredecessors is not null) reviewPredecessors(images);
                else
                {
                    var pin = OfflineServicePin.Load(root, Path.GetDirectoryName(owner.Identity.Executable)!);
                    foreach (var previous in pin.PreviousFrontends.Where(p => p.Files.Any(f => f.Path == McpFrontdoorPolicy.FileName)))
                    {
                        _ = McpFrontdoorPolicy.Read(previous.Directory) ?? throw new InvalidOperationException("Prior policy disappeared.");
                        images.Add(Path.Combine(previous.Directory, "Studio5000.StdioHost.exe"));
                        images.Add(Path.Combine(previous.Directory, "Studio5000.Mcp.exe"));
                    }
                }
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException)
            { Console.Error.WriteLine("Prior metadata observations retained: " + error.Message); }
            return new(ledger, owner, owner.AdmitFrontdoor(images.ToArray()), lease);
        }
        catch { lease.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (closed) return;
        // Closing the OS lease precedes the terminal metadata attestation.
        // Failure leaves a retained claim; it never terminates this/other apps.
        Interlocked.Exchange(ref lease, null)?.Dispose();
        Record = Owner.CloseFrontdoor(Record.ResourceId, Record.Revision);
        closed = true;
    }
}
