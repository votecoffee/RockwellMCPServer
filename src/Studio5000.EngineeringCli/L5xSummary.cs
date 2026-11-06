using System.Security.Cryptography;
using System.Xml.Linq;
using Studio5000.Core;

namespace Rockwell.Engineering;

internal static class L5xSummary
{
    internal static object Read(string path)
    {
        if (!Path.GetExtension(path).Equals(".l5x", StringComparison.OrdinalIgnoreCase))
            throw new OperationException("invalid_input", "Supply a saved Logix .L5X export.");
        var bytes = ReadBytes(path, 64 * 1024 * 1024);
        var document = XmlProjects.Parse(bytes);
        var root = document.Root!;
        var controller = root.Element("Controller")!;
        var programs = Items(controller, "Programs", "Program").ToArray();
        var routines = programs.SelectMany(program => Items(program, "Routines", "Routine")).ToArray();
        return new
        {
            input = Fingerprint(path, bytes),
            export = new
            {
                targetType = (string?)root.Attribute("TargetType"),
                targetName = (string?)root.Attribute("TargetName"),
                containsContext = (string?)root.Attribute("ContainsContext"),
                softwareRevision = (string?)root.Attribute("SoftwareRevision")
            },
            controller = new { name = (string?)controller.Attribute("Name"), processor = (string?)controller.Attribute("ProcessorType") },
            counts = new
            {
                programs = programs.Length,
                programRoutines = routines.Length,
                programRungs = routines.Sum(routine => Items(routine, "RLLContent", "Rung").Count()),
                controllerTags = Items(controller, "Tags", "Tag").Count(),
                programTags = programs.Sum(program => Items(program, "Tags", "Tag").Count()),
                dataTypes = Items(controller, "DataTypes", "DataType").Count(),
                addOnInstructions = Items(controller, "AddOnInstructionDefinitions", "AddOnInstructionDefinition").Count(),
                tasks = Items(controller, "Tasks", "Task").Count()
            }
        };
    }

    internal static byte[] ReadBytes(string path, int maximum)
    {
        ProjectService.RejectLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is <= 0 || file.Length > maximum)
            throw new OperationException("invalid_input_size", $"Input must contain 1 through {maximum} bytes.");
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        return bytes;
    }

    internal static object Fingerprint(string path, byte[] bytes) =>
        new { path, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };

    private static IEnumerable<XElement> Items(XElement parent, string container, string element) =>
        parent.Element(container)?.Elements(element) ?? [];
}
