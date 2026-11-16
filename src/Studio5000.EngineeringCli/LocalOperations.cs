using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Studio5000.Client;
using Studio5000.Core;
using Studio5000.Extensibility;
using Studio5000.Extensions.Rockwell;

namespace Rockwell.Engineering;

internal static class LocalOperations
{
    private static readonly Dictionary<string, string> Manual = new(StringComparer.Ordinal)
    {
        ["ftview_launch_studio"] = "Open FactoryTalk View Studio in the operator's desktop session."
    };
    private static readonly Dictionary<string, (ProxyExtension Owner, ExtensionOperation Help)> Aliases =
        new ProxyExtension[] { new LogixExtension(), new FactoryTalkExtension(), new ViewDesignerExtension(), new WatchesExtension() }
            .SelectMany(owner => owner.Describe().Select(help => (Owner: owner, Help: help)))
            .ToDictionary(entry => entry.Help.Name, StringComparer.Ordinal);
    private static readonly Dictionary<string, Route> Routes = CreateRoutes();
    private static readonly Route[] MatchOrder = Routes.Values.OrderByDescending(route => route.LiteralSegments).ToArray();

    public static object Catalog() => new
    {
        historicalNamedCount = Aliases.Count,
        supportedNamedCount = Aliases.Count - Manual.Count,
        directCount = Routes.Count,
        named = Aliases.Values.Where(x => !Manual.ContainsKey(x.Help.Name))
            .Select(x => new { x.Help.Name, x.Help.Effect, x.Help.Description }).OrderBy(x => x.Name).ToArray(),
        direct = Routes.Keys.Order().ToArray(),
        operatorActions = Manual,
        installedBridges = WorkspaceRun.InstalledBridges()
    };

    public static object Help(string name)
    {
        if (Manual.TryGetValue(name, out var action)) return new { name, operatorAction = action };
        return FindSchema(name);
    }

    private static ExtensionOperation FindSchema(string name) => Routes.TryGetValue(name, out var route) ? route.Help
        : Aliases.TryGetValue(name, out var alias) ? alias.Help
        : throw new OperationException("unknown_workspace_operation", $"Unknown workspace operation '{name}'. Use operations to list available commands.");

    public static void Validate(string name, JsonElement arguments)
    {
        if (Manual.TryGetValue(name, out var action)) throw new OperationException("operator_action_required", action);
        try { ExtensionSchema.Validate(FindSchema(name).InputSchema, arguments); }
        catch (ArgumentException ex) { throw new OperationException("invalid_operation_arguments", ex.Message); }
    }

    public static async Task<JsonElement> InvokeAsync(string name, JsonElement arguments, Transport transport, CancellationToken ct)
    {
        if (Routes.TryGetValue(name, out var route))
            return JsonSerializer.SerializeToElement(await transport.InvokeAsync(route, arguments, ct), ProjectService.Json);
        using var client = ServiceClient.InProcess(transport);
        return await Aliases[name].Owner.InvokeLocalAsync(name, arguments, client, ct);
    }

    private static Dictionary<string, Route> CreateRoutes()
    {
        var routes = new Dictionary<string, Route>(StringComparer.Ordinal);
        void Add(string method, string path, Delegate handler) => routes.Add(method + " " + path, new Route(method, path, handler));
        EngineeringOperations.Register(Add);
        Add("POST", "/api/sdk/service/ensure-running", (ProjectService service, SdkServiceEnsureRequest request, CancellationToken ct) =>
            service.EnsureSdkServiceRunningAsync(request, ct));
        Add("GET", "/api/artifacts/{id}", (ProjectService service, string id) =>
        {
            var (artifact, path) = service.Download(id);
            return new { artifact, path };
        });
        return routes;
    }

    internal sealed class Transport(ProjectService service, string job, bool native, bool batch, string stateDirectory) : HttpMessageHandler
    {
        private readonly HashSet<string> watches = new(StringComparer.Ordinal);
        public bool NativeOutcomeUncertain { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            if (uri.Host != "rockwell-local.invalid")
                throw new OperationException("invalid_local_route", "The workspace dispatch accepts only local engineering routes.");
            var segments = uri.AbsolutePath.Split('/').Select(Uri.UnescapeDataString).ToArray();
            foreach (var route in MatchOrder)
            {
                if (route.Method != request.Method.Method || !route.Match(segments, out var values)) continue;
                foreach (var field in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
                {
                    var parts = field.Split('=', 2);
                    var name = Uri.UnescapeDataString(parts[0]);
                    values.Add(name, route.ParseQuery(name, Uri.UnescapeDataString(parts.Length == 2 ? parts[1] : "")));
                }
                if (route.Body is { } body && request.Content is { } content)
                    values.Add(body, await content.ReadFromJsonAsync<JsonElement>(ProjectService.Json, ct));
                var arguments = JsonSerializer.SerializeToElement(values, ProjectService.Json);
                var result = await InvokeAsync(route, arguments, ct);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(result, options: ProjectService.Json) };
            }
            throw new OperationException("unknown_local_route", $"No local engineering handler matches {request.Method} {uri.AbsolutePath}.");
        }

