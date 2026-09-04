using System.Buffers.Binary;
using System.Text;
using DumpToolbox.Core;

namespace DumpToolbox.Core.Tests;

public sealed class Nrg2BinServiceTests
{
    private const int CookedSectorSize = 2048;
    private const int RawSectorSize = 2352;

    [Fact]
    public async Task DaoMixedModeConversionRebuildsThreeSecondScrambledTrack2Pregap()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dumptoolbox-nrg-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string nrgPath = Path.Combine(directory, "mixed.nrg");
        string binPath = Path.Combine(directory, "mixed.bin");
        string cuePath = Path.Combine(directory, "mixed.cue");

        try
        {
            await File.WriteAllBytesAsync(nrgPath, BuildMixedModeNrg());
            var service = new Nrg2BinService();

            Nrg2BinInspection inspection = await service.AnalyzeAsync(nrgPath);

            Assert.Equal(228, inspection.OutputSectors);
            Assert.Equal(2, inspection.Tracks[0].SectorCount);
            Assert.Equal(0, inspection.Tracks[0].PregapSectors);
            Assert.Equal(150L * CookedSectorSize, inspection.Tracks[0].SourceOffset);
            Assert.Equal(75, inspection.Tracks[1].SyntheticScrambledPregapSectors);
            Assert.Equal(225, inspection.Tracks[1].PregapSectors);
            Assert.Equal(2, inspection.Tracks[1].OutputIndex00Sector);
            Assert.Equal(227, inspection.Tracks[1].OutputIndex01Sector);

            await service.ConvertAsync(nrgPath, binPath, cuePath);

            Assert.Equal(228L * RawSectorSize, new FileInfo(binPath).Length);
            string cue = await File.ReadAllTextAsync(cuePath);
            Assert.Contains("TRACK 01 MODE1/2352\n    INDEX 01 00:00:00", NormalizeNewlines(cue));
            Assert.Contains("TRACK 02 AUDIO\n    INDEX 00 00:00:02\n    INDEX 01 00:03:02", NormalizeNewlines(cue));

            byte[] output = await File.ReadAllBytesAsync(binPath);
            Assert.Equal(new byte[] { 0x00, 0x02, 0x00 }, output.AsSpan(12, 3).ToArray());
            Assert.All(output.AsSpan(16, CookedSectorSize).ToArray(), value => Assert.Equal(0x31, value));

            byte[] expectedScrambled = new byte[RawSectorSize];
            Iso2BinService.BuildRawSectorFromCooked(new byte[CookedSectorSize], expectedScrambled, 2, CdSectorMode.Mode1);
            CdPregapScrambleService.ScrambleSectorInPlace(expectedScrambled);
            Assert.Equal(expectedScrambled, output.AsSpan(2 * RawSectorSize, RawSectorSize).ToArray());

            Iso2BinService.BuildRawSectorFromCooked(new byte[CookedSectorSize], expectedScrambled, 76, CdSectorMode.Mode1);
            CdPregapScrambleService.ScrambleSectorInPlace(expectedScrambled);
            Assert.Equal(expectedScrambled, output.AsSpan(76 * RawSectorSize, RawSectorSize).ToArray());

            Assert.All(output.AsSpan(77 * RawSectorSize, 150 * RawSectorSize).ToArray(), value => Assert.Equal(0, value));
            Assert.All(output.AsSpan(227 * RawSectorSize, RawSectorSize).ToArray(), value => Assert.Equal(0xA5, value));
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private static byte[] BuildMixedModeNrg()
    {
        using var stream = new MemoryStream();

        WriteRepeated(stream, 150 * CookedSectorSize, 0x00);
        WriteRepeated(stream, CookedSectorSize, 0x31);
        WriteRepeated(stream, CookedSectorSize, 0x32);
        long track1End = stream.Position;

        WriteRepeated(stream, 150 * RawSectorSize, 0x00);
        long track2Index1 = stream.Position;
        WriteRepeated(stream, RawSectorSize, 0xA5);
        long track2End = stream.Position;

        long chunkChainOffset = stream.Position;
        WriteChunk(stream, "CUEX", BuildCuex(
            (0x41, 0x01, 0x00, -150),
            (0x41, 0x01, 0x01, 0),
            (0x01, 0x02, 0x00, 2),
            (0x01, 0x02, 0x01, 152),
            (0x01, 0xAA, 0x01, 153)));
        WriteChunk(stream, "DAOX", BuildDaox(track1End, track2Index1, track2End));
        WriteChunk(stream, "SINF", BigEndian32(2));
        WriteChunk(stream, "END!", Array.Empty<byte>());

        stream.Write(Encoding.ASCII.GetBytes("NER5"));
        Span<byte> offset = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(offset, checked((ulong)chunkChainOffset));
        stream.Write(offset);
        return stream.ToArray();
    }

    private static byte[] BuildCuex(params (byte Control, byte Track, byte Index, int Lba)[] entries)
    {
        byte[] payload = new byte[entries.Length * 8];
        for (int i = 0; i < entries.Length; i++)
        {
            int offset = i * 8;
            payload[offset] = entries[i].Control;
            payload[offset + 1] = entries[i].Track;
            payload[offset + 2] = entries[i].Index;
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(offset + 4, 4), entries[i].Lba);
        }
        return payload;
    }

    private static byte[] BuildDaox(long track1End, long track2Index1, long track2End)
    {
        byte[] payload = new byte[22 + 2 * 42];
        payload[20] = 1;
        payload[21] = 2;
        WriteDaoxTrack(payload.AsSpan(22, 42), CookedSectorSize, 0x0000, 0, 150L * CookedSectorSize, track1End);
        WriteDaoxTrack(payload.AsSpan(64, 42), RawSectorSize, 0x0700, track1End, track2Index1, track2End);
        return payload;
    }

    private static void WriteDaoxTrack(Span<byte> record, int sectorSize, ushort mode, long index0, long index1, long end)
    {
        BinaryPrimitives.WriteUInt16BigEndian(record.Slice(12, 2), checked((ushort)sectorSize));
        BinaryPrimitives.WriteUInt16BigEndian(record.Slice(14, 2), mode);
        BinaryPrimitives.WriteUInt64BigEndian(record.Slice(18, 8), checked((ulong)index0));
        BinaryPrimitives.WriteUInt64BigEndian(record.Slice(26, 8), checked((ulong)index1));
        BinaryPrimitives.WriteUInt64BigEndian(record.Slice(34, 8), checked((ulong)end));
    }

    private static void WriteChunk(Stream stream, string id, byte[] payload)
    {
        stream.Write(Encoding.ASCII.GetBytes(id));
        stream.Write(BigEndian32(payload.Length));
        stream.Write(payload);
    }

    private static byte[] BigEndian32(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static void WriteRepeated(Stream stream, int count, byte value)
    {
        byte[] bytes = Enumerable.Repeat(value, count).ToArray();
        stream.Write(bytes);
    }

    private static string NormalizeNewlines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);
}
