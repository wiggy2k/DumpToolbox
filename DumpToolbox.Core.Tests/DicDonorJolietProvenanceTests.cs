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

    [Fact]
    public void NumberedCollisionAliasFailsOrdinaryRevalidationUntilForced()
    {
        const string method = "Donor Joliet pathname -> DIC primary ISO9660 projection + exact size";
        const string jolietPath = "DirectX/Apr2005_d3dx9_25_x86.cab";
        const string isoPath = "DIRECTX/APR20052.CAB";

        Assert.False(DicLogImportService.MatchedJolietPathCanBeUsed(
            jolietPath,
            isoPath,
            method,
            forceMatchedJolietNames: false));
        Assert.True(DicLogImportService.MatchedJolietPathCanBeUsed(
            jolietPath,
            isoPath,
            method,
            forceMatchedJolietNames: true));
    }

    [Fact]
    public void OrdinaryProjectedJolietNameStillPassesRevalidation()
    {
        const string method = "Donor Joliet pathname -> DIC primary ISO9660 projection + exact size";

        Assert.True(DicLogImportService.MatchedJolietPathCanBeUsed(
            "DirectX/Apr2005_d3dx9_25_x64.cab",
            "DIRECTX/APR2005_.CAB",
            method,
            forceMatchedJolietNames: false));
    }

    [Fact]
    public void ForceStillRequiresASavedSourceRelativePath()
    {
        Assert.False(DicLogImportService.MatchedJolietPathCanBeUsed(
            string.Empty,
            "DIRECTX/APR20052.CAB",
            "Saved DIC match",
            forceMatchedJolietNames: true));
    }

    [Theory]
    [InlineData("DirectX/Apr2005_d3dx9_25_x64.cab", "DIRECTX/APR20052.CAB")]
    [InlineData("DirectX/APR2007_XACT_x86.cab", "DIRECTX/APR20075.CAB")]
    [InlineData("DirectX/Aug2009_D3DCompiler_42_x64.cab", "DIRECTX/AUG20010.CAB")]
    public void NeroNumberedCollisionAliasesAreRecognised(string jolietPath, string isoPath)
    {
        Assert.True(DicDonorImageService.PayloadFileFlagsMatch(0, 0));
        Assert.True(SkeletonResurrectionService.DonorJolietPathMatchesIsoCollisionAlias(jolietPath, isoPath));
    }

    [Theory]
    [InlineData("Elsewhere/Apr2005_d3dx9_25_x64.cab", "DIRECTX/APR20052.CAB")]
    [InlineData("DirectX/Apr2005_d3dx9_25_x64.cab", "DIRECTX/APR2005A.CAB")]
    [InlineData("DirectX/Apr2005_d3dx9_25_x64.dll", "DIRECTX/APR20052.CAB")]
    public void NeroCollisionAliasesRejectDifferentParentsOrShapes(string jolietPath, string isoPath)
    {
        Assert.False(SkeletonResurrectionService.DonorJolietPathMatchesIsoCollisionAlias(jolietPath, isoPath));
    }
}
