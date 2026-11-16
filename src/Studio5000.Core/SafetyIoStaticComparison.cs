using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafetyIoStaticBrowseRequest(string ProjectId, string ExpectedSha256, int ExpectedRevision, string ModuleName,
    SafetyIoStaticProfileRequest Profile, string ExpectedProfileSha256, string Encoding);
public sealed record SafetyIoStaticValue(string Category, string Name, int Offset, int Bytes, uint Mask, uint Value, string RawHex, string? EnumKey, bool Known);
public sealed record SafetyIoStaticSnapshot(ComparisonSnapshot Source, string ModuleName, string ModuleXPath, string ModuleSha256, string ModuleContextTextSha256,
    string CatalogNumber, string Series, string Major, string Minor, SafetyIoStaticProfile Profile, string ConfigurationXPath,
    string ConfigurationSha256, string Encoding, int NativeTokenCount, int PayloadBytes, string PayloadSha256,
    string? NativeConfigSize, string TerminalPaddingHex, SafetyIoStaticValue[] Values, string[] Diagnostics, string Boundary);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafetyIoStaticCompareRequest(SafetyIoStaticBrowseRequest Before, SafetyIoStaticBrowseRequest After,
    int Offset = 0, int Limit = 100, bool IncludeUnchanged = false);
public sealed record SafetyIoStaticDifference(string Name, string Category, SafetyIoStaticValue Before, SafetyIoStaticValue After, bool Equal);
public sealed record SafetyIoStaticComparison(string ComparisonSha256, SafetyIoStaticSnapshot Before, SafetyIoStaticSnapshot After,
    bool ParametersEqual, bool HasUnknownParameterValues, bool ModuleContextEqual, bool NativeConfigSizeEqual, bool TerminalPaddingEqual, int ParameterFields, int ChangedParameters, int TotalRows, int Offset, int? NextOffset,
    SafetyIoStaticDifference[] Rows, SafetyIoStaticDifference[] NonParameterDifferences, string Boundary, Artifact? Receipt = null);

