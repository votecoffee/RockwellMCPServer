using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafetyIoBrowseRequest(string ProjectId, string ExpectedSha256, int ExpectedRevision, string ModuleName,
    SafetyIoProfileFiles Profile, string ExpectedProfileSha256);
public sealed record SafetyIoInput(int Point, string Mode, string TestSource, int ModeOffset, int TestSourceOffset, string PairMode);
public sealed record SafetyIoTestOutput(int Point, string Mode, int Offset);
public sealed record SafetyIoSignature(string ConfigCRC, string ConfigTime, string ConfigDate, string Status);
public sealed record SafetyIoConfiguration(ComparisonSnapshot Source, string ModuleName, string ModuleXPath, string CatalogNumber,
    string Series, string Major, string Minor, SafetyIoProfile Profile, string ConfigurationXPath, string ConfigurationSha256,
    SafetyIoInput[] Inputs, SafetyIoTestOutput[] TestOutputs, SafetyIoSignature Signature, string[] Diagnostics, string Boundary);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafetyIoInputEdit(int Point, string ExpectedMode, string ExpectedTestSource, string Mode, string TestSource);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafetyIoTestOutputEdit(int Point, string ExpectedMode, string Mode);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafetyIoEditRequest(SafetyIoBrowseRequest Source, string ExpectedConfigurationSha256,
    SafetyIoInputEdit[] Inputs, SafetyIoTestOutputEdit[] TestOutputs, string SafetyHandling = "block", string SignatureHandling = "require-native");
