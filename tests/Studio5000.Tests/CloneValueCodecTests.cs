using System.Globalization;
using System.Text;
using Studio5000.Core;
using Xunit;

namespace Studio5000.Tests;

public sealed class CloneValueCodecTests
{
    [Fact]
    public void Byte_literals_round_trip_all_bytes_and_do_not_introduce_XML_delimiters()
    {
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray(); var literal = CloneValueCodecs.Literal(bytes);
        Assert.Equal(bytes, CloneValueCodecs.Bytes(literal)); Assert.DoesNotContain("<", literal); Assert.DoesNotContain(">", literal); Assert.DoesNotContain("&", literal);
        Assert.Equal(new byte[] { 10, 13, 9, 12, 36, 39, 34 }, CloneValueCodecs.Bytes("'$L$R$T$P$$$'$\"'"));
    }
    [Theory] [InlineData("['open]")] [InlineData("[1,]")] [InlineData("[1 2]")] [InlineData("[1]tail")] [InlineData("")]
    public void Malformed_L5K_data_is_rejected_without_guessing(string data) => Assert.Throws<OperationException>(() => CloneValueCodecs.Parse(data));
    [Fact]
    public void L5K_index_paths_select_exact_nested_tokens_and_preserve_siblings()
    {
        const string data = " [ [8,'10.0.0.1'], 16#FFFF_FFFF, 'leave$27it'] \r\n";
        var root = CloneValueCodecs.Parse(data); var selected = CloneValueCodecs.Select(root, [0, 1]); Assert.Equal("'10.0.0.1'", data[selected.Start..selected.End]);
        Assert.Throws<OperationException>(() => CloneValueCodecs.Select(root, [3])); Assert.Throws<OperationException>(() => CloneValueCodecs.Select(root, null));
        Assert.Equal(" [ [8,'10.0.0.2'], 16#FFFF_FFFF, 'leave$27it'] \r\n", CloneValueCodecs.Apply(data, [new(selected.Start, selected.End - selected.Start, "'10.0.0.2'")]));
    }
    [Theory] [InlineData("-64", 192, 168, 8, true, "-88")] [InlineData("16#C0", 192, 10, 8, true, "10")]
    [InlineData("255", 255, 128, 8, false, "128")] [InlineData("2#11111111", 255, 1, 8, false, "1")]
    public void Numeric_IP_fields_preserve_signed_bit_meaning(string raw, uint before, uint after, int bits, bool signed, string expected) =>
        Assert.Equal(expected, CloneValueCodecs.ReplaceInteger(raw, before, after, bits, signed));
    [Fact]
    public void Packed_unsigned_IPs_are_written_as_exact_signed_DINT_bit_patterns()
    { Assert.Equal(unchecked((int)0xc0a80505u).ToString(CultureInfo.InvariantCulture), CloneValueCodecs.ReplaceInteger("-1", uint.MaxValue, 0xc0a80505u, 32, true)); }
    [Theory] [InlineData("256", 0u, 1u, 8, false)] [InlineData("12.5", 12u, 13u, 32, true)] [InlineData("16#GG", 0u, 1u, 32, true)] [InlineData("1", 1u, 2u, 64, true)]
    public void Invalid_or_incompatible_numeric_fields_are_rejected(string raw, uint before, uint after, int bits, bool signed) =>
        Assert.Throws<OperationException>(() => CloneValueCodecs.ReplaceInteger(raw, before, after, bits, signed));
    [Fact]
    public void Buffer_shrink_keeps_original_capacity_and_zeros_only_the_padding()
    {
        var source = new byte[32]; Encoding.ASCII.GetBytes("192.168.5.5").CopyTo(source, 0);
        var changed = CloneValueCodecs.RebuildBuffer(source, 11, 32, [new(0, 11, "10.0.0.1")], true);
        Assert.Equal(32, changed.Length); Assert.Equal("10.0.0.1", Encoding.ASCII.GetString(changed, 0, 8)); Assert.All(changed.Skip(8), b => Assert.Equal(0, b));
        source[31] = 1; Assert.Throws<OperationException>(() => CloneValueCodecs.RebuildBuffer(source, 11, 32, [], true));
        Assert.Throws<OperationException>(() => CloneValueCodecs.RebuildBuffer(new byte[8], 8, 8, [new(0, 8, "192.168.5.5")], true));
    }
    [Fact]
    public void Simultaneous_source_offsets_do_not_cascade_and_overlap_is_rejected()
    {
        Assert.Equal("10.0.0.2|192.168.5.5", CloneValueCodecs.Apply("10.0.0.1|10.0.0.2", [new(0, 8, "10.0.0.2"), new(9, 8, "192.168.5.5")]));
        Assert.Throws<OperationException>(() => CloneValueCodecs.Apply("abc", [new(0, 2, "x"), new(1, 1, "y")]));
    }
    [Fact]
    public void CIP_link_shrink_repairs_the_length_and_removes_only_its_old_alignment_pad()
    {
        byte[] original = [0x12, 11, .. Encoding.ASCII.GetBytes("192.168.5.5"), 0, 1, 0];
        var edit = CloneValueCodecs.ExtendedLink(original, 0, "192.168.5.5", "10.0.0.1");
        var changed = Encoding.Latin1.GetBytes(CloneValueCodecs.Apply(Encoding.Latin1.GetString(original), [edit]));
        byte[] expected = [0x12, 8, .. Encoding.ASCII.GetBytes("10.0.0.1"), 1, 0]; Assert.Equal(expected, changed);
        original[13] = 2; Assert.Throws<OperationException>(() => CloneValueCodecs.ExtendedLink(original, 0, "192.168.5.5", "10.0.0.1"));
        original[13] = 0; original[0] = 0x1f; Assert.Throws<OperationException>(() => CloneValueCodecs.ExtendedLink(original, 0, "192.168.5.5", "10.0.0.1"));
    }
}
