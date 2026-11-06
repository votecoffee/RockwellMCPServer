using System.Diagnostics;
using System.Text;
using Studio5000.Core.Resources;

namespace Rockwell.Engineering;

internal sealed record OwnedResult(int? ExitCode, string Output, string ErrorOutput, string Cleanup, string? ResourceId,
    ProcessIdentity? Child, Exception? Failure);

internal static class OwnedCommand
{
    internal static OwnedResult Run(string job, ProcessStartInfo start, string? input, CancellationToken cancellation)
    {
        var ledger = new ResourceLedger(Path.Combine(job, "registry"), new WindowsResourceProcessDriver(), 2);
        var owner = ledger.Owner("rockwell-engineering-cli");
        WindowsChildProcess? child = null;
        int? exit = null;
        string output = "", error = "", cleanup = "not-started";
        Exception? failure = null;
        try
        {
            cancellation.ThrowIfCancellationRequested();
            cleanup = "uncertain";
            child = WindowsChildProcess.Launch(owner, start, "Rockwell middleware job", maximumProcesses: 8);
            var stdout = Task.Run(() => Read(child.StandardOutput, 4 * 1024 * 1024));
            var stderr = Task.Run(() => Read(child.StandardError, 64 * 1024));
            var stdin = Task.Run(() =>
            {
                using var writer = child.StandardInput;
                if (input is not null) writer.Write(input);
            });
            Task.WhenAll(stdout, stderr, stdin, child.WaitForExitAsync(cancellation)).WaitAsync(cancellation).GetAwaiter().GetResult();
            exit = child.ExitCode;
            output = stdout.Result;
            error = stderr.Result;
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (child is not null)
            {
                try
                {
                    var receipt = child.Close();
                    cleanup = receipt.Outcome is "terminated" or "already_exited" ? "confirmed" : "uncertain";
                }
                catch (Exception ex)
                {
                    cleanup = "uncertain";
                    failure ??= ex;
                }
            }
        }
        return new(exit, output, error, cleanup, child?.ResourceId, child?.Identity, failure);
    }

    private static string Read(StreamReader source, int maximum)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        while (source.Read(buffer, 0, buffer.Length) is var count && count != 0)
        {
            if (result.Length + count > maximum) throw new IOException("Worker output exceeds its limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
