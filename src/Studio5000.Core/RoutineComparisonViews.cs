using System.Xml.Linq;
using System.Text;

namespace Studio5000.Core;

public sealed record ComparisonViewInfo(string Mode, string Boundary);

// Projection of known routine documentation only. No executable tokens,
// declarations, stored values, metadata, unknown XML or ciphertext are rewritten.
internal static class RoutineComparisonViews
{
    internal static ComparisonViewInfo? Info(string? mode)
    {
        if (mode is null or "exact") return null;
        if (mode is not ("instructions" or "annotation-blank-lines"))
            throw new OperationException("invalid_comparison_view", "Use routineView exact, instructions or annotation-blank-lines.");
        return new(mode, mode == "instructions"
            ? "Opt-in routine view excludes known XML routine descriptions and rung comments in readable RLL/ST only. Instruction text and routine metadata remain exact. Inline code comments, unsupported/opaque routines, tags, values and configuration remain significant. Source hashes are original-file hashes; projected component hashes are not native equivalence."
            : "Opt-in routine view removes blank lines and normalizes CRLF/CR to LF in known XML routine descriptions and rung comments in readable RLL/ST only. Nonblank text, instruction text, metadata, unsupported/opaque XML, tags, values and configuration remain significant. Source hashes remain original-file hashes.");
    }
    private static bool Supported(XElement routine) => (string?)routine.Attribute("Type") is "RLL" or "ST" &&
        !routine.DescendantsAndSelf().Any(e => e.Name.LocalName == "EncodedData" || e.Attributes().Any(a =>
            a.Name.LocalName.Contains("Encrypted", StringComparison.OrdinalIgnoreCase) || a.Name.LocalName.Contains("SourceKey", StringComparison.OrdinalIgnoreCase)));
    private static bool Annotation(XElement e) => e.Name.Namespace == XNamespace.None &&
        (e.Name.LocalName is "Description" or "LocalizedDescription" && e.Parent?.Name == "Routine" ||
         e.Name.LocalName is "Comment" or "LocalizedComment" && e.Parent?.Name == "Rung" && e.Parent.Parent?.Name == "RLLContent");
    private static string Lines(string value)
    {
        var output = new StringBuilder(); var start = 0; var nonblank = false;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i < value.Length && value[i] is not ('\r' or '\n')) { nonblank |= value[i] is not (' ' or '\t'); continue; }
            if (nonblank) { if (output.Length > 0) output.Append('\n'); output.Append(value.AsSpan(start, i - start)); }
            if (i < value.Length && value[i] == '\r' && i + 1 < value.Length && value[i + 1] == '\n') i++;
            start = i + 1; nonblank = false;
        }
        return output.ToString();
    }
    internal static string? Canon(XElement element, bool numbers, string? mode, bool header = false)
    {
        if (Info(mode) is null || header || !element.AncestorsAndSelf("Routine").Any() &&
            element.Name.LocalName is not ("RSLogix5000Content" or "Controller" or "Programs" or "Program" or "Routines" or "AddOnInstructionDefinitions" or "AddOnInstructionDefinition"))
            return ProjectComparison.Normalize(element, numbers, header).ToString(SaveOptions.DisableFormatting);
        var copy = new XElement(element);
        bool Applies(XElement e) => e.AncestorsAndSelf("Routine").FirstOrDefault() is { } r ? Supported(r) :
            element.Ancestors("Routine").FirstOrDefault() is { } owner && Supported(owner);
        var annotations = copy.DescendantsAndSelf().Where(e =>
            (e == copy ? Annotation(element) : Annotation(e) || e.Parent == copy && element.Name == "Rung" && element.Parent?.Name == "RLLContent" &&
                e.Name.Namespace == XNamespace.None && e.Name.LocalName is "Comment" or "LocalizedComment") && Applies(e)).ToArray();
        foreach (var annotation in annotations)
        {
            if (mode == "instructions")
            { if (annotation == copy) return null; annotation.Remove(); }
            else foreach (var leaf in annotation.DescendantsAndSelf().Where(e => !e.HasElements && e.Nodes().All(n => n is XText))) leaf.Value = Lines(leaf.Value);
        }
        return ProjectComparison.Normalize(copy, numbers, header).ToString(SaveOptions.DisableFormatting);
    }
}
