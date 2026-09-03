using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class DicDonorJolietProvenanceTests
{
    [Fact]
    public void ExactPrimaryDonorMatchRetainsMappedJolietAuthority()
    {
        string method = DicDonorImageService.AddDonorJolietProvenance(
            "Donor ISO9660 exact relative path+filename+size");

        Assert.Equal(
            "Donor ISO9660 exact relative path+filename+size + mapped Joliet pathname",
            method);
        Assert.True(DicLogImportService.MatchMethodTrustsRelativePath(method));
    }

    [Fact]
    public void ExistingJolietMatchMethodIsNotRewritten()
    {
        const string method = "Donor Joliet pathname -> DIC primary ISO9660 projection + exact size";

        Assert.Equal(method, DicDonorImageService.AddDonorJolietProvenance(method));
        Assert.True(DicLogImportService.MatchMethodTrustsRelativePath(method));
    }
}
