using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record TypeDependencyPackageRequest(MergeSnapshot Source, MergeSnapshot Baseline, string? ProgramName = null,
    string[]? ControllerTags = null, string[]? DataTypes = null);
public sealed record TypeDependencyDefinition(string Name, string SourceRawSha256, string CanonicalSha256,
    string[] References, string DestinationOperation, bool ExistingDestinationDefinition);
public sealed record TypeDependencyReference(string Scope, string Name, string SourceRawSha256, string? DataType, string? AliasFor);
public sealed record TypeDependencyClosure(MergeSnapshot Source, MergeSnapshot Baseline, string[] Roots,
    TypeDependencyDefinition[] Definitions, TypeDependencyReference[] TagReferences, string[] BuiltInTypes,
    bool CompleteForDeclaredSourceGraph, bool NativePreconfigurationAccepted, string Boundary);
public sealed record TypeDependencyContent(byte[]? TypesXml, byte[] ManifestJson, byte[] Zip, TypeDependencyClosure Closure);
public sealed record TypeDependencyPackage(Artifact? Types, Artifact Manifest, Artifact Package, TypeDependencyClosure Closure);

public static class LogixTypeDependencies
{
    public const string SourceClosure = "source_type_closure";
    public const string SelectedNewOnly = "selected_new_only";
    public const string Boundary = "Complete source-bound declared datatype/member/alias and direct RLL/ST reference graph only. Destination existence is not a substitute for a source definition or native temporary-project preconfiguration evidence. No tag values, native job or import is executed. Fragment XSD and matching readback never establish native preconfiguration/import acceptance or the cause of a reported failure.";

