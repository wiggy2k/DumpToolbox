using System.Buffers;

namespace DumpToolbox.Core;

public sealed record FlacStreamInfo(
    int SampleRate,
    int Channels,
    int BitsPerSample,
    long TotalSamples,
    int MinBlockSize,
    int MaxBlockSize,
    string Md5)
{
    public bool IsCddaCompatible => SampleRate == 44100 && Channels == 2 && BitsPerSample == 16;
    public long DecodedCddaBytes => TotalSamples > 0 ? TotalSamples * 4L : 0;
    public double DurationSeconds => SampleRate > 0 && TotalSamples > 0 ? (double)TotalSamples / SampleRate : 0;
}

public sealed record FlacDecodeProgress(long SamplesDecoded, long TotalSamples, string Message)
{
    public double Fraction => TotalSamples <= 0 ? 0 : Math.Clamp((double)SamplesDecoded / TotalSamples, 0, 1);
}

/// <summary>
/// Small native-FLAC decoder for CDDA recovery. It intentionally supports the normal
/// FLAC stream format rather than invoking ffmpeg/flac externally, keeping published
/// DumpToolbox builds self-contained. Ogg-FLAC is not accepted.
/// </summary>
public sealed class FlacDecoder
{
    private const int CopyBufferSize = 256 * 1024;

    public Task<FlacStreamInfo> InspectAsync(string path, CancellationToken cancellationToken = default) =>
        Task.Run(() => Inspect(path, cancellationToken), cancellationToken);

    public FlacStreamInfo Inspect(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A FLAC file is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("FLAC file not found.", path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
        LocateNativeFlacMarker(stream, cancellationToken);
        return ReadMetadata(stream, cancellationToken).Info;
    }

    public Task<FlacStreamInfo> DecodeToCddaAsync(
        string inputPath,
        string outputPath,
        IProgress<FlacDecodeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => DecodeToCdda(inputPath, outputPath, progress, cancellationToken), cancellationToken);
    }

