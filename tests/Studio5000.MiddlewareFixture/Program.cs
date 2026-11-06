using System.Diagnostics;
using System.Text.Json;
using Rockwell.Engineering;
using Studio5000.Core.Resources;

var executable = Environment.ProcessPath!;
if (args is ["leaf", var leafRoot])
{
    var marker = Path.Combine(leafRoot, "leaf.json");
    File.WriteAllText(marker + ".tmp", JsonSerializer.Serialize(new WindowsResourceProcessDriver().CurrentIdentity()));
    File.Move(marker + ".tmp", marker);
    Thread.Sleep(30000);
    return 1;
}
if (args is ["worker", var workerRoot])
{
    using var leaf = Process.Start(Start("leaf", workerRoot))!;
    leaf.WaitForExit();
    return 1;
}

using var admission = new Mutex(false, @"Local\OpenAIEngineeringMiddleware.Job.v1");
if (!admission.WaitOne(0)) return 75;
try
{
    if (args is ["holder"])
    {
        Console.WriteLine("ready");
        _ = Console.ReadLine();
        return 0;
    }
    if (args is not ["cleanup", var root] || !Path.IsPathFullyQualified(root) || !Directory.Exists(root)) return 2;
    var leafPath = Path.Combine(root, "leaf.json");
    using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    _ = Task.Run(async () =>
    {
        while (!cancellation.IsCancellationRequested && !File.Exists(leafPath)) await Task.Delay(20);
        await cancellation.CancelAsync();
    });
    var result = OwnedCommand.Run(root, Start("worker", root), null, cancellation.Token);
    var driver = new WindowsResourceProcessDriver();
    var leafIdentity = JsonSerializer.Deserialize<ProcessIdentity>(File.ReadAllBytes(leafPath))!;
    var workerState = driver.State(result.Child!);
    var leafState = driver.State(leafIdentity);
    var passed = result.Failure is OperationCanceledException && result.Cleanup == "confirmed" &&
        workerState is "absent" or "different" && leafState is "absent" or "different";
    Console.WriteLine(JsonSerializer.Serialize(new { passed, result.Cleanup, workerState, leafState }));
    return passed ? 0 : 1;
}
finally { admission.ReleaseMutex(); }

ProcessStartInfo Start(string action, string path)
{
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = path };
    start.ArgumentList.Add(action);
    start.ArgumentList.Add(path);
    return start;
}
