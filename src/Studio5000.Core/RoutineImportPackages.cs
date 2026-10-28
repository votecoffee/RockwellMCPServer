using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record RoutineImportTagSelection(string Name, string Scope = "program");
public sealed record RoutineImportPackageRequest(MergeSnapshot Source, MergeSnapshot Baseline, string ProgramName,
    string RoutineName, RoutineImportTagSelection[]? PrerequisiteTags = null, string Mode = "tag_definition_free");
public sealed record RoutineImportInitialData(string Format, string Xml);
public sealed record RoutineImportTagPrerequisite(string Scope, string? ProgramName, string Name, string DataType,
    int[] Dimensions, string? SourceDimensions, Dictionary<string, string> Attributes, RoutineImportInitialData[] InitialData,
    string Delivery = "create_before_import");
public sealed record RoutineImportPrerequisites(int SchemaVersion, string Mode, ComparisonSnapshot Source,
    ComparisonSnapshot Baseline, string ProgramName, string RoutineName, RoutineImportTagPrerequisite[] Tags,
    string CreationPolicy, string[] Steps, string DependencyCoverage, bool NativeImportAccepted = false)
{
    public RoutineImportTagPrerequisite[] CreateBeforeImport => Tags.Where(t => t.Delivery == "create_before_import").ToArray();
    public RoutineImportTagPrerequisite[] BundledProgramDependencies => Tags.Where(t => t.Delivery == "bundled_program_dependency").ToArray();
    public RoutineImportTagPrerequisite[] ControllerCreateBeforeImport => CreateBeforeImport.Where(t => t.Scope == "controller").ToArray();
}
public sealed record RoutineImportValidation(string Mode, string ProgramName, string RoutineName,
    int TagDefinitions, int SavedValueElements, bool WholeRoutineTarget, bool NativeImportAccepted, string Boundary,
    int ControllerTagDefinitions = 0, int ProgramTagDefinitions = 0, int ExistingTagValueElements = 0);
public sealed record RoutineImportPackage(Artifact Routine, Artifact Prerequisites, Artifact Package,
    RoutineImportPrerequisites RequiredTags, RoutineImportValidation Validation, string ProgramXPath,
    string TargetName, string RecommendedWorkflow = "finalize_during_import", string ImportOption = "FinalizeEdits");
public sealed record RoutineImportPackageContent(byte[] RoutineXml, byte[] PrerequisitesJson, byte[] Zip,
    RoutineImportPrerequisites RequiredTags, RoutineImportValidation Validation);

public static class RoutineImportPackaging
{
    public const string TagDefinitionFree = "tag_definition_free";
    public const string ProgramOnly = "program_only";
    public const int MaximumPrerequisiteTags = 128;
    public const int MaximumRoutineBytes = 4 * 1024 * 1024;
    public const int MaximumPrerequisiteBytes = 2 * 1024 * 1024;
    public const string Boundary = "Offline RLL/ST package checks only; no native import/compiler or controller acceptance. Controller definitions and existing saved tag values are excluded. program_only can bundle selected new program tags/initial data; tag_definition_free excludes all tags/data. Logic constants/comments remain. Never initialize or overwrite an existing tag.";
    private static readonly HashSet<string> ValueElements = new(StringComparer.OrdinalIgnoreCase)
        { "Data", "DefaultData", "ForceData", "DataValue", "DataValueMember", "Array", "ArrayMember", "Structure", "StructureMember", "Element", "RawData" };

