using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Studio5000.Core;

public sealed record LogicTraceRegion(string Symbol, string MemberPath, string? DataType = null,
    int? BitOffset = null, int? BitLength = null, string? ArrayPath = null, int? ElementIndex = null,
    int? ElementBits = null, int? ArrayLength = null, string? Issue = null);

internal sealed class LogicTraceRegions(LogicContext context)
{
    private readonly Dictionary<string, XElement> types = context.Controller.Elements("DataTypes").Elements("DataType")
        .ToDictionary(t => (string)t.Attribute("Name")!, StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Symbol, string Tag, string? Program, string Member, string? Issue, XElement? Declaration), LogicTraceRegion> cache = [];

    internal static int Width(string? type) => type?.ToUpperInvariant() switch
    {
        "BIT" or "BOOL" => 1, "SINT" or "USINT" => 8, "INT" or "UINT" => 16,
        "DINT" or "UDINT" or "REAL" => 32, "LINT" or "ULINT" or "LREAL" => 64, _ => 0
    };

    internal LogicTraceRegion Resolve(LogicAddress address)
    {
        var key = (address.Symbol, address.Tag, address.Program, address.Member, address.Issue, address.Declaration);
        if (cache.TryGetValue(key, out var known)) return known;
        var region = Read(address);
        cache.Add(key, region);
        return region;
    }

