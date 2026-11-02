using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record SelectorRefactorRequest(string ExpectedSha256, string RoutineXPath, int RungIndex, string RouteTagPrefix);
public sealed record SelectorRouteTag(string Name, string ProgramName, string DataType);
public sealed record SelectorPreviewRung(string Kind, int? WriteOrder, string Text, int MaximumBranchDepth);
public sealed record SelectorWrite(int Order, string Source, string Destination, string RouteTag);
public sealed record SelectorRefactorReport(int SchemaVersion, string ToolVersion, ComparisonSnapshot Source, string RoutineXPath, int RungIndex, string OriginalText,
    string OriginalRungSha256, string NativeProfile, int OriginalBranchDepth, SelectorRouteTag[] ProposedTags,
    SelectorWrite[] Writes, SelectorPreviewRung[] Rungs, string ReviewSha256, string[] Limitations);

internal static class SelectorRefactor
{
    private const int MaximumPaths = 128;
    private static OperationException Unsupported(string message) => new("selector_refactor_unsupported", message);
    private sealed record Operand(string Type, string Root, bool Writable);

    public static SelectorRefactorReport Preview(ProjectInfo project, XDocument doc, SelectorRefactorRequest request)
    {
        if (request.RungIndex is < 0 or > 100000 || request.RoutineXPath is null || request.RoutineXPath.Length > 8192 ||
            request.RouteTagPrefix is null || !Regex.IsMatch(request.RouteTagPrefix, "^[A-Za-z_][A-Za-z0-9_]{0,23}$", RegexOptions.CultureInvariant))
            throw new OperationException("selector_refactor_query", "Select one RLL rung and a route-tag prefix of 1..24 identifier characters.");
        var routine = XmlProjects.Select(doc, request.RoutineXPath);
        if (routine.Name != "Routine" || (string?)routine.Attribute("Type") != "RLL" || routine.Parent?.Name != "Routines" || routine.Parent.Parent?.Name != "Program")
            throw Unsupported("Only readable program RLL routines are supported.");
        var program = routine.Parent.Parent; var programName = (string?)program.Attribute("Name") ?? throw Unsupported("Program name is absent.");
        if (string.Equals((string?)program.Attribute("Class"), "Safety", StringComparison.OrdinalIgnoreCase)) throw Unsupported("Safety-program selector refactoring is unsupported.");
        var rung = routine.Elements("RLLContent").Elements("Rung").ElementAtOrDefault(request.RungIndex) ?? throw new OperationException("selector_refactor_query", "Rung index is absent.");
        var text = (string?)rung.Element("Text") ?? "";
        if (rung.Attribute("OnlineEditType") is { Value: not "" and not "None" }) throw Unsupported("Resolve online-edit rung states before reviewing a saved selector.");
        var parts = LadderPreview.ParseLogic(text);
        if (parts.Count == 0 || parts[^1].Legs is not null || !parts[^1].Instruction.Equals("OTE", StringComparison.OrdinalIgnoreCase) || parts[^1].Operands.Length != 1)
            throw Unsupported("The selected rung must end in one top-level OTE for final setup.");
        var context = new LogicContext(doc); var types = LogixStructures.Types(doc);
        foreach (var scope in new[] { context.Controller, program })
            if (scope.Elements("Tags").Elements("Tag").GroupBy(t => (string?)t.Attribute("Name"), StringComparer.OrdinalIgnoreCase).Any(g => g.Count() != 1))
                throw Unsupported("Ambiguous tag declarations are unsupported.");
        if (context.Controller.Elements("Programs").Elements("Program").Count(p => LogicContext.Same((string?)p.Attribute("Name"), programName)) != 1)
            throw Unsupported("Ambiguous program declarations are unsupported.");
        Operand Resolve(string operand, bool constant = false)
        {
            if (constant && int.TryParse(operand, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _)) return new("DINT", "", false);
            var address = context.Resolve(operand, programName);
            if (address is null || address.Issue is not null) throw Unsupported("Unresolved or unsupported saved operand: " + operand);
            if (address.Program is not null && !LogicContext.Same(address.Program, programName)) throw Unsupported("Cross-program operands are unsupported: " + operand);
            var declaration = context.Tag(address.Tag, address.Program)!;
            var type = (string?)declaration.Attribute("DataType") ?? ""; var dims = LogixStructures.Dimensions((string?)declaration.Attribute("Dimensions"));
            var suffix = address.Member; var position = 0;
            while (position < suffix.Length)
            {
                if (suffix[position] == '[')
                {
                    var end = suffix.IndexOf(']', position); if (end < 0) throw Unsupported("Unclosed array address: " + operand);
                    var indices = suffix[(position + 1)..end].Split(',');
                    if (indices.Length != dims.Length || dims.Length == 0) throw Unsupported("Select one complete fixed array element: " + operand);
                    for (var i = 0; i < indices.Length; i++)
                        if (!int.TryParse(indices[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= dims[i])
                            throw Unsupported("Dynamic or out-of-range array address: " + operand);
                    dims = []; position = end + 1;
                }
                else if (suffix[position] == '.')
                {
                    var start = ++position; while (position < suffix.Length && (char.IsAsciiLetterOrDigit(suffix[position]) || suffix[position] == '_')) position++;
                    var name = suffix[start..position]; if (dims.Length != 0 || name.Length == 0) throw Unsupported("Unresolved member: " + operand);
                    if (type.Equals("DINT", StringComparison.OrdinalIgnoreCase) && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var bit) && bit < 32) type = "BOOL";
                    else
                    {
                        var declarations = types.Where(t => LogicContext.Same(t.Name, type)).ToArray();
                        if (declarations.Length != 1) throw Unsupported("Member type metadata is unavailable or ambiguous: " + operand);
                        var members = declarations[0].Members.Where(m => LogicContext.Same(m.Name, name)).ToArray();
                        if (members.Length != 1 || members[0].Hidden) throw Unsupported("Member metadata is unavailable, hidden or ambiguous: " + operand);
                        type = members[0].DataType; dims = members[0].Dimensions;
                        if (type.Equals("BIT", StringComparison.OrdinalIgnoreCase)) type = "BOOL";
                    }
                }
                else throw Unsupported("Unsupported member address: " + operand);
            }
            if (dims.Length != 0) throw Unsupported("Array operands must select a scalar leaf: " + operand);
            var writable = !string.Equals((string?)declaration.Attribute("Constant"), "true", StringComparison.OrdinalIgnoreCase) &&
                string.Equals((string?)declaration.Attribute("TagType") ?? "Base", "Base", StringComparison.OrdinalIgnoreCase);
            return new(type.ToUpperInvariant(), address.Symbol, writable);
        }
        void Require(Operand operand, string type, bool writable = false)
        {
            if (operand.Type != type || writable && !operand.Writable) throw Unsupported($"Expected {(writable ? "writable " : "")}{type} scalar; found {operand.Type}.");
        }
        var predicateRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var all = new List<LadderPreview.Part>();
        void Gather(List<LadderPreview.Part> sequence)
        {
            foreach (var part in sequence)
                if (part.Legs is { } legs) { foreach (var leg in legs) Gather(leg); }
                else all.Add(part);
        }
        Gather(parts[..^1]);
        foreach (var part in all)
        {
            if (LadderPreflight.Predicate(part))
            {
                var contact = part.Instruction.Equals("XIC", StringComparison.OrdinalIgnoreCase) || part.Instruction.Equals("XIO", StringComparison.OrdinalIgnoreCase);
                foreach (var value in part.Operands)
                {
                    var operand = Resolve(value, !contact); Require(operand, contact ? "BOOL" : "DINT");
                    if (operand.Root.Length > 0) predicateRoots.Add(operand.Root);
                }
            }
            else if (part.Instruction.Equals("MOV", StringComparison.OrdinalIgnoreCase) && part.Operands.Length == 2)
            {
                Require(Resolve(part.Operands[0], true), "DINT"); var destination = Resolve(part.Operands[1]); Require(destination, "DINT", true); destinations.Add(destination.Root);
            }
            else throw Unsupported("Only nonstateful supported predicates and DINT MOVs may precede the final OTE. Calls, jumps, other outputs and unknown effects are rejected: " + part.Instruction);
        }
        if (destinations.Overlaps(predicateRoots)) throw Unsupported("A MOV destination (including aliases or the same structure/array root) is read by a predicate.");
        var output = Resolve(parts[^1].Operands[0]); Require(output, "BOOL", true);
        if (predicateRoots.Contains(output.Root) || destinations.Contains(output.Root)) throw Unsupported("The final OTE shares storage with a predicate or MOV destination.");
        var count = all.Count(p => p.Instruction.Equals("MOV", StringComparison.OrdinalIgnoreCase));
        if (count is < 1 or > 32) throw Unsupported("Select a selector containing 1..32 DINT MOV instructions.");
        var tags = Enumerable.Range(1, count).Select(i => new SelectorRouteTag(request.RouteTagPrefix + "_" + i.ToString("D2", CultureInfo.InvariantCulture), programName, "BOOL")).ToArray();
        foreach (var tag in tags)
            if (context.Tag(tag.Name, programName) is not null || context.Tag(tag.Name, null) is not null) throw Unsupported("Proposed route tag already exists: " + tag.Name);
        var rungs = new List<SelectorPreviewRung>(); var writes = new List<SelectorWrite>();
        static string Instruction(LadderPreview.Part part) => part.Instruction + "(" + string.Join(",", part.Operands) + ")";
        static string Condition(List<List<string>> paths) => paths.Count == 1 ? string.Concat(paths[0]) : "[" + string.Join(",", paths.Select(p => p.Count == 0 ? "NOP()" : string.Concat(p))) + "]";
        void Add(string kind, int? order, string code)
        {
            // Each proposed rung must independently satisfy the same parser/resource/native-depth contracts.
            _ = LadderPreview.ParseLogic(code); var depth = LadderPreflight.ValidateNativeDepth(code);
            rungs.Add(new(kind, order, code, depth));
        }
        List<List<string>> Walk(List<LadderPreview.Part> sequence, List<List<string>> incoming)
        {
            var paths = incoming.Select(p => p.ToList()).ToList();
            foreach (var part in sequence)
            {
                if (part.Legs is { } legs) paths = legs.SelectMany(leg => Walk(leg, paths)).ToList();
                else if (LadderPreflight.Predicate(part)) foreach (var path in paths) path.Add(Instruction(part));
                else
                {
                    var index = writes.Count; var tag = tags[index].Name;
                    Add("route-summary", index + 1, Condition(paths) + "OTE(" + tag + ");");
                    Add("original-mov", index + 1, "XIC(" + tag + ")" + Instruction(part) + ";");
                    writes.Add(new(index + 1, part.Operands[0], part.Operands[1], tag));
                    paths = [["XIC(" + tag + ")"]];
                }
                if (paths.Count > MaximumPaths || paths.Any(p => p.Count > 128)) throw Unsupported("Selector expansion exceeds the 128-path/128-predicate budget; no partial proposal was produced.");
            }
            return paths;
        }
        var finalPaths = Walk(parts[..^1], [[]]);
        if (finalPaths.Any(p => !p.Any(s => tags.Any(t => s == "XIC(" + t.Name + ")"))))
            throw Unsupported("A final setup path bypasses all MOV route summaries; this selector form is unsupported.");
        Add("final-setup", null, Condition(finalPaths) + Instruction(parts[^1]) + ";");
        string[] limitations = [
            "Preview only: proposed program BOOL tags and replacement rungs are returned; no tags, project revisions, artifacts or native jobs are created.",
            "Original MOV operands and depth-first write order are retained, including later-write priority. Predicates after a MOV do not gate that earlier write. All summaries use current OTE results, never latches.",
            "Calls/jumps/stateful or unknown instructions, destination-dependent predicates, dynamic addressing, non-DINT MOVs, safety programs and AOI bodies are unsupported.",
            "Root-level alias/storage checks conservatively reject possible shared-structure/array dependencies. External writers, asynchronous changes, scheduling and helper-tag exclusivity outside this saved project are not proven.",
            "Boolean continuity under stable synthetic inputs is not native compilation, scan timing/order equivalence, motion, safety or runtime acceptance. Any later application requires separate source-bound review and native readback." ];
        var report = new SelectorRefactorReport(1, WorkspaceIdentity.Version, new(project.Id, project.Name, project.Revision, project.CurrentSha256), request.RoutineXPath, request.RungIndex,
            text, ProjectService.Hash(Encoding.UTF8.GetBytes(text)), LadderPreflight.NativeProfile, LadderBranchGrammar.MeasureDepth(text), tags, writes.ToArray(), rungs.ToArray(), "", limitations);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, ProjectService.Json);
        if (bytes.Length > 2 * 1024 * 1024) throw Unsupported("Selector proposal exceeds 2 MiB; no partial proposal was produced.");
        return report with { ReviewSha256 = ProjectService.Hash(bytes) };
    }
}

public sealed partial class ProjectService
{
    public SelectorRefactorReport PreviewSelectorRefactor(string id, SelectorRefactorRequest request)
    {
        var project = Get(id); Expect(project, request.ExpectedSha256);
        if (project.Kind != "logix") throw new OperationException("not_logix", "Select a Logix snapshot.");
        return SelectorRefactor.Preview(project, XmlProjects.Parse(File.ReadAllBytes(RevisionFile(project.Id, project.Revision))), request);
    }
}