        internal async Task<object?> InvokeAsync(Route route, JsonElement arguments, CancellationToken ct)
        {
            var startWatch = route.Name == "POST /api/watches/{id}/start";
            if (startWatch && (!batch || !native))
                throw new OperationException("watch_batch_required", "Start polling inside an explicitly native-enabled bounded steps batch.");
            using var sdkServiceLease = route.Name == "POST /api/sdk/service/ensure-running" ? WorkspaceRun.AcquireSdkServiceLease() : null;
            var result = await route.CallAsync(service, arguments, ct);
            if (startWatch && result is WatchStatus watch) watches.Add(watch.Definition.Id);
            if (result is SdkJob sdk && route.Method == "POST" && route.Name != "POST /api/sdk/jobs/{id}/cancel")
                return await FinishSdkAsync(sdk, ct);
            if (result is VersionCompatibilityRun run)
                await FinishSdkAsync(service.SdkJob(run.ReadbackJobId ?? run.ConversionJobId), ct);
            return result;
        }

        private async Task<SdkJob> FinishSdkAsync(SdkJob sdk, CancellationToken ct)
        {
            sdk = await SdkJobWait.FinishAsync(sdk, job, stateDirectory, service.SdkJob, ct);
            if (sdk.Status == "succeeded") return sdk;
            NativeOutcomeUncertain = true;
            throw new OperationException("native_job_review_required", $"SDK job {sdk.Id} reported {sdk.Status}. Inspect its durable workspace receipt before another native dispatch.");
        }

        public async Task StopOwnedWatchesAsync()
        {
            foreach (var id in watches)
            {
                try
                {
                    var stopped = await service.StopWatchAsync(id);
                    EngineeringCli.WriteNew(Path.Combine(job, "watch-" + id + "-stop.json"),
                        new { id, stopped.State, stopped.Error, samples = stopped.Latest?.Sequence });
                }
                catch (OperationException ex) when (ex.Code == "not_found") { }
            }
        }
    }

    internal sealed class Route
    {
        private readonly string[] segments;
        private readonly ParameterInfo[] parameters;
        private readonly ParameterInfo[] supplied;
        private readonly HashSet<string> required;
        private readonly Delegate handler;
        public string Name => Help.Name;
        public string Method { get; }
        public string? Body { get; }
        public int LiteralSegments { get; }
        public ExtensionOperation Help { get; }

        public Route(string method, string path, Delegate handler)
        {
            Method = method;
            this.handler = handler;
            segments = path.Split('/');
            LiteralSegments = segments.Count(segment => !segment.StartsWith('{'));
            parameters = handler.Method.GetParameters();
            supplied = parameters.Where(p => p.ParameterType != typeof(ProjectService) && p.ParameterType != typeof(CancellationToken)).ToArray();
            Body = method == "POST" ? supplied.SingleOrDefault(p => !IsScalar(p.ParameterType))?.Name : null;
            var nullability = new NullabilityInfoContext();
            required = supplied.Where(p => segments.Contains("{" + p.Name + "}") ||
                (Nullable.GetUnderlyingType(p.ParameterType) is null && nullability.Create(p).ReadState != NullabilityState.Nullable))
                .Select(p => p.Name!).ToHashSet(StringComparer.Ordinal);
            var properties = new JsonObject();
            foreach (var parameter in supplied) properties[parameter.Name!] = ProxyExtension.DescribeType(parameter.ParameterType);
            var schema = new JsonObject { ["type"] = "object", ["properties"] = properties,
                ["required"] = JsonSerializer.SerializeToNode(required), ["additionalProperties"] = false };
            Help = new(method + " " + path, "Shared typed engineering handler.", method == "GET" ? "read" : "operation",
                JsonSerializer.SerializeToElement(schema));
        }

        public bool Match(string[] actual, out Dictionary<string, JsonElement> values)
        {
            values = new(StringComparer.Ordinal);
            if (actual.Length != segments.Length) return false;
            for (var i = 0; i < actual.Length; i++)
            {
                var template = segments[i];
                if (template.StartsWith('{'))
                    values.Add(template[1..^1], JsonSerializer.SerializeToElement(actual[i]));
                else if (!string.Equals(template, actual[i], StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        public JsonElement ParseQuery(string name, string value)
        {
            var type = supplied.SingleOrDefault(p => p.Name == name)?.ParameterType;
            if (type == typeof(string)) return JsonSerializer.SerializeToElement(value);
            if ((type == typeof(int?) || type == typeof(int)) && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
                return JsonSerializer.SerializeToElement(number);
            if ((type == typeof(bool?) || type == typeof(bool)) && bool.TryParse(value, out var flag))
                return JsonSerializer.SerializeToElement(flag);
            throw new OperationException("invalid_operation_arguments", $"Invalid value for query field '{name}'.");
        }

        public async Task<object?> CallAsync(ProjectService service, JsonElement arguments, CancellationToken ct)
        {
            Validate(Name, arguments);
            var values = parameters.Select(p => p.ParameterType == typeof(ProjectService) ? service
                : p.ParameterType == typeof(CancellationToken) ? (object)ct
                : arguments.TryGetProperty(p.Name!, out var value) ? value.Deserialize(p.ParameterType, ProjectService.Json) : null).ToArray();
            try
            {
                var result = handler.DynamicInvoke(values);
                if (result is not Task task) return result;
                await task;
                return task.GetType().GetProperty("Result")?.GetValue(task);
            }
            catch (TargetInvocationException ex) when (ex.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }

        private static bool IsScalar(Type type) => type == typeof(string) || (Nullable.GetUnderlyingType(type) ?? type).IsPrimitive;
    }
}
