namespace DumpToolbox.Core.Tests;

public sealed class TargetParserTests
{
    [Fact]
    public void ParseRedumpFilenameRowPreservesOutputName()
    {
        const string row = "Track 02.bin 2352 cbf43926 0123456789abcdef0123456789abcdef 0123456789abcdef0123456789abcdef01234567";

        HashTarget target = Assert.Single(TargetParser.Parse(row));

        Assert.Equal(2352, target.Size);
        Assert.Equal(0xCBF43926u, target.Crc32);
        Assert.Equal("Track 02.bin", target.Label);
        Assert.Equal("Track 02.bin", target.OutputFileName);
        Assert.Equal("0123456789abcdef0123456789abcdef01234567", target.NormalizedSha1);
    }

    [Fact]
    public void ParseXmlDatSkipsCueEntries()
    {
        const string xml = """
            <game name="Disc">
              <rom name="Disc.cue" size="100" crc="00000000" md5="00000000000000000000000000000000" />
              <rom name="Track 01.bin" size="2048" crc="89abcdef" md5="0123456789abcdef0123456789abcdef" />
            </game>
            """;

        HashTarget target = Assert.Single(TargetParser.Parse(xml));

        Assert.Equal("Track 01.bin", target.OutputFileName);
        Assert.Equal(2048, target.Size);
        Assert.Equal(0x89ABCDEFu, target.Crc32);
    }

    [Fact]
    public void ParseRejectsInputWithoutTargets()
    {
        FormatException error = Assert.Throws<FormatException>(() => TargetParser.Parse("Redump heading only"));

        Assert.Contains("No hash targets were found", error.Message, StringComparison.Ordinal);
    }
}
