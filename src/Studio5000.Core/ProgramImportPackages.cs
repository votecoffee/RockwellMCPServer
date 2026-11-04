using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record ProgramImportPackageRequest(MergeSnapshot Source, MergeSnapshot Baseline, string ProgramName,
    string TaskName, string[]? ChangedRoutines = null, string[]? NewControllerTags = null, string[]? NewDataTypes = null,
    ProgramImportSchemaRequest? Schema = null);
public sealed record ProgramImportDefinition(string Kind, string Name, string RawSha256, string CanonicalSha256,
    string Disposition, int? ContentItems = null);
public sealed record ProgramImportValuePolicy(bool PreserveExistingOfflineValuesCheckboxRequired,
    string ExistingOnlineValues, string NewTagValues, bool NativePreservationObserved = false);
public sealed record ProgramImportManifest(int SchemaVersion, string Kind, MergeSnapshot Source, MergeSnapshot Baseline,
    string ProgramName, string TaskName, string ControllerName, string ProgramXmlSha256, string SourceProgramRawSha256,
    int ChangedRoutineCount, int IncludedRoutineCount, int IncludedLocalTagCount, ProgramImportDefinition[] Definitions,
    string[] NewControllerTags, string[] NewDataTypes, string BaselineTaskSha256, ProgramImportValuePolicy Values,
    ProgramImportSchemaReport Schema, string[] NativeSteps, string DependencyCoverage, string Boundary,
    bool PackagingOnly = true, bool NativeImportAccepted = false, string ImportOperation = "overwrite_program",
    string ReplacementScope = "all_routines_and_local_tags", string SchedulePolicy = "preserve_existing_task_and_order",
    bool RungInsertion = false);
public sealed record ProgramImportContent(byte[] ProgramXml, byte[] ManifestJson, byte[] Zip, ProgramImportManifest Manifest);
public sealed record ProgramImportPackage(Artifact Program, Artifact Manifest, Artifact Package, ProgramImportManifest Plan);
public sealed record ProgramImportReadbackRequest(string ManifestArtifactId, string ExpectedManifestSha256,
    MergeSnapshot Readback, string ValueMode = "offline_preserved");
public sealed record ProgramImportReadback(bool ProgramMetadataMatches, bool RoutinesMatch, bool LocalTagDeclarationsMatch,
    bool DependenciesMatch, bool TaskScheduleMatches, bool OtherControllerPayloadsMatch, bool? SavedValuesMatch,
    bool Matches, string[] Differences, string Boundary, bool NativeImportAccepted = false);

public static class ProgramImportPackaging
{
    public const int MaximumOutputBytes = 32 * 1024 * 1024;
    public const string Boundary = "Source-bound whole-program overwrite packaging and saved-XML readback only. No import, rung insertion, semantic/compiler validation, native finalization, download or controller connection. All native wrapper/import, edit-state, schedule and value-preservation acceptance remain pending.";
    private static readonly HashSet<string> BuiltIns = new(StringComparer.OrdinalIgnoreCase)
        { "BOOL", "SINT", "INT", "DINT", "LINT", "REAL", "LREAL", "USINT", "UINT", "UDINT", "ULINT", "STRING", "TIMER", "COUNTER", "CONTROL", "MESSAGE", "DT", "LDT", "TIME", "LTIME", "TIME32", "ITIME", "DATE", "TOD", "LTOD" };

