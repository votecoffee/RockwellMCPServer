using System.Text.Json;
using Studio5000.Core;
using Studio5000.Extensibility;
using Xunit;

namespace Studio5000.Tests;

public sealed class ExtensionWorkerPoolTests
{
    private sealed class Worker(Func<CancellationToken, Task<JsonElement>> call, string name = "echo") : IExtensionWorker
    {
        public bool Disposed;
        public int DisposalCount;
        public bool FailDisposal;
        public Task<ExtensionOperation[]> DescribeAsync(CancellationToken ct) => Task.FromResult(new[] { new ExtensionOperation(name, "fixture", "read", JsonSerializer.SerializeToElement(new { type = "object", properties = new { } })) });
        public Task<JsonElement> InvokeAsync(string operation, JsonElement arguments, CancellationToken ct) => call(ct);
        public void Dispose() { Disposed = true; Interlocked.Increment(ref DisposalCount); if (FailDisposal) throw new IOException("fixture close failed"); }
    }
    [Fact]
    public async Task OptedInGenerationRunsIndependentCallsInSeparateWorkersWithoutCrossingResponses()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var active = 0; var created = 0;
        using var pool = new ExtensionWorkerPool(2, () =>
        {
            var id = Interlocked.Increment(ref created); return new Worker(async ct => { if (Interlocked.Increment(ref active) == 2) started.TrySetResult(); await release.Task.WaitAsync(ct); return JsonSerializer.SerializeToElement(id); });
        });
        await pool.DescribeAsync(default); var first = pool.InvokeAsync("echo", default, default); var second = pool.InvokeAsync("echo", default, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3)); Assert.Equal(2, created); release.SetResult(); var replies = await Task.WhenAll(first, second); Assert.Equal([1, 2], replies.Select(r => r.GetInt32()).Order().ToArray());
    }
    [Fact]
    public async Task CancellingAQueuedCallDoesNotInterruptAnotherInvocation()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var owned = new List<Worker>();
        using var pool = new ExtensionWorkerPool(1, () => { var w = new Worker(async ct => { await release.Task.WaitAsync(ct); return JsonSerializer.SerializeToElement("done"); }); owned.Add(w); return w; });
        await pool.DescribeAsync(default); var first = pool.InvokeAsync("echo", default, default); using var cancel = new CancellationTokenSource(); var second = pool.InvokeAsync("echo", default, cancel.Token);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second); Assert.False(owned[0].Disposed); release.SetResult(); Assert.Equal("done", (await first).GetString());
    }
    [Fact]
    public async Task AWorkerWithADifferentCatalogCannotRunInsideTheSameGeneration()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var count = 0;
        using var pool = new ExtensionWorkerPool(2, () => new Worker(async ct => { await release.Task.WaitAsync(ct); return default; }, Interlocked.Increment(ref count) == 1 ? "echo" : "different"));
        await pool.DescribeAsync(default); var first = pool.InvokeAsync("echo", default, default);
        await Assert.ThrowsAsync<OperationException>(() => pool.InvokeAsync("echo", default, default)); release.SetResult(); await first;
    }
    [Fact]
    public void LegacyManifestsKeepSingleWorkerSemantics()
    {
        var manifest = JsonSerializer.Deserialize<ExtensionPackage>("{\"id\":\"legacy\",\"version\":\"1\",\"contractVersion\":1,\"entryAssembly\":\"a.dll\",\"entryType\":\"Entry\",\"files\":{}}", ExtensionProtocol.Json)!;
        Assert.Equal(1, manifest.ParallelWorkers);
        Assert.False(manifest.ReleaseIdleWorkers);
    }

    [Fact]
    public async Task IdleReleaseCachesCatalogButRetainsNoWorkerAfterDescribeOrInvocation()
    {
        var owned = new List<Worker>();
        using var pool = new ExtensionWorkerPool(1, () => { var w = new Worker(_ => Task.FromResult(JsonSerializer.SerializeToElement("done"))); owned.Add(w); return w; }, releaseIdleWorkers: true);
        Assert.Empty(owned);
        await pool.DescribeAsync(default); Assert.Single(owned); Assert.True(owned[0].Disposed);
        await pool.DescribeAsync(default); Assert.Single(owned);
        Assert.Equal("done", (await pool.InvokeAsync("echo", default, default)).GetString());
        Assert.Equal(2, owned.Count); Assert.All(owned, w => Assert.Equal(1, w.DisposalCount));
        Assert.Equal("done", (await pool.InvokeAsync("echo", default, default)).GetString());
        Assert.Equal(3, owned.Count); Assert.All(owned, w => Assert.Equal(1, w.DisposalCount));
    }

    [Fact]
    public async Task IdleReleasePreservesActiveWorkerAndDoesNotSpawnForCanceledQueuedCall()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var owned = new List<Worker>();
        using var pool = new ExtensionWorkerPool(1, () => { var w = new Worker(async ct => { await release.Task.WaitAsync(ct); return default; }); owned.Add(w); return w; }, true);
        await pool.DescribeAsync(default); var active = pool.InvokeAsync("echo", default, default);
        Assert.Equal(2, owned.Count); Assert.False(owned[1].Disposed);
        using var canceled = new CancellationTokenSource(); var queued = pool.InvokeAsync("echo", default, canceled.Token);
        canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(2, owned.Count); Assert.False(owned[1].Disposed);
        release.SetResult(); await active; Assert.All(owned, w => Assert.Equal(1, w.DisposalCount));
    }

    [Fact]
    public async Task IdleReleaseRejectsChangedCatalogBeforeInvokingFreshWorker()
    {
        var calls = 0; var owned = new List<Worker>();
        using var pool = new ExtensionWorkerPool(1, () => { var w = new Worker(_ => { calls++; return Task.FromResult(default(JsonElement)); }, owned.Count == 0 ? "echo" : "different"); owned.Add(w); return w; }, true);
        await pool.DescribeAsync(default);
        var error = await Assert.ThrowsAsync<OperationException>(() => pool.InvokeAsync("echo", default, default));
        Assert.Equal("invalid_extension", error.Code); Assert.Equal(0, calls);
        Assert.All(owned, w => Assert.Equal(1, w.DisposalCount));
    }

    [Fact]
    public async Task DisposalDuringActiveCallReleasesEachOwnedWorkerExactlyOnce()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var owned = new List<Worker>();
        using var pool = new ExtensionWorkerPool(1, () => { var w = new Worker(async ct => { await release.Task.WaitAsync(ct); return default; }); owned.Add(w); return w; }, true);
        await pool.DescribeAsync(default); var call = pool.InvokeAsync("echo", default, default);
        pool.Dispose(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.All(owned, w => Assert.Equal(1, w.DisposalCount));
    }

    [Fact]
    public async Task CleanupFailureCannotStrandTheOnlyInvocationSlot()
    {
        var owned = new List<Worker>();
        using var pool = new ExtensionWorkerPool(1, () => { var w = new Worker(_ => Task.FromResult(default(JsonElement))) { FailDisposal = owned.Count == 1 }; owned.Add(w); return w; }, true);
        await pool.DescribeAsync(default);
        await Assert.ThrowsAsync<IOException>(() => pool.InvokeAsync("echo", default, default));
        await pool.InvokeAsync("echo", default, default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(3, owned.Count); Assert.All(owned, w => Assert.Equal(1, w.DisposalCount));
    }

    [Fact]
    public async Task OneFailedCloseDoesNotSkipOtherOwnedWorkersDuringShutdown()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var owned = new List<Worker>();
        using var pool = new ExtensionWorkerPool(2, () => { var w = new Worker(async ct => { await release.Task.WaitAsync(ct); return default; }); owned.Add(w); return w; }, true);
        await pool.DescribeAsync(default);
        var first = pool.InvokeAsync("echo", default, default); var second = pool.InvokeAsync("echo", default, default);
        owned[1].FailDisposal = true;
        Assert.Single(Assert.Throws<AggregateException>(() => pool.Dispose()).InnerExceptions);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        Assert.Equal(3, owned.Count); Assert.All(owned, w => Assert.Equal(1, w.DisposalCount));
    }
}
