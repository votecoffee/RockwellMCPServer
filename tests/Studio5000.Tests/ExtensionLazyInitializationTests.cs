using System.Text.Json;
using Studio5000.Core;
using Studio5000.Extensibility;
using Xunit;

namespace Studio5000.Tests;

public sealed class ExtensionLazyInitializationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rockwell-lazy-extensions-" + Guid.NewGuid().ToString("N"));
    private readonly ServiceOptions options;
    private readonly ExtensionHost host;
    private readonly List<(string Id, Worker Worker)> workers = [];
    private Func<string, Worker> factory = _ => new();
    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value, ExtensionProtocol.Json);
    private static ExtensionOperation Operation() => new("echo", "Generated fixture", "read", J(new { type = "object", additionalProperties = false }));
    public ExtensionLazyInitializationTests()
    {
        options = new() { StateDirectory = Path.Combine(root, "state"), ExtensionsDirectory = Path.Combine(root, "packages") };
        host = new(options, root, (_, id) => { var worker = factory(id); workers.Add((id, worker)); return worker; });
    }
    private void Package(string id, string version = "1", bool selected = true, bool badHash = false)
    {
        var folder = Path.Combine(options.ExtensionsDirectory, id, "versions", version); Directory.CreateDirectory(folder);
        var bytes = System.Text.Encoding.UTF8.GetBytes(id + " " + version); File.WriteAllBytes(Path.Combine(folder, "fixture.dll"), bytes);
        File.WriteAllText(Path.Combine(folder, "manifest.json"), JsonSerializer.Serialize(new ExtensionPackage(id, version, 1,
            "fixture.dll", id, new() { ["fixture.dll"] = badHash ? new('0', 64) : ProjectService.Hash(bytes) }), ExtensionProtocol.Json));
        if (selected) File.WriteAllText(Path.Combine(options.ExtensionsDirectory, id, "active.json"), JsonSerializer.Serialize(new ExtensionPointer(version), ExtensionProtocol.Json));
    }
    private Task<ExtensionCatalog> Catalog(string id, CancellationToken ct = default) => host.CatalogAsync(id, ct);
    [Fact]
    public async Task FilteredDescribeAndInvokeStayLazyAndFullCatalogKeepsTheirGeneration()
    {
        Package("logix"); Package("viewdesigner");
        var first = Assert.Single((await Catalog("logix")).Extensions);
        Assert.Equal("logix", Assert.Single(workers).Id);
        Assert.Equal("echo", (await host.DescribeAsync("logix", "echo", first.Generation)).Name);
        await host.InvokeAsync("read", new("logix", "echo", first.Generation, J(new { })));
        Assert.Single(workers);
        var all = await host.CatalogAsync(); Assert.Equal(2, all.Extensions.Length);
        Assert.Equal(first.Generation, all.Extensions.Single(x => x.Id == "logix").Generation);
        Assert.Equal(2, workers.Count); await host.CatalogAsync(); Assert.Equal(2, workers.Count);
    }
    [Fact]
    public async Task BrokenUnrequestedPackageWaitsUntilRequestedAndItsErrorNeedsExplicitReload()
    {
        Package("logix"); Package("viewdesigner", badHash: true);
        Assert.Empty((await Catalog("logix")).LoadErrors);
        var failed = await Catalog("viewdesigner"); Assert.Empty(failed.Extensions);
        Assert.StartsWith("viewdesigner:", Assert.Single(failed.LoadErrors)); Assert.Single(workers);
        Package("viewdesigner"); // Repair alone is not an automatic validation retry.
        Assert.Single((await Catalog("viewdesigner")).LoadErrors); Assert.Single(workers);
        Assert.Single((await Catalog("logix")).LoadErrors); // Known failures remain visible.
        var restored = await host.ReloadAsync(new("viewdesigner", "1"));
        Assert.Empty((await Catalog("viewdesigner")).LoadErrors);
        Assert.Equal(restored.Generation, Assert.Single((await Catalog("viewdesigner")).Extensions).Generation);
        Assert.Equal(2, workers.Count);
    }
    [Fact]
    public async Task CanceledWaiterCannotCancelAnotherValidationOrReplaceItsGeneration()
    {
        Package("logix"); var worker = new Worker { Block = true }; factory = _ => worker;
        var first = Catalog("logix"); await worker.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            Assert.False(host.TryFenceShutdown());
            using var cancel = new CancellationTokenSource(); var waiting = Catalog("logix", cancel.Token); cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.False(first.IsCompleted); Assert.False(worker.Disposed); Assert.Single(workers);
        }
        finally { worker.Finish.TrySetResult(); }
        var generation = Assert.Single((await first).Extensions).Generation;
        Assert.Equal(generation, Assert.Single((await Catalog("logix")).Extensions).Generation); Assert.Single(workers);
    }
    [Fact]
    public async Task CancelingFullInitializationKeepsEarlierSelectionsAndDisposesOnlyTheCandidate()
    {
        Package("logix"); Package("viewdesigner");
        var first = Assert.Single((await Catalog("logix")).Extensions);
        var candidate = new Worker { Block = true }; factory = _ => candidate;
        using var cancel = new CancellationTokenSource(); var all = host.CatalogAsync(cancel.Token);
        await candidate.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => all);
        Assert.True(candidate.Disposed); Assert.False(workers[0].Worker.Disposed);
        factory = _ => new(); var completed = await host.CatalogAsync();
        Assert.Equal(first.Generation, completed.Extensions.Single(x => x.Id == "logix").Generation);
        Assert.Equal(2, completed.Extensions.Length); Assert.Equal(3, workers.Count);
    }
    [Fact]
    public async Task ExplicitReloadDoesNotFirstLoadTheOldSelectionOrOtherPackages()
    {
        Package("logix"); Package("logix", "2", selected: false); Package("viewdesigner");
        var loaded = await host.ReloadAsync(new("logix", "2")); Assert.Single(workers);
        Assert.Equal("2", loaded.Version);
        var all = await host.CatalogAsync(); Assert.Equal(2, workers.Count);
        Assert.Equal(loaded.Generation, all.Extensions.Single(x => x.Id == "logix").Generation);
        Assert.Equal("stale_generation", (await Assert.ThrowsAsync<OperationException>(() => host.ReloadAsync(new("logix", "1", "old")))).Code);
        Assert.Equal(loaded.Generation, Assert.Single((await Catalog("logix")).Extensions).Generation);
    }
    [Fact]
    public async Task CandidateThatIgnoresCancellationCannotPublishAfterTheRequestIsCanceled()
    {
        Package("logix"); var candidate = new Worker { Block = true, IgnoreCancellation = true }; factory = _ => candidate;
        using var cancel = new CancellationTokenSource(); var pending = Catalog("logix", cancel.Token);
        await candidate.Started.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel(); candidate.Finish.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); Assert.True(candidate.Disposed);
        Assert.False(File.Exists(Path.Combine(options.StateDirectory, "extensions", "logix-1.sha256")));
        factory = _ => new(); Assert.Single((await Catalog("logix")).Extensions); Assert.Equal(2, workers.Count);
    }
    [Fact]
    public async Task DisposedHostCannotActivateACandidateWhoseValidationWasAlreadyRunning()
    {
        Package("logix"); var candidate = new Worker { Block = true }; factory = _ => candidate;
        var pending = Catalog("logix"); await candidate.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var disposal = Task.Run(host.Dispose);
        try { await disposal.WaitAsync(TimeSpan.FromSeconds(3)); }
        finally { candidate.Finish.TrySetResult(); }
        await Assert.ThrowsAsync<ObjectDisposedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(3))); Assert.True(candidate.Disposed);
        Assert.False(File.Exists(Path.Combine(options.StateDirectory, "extensions", "logix-1.sha256")));
    }
    [Fact]
    public async Task ShutdownFenceRefusesUnloadedSelectionsWithoutCachingALoadFailure()
    {
        Package("logix"); Assert.True(host.TryFenceShutdown());
        Assert.Equal("service_stopping", (await Assert.ThrowsAsync<OperationException>(() => Catalog("logix"))).Code);
        Assert.Empty(workers); host.ReleaseShutdownFence();
        Assert.Empty((await Catalog("logix")).LoadErrors); Assert.Single(workers);
    }
    [Fact]
    public async Task UnknownAndInvalidFiltersNeverStartOtherPackages()
    {
        Package("logix"); Assert.Empty((await Catalog("missing")).Extensions); Assert.Empty(workers);
        Assert.Equal("invalid_extension", (await Assert.ThrowsAsync<OperationException>(() => Catalog("../logix"))).Code);
        Assert.Empty(workers); Assert.Single((await host.CatalogAsync()).Extensions);
    }
    public void Dispose() { host.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    private sealed class Worker : IExtensionWorker
    {
        public bool Block, Disposed, IgnoreCancellation;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ExtensionOperation[]> DescribeAsync(CancellationToken ct)
        { Started.TrySetResult(); if (Block) await Finish.Task.WaitAsync(IgnoreCancellation ? CancellationToken.None : ct); return [Operation()]; }
        public Task<JsonElement> InvokeAsync(string operation, JsonElement arguments, CancellationToken ct) => Task.FromResult(J(new { ok = true }));
        public void Dispose() { Disposed = true; Finish.TrySetResult(); }
    }
}
