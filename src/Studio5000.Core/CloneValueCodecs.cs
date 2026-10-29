using System.Globalization;
using System.Numerics;
using System.Text;

namespace Studio5000.Core;

// Deliberately narrow saved-data codecs. A reviewed contract supplies exact
// L5K index paths; no UDT/AOI layout or native CIP operation is inferred.
internal static class CloneValueCodecs
{
    internal sealed record Node(int Start, int End, Node[]? Children);
    internal sealed record Edit(int Start, int Length, string Value);
    internal static OperationException Error(string message) => new("clone_contract", message);
    internal static Node Parse(string text)
    {
        var position = 0; var count = 0;
        void Space() { while (position < text.Length && char.IsWhiteSpace(text[position])) position++; }
        Node Read(int depth)
        {
            Space(); var start = position;
            if (++count > 100000 || depth > 64 || position >= text.Length) throw Error("L5K saved data exceeds its parse budget or is incomplete.");
            if (text[position] == '[')
            {
                position++; Space(); var children = new List<Node>();
                if (position < text.Length && text[position] != ']')
                    while (true) { children.Add(Read(depth + 1)); Space(); if (position >= text.Length || text[position] != ',') break; position++; }
                if (position >= text.Length || text[position++] != ']') throw Error("Only explicit bracket/comma L5K saved-data lists are supported.");
                return new(start, position, children.ToArray());
            }
            if (text[position] == '\'')
            {
                position++;
                while (position < text.Length && text[position] != '\'')
                {
                    if (text[position++] == '$')
                    {
                        if (position >= text.Length) throw Error("Incomplete L5K byte escape.");
                        if (Uri.IsHexDigit(text[position]) && position + 1 < text.Length && Uri.IsHexDigit(text[position + 1])) position += 2;
                        else position++;
                    }
                }
                if (position >= text.Length || text[position++] != '\'') throw Error("Incomplete L5K quoted byte string.");
            }
            else
            {
                while (position < text.Length && !char.IsWhiteSpace(text[position]) && text[position] is not (',' or ']' or '[' or '\'')) position++;
                if (position == start) throw Error("Unsupported L5K saved-data token.");
            }
            return new(start, position, null);
        }
        var node = Read(0); Space(); if (position != text.Length) throw Error("Unparsed L5K saved-data suffix."); return node;
    }
    internal static Node Select(Node root, int[]? path)
    {
        if (path is null || path.Length > 32) throw Error("L5K edits require an explicit index path (empty selects the root), up to 32 levels.");
        foreach (var index in path)
        { if (index < 0 || root.Children is null || index >= root.Children.Length) throw Error("L5K index path does not select saved data."); root = root.Children[index]; }
        return root;
    }
    internal static string Apply(string source, IEnumerable<Edit> edits)
    {
        var result = new StringBuilder(); var previous = 0;
        foreach (var edit in edits.OrderBy(e => e.Start))
        {
            if (edit.Start < previous || edit.Length < 1 || edit.Start + edit.Length > source.Length) throw Error("Overlapping or invalid clone edits.");
            result.Append(source, previous, edit.Start - previous); result.Append(edit.Value); previous = edit.Start + edit.Length;
        }
        return result.Append(source, previous, source.Length - previous).ToString();
    }
    internal static byte[] Bytes(string literal)
    {
        if (literal.Length < 2 || literal[0] != '\'' || literal[^1] != '\'') throw Error("A saved string requires one single-quoted Logix byte literal.");
        var bytes = new List<byte>();
        for (var i = 1; i < literal.Length - 1; i++)
        {
            var c = literal[i];
            if (c == '$')
            {
                if (++i >= literal.Length - 1) throw Error("Incomplete Logix byte escape.");
                if (i + 1 < literal.Length - 1 && Uri.IsHexDigit(literal[i]) && Uri.IsHexDigit(literal[i + 1]))
                { bytes.Add(byte.Parse(literal.Substring(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)); i++; }
                else bytes.Add(char.ToUpperInvariant(literal[i]) switch { '$' => 36, '\'' => 39, '"' => 34, 'L' or 'N' => 10, 'P' => 12, 'R' => 13, 'T' => 9, _ => throw Error("Unsupported Logix byte escape.") });
            }
            else { if (c is < ' ' or > '~' or '\'') throw Error("Non-ASCII saved bytes require explicit $HH escapes."); bytes.Add((byte)c); }
        }
        return bytes.ToArray();
    }
    internal static string Literal(byte[] bytes) => "'" + string.Concat(bytes.Select(b => b is >= 32 and <= 126 && b is not (36 or 38 or 39 or 60 or 62) ? ((char)b).ToString() : "$" + b.ToString("X2", CultureInfo.InvariantCulture))) + "'";
    internal static BigInteger Integer(string value)
    {
        var text = value.Replace("_", "", StringComparison.Ordinal); var parts = text.Split('#');
        if (parts.Length == 1 && BigInteger.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)) return number;
        if (parts.Length != 2 || !int.TryParse(parts[0], out var radix) || radix is not (2 or 8 or 16) || parts[1].Length is < 1 or > 64) throw Error("Expected an explicit decimal or 2#/8#/16# integer.");
        var result = BigInteger.Zero;
        foreach (var c in parts[1]) { var digit = "0123456789ABCDEF".IndexOf(char.ToUpperInvariant(c)); if (digit < 0 || digit >= radix) throw Error("Invalid saved integer."); result = result * radix + digit; }
        return result;
    }
    internal static string ReplaceInteger(string original, uint before, uint after, int bits, bool signed)
    {
        if (bits is not (8 or 16 or 32)) throw Error("Select an explicit 8, 16 or 32 bit saved integer.");
        var modulus = BigInteger.One << bits; var number = Integer(original);
        if (number < (signed ? -(modulus / 2) : 0) || number >= modulus || before >= modulus || after >= modulus || (number < 0 ? number + modulus : number) != before)
            throw Error("The saved numeric address differs from the reviewed mapping or integer width.");
        return (signed && after >= modulus / 2 ? after - modulus : after).ToString(CultureInfo.InvariantCulture);
    }
    internal static byte[] RebuildBuffer(byte[] original, int length, int capacity, IEnumerable<Edit> edits, bool padded)
    {
        if (capacity is < 1 or > 65536 || length < 0 || length > capacity || original.Length != (padded ? capacity : length) || original.Skip(length).Any(b => b != 0))
            throw Error("Saved LEN, padding and reviewed capacity must agree; nonzero bytes beyond LEN are unsupported.");
        var payload = Encoding.Latin1.GetString(original, 0, length); var changed = Encoding.Latin1.GetBytes(Apply(payload, edits));
        if (changed.Length > capacity) throw Error("The new saved string exceeds its original reviewed capacity.");
        if (!padded) return changed;
        var result = new byte[capacity]; changed.CopyTo(result, 0); return result;
    }
    internal static Edit ExtendedLink(byte[] payload, int offset, string before, string after)
    {
        var old = Encoding.ASCII.GetBytes(before); var next = Encoding.ASCII.GetBytes(after);
        // The contract pins the start of one non-extended-port, extended-link
        // segment, not a guessed occurrence inside an arbitrary binary blob.
        if (offset < 0 || offset + 2 + old.Length + (old.Length % 2) > payload.Length ||
            (payload[offset] & 0xf0) != 0x10 || (payload[offset] & 0x0f) == 0x0f || payload[offset + 1] != old.Length ||
            !payload.AsSpan(offset + 2, old.Length).SequenceEqual(old) || old.Length % 2 == 1 && payload[offset + 2 + old.Length] != 0)
            throw Error("The reviewed CIP extended-link segment/ASCII length/pad does not match saved bytes.");
        var segment = new byte[2 + next.Length + next.Length % 2]; segment[0] = payload[offset]; segment[1] = (byte)next.Length; next.CopyTo(segment, 2);
        return new(offset, 2 + old.Length + old.Length % 2, Encoding.Latin1.GetString(segment));
    }
}
