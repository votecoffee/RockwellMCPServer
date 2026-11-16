using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace Studio5000.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AoiDependencyPlanRequest(MergeSnapshot Source, MergeSnapshot Destination, string[] Roots,
    string[] AoiAdditions, string[] DataTypeAdditions);
public sealed record AoiDependencyDefinition(string Kind, string Name, string Operation, string RawSha256, string[] Dependencies);
public sealed record AoiDependencyPlan(ComparisonSnapshot Source, ComparisonSnapshot Destination, bool CanPlan,
    AoiDependencyDefinition[] Definitions, StructuralReferenceAddition[] ReferenceAdditions, StructuralEdit[] Edits,
    string[] DataTypeScope, StructuralFinding[] Findings, string ReviewSha256, string Boundary, Artifact? PlanReceipt = null);

internal static class AoiDependencyPlanning
{
    internal const string Boundary = "Offline standard readable RLL AOI/UDT dependency additions only. Explicit new identities and source hashes; existing libraries are never replaced. Visible declared types and bounded RLL calls are checked; opaque, signed, safety, ST/FBD/SFC and unknown calls are refused. Native import/build/runtime acceptance is not performed. Combine returned references with an independently reviewed structural scope; safety overlays remain separate explicit authorizations.";
    // Deliberately bounded: unknown vendor instructions require another supported review path.
    private static readonly HashSet<string> Instructions = new(("XIC XIO OTE OTL OTU ONS OSR OSF " +
        "MOV MOVE COP CPS FLL CLR ADD SUB MUL DIV MOD NEG ABS SQRT CPT AND OR XOR NOT BTD SWPB " +
        "EQU NEQ LES LEQ GRT GEQ LIM MEQ CMP TON TOF RTO RES CTU CTD NOP AFI TND RET SBR JSR JMP LBL " +
        "MCR FAL FSC SIZE GSV SSV MSG FFL FFU LFL LFU SRT DTR TRN FRD TOD BSL BSR SQI SQO SQF").Split(' '), StringComparer.OrdinalIgnoreCase);
    internal static string Contract(XElement definition) => LogixPreservation.Fingerprint(definition)!;
    internal static (string[] Types, string[] Calls) Dependencies(XElement node, HashSet<string> sourceAois, ref int textBudget)
    {
        var calls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (node.Elements("Routines").Count() != 1 || !node.Elements("Routines").Elements("Routine").Any() ||
            node.Elements("Parameters").Elements("Parameter").Concat(node.Elements("LocalTags").Elements("LocalTag")).Any(p => string.IsNullOrWhiteSpace((string?)p.Attribute("DataType"))))
            throw new OperationException("aoi_dependency_unsupported", "Require readable AOI routines and explicit datatypes for every parameter/local tag.");
        foreach (var routine in node.Elements("Routines").Elements("Routine"))
        {
            if ((string?)routine.Attribute("Type") != "RLL" || routine.Elements().Any(e => e.Name.LocalName.Contains("Encoded", StringComparison.OrdinalIgnoreCase)))
                throw new OperationException("aoi_dependency_unsupported", "Only fully readable RLL AOI routines are supported by this dependency planner.");
            foreach (var rung in routine.Elements("RLLContent").Elements("Rung"))
            {
                if (rung.Attribute("OnlineEditType") is { Value: not "" and not "None" }) throw new OperationException("aoi_dependency_unsupported", "AOI routines with online-edit variants are unsupported.");
                var texts = rung.Elements("Text").ToArray();
                if (texts.Length != 1 || texts[0].HasElements) throw new OperationException("aoi_dependency_unsupported", "Each AOI rung requires one readable scalar Text.");
                if ((textBudget += texts[0].Value.Length) > 16 * 1024 * 1024) throw new OperationException("aoi_dependency_limit", "AOI dependency source exceeds 16 MiB of logic.");
                void Visit(IEnumerable<LadderPreview.Part> parts)
                {
                    foreach (var part in parts)
                        if (part.Legs is { } legs) foreach (var leg in legs) Visit(leg);
                        else if (sourceAois.Contains(part.Instruction)) calls.Add(part.Instruction);
                        else if (!Instructions.Contains(part.Instruction)) throw new OperationException("aoi_dependency_unsupported", "Unresolved AOI or unsupported native instruction: " + part.Instruction);
                }
                Visit(LadderPreview.ParseLogic(texts[0].Value));
            }
        }
        return (node.DescendantsAndSelf().Attributes("DataType").Select(a => a.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), calls.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}

public sealed partial class ProjectService
{
    public async Task<AoiDependencyPlan> PlanAoiDependenciesAsync(AoiDependencyPlanRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var source = PreservationSource(request.Source); var destination = PreservationSource(request.Destination);
            var from = ModuleConnectionEditing.Parse(source.Bytes); var to = ModuleConnectionEditing.Parse(destination.Bytes);
            var lexical = new XmlSourceDocument(source.Bytes, from); var target = new XmlSourceDocument(destination.Bytes, to);
            var controller = from.Root!.Element("Controller")!; var fresh = to.Root!.Element("Controller")!;
            var findings = new List<StructuralFinding>(); var definitions = new List<AoiDependencyDefinition>();
            void Block(string name, string detail) => findings.Add(new("blocked", "aoi-dependency", name, detail));
            foreach (var doc in new[] { from, to })
                if ((string?)doc.Root!.Attribute("TargetType") != "Controller" || (string?)doc.Root.Attribute("ContainsContext") != "false")
                    throw new OperationException("aoi_dependency_source", "Use full saved controller exports with ContainsContext=false.");
            if ((string?)controller.Attribute("Name") != (string?)fresh.Attribute("Name") || (string?)from.Root.Attribute("SoftwareRevision") != (string?)to.Root.Attribute("SoftwareRevision"))
                throw new OperationException("aoi_dependency_source", "Pin already-retargeted same-controller, same-version exports; no identity/version conversion is inferred.");
            HashSet<string> Names(string[]? names, int max)
            {
                if (names is null || names.Length > max) throw new OperationException("aoi_dependency_policy", "Use explicit bounded root and addition lists.");
                foreach (var name in names) WatchValidation.Identifier(name);
                var result = names.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (result.Count != names.Length) throw new OperationException("aoi_dependency_policy", "Names must be unique, ignoring case.");
                return result;
            }
            var roots = Names(request.Roots, 32); var approvedAois = Names(request.AoiAdditions, 128); var approvedTypes = Names(request.DataTypeAdditions, 128);
            if (roots.Count == 0) throw new OperationException("aoi_dependency_policy", "Select at least one exact root AOI.");
            Dictionary<string, XElement> Index(XElement c, string section) => LogixPreservation.Index(c.Elements(section).Elements(), n => (string?)n.Attribute("Name") ?? throw new OperationException("aoi_dependency_source", "A library/type entry has no name."));
            var aois = Index(controller, "AddOnInstructionDefinitions"); var types = Index(controller, "DataTypes");
            var existingAois = Index(fresh, "AddOnInstructionDefinitions"); var existingTypes = Index(fresh, "DataTypes");
            foreach (var name in aois.Keys.Intersect(types.Keys, StringComparer.OrdinalIgnoreCase)) Block(name, "Source AOI/UDT namespace collision prevents a unique dependency binding.");
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var budget = 0; long rawBudget = 0;
            void Visit(string name, bool aoi)
            {
                ct.ThrowIfCancellationRequested(); var key = (aoi ? "aoi/" : "udt/") + name;
                if (done.Contains(key)) return;
                if (visiting.Count >= 32 || !visiting.Add(key)) { Block(name, "Cyclic or deeper-than-32 dependency graph."); return; }
                if (done.Count + visiting.Count > 256) throw new OperationException("aoi_dependency_limit", "A dependency closure supports at most 256 definitions.");
                var index = aoi ? aois : types; var existing = aoi ? existingAois : existingTypes; var other = aoi ? existingTypes : existingAois;
                if (!index.TryGetValue(name, out var node)) { Block(name, "Concrete source definition is missing."); visiting.Remove(key); return; }
                if (other.ContainsKey(name)) Block(name, "Destination AOI/UDT namespace collision.");
                static bool OpaqueOrSigned(string name) => new[] { "Encoded", "Encrypted", "Encryption", "Signature", "Protected" }.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));
                if (node.Name != (aoi ? "AddOnInstructionDefinition" : "DataType") || LogixPreservation.Safety(node) || node.DescendantsAndSelf().Any(e => OpaqueOrSigned(e.Name.LocalName) || e.Attributes().Any(a => OpaqueOrSigned(a.Name.LocalName))))
                    Block(name, "Only standard readable unsigned definitions can participate; protected/safety dependencies require a separate review.");
                string[] typeNames = [], calls = [];
                try
                {
                    if (aoi) (typeNames, calls) = AoiDependencyPlanning.Dependencies(node, aois.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase), ref budget);
                    else
                    {
                        if (node.Elements("Members").Count() != 1 || !node.Elements("Members").Elements("Member").Any() || node.Elements("Members").Elements("Member").Any(m => string.IsNullOrWhiteSpace((string?)m.Attribute("DataType"))))
                            throw new OperationException("aoi_dependency_unsupported", "Require one populated Members container and explicit member datatypes.");
                        typeNames = node.Elements("Members").Elements("Member").Attributes("DataType").Select(a => a.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    }
                }
                catch (OperationException ex) { Block(name, ex.Message); }
                var dependencies = new List<string>();
                foreach (var dependency in typeNames.Concat(calls).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (aois.ContainsKey(dependency)) { dependencies.Add("aoi/" + dependency); Visit(dependency, true); }
                    else if (types.ContainsKey(dependency)) { dependencies.Add("udt/" + dependency); Visit(dependency, false); }
                    else if (!ProgramImportPackaging.IsBuiltInType(dependency)) Block(name, "Missing or unsupported declared datatype: " + dependency);
                }
                existing.TryGetValue(name, out var current);
                if (current is not null && (aoi ? AoiDependencyPlanning.Contract(node) != AoiDependencyPlanning.Contract(current) : StructuralEditing.LayoutFingerprint(node) != StructuralEditing.LayoutFingerprint(current)))
                    Block(name, "Existing dependency differs from the source contract/layout; no overwrite or upgrade is authorized.");
                var approved = aoi ? approvedAois : approvedTypes;
                if (current is null && !approved.Contains(name)) Block(name, "Explicitly approve this new dependency identity.");
                if (current is not null && approved.Contains(name)) Block(name, "An approved addition collides with an existing identity; retain compatible dependencies as Use Existing.");
                var raw = lexical.Lexical(node);
                if (current is null && (raw.Length > 1024 * 1024 || (rawBudget += raw.Length) > 4 * 1024 * 1024)) throw new OperationException("aoi_dependency_limit", "New definitions exceed 1 MiB each or 4 MiB total.");
                definitions.Add(new(aoi ? "aoi" : "udt", name, current is null ? "add-reviewed" : "use-existing", lexical.Fingerprint(node), dependencies.ToArray()));
                done.Add(key); visiting.Remove(key);
            }
            foreach (var root in roots.Order(StringComparer.OrdinalIgnoreCase)) Visit(root, true);
            foreach (var name in approvedAois) if (!done.Contains("aoi/" + name)) Block(name, "Approved AOI is outside the selected dependency closure.");
            foreach (var name in approvedTypes) if (!done.Contains("udt/" + name)) Block(name, "Approved datatype is outside the selected dependency closure.");
            foreach (var definition in definitions.Where(d => d.Operation == "add-reviewed"))
                if (fresh.Elements(definition.Kind == "aoi" ? "AddOnInstructionDefinitions" : "DataTypes").Count() != 1)
                    Block(definition.Name, "The destination needs one existing library/type container; this planner does not create or replace controller configuration.");
            var canPlan = findings.All(f => f.Status != "blocked"); var references = new List<StructuralReferenceAddition>(); var edits = new List<StructuralEdit>();
            if (canPlan)
            {
                var snapshot = StructuralArtifact(source.Project.Id, "l5x", source.Bytes);
                foreach (var definition in definitions.Where(d => d.Operation == "add-reviewed"))
                {
                    var node = (definition.Kind == "aoi" ? aois : types)[definition.Name];
                    var container = fresh.Element(definition.Kind == "aoi" ? "AddOnInstructionDefinitions" : "DataTypes")!;
                    var key = definition.Kind + "/" + definition.Name;
                    references.Add(new(key, definition.Kind, definition.Name, new(null, snapshot.Sha256, snapshot.Id), XmlProjects.PathOf(node), definition.RawSha256));
                    edits.Add(new("add_reference", XmlProjects.PathOf(container), target.Fingerprint(container), ReferenceKey: key));
                }
            }
            findings.Add(new("review", "native-acceptance", "project", "No native import, build, controller access or safety acceptance. Apply only after reviewing the complete structural scope and initialization decisions."));
            ComparisonSnapshot Snapshot(ProjectInfo p) => new(p.Id, p.Name, p.Revision, p.CurrentSha256);
            var report = new AoiDependencyPlan(Snapshot(source.Project), Snapshot(destination.Project), canPlan, definitions.ToArray(), references.ToArray(), edits.ToArray(),
                definitions.Where(d => d.Kind == "udt" && d.Operation == "add-reviewed").Select(d => d.Name).ToArray(), findings.ToArray(), "", AoiDependencyPlanning.Boundary);
            report = report with { ReviewSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(report, Json)) };
            if (JsonSerializer.SerializeToUtf8Bytes(report, Json).Length > 1024 * 1024 - 4096)
                throw new OperationException("aoi_dependency_limit", "The complete dependency review exceeds the 1 MiB structural plan budget; no partial plan is emitted.");
            var receipt = StructuralArtifact(destination.Project.Id, "json", JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, kind = "aoi_dependency_plan", producer = WorkspaceIdentity.Name, toolVersion = WorkspaceIdentity.Version, report }, Json));
            return report with { PlanReceipt = receipt };
        }
        finally { gate.Release(); }
    }
}
