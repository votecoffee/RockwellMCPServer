using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record LogicTraceRequest(string ExpectedSha256, string TagName, string? ProgramName = null, string Direction = "both", int Depth = 3,
    string? MemberPath = null, int Offset = 0, int Limit = 100, int DependencyOffset = 0, int DependencyLimit = 100, string Role = "all",
    bool? IncludeRelatedUsages = null);
public sealed record LogicUsage(string Symbol, string Operand, string Role, string Instruction, string Owner, string RoutineXPath, string SourceXPath,
    int RungIndex, string[] AliasChain, LogicTraceRegion? Region = null, int? InstructionIndex = null);
public sealed record LogicDependency(string From, string To, string Kind, string SourceXPath,
    LogicTraceRegion? FromRegion = null, LogicTraceRegion? ToRegion = null, string? Mapping = null);
public sealed record LogicTraceReport(string ProjectId, string ProjectSha256, string Root, string[] Symbols, LogicUsage[] Usages,
    LogicDependency[] Dependencies, string[] Diagnostics, bool Limited, string Boundary,
    int TotalUsages = 0, int Offset = 0, int Limit = 100, int? NextOffset = null,
    int TotalDependencies = 0, int DependencyOffset = 0, int DependencyLimit = 100, int? NextDependencyOffset = null, bool Partial = false);
internal sealed record LogicAddress(string Symbol, string Tag, string? Program, string Member, string[] Aliases, string? Issue = null,
    XElement? Declaration = null);

internal sealed class LogicContext(XDocument document)
{
    private Dictionary<string, XElement>? controllerTags;
    private Dictionary<string, XElement>? programs;
    private readonly Dictionary<string, Dictionary<string, XElement>> localTags = new(StringComparer.OrdinalIgnoreCase);
    internal XDocument Document => document;
    internal XElement Controller => document.Root!.Element("Controller")!;
    internal static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal XElement? Tag(string name, string? program)
    {
        static Dictionary<string, XElement> Index(IEnumerable<XElement> nodes)
        {
            var index = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var node in nodes)
                if ((string?)node.Attribute("Name") is { } key) index.TryAdd(key, node);
            return index;
        }
        if (program is null)
            return (controllerTags ??= Index(Controller.Elements("Tags").Elements("Tag"))).GetValueOrDefault(name);
        if (!localTags.TryGetValue(program, out var tags))
        {
            programs ??= Index(Controller.Elements("Programs").Elements("Program"));
            var scope = programs.GetValueOrDefault(program);
            if (scope is null) return null;
            localTags.Add(program, tags = Index(scope.Elements("Tags").Elements("Tag")));
        }
        return tags.GetValueOrDefault(name);
    }
    internal LogicAddress? Resolve(string text, string? program, Dictionary<string, LogicAddress>? bindings = null, HashSet<string>? active = null)
    {
        var match = Regex.Match(text.Trim(), @"\A(?:(?:Program:)(?<program>[A-Za-z_][A-Za-z0-9_]*)\.)?(?<tag>[A-Za-z_][A-Za-z0-9_]*)(?<member>(?:\.[A-Za-z_0-9]+|\[[^\]]+\])*)\z", RegexOptions.IgnoreCase);
        if (!match.Success || text.Equals("true", StringComparison.OrdinalIgnoreCase) || text.Equals("false", StringComparison.OrdinalIgnoreCase)) return null;
        var name = match.Groups["tag"].Value; var suffix = match.Groups["member"].Value;
        if (!match.Groups["program"].Success && bindings?.TryGetValue(name, out var binding) == true) return binding with { Member = binding.Member + suffix };
        var scope = match.Groups["program"].Success ? match.Groups["program"].Value : program is not null && Tag(name, program) is not null ? program : null;
        var tag = Tag(name, scope); if (tag is null) return new((scope is null ? "Controller" : "Program:" + scope) + "." + name, name, scope, suffix, [], "declaration_missing");
        name = (string)tag.Attribute("Name")!; var symbol = (scope is null ? "Controller" : "Program:" + scope) + "." + name;
        active ??= new(StringComparer.OrdinalIgnoreCase);
        if (active.Count >= 32 || !active.Add(symbol)) return new(symbol, name, scope, suffix, [symbol], "alias_cycle_or_limit");
        if (tag.Attribute("AliasFor") is not { } alias) return new(symbol, name, scope, suffix, []);
        var resolved = Resolve(alias.Value, scope, bindings, active);
        return resolved is null ? new(symbol, name, scope, suffix, [symbol], "alias_target_unsupported") : resolved with { Member = resolved.Member + suffix, Aliases = new[] { symbol }.Concat(resolved.Aliases).ToArray() };
    }
}

