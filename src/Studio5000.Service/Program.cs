using System.Net;
using System.Security.Cryptography;
using System.Text;
using Studio5000.Core;
using Studio5000.Extensibility;
using Studio5000.Core.Resources;

if (args is ["--version"]) { Console.WriteLine(WorkspaceIdentity.Version); return; }
if (args is ["--native-local-settings", var candidateRoot])
{
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(NativeExecutionPolicy.CreateLocalOptions(candidateRoot), ServiceSettingsStore.Json));
    return; // Pure configuration generation: no service/native/resource initialization.
}
var root = Path.GetFullPath(Environment.GetEnvironmentVariable("STUDIO5000_PLUGIN_ROOT") ?? Directory.GetCurrentDirectory());
if (File.Exists(Path.Combine(root, ".local", "maintenance-hold.json"))) throw new InvalidOperationException("RockwellMCPServer is on operator maintenance hold; explicit human release is required.");
using var supervisorOwner = Environment.GetEnvironmentVariable("ROCKWELL_MANAGED_SERVICE") == "1" ? ParentProcessOwners.Capture() : null;
if (args.Length == 1 && args[0] == "--stage-broad-local-settings")
{
    var settings = ServiceSettingsStore.OpenOffline(root); var current = settings.Read();
    var result = settings.Save(new(current.Revision, settings.BroadLocalPreview(current.Saved)));
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { saved = true, restartRequired = true,
        configurationPath = result.Settings.ConfigurationPath, backupPath = result.BackupPath,
        nativeConnectionAttempted = false, runningServiceChanged = false }));
    return;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = root });
if (supervisorOwner is not null)
    builder.Services.AddHostedService(provider => new SupervisorExitService(supervisorOwner, provider.GetRequiredService<IHostApplicationLifetime>()));
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.MaxDepth = 128);
builder.Configuration.AddJsonFile(Path.Combine(root, "service.local.json"), optional: true, reloadOnChange: false);
var url = builder.Configuration["ServiceUrl"] ?? "http://127.0.0.1:5487";
if (!Uri.TryCreate(url, UriKind.Absolute, out var address) || address.Scheme != "http" ||
    !IPAddress.TryParse(address.Host, out var ip) || !IPAddress.IsLoopback(ip))
    throw new InvalidOperationException("ServiceUrl must be an HTTP loopback IP address.");
