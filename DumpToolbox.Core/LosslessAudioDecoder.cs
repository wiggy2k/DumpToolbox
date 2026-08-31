using System.Buffers;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace DumpToolbox.Core;

public sealed record LosslessAudioInfo(
    string FormatName,
    string CodecName,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    long TotalSamples,
    bool IsLossless,
    string DecoderName)
{
    public bool IsCddaCompatible =>
        IsLossless && SampleRate == 44100 && Channels == 2 && BitsPerSample == 16;

    public long DecodedCddaBytes => TotalSamples > 0 ? TotalSamples * 4L : 0;
    public double DurationSeconds => SampleRate > 0 && TotalSamples > 0 ? (double)TotalSamples / SampleRate : 0;
}

public sealed record LosslessAudioDecodeProgress(long SamplesDecoded, long TotalSamples, string Message)
{
    public double Fraction => TotalSamples <= 0 ? 0 : Math.Clamp((double)SamplesDecoded / TotalSamples, 0, 1);
}

/// <summary>
/// Front-end for exact lossless CD-DA source decoding.
/// Native FLAC and PCM WAV need no external tools. Other supported lossless
/// codecs are decoded through ffmpeg/ffprobe after the codec and PCM format
/// have been verified; DumpToolbox never asks ffmpeg to resample or remix.
/// </summary>
public sealed class LosslessAudioDecoder
{
    private const int CopyBufferSize = 1024 * 1024;
    private readonly FlacDecoder _flac = new();

