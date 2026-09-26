namespace DumpToolbox.Core;

/// <summary>
/// Exposes one or more ISO9660 file extents as a single seekable logical stream
/// without materializing the complete file in memory.
/// </summary>
internal sealed class OpticalImageExtentStream : Stream
{
    private static readonly byte[] SyncPattern =
        [0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00];

    private readonly FileStream _source;
    private readonly ExtentMap[] _extents;
    private readonly int _sourceSectorSize;
    private readonly int _baseLba;
    private readonly byte[] _rawSector = new byte[SkeletonResurrectionService.RawSectorSize];
    private long _cachedRawSector = -1;
    private long _position;

    public OpticalImageExtentStream(
        string imagePath,
        IReadOnlyList<SkeletonSourceImageExtent> extents,
        long expectedLength)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("A source image path is required.", nameof(imagePath));
        if (extents.Count == 0)
            throw new ArgumentException("At least one source-image extent is required.", nameof(extents));
        if (expectedLength < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedLength));

        _source = new FileStream(
            imagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            1024 * 1024,
            FileOptions.RandomAccess);

        try
        {
            (_sourceSectorSize, _baseLba) = DetectImageGeometry(_source);
            _extents = new ExtentMap[extents.Count];
            long logicalStart = 0;
            for (int i = 0; i < extents.Count; i++)
            {
                SkeletonSourceImageExtent extent = extents[i];
                if (extent.Length < 0 || extent.Lba < _baseLba)
                    throw new InvalidDataException($"Invalid source-image extent at LBA {extent.Lba:N0}.");

                long physicalSector = extent.Lba - _baseLba;
                long sectors = DivideRoundUp(extent.Length, SkeletonResurrectionService.CookedSectorSize);
                long availableSectors = _source.Length / _sourceSectorSize;
                if (physicalSector > availableSectors || sectors > availableSectors - physicalSector)
                    throw new InvalidDataException($"Source-image extent at LBA {extent.Lba:N0} is outside the image.");

                _extents[i] = new ExtentMap(logicalStart, extent.Lba, extent.Length);
                logicalStart = checked(logicalStart + extent.Length);
            }

            if (logicalStart != expectedLength)
            {
                throw new InvalidDataException(
                    $"Source-image extents contain {logicalStart:N0} byte(s), expected {expectedLength:N0}.");
            }

            Length = logicalStart;
        }
        catch
        {
            _source.Dispose();
            throw;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length { get; }

    public override long Position
    {
        get => _position;
        set
        {
            if (value < 0)
                throw new ArgumentOutOfRangeException(nameof(value));
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (offset < 0 || count < 0 || offset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(offset < 0 ? nameof(offset) : nameof(count));
        return Read(buffer.AsSpan(offset, count));
    }

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0 || _position >= Length)
            return 0;

        int requested = checked((int)Math.Min((long)buffer.Length, Length - _position));
        int written = 0;
        while (written < requested)
        {
            int extentIndex = FindExtent(_position);
            if (extentIndex < 0)
                throw new EndOfStreamException("The logical source position is outside its image extents.");

            ExtentMap extent = _extents[extentIndex];
            long withinExtent = _position - extent.LogicalStart;
            int take = checked((int)Math.Min(
                (long)(requested - written),
                extent.Length - withinExtent));

            if (_sourceSectorSize == SkeletonResurrectionService.CookedSectorSize)
            {
                long physicalSector = extent.Lba - _baseLba;
                _source.Position = checked(
                    physicalSector * SkeletonResurrectionService.CookedSectorSize + withinExtent);
                ReadExactly(_source, buffer.Slice(written, take));
                written += take;
                _position += take;
                continue;
            }

            int remaining = take;
            while (remaining > 0)
            {
                long sectorWithinExtent = withinExtent / SkeletonResurrectionService.CookedSectorSize;
                int inSector = checked((int)(withinExtent % SkeletonResurrectionService.CookedSectorSize));
                long physicalSector = extent.Lba - _baseLba + sectorWithinExtent;
                LoadRawSector(physicalSector, extent.Lba + sectorWithinExtent);
                int userOffset = GetUserDataOffset(_rawSector, extent.Lba + sectorWithinExtent);
                int sectorTake = Math.Min(
                    SkeletonResurrectionService.CookedSectorSize - inSector,
                    remaining);
                _rawSector.AsSpan(userOffset + inSector, sectorTake)
                    .CopyTo(buffer.Slice(written, sectorTake));
                written += sectorTake;
                remaining -= sectorTake;
                withinExtent += sectorTake;
                _position += sectorTake;
            }
        }

        return written;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(Length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin))
        };
        if (target < 0)
            throw new IOException("Cannot seek before the beginning of the logical source stream.");
        _position = target;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _source.Dispose();
        base.Dispose(disposing);
    }

    private int FindExtent(long logicalPosition)
    {
        for (int i = 0; i < _extents.Length; i++)
        {
            ExtentMap extent = _extents[i];
            if (logicalPosition >= extent.LogicalStart &&
                logicalPosition < extent.LogicalStart + extent.Length)
            {
                return i;
            }
        }
        return -1;
    }

    private void LoadRawSector(long physicalSector, long lba)
    {
        if (_cachedRawSector == physicalSector)
            return;

        _source.Position = checked(physicalSector * SkeletonResurrectionService.RawSectorSize);
        ReadExactly(_source, _rawSector);
        if (!_rawSector.AsSpan(0, SyncPattern.Length).SequenceEqual(SyncPattern))
            throw new InvalidDataException($"Raw source-image sync is invalid at LBA {lba:N0}.");
        _cachedRawSector = physicalSector;
    }

    private static int GetUserDataOffset(byte[] raw, long lba)
    {
        if (raw[15] == 1)
            return 16;
        if (raw[15] == 2 && (raw[18] & 0x20) == 0)
            return 24;
        throw new InvalidDataException(
            $"Source-image file extent at LBA {lba:N0} is not Mode 1 / Mode 2 Form 1.");
    }

    private static (int SectorSize, int BaseLba) DetectImageGeometry(FileStream source)
    {
        if (source.Length > 0 && source.Length % SkeletonResurrectionService.RawSectorSize == 0)
        {
            Span<byte> header = stackalloc byte[16];
            source.Position = 0;
            if (source.Read(header) == header.Length &&
                header[..SyncPattern.Length].SequenceEqual(SyncPattern))
            {
                int minute = DecodeBcd(header[12]);
                int second = DecodeBcd(header[13]);
                int frame = DecodeBcd(header[14]);
                return (
                    SkeletonResurrectionService.RawSectorSize,
                    checked((minute * 60 + second) * 75 + frame - 150));
            }
        }

        if (source.Length > 0 && source.Length % SkeletonResurrectionService.CookedSectorSize == 0)
            return (SkeletonResurrectionService.CookedSectorSize, 0);

        throw new InvalidDataException(
            "Source image is neither a 2048-byte cooked image nor a sync-aligned 2352-byte raw CD track.");
    }

    private static int DecodeBcd(byte value)
    {
        int high = (value >> 4) & 0x0F;
        int low = value & 0x0F;
        if (high > 9 || low > 9)
            throw new InvalidDataException("Raw source image contains an invalid BCD sector address.");
        return high * 10 + low;
    }

    private static long DivideRoundUp(long value, long divisor) =>
        value == 0 ? 0 : checked((value + divisor - 1) / divisor);

    private static void ReadExactly(Stream source, Span<byte> destination)
    {
        int read = 0;
        while (read < destination.Length)
        {
            int count = source.Read(destination[read..]);
            if (count == 0)
                throw new EndOfStreamException("Unexpected end of source image.");
            read += count;
        }
    }

    private sealed record ExtentMap(long LogicalStart, long Lba, long Length);
}
