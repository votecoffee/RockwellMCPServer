using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record CloneLengthField(string XPath, string Attribute, string ValueSha256);
public sealed record CloneField(string XPath, string? Attribute, string ValueSha256, string Codec = "text", int Offset = 0,
    int[]? L5kPath = null, int? Octet = null, string ByteOrder = "network", int IntegerBits = 32, bool Signed = true,
    int? Capacity = null, bool Padded = false, CloneLengthField? LengthField = null, int? LinkOffset = null,
    int? TokenLength = null, string? HiddenLocalName = null, string? HiddenLocalReason = null);
public sealed record CloneSite(string Purpose, CloneField Field, string? ValueGroup = null);
public sealed record CloneIdentityChange(string Key, string Before, string After, CloneSite[] Sites);
public sealed record CloneAddressChange(string Key, string Before, string After, CloneSite[] Sites, string RuntimeReview);
public sealed record ProjectCloneRequest(MergeSnapshot Source, string Name, CloneIdentityChange[] Identities, CloneAddressChange[] Addresses);
public sealed record ProjectCloneApplyRequest(ProjectCloneRequest Preview, string ExpectedReviewSha256);
public sealed record CloneFinding(string Status, string Key, string Location, string Detail);
public sealed record CloneChangedField(string Key, string Purpose, string Location, string Codec, string BeforeSha256, string AfterSha256);
public sealed record ProjectClonePlan(ProjectCloneRequest Request, ComparisonSnapshot Source, bool CanApply, string? ResultSha256,
    string ReviewSha256, CloneChangedField[] Fields, CloneFinding[] Findings, string Boundary);
public sealed record ProjectCloneResult(ProjectInfo Project, Artifact Receipt, string ReviewSha256);
public sealed record ProjectCloneReadbackRequest(ProjectCloneRequest Preview, string ExpectedReviewSha256, MergeSnapshot Readback);
public sealed record ProjectCloneReadbackReport(bool Matches, bool NativeOriginVerified, ComparisonSnapshot Readback,
    string ExpectedSha256, string ActualSha256, string ReviewSha256, CloneFinding[] Findings, string Boundary);