    public static RoutineImportPackageContent Build(ProjectInfo source, byte[] sourceBytes, ProjectInfo baseline,
        byte[] baselineBytes, RoutineImportPackageRequest request)
    {
        if (request.Mode is not (TagDefinitionFree or ProgramOnly)) throw Error("mode", "Use tag_definition_free or program_only.");
        OnlineEdits.RoutinePath(request.ProgramName, request.RoutineName);
        if (source.Kind != "logix" || baseline.Kind != "logix" || request.Source is null || request.Baseline is null ||
            source.Id != request.Source.ProjectId || baseline.Id != request.Baseline.ProjectId ||
            !ProjectService.Hash(sourceBytes).Equals(request.Source.Sha256, StringComparison.OrdinalIgnoreCase) || !ProjectService.Hash(baselineBytes).Equals(request.Baseline.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Error("source", "Bind exact managed source and baseline Logix snapshots and hashes.");
        var doc = XmlProjects.Parse(sourceBytes); var before = XmlProjects.Parse(baselineBytes);
        LogixLexicalDocument.ValidateBudget(doc); LogixLexicalDocument.ValidateBudget(before);
        var controller = doc.Root!.Element("Controller")!; var originalController = before.Root!.Element("Controller")!;
        var controllerName = (string?)controller.Attribute("Name");
        if (string.IsNullOrWhiteSpace(controllerName) || !string.Equals(controllerName, (string?)originalController.Attribute("Name"), StringComparison.OrdinalIgnoreCase))
            throw Error("context", "Source and baseline must identify the same controller; no controller conversion or correlation is inferred.");
        var program = Named(controller.Elements("Programs").Elements("Program"), request.ProgramName);
        var originalProgram = Named(originalController.Elements("Programs").Elements("Program"), request.ProgramName);
        var routine = Named(program.Elements("Routines").Elements("Routine"), request.RoutineName);
        if ((string?)program.Attribute("Name") != request.ProgramName || (string?)routine.Attribute("Name") != request.RoutineName)
            throw Error("context", "Use the source program/routine names with their exact spelling; this mode does not rename targets.");
        if ((string?)program.Attribute("Class") == "Safety" || (string?)originalProgram.Attribute("Class") == "Safety" || (string?)routine.Attribute("Class") == "Safety")
            throw Error("protected", "This package profile does not prepare safety routines.");
        if ((string?)routine.Attribute("Type") is not ("RLL" or "ST") || routine.DescendantsAndSelf().Any(e => e.Name.LocalName.Contains("Encoded", StringComparison.OrdinalIgnoreCase)) ||
            routine.Attributes().Any(a => a.Name.LocalName is "SourceKey" or "SourceProtectionType"))
            throw Error("protected", "Select readable, unprotected RLL or ST; opaque and other routine formats need their own preservation/import profile.");
        var root = doc.Root;
        var schema = (string?)root.Attribute("SchemaRevision"); var software = (string?)root.Attribute("SoftwareRevision");
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(software)) throw Error("context", "Preserve a source export's SchemaRevision and SoftwareRevision; do not guess an import version.");
        var copy = new XElement(routine); copy.SetAttributeValue("Use", "Target");
        var output = new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("RSLogix5000Content", new XAttribute("SchemaRevision", schema), new XAttribute("SoftwareRevision", software),
                new XAttribute("TargetType", "Routine"), new XAttribute("TargetName", request.RoutineName), new XAttribute("TargetCount", "1"), new XAttribute("ContainsContext", "true"),
                new XElement("Controller", new XAttribute("Use", "Context"), new XAttribute("Name", controllerName),
                    new XElement("Programs", new XAttribute("Use", "Context"), new XElement("Program", new XAttribute("Use", "Context"), new XAttribute("Name", request.ProgramName),
                        new XElement("Routines", new XAttribute("Use", "Context"), copy))))));

        var selections = request.PrerequisiteTags ?? [];
        if (selections.Length > MaximumPrerequisiteTags || selections.Any(s => s is null || s.Scope is not ("program" or "controller")) ||
            selections.Select(s => s.Scope + ":" + s.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selections.Length)
            throw Error("selection", "Select up to 128 distinct explicit program/controller tag prerequisites.");
        var tags = new List<RoutineImportTagPrerequisite>();
        var bundled = new List<XElement>();
        foreach (var selection in selections)
        {
            OnlineEdits.ProgramPath(selection.Name); // Same bounded identifier grammar; no selectors/member paths.
            var local = selection.Scope == "program";
            var tag = Named((local ? program : controller).Elements("Tags").Elements("Tag"), selection.Name);
            if ((local ? originalProgram : originalController).Elements("Tags").Elements("Tag").Any(t => SameName(t, selection.Name)))
                throw Error("existing_tag", "A selected prerequisite already exists in the baseline. Exclude it: existing values must never enter the prerequisite manifest.");
            if ((string?)tag.Attribute("TagType") != "Base" || tag.Attribute("AliasFor") is not null || string.IsNullOrWhiteSpace((string?)tag.Attribute("DataType")) || tag.Descendants().Any(e => e.Name.LocalName.Equals("ForceData", StringComparison.OrdinalIgnoreCase)) ||
                (string?)tag.Attribute("Class") == "Safety" || tag.DescendantsAndSelf().Attributes().Any(a => a.Name.LocalName == "ForceValue"))
                throw Error("tag", "Prerequisites require unforced Base tags with an explicit data type; aliases/produced/consumed tags need a separate workflow.");
            var data = tag.Elements("Data").Select(d => new RoutineImportInitialData((string?)d.Attribute("Format") ?? "Raw", d.ToString(SaveOptions.DisableFormatting))).ToArray();
            if (data.Length == 0 || data.Any(d => string.IsNullOrWhiteSpace(d.Format)) || data.Select(d => d.Format).Distinct(StringComparer.Ordinal).Count() != data.Length)
                throw Error("initial_data", "Every new prerequisite needs exact, uniquely formatted saved initial Data representations. Missing values are not guessed.");
            var dimensions = (string?)tag.Attribute("Dimensions");
            var attributes = tag.Attributes().Where(a => a.Name.LocalName is "Radix" or "Constant" or "ExternalAccess")
                .ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal);
            tags.Add(new(selection.Scope, local ? request.ProgramName : null, (string)tag.Attribute("Name")!,
                (string)tag.Attribute("DataType")!, Dimensions(dimensions), dimensions, attributes, data,
                request.Mode == ProgramOnly && local ? "bundled_program_dependency" : "create_before_import"));
            if (request.Mode == ProgramOnly && local)
            {
                // Referenced program tags are sibling dependencies of the target routine,
                // not Routine children or controller Tags. No existing definitions are copied.
                var dependency = new XElement(tag); dependency.Attribute("Use")?.Remove(); bundled.Add(dependency);
            }
        }
        if (bundled.Count > 0) output.Descendants("Program").Single().Element("Routines")!.AddBeforeSelf(new XElement("Tags", bundled));
        var validation = Validate(output, request.ProgramName, request.RoutineName, request.Mode, before);
        var xml = Serialize(output);
        if (xml.Length > MaximumRoutineBytes) throw Error("limit", "The routine import exceeds 4 MiB.");
        using (var reader = XmlReader.Create(new MemoryStream(xml), new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            LadderBranchGrammar.ValidateImport(reader);
        string[] steps =
        [
            "Review the pinned source/baseline and explicit prerequisite selection. Verify remaining UDT, AOI, alias and indirect dependencies separately.",
            "Create and verify only CreateBeforeImport tags still missing in their exact scopes, with the listed type/dimensions/initial Data. If already present, preserve current values and validate layout; never reinitialize an existing tag.",
            request.Mode == ProgramOnly ? "Selected new program helpers are bundled in Program/Tags. Verify they are still missing in the current destination before import; if any now exist, refresh the baseline and regenerate without them, or use tag_definition_free. Controller parameters remain separate create-first prerequisites." : "Complete and verify all missing tag creation before import; this fallback contains no tag definitions or saved data.",
            $"Use importKind=routine, routinePackagingMode={request.Mode} and workflow=finalize_during_import. program_only planning also requires the pinned routineTagBaseline. Target the parent program XPath and exact routine targetName to replace the full routine; keep a matching ACD and stop on correlation mismatch.",
            "Select FinalizeEdits on that initial import when this no-test-interval workflow is authorized. Do not change controller mode or retry an uncertain import to force finalization.",
            "Verify native import/readback, whole-routine rung/line count, resulting edit state and saved-value preservation separately. XML checks and SDK return alone do not prove native or functional acceptance."
        ];
        var manifest = new RoutineImportPrerequisites(1, request.Mode,
            new(source.Id, source.Name, source.Revision, request.Source.Sha256), new(baseline.Id, baseline.Name, baseline.Revision, request.Baseline.Sha256),
            request.ProgramName, request.RoutineName, tags.ToArray(), "create_missing_only_never_write_existing", steps,
            "Explicit caller-selected new tags, absent in the pinned baseline; not a complete compiler-bound dependency closure.");
        var json = JsonSerializer.SerializeToUtf8Bytes(manifest, ProjectService.Json);
        if (json.Length > MaximumPrerequisiteBytes) throw Error("limit", "The separate prerequisite manifest exceeds 2 MiB.");
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
        {
            void Add(string name, byte[] bytes) { var entry = archive.CreateEntry(name); entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); using var stream = entry.Open(); stream.Write(bytes); }
            Add("routine.l5x", xml); Add("prerequisites.json", json);
            Add("README.txt", Encoding.UTF8.GetBytes(string.Join("\n\n", steps) + "\n\n" + Boundary + "\n"));
        }
        return new(xml, json, zip.ToArray(), manifest, validation);
    }

    public static RoutineImportValidation ValidateTagFree(XDocument doc, string programName, string routineName)
        => Validate(doc, programName, routineName, TagDefinitionFree);

    public static RoutineImportValidation ValidateProgramOnly(XDocument doc, string programName, string routineName, XDocument baseline)
        => Validate(doc, programName, routineName, ProgramOnly, baseline);

    private static RoutineImportValidation Validate(XDocument doc, string programName, string routineName, string mode, XDocument? baseline = null)
    {
        OnlineEdits.RoutinePath(programName, routineName);
        var tags = doc.Descendants().Count(e => e.Name.LocalName.Equals("Tag", StringComparison.OrdinalIgnoreCase));
        var values = doc.Descendants().Count(e => ValueElements.Contains(e.Name.LocalName));
        if (mode == TagDefinitionFree && (tags != 0 || values != 0 || doc.Descendants().Any(e => e.Name.LocalName.Equals("Tags", StringComparison.OrdinalIgnoreCase))))
            throw Error("payload", "tag_definition_free rejects every Tag definition, Tags container and saved-value representation, including namespaced or nested elements.");
        var root = doc.Root; var controller = root?.Elements("Controller").FirstOrDefault();
        var program = controller?.Elements("Programs").Elements("Program").FirstOrDefault();
        var routine = program?.Elements("Routines").Elements("Routine").FirstOrDefault();
        if (root?.Name != "RSLogix5000Content" || (string?)root.Attribute("TargetType") != "Routine" || (string?)root.Attribute("TargetName") != routineName ||
            (string?)root.Attribute("TargetCount") != "1" || (string?)root.Attribute("ContainsContext") != "true" ||
            controller is null || (string?)controller.Attribute("Use") != "Context" || program is null || (string?)program.Attribute("Use") != "Context" ||
            (string?)program.Attribute("Name") != programName || routine is null || (string?)routine.Attribute("Name") != routineName || (string?)routine.Attribute("Use") != "Target" ||
            (string?)routine.Attribute("Type") is not ("RLL" or "ST") || root.Elements().Count() != 1 || controller.Elements().Count() != 1 ||
            program.Elements().Any(e => e.Name != "Routines" && (mode != ProgramOnly || e.Name != "Tags")) || program.Elements("Routines").Count() != 1 ||
            controller.Elements("Programs").Elements().Count() != 1 || program.Elements("Routines").Elements().Count() != 1)
            throw Error("payload", "Use one full RLL/ST Routine target with only its exact controller/program context; no other component definitions are allowed.");
        var content = (string?)routine.Attribute("Type") == "RLL" ? "RLLContent" : "STContent";
        if (routine.Elements(content).Count() != 1 || routine.Elements().Any(e => e.Name != content && e.Name != "Description"))
            throw Error("payload", "Only the full readable routine content and its Description belong in this package.");
        if (routine.Descendants().Any(e => e.Name.LocalName.Contains("Encoded", StringComparison.OrdinalIgnoreCase)) ||
            (string?)program.Attribute("Class") == "Safety" || (string?)routine.Attribute("Class") == "Safety" ||
            routine.Attributes().Any(a => a.Name.LocalName is "SourceKey" or "SourceProtectionType"))
            throw Error("protected", "Opaque, protected or safety routine content is outside this package profile.");
        var dependencies = program.Elements("Tags").Elements("Tag").ToArray();
        if (mode == ProgramOnly)
        {
            if (baseline is null || baseline.Root?.Name != "RSLogix5000Content") throw Error("baseline", "program_only validation requires the exact destination baseline snapshot.");
            var baseController = baseline.Root.Element("Controller");
            if (baseController is null || string.IsNullOrWhiteSpace((string?)controller.Attribute("Name")) ||
                !string.Equals((string?)controller.Attribute("Name"), (string?)baseController.Attribute("Name"), StringComparison.OrdinalIgnoreCase))
                throw Error("baseline", "Controller context differs from the pinned baseline.");
            var baseProgram = Named(baseController.Elements("Programs").Elements("Program"), programName);
            if ((string?)baseProgram.Attribute("Class") == "Safety") throw Error("protected", "Safety destinations are outside this package profile.");
            var containers = doc.Descendants().Where(e => e.Name.LocalName.Equals("Tags", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (program.Elements("Tags").Count() > 1 || containers.Length != program.Elements("Tags").Count() || tags != dependencies.Length ||
                dependencies.Length > MaximumPrerequisiteTags || program.Elements("Tags").Elements().Count() != dependencies.Length ||
                doc.Descendants().Where(e => ValueElements.Contains(e.Name.LocalName)).Any(e => !e.Ancestors("Tag").Any(t => dependencies.Contains(t))))
                throw Error("payload", "program_only permits only direct Program/Tags/Tag dependencies and their new initial data; controller, nested and other saved-value payloads are rejected.");
            foreach (var dependency in dependencies)
            {
                var name = (string?)dependency.Attribute("Name") ?? ""; OnlineEdits.ProgramPath(name);
                if (baseProgram.Elements("Tags").Elements("Tag").Any(t => SameName(t, name))) throw Error("existing_tag", "A bundled program tag already exists in the baseline; preserve it and exclude its definition/values.");
                if ((string?)dependency.Attribute("TagType") != "Base" || dependency.Attribute("AliasFor") is not null || string.IsNullOrWhiteSpace((string?)dependency.Attribute("DataType")) ||
                    dependency.Descendants().Any(e => e.Name.LocalName.Equals("ForceData", StringComparison.OrdinalIgnoreCase)) || (string?)dependency.Attribute("Class") == "Safety" ||
                    dependency.DescendantsAndSelf().Attributes().Any(a => a.Name.LocalName == "ForceValue") || !dependency.Elements("Data").Any())
                    throw Error("tag", "Bundled dependencies must be unforced new Base program tags with exact initial data.");
                var formats = dependency.Elements("Data").Select(d => (string?)d.Attribute("Format") ?? "Raw").ToArray();
                if (formats.Any(string.IsNullOrWhiteSpace) || formats.Distinct(StringComparer.Ordinal).Count() != formats.Length)
                    throw Error("initial_data", "Initial Data representations must have distinct, nonempty formats.");
                Dimensions((string?)dependency.Attribute("Dimensions"));
            }
            if (dependencies.Select(t => (string)t.Attribute("Name")!).Distinct(StringComparer.OrdinalIgnoreCase).Count() != dependencies.Length)
                throw Error("selection", "Bundled program tag names must be unique.");
        }
        return new(mode, programName, routineName, tags, values, true, false, Boundary, 0, dependencies.Length, 0);
    }
    private static XElement Named(IEnumerable<XElement> values, string name)
    { var matches = values.Where(e => SameName(e, name)).ToArray(); return matches.Length == 1 ? matches[0] : throw Error("context", "Each selected program/routine/tag name must match exactly once in its declared scope."); }
    private static bool SameName(XElement element, string name) => string.Equals((string?)element.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase);
    private static int[] Dimensions(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim() == "0") return [];
        var pieces = value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries); var result = new List<int>();
        if (pieces.Length is < 1 or > 3) throw Error("dimensions", "Use scalar Dimensions=0 or one to three explicit positive array dimensions.");
        foreach (var piece in pieces)
        { if (!int.TryParse(piece, NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size <= 0) throw Error("dimensions", "Array dimensions must be positive exact integers."); result.Add(size); }
        return result.ToArray();
    }
    private static byte[] Serialize(XDocument doc)
    {
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, new() { Encoding = new UTF8Encoding(false), Indent = false, NewLineHandling = NewLineHandling.None })) doc.Save(writer);
        return stream.ToArray();
    }
    private static OperationException Error(string suffix, string message) => new("routine_import_" + suffix, message);
}