public static class LogicTracing
{
    private const int MaximumUsages = 250_000;
    private const int MaximumDependencies = 500_000;
    private const int MaximumExpandedRungs = 100_000;
    public const string Boundary = "Conservative saved-project dependency analysis. Exact members/bits use declared layouts, aliases and known BTD/COP/CPS ranges. Unknown copy packing/lengths and dynamic or unsupported effects stay explicit possible effects. Source-bound usage/dependency pages describe possible reads and writes; scheduling, indirect dispatch, external writers, live values and native compiler binding require separate evidence.";
    internal static string[] Roles(string instruction, int count)
    {
        var name = instruction.ToUpperInvariant(); var roles = Enumerable.Repeat("read", count).ToArray();
        int[] writes = name switch
        {
            "XIC" or "XIO" or "EQU" or "NEQ" or "GRT" or "GEQ" or "LES" or "LEQ" or "LIM" or "MEQ" => [],
            "OTE" or "OTL" or "OTU" or "RES" or "CPT" => [0], "MOV" or "NOT" => [1],
            "ADD" or "SUB" or "MUL" or "DIV" or "MOD" or "AND" or "OR" or "XOR" => [2],
            "COP" or "CPS" or "FLL" => [1], "TON" or "TOF" or "RTO" or "CTU" or "CTD" => [0],
            "BTD" => [2],
            "ONS" => [0], "NOP" or "AFI" => [], _ => [-1]
        };
        if (writes.Contains(-1)) return Enumerable.Repeat("unknown", count).ToArray();
        foreach (var i in writes.Where(i => i < count)) roles[i] = name is "TON" or "TOF" or "RTO" or "CTU" or "CTD" or "ONS" ? "read-write" : "write";
        return roles;
    }
    public static LogicTraceReport Trace(ProjectInfo project, XDocument document, LogicTraceRequest request)
    {
        if (request.Depth is < 0 or > 8 || request.Direction is not ("both" or "upstream" or "downstream")) throw new OperationException("logic_trace_query", "Use depth 0..8 and direction both/upstream/downstream.");
        var (tagName, member) = LogicTraceRegions.Selector(request);
        if (request.ProgramName is not null) WatchValidation.Identifier(request.ProgramName);
        LogicTraceResults.Validate(request);
        ProjectComparison.ValidateSize(document, ProjectComparison.MaximumAnalysisElements); var context = new LogicContext(document);
        if (context.Tag(tagName, request.ProgramName) is null) throw new OperationException("logic_trace_tag", "Select an existing exact tag declaration.");
        var root = context.Resolve((request.ProgramName is null ? "" : "Program:" + request.ProgramName + ".") + tagName + member, null)!;
        var regions = new LogicTraceRegions(context);
        var rootRegion = regions.Resolve(root);
        var precise = member.Length > 0 || root.Member.Length > 0;
        if (precise && rootRegion.Issue is not null)
            throw new OperationException("logic_trace_selector", "The exact member/bit selector cannot be resolved: " + rootRegion.Issue);
        var usages = new List<LogicUsage>(); var edges = new List<LogicDependency>(); var issues = new HashSet<string>(StringComparer.Ordinal); var limited = false; var calls = 0; var sourceCharacters = 0;
        void Issue(string message)
        {
            if (message.Length > 2048) { message = message[..2048] + "…"; limited = true; }
            if (issues.Count < 100) issues.Add(message); else limited = true;
        }
        void Edge(LogicDependency edge) { if (edges.Count >= MaximumDependencies) throw new OperationException("logic_trace_limit", "Trace exceeds 500000 dependencies; use a smaller saved snapshot."); edges.Add(edge); }
        if (root.Issue is not null) Issue(root.Issue + ": " + root.Symbol);
        IEnumerable<LadderPreview.Part> Flatten(IEnumerable<LadderPreview.Part> parts) => parts.SelectMany(p => p.Legs is null ? new[] { p } : p.Legs.SelectMany(Flatten));
        var definitions = context.Controller.Elements("AddOnInstructionDefinitions").Elements("AddOnInstructionDefinition").ToDictionary(a => (string)a.Attribute("Name")!, StringComparer.OrdinalIgnoreCase);
        void Routine(XElement routine, string? program, string owner, Dictionary<string, LogicAddress>? bindings, HashSet<string> active)
        {
            var routinePath = XmlProjects.PathOf(routine);
            var ownerPath = owner + "/" + (string?)routine.Attribute("Name");
            if ((string?)routine.Attribute("Type") == "ST")
            {
                Issue("st_partial_effects: simple leading assignments classified; compound control flow/calls remain unknown: " + routinePath);
                var hasComments = routine.Value.Contains("(*", StringComparison.Ordinal);
                foreach (var line in routine.Elements("STContent").Elements("Line"))
                {
                    var text = line.Value; if (text.Length > 1024 * 1024 || (sourceCharacters += text.Length) > 16 * 1024 * 1024) throw new OperationException("logic_trace_limit", "ST source exceeds the trace text budget.");
                    var assignment = Regex.Match(text, @"\A\s*(?<left>(?:Program:[A-Za-z_][A-Za-z0-9_]*\.)?[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_0-9]+|\[\d+\])*)\s*:=", RegexOptions.IgnoreCase);
                    // Classify only a single leading assignment. Calls and compound statements may write other operands.
                    var simple = !hasComments && assignment.Success && text.IndexOf(":=", assignment.Index + assignment.Length, StringComparison.Ordinal) < 0 &&
                        !Regex.IsMatch(text[assignment.Length..], @"[();]|\b(?:IF|THEN|ELSE|FOR|WHILE|REPEAT|CASE|RETURN)\b", RegexOptions.IgnoreCase);
                    // A single trailing semicolon is permitted, but not a second statement.
                    if (!simple && !hasComments && assignment.Success && text.TrimEnd().EndsWith(';'))
                    {
                        var body = text.TrimEnd()[..^1];
                        simple = body.IndexOf(":=", assignment.Length, StringComparison.Ordinal) < 0 && !Regex.IsMatch(body[assignment.Length..], @"[();]|\b(?:IF|THEN|ELSE|FOR|WHILE|REPEAT|CASE|RETURN)\b", RegexOptions.IgnoreCase);
                    }
                    var local = new List<LogicUsage>(); var path = XmlProjects.PathOf(line);
                    foreach (var token in TagReferences.Lex(text, Issue))
                    {
                        var operand = text.Substring(token.Start, token.Length); var address = context.Resolve(operand, program, bindings); if (address is null) continue;
                        var role = simple ? token.Start == assignment.Groups["left"].Index ? "write" : "read" : "unknown";
                        if (address.Issue is not null) Issue(address.Issue + ": " + operand);
                        local.Add(new(address.Symbol, operand, role, "ST", ownerPath, routinePath, path, -1, address.Aliases, regions.Resolve(address)));
                        if (local.Count > 10000) throw new OperationException("logic_trace_limit", "ST line exceeds the trace operand budget.");
                    }
                    usages.AddRange(local);
                    foreach (var read in local.Where(u => u.Role == "read")) foreach (var write in local.Where(u => u.Role == "write"))
                        Edge(new(read.Symbol, write.Symbol, "possible-statement-dependency", path, read.Region, write.Region, "value-dependency"));
                    if (usages.Count > MaximumUsages) throw new OperationException("logic_trace_limit", "ST trace exceeds its usage budget.");
                }
                return;
            }
            if ((string?)routine.Attribute("Type") != "RLL") { Issue("unsupported_routine_language: " + routinePath); return; }
            var index = 0; var rungOrdinal = 0; var contentPath = "";
            XElement? previousContent = null;
            foreach (var rung in routine.Elements("RLLContent").Elements("Rung"))
            {
                if (rung.Parent != previousContent)
                {
                    previousContent = rung.Parent!;
                    contentPath = routinePath + $"/RLLContent[{previousContent.ElementsBeforeSelf("RLLContent").Count() + 1}]";
                    rungOrdinal = 0;
                }
                var path = contentPath + $"/Rung[{++rungOrdinal}]"; var text = (string?)rung.Element("Text") ?? "";
                if ((sourceCharacters += text.Length) > 16 * 1024 * 1024 || ++calls > MaximumExpandedRungs) throw new OperationException("logic_trace_limit", "Trace exceeds 16 MiB source or 100000 expanded rungs.");
                LadderPreview.Part[] parts;
                try { parts = Flatten(LadderPreview.ParseLogic(text)).ToArray(); }
                catch (OperationException ex) { Issue(ex.Code + ": " + path); index++; continue; }
                var local = new List<LogicUsage>();
                var instructionIndex = 0;
                foreach (var part in parts)
                {
                    instructionIndex++;
                    var roles = Roles(part.Instruction, part.Operands.Length);
                    if (part.Instruction.Equals("JSR", StringComparison.OrdinalIgnoreCase)) { Issue("conditional_routine_call: " + path); continue; }
                    if (definitions.TryGetValue(part.Instruction, out var aoi))
                    {
                        var parameters = aoi.Elements("Parameters").Elements("Parameter").Where(p => (string?)p.Attribute("Name") is not ("EnableIn" or "EnableOut")).ToArray();
                        var visible = parameters.Where(p => (string?)p.Attribute("Required") == "true" || (string?)p.Attribute("Visible") == "true" || (string?)p.Attribute("Usage") == "InOut").ToArray();
                        var required = parameters.Where(p => (string?)p.Attribute("Required") == "true" || (string?)p.Attribute("Usage") == "InOut").ToArray();
                        var layout = part.Operands.Length == visible.Length + 1 ? visible : part.Operands.Length == required.Length + 1 ? required : null;
                        var instance = part.Operands.Length > 0 ? context.Resolve(part.Operands[0], program, bindings) : null;
                        if (layout is null || instance is null || instance.Issue is not null || active.Count >= 8 || !active.Add(part.Instruction))
                        { Issue("aoi_binding_or_recursion_unknown: " + part.Instruction + ": " + path); roles = Enumerable.Repeat("unknown", part.Operands.Length).ToArray(); }
                        else
                        {
                            roles = new[] { "read-write" }.Concat(layout.Select(p => (string?)p.Attribute("Usage") switch { "Input" => "read", "Output" => "write", "InOut" => "read-write", _ => "unknown" })).ToArray();
                            var map = new Dictionary<string, LogicAddress>(StringComparer.OrdinalIgnoreCase);
                            foreach (var tag in aoi.Elements("LocalTags").Elements("LocalTag").Concat(parameters))
                            {
                                var name = (string)tag.Attribute("Name")!;
                                map[name] = instance with { Symbol = instance.Symbol + instance.Member + "." + name, Member = "", Aliases = [], Declaration = tag };
                            }
                            foreach (var p in parameters.Where(p => p.Attribute("AliasFor") is not null))
                            {
                                var target = p.Attribute("AliasFor")!.Value.Split('.', 2);
                                if (map.TryGetValue(target[0], out var resolved)) map[(string)p.Attribute("Name")!] = resolved with { Member = target.Length > 1 ? "." + target[1] : "" };
                            }
                            for (var i = 0; i < layout.Length; i++)
                            {
                                var name = (string)layout[i].Attribute("Name")!; var actual = context.Resolve(part.Operands[i + 1], program, bindings);
                                if (actual is null) continue;
                                if ((string?)layout[i].Attribute("Usage") == "InOut") map[name] = actual;
                                else
                                {
                                    var parameter = map[name]; var usage = (string?)layout[i].Attribute("Usage");
                                    Edge(usage == "Output"
                                        ? new(parameter.Symbol, actual.Symbol, "aoi-output", path, regions.Resolve(parameter), regions.Resolve(actual), "value-dependency")
                                        : new(actual.Symbol, parameter.Symbol, "aoi-input", path, regions.Resolve(actual), regions.Resolve(parameter), "value-dependency"));
                                }
                            }
                            foreach (var body in aoi.Elements("Routines").Elements("Routine")) Routine(body, program, owner + "/AOI:" + part.Operands[0] + ":" + part.Instruction, map, active);
                            active.Remove(part.Instruction);
                        }
                    }
                    else if (roles.Any(r => r == "unknown")) Issue("instruction_effect_unknown: " + part.Instruction + ": " + path);
                    var destination = part.Operands.Length > 1 ? context.Resolve(part.Operands[1], program, bindings) : null;
                    for (var i = 0; i < part.Operands.Length; i++)
                    {
                        var address = context.Resolve(part.Operands[i], program, bindings);
                        if (address is null) { if (part.Operands[i].Any(char.IsAsciiLetter) && !bool.TryParse(part.Operands[i], out _) && !decimal.TryParse(part.Operands[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _)) Issue("operand_expression_not_resolved: " + part.Operands[i]); continue; }
                        var role = roles[i];
                        if (address.Issue is not null) Issue(address.Issue + ": " + part.Operands[i]);
                        if (Regex.IsMatch(address.Member, @"\[[^\]]*[A-Za-z_][^\]]*\]")) { role = "unknown"; Issue("dynamic_address_unknown: " + part.Operands[i]); }
                        var region = LogicTraceRegions.Effect(regions.Resolve(address), part, i, destination is null ? null : regions.Resolve(destination));
                        if (region.Issue is not null) Issue(region.Issue + ": " + path + ": " + part.Instruction);
                        local.Add(new(address.Symbol, part.Operands[i], role, part.Instruction, ownerPath, routinePath, path, index, address.Aliases, region, instructionIndex));
                    }
                }
                usages.AddRange(local);
                foreach (var read in local.Where(u => u.Role is "read" or "read-write"))
                foreach (var write in local.Where(u => u.Role is "write" or "read-write"))
                {
                    var sameInstruction = read.InstructionIndex == write.InstructionIndex;
                    var bitwise = sameInstruction && (read.Instruction.ToUpperInvariant() is "BTD" or "COP" or "CPS") &&
                        read.Region?.Issue is null && write.Region?.Issue is null;
                    var guard = read.Instruction.ToUpperInvariant() is "XIC" or "XIO" or "EQU" or "NEQ" or "GRT" or "GEQ" or "LES" or "LEQ" or "LIM" or "MEQ";
                    Edge(new(read.Symbol, write.Symbol, "possible-rung-dependency", path, read.Region, write.Region,
                        bitwise ? "bitwise" : sameInstruction ? "value-dependency" : guard ? "guard" : "aggregate-rung"));
                }
                if (usages.Count > MaximumUsages) throw new OperationException("logic_trace_limit", "Trace exceeds its usage budget.");
                index++;
            }
            if (index == 0) Issue("routine_empty_or_source_unavailable: " + routinePath);
        }
        foreach (var program in context.Controller.Elements("Programs").Elements("Program"))
        foreach (var routine in program.Elements("Routines").Elements("Routine")) Routine(routine, (string?)program.Attribute("Name"), "Program:" + (string?)program.Attribute("Name"), null, new(StringComparer.OrdinalIgnoreCase));
        return LogicTraceResults.Build(project, request, rootRegion, precise, usages, edges, issues.ToArray(), limited);
    }
}

public sealed partial class ProjectService
{
    public LogicTraceReport TraceLogic(string id, LogicTraceRequest request)
    {
        var (project, bytes) = ReadBoundedLogixRevision(id, request.ExpectedSha256);
        return LogicTracing.Trace(project, XmlProjects.ParseForAnalysis(bytes), request);
    }
}
