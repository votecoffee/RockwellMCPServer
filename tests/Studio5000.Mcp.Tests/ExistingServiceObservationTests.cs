using ModelContextProtocol.Protocol;
using Studio5000.Core;
using Studio5000.Core.Resources;
using Studio5000.Mcp;
using Xunit;

namespace Studio5000.Mcp.Tests;

public sealed class ExistingServiceObservationTests
{
    private static readonly ProcessIdentity Owner = new(new string('a', 64), 123, "100", @"C:\Fixture\Service.exe");
    private static readonly ProcessIdentity Replacement = Owner with { Pid = 456, Created = "200" };
    private static OfflineServiceReady Ready(ProcessIdentity? owner = null) => new(false, "fixture-version", owner ?? Owner);
    private static TaskCompletionSource<T> Completion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Driver : IResourceProcessDriver
    {
        public string StateValue = "alive";
        public Exception? QueryError;
        public int Queries;
        public ProcessIdentity CurrentIdentity() => throw new InvalidOperationException("No registration permitted");
        public ProcessIdentity Capture(uint pid) => throw new InvalidOperationException("No process capture permitted");
        public string State(ProcessIdentity identity)
        {
            Interlocked.Increment(ref Queries); Assert.Equal(Owner, identity);
            if (QueryError is not null) throw QueryError;
            return StateValue;
        }
        public IResourceProcessHandle Open(ProcessIdentity identity) => throw new InvalidOperationException("No process control permitted");
    }

