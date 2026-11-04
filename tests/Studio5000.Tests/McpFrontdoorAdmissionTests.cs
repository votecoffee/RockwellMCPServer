using System.Text.Json;
using Studio5000.Core;
using Studio5000.Core.Resources;
using Xunit;

namespace Studio5000.Tests;

public sealed class McpFrontdoorAdmissionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-frontdoor-" + Guid.NewGuid().ToString("N"));
    private readonly Driver driver = new();
    private readonly ResourceLedger execution, metadata;
    private sealed class Driver : IResourceProcessDriver
    {
        internal ProcessIdentity Current = null!;
        internal readonly Dictionary<ProcessIdentity, string> States = [];
        public ProcessIdentity CurrentIdentity() => Current;
        public ProcessIdentity Capture(uint pid) => throw new InvalidOperationException("Admission must use the genuine current identity.");
        public string State(ProcessIdentity value) => value == Current ? "alive" : States.GetValueOrDefault(value, "absent");
        public IResourceProcessHandle Open(ProcessIdentity value) => throw new InvalidOperationException("Metadata cannot acquire a termination handle.");
    }
    private ProcessIdentity Identity(uint pid, string image = "frontdoor.exe") =>
        new ProcessIdentity(new('a', 64), pid, "123", Path.Combine(root, image)).Normalize();
    public McpFrontdoorAdmissionTests()
    {
        Directory.CreateDirectory(root); driver.Current = Identity(100);
        execution = new(Path.Combine(root, "registry"), driver, 16);
        metadata = new(Path.Combine(root, "registry"), driver, 8);
    }
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void Normal_session_admission_does_not_review_unused_predecessor_closures()
    {
        if (!OperatingSystem.IsWindows()) return;
        var reviews = 0;
        using var session = McpFrontdoorSession.Open(root, Path.Combine(root, "registry"), new(), default, metadata,
            _ => reviews++);
        Assert.Equal("active", session.Record.State);
        Assert.Equal(0, reviews);
    }

    [Theory]
    [InlineData("departed")] [InlineData("unknown")] [InlineData("review-failed")]
    public void Full_metadata_budget_reviews_predecessors_before_recovery_and_keeps_uncertainty(string condition)
    {
        if (!OperatingSystem.IsWindows()) return;
        var ledger = new ResourceLedger(Path.Combine(root, "recovery-registry"), driver, 1);
        var old = ledger.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId).AdmitFrontdoor();
        var oldPath = Path.Combine(ledger.DirectoryPath, old.ResourceId + ".json"); var bytes = File.ReadAllBytes(oldPath);
        driver.Current = Identity(200, "new.exe"); driver.States[old.Owner] = condition == "unknown" ? "unknown" : "absent";
        var reviews = 0; var reads = 0; ledger.BeforeRecordsRead = () => reads++;
        var policy = new McpFrontdoorPolicy(MaximumMetadataClients: 1, MaximumStartingClients: 1);
        void Review(HashSet<string> images)
        {
            reviews++;
            if (condition == "review-failed") throw new IOException("Synthetic unavailable predecessor proof");
            images.Add(old.Owner.Executable);
        }
        if (condition == "departed")
        {
            using var session = McpFrontdoorSession.Open(root, Path.Combine(root, "recovery-registry"), policy, default, ledger, Review);
            Assert.Equal("released", ledger.Get(old.ResourceId).State); Assert.Equal("active", session.Record.State);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => McpFrontdoorSession.Open(root, Path.Combine(root, "recovery-registry"), policy, default, ledger, Review));
            Assert.Equal(bytes, File.ReadAllBytes(oldPath));
            using var available = McpClientLease.AcquireFrontdoor(root, policy); Assert.Equal(0, available.Slot);
        }
        Assert.Equal(1, reviews); Assert.Equal(2, reads);
    }

    [Fact]
    public void Session_open_scans_history_once_without_changing_unrelated_claims()
    {
        if (!OperatingSystem.IsWindows()) return;
        execution.Owner("rockwell-mcp-server").BeginLaunch("Preserved uncertain native fixture", "sdk-worker");
        var original = Directory.GetFiles(execution.DirectoryPath, "*.json").ToDictionary(p => p, File.ReadAllBytes);
        var reads = 0; metadata.BeforeRecordsRead = () => reads++;
        using var session = McpFrontdoorSession.Open(root, Path.Combine(root, "registry"), new(), default, metadata);
        Assert.Equal(1, reads);
        Assert.Equal("active", session.Record.State);
        Assert.All(original, entry => Assert.Equal(entry.Value, File.ReadAllBytes(entry.Key)));
    }

    [Theory]
    [InlineData("absent")] [InlineData("unknown")] [InlineData("barrier")] [InlineData("member-conflict")]
    public void Combined_admission_keeps_the_same_reconciliation_and_conflict_rules(string condition)
    {
        if (!OperatingSystem.IsWindows()) return;
        var old = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId).AdmitFrontdoor();
        if (condition == "barrier") old = old with { RecoveryBarrier = "Retain uncertainty" };
        var path = Path.Combine(metadata.DirectoryPath, old.ResourceId + ".json");
        var bytes = ResourceJson.Write(old); File.WriteAllBytes(path, bytes);
        if (condition == "member-conflict")
        {
            var conflict = old with { ResourceId = Guid.NewGuid().ToString("N"), AppId = "other-app", Owner = Identity(900), Members = [old.Owner] };
            File.WriteAllBytes(Path.Combine(metadata.DirectoryPath, conflict.ResourceId + ".json"), ResourceJson.Write(conflict));
        }
        driver.Current = Identity(200, "new.exe"); driver.States[old.Owner] = condition == "unknown" ? "unknown" : "absent";
        var reads = 0; metadata.BeforeRecordsRead = () => reads++;
        var record = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId).AdmitFrontdoor([old.Owner.Executable]);
        Assert.Equal(1, reads); Assert.Equal("active", record.State);
        if (condition == "absent") Assert.Equal("released", metadata.Get(old.ResourceId).State);
        else Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    [Fact]
    public void Combined_admission_rescans_before_each_capacity_decision()
    {
        if (!OperatingSystem.IsWindows()) return;
        var ledger = new ResourceLedger(Path.Combine(root, "fresh-registry"), driver, 1);
        var first = ledger.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId); first.AdmitFrontdoor();
        var reads = 0; ledger.BeforeRecordsRead = () => reads++;
        var next = ledger.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId);
        Assert.Throws<InvalidOperationException>(() => next.AdmitFrontdoor([first.Identity.Executable]));
        Assert.Equal(1, reads);
    }

    [Fact]
    public void Malformed_history_is_not_skipped_and_failed_open_releases_its_os_slot()
    {
        if (!OperatingSystem.IsWindows()) return;
        execution.Owner("rockwell-mcp-server").BeginLaunch("Preserved claim", "sdk-worker");
        var invalid = Path.Combine(metadata.DirectoryPath, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(invalid, "{");
        var policy = new McpFrontdoorPolicy(MaximumMetadataClients: 1, MaximumStartingClients: 1);
        Assert.ThrowsAny<JsonException>(() => McpFrontdoorSession.Open(root, Path.Combine(root, "registry"), policy, default, metadata));
        File.Delete(invalid); // Only this fixture's malformed file.
        using var session = McpFrontdoorSession.Open(root, Path.Combine(root, "registry"), policy, default, metadata);
        Assert.Equal("active", session.Record.State);
    }

    [Fact]
    public void Saturated_execution_claims_remain_unchanged_when_metadata_admits_a_self_observation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var owner = execution.Owner("rockwell-mcp-server");
        for (var n = 0; n < 16; n++) owner.BeginLaunch("Uncertain native fixture", "sdk-worker");
        var files = Directory.GetFiles(execution.DirectoryPath, "*.json").ToDictionary(p => p, File.ReadAllBytes);
        var frontdoor = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId);
        var record = frontdoor.AdmitFrontdoor();
        Assert.Equal(frontdoor.Identity, record.Owner); Assert.Null(record.Process); Assert.Equal("attached", record.Ownership); Assert.Equal("report", record.CleanupMode);
        Assert.Equal(16, execution.Records().Count(r => r.AppId == owner.AppId && !r.Terminal));
        Assert.All(files, p => Assert.Equal(p.Value, File.ReadAllBytes(p.Key)));
        Assert.Throws<InvalidOperationException>(() => owner.BeginLaunch("Still full", "sdk-worker"));
    }
    [Fact]
    public void Self_observation_is_atomic_and_detaches_only_after_its_exact_reviewed_revision()
    {
        if (!OperatingSystem.IsWindows()) return;
        var owner = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId); var record = owner.AdmitFrontdoor();
        Assert.Throws<InvalidOperationException>(() => owner.CloseFrontdoor(record.ResourceId, new('0', 64)));
        var closed = owner.CloseFrontdoor(record.ResourceId, record.Revision);
        Assert.Equal("released", closed.State); Assert.Null(closed.Receipt);
        Assert.Equal(record.Revision, closed.Reconciliation!.PreviousRevision); Assert.Equal(owner.Identity, closed.Reconciliation.Actor);
        Assert.Contains("No process termination", closed.Reconciliation.Evidence);
    }
    [Fact]
    public void Wrong_application_or_current_generation_cannot_admit_or_detach_frontdoor_state()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<InvalidOperationException>(() => execution.Owner("rockwell-mcp-server").AdmitFrontdoor());
        var owner = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId); var record = owner.AdmitFrontdoor();
        driver.Current = Identity(900);
        Assert.Throws<InvalidOperationException>(() => owner.CloseFrontdoor(record.ResourceId, record.Revision));
        Assert.Equal(record.Revision, metadata.Get(record.ResourceId).Revision);
    }
    [Theory]
    [InlineData("absent")] [InlineData("different")]
    public void Departed_metadata_generation_reconciles_without_any_process_action(string state)
    {
        if (!OperatingSystem.IsWindows()) return;
        var record = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId).AdmitFrontdoor();
        driver.Current = Identity(200, "new.exe"); driver.States[record.Owner] = state;
        var actor = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId);
        var result = actor.ReconcileExitedFrontdoor(record.ResourceId, record.Revision, record.Owner.Executable);
        Assert.Equal("released", result!.State); Assert.Equal(record.Owner, result.Owner);
        Assert.Equal(record.Revision, result.Reconciliation!.PreviousRevision); Assert.Equal(actor.Identity, result.Reconciliation.Actor);
    }
    [Theory]
    [InlineData("unknown")] [InlineData("alive")] [InlineData("barrier")] [InlineData("receipt")]
    [InlineData("native")] [InlineData("private")] [InlineData("conflict")] [InlineData("image")] [InlineData("revision")]
    public void Unknown_native_changed_or_conflicting_observations_are_preserved(string condition)
    {
        if (!OperatingSystem.IsWindows()) return;
        var record = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId).AdmitFrontdoor();
        if (condition is "alive" or "unknown") driver.States[record.Owner] = condition;
        if (condition == "barrier") record = record with { RecoveryBarrier = "Retain native uncertainty" };
        if (condition == "receipt") record = record with { Receipt = new(new('1', 32), record.Revision, "uncertain", "Retain") };
        if (condition == "native") record = record with { Kind = "sdk-session" };
        if (condition == "private") record = record with { Ownership = "private", CleanupMode = "terminate" };
        var path = Path.Combine(metadata.DirectoryPath, record.ResourceId + ".json"); var bytes = ResourceJson.Write(record); File.WriteAllBytes(path, bytes);
        if (condition == "conflict")
        {
            var other = record with { ResourceId = Guid.NewGuid().ToString("N"), AppId = "other-app", Owner = Identity(800), Process = record.Owner };
            File.WriteAllBytes(Path.Combine(metadata.DirectoryPath, other.ResourceId + ".json"), ResourceJson.Write(other));
        }
        driver.Current = Identity(200, "new.exe"); var actor = metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId);
        Assert.Null(actor.ReconcileExitedFrontdoor(record.ResourceId, condition == "revision" ? new('0', 64) : record.Revision,
            condition == "image" ? driver.Current.Executable : record.Owner.Executable));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }
    [Fact]
    public void Metadata_budget_is_bounded_independently_of_execution_admission()
    {
        if (!OperatingSystem.IsWindows()) return;
        for (uint i = 0; i < 8; i++) { driver.Current = Identity(100+i); metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId).AdmitFrontdoor(); }
        driver.Current = Identity(900);
        Assert.Throws<InvalidOperationException>(() => metadata.Owner(ResourceLedger.ResourceOwner.FrontdoorAppId).AdmitFrontdoor());
        Assert.DoesNotContain(execution.Records(), r => r.AppId == "rockwell-mcp-server");
    }
    [Fact]
    public void Policy_reservation_and_available_commit_floor_refuse_before_work()
    {
        var policy = new McpFrontdoorPolicy(MaximumMetadataClients: 24, MaximumStartingClients: 4, MaximumTransportCommitReservationBytes: 7516192768);
        policy.CheckReservation(23, 16UL * 1024 * 1024 * 1024);
        Assert.Throws<OperationException>(() => policy.CheckReservation(24, ulong.MaxValue));
        Assert.Throws<OperationException>(() => policy.CheckReservation(4, 1024 * 1024 * 1024));
        Assert.Throws<ArgumentException>(() => (policy with { MaximumTransportCommitReservationBytes = 1024 }).Validate());
        Assert.Throws<ArgumentException>(() => (policy with { MaximumMetadataClients = 65 }).Validate());
    }
    [Fact]
    public void Fifth_metadata_lease_coexists_with_four_legacy_clients_without_evicting_them()
    {
        if (!OperatingSystem.IsWindows()) return;
        var legacy = Enumerable.Range(0, 4).Select(_ => McpClientLease.Acquire(root, new(4))).ToArray();
        try
        {
            using var fifth = McpClientLease.AcquireFrontdoor(root, new());
            Assert.Equal(4, fifth.Slot);
            Assert.Throws<InvalidOperationException>(() => McpClientLease.Acquire(root, new(4)));
            Assert.All(legacy, lease => Assert.InRange(lease.Slot, 0, 3));
        }
        finally { foreach (var lease in legacy) lease.Dispose(); }
    }
    [Fact]
    public void Policy_file_is_optional_for_legacy_and_duplicate_fields_are_refused()
    {
        Assert.Null(McpFrontdoorPolicy.Read(root));
        File.WriteAllText(Path.Combine(root, McpFrontdoorPolicy.FileName), JsonSerializer.Serialize(new McpFrontdoorPolicy(), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.NotNull(McpFrontdoorPolicy.Read(root));
        File.WriteAllText(Path.Combine(root, McpFrontdoorPolicy.FileName), "{\"schemaVersion\":1,\"maximumMetadataClients\":8,\"maximumMetadataClients\":16}");
        Assert.Throws<InvalidOperationException>(() => McpFrontdoorPolicy.Read(root));
    }
    [Theory]
    [InlineData(McpFrontdoorPolicy.EnsurePinnedOffline)]
    [InlineData(McpFrontdoorPolicy.ExistingGenuineOnly)]
    public void Service_scope_is_explicit_and_unknown_modes_cannot_fall_back_to_startup(string mode)
    {
        var policy = new McpFrontdoorPolicy(ServiceMode: mode);
        File.WriteAllText(Path.Combine(root, McpFrontdoorPolicy.FileName), JsonSerializer.Serialize(policy, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(mode, McpFrontdoorPolicy.Read(root)!.ServiceMode);
        Assert.Throws<ArgumentException>(() => (policy with { ServiceMode = "unknown" }).Validate());
        Assert.Throws<ArgumentException>(() => (policy with { ServiceMode = null! }).Validate());
    }
    [Fact]
    public void Forwarding_slots_are_temporary_and_do_not_consume_metadata_or_execution_claims()
    {
        if (!OperatingSystem.IsWindows()) return;
        var policy = new McpFrontdoorPolicy(MaximumForwardedCalls: 2);
        using var first = McpClientLease.AcquireForwardedCall(root, policy);
        using var second = McpClientLease.AcquireForwardedCall(root, policy);
        Assert.Throws<InvalidOperationException>(() => McpClientLease.AcquireForwardedCall(root, policy));
        using var metadataLease = McpClientLease.AcquireFrontdoor(root, policy);
        Assert.Empty(metadata.Records());
        first.Dispose();
        using var later = McpClientLease.AcquireForwardedCall(root, policy);
        Assert.Equal(first.Slot, later.Slot);
    }
    [Fact]
    public async Task Waiting_startup_ramp_observes_parent_cancellation_without_evicting_the_holder()
    {
        if (!OperatingSystem.IsWindows()) return;
        var policy = new McpFrontdoorPolicy(MaximumStartingClients: 1);
        using var first = McpClientLease.AcquireFrontdoorStart(root, policy);
        using var cancel = new CancellationTokenSource(100);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Run(() => McpClientLease.AcquireFrontdoorStart(root, policy, cancel.Token)));
        using var alreadyCancelled = new CancellationTokenSource(); alreadyCancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => McpClientLease.AcquireFrontdoor(root, policy, alreadyCancelled.Token));
    }
}
