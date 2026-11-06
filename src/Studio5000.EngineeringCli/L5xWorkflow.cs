using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;

namespace Rockwell.Engineering;

internal static class L5xWorkflow
{
    private const int MaximumL5x = 64 * 1024 * 1024;
    private const int MaximumText = 4096;

    internal static object Inspect(string requestPath)
    {
        var request = ReadJson<InspectionRequest>(requestPath);
        var source = L5xPath(request.SourcePath);
        var bytes = L5xSummary.ReadBytes(source, MaximumL5x);
        var controller = XmlProjects.Parse(bytes).Root!.Element("Controller")!;
        if (request.Offset < 0 || request.Limit is < 1 or > 100 || request.Kind != "rungs" && request.Routine is not null)
            throw new OperationException("invalid_inspection", "Use offset >= 0, limit 1 through 100, and a routine name only for rungs.");
        var program = request.Program is null ? null : Named(controller.Element("Programs"), "Program", request.Program);
        IEnumerable<XElement> rows = request.Kind switch
        {
            "tags" => (program ?? controller).Element("Tags")?.Elements("Tag") ?? [],
            "routines" when program is not null => program.Element("Routines")?.Elements("Routine") ?? [],
            "rungs" when program is not null && request.Routine is not null => Rungs(program, request.Routine),
            _ => throw new OperationException("invalid_inspection", "Choose tags, routines with a program, or rungs with a program and routine.")
        };
        var total = rows.Count();
        var page = rows.Skip(request.Offset).Take(request.Limit).Select(element =>
        {
            var value = element.Elements("Data").FirstOrDefault(data => (string?)data.Attribute("Format") == "Decorated")
                ?.Element("DataValue")?.Attribute("Value")?.Value;
            var logic = element.Name == "Rung" ? element.Element("Text")?.Value : null;
            return new
            {
                name = (string?)element.Attribute("Name"),
                number = (string?)element.Attribute("Number"),
                type = (string?)element.Attribute("Type") ?? (string?)element.Attribute("TagType"),
                dataType = (string?)element.Attribute("DataType"),
                aliasFor = (string?)element.Attribute("AliasFor"),
                savedScalar = Bounded(value),
                logic = Bounded(logic),
                logicTruncated = logic?.Length > MaximumText,
                xpath = XmlProjects.PathOf(element),
                editableTexts = element.Elements().Where(IsEditableText).Select(text =>
                    new { kind = text.Name.LocalName, xpath = XmlProjects.PathOf(text), text = text.Value })
            };
        }).ToArray();
        return new { input = L5xSummary.Fingerprint(source, bytes), request.Kind,
            program = (string?)program?.Attribute("Name"), request.Routine, total, request.Offset, request.Limit, items = page };
    }

    internal static object Preview(string job, string requestPath)
    {
        var request = ReadJson<PreviewRequest>(requestPath);
        var source = L5xPath(request.SourcePath);
        var original = L5xSummary.ReadBytes(source, MaximumL5x);
        RequireHash(original, request.ExpectedSha256, "stale_source");
        var text = request.Text?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            ?? throw new OperationException("invalid_edit", "Supply the replacement text.");
        var (before, revised) = Revise(original, request.XPath, text);
        var review = new Review(1, source, ProjectService.Hash(original), request.XPath, before, text,
            ProjectService.Hash(revised));
        var previewPath = Path.Combine(job, "preview.L5X");
        using (var preview = new FileStream(previewPath, FileMode.CreateNew, FileAccess.Write)) preview.Write(revised);
        var reviewPath = Path.Combine(job, "review.json");
        EngineeringCli.WriteNew(reviewPath, review);
        return new { input = L5xSummary.Fingerprint(source, original), review,
            reviewFile = new { path = reviewPath, sha256 = ProjectService.Hash(File.ReadAllBytes(reviewPath)) },
            previewFile = new { path = previewPath, sha256 = review.OutputSha256 }, preservation = "scalar-byte-spans-v1" };
    }

    internal static object Apply(string job, string requestPath)
    {
        var request = ReadJson<ApplyRequest>(requestPath);
        var reviewPath = Absolute(request.ReviewPath, "review path");
        var reviewBytes = L5xSummary.ReadBytes(reviewPath, 64 * 1024);
        RequireHash(reviewBytes, request.ReviewSha256, "stale_review");
        var review = Deserialize<Review>(reviewBytes);
        if (review.SchemaVersion != 1) throw new OperationException("invalid_review", "Use a review created by l5x-preview.");
        var source = L5xPath(review.SourcePath);
        var output = L5xPath(request.OutputPath);
        ProjectService.RejectLinks(source);
        ProjectService.RejectLinks(output);
        if (string.Equals(source, output, StringComparison.OrdinalIgnoreCase) || File.Exists(output) || Directory.Exists(output))
            throw new OperationException("output_exists", "Choose a new L5X output path. Existing files are preserved.");
        var directory = Path.GetDirectoryName(output)!;
        if (!Directory.Exists(directory)) throw new OperationException("output_directory_missing", "The output directory must exist.");