    private static readonly HashSet<string> ExternalLosslessCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "flac", "ape", "tta", "alac", "tak", "pcm_s16le", "pcm_s16be"
    };

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".flac", ".wav", ".ape", ".tta", ".m4a", ".mp4", ".aif", ".aiff", ".oga", ".ogg", ".tak"
    };

    public static bool IsSupportedSourcePath(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path));

    public static string SupportedSourceDescription =>
        "FLAC, WAV PCM, APE, TTA, ALAC (M4A/MP4), AIFF PCM, Ogg-FLAC and TAK";

    public async Task<LosslessAudioInfo> InspectAsync(string path, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("An audio source file is required.", nameof(path));
        if (!File.Exists(path))
            throw new FileNotFoundException("Audio source file not found.", path);

        string extension = Path.GetExtension(path);
        if (extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            FlacStreamInfo info = await _flac.InspectAsync(path, cancellationToken).ConfigureAwait(false);
            return FromFlac(info);
        }

        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            WavePcmInfo wave = await Task.Run(() => InspectWave(path, cancellationToken), cancellationToken).ConfigureAwait(false);
            return new LosslessAudioInfo(
                "WAV",
                "pcm_s16le",
                wave.SampleRate,
                wave.Channels,
                wave.BitsPerSample,
                wave.TotalSamples,
                true,
                "Built-in PCM WAV");
        }

        if (!IsSupportedSourcePath(path))
            throw new NotSupportedException($"'{extension}' is not a supported Audio source format.");

        return await ProbeWithFfprobeAsync(path, cancellationToken).ConfigureAwait(false);
    }

    public async Task<LosslessAudioInfo> DecodeToCddaAsync(
        string inputPath,
        string outputPath,
        IProgress<LosslessAudioDecodeProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        LosslessAudioInfo info = await InspectAsync(inputPath, cancellationToken).ConfigureAwait(false);
        if (!info.IsLossless)
            throw new InvalidDataException($"{Path.GetFileName(inputPath)} is not a verified lossless source.");
        if (!info.IsCddaCompatible)
        {
            throw new InvalidDataException(
                $"{Path.GetFileName(inputPath)} is {info.SampleRate:N0} Hz / {info.BitsPerSample}-bit / {info.Channels} channel(s); " +
                "exact CDDA recovery requires lossless 44,100 Hz / 16-bit / stereo audio. Resampling or bit-depth conversion is intentionally not performed.");
        }

        string extension = Path.GetExtension(inputPath);
        if (extension.Equals(".flac", StringComparison.OrdinalIgnoreCase))
        {
            var translated = progress is null
                ? null
                : new Progress<FlacDecodeProgress>(p => progress.Report(
                    new LosslessAudioDecodeProgress(p.SamplesDecoded, p.TotalSamples, p.Message)));
            await _flac.DecodeToCddaAsync(inputPath, outputPath, translated, cancellationToken).ConfigureAwait(false);
            return info;
        }

        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            await DecodeWaveToCddaAsync(inputPath, outputPath, info, progress, cancellationToken).ConfigureAwait(false);
            return info;
        }

        await DecodeWithFfmpegAsync(inputPath, outputPath, info, progress, cancellationToken).ConfigureAwait(false);
        return info;
    }

    private static LosslessAudioInfo FromFlac(FlacStreamInfo info) => new(
        "FLAC",
        "flac",
        info.SampleRate,
        info.Channels,
        info.BitsPerSample,
        info.TotalSamples,
        true,
        "Built-in FLAC");

    private static async Task DecodeWaveToCddaAsync(
        string inputPath,
        string outputPath,
        LosslessAudioInfo info,
        IProgress<LosslessAudioDecodeProgress>? progress,
        CancellationToken cancellationToken)
    {
        WavePcmInfo wave = InspectWave(inputPath, cancellationToken);
        string fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput) ?? Directory.GetCurrentDirectory());
        string partial = fullOutput + ".partial";
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferSize);

        try
        {
            await using var input = new FileStream(
                inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var output = new FileStream(
                partial, FileMode.Create, FileAccess.Write, FileShare.None, CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                input.Position = wave.DataOffset;
                long remaining = wave.DataLength;
                long copied = 0;
                long nextProgress = 0;
                while (remaining > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int wanted = (int)Math.Min(buffer.Length, remaining);
                    int read = await input.ReadAsync(buffer.AsMemory(0, wanted), cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                        throw new EndOfStreamException("WAV data chunk ended unexpectedly.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    remaining -= read;
                    copied += read;
                    long samples = copied / 4;
                    if (copied >= nextProgress)
                    {
                        progress?.Report(new LosslessAudioDecodeProgress(samples, info.TotalSamples,
                            $"Copied {samples:N0} / {info.TotalSamples:N0} sample frames"));
                        nextProgress = copied + CopyBufferSize;
                    }
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }

            File.Move(partial, fullOutput, true);
            progress?.Report(new LosslessAudioDecodeProgress(info.TotalSamples, info.TotalSamples, "Decode complete"));
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static WavePcmInfo InspectWave(string path, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream);
        if (stream.Length < 12 || new string(reader.ReadChars(4)) != "RIFF")
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a RIFF/WAVE PCM file.");
        _ = reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
            throw new InvalidDataException($"'{Path.GetFileName(path)}' is not a RIFF/WAVE PCM file.");

        ushort formatTag = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort blockAlign = 0;
        ushort containerBits = 0;
        ushort validBits = 0;
        bool pcm = false;
        long dataOffset = -1;
        long dataLength = -1;

        while (stream.Position + 8 <= stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string id = new string(reader.ReadChars(4));
            uint chunkSize = reader.ReadUInt32();
            long chunkStart = stream.Position;
            long chunkEnd = checked(chunkStart + chunkSize);
            if (chunkEnd > stream.Length)
                throw new InvalidDataException($"WAV file '{Path.GetFileName(path)}' contains a truncated '{id}' chunk.");

            if (id == "fmt ")
            {
                if (chunkSize < 16)
                    throw new InvalidDataException("WAV fmt chunk is too short.");
                formatTag = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadUInt32();
                _ = reader.ReadUInt32(); // average bytes/sec
                blockAlign = reader.ReadUInt16();
                containerBits = reader.ReadUInt16();
                validBits = containerBits;
                pcm = formatTag == 1;

                if (formatTag == 0xFFFE && chunkSize >= 40)
                {
                    ushort cbSize = reader.ReadUInt16();
                    if (cbSize >= 22)
                    {
                        validBits = reader.ReadUInt16();
                        _ = reader.ReadUInt32(); // channel mask
                        byte[] subFormat = reader.ReadBytes(16);
                        byte[] pcmGuid =
                        {
                            0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x10, 0x00,
                            0x80, 0x00, 0x00, 0xAA, 0x00, 0x38, 0x9B, 0x71
                        };
                        pcm = subFormat.AsSpan().SequenceEqual(pcmGuid);
                    }
                }
            }
            else if (id == "data")
            {
                dataOffset = chunkStart;
                dataLength = chunkSize;
            }

            stream.Position = chunkEnd + (chunkSize & 1);
            if (stream.Position > stream.Length)
                break;
        }

        if (!pcm)
            throw new InvalidDataException("WAV source is not uncompressed integer PCM.");
        if (dataOffset < 0)
            throw new InvalidDataException("WAV source has no data chunk.");
        if (blockAlign == 0 || dataLength % blockAlign != 0)
            throw new InvalidDataException("WAV PCM data length is not aligned to complete sample frames.");

        int bits = validBits > 0 ? validBits : containerBits;
        return new WavePcmInfo(
            checked((int)sampleRate),
            channels,
            bits,
            dataOffset,
            dataLength,
            dataLength / blockAlign);
    }

    private static async Task<LosslessAudioInfo> ProbeWithFfprobeAsync(string path, CancellationToken cancellationToken)
    {
        using Process process = StartTool("ffprobe", psi =>
        {
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.ArgumentList.Add("-v");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-select_streams");
            psi.ArgumentList.Add("a:0");
            psi.ArgumentList.Add("-show_entries");
            psi.ArgumentList.Add("stream=codec_name,sample_rate,channels,bits_per_sample,bits_per_raw_sample,sample_fmt,duration,duration_ts,time_base");
            psi.ArgumentList.Add("-of");
            psi.ArgumentList.Add("json");
            psi.ArgumentList.Add(path);
        });

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
            throw new InvalidDataException($"ffprobe could not inspect '{Path.GetFileName(path)}': {CleanToolError(stderr)}");

        using JsonDocument document = JsonDocument.Parse(stdout);
        if (!document.RootElement.TryGetProperty("streams", out JsonElement streams) || streams.GetArrayLength() == 0)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' has no audio stream.");

        JsonElement stream = streams[0];
        string codec = GetString(stream, "codec_name");
        int sampleRate = GetInt(stream, "sample_rate");
        int channels = GetInt(stream, "channels");
        int bits = GetInt(stream, "bits_per_raw_sample");
        if (bits <= 0)
            bits = GetInt(stream, "bits_per_sample");
        if (bits <= 0)
            bits = BitsFromSampleFormat(GetString(stream, "sample_fmt"));

        bool lossless = ExternalLosslessCodecs.Contains(codec) || codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase);
        if (codec.Equals("wavpack", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                "WavPack can be lossless, hybrid or lossy and the current probe cannot prove which mode produced this .wv file. " +
                "It is intentionally rejected for checksum recovery rather than risk accepting a lossy source.");
        }
        if (!lossless)
            throw new InvalidDataException($"'{Path.GetFileName(path)}' uses codec '{codec}', which is not on DumpToolbox's verified-lossless list.");

        long totalSamples = CalculateTotalSamples(stream, sampleRate);
        string formatName = codec.ToLowerInvariant() switch
        {
            "ape" => "Monkey's Audio",
            "tta" => "True Audio",
            "alac" => "Apple Lossless",
            "tak" => "TAK",
            "flac" => "FLAC",
            "pcm_s16be" => "AIFF/PCM",
            "pcm_s16le" => "PCM",
            _ when codec.StartsWith("pcm_", StringComparison.OrdinalIgnoreCase) => "PCM",
            _ => codec.ToUpperInvariant()
        };

        return new LosslessAudioInfo(
            formatName,
            codec,
            sampleRate,
            channels,
            bits,
            totalSamples,
            true,
            "FFmpeg");
    }

    private static async Task DecodeWithFfmpegAsync(
        string inputPath,
        string outputPath,
        LosslessAudioInfo info,
        IProgress<LosslessAudioDecodeProgress>? progress,
        CancellationToken cancellationToken)
    {
        string fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput) ?? Directory.GetCurrentDirectory());
        string partial = fullOutput + ".partial";
        try
        {
            using Process process = StartTool("ffmpeg", psi =>
            {
                psi.RedirectStandardError = true;
                psi.ArgumentList.Add("-hide_banner");
                psi.ArgumentList.Add("-loglevel");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-nostdin");
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(inputPath);
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:a:0");
                psi.ArgumentList.Add("-vn");
                psi.ArgumentList.Add("-sn");
                psi.ArgumentList.Add("-dn");
                // Deliberately do not specify -ar or -ac: the inspected source
                // must already be CDDA-compatible, so resampling/remixing is forbidden.
                psi.ArgumentList.Add("-c:a");
                psi.ArgumentList.Add("pcm_s16le");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("s16le");
                psi.ArgumentList.Add(partial);
            });

            Task<string> stderrTask = process.StandardError.ReadToEndAsync();
            try
            {
                while (!process.HasExited)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(partial))
                    {
                        long samples = new FileInfo(partial).Length / 4;
                        progress?.Report(new LosslessAudioDecodeProgress(samples, info.TotalSamples,
                            info.TotalSamples > 0
                                ? $"Decoded {samples:N0} / {info.TotalSamples:N0} sample frames"
                                : $"Decoded {samples:N0} sample frames"));
                    }
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }

            string stderr = await stderrTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new InvalidDataException($"ffmpeg could not decode '{Path.GetFileName(inputPath)}': {CleanToolError(stderr)}");

            long bytes = new FileInfo(partial).Length;
            if (bytes % 4 != 0)
                throw new InvalidDataException("Decoded PCM length is not aligned to complete stereo sample frames.");
            long decodedSamples = bytes / 4;

            // Container/stream duration reported by ffprobe is useful for progress,
            // but is not always sample-exact for every container. The decoded PCM
            // itself is authoritative and the Redump CRC32/MD5 verification is the
            // final exactness check, so do not reject an otherwise valid decode
            // solely because rounded duration metadata differs by a few frames.
            File.Move(partial, fullOutput, true);
            progress?.Report(new LosslessAudioDecodeProgress(decodedSamples, info.TotalSamples > 0 ? info.TotalSamples : decodedSamples, "Decode complete"));
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
    }

    private static Process StartTool(string baseName, Action<ProcessStartInfo> configure)
    {
        Exception? last = null;
        foreach (string candidate in ToolCandidates(baseName))
        {
            var psi = new ProcessStartInfo
            {
                FileName = candidate,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            configure(psi);
            try
            {
                Process? process = Process.Start(psi);
                if (process is not null)
                    return process;
            }
            catch (Win32Exception ex)
            {
                last = ex;
            }
            catch (FileNotFoundException ex)
            {
                last = ex;
            }
        }

        throw new NotSupportedException(
            $"{baseName} is required for this lossless format. Put ffmpeg.exe and ffprobe.exe beside DumpToolbox.exe, " +
            "add them to PATH, or set DUMPTOOLBOX_FFMPEG_DIR to their folder.",
            last);
    }

    private static IEnumerable<string> ToolCandidates(string baseName)
    {
        string executable = OperatingSystem.IsWindows() ? baseName + ".exe" : baseName;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string? configured = Environment.GetEnvironmentVariable("DUMPTOOLBOX_FFMPEG_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string candidate = Path.Combine(configured.Trim().Trim('"'), executable);
            if (seen.Add(candidate) && File.Exists(candidate))
                yield return candidate;
        }

        string local = Path.Combine(AppContext.BaseDirectory, executable);
        if (seen.Add(local) && File.Exists(local))
            yield return local;

        if (seen.Add(executable))
            yield return executable;
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch { }
    }

    private static string CleanToolError(string error)
    {
        string clean = (error ?? string.Empty).Trim();
        if (clean.Length == 0)
            return "unknown decoder error";
        return clean.Length <= 1200 ? clean : clean[^1200..];
    }

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out JsonElement value))
            return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            _ => string.Empty
        };
    }

    private static int GetInt(JsonElement element, string name)
    {
        string text = GetString(element, name);
        return int.TryParse(text, out int value) ? value : 0;
    }

    private static int BitsFromSampleFormat(string sampleFormat)
    {
        if (sampleFormat.StartsWith("s16", StringComparison.OrdinalIgnoreCase) ||
            sampleFormat.StartsWith("u16", StringComparison.OrdinalIgnoreCase))
            return 16;
        if (sampleFormat.StartsWith("s24", StringComparison.OrdinalIgnoreCase) ||
            sampleFormat.StartsWith("u24", StringComparison.OrdinalIgnoreCase))
            return 24;
        if (sampleFormat.StartsWith("s32", StringComparison.OrdinalIgnoreCase) ||
            sampleFormat.StartsWith("u32", StringComparison.OrdinalIgnoreCase))
            return 32;
        if (sampleFormat.StartsWith("u8", StringComparison.OrdinalIgnoreCase))
            return 8;
        return 0;
    }

    private static long CalculateTotalSamples(JsonElement stream, int sampleRate)
    {
        if (sampleRate <= 0)
            return 0;

        string durationTsText = GetString(stream, "duration_ts");
        string timeBase = GetString(stream, "time_base");
        if (long.TryParse(durationTsText, out long durationTs) && durationTs > 0)
        {
            string[] parts = timeBase.Split('/');
            if (parts.Length == 2 && long.TryParse(parts[0], out long numerator) &&
                long.TryParse(parts[1], out long denominator) && denominator != 0)
            {
                double samples = durationTs * (double)numerator / denominator * sampleRate;
                if (samples > 0 && samples <= long.MaxValue)
                    return (long)Math.Round(samples);
            }
        }

        string durationText = GetString(stream, "duration");
        if (double.TryParse(durationText, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double duration) && duration > 0)
            return checked((long)Math.Round(duration * sampleRate));

        return 0;
    }

    private sealed record WavePcmInfo(
        int SampleRate,
        int Channels,
        int BitsPerSample,
        long DataOffset,
        long DataLength,
        long TotalSamples);
}