    internal static TypeDependencyClosure Resolve(LogixLexicalDocument source, LogixLexicalDocument baseline,
        TypeDependencyPackageRequest request)
    {
        if (request.Source is null || request.Baseline is null) throw Error("source", "Pin source and destination snapshots.");
        var controller = source.Document.Root!.Element("Controller")!; var destination = baseline.Document.Root!.Element("Controller")!;
        if (string.IsNullOrWhiteSpace((string?)controller.Attribute("Name")) || string.IsNullOrWhiteSpace((string?)source.Document.Root.Attribute("SoftwareRevision")) ||
            (string?)controller.Attribute("Name") != (string?)destination.Attribute("Name") ||
            (string?)source.Document.Root.Attribute("SoftwareRevision") != (string?)baseline.Document.Root!.Attribute("SoftwareRevision"))
            throw Error("source", "Controller and software version must match; no conversion is inferred.");
        var definitions = controller.Elements("DataTypes").Elements("DataType").ToDictionary(Name, StringComparer.OrdinalIgnoreCase);
        var existing = destination.Elements("DataTypes").Elements("DataType").ToDictionary(Name, StringComparer.OrdinalIgnoreCase);
        var aois = controller.Elements("AddOnInstructionDefinitions").Elements("AddOnInstructionDefinition").Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var destinationAois = destination.Elements("AddOnInstructionDefinitions").Elements("AddOnInstructionDefinition").Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var globals = controller.Elements("Tags").Elements("Tag").ToDictionary(Name, StringComparer.OrdinalIgnoreCase);
        var programs = controller.Elements("Programs").Elements("Program").ToDictionary(Name, StringComparer.OrdinalIgnoreCase);
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var builtIns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var completed = new Dictionary<string, TypeDependencyDefinition>(StringComparer.OrdinalIgnoreCase); var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new Dictionary<string, TypeDependencyReference>(StringComparer.OrdinalIgnoreCase); var aliasStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void VisitType(string name)
        {
            if (completed.ContainsKey(name) || builtIns.Contains(name)) return;
            if (aois.Contains(name)) throw Error("unsupported_reference", "AOI datatype requires a reviewed native AOI/reference export, not an incomplete datatype-only substitute: " + name);
            if (!definitions.TryGetValue(name, out var definition))
            {
                if (ProgramImportPackaging.IsBuiltInType(name)) { builtIns.Add(name); return; }
                throw Error("source_missing", "Concrete source datatype definition is missing, even if the destination knows it: " + name + ". Module/AOI/unknown types need their own complete native export profile.");
            }
            OnlineEdits.ProgramPath(name);
            if (destinationAois.Contains(name)) throw Error("destination_conflict", "Datatype name collides with an existing destination AOI: " + name);
            if (visiting.Count >= 32 || !visiting.Add(name)) throw Error("cycle", "Cyclic or deeper-than-32 datatype references: " + name);
            if (completed.Count + visiting.Count > 256) throw Error("limit", "A closure supports at most 256 custom datatypes.");
            if (definition.DescendantsAndSelf().Any(e => e.Name.LocalName is "Tag" or "Tags" or "Data" or "DefaultData" or "ForceData" || e.Name.LocalName.Contains("Encoded", StringComparison.OrdinalIgnoreCase)))
                throw Error("unsupported_reference", "Type-only delivery refuses tag/value/encoded payload inside a datatype definition.");
            var dependencies = definition.Elements("Members").Elements("Member").Select(m => (string?)m.Attribute("DataType") ?? throw Error("source_missing", "A member datatype is missing.")).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            foreach (var dependency in dependencies) VisitType(dependency);
            var collision = existing.GetValueOrDefault(name);
            if (collision is not null && Layout(definition) != Layout(collision)) throw Error("destination_conflict", "Source and existing destination type layouts differ; no overwrite is authorized: " + name);
            completed.Add(name, new(Name(definition), Hash(source.Raw(definition)), Canonical(definition), dependencies,
                collision is null ? "create_new_after_native_review" : "use_existing_never_overwrite", collision is not null));
            visiting.Remove(name);
        }
        void Tag(XElement tag, string scope)
        {
            var key = scope + ":" + Name(tag);
            OnlineEdits.ProgramPath(Name(tag));
            if (references.ContainsKey(key)) return;
            if (aliasStack.Count >= 32 || !aliasStack.Add(key)) throw Error("cycle", "Cyclic or deeper-than-32 alias references: " + key);
            var alias = (string?)tag.Attribute("AliasFor"); var type = (string?)tag.Attribute("DataType");
            if (alias is not null)
            {
                if (alias.Length > 4096) throw Error("limit", "Alias address exceeds 4096 characters.");
                var diagnostics = new List<string>(); var tokens = TagReferences.Lex(alias, diagnostics.Add).ToArray();
                if (tokens.Length != 1 || diagnostics.Count != 0 || tokens[0].Status != "candidate")
                    throw Error("unsupported_reference", "Alias cannot be resolved to one exact saved tag: " + key);
                var token = tokens[0]; XElement? target = null; string targetScope = "controller";
                if (token.Program is { } qualified)
                { targetScope = qualified; target = programs.GetValueOrDefault(qualified)?.Elements("Tags").Elements("Tag").SingleOrDefault(t => Same(t, token.Name)); }
                else
                {
                    if (scope != "controller") { target = programs.GetValueOrDefault(scope)?.Elements("Tags").Elements("Tag").SingleOrDefault(t => Same(t, token.Name)); if (target is not null) targetScope = scope; }
                    target ??= globals.GetValueOrDefault(token.Name);
                }
                if (target is null) throw Error("source_missing", "Alias target declaration is absent from the exact source: " + key);
                Tag(target, targetScope);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(type)) throw Error("source_missing", "Tag datatype is missing: " + key);
                roots.Add(type); VisitType(type);
            }
            if (references.Count >= 8192) throw Error("limit", "A closure supports at most 8192 tag references.");
            references.Add(key, new(scope, Name(tag), Hash(source.Raw(tag)), type, alias)); aliasStack.Remove(key);
        }
        if (request.ProgramName is { } programName)
        {
            OnlineEdits.ProgramPath(programName);
            var program = programs.GetValueOrDefault(programName) ?? throw Error("source_missing", "The source program is absent.");
            foreach (var tag in program.Elements("Tags").Elements("Tag")) Tag(tag, programName);
            var localNames = program.Elements("Tags").Elements("Tag").Select(Name).ToHashSet(StringComparer.OrdinalIgnoreCase); long characters = 0; var tokensRead = 0;
            foreach (var routine in program.Elements("Routines").Elements("Routine"))
            {
                var language = (string?)routine.Attribute("Type");
                if (language is not ("RLL" or "ST")) throw Error("unsupported_reference", "Complete direct-reference collection currently requires RLL/ST; FBD/SFC need a reviewed native reference export.");
                var texts = language == "RLL" ? routine.Elements("RLLContent").Elements("Rung").Elements("Text").Select(t => t.Value) : routine.Elements("STContent").Elements("Line").Select(t => t.Value);
                foreach (var text in texts)
                {
                    if (text.Length > 1024 * 1024 || (characters += text.Length) > 16 * 1024 * 1024) throw Error("limit", "Reference source exceeds its 1 MiB text/16 MiB aggregate budget.");
                    var diagnostics = new List<string>();
                    foreach (var token in TagReferences.Lex(text, diagnostics.Add))
                    {
                        if (++tokensRead > 100000) throw Error("limit", "Reference source exceeds 100000 candidates.");
                        if (token.Program is { } qualified)
                        {
                            var tag = programs.GetValueOrDefault(qualified)?.Elements("Tags").Elements("Tag").SingleOrDefault(t => Same(t, token.Name));
                            if (tag is null) throw Error("source_missing", "Qualified tag declaration is absent: " + qualified + "." + token.Name);
                            Tag(tag, qualified);
                        }
                        else if (!localNames.Contains(token.Name) && globals.TryGetValue(token.Name, out var global)) Tag(global, "controller");
                    }
                    if (diagnostics.Count != 0) throw Error("unsupported_reference", "Unresolved qualified/comment/string syntax prevents a complete declared reference claim.");
                }
            }
        }
        foreach (var name in Select(request.ControllerTags)) Tag(globals.GetValueOrDefault(name) ?? throw Error("source_missing", "Selected controller tag is absent: " + name), "controller");
        foreach (var name in Select(request.DataTypes)) { roots.Add(name); VisitType(name); }
        if (roots.Count == 0 && request.ProgramName is null) throw Error("selection", "Select a program, explicit controller tags or datatype roots.");
        return new(request.Source, request.Baseline, roots.ToArray(), completed.Values.ToArray(), references.Values.ToArray(), builtIns.ToArray(), true, false, Boundary);
    }

    public static TypeDependencyContent Build(byte[] sourceBytes, byte[] baselineBytes, TypeDependencyPackageRequest request)
    {
        if (request.Source is null || request.Baseline is null || !ProjectService.Hash(sourceBytes).Equals(request.Source.Sha256, StringComparison.OrdinalIgnoreCase) || !ProjectService.Hash(baselineBytes).Equals(request.Baseline.Sha256, StringComparison.OrdinalIgnoreCase))
            throw Error("source", "Bind exact source/baseline snapshot hashes.");
        var source = new LogixLexicalDocument(sourceBytes); var baseline = new LogixLexicalDocument(baselineBytes);
        var closure = Resolve(source, baseline, request); var controller = source.Document.Root!.Element("Controller")!;
        var types = closure.Definitions.Select(d => controller.Elements("DataTypes").Elements("DataType").Single(t => Same(t, d.Name))).ToArray();
        var newTypes = closure.Definitions.Where(d => !d.ExistingDestinationDefinition).ToArray(); byte[]? xml = null;
        var rawBytes = types.Sum(t => Encoding.UTF8.GetByteCount(source.Raw(t)));
        if (rawBytes > ProgramImportPackaging.MaximumOutputBytes / (newTypes.Length == 0 ? 1 : 2)) throw Error("limit", "Dependency delivery exceeds its 32 MiB aggregate budget.");
        if (newTypes.Length != 0)
        {
            var header = new XElement("RSLogix5000Content", new XAttribute("SchemaRevision", (string?)source.Document.Root.Attribute("SchemaRevision") ?? throw Error("source", "SchemaRevision is required.")),
                new XAttribute("SoftwareRevision", (string?)source.Document.Root.Attribute("SoftwareRevision") ?? throw Error("source", "SoftwareRevision is required.")),
                new XAttribute("TargetType", "DataType"), new XAttribute("TargetName", newTypes[0].Name), new XAttribute("TargetCount", newTypes.Length), new XAttribute("ContainsContext", "true"));
            var prefix = header.ToString(SaveOptions.DisableFormatting).Replace(" />", ">").Replace("/>", ">");
            string Attr(string s) => System.Security.SecurityElement.Escape(s)!;
            xml = Encoding.UTF8.GetBytes("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" + prefix + "<Controller Use=\"Context\" Name=\"" + Attr((string)controller.Attribute("Name")!) + "\"><DataTypes>" +
                string.Concat(types.Select(t => ImportRaw(source, t, !closure.Definitions.Single(d => d.Name == Name(t)).ExistingDestinationDefinition))) + "</DataTypes></Controller></RSLogix5000Content>\n");
            var parsed = XmlProjects.Parse(xml);
            if (parsed.Descendants().Any(e => e.Name.LocalName is "Tag" or "Tags" or "Data" or "DefaultData" or "ForceData" or "Program" or "Routine" or "Module" or "Task")) throw Error("payload", "Type-only import must contain no tag/value/logic/hardware/task payload.");
        }
        var manifest = JsonSerializer.SerializeToUtf8Bytes(closure, ProjectService.Json);
        if ((xml?.Length ?? 0) + manifest.Length + rawBytes > ProgramImportPackaging.MaximumOutputBytes) throw Error("limit", "Dependency delivery exceeds 32 MiB.");
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            void Add(string name, byte[] bytes) { var entry = zip.CreateEntry(name); entry.LastWriteTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero); using var stream = entry.Open(); stream.Write(bytes); }
            Add("manifest.json", manifest); if (xml is not null) Add("type-dependencies.l5x", xml);
            foreach (var type in types) Add("source-definitions/" + Name(type) + ".xml", Encoding.UTF8.GetBytes(source.Raw(type)));
            Add("README.txt", Encoding.UTF8.GetBytes(Boundary + "\n\nReview every new type and collision. Existing references require Use Existing, never Overwrite. Imported metadata changes only Use; source-definitions retain exact source XML. When all types already exist, no native import file is emitted. A missing source closure is a possible compatibility concern, not a proven cause or verified repair of a native failure. Do not replay a failed import.\n"));
        }
        return new(xml, manifest, buffer.ToArray(), closure);
    }
    internal static string ImportRaw(LogixLexicalDocument source, XElement type, bool target)
    {
        var span = source.Locate(type); var open = source.Text[span.Start..span.OpenEnd]; var value = target ? "Target" : "Reference";
        if (type.Attribute("Use") is not null) open = Regex.Replace(open, "(?<=\\s)Use\\s*=\\s*(?:\"[^\"]*\"|'[^']*')", "Use=\"" + value + "\"", RegexOptions.CultureInvariant);
        else open = open.Insert(open.Length - (span.Empty ? 2 : 1), " Use=\"" + value + "\"");
        return open + source.Text[span.OpenEnd..span.End];
    }
    private static string[] Select(string[]? names)
    { var a = names ?? []; if (a.Length > 256 || a.Distinct(StringComparer.OrdinalIgnoreCase).Count() != a.Length) throw Error("selection", "Choose at most 256 distinct roots per kind."); foreach (var n in a) OnlineEdits.ProgramPath(n); return a; }
    private static string Name(XElement e) => (string?)e.Attribute("Name") ?? throw Error("source_missing", "A named declaration is required.");
    private static bool Same(XElement e, string name) => string.Equals(Name(e), name, StringComparison.OrdinalIgnoreCase);
    private static string Hash(string s) => ProjectService.Hash(Encoding.UTF8.GetBytes(s));
    private static string Canonical(XElement e) { var copy = new XElement(e); copy.DescendantsAndSelf().Attributes("Use").Remove(); return Hash(ProjectComparison.Normalize(copy, true).ToString(SaveOptions.DisableFormatting)); }
    private static string Layout(XElement e) { var copy = new XElement(e); copy.Descendants("Description").Remove(); return Canonical(copy); }
    private static OperationException Error(string suffix, string message) => new("type_dependency_" + suffix, message);
}

