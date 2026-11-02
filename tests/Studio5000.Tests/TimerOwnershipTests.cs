using System.Text;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class TimerOwnershipTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellTimerAudit", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    public TimerOwnershipTests()
    {
        Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] });
    }
    private static XElement Tag(string name, string type, string? alias = null, string? dimensions = null) => new("Tag", new XAttribute("Name", name),
        new XAttribute("TagType", alias is null ? "Base" : "Alias"), new XAttribute("DataType", type), alias is null ? null : new XAttribute("AliasFor", alias), dimensions is null ? null : new XAttribute("Dimensions", dimensions));
    private static XElement Routine(string name, params string[] texts) => new("Routine", new XAttribute("Name", name), new XAttribute("Type", "RLL"),
        new XElement("RLLContent", texts.Select((text, i) => new XElement("Rung", new XAttribute("Number", i), new XElement("Text", new XCData(text))))));
    private static XElement Program(string name, params XElement[] routines) => new("Program", new XAttribute("Name", name), new XAttribute("MainRoutineName", routines.FirstOrDefault()?.Attribute("Name")?.Value ?? "Main"),
        new XElement("Tags", Tag("State", "StateType"), Tag("HMI", "HmiType"), Tag("LocalTimer", "TIMER"), Tag("UseCopy", "BOOL"), Tag("Count", "DINT"), Tag("OutValue", "DINT"),
            Tag("Values", "DINT", dimensions: "4"), Tag("ControllerAlias", "TIMER", "ControllerTimer"), Tag("Chain", "TIMER", "ControllerAlias"), Tag("DelayDN", "BOOL", "State.ManIntervention.DN"), Tag("StateArray", "StateType", dimensions: "3")), new XElement("Routines", routines));
    private static XDocument Project(params XElement[] programs) => new(new XElement("RSLogix5000Content", new XElement("Controller", new XAttribute("Name", "Fixture"),
        new XElement("DataTypes", new XElement("DataType", new XAttribute("Name", "StateType"), new XElement("Members", new[] { "ManIntervention", "MsgDelay" }.Select(n => new XElement("Member", new XAttribute("Name", n), new XAttribute("DataType", "TIMER"))))),
            new XElement("DataType", new XAttribute("Name", "HmiType"), new XElement("Members", new[] { "PBSelectTimeout", "PB3SelectTimeout" }.Select(n => new XElement("Member", new XAttribute("Name", n), new XAttribute("DataType", "TIMER")))))),
        new XElement("Tags", Tag("ControllerTimer", "TIMER"), Tag("TimerSlots", "TIMER", dimensions: "4")), new XElement("Programs", programs))));
    private async Task<(ProjectInfo Project, string Path)> Register(XDocument doc)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); await File.WriteAllTextAsync(path, doc.ToString(SaveOptions.DisableFormatting), new UTF8Encoding(false));
        return (await service.RegisterAsync(path), path);
    }
    private TimerOwnershipReport Audit(ProjectInfo p) => service.AuditTimerOwnership(p.Id, new(p.CurrentSha256));
    private const string FourTimers = "TON(State.ManIntervention,100,0)TOF(State.MsgDelay,100,0)TON(HMI.PBSelectTimeout,100,0)RTO(HMI.PB3SelectTimeout,100,0);";
    [Fact]
    public async Task Copied_state_and_axis_timers_share_storage_despite_opposite_JSR_gates()
    {
        var doc = Project(Program("Example", Routine("Main", "XIO(UseCopy)JSR(Original,0);", "XIC(UseCopy)JSR(UserCopy,0);"), Routine("Original", FourTimers), Routine("UserCopy", FourTimers)));
        var (p, path) = await Register(doc); var before = await File.ReadAllBytesAsync(path); var report = Audit(p);
        Assert.Equal("fail", report.Status); Assert.Equal(8, report.Owners.Length); Assert.Equal(4, report.Groups.Length);
        Assert.All(report.Groups, g => { Assert.Equal("fail", g.Status); Assert.Equal("multiple-direct-instruction-owners", g.Classification); Assert.Equal(2, g.OwnerIds.Length); });
        Assert.Contains(report.Groups, g => g.StorageIdentity.EndsWith("State.ManIntervention", StringComparison.Ordinal));
        Assert.Contains(report.Groups, g => g.StorageIdentity.EndsWith("HMI.PB3SelectTimeout", StringComparison.Ordinal));
        Assert.Equal(2, report.Calls.Length); Assert.Contains(report.Calls, c => c.StructuralPrefix.Contains("XIO(UseCopy)")); Assert.Contains(report.Calls, c => c.StructuralPrefix.Contains("XIC(UseCopy)"));
        Assert.All(report.Owners, o => Assert.Single(o.IncomingCallIds)); Assert.False(report.CompleteScan);
        Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Equal(0, service.Get(p.Id).Revision); Assert.Empty(service.SdkJobList()); Assert.Equal(report.AuditSha256, Audit(p).AuditSha256);
    }
    [Fact]
    public async Task Controller_alias_chains_and_fixed_array_member_spellings_are_canonical()
    {
        var doc = Project(Program("Example", Routine("Main", "TON(ControllerAlias,1,0)TON(TimerSlots[01],1,0)TON(StateArray[1].MsgDelay,1,0);"),
            Routine("Copy", "TOF(Chain,1,0)RTO(timerSlots[1],1,0)TON(stateArray[01].msgDelay,1,0);")));
        var (p, _) = await Register(doc); var report = Audit(p);
        Assert.Equal(3, report.Groups.Length); Assert.All(report.Groups, g => Assert.Equal(2, g.OwnerIds.Length));
        Assert.Contains(report.Groups, g => g.StorageIdentity == "Controller.ControllerTimer");
        Assert.Contains(report.Groups, g => g.StorageIdentity == "Controller.TimerSlots[1]");
        Assert.Contains(report.Groups, g => g.StorageIdentity == "Program:Example.StateArray[1].MsgDelay");
        Assert.Contains(report.Owners, o => o.AliasChain.Length == 2);
    }
    [Fact]
    public async Task Controller_aliases_keep_declaration_scope_when_a_program_shadows_the_base_name()
    {
        var first = Program("First", Routine("Main", "TON(ControllerTimer,1,0)TON(GlobalAlias,1,0);")); first.Element("Tags")!.Add(Tag("ControllerTimer", "TIMER"));
        var doc = Project(first, Program("Second", Routine("Main", "TON(Chain,1,0);")));
        doc.Root!.Element("Controller")!.Element("Tags")!.Add(Tag("GlobalAlias", "TIMER", "ControllerTimer"));
        var (p, _) = await Register(doc); var report = Audit(p);
        Assert.Equal(2, report.Groups.Length);
        Assert.Single(report.Groups.Single(g => g.StorageIdentity == "Program:First.ControllerTimer").OwnerIds);
        Assert.Equal(2, report.Groups.Single(g => g.StorageIdentity == "Controller.ControllerTimer").OwnerIds.Length);
    }
    [Fact]
    public async Task Same_local_names_in_distinct_programs_and_distinct_array_elements_are_separate()
    {
        var doc = Project(Program("First", Routine("Main", "TON(LocalTimer,1,0)TON(TimerSlots[0],1,0);")), Program("Second", Routine("Main", "TON(LocalTimer,1,0)TON(TimerSlots[1],1,0);")));
        var (p, _) = await Register(doc); var report = Audit(p);
        Assert.Equal("pass", report.Status); Assert.Equal(4, report.Groups.Length); Assert.All(report.Groups, g => Assert.Single(g.OwnerIds));
        Assert.Equal(2, report.Groups.Count(g => g.StorageIdentity.EndsWith(".LocalTimer", StringComparison.Ordinal)));
    }
    [Fact]
    public async Task One_timer_instruction_with_several_callers_remains_one_owner()
    {
        var doc = Project(Program("Example", Routine("Main", "XIC(UseCopy)JSR(Worker,0);", "XIO(UseCopy)JSR(Worker,0);"), Routine("Worker", "TON(LocalTimer,1,0);")));
        var (p, _) = await Register(doc); var report = Audit(p);
        Assert.Equal("pass", report.Status); Assert.Equal(2, Assert.Single(report.Owners).IncomingCallIds.Length); Assert.Single(Assert.Single(report.Groups).OwnerIds);
        Assert.Contains(report.Findings, f => f.Status == "warning" && f.Detail.Contains("direct JSR call sites"));
    }
    [Theory]
    [InlineData(true, false, "pass", "documented-AOI-scan-mode-exception")]
    [InlineData(false, false, "unsupported", "AOI-scan-mode-exception-unverified")]
    [InlineData(true, true, "fail", "multiple-direct-instruction-owners")]
    public async Task AOI_local_scan_mode_pair_is_a_documented_exception_and_instance_callers_stay_separate(bool enabled, bool extra, string status, string classification)
    {
        var doc = Project(Program("Example", Routine("Main", "TimerLibrary(InstanceA)TimerLibrary(InstanceB)TimerLibrary(InstanceA);")));
        var program = doc.Descendants("Program").Single(); program.Element("Tags")!.Add(Tag("InstanceA", "TimerLibrary"), Tag("InstanceB", "TimerLibrary"));
        var definition = new XElement("AddOnInstructionDefinition", new XAttribute("Name", "TimerLibrary"), new XAttribute("ExecuteEnableInFalse", enabled ? "true" : "false"), new XElement("Parameters"),
            new XElement("LocalTags", new XElement("LocalTag", new XAttribute("Name", "Delay"), new XAttribute("DataType", "TIMER"))),
            new XElement("Routines", Routine("Logic", extra ? "TON(Delay,1,0)TON(Delay,1,0);" : "TON(Delay,1,0);"), Routine("EnableInFalse", "TOF(Delay,1,0);")));
        doc.Root!.Element("Controller")!.Add(new XElement("AddOnInstructionDefinitions", definition));
        var (p, path) = await Register(doc); var original = await File.ReadAllBytesAsync(path); var report = Audit(p);
        Assert.Equal(status, report.Status); Assert.Equal(classification, Assert.Single(report.Groups).Classification);
        Assert.All(report.Owners, o => Assert.Equal("aoi-definition-local-template", o.Scope));
        Assert.Equal(3, report.Calls.Count(c => c.Kind == "AOI")); Assert.Equal(2, report.Calls.Select(c => c.InstanceIdentity).Distinct().Count());
        Assert.Contains(report.Findings, f => f.Status == "warning" && f.Detail.Contains("Same AOI instance"));
        Assert.Equal("timer_rename_policy", Assert.Throws<OperationException>(() => service.PreviewTimerReferenceRename(p.Id,
            new(p.CurrentSha256, report.AuditSha256, report.Owners[0].Id, "PrivateTimer"))).Code);
        Assert.Equal(original, await File.ReadAllBytesAsync(path)); Assert.Empty(service.SdkJobList());
    }
    [Fact]
    public async Task Non_RLL_opaque_and_dynamic_storage_are_explicit_coverage_gaps()
    {
        var doc = Project(Program("Example", Routine("Main", "TON(TimerSlots[Count],1,0);"), new XElement("Routine", new XAttribute("Name", "Fbd"), new XAttribute("Type", "FBD"))));
        doc.Root!.Element("Controller")!.Add(new XElement("AddOnInstructionDefinitions", new XElement("AddOnInstructionDefinition", new XAttribute("Name", "OpaqueLibrary"), new XElement("EncodedData", "fixture"))));
        var (p, _) = await Register(doc); var report = Audit(p);
        Assert.Equal("unsupported", report.Status); Assert.Null(Assert.Single(report.Owners).StorageIdentity);
        Assert.Contains(report.Coverage, c => c.Kind == "non-RLL-excluded"); Assert.Contains(report.Coverage, c => c.Kind == "AOI-body-unavailable");
        Assert.Contains(report.Findings, f => f.Detail.Contains("Dynamic")); Assert.False(report.CompleteScan);
    }
    [Fact]
    public async Task JSR_labels_are_not_timer_consumers_even_when_a_timer_tag_shares_the_name()
    {
        var program = Program("Example", Routine("Main", "JSR(Worker,0);"), Routine("Worker", "TON(LocalTimer,1,0);")); program.Element("Tags")!.Add(Tag("Worker", "TIMER"));
        var (p, _) = await Register(Project(program)); var report = Audit(p);
        Assert.DoesNotContain(report.References, r => r.StorageIdentity.EndsWith(".Worker", StringComparison.Ordinal));
    }
    [Fact]
    public async Task Redirect_preview_includes_all_fields_reset_aliases_expression_and_index_consumers()
    {
        const string consumers = "XIC(State.ManIntervention.EN)XIC(DelayDN)XIC(State.ManIntervention.TT)MOV(25,State.ManIntervention.PRE)CPT(OutValue,State.ManIntervention.ACC+State.ManIntervention.PRE)MOV(Count,Values[State.ManIntervention.ACC])RES(State.ManIntervention);";
        var doc = Project(Program("Example", Routine("Original", "TON(State.ManIntervention,100,0);"), Routine("UserCopy", "TON(State.ManIntervention,100,0);"), Routine("CopyConsumers", consumers)));
        var (p, path) = await Register(doc); var before = await File.ReadAllBytesAsync(path); var report = Audit(p); var owner = report.Owners.Single(o => o.RoutineName == "UserCopy");
        var consumerPath = XmlProjects.PathOf(doc.Descendants("Routine").Single(r => (string?)r.Attribute("Name") == "CopyConsumers"));
        var request = new TimerReferenceRenameRequest(p.CurrentSha256, report.AuditSha256, owner.Id, "PrivateIntervention", [consumerPath]);
        var preview = service.PreviewTimerReferenceRename(p.Id, request);
        Assert.True(preview.CanReview); Assert.Equal("TIMER", preview.ProposedTag.DataType); Assert.Equal("Example", preview.ProposedTag.ProgramName);
        foreach (var field in new[] { ".EN", ".DN", ".TT", ".PRE", ".ACC" }) Assert.Contains(preview.References, r => r.Action == "redirect" && r.Reference.Field == field);
        Assert.Contains(preview.References, r => r.Action == "redirect" && r.Reference.Instruction == "RES");
        Assert.Contains(preview.Operands, o => o.After == "PrivateIntervention.ACC+PrivateIntervention.PRE");
        Assert.Contains(preview.Operands, o => o.After == "Values[PrivateIntervention.ACC]");
        Assert.Contains(preview.Operands, o => o.Before == "DelayDN" && o.After == "PrivateIntervention.DN");
        Assert.Single(preview.References, r => r.Action == "leave-original"); Assert.Equal(preview.ReviewSha256, service.PreviewTimerReferenceRename(p.Id, request).ReviewSha256);
        Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Equal(0, service.Get(p.Id).Revision); Assert.Empty(service.SdkJobList());
        Assert.Equal("stale_revision", Assert.Throws<OperationException>(() => service.PreviewTimerReferenceRename(p.Id, request with { ExpectedAuditSha256 = "stale" })).Code);
        Assert.Equal("timer_rename_collision", Assert.Throws<OperationException>(() => service.PreviewTimerReferenceRename(p.Id, request with { NewTagName = "LocalTimer" })).Code);
    }
    [Fact]
    public async Task Multi_owner_selected_scope_blocks_redirect_and_stale_sources_are_rejected()
    {
        var (p, _) = await Register(Project(Program("Example", Routine("Main", "TON(LocalTimer,1,0)TOF(LocalTimer,1,0);"))));
        var audit = Audit(p); var preview = service.PreviewTimerReferenceRename(p.Id, new(p.CurrentSha256, audit.AuditSha256, audit.Owners[0].Id, "PrivateTimer"));
        Assert.False(preview.CanReview); Assert.Contains(preview.Findings, f => f.Detail.Contains("retain shared ownership"));
        Assert.Equal("stale_revision", Assert.Throws<OperationException>(() => service.AuditTimerOwnership(p.Id, new("stale"))).Code);
    }
    [Fact]
    public async Task Caller_bound_AOI_timer_parameters_are_not_treated_as_private_instance_storage()
    {
        var doc = Project(Program("Example", Routine("Main", "NOP();")));
        doc.Root!.Element("Controller")!.Add(new XElement("AddOnInstructionDefinitions", new XElement("AddOnInstructionDefinition", new XAttribute("Name", "BoundTimer"),
            new XElement("Parameters", new XElement("Parameter", new XAttribute("Name", "Delay"), new XAttribute("DataType", "TIMER"), new XAttribute("Usage", "InOut"))),
            new XElement("Routines", Routine("Logic", "TON(Delay,1,0);")))));
        var (p, _) = await Register(doc); var report = Audit(p);
        Assert.Equal("unsupported", report.Status); Assert.Equal("AOI-timer-binding-unknown", Assert.Single(report.Groups).Classification);
        Assert.Contains(report.Coverage, c => c.Kind == "AOI-parameter-binding-unknown");
    }
    [Fact]
    public async Task Unknown_effects_cross_program_consumers_reserved_names_and_edit_states_block_preview()
    {
        var first = Program("Example", Routine("Main", "TON(LocalTimer,1,0)Unknown( LocalTimer.DN );"));
        var doc = Project(first, Program("OtherProgram", Routine("Main", "NOP();")));
        var (p, _) = await Register(doc); var audit = Audit(p); var request = new TimerReferenceRenameRequest(p.CurrentSha256, audit.AuditSha256, audit.Owners[0].Id, "PrivateTimer");
        var preview = service.PreviewTimerReferenceRename(p.Id, request); Assert.False(preview.CanReview); Assert.Contains(preview.Findings, f => f.Detail.Contains("Unknown instruction"));
        Assert.Equal("timer_rename_policy", Assert.Throws<OperationException>(() => service.PreviewTimerReferenceRename(p.Id, request with { NewTagName = "TRUE" })).Code);
        var otherPath = XmlProjects.PathOf(doc.Descendants("Program").Last().Descendants("Routine").Single());
        Assert.Equal("timer_rename_policy", Assert.Throws<OperationException>(() => service.PreviewTimerReferenceRename(p.Id, request with { ConsumerRoutineXPaths = [otherPath] })).Code);
        first.Descendants("Rung").Single().SetAttributeValue("OnlineEditType", "Pending");
        var (edits, _) = await Register(doc); var editsAudit = Audit(edits);
        Assert.False(service.PreviewTimerReferenceRename(edits.Id, new(edits.CurrentSha256, editsAudit.AuditSha256, editsAudit.Owners[0].Id, "PrivateTimer")).CanReview);
    }
    [Fact]
    public async Task Existing_controller_rename_preview_retains_all_timer_producer_and_consumer_tokens()
    {
        const string text = "TON(ControllerTimer,100,0)XIC(ControllerTimer.EN)XIC(ControllerTimer.DN)XIC(ControllerTimer.TT)MOV(25,ControllerTimer.PRE)CPT(OutValue,ControllerTimer.ACC+ControllerTimer.PRE)MOV(Count,Values[ControllerTimer.ACC])RES(ControllerTimer);";
        var (p, path) = await Register(Project(Program("Example", Routine("Main", text)))); var before = await File.ReadAllBytesAsync(path);
        var preview = await service.PreviewControllerTagRenameAsync(new(p.Id, p.CurrentSha256, "ControllerTimer", "RenamedTimer"));
        Assert.True(preview.CanApply); Assert.Equal(9, preview.References.Count(r => r.Kind == "RLL"));
        Assert.All(preview.References.Where(r => r.Kind == "RLL"), r => Assert.Equal("rename", r.Action));
        Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Equal(0, service.Get(p.Id).Revision); Assert.Empty(service.SdkJobList());
    }
    [Fact]
    public async Task Repeated_caller_bindings_enforce_response_budget_without_partial_reports_or_jobs()
    {
        var callers = Routine("Main", Enumerable.Repeat("JSR(Worker,0);", 1000).ToArray());
        var worker = Routine("Worker", Enumerable.Repeat("TON(LocalTimer,1,0);", 160).ToArray());
        var (p, path) = await Register(Project(Program("Example", callers, worker))); var before = await File.ReadAllBytesAsync(path);
        var ex = Assert.Throws<OperationException>(() => Audit(p)); Assert.Equal("timer_audit_limit", ex.Code);
        Assert.Equal(before, await File.ReadAllBytesAsync(path)); Assert.Empty(service.SdkJobList()); Assert.Equal(0, service.Get(p.Id).Revision);
    }
    public void Dispose() { service.Dispose(); Directory.Delete(root, true); }
}