    [Fact]
    public async Task Failed_discovery_leaves_body_undispatched_and_only_a_later_explicit_request_observes_again()
    {
        var calls = 0; var bodies = 0; var driver = new Driver(); var available = false;
        var gate = new ExistingServiceObservationGate(() =>
        {
            Interlocked.Increment(ref calls);
            return available ? Task.FromResult(Ready()) : Task.FromException<OfflineServiceReady>(new IOException("fixture missing"));
        }, driver);
        var wrapped = ServiceErrors.Wrap(async (_, ct) => { await gate.Ready(ct); Interlocked.Increment(ref bodies); return new CallToolResult(); });
        var failed = await wrapped(null!, default);
        Assert.True(failed.IsError); Assert.Equal("mcp_existing_service_unavailable", failed.StructuredContent!.Value.GetProperty("code").GetString());
        Assert.Equal("unbound-discovery", failed.StructuredContent.Value.GetProperty("phase").GetString());
        Assert.True(failed.StructuredContent.Value.GetProperty("laterExplicitRequestMayObserve").GetBoolean());
        Assert.False(failed.StructuredContent.Value.GetProperty("dispatched").GetBoolean());
        Assert.False(failed.StructuredContent.Value.GetProperty("automaticRetry").GetBoolean());
        Assert.Equal(1, calls); Assert.Equal(0, bodies);
        available = true;
        Assert.NotEqual(true, (await wrapped(null!, default)).IsError);
        Assert.Equal(2, calls); Assert.Equal(1, bodies);
        await gate.Ready(); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Each_failed_independent_request_has_one_observation_and_no_internal_loop()
    {
        var calls = 0;
        var gate = new ExistingServiceObservationGate(() => { calls++; return Task.FromException<OfflineServiceReady>(new IOException()); }, new Driver());
        for (var request = 1; request <= 3; request++)
        {
            await Assert.ThrowsAsync<ExistingServiceObservationException>(() => gate.Ready());
            Assert.Equal(request, calls);
        }
    }

    [Fact]
    public async Task Concurrent_callers_share_initial_and_later_inflight_observations()
    {
        var pending = Completion<OfflineServiceReady>(); var started = Completion<bool>(); var calls = 0;
        var gate = new ExistingServiceObservationGate(() => { calls++; started.TrySetResult(true); return pending.Task; }, new Driver());
        var first = Enumerable.Range(0, 4).Select(_ => gate.Ready()).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(1, calls);
        pending.SetException(new IOException());
        foreach (var task in first) await Assert.ThrowsAsync<ExistingServiceObservationException>(() => task);
        pending = Completion<OfflineServiceReady>(); started = Completion<bool>();
        var later = Enumerable.Range(0, 4).Select(_ => gate.Ready()).ToArray();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(2, calls);
        pending.SetResult(Ready()); await Task.WhenAll(later); Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Canceling_one_waiter_does_not_cancel_or_replace_shared_observation()
    {
        var pending = Completion<OfflineServiceReady>(); var started = Completion<bool>(); var calls = 0;
        var gate = new ExistingServiceObservationGate(() => { calls++; started.SetResult(true); return pending.Task; }, new Driver());
        using var canceled = new CancellationTokenSource();
        var first = gate.Ready(canceled.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        var later = gate.Ready(); Assert.Equal(1, calls);
        pending.SetResult(Ready()); Assert.Equal(Owner, (await later).Owner);
        await gate.Ready(); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Success_after_every_waiter_cancels_still_pins_owner_before_later_owner_loss()
    {
        var pending = Completion<OfflineServiceReady>(); var started = Completion<bool>(); var calls = 0; var driver = new Driver();
        var gate = new ExistingServiceObservationGate(() => { calls++; started.SetResult(true); return pending.Task; }, driver);
        using var canceled = new CancellationTokenSource(); var first = gate.Ready(canceled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5)); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        pending.SetResult(Ready()); driver.StateValue = "absent";
        var lost = await Assert.ThrowsAsync<ExistingServiceObservationException>(() => gate.Ready());
        Assert.Equal("bound-owner-unavailable", lost.Phase); Assert.False(lost.LaterExplicitRequestMayObserve); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Canceled_underlying_observation_is_not_a_faulted_task_and_is_not_replaced()
    {
        var calls = 0;
        var gate = new ExistingServiceObservationGate(() => { calls++; return Task.FromCanceled<OfflineServiceReady>(new(true)); }, new Driver());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.Ready());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.Ready()); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Already_canceled_request_never_schedules_a_discovery_or_replacement()
    {
        var calls = 0;
        var gate = new ExistingServiceObservationGate(() => { calls++; return Task.FromException<OfflineServiceReady>(new IOException()); }, new Driver());
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.Ready(canceled.Token)); Assert.Equal(0, calls);
        await Assert.ThrowsAsync<ExistingServiceObservationException>(() => gate.Ready()); Assert.Equal(1, calls);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.Ready(canceled.Token)); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("absent")][InlineData("different")][InlineData("unknown")]
    public async Task Bound_owner_loss_is_permanent_and_never_observes_or_adopts_replacement(string state)
    {
        var calls = 0; var driver = new Driver();
        var gate = new ExistingServiceObservationGate(() => Task.FromResult(++calls == 1 ? Ready() : Ready(Replacement)), driver);
        Assert.Equal(Owner, (await gate.Ready()).Owner); driver.StateValue = state;
        var lost = await Assert.ThrowsAsync<ExistingServiceObservationException>(() => gate.Ready());
        Assert.Equal("bound-owner-unavailable", lost.Phase); Assert.False(lost.LaterExplicitRequestMayObserve);
        driver.StateValue = "alive";
        await Assert.ThrowsAsync<ExistingServiceObservationException>(() => gate.Ready()); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Successful_observation_already_pins_owner_even_if_first_liveness_check_fails()
    {
        var calls = 0; var driver = new Driver { StateValue = "absent" };
        var gate = new ExistingServiceObservationGate(() => { calls++; return Task.FromResult(Ready()); }, driver);
        await Assert.ThrowsAsync<ExistingServiceObservationException>(() => gate.Ready());
        await Assert.ThrowsAsync<ExistingServiceObservationException>(() => gate.Ready()); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Unverifiable_bound_owner_is_latched_and_error_wrapper_never_dispatches_body_again()
    {
        var calls = 0; var bodies = 0; var driver = new Driver();
        var gate = new ExistingServiceObservationGate(() => { calls++; return Task.FromResult(Ready()); }, driver);
        var wrapped = ServiceErrors.Wrap(async (_, ct) => { await gate.Ready(ct); bodies++; return new CallToolResult(); });
        await wrapped(null!, default); Assert.Equal(1, bodies);
        driver.QueryError = new IOException("fixture identity query unavailable");
        var failed = await wrapped(null!, default);
        Assert.Equal("bound-owner-unavailable", failed.StructuredContent!.Value.GetProperty("phase").GetString());
        Assert.False(failed.StructuredContent.Value.GetProperty("laterExplicitRequestMayObserve").GetBoolean());
        Assert.False(failed.StructuredContent.Value.GetProperty("dispatched").GetBoolean());
        Assert.False(failed.StructuredContent.Value.GetProperty("automaticRetry").GetBoolean());
        driver.QueryError = null;
        Assert.True((await wrapped(null!, default)).IsError);
        Assert.Equal(1, calls); Assert.Equal(1, bodies); Assert.Equal(2, driver.Queries);
    }

    [Fact]
    public async Task Engineering_failure_is_never_replayed_or_treated_as_failed_discovery()
    {
        var calls = 0; var bodies = 0;
        var gate = new ExistingServiceObservationGate(() => { calls++; return Task.FromResult(Ready()); }, new Driver());
        var wrapped = ServiceErrors.Wrap(async (_, ct) =>
        {
            await gate.Ready(ct); bodies++;
            throw new IOException("fixture indeterminate engineering result");
        });
        await Assert.ThrowsAsync<IOException>(async () => await wrapped(null!, default));
        Assert.Equal(1, calls); Assert.Equal(1, bodies);
        await gate.Ready(); Assert.Equal(1, calls); Assert.Equal(1, bodies);
    }
}