public sealed partial class ProjectService
{
    public async Task<TypeDependencyPackage> PrepareTypeDependencyPackageAsync(TypeDependencyPackageRequest request, CancellationToken ct = default)
    {
        if (request.Source is null || request.Baseline is null) throw new OperationException("type_dependency_source", "Pin source and baseline snapshots.");
        using (await EnterWorkspaceAsync(request.Source.ProjectId, ct))
        {
            var source = PreservationSource(request.Source); var baseline = PreservationSource(request.Baseline);
            var built = LogixTypeDependencies.Build(source.Bytes, baseline.Bytes, request);
            Artifact Store(string format, byte[] bytes)
            {
                var id = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(id); Directory.CreateDirectory(dir); AtomicBytes(Path.Combine(dir, "project." + format), bytes);
                var artifact = new Artifact(id, source.Project.Id, format, Hash(bytes), bytes.Length, LogixTypeDependencies.Boundary); Save(Path.Combine(dir, "artifact.json"), artifact); return artifact;
            }
            var types = built.TypesXml is null ? null : Store("l5x", built.TypesXml); var manifest = Store("json", built.ManifestJson); var package = Store("zip", built.Zip);
            Audit("type_dependency_package", source.Project, source.Project.CurrentSha256, manifest.Sha256, package.Id);
            return new(types, manifest, package, built.Closure);
        }
    }
}
