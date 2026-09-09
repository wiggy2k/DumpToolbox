using DiscUtils.Udf;

namespace DumpToolbox.Core;

public sealed record UdfImageFile(string Path, long Length);

/// <summary>
/// Read-only access to a UDF filesystem stored in a cooked 2048-byte image or a
/// sync-aligned 2352-byte raw CD track.  DiscUtils parses UDF itself; the stream
/// adapter below exposes only each raw sector's 2048-byte Mode 1/Form 1 payload.
/// </summary>
public sealed class UdfImageReader : IDisposable
{
    private readonly OpticalPayloadStream _payload;
    private readonly UdfReader _udf;
    private bool _disposed;

    private UdfImageReader(string imagePath, OpticalPayloadStream payload, UdfReader udf)
    {
        ImagePath = imagePath;
        _payload = payload;
        _udf = udf;
        VolumeIdentifier = udf.VolumeLabel?.TrimEnd('\0', ' ') ?? string.Empty;

        string[] paths = udf.GetFiles(string.Empty, "*", SearchOption.AllDirectories).ToArray();
        Files = paths
            .Select(path => new UdfImageFile(
                NormalizePath(path),
                udf.GetFileInfo(path).Length))
            .OrderBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(file => file.Path, StringComparer.Ordinal)
            .ToArray();
    }

    public string ImagePath { get; }
    public int SourceSectorSize => _payload.SourceSectorSize;
    public string VolumeIdentifier { get; }
    public IReadOnlyList<UdfImageFile> Files { get; }

    public static UdfImageReader Open(string imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            throw new ArgumentException("Choose a 2048-byte ISO or 2352-byte BIN/IMG image.", nameof(imagePath));

        string fullPath = Path.GetFullPath(imagePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("Source image not found.", fullPath);

        OpticalPayloadStream? payload = null;
        UdfReader? udf = null;
        try
        {
            payload = OpticalPayloadStream.Open(fullPath);
            if (!UdfReader.Detect(payload))
                throw new InvalidDataException("The image does not contain a UDF Volume Recognition Sequence.");

            // Some recordable-media UDF images retain the medium's declared physical
            // partition capacity even though the captured track ends after the last
            // written block. DiscUtils validates that declaration before following the
            // virtual/metadata partition map, so expose the unwritten tail as sparse zero
            // blocks while continuing to reject reads of non-Form-1 sectors that do exist.
            payload.IncludeDeclaredUdfPartitionCapacity();
            payload.ConfigureVirtualPartition();
            payload.Position = 0;
            udf = new UdfReader(payload, SkeletonResurrectionService.CookedSectorSize);
            return new UdfImageReader(fullPath, payload, udf);
        }
        catch
        {
            udf?.Dispose();
            payload?.Dispose();
            throw;
        }
    }

    public Stream OpenFile(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string udfPath = path.Replace('/', '\\').TrimStart('\\');
        return _udf.OpenFile(udfPath, FileMode.Open, FileAccess.Read);
    }

    public static Stream OpenFile(string imagePath, string path)
    {
        UdfImageReader owner = Open(imagePath);
        try
        {
            return new OwnedUdfFileStream(owner.OpenFile(path), owner);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _udf.Dispose();
        _payload.Dispose();
    }

    internal static string NormalizePath(string path)
        => "/" + path.Replace('\\', '/').Trim('/');

    private sealed class OwnedUdfFileStream : Stream
    {
        private readonly Stream _file;
        private UdfImageReader? _owner;

        public OwnedUdfFileStream(Stream file, UdfImageReader owner)
        {
            _file = file;
            _owner = owner;
        }

        public override bool CanRead => _file.CanRead;
        public override bool CanSeek => _file.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _file.Length;
        public override long Position { get => _file.Position; set => _file.Position = value; }
        public override void Flush() => _file.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _file.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _file.Read(buffer);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _file.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => _file.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _file.Dispose();
                _owner?.Dispose();
                _owner = null;
            }
            base.Dispose(disposing);
        }
    }