    private LogicTraceRegion Read(LogicAddress address)
    {
        var declaration = address.Declaration ?? context.Tag(address.Tag, address.Program);
        var type = (string?)declaration?.Attribute("DataType");
        var member = ""; var at = 0; string? array = null; int? index = null, arrayLength = null;
        LogicTraceRegion Result(string? issue = null, int? bit = null) => new(address.Symbol, member, type,
            bit ?? (Width(type) > 0 ? 0 : null), bit is not null ? 1 : Width(type) > 0 ? Width(type) : null,
            array, index, Width(type) > 0 ? Width(type) : null, arrayLength, issue ?? address.Issue);
        if (declaration is null) return Result("declaration_missing");
        int[] dims;
        try { dims = LogixStructures.Dimensions((string?)declaration.Attribute("Dimensions") ?? (string?)declaration.Attribute("Dimension")); }
        catch (OperationException) { return Result("array_metadata_unknown"); }
        while (at < address.Member.Length)
        {
            if (address.Member[at] == '[')
            {
                var end = address.Member.IndexOf(']', at);
                if (end < 0) return Result("index_syntax_unknown");
                var parts = address.Member[(at + 1)..end].Split(',');
                if (dims.Length == 0 || parts.Length != dims.Length) return Result("array_rank_mismatch");
                long linear = 0, length = 1;
                var indices = new List<int>();
                for (var i = 0; i < parts.Length; i++)
                {
                    if (!int.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value))
                        return Result("dynamic_index_unknown");
                    if (value >= dims[i]) return Result("array_index_out_of_range");
                    linear = linear * dims[i] + value; length *= dims[i];
                    if (length > int.MaxValue) return Result("array_extent_unknown");
                    indices.Add(value);
                }
                array = member; index = (int)linear; arrayLength = (int)length;
                member += "[" + string.Join(",", indices) + "]";
                dims = []; at = end + 1;
                continue;
            }
            if (address.Member[at++] != '.') return Result("member_syntax_unknown");
            var start = at;
            while (at < address.Member.Length && (char.IsAsciiLetterOrDigit(address.Member[at]) || address.Member[at] == '_')) at++;
            var name = address.Member[start..at];
            if (dims.Length > 0) return Result("array_element_required");
            if (int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var bit))
            {
                var integerWidth = type?.ToUpperInvariant() is "SINT" or "USINT" or "INT" or "UINT" or "DINT" or "UDINT" or "LINT" or "ULINT" ? Width(type) : 0;
                if (integerWidth == 0 || bit >= integerWidth || at != address.Member.Length) return Result("bit_index_or_type_invalid");
                return Result(bit: bit);
            }
            if (type is null || !types.TryGetValue(type, out var definition)) return Result("member_layout_unknown");
            var field = definition.Elements("Members").Elements("Member").SingleOrDefault(m => LogicContext.Same((string?)m.Attribute("Name"), name));
            if (field is null) return Result("member_missing");
            if (field.Attribute("Target") is { } target)
            {
                var packed = definition.Elements("Members").Elements("Member").SingleOrDefault(m => LogicContext.Same((string?)m.Attribute("Name"), target.Value));
                var packedType = (string?)packed?.Attribute("DataType");
                var packedWidth = packedType?.ToUpperInvariant() is "SINT" or "INT" or "DINT" or "LINT" ? Width(packedType) : 0;
                if (type is null || at != address.Member.Length || (string?)field.Attribute("DataType") != "BIT" ||
                    !int.TryParse((string?)field.Attribute("BitNumber"), NumberStyles.None, CultureInfo.InvariantCulture, out var packedBit) ||
                    packedWidth == 0 || packedBit >= packedWidth || packed!.Attribute("Target") is not null ||
                    (string?)packed.Attribute("Dimension") is not (null or "0") || (string?)field.Attribute("Dimension") is not (null or "0"))
                    return Result("packed_member_target_unknown");
                member += "." + (string)packed.Attribute("Name")!;
                type = packedType;
                array = null; index = null; arrayLength = null;
                return Result(bit: packedBit);
            }
            member += "." + (string)field.Attribute("Name")!;
            type = (string?)field.Attribute("DataType");
            array = null; index = null; arrayLength = null;
            try { dims = LogixStructures.Dimensions((string?)field.Attribute("Dimension")); }
            catch (OperationException) { return Result("member_array_metadata_unknown"); }
        }
        if (dims.Length > 0)
        {
            long elements = 1;
            foreach (var dimension in dims)
            {
                elements *= dimension;
                if (elements > int.MaxValue) return Result("array_extent_unknown");
            }
            var width = Width(type);
            if (width == 0 || elements * width > int.MaxValue) return Result("array_element_required");
            return new(address.Symbol, member + "[" + string.Join(",", Enumerable.Repeat(0, dims.Length)) + "]", type,
                0, (int)elements * width, member, 0, width, (int)elements);
        }
        return Result();
    }

    internal static string Label(LogicTraceRegion region)
    {
        var label = region.Symbol + region.MemberPath;
        if (region.BitLength == 1 && region.ElementBits > 1) return label + "." + region.BitOffset;
        if (region.BitOffset is { } offset && region.BitLength is { } count && (offset != 0 || count != region.ElementBits))
            return label + "{bits " + offset + ".." + (offset + count - 1) + "}";
        return label;
    }

    private static bool Contains(string parent, string child) => parent.Length == 0 || LogicContext.Same(parent, child) ||
        child.StartsWith(parent + ".", StringComparison.OrdinalIgnoreCase) || child.StartsWith(parent + "[", StringComparison.OrdinalIgnoreCase);

    internal static bool Overlaps(LogicTraceRegion a, LogicTraceRegion b)
    {
        if (!LogicContext.Same(a.Symbol, b.Symbol)) return false;
        if (a.Issue is null && b.Issue is null &&
            a.ArrayPath is { } path && b.ArrayPath is not null && LogicContext.Same(path, b.ArrayPath) && a.ElementBits == b.ElementBits)
            return Start(a) < End(b) && Start(b) < End(a);
        if (!Contains(a.MemberPath, b.MemberPath) && !Contains(b.MemberPath, a.MemberPath)) return false;
        return a.Issue is not null || b.Issue is not null || !LogicContext.Same(a.MemberPath, b.MemberPath) ||
            a.BitOffset is null || b.BitOffset is null || Start(a) < End(b) && Start(b) < End(a);
    }

    private static long Start(LogicTraceRegion region) => (long)(region.ElementIndex ?? 0) * (region.ElementBits ?? 0) + (region.BitOffset ?? 0);
    private static long End(LogicTraceRegion region) => Start(region) + (region.BitLength ?? 0);

    internal static LogicTraceRegion? Map(LogicTraceRegion selected, LogicTraceRegion from, LogicTraceRegion to, string? mapping)
    {
        if (!Overlaps(selected, from)) return null;
        if (mapping != "bitwise" || selected.BitLength is null || from.BitLength is null || to.BitLength is null ||
            selected.Issue is not null || from.Issue is not null || to.Issue is not null ||
            !(LogicContext.Same(selected.MemberPath, from.MemberPath) ||
                selected.ArrayPath is not null && from.ArrayPath is not null && LogicContext.Same(selected.ArrayPath, from.ArrayPath)))
            return to;
        var first = Math.Max(Start(selected), Start(from)) - Start(from);
        var last = Math.Min(End(selected), End(from)) - Start(from);
        last = Math.Min(last, to.BitLength.Value);
        if (last <= first) return null;
        var position = Start(to) + first;
        if (to.ArrayPath is { } array && to.ElementBits is { } width)
        {
            var element = (int)(position / width);
            // Multi-dimensional array identity stays at the original element when no movement is needed.
            if (element != to.ElementIndex && to.MemberPath.Contains(',')) return to with { Issue = "multidimensional_copy_mapping_partial" };
            return to with { MemberPath = element == to.ElementIndex ? to.MemberPath : array + "[" + element + "]",
                ElementIndex = element, BitOffset = (int)(position % width), BitLength = (int)(last - first) };
        }
        return to with { BitOffset = (int)position, BitLength = (int)(last - first) };
    }

    internal static LogicTraceRegion Effect(LogicTraceRegion region, LadderPreview.Part part, int operand, LogicTraceRegion? destination)
    {
        if (part.Instruction.Equals("BTD", StringComparison.OrdinalIgnoreCase) && operand is 0 or 2)
        {
            var offsetIndex = operand == 0 ? 1 : 3;
            var width = region.DataType?.ToUpperInvariant() is "SINT" or "INT" or "DINT" ? Width(region.DataType) : 0;
            if (region.Issue is not null || part.Operands.Length != 5 || region.BitOffset != 0 || region.BitLength != width ||
                !Number(part.Operands[offsetIndex], out var offset) || !Number(part.Operands[4], out var count) ||
                width == 0 || offset >= width || count is < 1 or > 32)
                return region with { Issue = "btd_range_unknown" };
            return region with { BitOffset = offset, BitLength = Math.Min(count, width - offset) };
        }
        if ((part.Instruction.Equals("COP", StringComparison.OrdinalIgnoreCase) || part.Instruction.Equals("CPS", StringComparison.OrdinalIgnoreCase)) && operand is 0 or 1)
        {
            var width = CopyWidth(region.DataType); var destinationWidth = CopyWidth(destination?.DataType);
            if (region.Issue is null && destination?.Issue is null && part.Operands.Length == 3 && Number(part.Operands[2], out var count) &&
                count > 0 && width >= 8 && destinationWidth >= 8 && region.BitOffset == 0 && region.BitLength == width)
            {
                var bits = (long)destinationWidth * count;
                var available = (long)(region.ArrayLength is { } length ? length - region.ElementIndex!.Value : 1) * width;
                if (bits <= available && bits <= int.MaxValue) return region with { BitLength = (int)bits };
            }
            // COP can cross a member boundary into later base-tag bytes. No UDT offsets are inferred.
            return region with { MemberPath = "", BitOffset = null, BitLength = null, ArrayPath = null, ElementIndex = null,
                Issue = "copy_byte_layout_or_length_unknown" };
        }
        return region;
    }

    private static bool Number(string text, out int value) => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    private static int CopyWidth(string? type) => type?.ToUpperInvariant() is "SINT" or "USINT" or "INT" or "UINT" or "DINT" or "UDINT" or "LINT" or "ULINT" ? Width(type) : 0;

    internal static (string Tag, string Member) Selector(LogicTraceRequest request)
    {
        var tagName = request.TagName ?? "";
        var match = Regex.Match(tagName, @"\A(?<tag>[A-Za-z_][A-Za-z0-9_]*)(?<member>(?:\.[A-Za-z_0-9]+|\[[^\]]+\])*)\z", RegexOptions.CultureInvariant);
        if (!match.Success || tagName.Length > 256) throw new OperationException("logic_trace_query", "Select an exact tag, optional members, constant array indices and an integer bit.");
        var member = match.Groups["member"].Value;
        if (request.MemberPath is { Length: > 0 } path)
        {
            if (member.Length > 0) throw new OperationException("logic_trace_query", "Use inline members or memberPath, once.");
            member = path.StartsWith('[') || path.StartsWith('.') ? path : "." + path;
        }
        if (member.Length > 256 || !Regex.IsMatch(member, @"\A(?:\.[A-Za-z_0-9]+|\[\d+(?:,\s*\d+)*\])*\z", RegexOptions.CultureInvariant))
            throw new OperationException("logic_trace_query", "Member selectors require declared names, constant array indices and an optional terminal integer bit.");
        return (match.Groups["tag"].Value, member);
    }
}