builder.WebHost.UseUrls(url);
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Configuration["AllowedHosts"] = "127.0.0.1;localhost;[::1]";
var settingsSection = builder.Configuration.GetSection("Studio5000");
var options = new ServiceOptions();
// Bind configured arrays onto empty arrays rather than appending their defaults.
var configuredKeys = settingsSection.GetChildren().Select(s => s.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
if (configuredKeys.Contains(nameof(ServiceOptions.AllowedRoots))) options.AllowedRoots = [];
if (configuredKeys.Contains(nameof(ServiceOptions.OutputRoots))) options.OutputRoots = [];
settingsSection.Bind(options);
ServiceSettingsStore.ResolveAndValidate(options, root);
if (supervisorOwner is not null) ManagedResourcePolicy.Validate(Environment.GetEnvironmentVariable("ROCKWELL_RESOURCE_PROFILE"), options);
// Self-membership precedes all business service/worker initialization. Never
// assign an existing user-owned engineering application to this private job.
OwnedProcessContainment.EnterCurrentProcess(options.MaximumOwnedProcesses);
using var resourceSupervisor = new ResourceSupervisor(root,
    Environment.GetEnvironmentVariable("ROCKWELL_RESOURCE_REGISTRY") ?? WindowsResourceProcessDriver.DefaultRegistry(), options.MaximumOwnedProcesses, options.MaximumOwnedResources);
if (supervisorOwner is not null) ManagedServiceAdmission.WaitForOwner(root, resourceSupervisor.Ledger);
var local = Path.Combine(root, ".local");
Directory.CreateDirectory(local);
ProjectService.RejectLinks(local);
var tokenPath = Path.Combine(local, "service.key");
if (!File.Exists(tokenPath)) File.WriteAllText(tokenPath, Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
var token = File.ReadAllText(tokenPath).Trim();
if (token.Length < 32) throw new InvalidOperationException("service.key is too short.");
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<IHostLifetime>(new OwnerConsoleLifetime(new OwnerShutdownMailbox(root, resourceSupervisor.Owner.OwnerId, token)));
builder.Services.AddSingleton(new ServiceSettingsStore(root, url, options));
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton(_ => new ExtensionHost(options, root));
builder.Services.AddHostedService(provider => new OwnerStopService(root, resourceSupervisor, token,
    provider.GetRequiredService<ProjectService>(), provider.GetRequiredService<ExtensionHost>(), provider.GetRequiredService<IHostApplicationLifetime>()));
builder.Services.AddHostedService(_ => new ResourceRefreshService(resourceSupervisor));
var app = builder.Build();
_ = app.Services.GetRequiredService<ProjectService>(); // hold one exclusive process lock before publishing connection info
resourceSupervisor.WriteOwnerReceipt();
File.WriteAllText(Path.Combine(local, "connection.json"), System.Text.Json.JsonSerializer.Serialize(new { url, tokenFile = tokenPath }));
app.Use(async (context, next) =>
{
    try
    {
        if (context.Request.Path != "/health")
        {
            var supplied = context.Request.Headers.Authorization.ToString();
            var expected = "Bearer " + token;
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected)))
            { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { code = "unauthorized", message = "A local service bearer token is required." }); return; }
        }
        if (context.Request.Headers.ContainsKey("Origin"))
        { context.Response.StatusCode = 403; await context.Response.WriteAsJsonAsync(new { code = "origin_denied", message = "Browser-origin requests are not enabled." }); return; }
        await next(context);
    }
    catch (OperationException ex)
    {
        context.Response.StatusCode = ex.Code == "not_found" ? 404 : ex.Code == "request_queue_full" ? 429 : ex.Code is "settings_conflict" or "stale_revision" or "state_tampered" or "draft_tampered" or "transaction_conflict" or "transaction_closed" or "transaction_recovery_required" ? 409 : 400;
        await context.Response.WriteAsJsonAsync(new { code = ex.Code, message = ex.Message });
    }
    catch (Exception ex) when (ex is System.Xml.XmlException or System.Xml.Schema.XmlSchemaException or System.Xml.XPath.XPathException or ArgumentException or System.Text.Json.JsonException)
    { context.Response.StatusCode = 400; await context.Response.WriteAsJsonAsync(new { code = "invalid_request", message = ex.Message }); }
});
app.MapGet("/health", () => new { status = "ok", name = WorkspaceIdentity.Name, version = WorkspaceIdentity.Version,
    packageVersion = WorkspaceIdentity.PackageVersion, assemblyFileVersion = WorkspaceIdentity.AssemblyFileVersion });