public sealed partial class ProjectService
{
    public async Task<RoutineImportPackage> PrepareRoutineImportPackageAsync(RoutineImportPackageRequest request, CancellationToken ct = default)
    {
        if (request.Source is null || request.Baseline is null) throw new OperationException("routine_import_source", "Bind source and baseline snapshots.");
        using (await EnterWorkspaceAsync(request.Source.ProjectId, ct))
        {
            var source = PreservationSource(request.Source); var baseline = PreservationSource(request.Baseline);
            var built = RoutineImportPackaging.Build(source.Project, source.Bytes, baseline.Project, baseline.Bytes, request);
            Artifact Store(string format, byte[] bytes)
            {
                var id = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(id); Directory.CreateDirectory(dir);
                AtomicBytes(Path.Combine(dir, "project." + format), bytes);
                var artifact = new Artifact(id, source.Project.Id, format, Hash(bytes), bytes.Length, RoutineImportPackaging.Boundary);
                Save(Path.Combine(dir, "artifact.json"), artifact); return artifact;
            }
            var routine = Store("l5x", built.RoutineXml); var prerequisite = Store("json", built.PrerequisitesJson); var package = Store("zip", built.Zip);
            Audit("routine_import_package", source.Project, source.Project.CurrentSha256, routine.Sha256, package.Id);
            return new(routine, prerequisite, package, built.RequiredTags, built.Validation, OnlineEdits.ProgramPath(request.ProgramName), request.RoutineName);
        }
    }
}