    public static ProgramImportContent Build(byte[] sourceBytes, byte[] baselineBytes, ProgramImportPackageRequest request, byte[]? schemaBytes = null)
    {
        if (request.Source is null || request.Baseline is null ||
            !string.Equals(ProjectService.Hash(sourceBytes), request.Source.Sha256, StringComparison.OrdinalIgnoreCase) || !string.Equals(ProjectService.Hash(baselineBytes), request.Baseline.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Error("source", "Pin the exact source and destination snapshot bytes and SHA-256 values.");
        OnlineEdits.ProgramPath(request.ProgramName); OnlineEdits.ProgramPath(request.TaskName);
        var source = new LogixLexicalDocument(sourceBytes); var baseline = new LogixLexicalDocument(baselineBytes);
        if ((string?)source.Document.Root!.Attribute("TargetType") is not (null or "Controller" or "Program") ||
            (string?)baseline.Document.Root!.Attribute("TargetType") is not (null or "Controller") ||
            string.Equals((string?)baseline.Document.Root.Attribute("ContainsContext"), "true", StringComparison.OrdinalIgnoreCase))
            throw Error("context", "Use a whole-program/controller source and a whole-controller destination baseline, not routine/rung context masquerading as a complete program.");
        if ((string?)source.Document.Root.Attribute("TargetType") == "Program" &&
            ((string?)source.Document.Root.Attribute("TargetName") != request.ProgramName || (string?)source.Document.Root.Attribute("TargetCount") is not (null or "1")))
            throw Error("context", "A partial Program source must identify this exact single target, not another program's context.");
        var controller = source.Document.Root!.Element("Controller")!; var destination = baseline.Document.Root!.Element("Controller")!;
        var controllerName = (string?)controller.Attribute("Name");
        if (string.IsNullOrWhiteSpace(controllerName) || controllerName != (string?)destination.Attribute("Name") ||
            (string?)source.Document.Root.Attribute("SoftwareRevision") != (string?)baseline.Document.Root!.Attribute("SoftwareRevision"))
            throw Error("context", "Source and baseline must identify the same controller and software version; no conversion/correlation is inferred.");
        var program = Named(controller.Elements("Programs").Elements("Program"), request.ProgramName);
        var original = Named(destination.Elements("Programs").Elements("Program"), request.ProgramName);
        if ((string?)program.Attribute("Name") != request.ProgramName || (string?)program.Attribute("Class") == "Safety" || (string?)original.Attribute("Class") == "Safety")
            throw Error("context", "Use the exact existing standard-program name; renames, safety programs and phase conversion are outside this profile.");
        if (string.Equals((string?)program.Attribute("TestEdits"), "true", StringComparison.OrdinalIgnoreCase))
            throw Error("routine_state", "Select a fresh normal/finalized program export with no test edits; packaging never finalizes or discards edits.");
        var routines = Unique(program, "Routines", "Routine"); var oldRoutines = Unique(original, "Routines", "Routine");
        var tags = Unique(program, "Tags", "Tag"); var oldTags = Unique(original, "Tags", "Tag");
        if (routines.Length is 0 or > 1024 || tags.Length > 4096) throw Error("limit", "Select a complete program with 1..1024 routines and at most 4096 local tags.");
        foreach (var routine in routines) CheckRoutine(routine);
        CheckNoOmissions(oldRoutines, routines, "routine"); CheckNoOmissions(oldTags, tags, "local tag");
        foreach (var tag in oldTags)
            if (Layout(tag, true) != Layout(Named(tags, Name(tag)), true))
                throw Error("tag_layout", "Existing local-tag layout/alias/configuration changes are outside value-preserving packaging: " + Name(tag));
        foreach (var attr in new[] { "MainRoutineName", "FaultRoutineName" })
            if ((string?)program.Attribute(attr) is { Length: > 0 } target && !routines.Any(r => Same(r, target)))
                throw Error("assignment", "Program " + attr + " must identify an included complete routine.");
        var task = Named(destination.Elements("Tasks").Elements("Task"), request.TaskName);
        var schedules = destination.Elements("Tasks").Elements("Task").Where(t => t.Elements("ScheduledPrograms").Elements("ScheduledProgram").Any(p => Same(p, request.ProgramName))).ToArray();
        if (schedules.Length != 1 || schedules[0] != task || task.Elements("ScheduledPrograms").Elements("ScheduledProgram").Count(p => Same(p, request.ProgramName)) != 1)
            throw Error("schedule", "Pin the existing unique task/scheduled-program location. Task state is never manufactured or included in the import.");
        var globals = SelectNew(request.NewControllerTags, controller, destination, "Tags", "Tag");
        var types = SelectNew(request.NewDataTypes, controller, destination, "DataTypes", "DataType");
        foreach (var tag in globals)
            if ((string?)tag.Attribute("TagType") != "Base" || tag.Attribute("AliasFor") is not null || !tag.Elements("Data").Any() || ForcedOrSafety(tag))
                throw Error("dependency", "Selected new controller tags require unforced Base definitions with exact saved initial Data.");
        if (tags.Where(t => !oldTags.Any(o => Same(o, Name(t)))).Any(ForcedOrSafety))
            throw Error("dependency", "New program tags cannot introduce force/safety metadata in this packaging profile.");
        var sourceTypes = controller.Elements("DataTypes").Elements("DataType").ToArray();
        var destinationTypes = destination.Elements("DataTypes").Elements("DataType").ToArray();
        var destinationAois = destination.Elements("AddOnInstructionDefinitions").Elements("AddOnInstructionDefinition").ToArray();
        var sourceGlobals = controller.Elements("Tags").Elements("Tag").ToDictionary(Name, StringComparer.OrdinalIgnoreCase);
        var localNames = tags.Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var referencedGlobals = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long characters = 0;
        var texts = routines.Where(r => (string?)r.Attribute("Type") == "RLL").SelectMany(r => r.Elements("RLLContent").Elements("Rung").Elements("Text").Select(t => t.Value))
            .Concat(routines.Where(r => (string?)r.Attribute("Type") == "ST").SelectMany(r => r.Elements("STContent").Elements("Line").Select(t => t.Value)))
            .Concat(tags.Select(t => (string?)t.Attribute("AliasFor")).OfType<string>());
        foreach (var textSource in texts)
        {
            if (textSource.Length > 1024 * 1024 || (characters += textSource.Length) > 16 * 1024 * 1024)
                throw Error("limit", "Direct dependency scanning supports 1 MiB per text source and 16 MiB in total.");
            foreach (var token in TagReferences.Lex(textSource, _ => { }))
                if (token.Program is null && !localNames.Contains(token.Name) && sourceGlobals.ContainsKey(token.Name)) referencedGlobals.Add(token.Name);
        }
        foreach (var name in referencedGlobals)
        {
            var existing = destination.Elements("Tags").Elements("Tag").SingleOrDefault(t => Same(t, name));
            if (existing is null && !globals.Any(t => Same(t, name))) throw Error("dependency_missing", "Explicitly select the new directly referenced controller tag: " + name);
            if (existing is not null && Layout(sourceGlobals[name], true) != Layout(existing, true))
                throw Error("tag_layout", "A referenced existing controller-tag declaration differs from the destination: " + name + ". No existing controller-tag overwrite is packaged.");
        }
        foreach (var type in types)
            if (destinationAois.Any(t => Same(t, Name(type)))) throw Error("dependency_existing", "A selected type collides with a destination AOI; no existing type/AOI overwrite is packaged.");
        var checkedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var checking = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Type(string name)
        {
            if (checkedTypes.Contains(name)) return;
            if (checking.Count >= 32 || !checking.Add(name)) throw Error("dependency", "Cyclic or deeper-than-32 datatype dependencies are unsupported.");
            var a = sourceTypes.SingleOrDefault(t => Same(t, name)); var b = destinationTypes.SingleOrDefault(t => Same(t, name));
            if (b is not null)
            {
                if (a is not null && TypeLayout(a) != TypeLayout(b)) throw Error("type_conflict", "Existing destination datatype differs: " + name + ". It is not a new-type addition; referenced UDT overwrite has separate online restrictions.");
                if (a is not null) foreach (var member in a.Elements("Members").Elements("Member")) Type((string?)member.Attribute("DataType") ?? throw Error("dependency", "Member datatype is missing."));
            }
            else if (a is not null)
            {
                if (!types.Any(t => Same(t, name))) throw Error("dependency_missing", "Explicitly select the new required datatype: " + name);
                foreach (var member in a.Elements("Members").Elements("Member")) Type((string?)member.Attribute("DataType") ?? throw Error("dependency", "Member datatype is missing."));
            }
            else if (!BuiltIns.Contains(name) && !destinationAois.Any(t => Same(t, name)) &&
                !destination.Descendants("Tag").Any(t => string.Equals((string?)t.Attribute("DataType"), name, StringComparison.OrdinalIgnoreCase)))
                throw Error("dependency_missing", "Datatype is not declared by the destination or selected new definitions: " + name);
            checking.Remove(name); checkedTypes.Add(name);
        }
        foreach (var tag in tags.Concat(globals)) if ((string?)tag.Attribute("DataType") is { Length: > 0 } type) Type(type);
        foreach (var name in referencedGlobals) if ((string?)sourceGlobals[name].Attribute("DataType") is { Length: > 0 } type) Type(type);
        foreach (var type in types) Type(Name(type));
        var changed = routines.Where(r => oldRoutines.SingleOrDefault(o => Same(o, Name(r))) is not { } old || Canonical(r) != Canonical(old)).Select(Name).ToArray();
        if (request.ChangedRoutines is { } expected && (!Names(expected).SetEquals(changed) || expected.Length != changed.Length))
            throw Error("changed_set", "ChangedRoutines must match the exact changed/new routine set; it is not the set of routines to include.");
        var programRaw = source.Raw(program);
        var span = source.Locate(program); var open = source.Text[span.Start..span.OpenEnd];
        if (program.Attribute("Use") is not null) open = Regex.Replace(open, "(?<=\\s)Use\\s*=\\s*(?:\"[^\"]*\"|'[^']*')", "Use=\"Target\"", RegexOptions.CultureInvariant);
        else open = open.Insert(open.Length - 1, " Use=\"Target\"");
        var packagedProgram = open + source.Text[span.OpenEnd..span.End];
        var schemaRevision = (string?)source.Document.Root.Attribute("SchemaRevision"); var softwareRevision = (string?)source.Document.Root.Attribute("SoftwareRevision");
        if (string.IsNullOrWhiteSpace(schemaRevision) || string.IsNullOrWhiteSpace(softwareRevision)) throw Error("context", "Keep explicit source SchemaRevision and SoftwareRevision; no version is guessed.");
        string Attr(string value) => System.Security.SecurityElement.Escape(value)!;
        var text = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<RSLogix5000Content SchemaRevision=\"" + Attr(schemaRevision) + "\" SoftwareRevision=\"" + Attr(softwareRevision) +
            "\" TargetType=\"Program\" TargetName=\"" + Attr(request.ProgramName) + "\" TargetCount=\"1\" ContainsContext=\"true\"><Controller Use=\"Context\" Name=\"" + Attr(controllerName) + "\">" +
            (types.Length == 0 ? "" : "<DataTypes>" + string.Concat(types.Select(source.Raw)) + "</DataTypes>") +
            (globals.Length == 0 ? "" : "<Tags>" + string.Concat(globals.Select(source.Raw)) + "</Tags>") +
            "<Programs>" + packagedProgram + "</Programs></Controller></RSLogix5000Content>\n";
        var xml = Encoding.UTF8.GetBytes(text); if (xml.Length > MaximumOutputBytes) throw Error("limit", "The import exceeds 32 MiB.");
        var output = new LogixLexicalDocument(xml); var outputProgram = output.Document.Descendants("Program").Single();
        foreach (var e in routines.Concat(tags).Concat(globals).Concat(types))
        {
            var candidates = e.Name == "Routine" ? outputProgram.Elements("Routines").Elements("Routine") : e.Name == "DataType" ? output.Document.Root!.Element("Controller")!.Elements("DataTypes").Elements("DataType") :
                e.Parent?.Parent == program ? outputProgram.Elements("Tags").Elements("Tag") : output.Document.Root!.Element("Controller")!.Elements("Tags").Elements("Tag");
            if (source.Raw(e) != output.Raw(Named(candidates, Name(e)))) throw Error("preservation", "A selected definition changed during packaging.");
        }
        var schemaReport = new ProgramImportSchemaReport("structure_only", null, true, false, [], []);
        if (request.Schema is { } schemaRequest)
        {
            if (schemaBytes is null || !string.Equals(ProjectService.Hash(schemaBytes), schemaRequest.Sha256, StringComparison.OrdinalIgnoreCase)) throw Error("schema", "Bind exact schema bytes and SHA-256.");
            schemaReport = LogixImportSchema.Validate(output.Document, schemaBytes, schemaRequest.Scope);
            if (!schemaReport.Valid) throw Error("schema", string.Join("\n", schemaReport.Errors));
        }
        else if (schemaBytes is not null) throw Error("schema", "Schema bytes require an explicit hash and validation scope.");
        var definitions = routines.Select(r => Definition(source, r, "routine", changed.Contains(Name(r)) ? "changed" : "retained", ContentCount(r)))
            .Concat(tags.Select(t => Definition(source, t, "local_tag", oldTags.Any(o => Same(o, Name(t))) ? "existing_preserve_values" : "new_initial_values")))
            .Concat(globals.Select(t => Definition(source, t, "controller_tag", "new_initial_values")))
            .Concat(types.Select(t => Definition(source, t, "datatype", "new_definition"))).ToArray();
        string[] steps = [
            "Review source/baseline hashes, all included routines/local tags and the separately counted changed routine set. Overwrite omissions can delete destination objects; no omitted baseline objects are allowed here.",
            "Import the whole Program with Operation=Overwrite and the exact existing name. Resolve existing external tags/types/AOIs from the destination; create only the listed new controller tags/datatypes. Do not substitute a rung insertion operation.",
            "Keep Preserve existing tag values in offline project selected. Existing online data-table values remain current by the native import contract; new tags use imported initial values. Renames/layout conversion and non-data-table configuration require separate review.",
            "Use the exact recorded task schedule and main/fault routine assignments. For an authorized no-test-interval native workflow, select Finalize All Edits In Program during import; this package performs no finalization or native call. Pending/accepted/testing edits and native correlation must be resolved separately.",
            "Export/read back the resulting program, routine/local-tag set, selected new dependencies, unchanged controller payloads and task schedule. Compare saved values offline; live preservation/runtime behavior needs its own observation.",
            "A new UDT addition does not inherently require download. An existing referenced UDT cannot be overwritten online; existing unreferenced UDT overwrite and native compatibility have separate restrictions. This profile packages no existing UDT overwrites."
        ];
        var manifest = new ProgramImportManifest(1, "whole_program_import", request.Source, request.Baseline, request.ProgramName, request.TaskName, controllerName,
            ProjectService.Hash(xml), Hash(programRaw), changed.Length, routines.Length, tags.Length, definitions, globals.Select(Name).ToArray(), types.Select(Name).ToArray(),
            Canonical(task), new(true, "Existing online data-table values are preserved by the native import contract, not observed by this package.", "New tags receive imported initial values; subsequent running logic can change them."),
            schemaReport, steps, "Explicit new controller tag/type selection with datatype checks and lexical direct RLL/ST/local-alias references. Existing external symbols resolve from the destination. No compiler-bound FBD/SFC/AOI/alias/indirect/reference closure is claimed.", Boundary);
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, ProjectService.Json);
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
        {
            void Add(string name, byte[] bytes) { var entry = archive.CreateEntry(name); entry.LastWriteTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); using var stream = entry.Open(); stream.Write(bytes); }
            Add("program.l5x", xml); Add("manifest.json", json); Add("README.txt", Encoding.UTF8.GetBytes(string.Join("\n\n", steps) + "\n\n" + Boundary + "\n"));
        }
        return new(xml, json, zip.ToArray(), manifest);
    }