internal static class ProjectCloning
{
    internal const string Boundary = "Offline source-bound clone to a separate project. Simultaneous caller-reviewed identity/address sites only; no workbook role inference, global replacement or compiler-bound reference discovery. Unselected bytes and current saved values remain exact. Selected exported representations require coherent explicit sites; hidden AOI-local L5K paths require a named exported local and reason. RuntimeReview is the caller's coverage assessment, not executable dataflow proof. Controller CommPath and module Port Address are saved routing configuration, not a physical device TCP/IP change. Safety/SNN/signatures/ownership, style/recipe changes, opaque payload edits and physical configuration are excluded. Readback compares supplied saved files and does not authenticate native origin, compile, download or establish functional acceptance. No controller connection.";
    private static OperationException Error(string message) => CloneValueCodecs.Error(message);
    private static string Hash(string value) => ProjectService.Hash(Encoding.UTF8.GetBytes(value));
    private static byte[] Address(string text)
    {
        var parts = text?.Split('.');
        if (parts is null || parts.Length != 4 || parts.Any(p => !byte.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var b) || p != b.ToString(CultureInfo.InvariantCulture))) throw Error("Use canonical dotted-decimal IPv4 addresses, with no route guessing.");
        return parts.Select(p => byte.Parse(p, CultureInfo.InvariantCulture)).ToArray();
    }
    private static uint Number(byte[] address, CloneField field)
    {
        if (field.Octet is { } octet) { if (octet is < 0 or > 3) throw Error("Select octet 0 through 3."); return address[octet]; }
        if (field.IntegerBits != 32 || field.ByteOrder is not ("network" or "little-endian")) throw Error("Packed IP integers require 32 bits and explicit network or little-endian order.");
        var b = field.ByteOrder == "network" ? address : address.Reverse().ToArray();
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }
    private static bool Protected(XElement node, string? attribute)
    {
        return LogixPreservation.Safety(node) || node.AncestorsAndSelf().Any(e => e.Name.LocalName is
            "SafetyInfo" or "SafetyNetwork" or "SafetySignature" or "SafetySignatureHistory" or "SafetyTask" or "SafetyTagMap" or "ForceData" or "EncodedData" or "AddOnInstructionDefinition" or "DataType" ||
            e.Name.LocalName.Contains("Ownership", StringComparison.OrdinalIgnoreCase) || e.Name.LocalName.Contains("Signature", StringComparison.OrdinalIgnoreCase)) ||
            attribute is not null && (attribute.Contains("Safety", StringComparison.OrdinalIgnoreCase) || attribute.Contains("Signature", StringComparison.OrdinalIgnoreCase) || attribute.Contains("Owner", StringComparison.OrdinalIgnoreCase) || attribute.Contains("SNN", StringComparison.OrdinalIgnoreCase));
    }
    private sealed record Selected(CloneField Field, XElement Element, XObject Node, string Value, string Location);
    private sealed class Buffer(Selected selected, CloneValueCodecs.Node? l5k, CloneValueCodecs.Node? literal, byte[] bytes, int length, int capacity, bool padded, Selected? lengthField)
    {
        public Selected Selected = selected;
        public CloneValueCodecs.Node? L5k = l5k;
        public CloneValueCodecs.Node? Literal = literal;
        public byte[] Bytes = bytes;
        public int Length = length, Capacity = capacity;
        public bool Padded = padded;
        public Selected? LengthField = lengthField;
        public List<CloneValueCodecs.Edit> Edits = [];
    }
    internal static (ProjectClonePlan Plan, byte[] Bytes) Build(ProjectInfo project, byte[] bytes, ProjectCloneRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 120 || request.Name.Any(char.IsControl) ||
            request.Identities is null || request.Addresses is null || request.Identities.Length + request.Addresses.Length is < 1 or > 256 ||
            request.Identities.Any(i => i is null) || request.Addresses.Any(a => a is null)) throw Error("Choose a bounded clone name and 1 to 256 explicit identity/address mappings.");
        var keys = request.Identities.Select(i => i.Key).Concat(request.Addresses.Select(a => a.Key)).ToArray();
        if (keys.Any(k => string.IsNullOrWhiteSpace(k) || k.Length > 100) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length) throw Error("Contract mapping keys must be unique and at most 100 characters.");
        var sites = request.Identities.SelectMany(i => i.Sites ?? []).Concat(request.Addresses.SelectMany(a => a.Sites ?? [])).ToArray();
        if (sites.Length is < 1 or > 4096 || sites.Any(s => s is null || s.Field is null)) throw Error("Use 1 to 4096 exact field sites.");
        using var reader = XmlReader.Create(new MemoryStream(bytes), new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024 });
        var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo); XmlProjects.Validate(document);
        if (document.Root!.Elements("Controller").Count() != 1 || (string?)document.Root.Attribute("TargetType") is { } target && target != "Controller") throw Error("Select one whole-controller L5X snapshot, not a partial component export.");
        var source = new XmlSourceDocument(bytes, document, "clone_xml"); var patches = new List<XmlSourceDocument.Patch>();
        var findings = new List<CloneFinding>(); var changed = new List<CloneChangedField>();
        var buffers = new Dictionary<string, Buffer>(StringComparer.Ordinal);
        var elements = new Dictionary<string, XElement>(StringComparer.Ordinal); var hashes = new Dictionary<XObject, string>();
        var touched = new List<(string Key, string Group, Selected Selected)>(); var changedValues = new Dictionary<XObject, List<CloneValueCodecs.Edit>>();
        void Finding(string status, string key, string path, string detail) => findings.Add(new(status, key, path, detail));
        Selected Select(CloneField field)
        {
            if (string.IsNullOrWhiteSpace(field.XPath) || field.XPath.Length > 8192 || !field.XPath.StartsWith("/RSLogix5000Content", StringComparison.Ordinal) ||
                new[] { "//", "::", "|", "(" }.Any(field.XPath.Contains) || !StructuralEditing.Hash(field.ValueSha256)) throw Error("Each field requires an absolute exact child XPath (no descendant axes/functions/unions) and UTF-8 decoded-value SHA-256.");
            XElement element; XObject node;
            try
            {
                if (!elements.TryGetValue(field.XPath, out element!)) elements[field.XPath] = element = XmlProjects.Select(document, field.XPath);
                node = field.Attribute is null ? element : element.Attribute(field.Attribute) ?? throw Error("The selected attribute does not exist.");
            }
            catch (Exception ex) when (ex is System.Xml.XPath.XPathException or XmlException or ArgumentException) { throw Error("Use a valid exact element selector and unqualified attribute name."); }
            if (Protected(element, field.Attribute) || field.Attribute is null && element.HasElements) throw Error("Protected/safety/ownership fields and non-scalar XML edits are excluded.");
            var value = node is XAttribute attribute ? attribute.Value : element.Value;
            if (!hashes.TryGetValue(node, out var hash)) hashes[node] = hash = Hash(value);
            if (value.Length is < 1 or > 1024 * 1024 || !hash.Equals(field.ValueSha256, StringComparison.OrdinalIgnoreCase)) throw Error("The exact saved field hash changed or exceeds 1 MiB.");
            return new(field, element, node, value, XmlProjects.PathOf(element) + (field.Attribute is null ? "" : "/@" + field.Attribute));
        }
        void Edit(Selected selected, int offset, int length, string value, string key, string purpose)
        {
            // All positions refer to the original source. Source.Apply rejects
            // overlap even when two replacement addresses happen to coincide.
            var patch = source.ValueEditor(selected.Node)(offset, length, value);
            patches.Add(patch);
            if (!changedValues.TryGetValue(selected.Node, out var edits)) changedValues[selected.Node] = edits = [];
            edits.Add(new(offset, length, value));
            changed.Add(new(key, purpose, selected.Location, selected.Field.Codec, Hash(selected.Value.Substring(offset, length)), Hash(value)));
        }
        static bool Code(XElement element) => element.Name == "Text" && element.Parent?.Name == "Rung" && element.Ancestors("RLLContent").Any() || element.Name == "Line" && element.Parent?.Name == "STContent";
        static bool Stored(XElement element) => element.AncestorsAndSelf("Data").Any() && element.Ancestors("Tag").Any(t => t.Parent?.Name == "Tags");
        void Text(Selected selected, string before, string after, bool identifier, string key, string purpose)
        {
            var offset = selected.Field.Offset;
            if (offset < 0 || offset + before.Length > selected.Value.Length || selected.Value.Substring(offset, before.Length) != before) throw Error("An exact source occurrence no longer matches the contract.");
            bool Word(char c) => identifier ? char.IsAsciiLetterOrDigit(c) || c == '_' : char.IsAsciiDigit(c) || c == '.';
            if (offset > 0 && Word(selected.Value[offset - 1]) || offset + before.Length < selected.Value.Length && Word(selected.Value[offset + before.Length])) throw Error("The selected occurrence splits an identifier/address token.");
            Edit(selected, offset, before.Length, after, key, purpose);
        }
        foreach (var identity in request.Identities)
        {
            WatchValidation.Identifier(identity.Before); WatchValidation.Identifier(identity.After);
            if (identity.After.Length > 40 || identity.Before == identity.After || identity.Sites is null || identity.Sites.Length == 0) throw Error("Each identity mapping needs different bounded identifiers and exact declaration/reference sites.");
            foreach (var site in identity.Sites)
            {
                try
                {
                    var selected = Select(site.Field); var e = selected.Element; var attribute = site.Field.Attribute;
                    if (site.Field.Codec != "text" || Stored(e) || !(site.Purpose switch
                    {
                        "controller-name" => e.Name == "Controller" && attribute == "Name",
                        "project-target-name" => e == document.Root && attribute == "TargetName",
                        "symbol-declaration" => attribute == "Name" && e.Name.LocalName is "Tag" or "Program" or "Routine" or "Module" or "Task",
                        "symbol-reference" => Code(e) && attribute is null || attribute is "AliasFor" or "MainRoutineName" or "ProgramName" or "RoutineName" && e.AncestorsAndSelf("Controller").Any(),
                        _ => false
                    })) throw Error("Identity sites must name explicit standard declarations or supported lexical references; saved data is excluded.");
                    Text(selected, identity.Before, identity.After, true, identity.Key, site.Purpose);
                }
                catch (OperationException ex) { Finding("blocked", identity.Key, site.Field.XPath, ex.Message); }
            }
        }
        foreach (var address in request.Addresses)
        {
            var before = Address(address.Before); var after = Address(address.After);
            if (address.Before == address.After || address.Sites is null || address.Sites.Length == 0 || string.IsNullOrWhiteSpace(address.RuntimeReview) || address.RuntimeReview.Length > 2000) throw Error("Each address mapping needs sites and a reviewed runtime initialization/cache coverage statement.");
            foreach (var site in address.Sites)
            {
                try
                {
                    var selected = Select(site.Field); var field = site.Field; var element = selected.Element;
                    var stored = Stored(element); var code = Code(element) && field.Attribute is null;
                    var allowed = site.Purpose switch
                    {
                        "controller-comm-path" => element.Name == "Controller" && field.Attribute == "CommPath" && field.Codec == "text",
                        "module-address" => element.Name == "Port" && element.Ancestors("Module").Any() && field.Attribute == "Address" && field.Codec == "text",
                        "message-path" => element.Name == "MessageParameters" && field.Attribute == "CommPath" && field.Codec == "text",
                        "saved-value" or "hidden-cache" => stored,
                        "runtime-initializer" => code && field.Codec is "text" or "integer",
                        _ => false
                    };
                    if (!allowed) throw Error("Unsupported address purpose/location. Physical TCP/IP settings, safety, ownership and unrelated style/recipe edits require separate workflows.");
                    if (stored && (string?)element.Ancestors("Tag").First().Attribute("TagType") != "Base") throw Error("Saved address edits require an explicit base tag; aliases and non-base tag data are excluded.");
                    if (field.Octet is not null && (string.IsNullOrWhiteSpace(site.ValueGroup) || site.ValueGroup.Length > 100)) throw Error("Octet fields need an explicit valueGroup linking all changed octets across saved representations.");
                    if (site.ValueGroup is { Length: > 100 }) throw Error("A valueGroup supports at most 100 characters.");
                    if (stored) touched.Add((address.Key, site.ValueGroup ?? "value", selected));
                    if (field.Codec == "text")
                    {
                        if (stored && element.Name != "MessageParameters") throw Error("Saved tag values require a typed numeric/string codec.");
                        Text(selected, address.Before, address.After, false, address.Key, site.Purpose); continue;
                    }
                    var l5k = element.Name == "Data" && (string?)element.Attribute("Format") == "L5K";
                    var node = l5k ? CloneValueCodecs.Select(CloneValueCodecs.Parse(selected.Value), field.L5kPath) : null;
                    if (field.Codec == "integer")
                    {
                        if (!l5k && !code && (field.Attribute != "Value" || element.Name.LocalName is not ("DataValue" or "DataValueMember" or "Element"))) throw Error("Numeric sites require a saved scalar Value attribute, exact L5K path or reviewed RLL/ST initializer literal.");
                        if (node?.Children is not null) throw Error("The numeric L5K path must select a scalar token.");
                        var start = node?.Start ?? field.Offset; var length = node is not null ? node.End - node.Start : field.TokenLength ?? selected.Value.Length;
                        if (start < 0 || length < 1 || start + length > selected.Value.Length || code && field.TokenLength is null) throw Error("Initializers need exact numeric token offsets and lengths.");
                        bool NumericCharacter(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '.' or '#';
                        if (code && (start > 0 && NumericCharacter(selected.Value[start - 1]) || start + length < selected.Value.Length && NumericCharacter(selected.Value[start + length]))) throw Error("The initializer site splits a numeric token.");
                        if (!l5k && !code)
                        {
                            var type = element.AncestorsAndSelf().Select(e => (string?)e.Attribute("DataType")).FirstOrDefault(t => t is not null);
                            var expectedType = (field.IntegerBits, field.Signed) switch { (8, true) => "SINT", (8, false) => "USINT", (16, true) => "INT", (16, false) => "UINT", (32, true) => "DINT", (32, false) => "UDINT", _ => "unsupported" };
                            if (type != expectedType) throw Error("The reviewed numeric width/sign differs from the exported scalar/array DataType.");
                        }
                        var value = CloneValueCodecs.ReplaceInteger(selected.Value.Substring(start, length), Number(before, field), Number(after, field), field.IntegerBits, field.Signed);
                        Edit(selected, start, length, value, address.Key, site.Purpose); continue;
                    }
                    if (field.Codec is not ("string" or "l5k-string" or "cip-string" or "cip-l5k-string")) throw Error("Unknown address codec.");
                    if (l5k != field.Codec.EndsWith("l5k-string", StringComparison.Ordinal) || !stored || field.Attribute is not null) throw Error("String codecs require a saved scalar byte literal or an explicit L5K [LEN, byte-literal] path.");
                    var bufferKey = selected.Location + ":" + string.Join(',', field.L5kPath ?? []);
                    if (!buffers.TryGetValue(bufferKey, out var buffer))
                    {
                        byte[] data; int length, capacity; bool padded; Selected? lengthField = null; CloneValueCodecs.Node? literal = null;
                        if (l5k)
                        {
                            if (node?.Children is not { Length: 2 } children || children.Any(c => c.Children is not null)) throw Error("Select an exact L5K STRING pair [LEN, byte-literal]; hidden layout paths are never inferred.");
                            data = CloneValueCodecs.Bytes(selected.Value[children[1].Start..children[1].End]);
                            length = checked((int)CloneValueCodecs.Integer(selected.Value[children[0].Start..children[0].End]));
                            capacity = field.Capacity ?? data.Length; padded = field.Padded;
                        }
                        else
                        {
                            if (field.LengthField is null || field.Capacity is null) throw Error("Scalar saved strings require the explicit LEN/Length attribute field and reviewed capacity.");
                            var len = field.LengthField; lengthField = Select(new(len.XPath, len.Attribute, len.ValueSha256));
                            var dataElement = element.AncestorsAndSelf("Data").First();
                            var scalarString = element == dataElement && (string?)dataElement.Attribute("Format") == "String" && lengthField.Element == element && len.Attribute == "Length";
                            var structureString = element.Name == "DataValueMember" && (string?)element.Attribute("Name") == "DATA" && (string?)element.Attribute("DataType") == "STRING" &&
                                lengthField.Element.Parent == element.Parent && lengthField.Element.Name == "DataValueMember" && (string?)lengthField.Element.Attribute("Name") == "LEN" &&
                                (string?)lengthField.Element.Attribute("DataType") == "DINT" && len.Attribute == "Value";
                            if (!scalarString && !structureString) throw Error("Select the same String Data/@Length or the matching Decorated STRING LEN sibling Value field.");
                            literal = CloneValueCodecs.Parse(selected.Value);
                            if (literal.Children is not null) throw Error("A scalar saved string requires one quoted byte literal with optional surrounding whitespace.");
                            data = CloneValueCodecs.Bytes(selected.Value[literal.Start..literal.End]); length = checked((int)CloneValueCodecs.Integer(lengthField.Value)); capacity = field.Capacity.Value; padded = field.Padded;
                        }
                        _ = CloneValueCodecs.RebuildBuffer(data, length, capacity, [], padded);
                        buffer = new(selected, node, literal, data, length, capacity, padded, lengthField); buffers.Add(bufferKey, buffer);
                    }
                    else if (JsonSerializer.Serialize(buffer.Selected.Field with { Offset = 0, LinkOffset = null }, ProjectService.Json) != JsonSerializer.Serialize(field with { Offset = 0, LinkOffset = null }, ProjectService.Json))
                        throw Error("Repeated edits to one saved byte buffer must reuse the same capacity, LEN and encoding contract.");
                    if (field.Codec.StartsWith("cip-", StringComparison.Ordinal))
                    {
                        if (field.LinkOffset is null) throw Error("Binary CIP links need the reviewed segment byte offset.");
                        buffer.Edits.Add(CloneValueCodecs.ExtendedLink(buffer.Bytes[..buffer.Length], field.LinkOffset.Value, address.Before, address.After));
                    }
                    else
                    {
                        var payload = Encoding.Latin1.GetString(buffer.Bytes, 0, buffer.Length);
                        if (field.Offset < 0 || field.Offset + address.Before.Length > payload.Length || payload.Substring(field.Offset, address.Before.Length) != address.Before ||
                            field.Offset > 0 && (char.IsAsciiDigit(payload[field.Offset - 1]) || payload[field.Offset - 1] == '.') ||
                            field.Offset + address.Before.Length < payload.Length && (char.IsAsciiDigit(payload[field.Offset + address.Before.Length]) || payload[field.Offset + address.Before.Length] == '.'))
                            throw Error("The saved string address occurrence differs from the reviewed whole-address offset.");
                        buffer.Edits.Add(new(field.Offset, address.Before.Length, address.After));
                    }
                }
                catch (Exception ex) when (ex is OperationException or OverflowException or FormatException) { Finding("blocked", address.Key, site.Field.XPath, ex.Message); }
            }
            Finding("reviewed-coverage", address.Key, "runtime", address.RuntimeReview);
        }
        foreach (var buffer in buffers.Values)
        {
            try
            {
                var payload = CloneValueCodecs.Apply(Encoding.Latin1.GetString(buffer.Bytes, 0, buffer.Length), buffer.Edits);
                var bytesAfter = CloneValueCodecs.RebuildBuffer(buffer.Bytes, buffer.Length, buffer.Capacity, buffer.Edits, buffer.Padded);
                var selected = buffer.Selected;
                if (buffer.L5k is { } node)
                {
                    var children = node.Children!;
                    Edit(selected, children[0].Start, children[0].End - children[0].Start, payload.Length.ToString(CultureInfo.InvariantCulture), "saved-buffer", "string-length");
                    Edit(selected, children[1].Start, children[1].End - children[1].Start, CloneValueCodecs.Literal(bytesAfter), "saved-buffer", "string-bytes");
                }
                else
                {
                    var literal = buffer.Literal!;
                    Edit(selected, literal.Start, literal.End - literal.Start, CloneValueCodecs.Literal(bytesAfter), "saved-buffer", "string-bytes");
                    Edit(buffer.LengthField!, 0, buffer.LengthField!.Value.Length, payload.Length.ToString(CultureInfo.InvariantCulture), "saved-buffer", "string-length");
                }
            }
            catch (OperationException ex) { Finding("blocked", "saved-buffer", buffer.Selected.Location, ex.Message); }
        }
        foreach (var group in touched.GroupBy(t => (t.Key, Tag: t.Selected.Element.Ancestors("Tag").First(), t.Group)))
        {
            var tag = group.Key.Tag; var data = tag.Elements("Data").ToArray();
            var hidden = group.All(t => t.Selected.Field.HiddenLocalName is not null);
            if (group.Any(t => t.Selected.Field.HiddenLocalName is not null))
            {
                foreach (var entry in group.Where(t => t.Selected.Field.HiddenLocalName is not null))
                {
                    var field = entry.Selected.Field; var type = (string?)tag.Attribute("DataType");
                    var aoi = document.Descendants("AddOnInstructionDefinition").SingleOrDefault(a => (string?)a.Attribute("Name") == type);
                    if (string.IsNullOrWhiteSpace(field.HiddenLocalReason) || field.HiddenLocalReason.Length > 1000 ||
                        aoi?.Elements("LocalTags").Elements("LocalTag").Count(t => (string?)t.Attribute("Name") == field.HiddenLocalName) != 1 ||
                        (string?)entry.Selected.Element.Attribute("Format") != "L5K")
                        Finding("blocked", group.Key.Key, entry.Selected.Location, "A hidden-only exception requires one named exported AOI LocalTag, a bounded reason and an explicit L5K path.");
                }
            }
            var covered = group.Select(t => t.Selected.Element.AncestorsAndSelf("Data").First()).ToHashSet();
            var mapping = request.Addresses.Single(a => a.Key == group.Key.Key); var from = Address(mapping.Before); var to = Address(mapping.After);
            foreach (var representation in covered)
            {
                var fields = group.Where(t => t.Selected.Element.AncestorsAndSelf("Data").First() == representation).Select(t => t.Selected.Field).ToArray();
                if (fields.Any(f => f.Octet is not null))
                    foreach (var octet in Enumerable.Range(0, 4).Where(i => from[i] != to[i]))
                        if (!fields.Any(f => f.Codec == "integer" && f.Octet == octet)) Finding("blocked", group.Key.Key, XmlProjects.PathOf(representation), "An octet valueGroup is missing a changed octet in this saved representation: " + octet);
            }
            foreach (var representation in data)
            {
                var format = (string?)representation.Attribute("Format");
                if (format is not ("L5K" or "Decorated" or "String" or "Message") || !covered.Contains(representation) && !(hidden && format == "Decorated"))
                    Finding("blocked", group.Key.Key, XmlProjects.PathOf(representation), "Changed tag data has an unhandled saved representation; supply coherent sites or use a separately validated adapter. Raw/opaque payloads are never discarded.");
            }
        }
        byte[] result = bytes;
        try
        {
            result = source.Apply(patches); var after = XmlProjects.Parse(result);
            foreach (var (node, edits) in changedValues)
            {
                var element = node as XElement ?? ((XAttribute)node).Parent!; var corresponding = XmlProjects.Select(after, XmlProjects.PathOf(element));
                var actual = node is XAttribute a ? corresponding.Attribute(a.Name)?.Value : corresponding.Value;
                var original = node is XAttribute b ? b.Value : element.Value;
                if (actual != CloneValueCodecs.Apply(original, edits)) throw Error("Parsed clone readback differs from an exact edited field.");
            }
            var controller = after.Root!.Element("Controller")!;
            if ((string?)after.Root.Attribute("TargetName") is { } name && name != (string?)controller.Attribute("Name")) throw Error("The whole-project TargetName and Controller Name must agree after retargeting.");
        }
        catch (Exception ex) when (ex is OperationException or XmlException) { Finding("blocked", "result", "/", ex.Message); }
        var canApply = findings.All(f => f.Status != "blocked"); var outputHash = ProjectService.Hash(result);
        var review = ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(new { request, sourceSha256 = project.CurrentSha256, outputHash, fields = changed, findings, toolVersion = WorkspaceIdentity.Version }, ProjectService.Json));
        var plan = new ProjectClonePlan(request, new(project.Id, project.Name, project.Revision, project.CurrentSha256), canApply, canApply ? outputHash : null, review, changed.ToArray(), findings.ToArray(), Boundary);
        if (JsonSerializer.SerializeToUtf8Bytes(plan, ProjectService.Json).Length > 8 * 1024 * 1024) throw Error("Clone review exceeds 8 MiB.");
        return (plan, result);
    }
}

