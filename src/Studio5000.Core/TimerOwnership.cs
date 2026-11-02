using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record TimerOwnershipRequest(string ExpectedSha256);
public sealed record TimerInstructionOwner(string Id, string? StorageIdentity, string StorageRoot, string Scope, string? ProgramName,
    string? AoiDefinition, string RoutineName, string RoutineXPath, string SourceXPath, int RungIndex, int InstructionIndex,
    string Instruction, string Operand, string RungSha256, string[] AliasChain, string[] StructuralPrefix, string[] IncomingCallIds, string Resolution);
public sealed record TimerStorageReference(string Id, string StorageIdentity, string Field, string? ProgramName, string? AoiDefinition,
    string RoutineXPath, string SourceXPath, int RungIndex, int InstructionIndex, int OperandIndex, string Instruction, string Operand,
    string Role, string RungSha256, string[] AliasChain, int TokenStart, int TokenLength);
public sealed record TimerOwnerGroup(string StorageIdentity, string Scope, string[] OwnerIds, string Status, string Classification, string Detail);
public sealed record TimerCallSite(string Id, string Kind, string CallerRoutineXPath, string? TargetRoutineXPath, string Target,
    string? InstanceIdentity, string SourceXPath, string[] StructuralPrefix);
public sealed record TimerAuditFinding(string Status, string Location, string Detail);
public sealed record TimerAuditCoverage(string Kind, string Location, string Detail);
public sealed record TimerOwnershipReport(int SchemaVersion, string ToolVersion, ComparisonSnapshot Source, string Status,
    int ScannedRllRungs, TimerInstructionOwner[] Owners, TimerOwnerGroup[] Groups, TimerStorageReference[] References,
    TimerCallSite[] Calls, TimerAuditFinding[] Findings, TimerAuditCoverage[] Coverage, bool CompleteScan, string Boundary, string AuditSha256);

