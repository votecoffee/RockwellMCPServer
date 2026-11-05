using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Studio5000.Core;

try
{
    if (args.Length != 0) throw new ArgumentException("The stable launcher accepts no arguments.");
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The stable launcher requires Windows.");
    if (!Path.IsPathFullyQualified(Environment.GetEnvironmentVariable("ROCKWELL_RESOURCE_REGISTRY") ?? ""))
        throw new InvalidOperationException("Set the reviewed absolute resource registry in the saved binding.");

    var stateRoot = AppContext.BaseDirectory;
    using var parents = ParentProcessOwners.Capture();
    using var lifetime = new CancellationTokenSource();
    using var parentLost = new CancellationTokenSource();
    var watch = parents.RequestShutdownOnExitAsync(parentLost.Cancel, lifetime.Token);
    try
    {
        var selected = await Selection.ResolveAsync(stateRoot, parentLost.Token);
        parentLost.Token.ThrowIfCancellationRequested();
        Environment.SetEnvironmentVariable("STUDIO5000_PLUGIN_ROOT", selected.AppRoot);
        using var host = InheritedStdioProcess.Start(Path.Combine(selected.Snapshot, "Studio5000.StdioHost.exe"), stateRoot);
        return host.WaitForExitOrParentLoss(parentLost.Token);
    }
    catch (OperationCanceledException) when (parentLost.IsCancellationRequested) { return 0; }
    finally
    {
        lifetime.Cancel();
        await watch;
    }
}
catch (Exception error)
{
    Console.Error.WriteLine("Rockwell stable launcher: " + error.Message);
    return 1;
}

internal sealed record Selection(string AppRoot, string Snapshot)
{
    // Use the existing publisher's exact selector and closure verifier. This
    // subprocess only returns paths, with its own input and output handles.
    private const string Resolver = """
        $ErrorActionPreference = 'Stop'
        try {
            $root = [Environment]::CurrentDirectory
            $pointer = Join-Path $root 'launcher/current.json'
            $module = Join-Path $root 'StableMcp.psm1'
            if ([IO.FileInfo]::new($pointer).Length -gt 65536 -or [IO.FileInfo]::new($module).Length -gt 131072) {
                throw 'Launcher metadata exceeds its budget.'
            }
            $utf8 = [Text.UTF8Encoding]::new($false, $true)
            $selection = $utf8.GetString([IO.File]::ReadAllBytes($pointer)).TrimStart([char]0xfeff) | ConvertFrom-Json
            $sha = [Security.Cryptography.SHA256]::Create()
            $moduleFile = [IO.File]::OpenRead($module)
            try {
                $moduleHash = [BitConverter]::ToString($sha.ComputeHash($moduleFile)).Replace('-', '').ToLowerInvariant()
                if ($selection.module_sha256 -notmatch '^[a-f0-9]{64}$' -or $moduleHash -cne $selection.module_sha256) {
                    throw 'Stable module bytes differ.'
                }
                Import-Module $module -Force -DisableNameChecking
                $backend = Get-RockwellStableSelection $root
            } finally { $moduleFile.Dispose(); $sha.Dispose() }
            if (Test-Path -LiteralPath (Join-Path $backend.appRoot '.local/maintenance-hold.json')) {
                throw 'Operator maintenance hold blocks MCP startup.'
            }
            $update = Join-Path $backend.appRoot '.local/application-update.lock'
            if (Test-Path -LiteralPath $update) {
                $probe = [IO.File]::Open($update, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
                $probe.Dispose()
            }
            [Console]::OutputEncoding = $utf8
            [Console]::WriteLine((@{appRoot=$backend.appRoot; snapshot=$backend.snapshot} | ConvertTo-Json -Compress))
            exit 0
        } catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }
        """;

    public static async Task<Selection> ResolveAsync(string stateRoot, CancellationToken parentLost)
    {
        var pointer = Path.Combine(stateRoot, "launcher", "current.json");
        var initialHash = PointerHash(pointer);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            WorkingDirectory = stateRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardOutputEncoding = new UTF8Encoding(false, true)
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(Resolver)) })
            start.ArgumentList.Add(argument);
        start.Environment["PSModulePath"] = "";
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(parentLost);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The selector verifier did not start.");
        _ = process.SafeHandle;
        try
        {
            process.StandardInput.Close();
            var output = new char[8193];
            var length = await process.StandardOutput.ReadBlockAsync(output, deadline.Token);
            if (length == output.Length) throw new InvalidOperationException("Selector verifier output exceeds its budget.");
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException("Selector verification refused the launch.");
            if (!initialHash.AsSpan().SequenceEqual(PointerHash(pointer)))
                throw new InvalidOperationException("The backend selection changed during verification.");
            using var result = JsonDocument.Parse(new string(output, 0, length));
            var root = result.RootElement;
            return new(root.GetProperty("appRoot").GetString()!, root.GetProperty("snapshot").GetString()!);
        }
        finally
        {
            // This original handle belongs to the verifier alone. The selected
            // host and shared service cannot have started on this failure path.
            if (!process.HasExited) process.Kill();
        }
    }

    private static byte[] PointerHash(string path)
    {
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 65536) throw new InvalidOperationException("The backend selection exceeds its budget.");
        return SHA256.HashData(stream);
    }
}
