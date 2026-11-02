using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record TimerReferenceRenameRequest(string ExpectedSha256, string ExpectedAuditSha256, string OwnerId, string NewTagName, string[]? ConsumerRoutineXPaths = null);
public sealed record TimerPrivateTag(string Name, string ProgramName, string DataType);
public sealed record TimerRenameOperand(string SourceXPath, int InstructionIndex, int OperandIndex, string Before, string After, string[] ReferenceIds);
public sealed record TimerRenameReference(TimerStorageReference Reference, string Action, string? ProposedAddress);
public sealed record TimerReferenceRenameReport(int SchemaVersion, string ToolVersion, ComparisonSnapshot Source, string AuditSha256,
    string OwnerId, TimerPrivateTag ProposedTag, bool CanReview, TimerRenameReference[] References, TimerRenameOperand[] Operands,
    string[] SelectedRoutineXPaths, TimerAuditFinding[] Findings, TimerAuditCoverage[] Coverage, string Boundary, string ReviewSha256);

internal static class TimerReferenceRename
{
    internal static TimerReferenceRenameReport Preview(ProjectInfo project, XDocument doc, TimerReferenceRenameRequest request)
    {
        if (request.NewTagName is null || !Regex.IsMatch(request.NewTagName, "^[A-Za-z_][A-Za-z0-9_]{0,39}$", RegexOptions.CultureInvariant) ||
            request.NewTagName.ToUpperInvariant() is "TRUE" or "FALSE" or "AND" or "OR" or "XOR" or "NOT" or "MOD" or "BST" or "NXB" or "BND" ||
            request.ConsumerRoutineXPaths is { Length: > 32 } || request.ConsumerRoutineXPaths?.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 8192) == true)
            throw new OperationException("timer_rename_policy", "Choose a fresh tag identifier up to 40 characters and at most 32 explicit consumer routines.");
        var audit = TimerOwnership.Audit(project, doc);
        if (!audit.AuditSha256.Equals(request.ExpectedAuditSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Re-read the exact timer ownership audit before reviewing references.");
        var owner = audit.Owners.SingleOrDefault(o => o.Id == request.OwnerId) ?? throw new OperationException("timer_owner_missing", "Select an owner ID from the exact audit.");
        if (owner.ProgramName is null || owner.AoiDefinition is not null || owner.StorageIdentity is null)
            throw new OperationException("timer_rename_policy", "Only a resolved program-routine TIMER owner can be redirected. Preserve AOI library bodies.");
        var owningRoutine = XmlProjects.Select(doc, owner.RoutineXPath); var program = owningRoutine.Parent!.Parent!;
        if (LogicContext.Same((string?)program.Attribute("Class"), "Safety")) throw new OperationException("timer_rename_policy", "Safety-program timer redirection is unsupported.");
        var controller = doc.Root!.Element("Controller")!;
        if (program.Elements("Tags").Elements("Tag").Concat(controller.Elements("Tags").Elements("Tag")).Any(t => LogicContext.Same((string?)t.Attribute("Name"), request.NewTagName)))
            throw new OperationException("timer_rename_collision", "The proposed program/controller tag name already exists.");
        var selected = new HashSet<string>(StringComparer.Ordinal) { owner.RoutineXPath };
        foreach (var xpath in request.ConsumerRoutineXPaths ?? [])
        {
            var routine = XmlProjects.Select(doc, xpath);
            if (routine.Name != "Routine" || routine.Parent?.Name != "Routines" || routine.Parent.Parent != program || (string?)routine.Attribute("Type") != "RLL")
                throw new OperationException("timer_rename_policy", "Consumer routines must be readable RLL routines in the owner's same program.");
            selected.Add(XmlProjects.PathOf(routine));
        }
        var findings = new List<TimerAuditFinding>();
        foreach (var xpath in selected)
        {
            var routine = XmlProjects.Select(doc, xpath);
            if (routine.Descendants("Rung").Any(r => r.Attribute("OnlineEditType") is { Value: not "" and not "None" }))
                findings.Add(new("blocked", xpath, "Selected routine contains online-edit rung states."));
        }
        var selectedOwners = audit.Owners.Where(o => selected.Contains(o.RoutineXPath) && LogicContext.Same(o.StorageIdentity, owner.StorageIdentity)).ToArray();
        if (selectedOwners.Length != 1) findings.Add(new("blocked", owner.StorageIdentity, "Selected routines contain " + selectedOwners.Length + " owners of this TIMER; redirecting all references would retain shared ownership."));
        foreach (var finding in audit.Findings.Where(f => f.Status == "unsupported" && selected.Any(p => f.Location.StartsWith(p + "/", StringComparison.Ordinal))))
            findings.Add(new("blocked", finding.Location, "Unresolved possible timer references in selected scope: " + finding.Detail));
        foreach (var item in audit.Coverage.Where(c => selected.Any(p => c.Location == p || c.Location.StartsWith(p + "/", StringComparison.Ordinal))))
            findings.Add(new("blocked", item.Location, "Selected scope has incomplete source coverage: " + item.Detail));
        var references = audit.References.Where(r => LogicContext.Same(r.StorageIdentity, owner.StorageIdentity)).Select(r =>
            new TimerRenameReference(r, selected.Contains(r.RoutineXPath) ? "redirect" : "leave-original", selected.Contains(r.RoutineXPath) ? request.NewTagName + r.Field : null)).ToArray();
        foreach (var reference in references.Where(r => r.Action == "redirect" && r.Reference.Role == "unknown"))
            findings.Add(new("blocked", reference.Reference.SourceXPath, "Unknown instruction operand effects remain explicit: " + reference.Reference.Instruction));
        var operands = new List<TimerRenameOperand>();
        foreach (var group in references.Where(r => r.Action == "redirect").GroupBy(r => (r.Reference.SourceXPath, r.Reference.InstructionIndex, r.Reference.OperandIndex)))
        {
            var rows = group.OrderByDescending(r => r.Reference.TokenStart).ToArray(); var before = rows[0].Reference.Operand; var after = before; var previous = before.Length;
            foreach (var row in rows)
            {
                var reference = row.Reference;
                if (reference.TokenStart + reference.TokenLength > previous) throw new OperationException("timer_rename_policy", "Overlapping timer reference spans cannot be redirected.");
                after = after.Remove(reference.TokenStart, reference.TokenLength).Insert(reference.TokenStart, row.ProposedAddress!); previous = reference.TokenStart;
            }
            operands.Add(new(group.Key.SourceXPath, group.Key.InstructionIndex, group.Key.OperandIndex, before, after, rows.Select(r => r.Reference.Id).ToArray()));
        }
        var report = new TimerReferenceRenameReport(1, WorkspaceIdentity.Version, audit.Source, audit.AuditSha256, owner.Id,
            new(request.NewTagName, owner.ProgramName, "TIMER"), !findings.Any(f => f.Status == "blocked"), references, operands.ToArray(), selected.Order(StringComparer.Ordinal).ToArray(), findings.ToArray(), audit.Coverage,
            "Preview only. Propose a new private program TIMER and redirect all resolved selected-routine references, including EN/DN/TT/PRE/ACC, member bits, expression/index uses and RES. Visible references outside selected routines retain the original timer; alias declarations are not changed. AOI/library bodies stay unchanged. CanReview covers the explicit readable scope only; non-RLL/protected/external users, native import, scan/motion/safety and runtime acceptance are separate. No tag, project revision, artifact or native job is created; no automatic apply.", "");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, ProjectService.Json);
        if (bytes.Length > 8 * 1024 * 1024) throw new OperationException("timer_rename_limit", "Timer reference preview exceeds 8 MiB; no partial preview was produced.");
        return report with { ReviewSha256 = ProjectService.Hash(bytes) };
    }
}

public sealed partial class ProjectService
{
    public TimerReferenceRenameReport PreviewTimerReferenceRename(string id, TimerReferenceRenameRequest request)
    {
        var project = Get(id); Expect(project, request.ExpectedSha256);
        if (project.Kind != "logix") throw new OperationException("not_logix", "Select a Logix snapshot.");
        return TimerReferenceRename.Preview(project, XmlProjects.Parse(File.ReadAllBytes(RevisionFile(id, project.Revision))), request);
    }
}