public sealed partial class ProjectService
{
    private (ProjectClonePlan Plan, byte[] Bytes) BuildProjectClone(ProjectCloneRequest request)
    { if (request?.Source is null) throw CloneValueCodecs.Error("Select a pinned managed source snapshot."); var source = PreservationSource(request.Source); return ProjectCloning.Build(source.Project, source.Bytes, request); }
    public async Task<ProjectClonePlan> PreviewProjectCloneAsync(ProjectCloneRequest request, CancellationToken ct = default)
    { await gate.WaitAsync(ct); try { return BuildProjectClone(request).Plan; } finally { gate.Release(); } }
    public async Task<ProjectCloneResult> ApplyProjectCloneAsync(ProjectCloneApplyRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var built = BuildProjectClone(request.Preview); CheckCloneReview(built.Plan, request.ExpectedReviewSha256);
            var project = PublishDiagnosticProject(built.Bytes, request.Preview.Name, "project-clone:" + built.Plan.Source.Id);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, kind = "project_clone", toolVersion = WorkspaceIdentity.Version, createdAt = DateTimeOffset.UtcNow, plan = built.Plan, result = project }, Json);
            var id = Guid.NewGuid().ToString("N"); var dir = ArtifactDir(id); Directory.CreateDirectory(dir); AtomicBytes(Path.Combine(dir, "project.json"), bytes);
            var receipt = new Artifact(id, project.Id, "json", Hash(bytes), bytes.Length, ProjectCloning.Boundary); Save(Path.Combine(dir, "artifact.json"), receipt);
            return new(project, receipt, built.Plan.ReviewSha256);
        }
        finally { gate.Release(); }
    }
    private static void CheckCloneReview(ProjectClonePlan plan, string expected)
    {
        if (!string.Equals(plan.ReviewSha256, expected, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Clone sources, contract or findings changed; preview again.");
        if (!plan.CanApply) throw new OperationException("clone_blocked", "Resolve every blocked clone finding before applying or checking readback.");
    }
    private static CloneFinding[] CloneReadbackFindings(XElement expected, XElement actual, bool matches)
    {
        var findings = new List<CloneFinding> { new(matches ? "match" : "mismatch", "whole-project", "/", "Saved-XML document-element comparison includes saved data/configuration. Container whitespace, CDATA/text spelling and attribute order are normalized; ExportDate and Controller LastModifiedDate are ignored. XML declaration/prolog/epilog are not compared. No native origin is inferred. Use the separate validation workflow for explicitly reviewed native normalization/differences.") };
        if (matches) return findings.ToArray();
        void Difference(XElement leftElement, XElement rightElement, string path)
        {
            if (findings.Count >= 257) return;
            if (leftElement.Name != rightElement.Name) { findings.Add(new("mismatch", "element", path, "Element identity differs.")); return; }
            foreach (var name in leftElement.Attributes().Select(a => a.Name).Union(rightElement.Attributes().Select(a => a.Name)))
            {
                if ((string?)leftElement.Attribute(name) != (string?)rightElement.Attribute(name)) findings.Add(new("mismatch", "attribute", path + "/@" + name, "Saved attribute value differs or is missing."));
                if (findings.Count >= 257) break;
            }
            if (findings.Count >= 257) return;
            if (leftElement.Elements().Count() != rightElement.Elements().Count()) findings.Add(new("mismatch", "children", path, "Saved child count differs."));
            string Scalar(XElement e) => string.Concat(e.Nodes().Where(n => n is not XElement).Select(n => n.ToString(SaveOptions.DisableFormatting)));
            if (Scalar(leftElement) != Scalar(rightElement)) findings.Add(new("mismatch", "content", path, "Saved scalar text/comment/processing-instruction content differs."));
            var indexes = new Dictionary<XName, int>();
            foreach (var child in leftElement.Elements().Zip(rightElement.Elements()))
            { indexes.TryGetValue(child.First.Name, out var index); indexes[child.First.Name] = ++index; Difference(child.First, child.Second, path + "/" + child.First.Name.LocalName + "[" + index + "]"); if (findings.Count >= 257) break; }
        }
        Difference(expected, actual, "/RSLogix5000Content");
        if (findings.Count >= 257) return [.. findings.Take(257), new("truncated", "differences", "/", "Difference details are limited to 256; the whole-project mismatch still covers the complete files.")];
        return findings.ToArray();
    }
    public async Task<ProjectCloneReadbackReport> ReadbackProjectCloneAsync(ProjectCloneReadbackRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            var built = BuildProjectClone(request.Preview); CheckCloneReview(built.Plan, request.ExpectedReviewSha256);
            var readback = PreservationSource(request.Readback);
            var expectedTree = ProjectComparison.Normalize(XmlProjects.Parse(built.Bytes).Root!, true);
            var actualDocument = XmlProjects.Parse(readback.Bytes); LogixLexicalDocument.ValidateBudget(actualDocument);
            var actualTree = ProjectComparison.Normalize(actualDocument.Root!, true);
            var expected = Hash(Encoding.UTF8.GetBytes(expectedTree.ToString(SaveOptions.DisableFormatting)));
            var actual = Hash(Encoding.UTF8.GetBytes(actualTree.ToString(SaveOptions.DisableFormatting)));
            var findings = CloneReadbackFindings(expectedTree, actualTree, expected == actual);
            return new(expected == actual, false, new(readback.Project.Id, readback.Project.Name, readback.Project.Revision, readback.Project.CurrentSha256),
                ProjectService.Hash(built.Bytes), readback.Project.CurrentSha256, built.Plan.ReviewSha256, findings, ProjectCloning.Boundary);
        }
        finally { gate.Release(); }
    }
}
