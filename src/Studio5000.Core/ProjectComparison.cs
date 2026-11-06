using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record ProjectCompareRequest(string LeftProjectId, string LeftSha256, string RightProjectId, string RightSha256,
    string? Query = null, int Offset = 0, int Limit = 100, bool IncludeRungNumbers = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? ProgramName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? RoutineName = null);
public sealed record ProjectDifference(string Kind, string Path, string Change, string? LeftXPath, string? RightXPath,
    string? LeftSha256, string? RightSha256, string? LeftXml, string? RightXml, bool LeftTruncated, bool RightTruncated);
public sealed record ProjectComparisonReport(ProjectInfo Left, ProjectInfo Right, int TotalChanges, int FilteredChanges,
    int Added, int Removed, int Modified, int Offset, int Limit, ProjectDifference[] Differences, string Boundary);

public static class ProjectComparison
{
    internal const int MaximumAnalysisElements = 1_000_000;
    private static readonly HashSet<string> Expanded = ["RSLogix5000Content", "Controller", "Programs", "Program", "Routines",
        "AddOnInstructionDefinitions", "AddOnInstructionDefinition", "Tags", "DataTypes", "Modules", "Tasks"];

    public static ProjectComparisonReport Compare(ProjectInfo left, XDocument leftDocument, ProjectInfo right,
        XDocument rightDocument, ProjectCompareRequest request) => CompareCore(left, leftDocument, right, rightDocument, request, false);

    internal static ProjectComparisonReport CompareForReview(ProjectInfo left, XDocument leftDocument, ProjectInfo right,
        XDocument rightDocument, ProjectCompareRequest request) => CompareCore(left, leftDocument, right, rightDocument, request with { Offset = 0, Limit = 200 }, true);