internal static class SafetyIoStaticDecoding
{
    internal const string Boundary = "Read-only saved safety I/O parameter decoding/comparison using exact pinned AOP identity/layout/enums. Packing is explicitly selected; native ConfigSize/CfgSize/ID, signature tuple, padding and reserved bits are retained unvalidated evidence. IP/SNN/timing/I/O configuration remains bound by exact module/project hashes and is not interpreted as channel settings. No source rewrite, auto-alignment, SCID generation, SDK/AOP execution, live access or safety/native acceptance.";
    private static OperationException Error(string detail) => new("safety_io_static_configuration", detail);
    internal static byte[] Payload(string text, string encoding, int layoutBytes, out int count)
    {
        if (encoding is not ("flat-bytes" or "packed-dint32-le")) throw Error("Explicitly select flat-bytes or packed-dint32-le. Packing and byte order are never autodetected.");
        if (text.Length > 16384) throw Error("A native configuration vector is limited to 16 KiB text.");
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || trimmed[0] != '[' || trimmed[^1] != ']') throw Error("Use one flat scalar L5K vector; nested/typed aggregates are unsupported.");
        var literals = trimmed[1..^1].Split(','); count = literals.Length;
        var expected = encoding == "flat-bytes" ? layoutBytes : (layoutBytes + 3) / 4;
        if (count != expected || count > 256) throw Error("Native token count does not match the selected packing and exact pinned physical layout.");
        var payload = new byte[encoding == "flat-bytes" ? count : count * 4];
        for (var i = 0; i < count; i++)
        {
            var literal = literals[i].Trim(); long number;
            if (!Regex.IsMatch(literal, @"\A(?:-?[0-9]+|(?:2|8|16)#[0-9A-Fa-f_]+)\z", RegexOptions.CultureInvariant)) throw Error("Unsupported native integer literal; no text is normalized or repaired.");
            try
            {
                var separator = literal.IndexOf('#');
                if (separator < 0) number = long.Parse(literal, CultureInfo.InvariantCulture);
                else
                {
                    var value = Convert.ToUInt64(literal[(separator + 1)..].Replace("_", "", StringComparison.Ordinal), int.Parse(literal[..separator], CultureInfo.InvariantCulture));
                    if (value > uint.MaxValue) throw new OverflowException();
                    number = (long)value;
                }
            }
            catch (Exception ex) when (ex is OverflowException or FormatException or ArgumentException) { throw Error("Native integer literal is invalid or exceeds the selected storage width."); }
            if (encoding == "flat-bytes")
            { if (number is < -128 or > 255) throw Error("Flat byte literal is outside signed/unsigned byte range."); payload[i] = unchecked((byte)number); }
            else
            { if (number is < int.MinValue or > uint.MaxValue) throw Error("Packed DWORD literal is outside the signed/unsigned 32-bit range."); BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(i * 4, 4), unchecked((uint)number)); }
        }
        return payload;
    }
    internal static SafetyIoStaticSnapshot Decode(ProjectInfo project, byte[] bytes, SafetyIoStaticProfile profile, SafetyIoStaticBrowseRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ModuleName) || request.ModuleName.Length > 256) throw Error("Select an exact saved module name.");
        var doc = ModuleConnectionEditing.Parse(bytes); var source = new XmlSourceDocument(bytes, doc);
        var modules = doc.Root!.Element("Controller")!.Elements("Modules").Elements("Module").Where(m => (string?)m.Attribute("Name") == request.ModuleName).ToArray();
        if (modules.Length != 1) throw Error("Select exactly one existing module."); var module = modules[0];
        var catalog = (string?)module.Attribute("CatalogNumber"); var series = (string?)module.Attribute("Series");
        if (catalog != profile.CatalogNumber && catalog != profile.Id || series is not null && series != profile.Series ||
            (string?)module.Attribute("Vendor") != "1" || (string?)module.Attribute("ProductType") != "35" || (string?)module.Attribute("ProductCode") != profile.ProductCode || (string?)module.Attribute("Major") != profile.Major.ToString(CultureInfo.InvariantCulture))
            throw Error("Catalog/suffix, explicit series, vendor/product and major revision must exactly match the pinned supported identity.");
        var configurations = module.Elements("Communications").Elements().Where(e => e.Name.LocalName is "ConfigData" or "ConfigTag").ToArray();
        if (configurations.Length != 1) throw Error("Require one existing native ConfigData or ConfigTag."); var configuration = configurations[0];
        if (configuration.Attribute("DataType") is { } type && type.Value != profile.ConfigDataType) throw Error("Native configuration datatype differs from the pinned shared AOP layout.");
        var representations = configuration.Elements("Data").ToArray();
        if (representations.Length != 1 || (string?)representations[0].Attribute("Format") != "L5K" || representations[0].HasElements) throw Error("Require exactly one scalar L5K representation; alternates/duplicates are unsupported.");
        var payload = Payload(representations[0].Value, request.Encoding, profile.ConfigurationBytes, out var tokenCount);
        var values = new List<SafetyIoStaticValue>();
        uint Read(SafetyIoField f) => f.Bytes == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(f.Offset, 4)) : f.Bytes == 2 ? BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(f.Offset, 2)) : payload[f.Offset];
        foreach (var field in profile.Fields)
        {
            var raw = Read(field); var rawHex = Convert.ToHexString(payload.AsSpan(field.Offset, field.Bytes));
            var aliases = profile.Bits.Where(b => b.Target == field.Name).ToArray(); var mask = field.Bytes == 4 ? uint.MaxValue : field.Bytes == 2 ? ushort.MaxValue : byte.MaxValue;
            if (aliases.Length == 0)
            {
                var category = field.Name is "ConfigCRC" or "ConfigTime" or "ConfigDate" ? "signature" : field.Name is "CfgSize" or "CfgIDNum" ? "envelope" : field.Name.StartsWith("Pad", StringComparison.Ordinal) || field.Name == "ConfigPad" ? "padding" : "parameter";
                values.Add(new(category, field.Name, field.Offset, field.Bytes, mask, raw, rawHex, null, true));
            }
            else
            {
                uint used = 0;
                foreach (var bit in aliases)
                {
                    var bitMask = (uint)((1 << bit.Width) - 1) << bit.LowBit; used |= bitMask; var value = (raw & bitMask) >> bit.LowBit;
                    var enumeration = profile.Enums.Single(e => e.Id == bit.EnumId); var meaning = enumeration.Values.SingleOrDefault(v => v.Value == value);
                    values.Add(new("parameter", bit.Name, field.Offset, field.Bytes, bitMask, value, rawHex, meaning?.Key ?? "Unknown:" + value.ToString(CultureInfo.InvariantCulture), meaning is not null));
                }
                values.Add(new("reserved", field.Name + ".ReservedBits", field.Offset, field.Bytes, mask & ~used, raw & mask & ~used, rawHex, null, (raw & mask & ~used) == 0));
            }
        }
        var padding = Convert.ToHexString(payload.AsSpan(profile.ConfigurationBytes));
        var diagnostics = new List<string> { "native-envelope-and-CfgSize/CfgIDNum-semantics-not-validated", "signature-tuple-retained-not-certified", "packing-explicitly-selected-not-native-attested", "offsets-from-pinned-primitive-AOP-layout-no-native-execution" };
        if (series is null) diagnostics.Add(catalog == profile.Id ? "series-explicit-in-catalog-suffix" : "series-selected-from-pinned-identity");
        if (padding.Length > 0) diagnostics.Add("terminal-DWORD-padding-retained: " + padding);
        if (values.Any(v => v.Category == "parameter" && !v.Known)) diagnostics.Add("unknown-enum-values-present-no-semantic-equivalence-claim");
        if (values.Any(v => v.Category == "reserved" && v.Value != 0)) diagnostics.Add("nonzero-reserved-bits-retained-uninterpreted");
        var moduleSpan = source.Locate(module); var dataSpan = source.Locate(representations[0]);
        var context = source.Lexical(module).Remove(dataSpan.Start - moduleSpan.Start, dataSpan.End - dataSpan.Start);
        var contextHash = ProjectService.Hash(System.Text.Encoding.UTF8.GetBytes(context));
        return new(new(project.Id, project.Name, project.Revision, project.CurrentSha256), request.ModuleName, XmlProjects.PathOf(module), source.Fingerprint(module), contextHash, catalog!, profile.Series,
            (string)module.Attribute("Major")!, (string?)module.Attribute("Minor") ?? "unknown", profile, XmlProjects.PathOf(configuration), source.Fingerprint(configuration), request.Encoding, tokenCount,
            payload.Length, ProjectService.Hash(payload), (string?)configuration.Attribute("ConfigSize"), padding, values.ToArray(), diagnostics.ToArray(), Boundary);
    }
}

