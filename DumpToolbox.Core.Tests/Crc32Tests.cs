using System.Text;

namespace DumpToolbox.Core.Tests;

public sealed class Crc32Tests
{
    [Fact]
    public void ComputeMatchesStandardCheckValue()
    {
        uint crc = Crc32.Compute(Encoding.ASCII.GetBytes("123456789"));

        Assert.Equal(0xCBF43926u, crc);
    }

    [Fact]
    public void CombineMatchesConcatenatedInput()
    {
        byte[] prefix = Encoding.ASCII.GetBytes("Dump");
        byte[] suffix = Encoding.ASCII.GetBytes("Toolbox");
        byte[] combined = prefix.Concat(suffix).ToArray();

        uint result = Crc32.Combine(Crc32.Compute(prefix), Crc32.Compute(suffix), suffix.Length);

        Assert.Equal(Crc32.Compute(combined), result);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2352)]
    [InlineData(100_000)]
    public void InverseShiftRestoresOriginalCrc(long byteCount)
    {
        const uint original = 0xA5C31F29u;
        uint shifted = Crc32.CreateShiftOperator(byteCount).Apply(original);

        uint restored = Crc32.CreateInverseShiftOperator(byteCount).Apply(shifted);

        Assert.Equal(original, restored);
    }
}