public sealed record SafetyIoByteChange(string Field, int Offset, int Mask, int Before, int After);
public sealed record SafetyIoPreview(SafetyIoEditRequest Request, SafetyIoConfiguration Before, SafetyIoConfiguration After,
    SafetyIoByteChange[] Changes, bool CanApply, string? ResultSha256, string ReviewSha256, string SignatureStatus,
    bool NativeAopRoundTripRequired, bool UnselectedBytesPreserved, string[] Findings, string Boundary, Artifact? PreviewReceipt = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SafetyIoApplyRequest(SafetyIoEditRequest Preview, string ExpectedReviewSha256, string PreviewReceiptId, string PreviewReceiptSha256);
public sealed record SafetyIoResult(ProjectInfo Project, Artifact Candidate, Artifact Receipt, string ReviewSha256,
    bool ReviewOnly = true, string NativeAcceptance = "not-performed", string SignatureStatus = "unvalidated-after-parameter-change", bool NativeAopRoundTripRequired = true);
public sealed record SafetyIoStatus(Artifact PreviewReceipt, SafetyIoPreview Preview, string Status, SafetyIoResult? Result);
internal sealed record SafetyIoPreviewDocument(string Kind, int SchemaVersion, SafetyIoPreview Preview);
internal sealed record SafetyIoApplyState(string Status, string ReviewSha256, SafetyIoResult? Result);

internal static class SafetyIoConfigurationEditing
{
    internal const string Boundary = "Offline REVIEW ONLY 1734-IB8S/B major-2 configuration-byte inspection and point edits using pinned AOP identity/layout/enums and an exact saved source. Supports one 90-byte flat L5K vector only. Reserved bits, unselected fields, SCID tuple, SNN, network/timing, I/O data/forces, libraries and all unselected source bytes stay exact. The old ConfigCRC/ConfigTime/ConfigDate tuple is preserved as evidence, NOT regenerated or certified for changed parameters. Native AOP configuration/validation, save and independent readback are required; an XML import/build alone is not proof of signature regeneration. No SDK execution, controller connection, download or functional safety acceptance.";
    internal sealed record ByteToken(int Start, int Length, string Literal, byte Value);
    internal sealed record Decoded(SafetyIoConfiguration Info, XElement Data, byte[] Payload, ByteToken[] Tokens);
    private static OperationException Error(string detail) => new("safety_io_configuration", detail);
    private static ByteToken[] Tokens(string text)
    {
        if (text.Length > 16384) throw Error("Configuration text is limited to 16 KiB.");
        var start = 0; while (start < text.Length && char.IsWhiteSpace(text[start])) start++;
        var end = text.Length; while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        if (end - start < 2 || text[start] != '[' || text[end - 1] != ']') throw Error("Use one flat bracketed 90-byte L5K vector.");
        var result = new List<ByteToken>(); var position = start + 1;
        while (position < end - 1)
        {
            var match = Regex.Match(text[position..(end - 1)], @"\A\s*(?<n>(?:2|8|16)#[0-9A-Fa-f_]+|-?\d+)\s*(?<sep>,|\z)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
            if (!match.Success) throw Error("Only flat signed-decimal or unsigned radix byte literals are supported.");
            var literal = match.Groups["n"].Value; int value;
            try
            {
                var hash = literal.IndexOf('#'); value = hash < 0 ? int.Parse(literal, CultureInfo.InvariantCulture) : Convert.ToInt32(literal[(hash + 1)..].Replace("_", "", StringComparison.Ordinal), int.Parse(literal[..hash], CultureInfo.InvariantCulture));
            }
            catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException) { throw Error("Invalid byte literal."); }
            if (value is < -128 or > 255 || literal.Contains('#') && value < 0) throw Error("A raw configuration element is outside the signed/unsigned byte range.");
            result.Add(new(position + match.Groups["n"].Index, literal.Length, literal, unchecked((byte)value)));
            position += match.Length;
            if (result.Count > 90 || match.Groups["sep"].Value == "," && position == end - 1) throw Error("The vector must contain exactly 90 bytes without a trailing comma.");
        }
        if (position != end - 1 || result.Count != 90) throw Error("The vector must contain exactly 90 raw bytes; typed aggregate and nested formats are unsupported.");
        return result.ToArray();
    }
    private static string Format(ByteToken token, byte value)
    {
        var hash = token.Literal.IndexOf('#');
        if (hash < 0) return (token.Literal.StartsWith('-') ? unchecked((sbyte)value).ToString(CultureInfo.InvariantCulture) : value.ToString(CultureInfo.InvariantCulture));
        var radix = int.Parse(token.Literal[..hash], CultureInfo.InvariantCulture); var width = token.Literal[(hash + 1)..].Count(c => c != '_');
        return token.Literal[..(hash + 1)] + Convert.ToString(value, radix).ToUpperInvariant().PadLeft(width, '0');
    }
    internal static Decoded Decode(ProjectInfo project, XDocument document, SafetyIoProfiles profile, SafetyIoBrowseRequest request)
    {
        if (request.ModuleName is null || request.ModuleName.Length is < 1 or > 256 || profile.Info.Sha256 != request.ExpectedProfileSha256.ToLowerInvariant()) throw Error("Select one exact module and the current pinned profile hash.");
        var matches = document.Root!.Element("Controller")!.Elements("Modules").Elements("Module").Where(m => (string?)m.Attribute("Name") == request.ModuleName).ToArray();
        if (matches.Length != 1) throw Error("Select exactly one existing module name."); var module = matches[0];
        if ((string?)module.Attribute("CatalogNumber") != "1734-IB8S" || (string?)module.Attribute("Vendor") != "1" || (string?)module.Attribute("ProductType") != "35" || (string?)module.Attribute("ProductCode") != "15" ||
            (string?)module.Attribute("Major") != "2" || module.Attribute("Series") is { } series && series.Value != "B") throw Error("This adapter supports only the exact Allen-Bradley 1734-IB8S series-B/major-2 identity.");
        var configurations = module.Elements("Communications").Elements().Where(e => e.Name.LocalName is "ConfigData" or "ConfigTag").ToArray();
        if (configurations.Length != 1) throw Error("Exactly one existing ConfigData or ConfigTag is required."); var configuration = configurations[0];
        if ((string?)configuration.Attribute("ConfigSize") != "90" || configuration.Attribute("DataType") is { } type && type.Value != SafetyIoProfiles.DataType) throw Error("The configuration size/type does not match the pinned 90-byte layout.");
        var representations = configuration.Elements("Data").ToArray();
        if (representations.Length != 1 || (string?)representations[0].Attribute("Format") != "L5K" || representations[0].HasElements) throw Error("Only a single scalar L5K raw representation is supported; duplicate/alternate representations are not rewritten.");
        var data = representations[0]; var tokens = Tokens(data.Value); var payload = tokens.Select(t => t.Value).ToArray();
        string Integer(string field)
        {
            var f = profile.Field(field); var span = payload.AsSpan(f.Offset, f.Bytes);
            return (f.Bytes == 4 ? BinaryPrimitives.ReadUInt32LittleEndian(span) : BinaryPrimitives.ReadUInt16LittleEndian(span)).ToString(CultureInfo.InvariantCulture);
        }
        var inputs = Enumerable.Range(0, 8).Select(i => new SafetyIoInput(i, profile.Bit($"Pt{i:00}_Mode").Key(payload), profile.Bit($"Pt{i:00}_TestSource").Key(payload),
            profile.Bit($"Pt{i:00}_Mode").Storage.Offset, profile.Bit($"Pt{i:00}_TestSource").Storage.Offset, profile.Bit($"Pt{i / 2 * 2:00}_{i / 2 * 2 + 1:00}_Mode").Key(payload))).ToArray();
        var tests = Enumerable.Range(0, 4).Select(i => new SafetyIoTestOutput(i, profile.Bit($"Test{i:00}_Mode").Key(payload), profile.Bit($"Test{i:00}_Mode").Storage.Offset)).ToArray();
        var diagnostics = new List<string>();
        if (module.Attribute("Series") is null) diagnostics.Add("series-B-inferred-from-pinned-major-2-identity");
        foreach (var field in profile.Bits.Where(b => (payload[b.Storage.Offset] & ~b.Mask) != 0)) diagnostics.Add("reserved-bits-preserved: " + field.Name);
        if (inputs.Any(i => i.Mode.StartsWith("Unknown:", StringComparison.Ordinal) || i.TestSource.StartsWith("Unknown:", StringComparison.Ordinal) || i.PairMode.StartsWith("Unknown:", StringComparison.Ordinal)) || tests.Any(t => t.Mode.StartsWith("Unknown:", StringComparison.Ordinal))) diagnostics.Add("unknown-point-enum: native AOP review required");
        var info = new SafetyIoConfiguration(new(project.Id, project.Name, project.Revision, project.CurrentSha256), request.ModuleName, XmlProjects.PathOf(module), "1734-IB8S", "B", "2", (string?)module.Attribute("Minor") ?? "unknown",
            profile.Info, XmlProjects.PathOf(data), ProjectService.Hash(payload), inputs, tests,
            new(Integer("ConfigCRC"), Integer("ConfigTime"), Integer("ConfigDate"), "source-tuple-preserved; validity-not-established"), diagnostics.ToArray(), Boundary);
        return new(info, data, payload, tokens);
    }
    internal static (SafetyIoPreview Preview, byte[] Bytes) Build(ProjectInfo project, byte[] bytes, SafetyIoProfiles profile, SafetyIoEditRequest request)
    {
        if (request.Inputs is null || request.TestOutputs is null || request.Inputs.Length + request.TestOutputs.Length is < 1 or > 12 ||
            request.Inputs.Any(e => e is null || e.Point is < 0 or > 7) || request.TestOutputs.Any(e => e is null || e.Point is < 0 or > 3) ||
            request.Inputs.Select(e => e.Point).Distinct().Count() != request.Inputs.Length || request.TestOutputs.Select(e => e.Point).Distinct().Count() != request.TestOutputs.Length ||
            request.SafetyHandling is not ("block" or "review-only") || request.SignatureHandling is not ("require-native" or "preserve-unvalidated"))
            throw new OperationException("safety_io_policy", "Supply 1..12 unique point edits, explicit before modes/sources, safetyHandling block/review-only and signatureHandling require-native/preserve-unvalidated. Zeroing or inventing a signature is unsupported.");
        var doc = ModuleConnectionEditing.Parse(bytes); var source = new XmlSourceDocument(bytes, doc); var before = Decode(project, doc, profile, request.Source);
        if (!before.Info.ConfigurationSha256.Equals(request.ExpectedConfigurationSha256, StringComparison.OrdinalIgnoreCase)) throw new OperationException("stale_revision", "Configuration bytes changed; decode the exact source again.");
        var changed = (byte[])before.Payload.Clone(); var changes = new List<SafetyIoByteChange>();
        void Set(SafetyIoBitField field, string expected, string desired)
        {
            var actual = field.Key(before.Payload);
            if (actual != expected) throw new OperationException("stale_revision", "Point before-value differs: " + field.Name);
            var value = field.Enum.Values.SingleOrDefault(v => v.Key == desired) ?? throw new OperationException("safety_io_policy", "Choose a declared enum key for " + field.Name);
            if (before.Payload[field.Storage.Offset] != (before.Payload[field.Storage.Offset] & field.Mask)) throw Error("The selected field contains unrecognized reserved bits; native AOP review is required.");
            var offset = field.Storage.Offset; changed[offset] = (byte)((changed[offset] & ~field.Mask) | value.Value << field.LowBit);
            if (changed[offset] != before.Payload[offset]) changes.Add(new(field.Name, offset, field.Mask, before.Payload[offset], changed[offset]));
        }
        foreach (var edit in request.Inputs)
        {
            if (before.Info.Inputs[edit.Point].PairMode != "Single") throw Error("Editing paired/complementary input operation is outside this point-only adapter; use the native AOP.");
            Set(profile.Bit($"Pt{edit.Point:00}_Mode"), edit.ExpectedMode, edit.Mode); Set(profile.Bit($"Pt{edit.Point:00}_TestSource"), edit.ExpectedTestSource, edit.TestSource);
        }
        foreach (var edit in request.TestOutputs) Set(profile.Bit($"Test{edit.Point:00}_Mode"), edit.ExpectedMode, edit.Mode);
        for (var i = 0; i < 8; i++)
        {
            var mode = profile.Bit($"Pt{i:00}_Mode").Key(changed); var test = profile.Bit($"Pt{i:00}_TestSource").Key(changed);
            if (mode.StartsWith("Unknown:", StringComparison.Ordinal) || test.StartsWith("Unknown:", StringComparison.Ordinal)) throw Error("Unknown input enums require native AOP review.");
            if (mode == "SafetyPulseTest")
            {
                var index = test.Length == 2 && test[0] == 'T' ? test[1] - '0' : -1;
                if (index is < 0 or > 3 || profile.Bit($"Test{index:00}_Mode").Key(changed) != "PulseTest") throw Error("SafetyPulseTest inputs require an assigned PulseTest output; the review does not change coupled settings automatically.");
            }
            else if (test != "None") throw Error("Non-pulse-test input modes require test source None in this adapter.");
        }
        if (Enumerable.Range(0, 4).Any(i => profile.Bit($"Test{i:00}_Mode").Key(changed).StartsWith("Unknown:", StringComparison.Ordinal))) throw Error("Unknown test-output enums require native AOP review.");
        var editor = source.ValueEditor(before.Data);
        var patches = changes.Select(c => { var token = before.Tokens[c.Offset]; return editor(token.Start, token.Length, Format(token, (byte)c.After)); }).ToArray();
        var result = source.Apply(patches); var after = Decode(project, ModuleConnectionEditing.Parse(result), profile, request.Source);
        if (!after.Payload.SequenceEqual(changed) || before.Info.Signature != after.Info.Signature) throw Error("Configuration/signature readback differs from the exact selected-byte patches.");
        if (changes.Count > 0) after = after with { Info = after.Info with { Signature = after.Info.Signature with { Status = "unvalidated-after-parameter-change" } } };
        var findings = new List<string>();
        if (request.SafetyHandling != "review-only") findings.Add("BLOCKED: Explicit safetyHandling=review-only is required for a separate offline candidate.");
        if (request.SignatureHandling != "preserve-unvalidated") findings.Add("BLOCKED: Native SCID generation is unavailable. Explicit preserve-unvalidated emits only a review candidate with the original tuple retained.");
        if (changes.Count == 0) findings.Add("BLOCKED: No point bytes change.");
        findings.Add("The retained tuple is not certified for changed parameters. Use the matching native AOP on a trusted offline baseline, save and independently export/read back; zeroing the tuple and SDK import/build are not established regeneration contracts.");
        var canApply = findings.All(f => !f.StartsWith("BLOCKED:", StringComparison.Ordinal));
        var plan = new SafetyIoPreview(request, before.Info, after.Info, changes.ToArray(), canApply, canApply ? ProjectService.Hash(result) : null, "",
            changes.Count == 0 ? "source-tuple-preserved; validity-not-established" : "unvalidated-after-parameter-change", true, true, findings.ToArray(), Boundary);
        plan = plan with { ReviewSha256 = ProjectService.Hash(JsonSerializer.SerializeToUtf8Bytes(plan, ProjectService.Json)) };
        if (JsonSerializer.SerializeToUtf8Bytes(plan, ProjectService.Json).Length > 4 * 1024 * 1024) throw Error("The safety I/O review exceeds 4 MiB.");
        return (plan, result);
    }
}