    private static ProjectComparisonReport CompareCore(ProjectInfo left, XDocument leftDocument, ProjectInfo right,
        XDocument rightDocument, ProjectCompareRequest request, bool allMatches)
    {
        if (request.Offset is < 0 or > 100000 || request.Limit is < 1 or > 200 || request.Query?.Length > 256)
            throw new OperationException("invalid_comparison", "Use offset 0..100000, limit 1..200 and a query up to 256 characters.");
        ValidateSize(leftDocument, MaximumAnalysisElements); ValidateSize(rightDocument, MaximumAnalysisElements);
        ValidateScope(request);
        var changes = new List<ProjectDifference>();
        var addedCount = 0; var removedCount = 0; var modifiedCount = 0; var filteredCount = 0;
        string Canon(XElement element, bool header = false) => Normalize(element, request.IncludeRungNumbers, header).ToString(SaveOptions.DisableFormatting);
        void Add(XElement? a, XElement? b, string path, bool header = false)
        {
            if (!header && XNode.DeepEquals(a, b)) return;
            var before = a is null ? null : Canon(a, header);
            var after = b is null ? null : Canon(b, header);
            if (before == after) return;
            var kind = (b ?? a)!.Name.LocalName;
            var change = a is null ? "added" : b is null ? "removed" : "modified";
            if (a is null) addedCount++;
            else if (b is null) removedCount++;
            else modifiedCount++;
            if (!Matches(request.Query, path, kind)) return;
            var position = filteredCount++;
            if (allMatches && filteredCount > ComparisonReviews.MaximumChanges)
                throw new OperationException("comparison_review_limit", "A review supports up to 5000 matching changes; narrow the comparison filter.");
            if (!allMatches && (position < request.Offset || changes.Count == request.Limit)) return;
            static string? Hash(string? text) => text is null ? null : ProjectService.Hash(Encoding.UTF8.GetBytes(text));
            static string? Snip(string? text) => text is { Length: > 4096 } ? text[..4096] : text;
            changes.Add(new(kind, path, change,
                a is null ? null : XmlProjects.PathOf(a), b is null ? null : XmlProjects.PathOf(b), Hash(before), Hash(after),
                Snip(before), Snip(after), before?.Length > 4096, after?.Length > 4096));
        }
        void Walk(XElement? a, XElement? b, string path)
        {
            if (a is null || b is null) { if (a is not null || b is not null) Add(a, b, path); return; }
            if (a.Name != b.Name) { Add(a, b, path); return; }
            if (a.Name == "RLLContent")
            {
                if (XNode.DeepEquals(a, b)) return;
                Add(a, b, path + "/@metadata", true);
                var oldRungs = a.Elements().ToArray(); var newRungs = b.Elements().ToArray();
                if (oldRungs.Any(e => e.Name != "Rung") || newRungs.Any(e => e.Name != "Rung"))
                    throw new OperationException("unsupported_comparison", "RLLContent contains unsupported non-rung elements.");
                var matches = Align(oldRungs.Select(e => Canon(e)).ToArray(), newRungs.Select(e => Canon(e)).ToArray());
                var removed = new List<int>(); var added = new List<int>();
                void Flush()
                {
                    for (var i = 0; i < Math.Max(removed.Count, added.Count); i++)
                    {
                        var oldRung = i < removed.Count ? oldRungs[removed[i]] : null;
                        var newRung = i < added.Count ? newRungs[added[i]] : null;
                        var ordinal = i < added.Count ? added[i] : removed[i];
                        Add(oldRung, newRung, path + $"/Rung[position={ordinal}]");
                    }
                    removed.Clear(); added.Clear();
                }
                foreach (var pair in matches)
                {
                    if (pair.Left >= 0 && pair.Right >= 0) Flush();
                    else { if (pair.Left >= 0) removed.Add(pair.Left); if (pair.Right >= 0) added.Add(pair.Right); }
                }
                Flush(); return;
            }
            if (!Expanded.Contains(a.Name.LocalName) && !(a.Name == "Routine" && (string?)a.Attribute("Type") == "RLL" && (string?)b.Attribute("Type") == "RLL"))
            { Add(a, b, path); return; }
            Add(a, b, path + "/@metadata", true);
            var oldChildren = Children(a); var newChildren = Children(b);
            foreach (var key in oldChildren.Keys.Union(newChildren.Keys, StringComparer.OrdinalIgnoreCase))
            {
                oldChildren.TryGetValue(key, out var oldChild); newChildren.TryGetValue(key, out var newChild);
                Walk(oldChild, newChild, path + "/" + key);
            }
        }
        const string root = "/RSLogix5000Content";
        if (request.ProgramName is { } programName)
        {
            XElement? SelectScope(XDocument doc)
            {
                var program = doc.Root!.Element("Controller")!.Elements("Programs").Elements("Program")
                    .SingleOrDefault(p => LogicContext.Same((string?)p.Attribute("Name"), programName));
                return request.RoutineName is { } routineName
                    ? program?.Elements("Routines").Elements("Routine").SingleOrDefault(r => LogicContext.Same((string?)r.Attribute("Name"), routineName))
                    : program;
            }
            var a = SelectScope(leftDocument); var b = SelectScope(rightDocument);
            if (a is null && b is null) throw new OperationException("comparison_scope_missing", "The selected program or routine is absent from both snapshots.");
            var path = root + "/Controller[1]/Programs[1]/Program[" + programName + "]";
            if (request.RoutineName is { } routineName) path += "/Routines[1]/Routine[" + routineName + "]";
            Walk(a, b, path);
        }
        else Walk(leftDocument.Root, rightDocument.Root, root);
        var result = new ProjectComparisonReport(left, right, addedCount + removedCount + modifiedCount, filteredCount,
            addedCount, removedCount, modifiedCount, request.Offset, allMatches ? filteredCount : request.Limit, changes.ToArray(),
            "Structural XML comparison only. Counts cover the selected program/routine or the entire project when unscoped. Attribute order/formatting indentation and root ExportDate/Controller LastModifiedDate are ignored; scalar text, stored values, UDT member/AOI parameter order and unknown component XML remain significant. Rung numbers are optional. LCS aligns rung occurrences; moves are not inferred. No native semantic equivalence or runtime validation.");
        if (!allMatches && JsonSerializer.SerializeToUtf8Bytes(result, ProjectService.Json).Length > 4 * 1024 * 1024)
            throw new OperationException("comparison_page_too_large", "Comparison page exceeds 4 MiB; request a smaller limit.");
        return result;
    }

