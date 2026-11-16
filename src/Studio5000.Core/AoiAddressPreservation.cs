using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AoiAddressPin(string ProgramName, string RoutineName, string Instruction, string InstanceOperand, int[] ArgumentIndices);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AoiAddressPlanRequest(MergeSnapshot Fresh, MergeSnapshot Logic, AoiAddressPin[] Pins);
public sealed record AoiAddressReview(AoiAddressPin Pin, string FreshXPath, string LogicXPath, string[] FreshOctets, string[] LogicOctets);
public sealed record AoiAddressPlan(ComparisonSnapshot Fresh, ComparisonSnapshot Logic, AoiAddressReview[] Addresses,
    StructuralEdit[] LogicEdits, string ReviewSha256, string Boundary);

internal static class AoiAddressPreservation
{
    internal const string Boundary = "Explicit per-device readable RLL AOI numeric IPv4 operands only. Instance is argument zero; four distinct zero-based address argument indices are caller-selected. Exact source hashes, unique call, decimal 0..255 literals and complete readable standard routines are required. No inferred device mapping, dotted-string rewrite, controller connection or native acceptance.";
    internal sealed record Site(XElement Text, int[] Starts, int[] Ends, string[] Octets);
    internal static void Validate(AoiAddressPin[]? pins)
    {
        if (pins is null || pins.Length is < 1 or > 64) throw new OperationException("aoi_address_policy", "Select 1..64 explicit device address pins.");
        foreach (var p in pins)
        {
            if (p is null) throw new OperationException("aoi_address_policy", "A device pin cannot be null.");
            WatchValidation.Identifier(p.ProgramName); WatchValidation.Identifier(p.RoutineName); WatchValidation.Identifier(p.Instruction);
            if (p.InstanceOperand is null || p.InstanceOperand.Length > 256 || !Regex.IsMatch(p.InstanceOperand, @"\A[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*|\[[0-9]+\])*\z", RegexOptions.CultureInvariant) ||
                p.ArgumentIndices is not { Length: 4 } || p.ArgumentIndices.Any(i => i is < 1 or > 63) || p.ArgumentIndices.Distinct().Count() != 4)
                throw new OperationException("aoi_address_policy", "Use an exact static instance operand and four distinct address indices 1..63; index zero is the instance.");
        }
        if (pins.Select(p => p.ProgramName + "/" + p.RoutineName + "/" + p.Instruction + "/" + p.InstanceOperand).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pins.Length)
            throw new OperationException("aoi_address_policy", "Each selected device call can appear once.");
    }
    internal static Site Locate(XDocument doc, AoiAddressPin pin)
    {
        var programs = doc.Root!.Element("Controller")!.Elements("Programs").Elements("Program").Where(p => LogicContext.Same((string?)p.Attribute("Name"), pin.ProgramName)).ToArray();
        var routines = programs.SelectMany(p => p.Elements("Routines").Elements("Routine")).Where(r => LogicContext.Same((string?)r.Attribute("Name"), pin.RoutineName)).ToArray();
        if (programs.Length != 1 || routines.Length != 1 || (string?)routines[0].Attribute("Type") != "RLL" || LogixPreservation.Safety(routines[0]))
            throw new OperationException("aoi_address_scope", "Select one existing standard program RLL routine.");
        var definitions = doc.Root.Element("Controller")!.Elements("AddOnInstructionDefinitions").Elements("AddOnInstructionDefinition").Where(a => LogicContext.Same((string?)a.Attribute("Name"), pin.Instruction)).ToArray();
        if (definitions.Length != 1 || LogixPreservation.Safety(definitions[0])) throw new OperationException("aoi_address_scope", "The selected instruction requires one readable standard AOI definition.");
        var sites = new List<Site>(); var totalText = 0;
        foreach (var rung in routines[0].Elements("RLLContent").Elements("Rung"))
        {
            if (rung.Attribute("OnlineEditType") is { Value: not "" and not "None" }) throw new OperationException("aoi_address_scope", "Online-edit rung variants are unsupported.");
            var texts = rung.Elements("Text").ToArray();
            if (texts.Length != 1 || texts[0].HasElements) throw new OperationException("aoi_address_scope", "Each selected rung requires one scalar Text.");
            var text = texts[0].Value;
            if ((totalText += text.Length) > 1024 * 1024) throw new OperationException("aoi_address_limit", "Selected routine exceeds 1 MiB text.");
            _ = LadderPreview.ParseLogic(text);
            var masked = new string(TagReferences.MaskSource(text, _ => { }));
            foreach (Match match in Regex.Matches(masked, @"(?<![A-Za-z0-9_])" + Regex.Escape(pin.Instruction) + @"\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                var starts = new List<int> { match.Index + match.Length }; var ends = new List<int>(); var depth = 1; var brackets = 0;
                for (var i = starts[0]; i < masked.Length; i++)
                {
                    var c = masked[i];
                    if (c == '(') depth++;
                    else if (c == ')' && --depth == 0) { ends.Add(i); break; }
                    else if (c == '[') brackets++;
                    else if (c == ']') brackets--;
                    else if (c == ',' && depth == 1 && brackets == 0) { ends.Add(i); starts.Add(i + 1); }
                }
                if (ends.Count != starts.Count || !LogicContext.Same(text[starts[0]..ends[0]].Trim(), pin.InstanceOperand)) continue;
                if (pin.ArgumentIndices.Any(i => i >= starts.Count)) throw new OperationException("aoi_address_operand", "A selected address index is outside this AOI call.");
                var selectedStarts = new int[4]; var selectedEnds = new int[4]; var octets = new string[4];
                for (var n = 0; n < 4; n++)
                {
                    var index = pin.ArgumentIndices[n]; var start = starts[index]; var end = ends[index];
                    while (start < end && char.IsWhiteSpace(text[start])) start++;
                    while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
                    var literal = text[start..end];
                    if (!uint.TryParse(literal, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value > 255)
                        throw new OperationException("aoi_address_operand", "Only decimal 0..255 address literals are supported; expressions, tags and radix forms require separate review.");
                    selectedStarts[n] = start; selectedEnds[n] = end; octets[n] = literal;
                }
                sites.Add(new(texts[0], selectedStarts, selectedEnds, octets));
            }
        }
        return sites.Count == 1 ? sites[0] : throw new OperationException("aoi_address_ambiguous", "The exact instruction/instance must select one call in the routine.");
    }
}

public sealed partial class ProjectService
{
    public async Task<AoiAddressPlan> PlanAoiAddressesAsync(AoiAddressPlanRequest request, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            AoiAddressPreservation.Validate(request.Pins);
            var fresh = PreservationSource(request.Fresh); var logic = PreservationSource(request.Logic);
            var before = ModuleConnectionEditing.Parse(fresh.Bytes); var reviewed = ModuleConnectionEditing.Parse(logic.Bytes); var lexical = new XmlSourceDocument(logic.Bytes, reviewed);
            var rows = new List<AoiAddressReview>(); var replacements = new Dictionary<XElement, List<(int Start, int End, string Value)>>();
            foreach (var pin in request.Pins)
            {
                var a = AoiAddressPreservation.Locate(before, pin); var b = AoiAddressPreservation.Locate(reviewed, pin);
                rows.Add(new(pin, XmlProjects.PathOf(a.Text), XmlProjects.PathOf(b.Text), a.Octets, b.Octets));
                if (!replacements.TryGetValue(b.Text, out var edits)) replacements[b.Text] = edits = [];
                for (var i = 0; i < 4; i++) edits.Add((b.Starts[i], b.Ends[i], a.Octets[i]));
            }
            var changes = new List<StructuralEdit>();
            foreach (var (node, replacementsForNode) in replacements)
            {
                var value = node.Value; var end = value.Length;
                foreach (var edit in replacementsForNode.OrderByDescending(e => e.Start))
                { if (edit.End > end) throw new OperationException("aoi_address_policy", "Selected device operands overlap."); value = value[..edit.Start] + edit.Value + value[edit.End..]; end = edit.Start; }
                if (value != node.Value) changes.Add(new("set_text", XmlProjects.PathOf(node), lexical.Fingerprint(node), value));
            }
            ComparisonSnapshot Snapshot(ProjectInfo p) => new(p.Id, p.Name, p.Revision, p.CurrentSha256);
            var plan = new AoiAddressPlan(Snapshot(fresh.Project), Snapshot(logic.Project), rows.ToArray(), changes.ToArray(), "", AoiAddressPreservation.Boundary);
            return plan with { ReviewSha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(plan, Json)) };
        }
        finally { gate.Release(); }
    }
}