public sealed partial class ProjectService
{
    private SafetyIoStaticSnapshot BuildSafetyIoStatic(SafetyIoStaticBrowseRequest request)
    {
        if (request is null || request.Profile is null || !StructuralEditing.Hash(request.ExpectedProfileSha256)) throw new OperationException("safety_io_static_policy", "Pin exact project revision/hash, module, profile files/hash and explicit native encoding.");
        var source = StructuralSource(request.ProjectId, request.ExpectedSha256, request.ExpectedRevision); var profile = InspectSafetyIoStaticProfile(request.Profile);
        if (!profile.Sha256.Equals(request.ExpectedProfileSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Pinned static AOP profile hash changed.");
        return SafetyIoStaticDecoding.Decode(source.Project, source.Bytes, profile, request);
    }
    public async Task<SafetyIoStaticSnapshot> BrowseSafetyIoStaticAsync(SafetyIoStaticBrowseRequest request, CancellationToken ct = default)
    { await gate.WaitAsync(ct); try { return BuildSafetyIoStatic(request); } finally { gate.Release(); } }
    public async Task<SafetyIoStaticComparison> CompareSafetyIoStaticAsync(SafetyIoStaticCompareRequest request, CancellationToken ct = default)
    {
        if (request.Offset is < 0 or > 4096 || request.Limit is < 1 or > 200) throw new OperationException("safety_io_static_policy", "Use offset 0..4096 and limit 1..200.");
        await gate.WaitAsync(ct);
        try
        {
            var before = BuildSafetyIoStatic(request.Before); var after = BuildSafetyIoStatic(request.After);
            if (before.Profile.Id != after.Profile.Id || before.Profile.Sha256 != after.Profile.Sha256) throw new OperationException("safety_io_static_profile_mismatch", "Compare explicitly paired modules using the identical pinned model/layout/enums; cross-family alignment is not inferred.");
            var rows = before.Values.Zip(after.Values, (a, b) => new SafetyIoStaticDifference(a.Name, a.Category, a, b, a.Value == b.Value && a.Known == b.Known)).ToArray();
            var parameters = rows.Where(r => r.Category == "parameter").ToArray(); var selected = parameters.Where(r => request.IncludeUnchanged || !r.Equal).ToArray();
            var hash = Hash(JsonSerializer.SerializeToUtf8Bytes(new { before, after, adapter = "static-parameter-comparison-v1" }, Json));
            var report = new SafetyIoStaticComparison(hash, before, after, parameters.All(r => r.Equal), parameters.Any(r => !r.Before.Known || !r.After.Known), before.ModuleContextTextSha256 == after.ModuleContextTextSha256,
                before.NativeConfigSize == after.NativeConfigSize, before.TerminalPaddingHex == after.TerminalPaddingHex, parameters.Length,
                parameters.Count(r => !r.Equal), selected.Length, request.Offset, request.Offset + request.Limit < selected.Length ? request.Offset + request.Limit : null,
                selected.Skip(request.Offset).Take(request.Limit).ToArray(), rows.Where(r => r.Category != "parameter" && !r.Equal).ToArray(), SafetyIoStaticDecoding.Boundary);
            // Terminal padding is retained separately because it is outside the declared AOP fields.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, kind = "safety_io_static_comparison", producer = WorkspaceIdentity.Name, toolVersion = WorkspaceIdentity.Version, request, report,
                terminalPaddingEqual = before.TerminalPaddingHex == after.TerminalPaddingHex, nativeAcceptance = "not-performed", sourceFilesChanged = false }, Json);
            var receipt = StructuralArtifact(before.Source.Id, "json", bytes);
            return report with { Receipt = receipt };
        }
        finally { gate.Release(); }
    }
}