    internal sealed class OpticalPayloadStream : Stream
    {
        private const int CookedSectorSize = SkeletonResurrectionService.CookedSectorSize;
        private const int RawSectorSize = SkeletonResurrectionService.RawSectorSize;
        private static readonly byte[] Sync = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

        private readonly FileStream _source;
        private readonly byte[] _rawSector = new byte[RawSectorSize];
        private long _cachedRawSector = -1;
        private byte[]? _patchedLogicalVolumeDescriptor;
        private long _patchedLogicalVolumeDescriptorLba = -1;
        private uint[]? _virtualAllocationTable;
        private uint _physicalPartitionStart;
        private long _length;
        private long _position;

        private OpticalPayloadStream(FileStream source, int sourceSectorSize)
        {
            _source = source;
            SourceSectorSize = sourceSectorSize;
            PhysicalLength = checked((source.Length / sourceSectorSize) * CookedSectorSize);
            _length = PhysicalLength;
        }

        public int SourceSectorSize { get; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        private long PhysicalLength { get; }
        public override long Length => _length;

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

        public static OpticalPayloadStream Open(string path)
        {
            var source = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                1024 * 1024,
                FileOptions.RandomAccess);
            try
            {
                if (source.Length > 0 && source.Length % RawSectorSize == 0)
                {
                    Span<byte> sync = stackalloc byte[Sync.Length];
                    source.Position = 0;
                    if (source.Read(sync) == sync.Length && sync.SequenceEqual(Sync))
                        return new OpticalPayloadStream(source, RawSectorSize);
                }

                if (source.Length > 0 && source.Length % CookedSectorSize == 0)
                    return new OpticalPayloadStream(source, CookedSectorSize);

                throw new InvalidDataException("Image is neither a 2048-byte cooked image nor a sync-aligned 2352-byte raw CD track.");
            }
            catch
            {
                source.Dispose();
                throw;
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

            int requested = checked((int)Math.Min(buffer.Length, Length - _position));
            if (SourceSectorSize == CookedSectorSize && _virtualAllocationTable is null)
            {
                int physical = (int)Math.Min(requested, Math.Max(0, PhysicalLength - _position));
                int read = 0;
                if (physical > 0)
                {
                    _source.Position = _position;
                    while (read < physical)
                    {
                        int got = _source.Read(buffer.Slice(read, physical - read));
                        if (got == 0)
                            throw new EndOfStreamException();
                        read += got;
                    }
                }
                if (read < requested)
                    buffer.Slice(read, requested - read).Clear();
                _position += requested;
                return requested;
            }

            int written = 0;
            while (written < requested)
            {
                long sectorIndex = _position / CookedSectorSize;
                int inSector = (int)(_position % CookedSectorSize);
                int take = Math.Min(CookedSectorSize - inSector, requested - written);
                if (sectorIndex == _patchedLogicalVolumeDescriptorLba && _patchedLogicalVolumeDescriptor is not null)
                {
                    _patchedLogicalVolumeDescriptor.AsSpan(inSector, take).CopyTo(buffer.Slice(written, take));
                }
                else
                {
                    long physicalSector = TranslateVirtualSector(sectorIndex);
                    if (SourceSectorSize == RawSectorSize)
                    {
                        EnsureRawSector(physicalSector);
                        int userOffset = GetUserDataOffset(_rawSector, physicalSector);
                        _rawSector.AsSpan(userOffset + inSector, take).CopyTo(buffer.Slice(written, take));
                    }
                    else
                    {
                        ReadCookedBytes(checked(physicalSector * CookedSectorSize + inSector), buffer.Slice(written, take));
                    }
                }
                written += take;
                _position += take;
            }
            return written;
        }

        private void EnsureRawSector(long sectorIndex)
        {
            if (_cachedRawSector == sectorIndex)
                return;

            if (sectorIndex >= PhysicalLength / CookedSectorSize)
            {
                _rawSector.AsSpan().Clear();
                _cachedRawSector = sectorIndex;
                return;
            }

            _source.Position = checked(sectorIndex * RawSectorSize);
            _source.ReadExactly(_rawSector);
            if (!_rawSector.AsSpan(0, Sync.Length).SequenceEqual(Sync))
                throw new InvalidDataException($"Raw CD sync is invalid at source sector {sectorIndex:N0}.");
            _cachedRawSector = sectorIndex;
        }

        private static int GetUserDataOffset(byte[] raw, long sectorIndex)
        {
            if (raw.AsSpan().IndexOfAnyExcept((byte)0) < 0)
                return 16;
            byte mode = (byte)(raw[15] & 0x03);
            if (mode == 1)
                return 16;
            if (mode == 2 && (raw[18] & 0x20) == 0)
                return 24;
            throw new InvalidDataException($"UDF sector {sectorIndex:N0} is not stored as Mode 1 or Mode 2 Form 1.");
        }

        private long TranslateVirtualSector(long sectorIndex)
        {
            if (_virtualAllocationTable is null || sectorIndex < _physicalPartitionStart)
                return sectorIndex;

            long virtualBlock = sectorIndex - _physicalPartitionStart;
            if (virtualBlock < 0 || virtualBlock >= _virtualAllocationTable.Length)
                return sectorIndex;

            uint mappedBlock = _virtualAllocationTable[virtualBlock];
            if (mappedBlock == uint.MaxValue)
                return PhysicalLength / CookedSectorSize; // sparse/unrecorded: expose a zero block
            return checked(_physicalPartitionStart + mappedBlock);
        }

        private void ReadCookedBytes(long physicalOffset, Span<byte> destination)
        {
            if (physicalOffset >= PhysicalLength)
            {
                destination.Clear();
                return;
            }

            int physical = (int)Math.Min(destination.Length, PhysicalLength - physicalOffset);
            _source.Position = physicalOffset;
            int read = 0;
            while (read < physical)
            {
                int got = _source.Read(destination.Slice(read, physical - read));
                if (got == 0)
                    throw new EndOfStreamException();
                read += got;
            }
            if (read < destination.Length)
                destination[read..].Clear();
        }

        private void ReadPhysicalPayloadSector(long physicalSector, Span<byte> destination)
        {
            if (destination.Length < CookedSectorSize)
                throw new ArgumentException("A complete 2048-byte sector buffer is required.", nameof(destination));

            if (physicalSector < 0 || physicalSector >= PhysicalLength / CookedSectorSize)
            {
                destination[..CookedSectorSize].Clear();
                return;
            }

            if (SourceSectorSize == CookedSectorSize)
            {
                ReadCookedBytes(physicalSector * CookedSectorSize, destination[..CookedSectorSize]);
                return;
            }

            EnsureRawSector(physicalSector);
            int userOffset = GetUserDataOffset(_rawSector, physicalSector);
            _rawSector.AsSpan(userOffset, CookedSectorSize).CopyTo(destination);
        }

        public void IncludeDeclaredUdfPartitionCapacity()
        {
            long savedPosition = _position;
            try
            {
                Span<byte> anchor = stackalloc byte[32];
                Position = 256L * CookedSectorSize;
                if (Read(anchor) != anchor.Length || BitConverter.ToUInt16(anchor) != 2)
                    return;

                uint sequenceLength = BitConverter.ToUInt32(anchor[16..20]);
                uint sequenceLba = BitConverter.ToUInt32(anchor[20..24]);
                long descriptors = Math.Min(256, (sequenceLength + CookedSectorSize - 1L) / CookedSectorSize);
                byte[] descriptor = new byte[CookedSectorSize];
                for (long i = 0; i < descriptors; i++)
                {
                    Position = checked((sequenceLba + i) * CookedSectorSize);
                    if (Read(descriptor) != descriptor.Length)
                        break;

                    ushort tag = BitConverter.ToUInt16(descriptor, 0);
                    if (tag == 8)
                        break;
                    if (tag != 5)
                        continue;

                    uint start = BitConverter.ToUInt32(descriptor, 188);
                    uint blocks = BitConverter.ToUInt32(descriptor, 192);
                    long declaredLength = checked(((long)start + blocks) * CookedSectorSize);
                    if (declaredLength > _length)
                        _length = declaredLength;
                }
            }
            finally
            {
                _position = savedPosition;
            }
        }

        public void ConfigureVirtualPartition()
        {
            byte[] anchor = new byte[CookedSectorSize];
            ReadPhysicalPayloadSector(256, anchor);
            if (BitConverter.ToUInt16(anchor, 0) != 2)
                return;

            uint sequenceLength = BitConverter.ToUInt32(anchor, 16);
            uint sequenceLba = BitConverter.ToUInt32(anchor, 20);
            int descriptors = checked((int)Math.Min(256, (sequenceLength + CookedSectorSize - 1L) / CookedSectorSize));
            byte[]? logicalVolumeDescriptor = null;
            long logicalVolumeDescriptorLba = -1;
            byte[] descriptor = new byte[CookedSectorSize];

            for (int i = 0; i < descriptors; i++)
            {
                long lba = sequenceLba + i;
                ReadPhysicalPayloadSector(lba, descriptor);
                ushort tag = BitConverter.ToUInt16(descriptor, 0);
                if (tag == 8)
                    break;
                if (tag == 5)
                    _physicalPartitionStart = BitConverter.ToUInt32(descriptor, 188);
                if (tag == 6)
                {
                    logicalVolumeDescriptor = descriptor.ToArray();
                    logicalVolumeDescriptorLba = lba;
                }
            }

            if (logicalVolumeDescriptor is null || !ContainsVirtualPartitionMap(logicalVolumeDescriptor))
                return;

            _virtualAllocationTable = ReadLatestVirtualAllocationTable();
            _patchedLogicalVolumeDescriptor = ConvertVirtualMapToType1(logicalVolumeDescriptor);
            _patchedLogicalVolumeDescriptorLba = logicalVolumeDescriptorLba;
        }

        private static bool ContainsVirtualPartitionMap(byte[] descriptor)
        {
            int mapLength = checked((int)BitConverter.ToUInt32(descriptor, 264));
            int mapCount = checked((int)BitConverter.ToUInt32(descriptor, 268));
            int position = 440;
            for (int i = 0; i < mapCount && position + 2 <= descriptor.Length && position < 440 + mapLength; i++)
            {
                int length = descriptor[position + 1];
                if (length < 2 || position + length > descriptor.Length)
                    break;
                if (descriptor[position] == 2 && length >= 64)
                {
                    string identifier = System.Text.Encoding.ASCII.GetString(descriptor, position + 5, 23).TrimEnd('\0', ' ');
                    if (identifier.Equals("*UDF Virtual Partition", StringComparison.Ordinal))
                        return true;
                }
                position += length;
            }
            return false;
        }

        private uint[] ReadLatestVirtualAllocationTable()
        {
            byte[] sector = new byte[CookedSectorSize];
            long physicalSectors = PhysicalLength / CookedSectorSize;
            long firstCandidate = Math.Max(_physicalPartitionStart, physicalSectors - 4096);
            for (long lba = physicalSectors - 1; lba >= firstCandidate; lba--)
            {
                ReadPhysicalPayloadSector(lba, sector);
                ushort tag = BitConverter.ToUInt16(sector, 0);
                if (tag is not (261 or 266) || sector[31] != 0)
                    continue;

                int allocationType = BitConverter.ToUInt16(sector, 34) & 0x0007;
                int eaOffset;
                int adOffset;
                int dataOffset;
                if (tag == 261)
                {
                    eaOffset = 168;
                    adOffset = 172;
                    dataOffset = 176;
                }
                else
                {
                    eaOffset = 208;
                    adOffset = 212;
                    dataOffset = 216;
                }

                int extendedAttributes = checked((int)BitConverter.ToUInt32(sector, eaOffset));
                int allocationDescriptors = checked((int)BitConverter.ToUInt32(sector, adOffset));
                if (allocationType != 3 || allocationDescriptors <= 0 ||
                    dataOffset + extendedAttributes + allocationDescriptors > sector.Length)
                    continue;

                ReadOnlySpan<byte> vat = sector.AsSpan(dataOffset + extendedAttributes, allocationDescriptors);
                if (!TryGetVatEntries(vat, out int entriesOffset, out int count) || count == 0)
                    continue;

                var table = new uint[count];
                for (int i = 0; i < count; i++)
                    table[i] = BitConverter.ToUInt32(vat.Slice(entriesOffset + i * 4, 4));
                return table;
            }

            throw new InvalidDataException("The UDF virtual partition map is present, but its latest Virtual Allocation Table could not be found near the end of the recorded image.");
        }

        private static bool TryGetVatEntries(ReadOnlySpan<byte> vat, out int offset, out int count)
        {
            if (vat.Length >= 152)
            {
                int headerLength = BitConverter.ToUInt16(vat[..2]);
                int implementationUseLength = BitConverter.ToUInt16(vat[2..4]);
                if (headerLength >= 152 + implementationUseLength && headerLength <= vat.Length &&
                    ((vat.Length - headerLength) & 3) == 0)
                {
                    offset = headerLength;
                    count = (vat.Length - headerLength) / 4;
                    return true;
                }
            }

            const int oldVatSuffixLength = 36;
            if (vat.Length >= oldVatSuffixLength && ((vat.Length - oldVatSuffixLength) & 3) == 0)
            {
                string identifier = System.Text.Encoding.ASCII
                    .GetString(vat.Slice(vat.Length - oldVatSuffixLength + 1, 23))
                    .TrimEnd('\0', ' ');
                if (identifier.Equals("*UDF Virtual Alloc Tbl", StringComparison.Ordinal))
                {
                    offset = 0;
                    count = (vat.Length - oldVatSuffixLength) / 4;
                    return true;
                }
            }
            offset = 0;
            count = 0;
            return false;
        }

        private static byte[] ConvertVirtualMapToType1(byte[] descriptor)
        {
            byte[] patched = descriptor.ToArray();
            int mapLength = checked((int)BitConverter.ToUInt32(patched, 264));
            int mapCount = checked((int)BitConverter.ToUInt32(patched, 268));
            int source = 440;
            int destination = 440;
            int writtenMaps = 0;

            for (int i = 0; i < mapCount && source + 2 <= patched.Length && source < 440 + mapLength; i++)
            {
                int length = patched[source + 1];
                if (length < 2 || source + length > patched.Length)
                    break;

                if (patched[source] == 2 && length >= 64)
                {
                    string identifier = System.Text.Encoding.ASCII.GetString(patched, source + 5, 23).TrimEnd('\0', ' ');
                    if (identifier.Equals("*UDF Virtual Partition", StringComparison.Ordinal))
                    {
                        patched[destination] = 1;
                        patched[destination + 1] = 6;
                        patched[destination + 2] = patched[source + 36];
                        patched[destination + 3] = patched[source + 37];
                        patched[destination + 4] = patched[source + 38];
                        patched[destination + 5] = patched[source + 39];
                        destination += 6;
                        writtenMaps++;
                        source += length;
                        continue;
                    }
                }

                Buffer.BlockCopy(patched, source, patched, destination, length);
                destination += length;
                writtenMaps++;
                source += length;
            }

            Array.Clear(patched, destination, Math.Max(0, 440 + mapLength - destination));
            BitConverter.GetBytes(destination - 440).CopyTo(patched, 264);
            BitConverter.GetBytes(writtenMaps).CopyTo(patched, 268);
            UpdateDescriptorTag(patched);
            return patched;
        }

        private static void UpdateDescriptorTag(byte[] descriptor)
        {
            int crcLength = BitConverter.ToUInt16(descriptor, 10);
            ushort crc = 0;
            for (int i = 16; i < 16 + crcLength && i < descriptor.Length; i++)
            {
                crc ^= (ushort)(descriptor[i] << 8);
                for (int bit = 0; bit < 8; bit++)
                    crc = (ushort)((crc & 0x8000) != 0 ? (crc << 1) ^ 0x1021 : crc << 1);
            }
            BitConverter.GetBytes(crc).CopyTo(descriptor, 8);

            descriptor[4] = 0;
            int checksum = 0;
            for (int i = 0; i < 16; i++)
                if (i != 4)
                    checksum += descriptor[i];
            descriptor[4] = (byte)checksum;
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
                throw new IOException("Cannot seek before the start of the image.");
            _position = target;
            return target;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
