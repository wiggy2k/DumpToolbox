using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class DicDonorJolietProvenanceTests
{
    [Theory]
    [InlineData(0x00, 0x01)]
    [InlineData(0x01, 0x00)]
    [InlineData(0x05, 0x04)]
    public void PayloadFileFlagsMatch_IgnoresHiddenExistenceBit(byte targetFlags, byte donorFlags)
    {
        Assert.True(DicDonorImageService.PayloadFileFlagsMatch(targetFlags, donorFlags));
    }

    [Theory]
    [InlineData(0x00, 0x04)]
    [InlineData(0x00, 0x80)]
    [InlineData(0x01, 0x05)]
    public void PayloadFileFlagsMatch_KeepsStructuralFlagsStrict(byte targetFlags, byte donorFlags)
    {
        Assert.False(DicDonorImageService.PayloadFileFlagsMatch(targetFlags, donorFlags));
    }

    [Fact]
    public void ExactPrimaryDonorMatchRetainsMappedJolietAuthority()
    {
        string method = DicDonorImageService.AddDonorJolietProvenance(
            "Donor ISO9660 exact relative path+filename+size");

        Assert.Equal(
            "Donor ISO9660 exact relative path+filename+size + mapped Joliet pathname",
            method);
        Assert.True(DicLogImportService.MatchMethodTrustsRelativePath(method));
        Assert.True(DicLogImportService.MatchMethodProvesJolietIdentity(method));
    }

    [Fact]
    public void ExistingJolietMatchMethodIsNotRewritten()
    {
        const string method = "Donor Joliet pathname -> DIC primary ISO9660 projection + exact size";

        Assert.Equal(method, DicDonorImageService.AddDonorJolietProvenance(method));
        Assert.True(DicLogImportService.MatchMethodTrustsRelativePath(method));
    }
}