internal static class TimerOwnership
{
    internal const string Boundary = "Read-only direct RLL TON/TOF/RTO instruction ownership of resolved saved TIMER storage. Declaration scope, aliases, UDT members and fixed array indexes determine identity; spelling alone never establishes uniqueness. Guards and direct JSR/AOI call sites are structural observations, not reachability or exclusivity proofs. One instruction with multiple callers remains one instruction owner. AOI-local identities are definition templates with separate storage per instance; InOut/caller aliasing remains separate. Non-RLL, protected code, indirect addressing/dispatch, asynchronous/external writers, native scan order, compilation and runtime acceptance are not proven. No project mutation, SDK job or controller connection.";
    internal static bool IsTimerInstruction(string name) => name.ToUpperInvariant() is "TON" or "TOF" or "RTO";
    internal static TimerOwnershipReport Audit(ProjectInfo project, XDocument doc)
    {
        var storage = new TimerStorage(doc); var owners = new List<TimerInstructionOwner>(); var references = new List<TimerStorageReference>();
        var calls = new List<TimerCallSite>(); var groups = new List<TimerOwnerGroup>(); var findings = new List<TimerAuditFinding>(); var coverage = new List<TimerAuditCoverage>();
        var controller = doc.Root!.Element("Controller")!; var scanned = 0; long characters = 0;
        var pending = new List<(TimerStorageAddress Address, string Location)>();
        var definitions = controller.Elements("AddOnInstructionDefinitions").Elements("AddOnInstructionDefinition").ToLookup(d => (string?)d.Attribute("Name") ?? "", StringComparer.OrdinalIgnoreCase);
        var ownerCursor = 0; var referenceCursor = 0; var callCursor = 0; var findingCursor = 0; var coverageCursor = 0;
        long serializedBytes = 0; long originalOwnerBytes = 0;
        void AddSize<T>(T value)
        {
            serializedBytes += JsonSerializer.SerializeToUtf8Bytes(value, ProjectService.Json).Length;
            if (serializedBytes > 8 * 1024 * 1024) throw new OperationException("timer_audit_limit", "Timer records exceed 8 MiB; no partial report was produced.");
        }
        void Budget()
        {
            if (owners.Count + references.Count + calls.Count + findings.Count + coverage.Count + pending.Count + groups.Count > 20000)
                throw new OperationException("timer_audit_limit", "More than 20000 timer audit records; no partial report was produced.");
            while (ownerCursor < owners.Count) { var before = serializedBytes; AddSize(owners[ownerCursor++]); originalOwnerBytes += serializedBytes - before; }
            while (referenceCursor < references.Count) AddSize(references[referenceCursor++]);
            while (callCursor < calls.Count) AddSize(calls[callCursor++]);
            while (findingCursor < findings.Count) AddSize(findings[findingCursor++]);
            while (coverageCursor < coverage.Count) AddSize(coverage[coverageCursor++]);
        }
        string Id(string value) => ProjectService.Hash(Encoding.UTF8.GetBytes(project.CurrentSha256 + "|" + value));
        foreach (var routine in controller.Descendants("Routine").Where(r => r.Parent?.Name == "Routines" && r.Parent.Parent?.Name.LocalName is "Program" or "AddOnInstructionDefinition"))
        {
            var owner = routine.Parent!.Parent!; var aoi = owner.Name == "AddOnInstructionDefinition" ? (string?)owner.Attribute("Name") : null;
            var program = aoi is null ? (string?)owner.Attribute("Name") : null; var routinePath = XmlProjects.PathOf(routine); var name = (string?)routine.Attribute("Name") ?? "";
            if ((string?)routine.Attribute("Type") != "RLL") { coverage.Add(new("non-RLL-excluded", routinePath, "Routine type " + (string?)routine.Attribute("Type") + " is not inventoried for timers or consumers.")); Budget(); continue; }
            var rungs = routine.Elements("RLLContent").Elements("Rung").ToArray();
            if (rungs.Length == 0) { coverage.Add(new("no-readable-RLL-rungs", routinePath, "No readable rungs; this does not establish absence of timers.")); Budget(); }
            for (var rungIndex = 0; rungIndex < rungs.Length; rungIndex++)
            {
                var text = (string?)rungs[rungIndex].Element("Text") ?? ""; characters += text.Length;
                if (characters > 16 * 1024 * 1024) throw new OperationException("timer_audit_limit", "Executable source exceeds the 16 MiB audit budget.");
                var sourcePath = XmlProjects.PathOf(rungs[rungIndex]); var rungHash = ProjectService.Hash(Encoding.UTF8.GetBytes(text));
                List<LadderPreview.Part> parts;
                try { parts = LadderPreview.ParseLogic(text); scanned++; }
                catch (OperationException ex) { findings.Add(new("unsupported", sourcePath, "RLL could not be inventoried: " + ex.Message)); Budget(); continue; }
                var instructionIndex = 0;
                void Walk(List<LadderPreview.Part> sequence, List<string> prefix, string branchPath)
                {
                    var guards = prefix.ToList();
                    foreach (var part in sequence)
                    {
                        if (part.Legs is { } legs)
                        {
                            for (var leg = 0; leg < legs.Count; leg++) Walk(legs[leg], guards, branchPath + "/leg[" + (leg + 1) + "]");
                            guards.Add("uninterpreted parallel result at " + branchPath); continue;
                        }
                        var index = instructionIndex++; var location = sourcePath + "/instruction[" + (index + 1) + "]";
                        var resolved = part.Operands.Select(o => storage.Resolve(o, owner)).ToArray();
                        if (IsTimerInstruction(part.Instruction))
                        {
                            var operand = part.Operands.FirstOrDefault() ?? ""; var address = resolved.FirstOrDefault();
                            var valid = address is { Issue: null, Field: "", Constant: false } && LogicContext.Same(address.Type, "TIMER");
                            owners.Add(new(Id(location), valid ? address!.Identity : null, address?.Root ?? "", address?.Scope ?? "unresolved", program, aoi, name,
                                routinePath, sourcePath, rungIndex, index, part.Instruction, operand, rungHash, address?.Aliases ?? [], guards.ToArray(), [], valid ? "resolved" : "unsupported"));
                            if (!valid) findings.Add(new("unsupported", location, "Timer operand cannot establish a writable scalar TIMER identity: " + operand + "; " + (address?.Issue ?? "type, field, constant or address unsupported")));
                        }
                        var roles = LogicTracing.Roles(part.Instruction, part.Operands.Length);
                        for (var argument = 0; argument < resolved.Length; argument++)
                        {
                            var instruction = part.Instruction.ToUpperInvariant();
                            if (argument == 0 && instruction is "JSR" or "JMP" or "LBL" or "EVENT" || argument < 3 && instruction is "GSV" or "SSV") continue;
                            var operand = part.Operands[argument];
                            foreach (var token in TagReferences.Lex(operand, message => coverage.Add(new("operand-lexical-unknown", location, message))))
                            {
                                var address = storage.Resolve(operand.Substring(token.Start, token.Length), owner); if (address is null) continue;
                                if (address.Issue is not null) { if (address.PotentialTimer) pending.Add((address, location)); continue; }
                                if (!LogicContext.Same(address.Type, "TIMER")) continue;
                                references.Add(new(Id(location + "/operand[" + argument + "]/token[" + token.Start + "]"), address.Identity, address.Field, program, aoi,
                                    routinePath, sourcePath, rungIndex, index, argument, part.Instruction, operand,
                                    token.Role == "index_expression" ? "read-index" : roles[argument], rungHash, address.Aliases, token.Start, token.Length));
                            }
                        }
                        if (part.Instruction.Equals("JSR", StringComparison.OrdinalIgnoreCase))
                        {
                            var target = part.Operands.FirstOrDefault() ?? "";
                            var targets = owner.Elements("Routines").Elements("Routine").Where(r => LogicContext.Same((string?)r.Attribute("Name"), target)).ToArray();
                            calls.Add(new(Id(location + "/call"), "JSR", routinePath, targets.Length == 1 ? XmlProjects.PathOf(targets[0]) : null, target, null, sourcePath, guards.ToArray()));
                            if (targets.Length != 1) coverage.Add(new("JSR-target-unknown", location, "Indirect/missing/ambiguous routine target; no reachability proof."));
                        }
                        else if (definitions[part.Instruction].Any())
                        {
                            var instance = resolved.FirstOrDefault();
                            calls.Add(new(Id(location + "/call"), "AOI", routinePath, null, part.Instruction,
                                instance is { Issue: null, Field: "" } && LogicContext.Same(instance.Type, part.Instruction) ? instance.Identity : null, sourcePath, guards.ToArray()));
                        }
                        if (LadderPreflight.Predicate(part)) guards.Add(part.Instruction + "(" + string.Join(",", part.Operands) + ")");
                        else if (!part.Instruction.Equals("NOP", StringComparison.OrdinalIgnoreCase)) guards.Add("uninterpreted continuation after " + part.Instruction + " at " + location);
                        Budget();
                    }
                }
                Walk(parts, [], "rung");
            }
        }
        foreach (var definition in definitions.SelectMany(g => g))
            if (!definition.Elements("Routines").Elements("Routine").Any())
            { coverage.Add(new("AOI-body-unavailable", XmlProjects.PathOf(definition), "Missing/opaque/protected AOI body; public calls do not expose internal timer ownership.")); Budget(); }
        var unresolvedReferences = pending.ToArray(); pending.Clear();
        foreach (var entry in unresolvedReferences)
        { findings.Add(new("unsupported", entry.Location, "Possible timer consumer/storage is unresolved: " + entry.Address.Root + "; " + entry.Address.Issue)); Budget(); }
        foreach (var group in owners.Where(o => o.StorageIdentity is not null).GroupBy(o => o.StorageIdentity!, StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.ToArray(); var status = "pass"; var classification = "single-direct-instruction";
            var detail = "One direct instruction observed; this is not complete ownership or caller uniqueness proof.";
            if (rows[0].Scope == "aoi-parameter-binding")
            { status = "unsupported"; classification = "AOI-timer-binding-unknown"; detail = "Parameter storage is bound by callers; this template does not establish a physical timer identity across calls."; }
            if (rows.Length > 1)
            {
                var definition = rows[0].AoiDefinition is { } definitionName ? definitions[definitionName].ToArray() : [];
                var scanPair = rows.Length == 2 && rows.All(o => o.Scope == "aoi-definition-local-template") &&
                    rows.Select(o => o.RoutineName).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(["Logic", "EnableInFalse"]);
                if (scanPair && definition.Length == 1 && LogicContext.Same((string?)definition[0].Attribute("ExecuteEnableInFalse"), "true"))
                {
                    classification = "documented-AOI-scan-mode-exception";
                    detail = "Definition-local template used in Logic and enabled EnableInFalse. These alternative scan modes are documented in 1756-PM010N-EN-P, pages 47-49. Preserve library bodies; caller/instance uniqueness and native behavior remain separate.";
                }
                else if (scanPair)
                { status = "unsupported"; classification = "AOI-scan-mode-exception-unverified"; detail = "Logic/EnableInFalse pair observed, but enabled scan-mode metadata is missing/ambiguous; no automatic exception or edit."; }
                else
                { status = "fail"; classification = "multiple-direct-instruction-owners"; detail = rows.Length + " timer instructions resolve to the same storage identity. Opposite gates, apparent reachability or copied routine names do not waive ownership reuse."; }
            }
            groups.Add(new(group.Key, rows[0].Scope, rows.Select(o => o.Id).ToArray(), status, classification, detail));
        }
        foreach (var callGroup in calls.Where(c => c.Kind == "AOI" && c.InstanceIdentity is not null).GroupBy(c => c.InstanceIdentity!, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            findings.Add(new("warning", callGroup.Key, "Same AOI instance appears at " + callGroup.Count() + " call sites. This is a separate caller audit; definition-local template ownership does not prove per-instance execution uniqueness."));
        if (owners.Any(o => o.Scope == "aoi-parameter-binding")) coverage.Add(new("AOI-parameter-binding-unknown", "AOI parameters", "InOut timer identities are caller-dependent templates. Cross-call aliasing is not expanded or proven unique."));
        Budget();
        var incomingCalls = calls.Where(c => c.Kind == "JSR" && c.TargetRoutineXPath is not null).GroupBy(c => c.TargetRoutineXPath!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToArray(), StringComparer.Ordinal);
        var boundOwners = owners.Select(o => o with { IncomingCallIds = incomingCalls.GetValueOrDefault(o.RoutineXPath) ?? [] }).ToArray();
        serializedBytes -= originalOwnerBytes;
        foreach (var owner in boundOwners) AddSize(owner);
        foreach (var group in groups) AddSize(group);
        foreach (var owner in boundOwners.Where(o => o.IncomingCallIds.Length > 1))
            findings.Add(new("warning", owner.Id, "One timer instruction has " + owner.IncomingCallIds.Length + " direct JSR call sites. Caller/execution reuse is separate from instruction owner count; guards do not prove scan uniqueness."));
        Budget();
        var report = new TimerOwnershipReport(1, WorkspaceIdentity.Version, new(project.Id, project.Name, project.Revision, project.CurrentSha256),
            groups.Any(g => g.Status == "fail") ? "fail" : scanned == 0 || groups.Any(g => g.Status == "unsupported") || findings.Any(f => f.Status == "unsupported") ? "unsupported" : "pass",
            scanned, boundOwners, groups.ToArray(), references.ToArray(), calls.ToArray(), findings.ToArray(), coverage.ToArray(), false, Boundary, "");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, ProjectService.Json);
        if (bytes.Length > 8 * 1024 * 1024) throw new OperationException("timer_audit_limit", "Timer report exceeds 8 MiB; no partial report was produced.");
        return report with { AuditSha256 = ProjectService.Hash(bytes) };
    }
}

public sealed partial class ProjectService
{
    public TimerOwnershipReport AuditTimerOwnership(string id, TimerOwnershipRequest request)
    {
        var project = Get(id); Expect(project, request.ExpectedSha256);
        if (project.Kind != "logix") throw new OperationException("not_logix", "Select a Logix snapshot.");
        return TimerOwnership.Audit(project, XmlProjects.Parse(File.ReadAllBytes(RevisionFile(id, project.Revision))));
    }
}
