using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

if (args is not ["--cli", var cli] || !Path.IsPathFullyQualified(cli) ||
    !Path.GetFileName(cli).Equals("Rockwell.EngineeringCli.exe", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Usage: Rockwell.EngineeringMcp --cli <absolute Rockwell.EngineeringCli.exe path>");
    return 2;
}
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
builder.Logging.ClearProviders();
builder.Services.AddSingleton(new CliLocation(Path.GetFullPath(cli)));
builder.Services.AddMcpServer(options =>
{
    options.ServerInfo = new() { Name = "rockwell-engineering-cli", Version = "1.0.0" };
    options.ServerInstructions = "Summarize an explicitly named saved L5X export through the local CLI. Counts describe the supplied export.";
}).WithStdioServerTransport().WithToolsFromAssembly(typeof(RockwellTools).Assembly);
using var host = builder.Build();
await host.RunAsync();
return 0;

public sealed record CliLocation(string Executable);

[McpServerToolType]
public sealed class RockwellTools(CliLocation cli)
{
    [McpServerTool(Name = "rockwell_l5x_summary", ReadOnly = true),
     Description("Read a saved Logix L5X export and return its SHA-256, controller metadata and component counts.")]
    public async Task<JsonElement> Summarize(string path, CancellationToken ct, int timeoutSeconds = 30)
    {
        if (!Path.IsPathFullyQualified(path) || timeoutSeconds is < 1 or > 300)
            throw new ArgumentException("Use an absolute L5X path and a timeout of 1 through 300 seconds.");
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(cli.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(cli.Executable)!
        };
        foreach (var argument in new[] { "l5x-summary", "--input", path, "--timeout-seconds",
            timeoutSeconds.ToString(CultureInfo.InvariantCulture), "--cancel-on-stdin-eof" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The local CLI could not start.");
        using var cancellation = ct.Register(() =>
        {
            try { process.StandardInput.Close(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        });
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await Task.WhenAll(stdout, stderr, process.WaitForExitAsync());
        if (stdout.Result.Length == 0)
        {
            var detail = stderr.Result.Trim();
            if (detail.Length > 1000) detail = detail[..1000];
            throw new InvalidOperationException($"Rockwell CLI exited with code {process.ExitCode}. {detail}".TrimEnd());
        }
        using var result = JsonDocument.Parse(stdout.Result);
        return result.RootElement.Clone();
    }
}