    private static Dictionary<string, XElement> Children(XElement parent)
    {
        var result = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var ordinals = new Dictionary<XName, int>();
        foreach (var child in parent.Elements())
        {
            var ordinal = ordinals[child.Name] = ordinals.GetValueOrDefault(child.Name) + 1;
            var identity = child.Name == "Controller" ? null : (string?)child.Attribute("Name") ?? (string?)child.Attribute("OnlineEditType");
            var key = child.Name + (identity is not null ? "[" + identity + "]" : "[" + ordinal + "]");
            if (!result.TryAdd(key, child)) throw new OperationException("ambiguous_comparison", "Duplicate child identities prevent structural comparison.");
        }
        return result;
    }

    internal static XElement Normalize(XElement element, bool rungNumbers, bool header = false)
    {
        var result = new XElement(element.Name, element.Attributes().Where(a =>
            !(element.Name == "RSLogix5000Content" && a.Name == "ExportDate") &&
            !(element.Name == "Controller" && a.Name == "LastModifiedDate") &&
            !(!rungNumbers && element.Name == "Rung" && a.Name == "Number")).OrderBy(a => a.Name.ToString(), StringComparer.Ordinal).Select(a => new XAttribute(a)));
        foreach (var node in element.Nodes())
            switch (node)
            {
                case XElement child when !header: result.Add(Normalize(child, rungNumbers)); break;
                case XText text when !element.HasElements || !string.IsNullOrWhiteSpace(text.Value): result.Add(new XText(text.Value)); break;
                case XComment comment: result.Add(new XComment(comment.Value)); break;
                case XProcessingInstruction instruction: result.Add(new XProcessingInstruction(instruction.Target, instruction.Data)); break;
            }
        return result;
    }

    internal static bool Matches(string? query, string path, string kind) => string.IsNullOrEmpty(query) ||
        path.Contains(query, StringComparison.OrdinalIgnoreCase) || kind.Contains(query, StringComparison.OrdinalIgnoreCase);

    internal static void ValidateScope(ProjectCompareRequest request)
    {
        if (request.ProgramName is not null) WatchValidation.Identifier(request.ProgramName);
        if (request.RoutineName is not null)
        {
            if (request.ProgramName is null) throw new OperationException("comparison_scope", "A routine selector requires its program name.");
            WatchValidation.Identifier(request.RoutineName);
        }
    }

    internal static void ValidateSize(XDocument document, int maximumElements = 100_000)
    {
        var count = 0; var depth = -1;
        XElement? previous = null;
        foreach (var element in document.Descendants())
        {
            while (previous != element.Parent) { previous = previous!.Parent; depth--; }
            if (++count > maximumElements || ++depth > 64)
                throw new OperationException("comparison_limit", $"Comparison supports {maximumElements} XML elements and 64 nesting levels per project.");
            previous = element;
        }
    }

    internal static List<(int Left, int Right)> Align(string[] left, string[] right)
    {
        var result = new List<(int, int)>();
        var prefix = 0;
        while (prefix < Math.Min(left.Length, right.Length) && left[prefix] == right[prefix]) { result.Add((prefix, prefix)); prefix++; }
        var suffix = 0;
        while (suffix < Math.Min(left.Length, right.Length) - prefix && left[^(suffix + 1)] == right[^(suffix + 1)]) suffix++;
        var n = left.Length - prefix - suffix; var m = right.Length - prefix - suffix;
        if ((long)(n + 1) * (m + 1) > 1000000)
            throw new OperationException("comparison_limit", "Changed rung sequences exceed the 1000000-cell alignment budget; compare smaller project copies.");
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
            for (var j = m - 1; j >= 0; j--)
                table[i, j] = left[prefix + i] == right[prefix + j] ? 1 + table[i + 1, j + 1] : Math.Max(table[i + 1, j], table[i, j + 1]);
        var a = 0; var b = 0;
        while (a < n || b < m)
        {
            if (a < n && b < m && left[prefix + a] == right[prefix + b]) { result.Add((prefix + a++, prefix + b++)); }
            else if (a < n && (b == m || table[a + 1, b] >= table[a, b + 1])) result.Add((prefix + a++, -1));
            else result.Add((-1, prefix + b++));
        }
        for (var i = suffix; i > 0; i--) result.Add((left.Length - i, right.Length - i));
        return result;
    }
}
