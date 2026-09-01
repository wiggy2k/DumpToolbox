namespace DumpToolbox.Core.Tests;

public sealed class EdgeRecoveryServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"DumpToolboxEdgeTests_{Guid.NewGuid():N}");

    public EdgeRecoveryServiceTests() => Directory.CreateDirectory(_root);

    [Fact]
    public async Task RepairAsync_SavesFirstPartialAndLeadingZeroTrimmedCopy()
    {
        string source = WriteSource(
            0, 0, 0, 0,
            0, 0, 0, 0,
            1, 2, 3, 4,
            5, 6, 7, 8,
            9, 9, 9, 9,
            9, 9, 9, 9);
        HashTarget first = Target(16, "first.bin");
        HashTarget second = Target(8, "second.bin");

        await new EdgeRecoveryService().RepairAsync(
            source,
            [first, second],
            [Missing(first), Matched(second, 16)],
            _root,
            attemptRepair: false,
            savePartialForInspection: true);

        Assert.Equal(16, new FileInfo(Path.Combine(_root, "first.partial")).Length);
        Assert.Equal(
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            await File.ReadAllBytesAsync(Path.Combine(_root, "first.leading-zero-trimmed.partial")));
    }

    [Fact]
    public async Task RepairAsync_SavesLastPartialAndTrailingZeroTrimmedCopy()
    {
        string source = WriteSource(
            9, 9, 9, 9,
            9, 9, 9, 9,
            1, 2, 3, 4,
            5, 6, 7, 8,
            0, 0, 0, 0,
            0, 0, 0, 0);
        HashTarget first = Target(8, "first.bin");
        HashTarget last = Target(16, "last.bin");

        await new EdgeRecoveryService().RepairAsync(
            source,
            [first, last],
            [Matched(first, 0), Missing(last)],
            _root,
            attemptRepair: false,
            savePartialForInspection: true);

        Assert.Equal(16, new FileInfo(Path.Combine(_root, "last.partial")).Length);
        Assert.Equal(
            new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 },
            await File.ReadAllBytesAsync(Path.Combine(_root, "last.trailing-zero-trimmed.partial")));
    }

    [Fact]
    public async Task RepairAudioEdgesAsync_UsesCueAudioIndicesForTrimmedEdgeCopies()
    {
        string source = WriteSource(
            0, 0, 0, 0,
            1, 2, 3, 4,
            9, 9, 9, 9,
            9, 9, 9, 9);
        HashTarget first = Target(8, "cue-first.bin");
        HashTarget second = Target(8, "cue-second.bin");

        await new EdgeRecoveryService().RepairAudioEdgesAsync(
            source,
            [first, second],
            [Missing(first), Matched(second, 8)],
            [0, 1],
            _root,
            attemptRepair: false,
            savePartialForInspection: true);

        Assert.True(File.Exists(Path.Combine(_root, "cue-first.partial")));
        Assert.Equal(
            new byte[] { 1, 2, 3, 4 },
            await File.ReadAllBytesAsync(Path.Combine(_root, "cue-first.leading-zero-trimmed.partial")));
    }

    [Fact]
    public async Task RepairAudioEdgesAsync_DoesNotSaveSingletonUsingDataAnchor()
    {
        string source = WriteSource(
            9, 9, 9, 9,
            1, 2, 3, 4,
            0, 0, 0, 0);
        HashTarget data = Target(4, "data.bin");
        HashTarget audio = Target(8, "audio.bin");

        EdgeRecoveryOutcome outcome = await new EdgeRecoveryService().RepairAudioEdgesAsync(
            source,
            [data, audio],
            [Matched(data, 0), Missing(audio)],
            [1],
            _root,
            attemptRepair: false,
            savePartialForInspection: true);

        Assert.False(File.Exists(Path.Combine(_root, "audio.partial")));
        Assert.Contains(outcome.Messages, message => message.Contains("adjacent matched AUDIO track", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RepairAudioEdgesAsync_SavesCueLastTrackAndTrailingZeroTrimmedCopy()
    {
        string source = WriteSource(
            9, 9, 9, 9,
            1, 2, 3, 4,
            0, 0, 0, 0);
        HashTarget first = Target(4, "cue-audio-1.bin");
        HashTarget last = Target(8, "cue-audio-2.bin");

        await new EdgeRecoveryService().RepairAudioEdgesAsync(
            source,
            [first, last],
            [Matched(first, 0), Missing(last)],
            [0, 1],
            _root,
            attemptRepair: false,
            savePartialForInspection: true);

        Assert.True(File.Exists(Path.Combine(_root, "cue-audio-2.partial")));
        Assert.Equal(
            new byte[] { 1, 2, 3, 4 },
            await File.ReadAllBytesAsync(Path.Combine(_root, "cue-audio-2.trailing-zero-trimmed.partial")));
    }

    [Fact]
    public async Task RepairAudioEdgesAsync_DoesNotUseFollowingDataTrackAsLastAudioBoundary()
    {
        string source = WriteSource(
            9, 9, 9, 9,
            1, 2, 3, 4,
            0, 0, 0, 0,
            7, 7, 7, 7);
        HashTarget firstAudio = Target(4, "audio-1.bin");
        HashTarget lastAudio = Target(8, "audio-2.bin");
        HashTarget data = Target(4, "data.bin");

        EdgeRecoveryOutcome outcome = await new EdgeRecoveryService().RepairAudioEdgesAsync(
            source,
            [firstAudio, lastAudio, data],
            [Matched(firstAudio, 0), Missing(lastAudio), Matched(data, 12)],
            [0, 1],
            _root,
            attemptRepair: false,
            savePartialForInspection: true);

        Assert.False(File.Exists(Path.Combine(_root, "audio-2.partial")));
        Assert.Contains(outcome.Messages, message => message.Contains("data track rather than disc EOF", StringComparison.Ordinal));
    }

    private string WriteSource(params byte[] bytes)
    {
        string path = Path.Combine(_root, $"source_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static HashTarget Target(long size, string outputFileName) =>
        new(size, 0, OutputFileName: outputFileName);

    private static SearchResult Missing(HashTarget target) =>
        new(target, null, false, "Not found");

    private static SearchResult Matched(HashTarget target, long offset) =>
        new(target, offset, true, "Matched");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
