using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DumpToolbox.Core;

public sealed partial class DicDonorImageService
{
    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private sealed record DonorFilesystem(
        byte[]? Pvd,
        string VolumeIdentifier,
        bool HasJoliet,
        IReadOnlyList<DicDonorFile> Files,
        IReadOnlyList<DicDonorFile> JolietFiles,
        HashSet<long> MetadataLbas);

    private sealed class DonorImageReader : IAsyncDisposable
    {
        private readonly FileStream _stream;
        public int SectorSize { get; }
        public long SectorCount => _stream.Length / SectorSize;

        private DonorImageReader(FileStream stream, int sectorSize)
        {
            _stream = stream;
            SectorSize = sectorSize;
        }

        public static async Task<DonorImageReader> OpenAsync(string path, CancellationToken cancellationToken)
        {
            var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                4 * 1024 * 1024,
                FileOptions.Asynchronous | FileOptions.RandomAccess);

            try
            {
                if (stream.Length % CookedSectorSize == 0 &&
                    await HasCd001AtAsync(stream, CookedSectorSize, cancellationToken).ConfigureAwait(false))
                    return new DonorImageReader(stream, CookedSectorSize);

                if (stream.Length % RawSectorSize == 0 &&
                    await HasCd001AtAsync(stream, RawSectorSize, cancellationToken).ConfigureAwait(false))
                    return new DonorImageReader(stream, RawSectorSize);

                throw new InvalidOperationException("Could not identify the donor as a 2048-byte ISO or 2352-byte raw BIN containing an ISO9660 PVD at LBA 16.");
            }
            catch
            {
                await stream.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        private static async Task<bool> HasCd001AtAsync(FileStream stream, int sectorSize, CancellationToken cancellationToken)
        {
            try
            {
                if (stream.Length < (SystemAreaSectors + 1L) * sectorSize)
                    return false;
                if (sectorSize == CookedSectorSize)
                {
                    byte[] sector = new byte[CookedSectorSize];
                    stream.Position = SystemAreaSectors * (long)sectorSize;
                    await ReadExactlyAsync(stream, sector, cancellationToken).ConfigureAwait(false);
                    return sector.AsSpan(1, 5).SequenceEqual(Cd001);
                }

                byte[] raw = new byte[RawSectorSize];
                stream.Position = SystemAreaSectors * (long)sectorSize;
                await ReadExactlyAsync(stream, raw, cancellationToken).ConfigureAwait(false);
                byte logicalMode = (byte)(raw[15] & 0x03);
                int offset = logicalMode == 1 ? 16 : logicalMode == 2 && (raw[18] & 0x20) == 0 ? 24 : -1;
                return offset >= 0 && raw.AsSpan(offset + 1, 5).SequenceEqual(Cd001);
            }
            catch
            {
                return false;
            }
        }

        public async Task<byte[]> ReadForm1SectorAsync(long lba, CancellationToken cancellationToken)
        {
            if (lba < 0 || lba >= SectorCount)
                throw new EndOfStreamException($"LBA {lba} is outside donor image.");

            if (SectorSize == CookedSectorSize)
            {
                byte[] cooked = new byte[CookedSectorSize];
                _stream.Position = lba * CookedSectorSize;
                await ReadExactlyAsync(_stream, cooked, cancellationToken).ConfigureAwait(false);
                return cooked;
            }

            byte[] raw = new byte[RawSectorSize];
            _stream.Position = lba * RawSectorSize;
            await ReadExactlyAsync(_stream, raw, cancellationToken).ConfigureAwait(false);
            byte logicalMode = (byte)(raw[15] & 0x03);
            if (logicalMode == 1)
                return raw.AsSpan(16, CookedSectorSize).ToArray();
            if (logicalMode == 2 && (raw[18] & 0x20) == 0)
                return raw.AsSpan(24, CookedSectorSize).ToArray();
            throw new InvalidOperationException($"Donor LBA {lba:N0} is Mode 2 Form 2 and cannot be represented as a 2048-byte ISO sector.");
        }

        public async Task<byte[]> ReadRawSectorAsync(long lba, CancellationToken cancellationToken)
        {
            if (SectorSize != RawSectorSize)
                throw new InvalidOperationException("Raw-sector reads require a 2352-byte BIN donor.");
            if (lba < 0 || lba >= SectorCount)
                throw new EndOfStreamException($"LBA {lba} is outside donor image.");

            byte[] raw = new byte[RawSectorSize];
            _stream.Position = lba * RawSectorSize;
            await ReadExactlyAsync(_stream, raw, cancellationToken).ConfigureAwait(false);
            return raw;
        }

        public async Task<byte[]> ReadPayloadSectorAsync(long lba, CancellationToken cancellationToken)
        {
            if (lba < 0 || lba >= SectorCount)
                throw new EndOfStreamException($"LBA {lba} is outside donor image.");

            if (SectorSize == CookedSectorSize)
                return await ReadForm1SectorAsync(lba, cancellationToken).ConfigureAwait(false);

            byte[] raw = new byte[RawSectorSize];
            _stream.Position = lba * RawSectorSize;
            await ReadExactlyAsync(_stream, raw, cancellationToken).ConfigureAwait(false);
            byte logicalMode = (byte)(raw[15] & 0x03);
            if (logicalMode == 1)
                return raw.AsSpan(16, CookedSectorSize).ToArray();
            if (logicalMode == 2)
                return (raw[18] & 0x20) != 0
                    ? raw.AsSpan(24, 2324).ToArray()
                    : raw.AsSpan(24, CookedSectorSize).ToArray();
            throw new InvalidOperationException($"Unsupported raw donor sector mode at LBA {lba:N0}.");
        }

        public ValueTask DisposeAsync() => _stream.DisposeAsync();
    }
}
