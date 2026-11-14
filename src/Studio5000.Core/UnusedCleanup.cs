using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record UnusedCleanupSelection(string Kind, string Name, string? ProgramName = null);
public sealed record UnusedCleanupKeep(UnusedCleanupSelection Selection, string Category, string Reason);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UnusedCleanupRequest(string ProjectId, string ExpectedSha256, int ExpectedRevision,
    UnusedCleanupSelection[] Candidates, UnusedCleanupKeep[]? Retain = null);
public sealed record UnusedCleanupDeclaration(string Key, UnusedCleanupSelection Selection, string XPath, string ElementSha256);
public sealed record UnusedCleanupRetention(UnusedCleanupDeclaration Declaration, string Reason, string SourceXPath, string? RequiredBy = null);
public sealed record UnusedCleanupDependency(string From, string To, string Kind, string SourceXPath);
public sealed record UnusedCleanupPreservation(int RetainedDeclarations, string RetainedDeclarationSetSha256, bool UnselectedBytesPreserved);
public sealed record UnusedCleanupPreview(UnusedCleanupRequest Request, ComparisonSnapshot Source, bool CanApply,
    string? ResultSha256, string ReviewSha256, UnusedCleanupDeclaration[] Removals, UnusedCleanupRetention[] Retentions,
    UnusedCleanupDependency[] Dependencies, string[] Diagnostics, bool Partial, UnusedCleanupPreservation Preservation,
    string Boundary, Artifact? PreviewReceipt = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record UnusedCleanupApplyRequest(UnusedCleanupRequest Preview, string ExpectedReviewSha256,
    string PreviewReceiptId, string PreviewReceiptSha256);
public sealed record UnusedCleanupResult(ProjectInfo Project, Artifact Candidate, Artifact Receipt, string ReviewSha256,
    bool ReviewOnly = true, string NativeAcceptance = "not-performed");
public sealed record UnusedCleanupStatus(Artifact PreviewReceipt, UnusedCleanupPreview Preview, string Status, UnusedCleanupResult? Result);
internal sealed record UnusedCleanupReceipt(string Kind, int SchemaVersion, UnusedCleanupPreview Preview);
internal sealed record UnusedCleanupApplyState(string Status, string ReviewSha256, UnusedCleanupResult? Result);

internal static class UnusedCleanup
{
    internal const string Boundary = "Offline REVIEW ONLY removal of explicitly selected whole controller/program tags, UDTs and unprotected AOI definitions from a full saved controller export. Dependency closure retains declarations needed by kept logic, aliases, declarations and configuration. ExternalAccess other than explicit None, Produced/Consumed, I/O/system, MESSAGE, safety, force and protected objects are retained. Explicit HMI/external retain decisions are hash-bound review inputs; HMI sources, external clients and runtime binding are not discovered or proven absent. Unknown source/opaque content conservatively retains possible dependencies. No native jobs, controller connection or deployment acceptance. Every unselected byte and retained declaration/value is preserved.";
    private const string Root = "retained-source";
    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    internal static string Key(UnusedCleanupSelection s) => s.Kind + ":" + (s.ProgramName is null ? "controller/" : "program/" + s.ProgramName + "/") + s.Name;
    private static string Name(XElement e) => (string?)e.Attribute("Name") ?? "";
    private static string? Program(XElement e) => (string?)e.Ancestors("Program").FirstOrDefault()?.Attribute("Name");
    private static IEnumerable<(UnusedCleanupSelection Selection, XElement Node)> Declarations(XDocument doc)
    {
        var controller = doc.Root!.Element("Controller")!;
        foreach (var tag in controller.Elements("Tags").Elements("Tag").Concat(controller.Elements("Programs").Elements("Program").Elements("Tags").Elements("Tag")))
            yield return (new("tag", Name(tag), Program(tag)), tag);
        foreach (var type in controller.Elements("DataTypes").Elements("DataType")) yield return (new("udt", Name(type)), type);
        foreach (var aoi in controller.Elements("AddOnInstructionDefinitions").Elements())
            if (aoi.Name == "AddOnInstructionDefinition" || (string?)aoi.Attribute("EncodedType") == "AddOnInstructionDefinition") yield return (new("aoi", Name(aoi)), aoi);
    }
    private static string Commitment(Dictionary<string, XElement> declarations, XmlSourceDocument source, HashSet<string> removed) =>
        ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(declarations.Where(p => !removed.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new { key = p.Key, sha256 = source.Fingerprint(p.Value) }).ToArray(), ProjectService.Json));

    internal static (UnusedCleanupPreview Preview, byte[] Bytes) Build(ProjectInfo project, byte[] bytes, UnusedCleanupRequest request, CancellationToken ct)
    {
        var selections = request.Candidates;
        var keeps = request.Retain ?? [];
        if (selections is null || selections.Length is < 1 or > 200 || keeps.Length > 200 ||
            selections.Any(s => s is null || s.Kind is not ("tag" or "udt" or "aoi") || s.Kind != "tag" && s.ProgramName is not null) ||
            selections.Select(Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selections.Length ||
            keeps.Any(k => k is null || k.Selection is null || k.Category is not ("hmi" or "external" or "user") || string.IsNullOrWhiteSpace(k.Reason) || k.Reason.Length > 500))
            throw new OperationException("unused_cleanup_policy", "Select 1..200 distinct tag/udt/aoi declarations; only tags have programName. Use up to 200 explicit hmi/external/user retention decisions with reasons.");
        foreach (var s in selections) { WatchValidation.Identifier(s.Name); if (s.ProgramName is not null) WatchValidation.Identifier(s.ProgramName); }
        var doc = ModuleConnectionEditing.Parse(bytes); var source = new XmlSourceDocument(bytes, doc);
        var controller = doc.Root!.Element("Controller")!;
        if ((string?)doc.Root.Attribute("TargetType") != "Controller" || !Same((string?)doc.Root.Attribute("ContainsContext"), "false"))
            throw new OperationException("unused_cleanup_scope", "A full controller export with TargetType=Controller and ContainsContext=false is required; partial exports cannot establish dependency closure.");
        var all = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var identities = new Dictionary<XElement, string>();
        foreach (var (identity, node) in Declarations(doc))
        {
            if (all.Count >= 100000 || !all.TryAdd(Key(identity), node)) throw new OperationException("unused_cleanup_limit", "At most 100000 uniquely named top-level tag/type/AOI declarations can be indexed.");
            identities.Add(node, Key(identity));
        }
        var nodes = selections.ToDictionary(Key, s => all.GetValueOrDefault(Key(s)) ?? throw new OperationException("unused_cleanup_missing", "A selected exact declaration is absent: " + Key(s)), StringComparer.OrdinalIgnoreCase);
        var declarations = selections.ToDictionary(Key, s => new UnusedCleanupDeclaration(Key(s), s, XmlProjects.PathOf(nodes[Key(s)]), source.Fingerprint(nodes[Key(s)])), StringComparer.OrdinalIgnoreCase);
        var byName = selections.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(Key).ToArray(), StringComparer.OrdinalIgnoreCase);
        var selectedTypes = selections.Where(s => s.Kind is "udt" or "aoi").GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(Key).ToArray(), StringComparer.OrdinalIgnoreCase);
        var selectedAois = selections.Where(s => s.Kind == "aoi").ToDictionary(s => s.Name, Key, StringComparer.OrdinalIgnoreCase);
        var tagKeys = selections.Where(s => s.Kind == "tag").Select(Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var programs = controller.Elements("Programs").Elements("Program").ToDictionary(Name, p => p.Elements("Tags").Elements("Tag").Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var edges = new Dictionary<(string From, string To), UnusedCleanupDependency>();
        var retained = new Dictionary<string, UnusedCleanupRetention>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<string>(); var partial = false; long characters = 0;
        string Owner(XElement element) => element.AncestorsAndSelf().Select(e => identities.GetValueOrDefault(e)).FirstOrDefault(k => k is not null) is { } key && nodes.ContainsKey(key) ? key : Root;
        void Edge(string owner, string target, string kind, Func<string> location)
        {
            owner = owner == Root ? Root : declarations[owner].Key; target = declarations[target].Key;
            if (edges.ContainsKey((owner, target))) return;
            if (edges.Count >= 10000) throw new OperationException("unused_cleanup_limit", "Selected dependency graph exceeds 10000 edges; narrow the candidate set.");
            edges.Add((owner, target), new(owner, target, kind, location()));
        }
        void Keep(string key, string reason, string location, string? requiredBy = null)
        { if (!retained.ContainsKey(key)) retained.Add(key, new(declarations[key], reason, location, requiredBy)); }
        void Unknown(string owner, string location, string reason)
        {
            partial = true;
            if (diagnostics.Count < 50) diagnostics.Add(reason + ": " + location);
            foreach (var key in nodes.Keys) Edge(owner, key, reason, () => location);
        }
        void Mentions(string text, string owner, Func<string> location, string reason)
        {
            foreach (var word in Regex.EnumerateMatches(text.AsSpan(), @"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
                if (byName.TryGetValue(text.Substring(word.Index, word.Length), out var keys)) foreach (var key in keys) Edge(owner, key, reason, location);
        }
        foreach (var keep in keeps)
        {
            if (!declarations.TryGetValue(Key(keep.Selection), out var declaration)) throw new OperationException("unused_cleanup_policy", "A retention must identify an exact selected candidate.");
            Keep(declaration.Key, keep.Category + ": " + keep.Reason, declaration.XPath);
        }
        foreach (var (key, node) in nodes)
        {
            var selection = declarations[key].Selection; string? reason = null;
            if (selection.Kind == "tag")
            {
                var tagType = (string?)node.Attribute("TagType");
                if (!Same((string?)node.Attribute("ExternalAccess"), "None")) reason = "external-access: not explicitly None";
                if (!Same(tagType, "Base") && !Same(tagType, "Alias")) reason = "produced-consumed-or-unknown-tag-type";
                if (node.Elements().Any(e => e.Name.LocalName is "ProduceInfo" or "ConsumeInfo")) reason = "produced-consumed-configuration";
                if (Name(node).Contains(':') || node.Attribute("Module") is not null || Same((string?)node.Attribute("DataType"), "MODULE") || Same((string?)node.Attribute("Class"), "System")) reason = "io-or-system-tag";
                if (Same((string?)node.Attribute("DataType"), "MESSAGE")) reason = "message-configuration";
            }
            if (LogixPreservation.Safety(node) || node.DescendantsAndSelf().Any(e => e.Name.LocalName is "ForceData" or "EncodedData" || e.Attributes().Any(a =>
                a.Name.LocalName.Contains("Signature", StringComparison.OrdinalIgnoreCase) || a.Name.LocalName.Contains("Encrypted", StringComparison.OrdinalIgnoreCase) || a.Name.LocalName.Contains("Protected", StringComparison.OrdinalIgnoreCase) || a.Name.LocalName.Contains("SourceKey", StringComparison.OrdinalIgnoreCase)))) reason = "safety-force-signed-or-protected";
            if (Same((string?)node.Attribute("Use"), "Context")) reason = "context-declaration";
            if (reason is not null) Keep(key, reason, declarations[key].XPath);
        }
        foreach (var attribute in controller.DescendantsAndSelf().Attributes("DataType"))
        {
            ct.ThrowIfCancellationRequested();
            if (selectedTypes.TryGetValue(attribute.Value, out var targets)) foreach (var key in targets)
                Edge(Owner(attribute.Parent!), key, "declared-data-type", () => XmlProjects.PathOf(attribute.Parent!) + "/@DataType");
        }
        void Scan(string text, XElement location, string? program, string owner, HashSet<string>? aoiLocals = null)
        {
            ct.ThrowIfCancellationRequested();
            if (text.Length > 1024 * 1024 || (characters += text.Length) > 16 * 1024 * 1024) throw new OperationException("unused_cleanup_limit", "Reference scanning supports 1 MiB per unit/routine and 16 MiB total source text.");
            var issues = false;
            foreach (var token in TagReferences.Lex(text, _ => issues = true))
            {
                if (token.Program is null && aoiLocals?.Contains(token.Name) == true) continue;
                var binding = token.Program ?? (program is not null && programs[program].Contains(token.Name) ? program : null);
                var key = Key(new("tag", token.Name, binding));
                if (tagKeys.Contains(key)) Edge(owner, key, "lexical-tag-or-alias", () => XmlProjects.PathOf(location));
            }
            var masked = new string(TagReferences.MaskSource(text, _ => issues = true));
            foreach (Match call in Regex.Matches(masked, @"(?<![A-Za-z0-9_])([A-Za-z_][A-Za-z0-9_]*)\s*\(", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2)))
                if (selectedAois.TryGetValue(call.Groups[1].Value, out var key)) Edge(owner, key, "aoi-call", () => XmlProjects.PathOf(location));
            if (issues)
            {
                partial = true;
                if (diagnostics.Count < 50) diagnostics.Add("lexical-source-unresolved: " + XmlProjects.PathOf(location));
                Mentions(text, owner, () => XmlProjects.PathOf(location), "unresolved-source-mention");
            }
        }
        foreach (var routine in controller.Descendants("Routine"))
        {
            var owner = Owner(routine); var program = Program(routine); var aoi = routine.Ancestors("AddOnInstructionDefinition").FirstOrDefault();
            var locals = aoi is null ? null : aoi.Elements("Parameters").Elements("Parameter").Concat(aoi.Elements("LocalTags").Elements("LocalTag")).Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            switch ((string?)routine.Attribute("Type"))
            {
                case "RLL":
                    var rungs = routine.Elements("RLLContent").Elements("Rung").ToArray();
                    if (routine.Element("RLLContent") is null) Unknown(owner, XmlProjects.PathOf(routine), "missing-routine-source");
                    foreach (var rung in rungs)
                        if (rung.Element("Text") is { } text) Scan(text.Value, text, program, owner, locals);
                        else Unknown(owner, XmlProjects.PathOf(rung), "missing-rung-source");
                    break;
                case "ST":
                    var lines = routine.Elements("STContent").Elements("Line").ToArray();
                    if (routine.Element("STContent") is null) Unknown(owner, XmlProjects.PathOf(routine), "missing-routine-source");
                    else Scan(string.Join('\n', lines.Select(l => l.Value)), routine, program, owner, locals);
                    break;
                default: Unknown(owner, XmlProjects.PathOf(routine), "unsupported-routine-source"); break;
            }
        }
        foreach (var alias in controller.Descendants().Where(e => e.Attribute("AliasFor") is not null))
        {
            var aoi = alias.Ancestors("AddOnInstructionDefinition").FirstOrDefault();
            var locals = aoi is null ? null : aoi.Elements("Parameters").Elements("Parameter").Concat(aoi.Elements("LocalTags").Elements("LocalTag")).Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Scan((string)alias.Attribute("AliasFor")!, alias, Program(alias), Owner(alias), locals);
        }
        foreach (var opaque in controller.Descendants("EncodedData")) Unknown(Owner(opaque), XmlProjects.PathOf(opaque), "opaque-source");
        foreach (var configuration in controller.Descendants().Where(e => e.Name.LocalName is "MessageParameters" or "ProduceInfo" or "ConsumeInfo" or "Dependency"))
        {
            foreach (var attribute in configuration.Attributes()) Mentions(attribute.Value, Owner(configuration), () => XmlProjects.PathOf(configuration) + "/@" + attribute.Name.LocalName, "native-configuration-mention");
            if (!configuration.HasElements) Mentions(configuration.Value, Owner(configuration), () => XmlProjects.PathOf(configuration), "native-configuration-mention");
        }
        foreach (var element in controller.DescendantsAndSelf())
        {
            if (element.AncestorsAndSelf().Any(e => identities.ContainsKey(e) || e.Name.LocalName is "Routine" or "Description" or "Comment" or "LocalizedDescription" or "LocalizedComment")) continue;
            foreach (var attribute in element.Attributes()) Mentions(attribute.Value, Root, () => XmlProjects.PathOf(element) + "/@" + attribute.Name.LocalName, "configuration-mention");
            if (!element.HasElements) Mentions(element.Value, Root, () => XmlProjects.PathOf(element), "configuration-mention");
        }
        var pending = new Queue<string>();
        foreach (var edge in edges.Values.Where(e => e.From == Root)) Keep(edge.To, edge.Kind, edge.SourceXPath, Root);
        foreach (var key in retained.Keys) pending.Enqueue(key);
        var outgoing = edges.Values.ToLookup(e => e.From, StringComparer.OrdinalIgnoreCase);
        while (pending.TryDequeue(out var key)) foreach (var edge in outgoing[key])
            if (!retained.ContainsKey(edge.To)) { Keep(edge.To, "required-by-retained-declaration", edge.SourceXPath, key); pending.Enqueue(edge.To); }
        var removals = declarations.Values.Where(d => !retained.ContainsKey(d.Key)).ToArray();
        var removed = removals.Select(d => d.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var before = Commitment(all, source, removed);
        var result = source.Apply(removals.Select(d => { var span = source.Locate(nodes[d.Key]); return new XmlSourceDocument.Patch(span.Start, span.End, ""); }));
        var readback = ModuleConnectionEditing.Parse(result); var readbackSource = new XmlSourceDocument(result, readback);
        var remaining = Declarations(readback).ToDictionary(p => Key(p.Selection), p => p.Node, StringComparer.OrdinalIgnoreCase);
        if (remaining.Count != all.Count - removed.Count || removed.Any(remaining.ContainsKey) || before != Commitment(remaining, readbackSource, []))
            throw new OperationException("unused_cleanup_preservation", "Removal readback differs from the exact retained declaration/value fingerprints.");
        var report = new UnusedCleanupPreview(request, new(project.Id, project.Name, project.Revision, project.CurrentSha256), removals.Length > 0,
            removals.Length > 0 ? ProjectService.Hash(result) : null, "", removals, retained.Values.ToArray(), edges.Values.ToArray(), diagnostics.ToArray(), partial,
            new(remaining.Count, before, true), Boundary);
        var hash = ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(report, ProjectService.Json));
        report = report with { ReviewSha256 = hash };
        if (JsonSerializer.SerializeToUtf8Bytes(report, ProjectService.Json).Length > 4 * 1024 * 1024) throw new OperationException("unused_cleanup_limit", "Review exceeds 4 MiB; narrow the candidate set.");
        return (report, result);
    }
}