    public static ProgramImportReadback Compare(ProgramImportManifest manifest, byte[] sourceBytes, byte[] baselineBytes, byte[] readbackBytes, string valueMode = "offline_preserved")
    {
        if (manifest.Kind != "whole_program_import" || manifest.SchemaVersion != 1 || !manifest.PackagingOnly || manifest.NativeImportAccepted ||
            !string.Equals(ProjectService.Hash(sourceBytes), manifest.Source.Sha256, StringComparison.OrdinalIgnoreCase) || !string.Equals(ProjectService.Hash(baselineBytes), manifest.Baseline.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Error("receipt", "Use an intact whole-program packaging manifest and its exact original snapshots.");
        if (valueMode is not ("offline_preserved" or "online_declarations")) throw Error("value_mode", "Use offline_preserved or online_declarations; online mode does not verify current live values.");
        var source = new LogixLexicalDocument(sourceBytes); var baseline = new LogixLexicalDocument(baselineBytes); var actual = new LogixLexicalDocument(readbackBytes);
        var a = source.Document.Root!.Element("Controller")!; var b = baseline.Document.Root!.Element("Controller")!; var c = actual.Document.Root!.Element("Controller")!;
        if ((string?)c.Attribute("Name") != manifest.ControllerName ||
            (string?)actual.Document.Root.Attribute("SoftwareRevision") != (string?)baseline.Document.Root.Attribute("SoftwareRevision"))
            throw Error("context", "Readback controller/software version differs; conversion is not inferred.");
        var expected = Named(a.Elements("Programs").Elements("Program"), manifest.ProgramName); var old = Named(b.Elements("Programs").Elements("Program"), manifest.ProgramName);
        var result = Named(c.Elements("Programs").Elements("Program"), manifest.ProgramName); var differences = new List<string>();
        foreach (var routine in result.Elements("Routines").Elements("Routine")) CheckRoutine(routine);
        var expectedRoutines = Unique(expected, "Routines", "Routine"); var expectedTags = Unique(expected, "Tags", "Tag");
        var readRoutines = Unique(result, "Routines", "Routine"); var readTags = Unique(result, "Tags", "Tag");
        bool Set(XElement[] left, XElement[] right, Func<XElement, string> hash) => left.Length == right.Length && left.All(e => right.SingleOrDefault(r => Same(r, Name(e))) is { } r && hash(e) == hash(r));
        var routinesMatch = Set(expectedRoutines, readRoutines, Canonical);
        var tagsMatch = Set(expectedTags, readTags, e => Layout(e, false));
        XElement Header(XElement p) => new(p.Name, p.Attributes().Select(x => new XAttribute(x)), p.Elements().Where(e => e.Name != "Tags" && e.Name != "Routines").Select(e => new XElement(e)));
        var header = Canonical(Header(expected)) == Canonical(Header(result));
        var dependencies = true;
        foreach (var (container, name, names) in new[] { ("Tags", "Tag", manifest.NewControllerTags), ("DataTypes", "DataType", manifest.NewDataTypes) })
            foreach (var n in names)
            {
                var e = Named(a.Elements(container).Elements(name), n); var r = c.Elements(container).Elements(name).SingleOrDefault(x => Same(x, n));
                if (r is null || (name == "Tag" ? Layout(e, false) != Layout(r, false) : Canonical(e) != Canonical(r))) dependencies = false;
            }
        var task = c.Elements("Tasks").Elements("Task").SingleOrDefault(t => Same(t, manifest.TaskName));
        var schedule = task is not null && Canonical(task) == manifest.BaselineTaskSha256;
        XElement Other(XElement controller, bool imported)
        {
            var copy = new XElement(controller); copy.Elements("Programs").Elements("Program").Where(p => Same(p, manifest.ProgramName)).Remove();
            if (imported)
            {
                copy.Elements("Tags").Elements("Tag").Where(t => manifest.NewControllerTags.Contains(Name(t), StringComparer.OrdinalIgnoreCase)).Remove();
                copy.Elements("DataTypes").Elements("DataType").Where(t => manifest.NewDataTypes.Contains(Name(t), StringComparer.OrdinalIgnoreCase)).Remove();
            }
            foreach (var e in copy.Elements().Where(e => e.Name is { LocalName: "Tags" or "DataTypes" } && !e.HasElements).ToArray()) e.Remove();
            if (valueMode == "online_declarations") foreach (var tag in copy.Descendants("Tag")) RemoveData(tag);
            return copy;
        }
        var other = Canonical(Other(b, false)) == Canonical(Other(c, true));
        bool? values = null;
        if (valueMode == "offline_preserved")
        {
            values = expectedTags.All(t => readTags.SingleOrDefault(r => Same(r, Name(t))) is { } r && Data(old.Elements("Tags").Elements("Tag").SingleOrDefault(o => Same(o, Name(t))) ?? t) == Data(r));
            foreach (var name in manifest.NewControllerTags)
                values &= c.Elements("Tags").Elements("Tag").SingleOrDefault(t => Same(t, name)) is { } r && Data(Named(a.Elements("Tags").Elements("Tag"), name)) == Data(r);
        }
        if (!header) differences.Add("program_metadata"); if (!routinesMatch) differences.Add("routine_set_or_exact_content");
        if (!tagsMatch) differences.Add("local_tag_set_or_declarations"); if (!dependencies) differences.Add("new_controller_tag_or_datatype_definitions");
        if (!schedule) differences.Add("task_schedule"); if (!other) differences.Add("unrelated_controller_payload_or_existing_values");
        if (values == false) differences.Add("saved_tag_values");
        return new(header, routinesMatch, tagsMatch, dependencies, schedule, other, values, differences.Count == 0, differences.ToArray(),
            Boundary + (valueMode == "online_declarations" ? " Live-value preservation is not verified by declaration-only readback." : " Values are compared to saved snapshots; this is not a live-state observation."));
    }
    private static ProgramImportDefinition Definition(LogixLexicalDocument doc, XElement e, string kind, string disposition, int? count = null)
        => new(kind, Name(e), Hash(doc.Raw(e)), Canonical(e), disposition, count);
    private static int ContentCount(XElement routine) => routine.Elements().Where(e => e.Name.LocalName.EndsWith("Content", StringComparison.Ordinal)).Sum(e => e.Elements().Count());
    private static void CheckRoutine(XElement routine)
    {
        if ((string?)routine.Attribute("Type") is not ("RLL" or "ST" or "FBD" or "SFC") || routine.DescendantsAndSelf().Any(e => e.Name.LocalName.Contains("Encoded", StringComparison.OrdinalIgnoreCase)) ||
            routine.DescendantsAndSelf().Attributes().Any(a => a.Name.LocalName is "SourceKey" or "SourceProtectionType" || a.Name == "OnlineEditType" && a.Value != "Original"))
            throw Error("routine_state", "Use readable normal/finalized routine exports. Pending/test/unknown edit variants and protected content are not selected, stripped or finalized by packaging.");
        var contents = routine.Elements().Where(e => e.Name.LocalName.EndsWith("Content", StringComparison.Ordinal)).ToArray();
        if (contents.Length > 1) throw Error("routine_state", "Export one normal/finalized content variant per routine, not competing edit containers.");
    }
    private static XElement[] Unique(XElement parent, string container, string child)
    {
        if (parent.Elements(container).Count() > 1 || parent.Elements(container).Elements().Any(e => e.Name != child)) throw Error("shape", "Ambiguous " + container + " container.");
        var result = parent.Elements(container).Elements(child).ToArray();
        foreach (var e in result) OnlineEdits.ProgramPath(Name(e));
        if (result.Select(Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length) throw Error("shape", "Duplicate definitions.");
        return result;
    }
    private static XElement[] SelectNew(string[]? names, XElement source, XElement baseline, string container, string child)
    {
        var selected = names ?? []; var distinct = Names(selected);
        if (selected.Length > 256 || distinct.Count != selected.Length) throw Error("selection", "Select at most 256 distinct explicit new definitions per kind.");
        return selected.Select(name =>
        {
            if (baseline.Elements(container).Elements(child).Any(t => Same(t, name))) throw Error("dependency_existing", "Select only new " + child + " definitions; existing destination payloads are excluded: " + name);
            return Named(source.Elements(container).Elements(child), name);
        }).ToArray();
    }
    private static HashSet<string> Names(IEnumerable<string> names)
    { var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var n in names) { OnlineEdits.ProgramPath(n); result.Add(n); } return result; }
    private static void CheckNoOmissions(XElement[] old, XElement[] current, string kind)
    { foreach (var e in old) if (!current.Any(c => Same(c, Name(e)))) throw Error("omission", "Whole-program overwrite would delete an omitted " + kind + ": " + Name(e)); }
    private static XElement Named(IEnumerable<XElement> elements, string name)
    { var matches = elements.Where(e => Same(e, name)).ToArray(); return matches.Length == 1 ? matches[0] : throw Error("context", "Expected one exact named definition: " + name); }
    private static string Name(XElement e) => (string?)e.Attribute("Name") ?? throw Error("shape", "A named definition is required.");
    private static bool Same(XElement e, string name) => string.Equals((string?)e.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase);
    private static string Hash(string text) => ProjectService.Hash(Encoding.UTF8.GetBytes(text));
    private static bool ForcedOrSafety(XElement tag) => string.Equals((string?)tag.Attribute("Class"), "Safety", StringComparison.OrdinalIgnoreCase) ||
        tag.Descendants("ForceData").Any() || tag.DescendantsAndSelf().Attributes().Any(a => a.Name.LocalName == "ForceValue");
    private static void RemoveData(XElement tag) => tag.Elements().Where(e => e.Name.LocalName is "Data" or "DefaultData" or "ForceData").Remove();
    private static string Layout(XElement tag, bool ignoreComments)
    {
        var copy = new XElement(tag); RemoveData(copy);
        if (ignoreComments) copy.Elements().Where(e => e.Name.LocalName is "Description" or "Comments").Remove();
        return Canonical(copy);
    }
    private static string TypeLayout(XElement type)
    { var copy = new XElement(type); copy.Descendants("Description").Remove(); return Canonical(copy); }
    private static string Data(XElement tag) => Hash(string.Concat(tag.Elements().Where(e => e.Name.LocalName is "Data" or "DefaultData" or "ForceData").Select(e => ProjectComparison.Normalize(e, true).ToString(SaveOptions.DisableFormatting))));
    private static string Canonical(XElement e)
    {
        var copy = new XElement(e);
        copy.DescendantsAndSelf().Attributes("Use").Remove();
        copy.DescendantsAndSelf().Attributes("OnlineEditType").Where(a => a.Value == "Original").Remove();
        return Hash(ProjectComparison.Normalize(copy, true).ToString(SaveOptions.DisableFormatting));
    }
    private static OperationException Error(string suffix, string message) => new("program_import_" + suffix, message);
}

public sealed partial class ProjectService
{
    public async Task<ProgramImportPackage> PrepareProgramImportPackageAsync(ProgramImportPackageRequest request, CancellationToken ct = default)
    {
        if (request.Source is null || request.Baseline is null) throw new OperationException("program_import_source", "Pin source and baseline snapshots.");
        using (await EnterWorkspaceAsync(request.Source.ProjectId, ct))
        {
            var source = PreservationSource(request.Source); var baseline = PreservationSource(request.Baseline); byte[]? schema = null;
            if (request.Schema is { } selected)
            {
                var path = AllowedSource(selected.Path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > LogixImportSchema.MaximumSchemaBytes) throw new OperationException("program_import_schema_limit", "XSD exceeds 4 MiB.");
                var bytes = new byte[(int)stream.Length]; await stream.ReadExactlyAsync(bytes, ct);
                if (stream.ReadByte() != -1) throw new OperationException("program_import_schema_limit", "XSD changed while reading.");
                schema = bytes;
            }
            var built = ProgramImportPackaging.Build(source.Bytes, baseline.Bytes, request, schema);
            Artifact Store(string format, byte[] bytes)
            {
                var id = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(id); Directory.CreateDirectory(dir); AtomicBytes(Path.Combine(dir, "project." + format), bytes);
                var artifact = new Artifact(id, source.Project.Id, format, Hash(bytes), bytes.Length, ProgramImportPackaging.Boundary); Save(Path.Combine(dir, "artifact.json"), artifact); return artifact;
            }
            var program = Store("l5x", built.ProgramXml); var manifest = Store("json", built.ManifestJson); var package = Store("zip", built.Zip);
            Audit("program_import_package", source.Project, source.Project.CurrentSha256, program.Sha256, package.Id);
            return new(program, manifest, package, built.Manifest);
        }
    }
    public ProgramImportReadback CheckProgramImportReadback(ProgramImportReadbackRequest request)
    {
        var artifact = Download(request.ManifestArtifactId);
        if (artifact.Artifact.Format != "json" || artifact.Artifact.Bytes > 4 * 1024 * 1024 || !string.Equals(artifact.Artifact.Sha256, request.ExpectedManifestSha256, StringComparison.OrdinalIgnoreCase))
            throw new OperationException("program_import_receipt", "Pin the exact retained program-import manifest artifact.");
        var manifest = JsonSerializer.Deserialize<ProgramImportManifest>(File.ReadAllBytes(artifact.Path), Json) ?? throw new OperationException("program_import_receipt", "Missing manifest.");
        var source = PreservationSource(manifest.Source); var baseline = PreservationSource(manifest.Baseline); var readback = PreservationSource(request.Readback);
        return ProgramImportPackaging.Compare(manifest, source.Bytes, baseline.Bytes, readback.Bytes, request.ValueMode);
    }
}