app.MapGet("/api/management/activity", (ProjectService service, ExtensionHost extensions) =>
{
    var activity = service.ManagementActivity();
    return new { schema_version = 1, name = WorkspaceIdentity.Name, version = activity.Version,
        active_operations = Math.Clamp(activity.ActiveWorkspaces + activity.QueuedWorkspaces + activity.NativeJobs + activity.PollingWatches + extensions.ManagementActivity(), 0, 1000000),
        process_containment = OwnedProcessContainment.Snapshot(),
        restart_allowed = false, restart_reason = "No atomic restart fence for all chats, unsaved GUI documents, prepared reviews and unresolved native effects. Use the operator-managed update workflow." };
});
EngineeringOperations.Register((method, path, handler) => app.MapMethods(path, [method], handler));
app.MapGet("/api/settings", (ServiceSettingsStore settings) => settings.Read());
app.MapPost("/api/settings/broad-local-preview", (ServiceSettingsStore settings, System.Text.Json.JsonElement configuration) => settings.BroadLocalPreview(configuration));
app.MapPost("/api/settings", (ServiceSettingsStore settings, ServiceSettingsSaveRequest request) => settings.Save(request));
app.MapGet("/api/artifacts/{id}", (ProjectService service, string id) =>
{
    var (artifact, path) = service.Download(id);
    return Results.File(path, "application/octet-stream", $"{artifact.ProjectId}.{artifact.Format}");
});
app.MapPost("/api/sdk/service/ensure-running", (ProjectService service, SdkServiceEnsureRequest request, CancellationToken ct) => service.EnsureSdkServiceRunningAsync(request, ct));
app.MapPost("/api/factorytalk/launch", (ProjectService service) => service.LaunchFactoryTalkStudio());
app.MapGet("/api/broker/capabilities", async (ExtensionHost host, CancellationToken ct) => AppPublication.Catalog(await host.CatalogAsync(ct)));
app.MapGet("/api/broker/workflow", (string workflowId, string expectedRevision) => AppPublication.Workflow(workflowId, expectedRevision));
app.MapGet("/api/broker/reference", (string workflowId, string expectedRevision, string referenceId, string expectedSha256) => AppPublication.Reference(workflowId, expectedRevision, referenceId, expectedSha256));
app.MapGet("/api/extensions", async (ExtensionHost host, bool? includeSchemas, string? extensionId, CancellationToken ct) =>
{
    var catalog = await host.CatalogAsync(extensionId, ct);
    if (includeSchemas == false) return Results.Json(new
    {
        catalog.ProtocolVersion, catalog.LoadErrors,
        extensions = catalog.Extensions.Select(x => new { x.Id, x.Version, x.Generation, x.PackageSha256, x.ActiveCalls, x.State,
            operations = x.Operations.Select(o => new { o.Name, o.Description, o.Effect }) })
    });
    return Results.Json(catalog);
});
app.MapGet("/api/extensions/operation", (ExtensionHost host, string extensionId, string operation, string generation, CancellationToken ct) => host.DescribeAsync(extensionId, operation, generation, ct));
app.MapPost("/api/extensions/reload", (ExtensionHost host, ExtensionReloadRequest request, CancellationToken ct) => host.ReloadAsync(request, ct));
app.MapPost("/api/extensions/invoke/{effect}", (ExtensionHost host, string effect, ExtensionCall request, CancellationToken ct) => host.InvokeAsync(effect, request, ct));
app.MapGet("/api/extensions/jobs", (ExtensionHost host) => host.Jobs());
app.MapGet("/api/extensions/jobs/{id}", (ExtensionHost host, string id) => host.Job(id));
app.MapPost("/api/extensions/jobs", (ExtensionHost host, ExtensionJobRequest request, CancellationToken ct) => host.StartJobAsync(request, ct));
app.MapPost("/api/extensions/jobs/{id}/cancel", (ExtensionHost host, string id) => host.CancelJob(id));
await app.RunAsync();

sealed class SupervisorExitService(ParentProcessOwners owner, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => owner.RequestShutdownOnExitAsync(lifetime.StopApplication, stoppingToken);
}

sealed class OwnerStopService(string root, ResourceSupervisor resources, string key, ProjectService projects,
    ExtensionHost extensions, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mailbox = new OwnerShutdownMailbox(root, resources.Owner.OwnerId, key);
        var reportingFailure = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
            if (await mailbox.PollAsync(() =>
            {
                if (!projects.TryFenceShutdown()) return Task.FromResult(new OwnerShutdownDecision(false, "Finish active mutations, native jobs and polling before shutdown."));
                if (!extensions.TryFenceShutdown()) { projects.ReleaseShutdownFence(); return Task.FromResult(new OwnerShutdownDecision(false, "Extension calls/reloads/jobs are still active.")); }
                // Teardown exceptions propagate to the mailbox and preserve the admission fences.
                if (!resources.TryCloseOwnedResources())
                {
                    extensions.ReleaseShutdownFence(); projects.ReleaseShutdownFence();
                    return Task.FromResult(new OwnerShutdownDecision(false, "Native connections, dirty/unknown documents or shared leases require app-owned close/recovery before shutdown."));
                }
                return Task.FromResult(new OwnerShutdownDecision(true, "Owned resource cleanup verified; service shutdown requested."));
            }, stoppingToken)) { lifetime.StopApplication(); return; }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception)
            {
                // An unavailable mailbox/journal must not trigger the host's
                // default background-failure shutdown of native resources.
                if (!reportingFailure) { Console.Error.WriteLine("Owner shutdown reporting is unavailable; preserve the same intent and native state for inspection."); reportingFailure = true; }
            }
            await Task.Delay(100, stoppingToken);
        }
    }
}

sealed class ResourceRefreshService(ResourceSupervisor resources) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var reported = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            try { resources.Refresh(); }
            catch (Exception)
            {
                // Reporting/identity uncertainty cannot unwind a native owner.
                if (!reported) { Console.Error.WriteLine("Resource membership reporting is unavailable; retain the ledger and inspect before cleanup."); reported = true; }
            }
            await Task.Delay(2000, stoppingToken);
        }
    }
}

public partial class Program { }
