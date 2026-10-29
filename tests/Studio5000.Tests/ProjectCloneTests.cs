using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class ProjectCloneTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "RockwellClone", Guid.NewGuid().ToString("N"));
    private readonly ProjectService service;
    private const string Controller = "/RSLogix5000Content/Controller";
    private const string A = "10.0.0.1", B = "10.0.0.2", C = "192.168.5.5";
    public ProjectCloneTests() { Directory.CreateDirectory(root); service = new(new() { StateDirectory = Path.Combine(root, "state"), AllowedRoots = [root] }); }
    private static string Hash(string text) => ProjectService.Hash(Encoding.UTF8.GetBytes(text));
    private static string Tag(string name) => Controller + "/Tags/Tag[@Name='" + name + "']";
    private static string Data(string tag, string format) => Tag(tag) + "/Data[@Format='" + format + "']";
    private static string Source(string tags = "", string extra = "", string code = "MOV(7,Old);") =>
        "<?xml version='1.0' encoding='utf-8'?>\r\n<!-- original bytes -->\n<RSLogix5000Content TargetType='Controller' TargetName='Original'><Controller Name = 'Original' CommPath='2," + A + ",2," + B + "' SafetyNetworkNumber='retained-source'>" +
        "<Tags><Tag Name='Old' TagType='Base' DataType='LINT'><Data Format='L5K'><![CDATA[9223372036854775807]]></Data><Data Format='Opaque'><![CDATA[retained unknown format]]></Data></Tag>" + tags + "</Tags>" +
        "<Modules><Module Name='Self'><Ports><Port Id='1' Address='" + A + "'/></Ports></Module><Module Name='Peer'><Ports><Port Id='1' Address='" + B + "'/></Ports></Module></Modules>" +
        "<Programs><Program Name='Demo'><Routines><Routine Name='Main' Type='RLL'><RLLContent><Rung Number='0'><Comment><![CDATA[Old " + A + " documentation]]></Comment><Text><![CDATA[" + code + "]]></Text></Rung></RLLContent></Routine></Routines></Program></Programs>" +
        "<SafetyInfo Signature='retain'/><Ownership Owner='retain'/>" + extra + "</Controller></RSLogix5000Content>\r\n";
    private async Task<ProjectInfo> Register(string text, Encoding? encoding = null)
    { var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".l5x"); File.WriteAllText(path, text, encoding ?? new UTF8Encoding(true, true)); return await service.RegisterAsync(path); }
    private byte[] Revision(ProjectInfo project) => File.ReadAllBytes(Path.Combine(root, "state", "projects", project.Id, "revision-" + project.Revision + ".l5x"));
    private CloneField Field(ProjectInfo project, string xpath, string? attribute = null, string codec = "text")
    {
        var element = XmlProjects.Select(XmlProjects.Parse(Revision(project)), xpath); var value = attribute is null ? element.Value : (string)element.Attribute(attribute)!;
        return new(xpath, attribute, Hash(value), codec);
    }
    private static ProjectCloneRequest Request(ProjectInfo project, CloneAddressChange[]? addresses = null, CloneIdentityChange[]? identities = null) =>
        new(new(project.Id, project.CurrentSha256), "Reviewed clone", identities ?? [], addresses ?? []);
    private CloneAddressChange Module(ProjectInfo p, string module, string before, string after) => new(module, before, after,
        [new("module-address", Field(p, Controller + "/Modules/Module[@Name='" + module + "']/Ports/Port", "Address"))], "Reviewed module route only; no runtime initializer for this module binding.");
    private async Task<ProjectCloneResult> Apply(ProjectCloneRequest request)
    { var plan = await service.PreviewProjectCloneAsync(request); Assert.True(plan.CanApply, string.Join("; ", plan.Findings.Where(f => f.Status == "blocked").Select(f => f.Detail))); return await service.ApplyProjectCloneAsync(new(request, plan.ReviewSha256)); }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Simultaneous_routes_and_identities_preserve_unselected_values_source_encoding_and_protected_configuration(bool unicode)
    {
        var encoding = unicode ? (Encoding)new UnicodeEncoding(false, true, true) : new UTF8Encoding(true, true);
        var text = Source().Replace("encoding='utf-8'", "encoding='" + encoding.WebName + "'"); var p = await Register(text, encoding); var original = Revision(p);
        var path = Field(p, Controller, "CommPath");
        var self = Module(p, "Self", A, B) with { Sites = [.. Module(p, "Self", A, B).Sites, new("controller-comm-path", path with { Offset = 2 })] };
        var peer = Module(p, "Peer", B, C) with { Sites = [.. Module(p, "Peer", B, C).Sites, new("controller-comm-path", path with { Offset = 2 + A.Length + 3 })] };
        var codePath = Controller + "/Programs/Program/Routines/Routine/RLLContent/Rung/Text";
        CloneIdentityChange[] names = [new("controller", "Original", "Clone", [new("controller-name", Field(p, Controller, "Name")), new("project-target-name", Field(p, "/RSLogix5000Content", "TargetName"))]),
            new("tag", "Old", "Renamed", [new("symbol-declaration", Field(p, Tag("Old"), "Name")), new("symbol-reference", Field(p, codePath) with { Offset = 6 })])];
        var request = Request(p, [self, peer], names); var result = await Apply(request);
        var expected = text.Replace("TargetName='Original'", "TargetName='Clone'").Replace("Name = 'Original'", "Name = 'Clone'").Replace("Name='Old'", "Name='Renamed'").Replace("MOV(7,Old)", "MOV(7,Renamed)")
            .Replace("CommPath='2," + A + ",2," + B + "'", "CommPath='2," + B + ",2," + C + "'")
            .Replace("Id='1' Address='" + A + "'", "Id='1' Address='" + B + "'").Replace("Name='Peer'><Ports><Port Id='1' Address='" + B + "'", "Name='Peer'><Ports><Port Id='1' Address='" + C + "'");
        Assert.Equal(encoding.GetPreamble().Concat(encoding.GetBytes(expected)), Revision(result.Project)); Assert.Equal(original, Revision(p)); Assert.Equal(original, File.ReadAllBytes(p.SourcePath));
        Assert.NotEqual(p.Id, result.Project.Id); Assert.Contains("Offline source-bound clone", result.Receipt.Validation);
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(service.Download(result.Receipt.Id).Path)); Assert.Equal(result.ReviewSha256, receipt.RootElement.GetProperty("plan").GetProperty("reviewSha256").GetString());
    }

    private static string NumericTag(string name, bool array = false) => "<Tag Name='" + name + "' TagType='Base' DataType='" + (array ? "DINT' Dimensions='4" : "IP_TYPE") + "'><Data Format='L5K'><![CDATA[[10,0,0,1]]]></Data><Data Format='Decorated'>" +
        (array ? "<Array DataType='DINT' Dimensions='4'>" + string.Concat(Enumerable.Range(0, 4).Select(i => "<Element Index='[" + i + "]' Value='" + (i == 0 ? 10 : i == 3 ? 1 : 0) + "'/>")) + "</Array>" :
        "<Structure DataType='IP_TYPE'>" + string.Concat(Enumerable.Range(0, 4).Select(i => "<DataValueMember Name='O" + i + "' DataType='DINT' Value='" + (i == 0 ? 10 : i == 3 ? 1 : 0) + "'/>")) + "</Structure>") + "</Data></Tag>";
    private CloneSite[] NumericSites(ProjectInfo p, string tag, bool array)
    {
        var raw = Field(p, Data(tag, "L5K"), codec: "integer");
        return Enumerable.Range(0, 4).SelectMany(i => new CloneSite[] { new("saved-value", raw with { L5kPath = [i], Octet = i }, "address"),
            new("saved-value", Field(p, Data(tag, "Decorated") + (array ? "/Array/Element[@Index='[" + i + "]']" : "/Structure/DataValueMember[@Name='O" + i + "']"), "Value", "integer") with { Octet = i }, "address") }).ToArray();
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Numeric_UDT_and_HMI_array_representations_are_synchronized(bool array)
    {
        var p = await Register(Source(NumericTag("Address", array))); var sites = NumericSites(p, "Address", array);
        var request = Request(p, [new("address", A, C, sites, "Reviewed saved cache and initialization sites; no source initializer overwrites these values.")]);
        var result = await Apply(request); var doc = XmlProjects.Parse(Revision(result.Project));
        Assert.Equal("[192,168,5,5]", XmlProjects.Select(doc, Data("Address", "L5K")).Value);
        Assert.Equal(new[] { "192", "168", "5", "5" }, XmlProjects.Select(doc, Data("Address", "Decorated")).Descendants().Attributes("Value").Select(a => a.Value));
    }
    [Fact]
    public async Task Missing_or_opaque_representation_and_mismatched_numeric_cache_block_apply()
    {
        var p = await Register(Source(NumericTag("Address"))); var sites = NumericSites(p, "Address", false);
        var request = Request(p, [new("address", A, C, sites.Where(s => s.Field.Codec == "integer" && s.Field.Attribute is null).ToArray(), "Reviewed")]);
        var plan = await service.PreviewProjectCloneAsync(request); Assert.False(plan.CanApply); Assert.Contains(plan.Findings, f => f.Detail.Contains("unhandled saved representation"));
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyProjectCloneAsync(new(request, plan.ReviewSha256)));
        var stale = await Register(Source(NumericTag("Address").Replace("Name='O0' DataType='DINT' Value='10'", "Name='O0' DataType='DINT' Value='11'")));
        plan = await service.PreviewProjectCloneAsync(Request(stale, [new("address", A, C, NumericSites(stale, "Address", false), "Reviewed")])); Assert.False(plan.CanApply);
        plan = await service.PreviewProjectCloneAsync(Request(p, [new("address", A, C, sites.Where(s => s.Field.Octet == 0).ToArray(), "Reviewed")])); Assert.False(plan.CanApply);
        Assert.Contains(plan.Findings, f => f.Detail.Contains("missing a changed octet"));
        var opaque = await Register(Source(NumericTag("Address").Replace("</Tag>", "<Data Format='Raw'>ABC</Data></Tag>")));
        plan = await service.PreviewProjectCloneAsync(Request(opaque, [new("address", A, C, NumericSites(opaque, "Address", false), "Reviewed")])); Assert.False(plan.CanApply);
    }
    private static string StringTag(string name, byte[] payload, int capacity, bool hidden = false)
    {
        var padded = new byte[capacity]; payload.CopyTo(padded, 0); var literal = CloneValueCodecs.Literal(padded);
        return "<Tag Name='" + name + "' TagType='Base' DataType='" + (hidden ? "CacheAOI" : "STRING") + "'><Data Format='L5K'><![CDATA[" + (hidden ? "[" : "") + "[" + payload.Length + "," + literal + "]" + (hidden ? ",77]" : "") + "]]></Data>" +
            (hidden ? "<Data Format='Decorated'><Structure DataType='CacheAOI'><DataValueMember Name='Visible' DataType='DINT' Value='77'/></Structure></Data>" : "<Data Format='String' Length='" + payload.Length + "'><![CDATA[" + literal + "]]></Data>") + "</Tag>";
    }
    private CloneSite[] StringSites(ProjectInfo p, string tag, int capacity, bool cip = false, bool hidden = false)
    {
        var raw = Field(p, Data(tag, "L5K"), codec: cip ? "cip-l5k-string" : "l5k-string") with { L5kPath = hidden ? [0] : [], Capacity = capacity, Padded = true, LinkOffset = cip ? 2 : null,
            HiddenLocalName = hidden ? "HiddenRoute" : null, HiddenLocalReason = hidden ? "Reviewed exported AOI local layout; this local is absent from Decorated output." : null };
        if (hidden) return [new("hidden-cache", raw)];
        var len = Field(p, Data(tag, "String"), "Length");
        var text = Field(p, Data(tag, "String"), codec: cip ? "cip-string" : "string") with { Capacity = capacity, Padded = true, LengthField = new(len.XPath, len.Attribute!, len.ValueSha256), LinkOffset = cip ? 2 : null };
        return [new("saved-value", raw), new("saved-value", text)];
    }
    [Fact]
    public async Task String_LEN_and_original_padding_capacity_are_updated_in_both_representations()
    {
        var p = await Register(Source(StringTag("Address", Encoding.ASCII.GetBytes(A), 32))); var request = Request(p, [new("address", A, C, StringSites(p, "Address", 32), "Reviewed cached text; no runtime initializer.")]);
        var result = await Apply(request); var doc = XmlProjects.Parse(Revision(result.Project)); var data = XmlProjects.Select(doc, Data("Address", "String"));
        var bytes = CloneValueCodecs.Bytes(data.Value); Assert.Equal(32, bytes.Length); Assert.Equal(C.Length, (int)data.Attribute("Length")!); Assert.Equal(C, Encoding.ASCII.GetString(bytes, 0, C.Length)); Assert.All(bytes.Skip(C.Length), b => Assert.Equal(0, b));
        Assert.StartsWith("[" + C.Length + ",", XmlProjects.Select(doc, Data("Address", "L5K")).Value);
    }
    [Fact]
    public async Task Multiline_CDATA_string_wrappers_retain_their_original_whitespace()
    {
        var tag = StringTag("Address", Encoding.ASCII.GetBytes(A), 82).Replace("<![CDATA[", "\r\n  <![CDATA[").Replace("]]>", "]]>\r\n");
        var p = await Register(Source(tag)); var result = await Apply(Request(p, [new("address", A, C, StringSites(p, "Address", 82), "Reviewed multiline native-style wrappers.")]));
        var text = Encoding.UTF8.GetString(Revision(result.Project)); Assert.Contains("\r\n  <![CDATA[", text); Assert.Contains("]]>\r\n</Data>", text);
        var data = XmlProjects.Select(XmlProjects.Parse(Revision(result.Project)), Data("Address", "String")); Assert.Equal(C, Encoding.ASCII.GetString(CloneValueCodecs.Bytes(data.Value.Trim()), 0, C.Length)); Assert.Equal(C.Length, (int)data.Attribute("Length")!);
    }
    [Fact]
    public async Task Binary_CIP_extended_link_repairs_ASCII_length_word_pad_outer_LEN_and_capacity()
    {
        byte[] original = [1, 0, 0x12, (byte)A.Length, .. Encoding.ASCII.GetBytes(A), 1, 0];
        var p = await Register(Source(StringTag("Route", original, 48))); var request = Request(p, [new("route", A, C, StringSites(p, "Route", 48, cip: true), "Reviewed cached binary path and native initializer sources.")]);
        var result = await Apply(request); var data = XmlProjects.Select(XmlProjects.Parse(Revision(result.Project)), Data("Route", "String")); var bytes = CloneValueCodecs.Bytes(data.Value);
        Assert.Equal(C.Length, bytes[3]); Assert.Equal(C, Encoding.ASCII.GetString(bytes, 4, C.Length)); Assert.Equal(0, bytes[4 + C.Length]); Assert.Equal(new byte[] { 1, 0 }, bytes.Skip(5 + C.Length).Take(2));
        Assert.Equal(7 + C.Length, (int)data.Attribute("Length")!); Assert.Equal(48, bytes.Length);
        var invalid = original.ToArray(); invalid[3]--;
        var bad = await Register(Source(StringTag("Route", invalid, 48))); var blocked = await service.PreviewProjectCloneAsync(Request(bad, [new("route", A, C, StringSites(bad, "Route", 48, cip: true), "Reviewed")])); Assert.False(blocked.CanApply);
    }
    [Fact]
    public async Task Hidden_AOI_local_L5K_only_cache_needs_named_local_reason_and_exact_path()
    {
        var definition = "<AddOnInstructionDefinitions><AddOnInstructionDefinition Name='CacheAOI'><LocalTags><LocalTag Name='HiddenRoute' DataType='STRING'/></LocalTags></AddOnInstructionDefinition></AddOnInstructionDefinitions>";
        var p = await Register(Source(StringTag("Cache", Encoding.ASCII.GetBytes(A), 32, hidden: true), definition)); var sites = StringSites(p, "Cache", 32, hidden: true);
        var result = await Apply(Request(p, [new("cache", A, C, sites, "Reviewed local cache offsets against the exported AOI.")]));
        Assert.Contains(C, XmlProjects.Select(XmlProjects.Parse(Revision(result.Project)), Data("Cache", "L5K")).Value);
        Assert.Equal(XmlProjects.Select(XmlProjects.Parse(Revision(p)), Data("Cache", "Decorated")).ToString(), XmlProjects.Select(XmlProjects.Parse(Revision(result.Project)), Data("Cache", "Decorated")).ToString());
        var invalid = sites.Select(s => s with { Field = s.Field with { HiddenLocalName = "Unreviewed" } }).ToArray();
        Assert.False((await service.PreviewProjectCloneAsync(Request(p, [new("cache", A, C, invalid, "Reviewed")]))).CanApply);
    }
    [Theory] [InlineData("network")] [InlineData("little-endian")]
    public async Task Packed_saved_addresses_and_runtime_initializers_use_the_same_reviewed_bit_order(string order)
    {
        uint Packed(string ip) { var b = ip.Split('.').Select(byte.Parse).ToArray(); if (order == "little-endian") Array.Reverse(b); return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3]; }
        var before = unchecked((int)Packed(A)).ToString(System.Globalization.CultureInfo.InvariantCulture); var after = unchecked((int)Packed(C)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var tag = "<Tag Name='Cache' TagType='Base' DataType='DINT'><Data Format='L5K'><![CDATA[" + before + "]]></Data><Data Format='Decorated'><DataValue DataType='DINT' Value='" + before + "'/></Data></Tag>";
        var p = await Register(Source(tag, code: "MOV(" + before + ",Cache);"));
        CloneSite[] Sites(ProjectInfo source) => [new("saved-value", Field(source, Data("Cache", "L5K"), codec: "integer") with { L5kPath = [], ByteOrder = order }),
            new("saved-value", Field(source, Data("Cache", "Decorated") + "/DataValue", "Value", "integer") with { ByteOrder = order }),
            new("runtime-initializer", Field(source, Controller + "/Programs/Program/Routines/Routine/RLLContent/Rung/Text", codec: "integer") with { Offset = 4, TokenLength = before.Length, ByteOrder = order })];
        var result = await Apply(Request(p, [new("cache", A, C, Sites(p), "Reviewed exact saved cache and MOV initializer together.")])); var doc = XmlProjects.Parse(Revision(result.Project));
        Assert.Equal(after, XmlProjects.Select(doc, Data("Cache", "L5K")).Value); Assert.Equal("MOV(" + after + ",Cache);", doc.Descendants("Text").Single().Value);
        var mismatch = await Register(Source(tag, code: "MOV(" + (unchecked((int)Packed(A)) + 1) + ",Cache);")); Assert.False((await service.PreviewProjectCloneAsync(Request(mismatch, [new("cache", A, C, Sites(mismatch), "Reviewed")]))).CanApply);
    }
    [Fact]
    public async Task Decorated_STRING_sibling_LEN_and_MESSAGE_path_have_typed_saved_readback()
    {
        var scalar = StringTag("Address", Encoding.ASCII.GetBytes(A), 32); var originalLiteral = CloneValueCodecs.Literal([.. Encoding.ASCII.GetBytes(A), .. new byte[32 - A.Length]]);
        var decorated = "<Data Format='Decorated'><Structure DataType='STRING'><DataValueMember Name='LEN' DataType='DINT' Value='" + A.Length + "'/><DataValueMember Name='DATA' DataType='STRING'><![CDATA[" + originalLiteral + "]]></DataValueMember></Structure></Data>";
        scalar = scalar.Replace("<Data Format='String' Length='" + A.Length + "'><![CDATA[" + originalLiteral + "]]></Data>", decorated);
        var message = "<Tag Name='Request' TagType='Base' DataType='MESSAGE'><Data Format='Message'><MessageParameters CommPath='2," + A + "' RemoteElement='PeerData' MessageType='CIP Data Table Read'/></Data></Tag>";
        var p = await Register(Source(scalar + message)); var len = Field(p, Data("Address", "Decorated") + "/Structure/DataValueMember[@Name='LEN']", "Value");
        CloneSite[] sites = [new("saved-value", Field(p, Data("Address", "L5K"), codec: "l5k-string") with { L5kPath = [], Capacity = 32, Padded = true }),
            new("saved-value", Field(p, Data("Address", "Decorated") + "/Structure/DataValueMember[@Name='DATA']", codec: "string") with { Capacity = 32, Padded = true, LengthField = new(len.XPath, len.Attribute!, len.ValueSha256) }),
            new("message-path", Field(p, Data("Request", "Message") + "/MessageParameters", "CommPath") with { Offset = 2 })];
        var result = await Apply(Request(p, [new("address", A, C, sites, "Reviewed saved string and explicit MESSAGE route; no initializer.")])); var doc = XmlProjects.Parse(Revision(result.Project));
        Assert.Equal(C.Length.ToString(), (string?)XmlProjects.Select(doc, len.XPath).Attribute("Value")); Assert.Equal("2," + C, (string?)XmlProjects.Select(doc, Data("Request", "Message") + "/MessageParameters").Attribute("CommPath"));
    }
    [Fact]
    public async Task Multiple_IPs_in_one_padded_string_use_original_offsets_without_cascading()
    {
        var payload = A + "|" + B; var p = await Register(Source(StringTag("Address", Encoding.ASCII.GetBytes(payload), 64))); var first = StringSites(p, "Address", 64);
        var second = first.Select(s => s with { Field = s.Field with { Offset = A.Length + 1 } }).ToArray();
        var result = await Apply(Request(p, [new("self", A, B, first, "Reviewed first address."), new("peer", B, C, second, "Reviewed second address.")]));
        var value = XmlProjects.Select(XmlProjects.Parse(Revision(result.Project)), Data("Address", "String")); var bytes = CloneValueCodecs.Bytes(value.Value);
        Assert.Equal(B + "|" + C, Encoding.ASCII.GetString(bytes, 0, (int)value.Attribute("Length")!)); Assert.Equal(64, bytes.Length);
    }
    [Fact]
    public async Task Supplied_saved_readback_reports_value_and_protected_configuration_differences_without_origin_claim()
    {
        var p = await Register(Source()); var request = Request(p, [Module(p, "Peer", B, C)]); var plan = await service.PreviewProjectCloneAsync(request); var result = await service.ApplyProjectCloneAsync(new(request, plan.ReviewSha256));
        var readback = await service.ReadbackProjectCloneAsync(new(request, plan.ReviewSha256, new(result.Project.Id, result.Project.CurrentSha256))); Assert.True(readback.Matches); Assert.False(readback.NativeOriginVerified);
        foreach (var altered in new[] { Encoding.UTF8.GetString(Revision(result.Project)).TrimStart('\uFEFF').Replace("retained-source", "changed"), Encoding.UTF8.GetString(Revision(result.Project)).TrimStart('\uFEFF').Replace("9223372036854775807", "9223372036854775806") })
        { var different = await Register(altered); var mismatch = await service.ReadbackProjectCloneAsync(new(request, plan.ReviewSha256, new(different.Id, different.CurrentSha256))); Assert.False(mismatch.Matches); Assert.Contains(mismatch.Findings, f => f.Key is "attribute" or "content"); }
        await Assert.ThrowsAsync<OperationException>(() => service.ApplyProjectCloneAsync(new(request with { Name = "Different review" }, plan.ReviewSha256)));
    }
    [Theory] [InlineData("SafetyInfo", "Signature")] [InlineData("Ownership", "Owner")]
    public async Task Safety_ownership_and_physical_configuration_are_excluded(string element, string attribute)
    {
        var p = await Register(Source()); var selected = Field(p, Controller + "/" + element, attribute);
        var request = Request(p, identities: [new("protected", "retain", "changed", [new("symbol-reference", selected)])]); Assert.False((await service.PreviewProjectCloneAsync(request)).CanApply);
        var physical = Request(p, [new("physical", A, C, [new("physical-tcpip", Field(p, Controller + "/Modules/Module[@Name='Self']/Ports/Port", "Address"))], "Reviewed")]); Assert.False((await service.PreviewProjectCloneAsync(physical)).CanApply);
    }
    [Fact]
    public async Task Overlap_capacity_and_stale_field_hashes_fail_closed()
    {
        var p = await Register(Source(StringTag("Address", Encoding.ASCII.GetBytes(A), A.Length))); var module = Module(p, "Self", A, B);
        Assert.False((await service.PreviewProjectCloneAsync(Request(p, [module, module with { Key = "duplicate" }]))).CanApply);
        Assert.False((await service.PreviewProjectCloneAsync(Request(p, [module with { Sites = [module.Sites[0] with { Field = module.Sites[0].Field with { ValueSha256 = new string('0', 64) } }] }]))).CanApply);
        Assert.False((await service.PreviewProjectCloneAsync(Request(p, [new("capacity", A, C, StringSites(p, "Address", A.Length), "Reviewed")]))).CanApply);
    }
    [Fact]
    public async Task Unpaired_project_identity_and_partial_exports_are_rejected()
    {
        var p = await Register(Source());
        var request = Request(p, identities: [new("controller", "Original", "Clone", [new("controller-name", Field(p, Controller, "Name"))])]);
        Assert.False((await service.PreviewProjectCloneAsync(request)).CanApply);
        var partial = await Register(Source().Replace("TargetType='Controller'", "TargetType='Routine'"));
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewProjectCloneAsync(Request(partial, [Module(partial, "Self", A, B)])));
        await Assert.ThrowsAsync<OperationException>(() => service.PreviewProjectCloneAsync(Request(p, [Module(p, "Self", A, B)]) with { Source = new(p.Id, new string('0', 64)) }));
    }
    public void Dispose() { service.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
}