    private static FlacStreamInfo DecodeToCdda(
        string inputPath,
        string outputPath,
        IProgress<FlacDecodeProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("FLAC file not found.", inputPath);

        string fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput) ?? Directory.GetCurrentDirectory());
        string partial = fullOutput + ".partial";

        try
        {
            using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize, FileOptions.SequentialScan);
            LocateNativeFlacMarker(input, cancellationToken);
            FlacMetadata metadata = ReadMetadata(input, cancellationToken);
            FlacStreamInfo info = metadata.Info;

            if (!info.IsCddaCompatible)
            {
                throw new InvalidDataException(
                    $"{Path.GetFileName(inputPath)} is {info.SampleRate:N0} Hz, {info.BitsPerSample}-bit, {info.Channels} channel(s). " +
                    "CDDA recovery requires lossless 44,100 Hz, 16-bit, stereo FLAC without resampling.");
            }

            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize, FileOptions.SequentialScan))
            {
                var reader = new FlacBitReader(input);
                long decoded = 0;
                long nextProgress = 0;

                while (info.TotalSamples == 0 ? input.Position < input.Length : decoded < info.TotalSamples)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FlacFrameHeader header;
                    try
                    {
                        header = ReadFrameHeader(reader, info);
                    }
                    catch (EndOfStreamException) when (info.TotalSamples == 0)
                    {
                        break;
                    }

                    if (header.SampleRate != info.SampleRate)
                        throw new InvalidDataException($"FLAC frame sample rate changed to {header.SampleRate} Hz.");
                    if (header.Channels != info.Channels)
                        throw new InvalidDataException($"FLAC frame channel count changed to {header.Channels}.");
                    if (header.BitsPerSample != info.BitsPerSample)
                        throw new InvalidDataException($"FLAC frame bit depth changed to {header.BitsPerSample}-bit.");

                    long[][] channels = DecodeFrame(reader, header);
                    WriteCddaStereo(output, channels[0], channels[1], header.BlockSize, cancellationToken);
                    decoded += header.BlockSize;

                    if (info.TotalSamples > 0 && decoded > info.TotalSamples)
                        throw new InvalidDataException("FLAC decoded more samples than STREAMINFO declares.");

                    if (decoded >= nextProgress)
                    {
                        progress?.Report(new FlacDecodeProgress(decoded, info.TotalSamples,
                            $"Decoded {decoded:N0}" + (info.TotalSamples > 0 ? $" / {info.TotalSamples:N0} sample frames" : " sample frames")));
                        nextProgress = decoded + Math.Max(info.SampleRate, 1);
                    }
                }

                if (info.TotalSamples > 0 && decoded != info.TotalSamples)
                    throw new InvalidDataException($"FLAC ended after {decoded:N0} sample frames; STREAMINFO declares {info.TotalSamples:N0}.");

                output.Flush(true);
            }

            File.Move(partial, fullOutput, true);
            progress?.Report(new FlacDecodeProgress(info.TotalSamples, info.TotalSamples, "Decode complete"));
            return info;
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
    }

    private static void LocateNativeFlacMarker(Stream stream, CancellationToken cancellationToken)
    {
        // Native FLAC normally starts at byte zero. A few tagging tools prepend ID3v2;
        // tolerate that by scanning a small prefix for the fLaC marker.
        const int scanLimit = 1024 * 1024;
        byte[] window = new byte[4];
        int filled = 0;
        int scanned = 0;
        while (scanned < scanLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int value = stream.ReadByte();
            if (value < 0)
                break;
            scanned++;
            if (filled < 4)
                window[filled++] = (byte)value;
            else
            {
                window[0] = window[1];
                window[1] = window[2];
                window[2] = window[3];
                window[3] = (byte)value;
            }

            if (filled == 4 && window[0] == (byte)'f' && window[1] == (byte)'L' && window[2] == (byte)'a' && window[3] == (byte)'C')
                return;

            if (filled == 4 && scanned == 4 && window[0] == (byte)'O' && window[1] == (byte)'g' && window[2] == (byte)'g' && window[3] == (byte)'S')
                throw new NotSupportedException("Ogg-FLAC is not supported by the built-in decoder; use a native .flac stream.");
        }

        throw new InvalidDataException("No native FLAC marker (fLaC) was found in the first 1 MiB.");
    }

    private static FlacMetadata ReadMetadata(Stream stream, CancellationToken cancellationToken)
    {
        FlacStreamInfo? streamInfo = null;
        bool last = false;
        while (!last)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int first = stream.ReadByte();
            if (first < 0)
                throw new EndOfStreamException("FLAC ended while reading metadata.");
            last = (first & 0x80) != 0;
            int type = first & 0x7F;
            int length = ReadUInt24Be(stream);

            if (type == 0)
            {
                if (length != 34)
                    throw new InvalidDataException($"Invalid FLAC STREAMINFO length {length}; expected 34.");
                byte[] data = new byte[34];
                ReadExactly(stream, data);
                int minBlock = ReadUInt16Be(data.AsSpan(0, 2));
                int maxBlock = ReadUInt16Be(data.AsSpan(2, 2));
                ulong packed = ReadUInt64Be(data.AsSpan(10, 8));
                int sampleRate = (int)((packed >> 44) & 0xFFFFF);
                int channels = (int)((packed >> 41) & 0x7) + 1;
                int bits = (int)((packed >> 36) & 0x1F) + 1;
                long totalSamples = (long)(packed & 0xFFFFFFFFFUL);
                string md5 = Convert.ToHexString(data.AsSpan(18, 16)).ToLowerInvariant();
                streamInfo = new FlacStreamInfo(sampleRate, channels, bits, totalSamples, minBlock, maxBlock, md5);
            }
            else
            {
                SkipExactly(stream, length);
            }
        }

        if (streamInfo is null)
            throw new InvalidDataException("FLAC STREAMINFO block is missing.");

        return new FlacMetadata(streamInfo);
    }

    private static FlacFrameHeader ReadFrameHeader(FlacBitReader reader, FlacStreamInfo streamInfo)
    {
        reader.AlignToByte();
        uint sync = reader.ReadBits(14);
        if (sync != 0x3FFE)
            throw new InvalidDataException($"Invalid FLAC frame sync code 0x{sync:X}.");
        if (reader.ReadBit() != 0)
            throw new InvalidDataException("Reserved FLAC frame-header bit is set.");

        _ = reader.ReadBit(); // blocking strategy; frame/sample number is not needed for decode.
        int blockSizeCode = (int)reader.ReadBits(4);
        int sampleRateCode = (int)reader.ReadBits(4);
        int channelAssignment = (int)reader.ReadBits(4);
        int sampleSizeCode = (int)reader.ReadBits(3);
        if (reader.ReadBit() != 0)
            throw new InvalidDataException("Reserved FLAC frame-header bit is set.");

        ConsumeUtf8Number(reader);
        int blockSize = DecodeBlockSize(reader, blockSizeCode);
        int sampleRate = DecodeSampleRate(reader, sampleRateCode, streamInfo.SampleRate);
        int bitsPerSample = DecodeSampleSize(sampleSizeCode, streamInfo.BitsPerSample);
        int channels = channelAssignment <= 7 ? channelAssignment + 1 : 2;
        if (channelAssignment > 10)
            throw new InvalidDataException($"Reserved FLAC channel assignment {channelAssignment}.");

        _ = reader.ReadAlignedByte(); // CRC-8; structural decode is sufficient for recovery.

        if (blockSize <= 0)
            throw new InvalidDataException("FLAC frame declared an invalid block size.");
        return new FlacFrameHeader(blockSize, sampleRate, channels, bitsPerSample, channelAssignment);
    }

    private static long[][] DecodeFrame(FlacBitReader reader, FlacFrameHeader header)
    {
        var channels = new long[header.Channels][];
        for (int channel = 0; channel < header.Channels; channel++)
        {
            int channelBits = header.BitsPerSample;
            if (header.ChannelAssignment == 8 && channel == 1)
                channelBits++;
            else if (header.ChannelAssignment == 9 && channel == 0)
                channelBits++;
            else if (header.ChannelAssignment == 10 && channel == 1)
                channelBits++;

            channels[channel] = DecodeSubframe(reader, header.BlockSize, channelBits);
        }

        reader.AlignToByte();
        _ = reader.ReadBits(16); // frame CRC-16

        if (header.Channels == 2)
            RestoreStereoDecorrelaton(channels, header.ChannelAssignment, header.BlockSize);
        return channels;
    }

    private static long[] DecodeSubframe(FlacBitReader reader, int blockSize, int bitsPerSample)
    {
        if (reader.ReadBit() != 0)
            throw new InvalidDataException("Invalid FLAC subframe padding bit.");
        int type = (int)reader.ReadBits(6);
        bool hasWastedBits = reader.ReadBit() != 0;
        int wastedBits = hasWastedBits ? checked(reader.ReadUnary() + 1) : 0;
        int effectiveBits = bitsPerSample - wastedBits;
        if (effectiveBits <= 0)
            throw new InvalidDataException("FLAC wasted-bits flag consumed the entire sample width.");

        var samples = new long[blockSize];
        if (type == 0)
        {
            long value = reader.ReadSigned(effectiveBits);
            Array.Fill(samples, value);
        }
        else if (type == 1)
        {
            for (int i = 0; i < blockSize; i++)
                samples[i] = reader.ReadSigned(effectiveBits);
        }
        else if (type >= 8 && type <= 12)
        {
            int order = type - 8;
            for (int i = 0; i < order; i++)
                samples[i] = reader.ReadSigned(effectiveBits);
            DecodeResidual(reader, samples, blockSize, order);
            RestoreFixedPredictor(samples, blockSize, order);
        }
        else if (type >= 32)
        {
            int order = type - 31;
            for (int i = 0; i < order; i++)
                samples[i] = reader.ReadSigned(effectiveBits);

            int precisionRaw = (int)reader.ReadBits(4);
            if (precisionRaw == 15)
                throw new InvalidDataException("Reserved FLAC LPC coefficient precision.");
            int precision = precisionRaw + 1;
            int shift = (int)reader.ReadSigned(5);
            var coefficients = new long[order];
            for (int i = 0; i < order; i++)
                coefficients[i] = reader.ReadSigned(precision);

            DecodeResidual(reader, samples, blockSize, order);
            for (int i = order; i < blockSize; i++)
            {
                long sum = 0;
                for (int j = 0; j < order; j++)
                    sum += coefficients[j] * samples[i - j - 1];
                long predicted = shift >= 0 ? sum >> shift : sum << -shift;
                samples[i] += predicted;
            }
        }
        else
        {
            throw new InvalidDataException($"Reserved/unsupported FLAC subframe type {type}.");
        }

        if (wastedBits > 0)
        {
            for (int i = 0; i < blockSize; i++)
                samples[i] <<= wastedBits;
        }
        return samples;
    }

    private static void DecodeResidual(FlacBitReader reader, long[] samples, int blockSize, int predictorOrder)
    {
        int method = (int)reader.ReadBits(2);
        int parameterBits = method switch
        {
            0 => 4,
            1 => 5,
            _ => throw new InvalidDataException($"Reserved FLAC residual coding method {method}.")
        };
        int escape = (1 << parameterBits) - 1;
        int partitionOrder = (int)reader.ReadBits(4);
        int partitions = 1 << partitionOrder;
        if (blockSize % partitions != 0)
            throw new InvalidDataException("FLAC residual partition does not divide the frame block size.");

        int partitionSamples = blockSize / partitions;
        int destination = predictorOrder;
        for (int partition = 0; partition < partitions; partition++)
        {
            int count = partitionSamples - (partition == 0 ? predictorOrder : 0);
            if (count < 0)
                throw new InvalidDataException("Invalid FLAC residual partition/predictor combination.");
            int parameter = (int)reader.ReadBits(parameterBits);

            if (parameter == escape)
            {
                int rawBits = (int)reader.ReadBits(5);
                for (int i = 0; i < count; i++)
                    samples[destination++] = rawBits == 0 ? 0 : reader.ReadSigned(rawBits);
            }
            else
            {
                for (int i = 0; i < count; i++)
                {
                    int quotient = reader.ReadUnary();
                    ulong remainder = parameter == 0 ? 0 : reader.ReadBits(parameter);
                    ulong folded = ((ulong)quotient << parameter) | remainder;
                    long value = (long)(folded >> 1);
                    if ((folded & 1) != 0)
                        value = -value - 1;
                    samples[destination++] = value;
                }
            }
        }

        if (destination != blockSize)
            throw new InvalidDataException("FLAC residual sample count did not fill the frame.");
    }

    private static void RestoreFixedPredictor(long[] samples, int blockSize, int order)
    {
        for (int i = order; i < blockSize; i++)
        {
            long residual = samples[i];
            samples[i] = order switch
            {
                0 => residual,
                1 => residual + samples[i - 1],
                2 => residual + 2 * samples[i - 1] - samples[i - 2],
                3 => residual + 3 * samples[i - 1] - 3 * samples[i - 2] + samples[i - 3],
                4 => residual + 4 * samples[i - 1] - 6 * samples[i - 2] + 4 * samples[i - 3] - samples[i - 4],
                _ => throw new InvalidDataException($"Invalid fixed predictor order {order}.")
            };
        }
    }

    private static void RestoreStereoDecorrelaton(long[][] channels, int assignment, int blockSize)
    {
        if (assignment <= 7)
            return;

        long[] first = channels[0];
        long[] second = channels[1];
        for (int i = 0; i < blockSize; i++)
        {
            if (assignment == 8) // left + side
            {
                long left = first[i];
                long side = second[i];
                second[i] = left - side;
            }
            else if (assignment == 9) // side + right
            {
                long side = first[i];
                long right = second[i];
                first[i] = side + right;
            }
            else if (assignment == 10) // mid + side
            {
                long mid = first[i] << 1;
                long side = second[i];
                mid |= side & 1;
                first[i] = (mid + side) >> 1;
                second[i] = (mid - side) >> 1;
            }
        }
    }

    private static void WriteCddaStereo(Stream output, long[] left, long[] right, int count, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(count * 4, CopyBufferSize));
        try
        {
            int sample = 0;
            while (sample < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int frames = Math.Min((buffer.Length / 4), count - sample);
                int p = 0;
                for (int i = 0; i < frames; i++, sample++)
                {
                    long l = left[sample];
                    long r = right[sample];
                    if (l < short.MinValue || l > short.MaxValue || r < short.MinValue || r > short.MaxValue)
                        throw new InvalidDataException("Decoded FLAC sample exceeds signed 16-bit CDDA range.");
                    short ls = (short)l;
                    short rs = (short)r;
                    // Redump-style BIN/CUE audio is conventionally represented as
                    // little-endian signed 16-bit stereo PCM, matching WAVE sample byte order.
                    buffer[p++] = (byte)ls;
                    buffer[p++] = (byte)((ushort)ls >> 8);
                    buffer[p++] = (byte)rs;
                    buffer[p++] = (byte)((ushort)rs >> 8);
                }
                output.Write(buffer, 0, p);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ConsumeUtf8Number(FlacBitReader reader)
    {
        byte first = reader.ReadAlignedByte();
        int continuation;
        if ((first & 0x80) == 0)
            continuation = 0;
        else if ((first & 0xE0) == 0xC0)
            continuation = 1;
        else if ((first & 0xF0) == 0xE0)
            continuation = 2;
        else if ((first & 0xF8) == 0xF0)
            continuation = 3;
        else if ((first & 0xFC) == 0xF8)
            continuation = 4;
        else if ((first & 0xFE) == 0xFC)
            continuation = 5;
        else if (first == 0xFE)
            continuation = 6;
        else
            throw new InvalidDataException("Invalid UTF-8-coded FLAC frame/sample number.");

        for (int i = 0; i < continuation; i++)
        {
            byte next = reader.ReadAlignedByte();
            if ((next & 0xC0) != 0x80)
                throw new InvalidDataException("Invalid continuation byte in FLAC frame/sample number.");
        }
    }

    private static int DecodeBlockSize(FlacBitReader reader, int code) => code switch
    {
        0 => throw new InvalidDataException("Reserved FLAC block-size code 0."),
        1 => 192,
        >= 2 and <= 5 => 576 << (code - 2),
        6 => reader.ReadAlignedByte() + 1,
        7 => checked((int)reader.ReadBitsAligned(16) + 1),
        >= 8 and <= 15 => 256 << (code - 8),
        _ => throw new InvalidDataException("Invalid FLAC block-size code.")
    };

    private static int DecodeSampleRate(FlacBitReader reader, int code, int streamInfoRate) => code switch
    {
        0 => streamInfoRate,
        1 => 88200,
        2 => 176400,
        3 => 192000,
        4 => 8000,
        5 => 16000,
        6 => 22050,
        7 => 24000,
        8 => 32000,
        9 => 44100,
        10 => 48000,
        11 => 96000,
        12 => reader.ReadAlignedByte() * 1000,
        13 => (int)reader.ReadBitsAligned(16),
        14 => checked((int)reader.ReadBitsAligned(16) * 10),
        _ => throw new InvalidDataException("Reserved FLAC sample-rate code 15.")
    };

    private static int DecodeSampleSize(int code, int streamInfoBits) => code switch
    {
        0 => streamInfoBits,
        1 => 8,
        2 => 12,
        4 => 16,
        5 => 20,
        6 => 24,
        _ => throw new InvalidDataException($"Reserved FLAC sample-size code {code}.")
    };

    private static int ReadUInt24Be(Stream stream)
    {
        int a = stream.ReadByte();
        int b = stream.ReadByte();
        int c = stream.ReadByte();
        if ((a | b | c) < 0)
            throw new EndOfStreamException();
        return (a << 16) | (b << 8) | c;
    }

    private static int ReadUInt16Be(ReadOnlySpan<byte> data) => (data[0] << 8) | data[1];

    private static ulong ReadUInt64Be(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        for (int i = 0; i < 8; i++)
            value = (value << 8) | data[i];
        return value;
    }

    private static void ReadExactly(Stream stream, Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = stream.Read(destination[total..]);
            if (read == 0)
                throw new EndOfStreamException();
            total += read;
        }
    }

    private static void SkipExactly(Stream stream, int bytes)
    {
        if (bytes < 0)
            throw new InvalidDataException("Negative FLAC metadata length.");
        if (stream.CanSeek)
        {
            if (stream.Position + bytes > stream.Length)
                throw new EndOfStreamException();
            stream.Seek(bytes, SeekOrigin.Current);
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Min(bytes, 64 * 1024));
        try
        {
            int remaining = bytes;
            while (remaining > 0)
            {
                int read = stream.Read(buffer, 0, Math.Min(buffer.Length, remaining));
                if (read == 0)
                    throw new EndOfStreamException();
                remaining -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed record FlacMetadata(FlacStreamInfo Info);
    private sealed record FlacFrameHeader(int BlockSize, int SampleRate, int Channels, int BitsPerSample, int ChannelAssignment);

    private sealed class FlacBitReader
    {
        private readonly Stream _stream;
        private int _currentByte;
        private int _bitsRemaining;

        public FlacBitReader(Stream stream) => _stream = stream;

        public int ReadBit()
        {
            if (_bitsRemaining == 0)
            {
                _currentByte = _stream.ReadByte();
                if (_currentByte < 0)
                    throw new EndOfStreamException();
                _bitsRemaining = 8;
            }
            int bit = (_currentByte >> (_bitsRemaining - 1)) & 1;
            _bitsRemaining--;
            return bit;
        }

        public uint ReadBits(int count)
        {
            if (count < 0 || count > 32)
                throw new ArgumentOutOfRangeException(nameof(count));
            uint value = 0;
            for (int i = 0; i < count; i++)
                value = (value << 1) | (uint)ReadBit();
            return value;
        }

        public long ReadSigned(int count)
        {
            if (count == 0)
                return 0;
            if (count < 0 || count > 32)
                throw new ArgumentOutOfRangeException(nameof(count));
            uint raw = ReadBits(count);
            uint sign = 1u << (count - 1);
            if ((raw & sign) == 0)
                return raw;
            long full = 1L << count;
            return (long)raw - full;
        }

        public int ReadUnary()
        {
            int zeros = 0;
            while (ReadBit() == 0)
            {
                zeros++;
                if (zeros > 100_000_000)
                    throw new InvalidDataException("Unreasonable FLAC unary code length.");
            }
            return zeros;
        }

        public void AlignToByte() => _bitsRemaining = 0;

        public byte ReadAlignedByte()
        {
            if (_bitsRemaining != 0)
                throw new InvalidOperationException("FLAC parser expected a byte-aligned field.");
            int value = _stream.ReadByte();
            if (value < 0)
                throw new EndOfStreamException();
            return (byte)value;
        }

        public uint ReadBitsAligned(int count)
        {
            if (_bitsRemaining != 0 || count % 8 != 0)
                throw new InvalidOperationException("FLAC parser expected a byte-aligned integer.");
            return ReadBits(count);
        }
    }
}
