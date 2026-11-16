using Studio5000.Core;

namespace Rockwell.Engineering;

// The workspace owns a bounded SDK session; it is not a detached background host.
// Record the job identity before waiting, so parent termination never loses it.
internal static class SdkJobWait
{
    internal static async Task<SdkJob> FinishAsync(SdkJob sdk, string job, string stateDirectory,
        Func<string, SdkJob> read, CancellationToken ct)
    {
        var reference = Path.Combine(job, "sdk-" + sdk.Id + ".json");
        EngineeringCli.WriteNew(reference, new { sdk.Id, sdk.Status, sdk.SourceSha256, sdk.ControllerEffectsPossible,
            record = Path.Combine(stateDirectory, "sdk-jobs", sdk.Id, "job.json") });
        while (sdk.Status is "queued" or "running")
        {
            await Task.Delay(100, ct);
            sdk = read(sdk.Id);
        }
        EngineeringCli.WriteNew(Path.Combine(job, "sdk-" + sdk.Id + "-terminal.json"),
            new { sdk.Id, sdk.Status, sdk.Error, reference });
        return sdk;
    }

    internal static string[] References(string job) => Directory.EnumerateFiles(job, "sdk-*.json")
        .Where(path => Path.GetFileName(path) is { Length: 41 } name &&
            name.AsSpan(4, 32).ToString().All(char.IsAsciiHexDigit))
        .Take(64).Order(StringComparer.Ordinal).ToArray();
}