        // Keep a read-sharing handle on the exact source through publication.
        using var sourceFile = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        var original = L5xSummary.ReadBytes(sourceFile, MaximumL5x);
        RequireHash(original, review.SourceSha256, "stale_source");
        var (before, revised) = Revise(original, review.XPath, review.After);
        if (before != review.Before) throw new OperationException("stale_review", "The selected source text differs from the review.");
        RequireHash(revised, review.OutputSha256, "stale_review");

        var temporary = Path.Combine(directory, ".rockwell-" + Guid.NewGuid().ToString("N") + ".tmp");
        EngineeringCli.WriteNew(Path.Combine(job, "apply-intent.json"), new { source, reviewPath, reviewSha256 = request.ReviewSha256,
            output, temporary, expectedOutputSha256 = review.OutputSha256 });
        var wroteTemporary = false;
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                wroteTemporary = true;
                file.Write(revised);
                file.Flush(true);
            }
            File.Move(temporary, output, overwrite: false);
            wroteTemporary = false;
        }
        finally { if (wroteTemporary) File.Delete(temporary); }
        var readback = L5xSummary.ReadBytes(output, MaximumL5x);
        RequireHash(readback, review.OutputSha256, "output_readback_mismatch");
        var text = XmlProjects.Select(XmlProjects.Parse(readback), review.XPath).Value;
        if (text != review.After) throw new OperationException("output_readback_mismatch", "The published text differs from the review.");
        return new { input = L5xSummary.Fingerprint(source, original), output = L5xSummary.Fingerprint(output, readback),
            reviewSha256 = request.ReviewSha256, readback = new { review.XPath, text }, sourcePreserved = true };
    }

    internal static T ReadJson<T>(string path) where T : class =>
        Deserialize<T>(L5xSummary.ReadBytes(path, 64 * 1024));

    private static T Deserialize<T>(byte[] bytes) where T : class =>
        JsonSerializer.Deserialize<T>(bytes, EngineeringCli.StrictJson)
            ?? throw new OperationException("invalid_request", "Supply a JSON request object.");

    internal static string Absolute(string? path, string label) =>
        path is not null && Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) :
        throw new OperationException("invalid_path", $"Use an absolute {label}.");

    internal static void RequireHash(byte[] bytes, string? expected, string code)
    {
        if (!string.Equals(ProjectService.Hash(bytes), expected, StringComparison.OrdinalIgnoreCase))
            throw new OperationException(code, "The file bytes do not match the required SHA-256.");
    }

    private static string L5xPath(string? path)
    {
        var full = Absolute(path, "L5X path");
        if (!Path.GetExtension(full).Equals(".l5x", StringComparison.OrdinalIgnoreCase))
            throw new OperationException("invalid_input", "Use an L5X file.");
        return full;
    }

    private static XElement Named(XElement? container, string kind, string name) =>
        container?.Elements(kind).SingleOrDefault(element => string.Equals((string?)element.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase))
            ?? throw new OperationException("scope_missing", $"The requested {kind.ToLowerInvariant()} was not found in this export.");

    private static IEnumerable<XElement> Rungs(XElement program, string routine)
    {
        var element = Named(program.Element("Routines"), "Routine", routine);
        if ((string?)element.Attribute("Type") != "RLL")
            throw new OperationException("unsupported_routine", "Rung inspection requires an RLL routine.");
        return element.Element("RLLContent")?.Elements("Rung") ?? [];
    }

    private static bool IsEditableText(XElement text) => !text.HasElements && text.Value.Length <= MaximumText &&
        (text.Name == "Description" && text.Parent?.Name.LocalName is "Tag" or "Routine" ||
         text.Name == "Comment" && text.Parent?.Name == "Rung");

    private static string? Bounded(string? text) => text?.Length > MaximumText ? text[..MaximumText] : text;

    private static (string Before, byte[] Revised) Revise(byte[] original, string? xpath, string? text)
    {
        if (string.IsNullOrWhiteSpace(xpath) || xpath.Length > 2048 || text is null || text.Length > MaximumText)
            throw new OperationException("invalid_edit", "Supply an inspected XPath and at most 4096 characters of text.");
        var document = XmlProjects.Parse(original);
        var selected = XmlProjects.Select(document, xpath);
        if (!IsEditableText(selected) || XmlProjects.PathOf(selected) != xpath)
            throw new OperationException("invalid_edit", "Use the exact inspected XPath of an existing scalar tag/routine description or rung comment.");
        var revised = XmlProjects.Apply(original, [new Edit("set_text", xpath, text)]);
        if (revised.Length > MaximumL5x)
            throw new OperationException("invalid_output_size", "The revised L5X would exceed 64 MiB.");
        return (selected.Value, revised);
    }

    private sealed record InspectionRequest(string SourcePath, string Kind, string? Program = null, string? Routine = null,
        int Offset = 0, int Limit = 25);
    private sealed record PreviewRequest(string SourcePath, string ExpectedSha256, string XPath, string Text);
    private sealed record ApplyRequest(string ReviewPath, string ReviewSha256, string OutputPath);
    private sealed record Review(int SchemaVersion, string SourcePath, string SourceSha256, string XPath, string Before,
        string After, string OutputSha256);
}
