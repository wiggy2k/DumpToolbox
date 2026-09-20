using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using System.Text;

namespace DumpToolbox.Core;

public sealed partial class SkeletonResurrectionService
{
    private const int NeroSystemAreaBytes = SystemAreaSectors * CookedSectorSize;
    private const int NeroRecordOffset = (SystemAreaSectors - 1) * CookedSectorSize;
    private const int NeroPrivateValueOffset = NeroRecordOffset + 24;
    private const long NeroPrivateValueCount = 1L << 32;
    private const int NeroSearchRangeSize = 1 << 18;
    private const int NeroCancellationInterval = 1 << 12;

    private static readonly uint[] NeroSha1StateBeforeRecord = CreateNeroSha1StateBeforeRecord();
    private static readonly uint[] NeroSha1PaddingSchedule = CreateNeroSha1PaddingSchedule();

    public static bool CanRecoverNeroSystemArea(SkeletonInspectionResult inspection)
    {
        if (inspection.NeroSystemAreaRecovery is null)
            return false;

        return inspection.Entries.Any(entry =>
            entry.SpecialKind == SkeletonSpecialKind.SystemArea &&
            string.Equals(entry.Sha1, inspection.NeroSystemAreaRecovery.ExpectedSystemAreaSha1, StringComparison.OrdinalIgnoreCase));
    }

    public Task<SkeletonSourceMatch> RecoverNeroSystemAreaAsync(
        SkeletonInspectionResult inspection,
        IProgress<NeroSystemAreaRecoveryProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        NeroSystemAreaRecoveryInfo info = inspection.NeroSystemAreaRecovery
            ?? throw new InvalidOperationException("The skeleton does not contain a verified hidden Nero NRI project record.");
        SkeletonContentEntry entry = inspection.Entries.FirstOrDefault(candidate =>
            candidate.SpecialKind == SkeletonSpecialKind.SystemArea &&
            string.Equals(candidate.Sha1, info.ExpectedSystemAreaSha1, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The Nero recovery information does not correspond to a SYSTEM_AREA manifest entry.");

        return Task.Run(() =>
        {
            uint? privateValue = FindNeroPrivateValue(
                info,
                0,
                NeroPrivateValueCount,
                progress,
                cancellationToken);
            if (privateValue is null)
            {
                throw new InvalidOperationException(
                    $"The hidden Nero project '{info.ProjectFileName}' was found, but no four-byte Nero value produced the expected SYSTEM_AREA SHA-1. " +
                    "This image does not use the supported 32-byte Nero system-area layout.");
            }

            byte[] payload = BuildNeroSystemAreaForPrivateValue(info, privateValue.Value);
            string actualSha1 = Convert.ToHexString(SHA1.HashData(payload)).ToLowerInvariant();
            if (!actualSha1.Equals(info.ExpectedSystemAreaSha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The generated Nero SYSTEM_AREA failed its final SHA-1 verification.");

            return new SkeletonSourceMatch(
                entry,
                $"Generated from hidden Nero project {info.ProjectFileName}",
                actualSha1,
                IsXa: false,
                MatchMethod: "Nero hidden-NRI system-area reconstruction",
                SourceRelativePath: info.ProjectFileName,
                SourceLength: payload.LongLength,
                GeneratedPayload: payload);
        }, cancellationToken);
    }

    internal static uint? FindNeroPrivateValue(
        NeroSystemAreaRecoveryInfo info,
        long startInclusive,
        long endExclusive,
        IProgress<NeroSystemAreaRecoveryProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int? maxDegreeOfParallelism = null)
    {
        if (startInclusive < 0 || endExclusive > NeroPrivateValueCount || endExclusive <= startInclusive)
            throw new ArgumentOutOfRangeException(nameof(startInclusive));

        byte[] expectedDigest;
        try
        {
            expectedDigest = Convert.FromHexString(info.ExpectedSystemAreaSha1);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("The SYSTEM_AREA manifest SHA-1 is invalid.", ex);
        }
        if (expectedDigest.Length != 20)
            throw new InvalidOperationException("The SYSTEM_AREA manifest SHA-1 is invalid.");

        uint target0 = BinaryPrimitives.ReadUInt32BigEndian(expectedDigest.AsSpan(0, 4));
        uint target1 = BinaryPrimitives.ReadUInt32BigEndian(expectedDigest.AsSpan(4, 4));
        uint target2 = BinaryPrimitives.ReadUInt32BigEndian(expectedDigest.AsSpan(8, 4));
        uint target3 = BinaryPrimitives.ReadUInt32BigEndian(expectedDigest.AsSpan(12, 4));
        uint target4 = BinaryPrimitives.ReadUInt32BigEndian(expectedDigest.AsSpan(16, 4));
        uint[] templateWords = CreateNeroRecordBlockWords(info);

        long total = endExclusive - startInclusive;
        long tested = 0;
        long found = -1;
        long lastReportTimestamp = 0;
        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new NeroSystemAreaRecoveryProgress(0, total, TimeSpan.Zero));

        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = maxDegreeOfParallelism ?? Math.Max(1, Environment.ProcessorCount - 1)
        };

        Parallel.ForEach(
            Partitioner.Create(startInclusive, endExclusive, NeroSearchRangeSize),
            options,
            () => new NeroSearchWorker(),
            (range, loopState, worker) =>
            {
                long pending = 0;
                long value = range.Item1;
                if (Avx2.IsSupported)
                {
                    long vectorEnd = range.Item2 - 7;
                    for (; value < vectorEnd; value += 8)
                    {
                        if ((pending & (NeroCancellationInterval - 1)) == 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (Volatile.Read(ref found) >= 0)
                                break;
                        }

                        uint? vectorMatch = NeroCandidateBatchMatches(
                            unchecked((uint)value),
                            templateWords,
                            worker.VectorSchedule,
                            target0,
                            target1,
                            target2,
                            target3,
                            target4);
                        pending += 8;
                        if (vectorMatch is null)
                            continue;

                        Interlocked.CompareExchange(ref found, vectorMatch.Value, -1);
                        loopState.Stop();
                        break;
                    }
                }

                for (; value < range.Item2 && Volatile.Read(ref found) < 0; value++)
                {
                    if ((pending & (NeroCancellationInterval - 1)) == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (Volatile.Read(ref found) >= 0)
                            break;
                    }

                    uint candidate = unchecked((uint)value);
                    bool match = NeroCandidateMatches(
                        candidate,
                        templateWords,
                        worker.ScalarSchedule,
                        target0,
                        target1,
                        target2,
                        target3,
                        target4);
                    pending++;

                    if (!match)
                        continue;

                    Interlocked.CompareExchange(ref found, value, -1);
                    loopState.Stop();
                    break;
                }

                long completed = Interlocked.Add(ref tested, pending);
                ReportNeroProgress(progress, stopwatch, completed, total, ref lastReportTimestamp, force: false);
                return worker;
            },
            _ => { });

        long finalTested = Math.Min(Volatile.Read(ref tested), total);
        long finalValue = Volatile.Read(ref found);
        uint? result = finalValue < 0 ? null : unchecked((uint)finalValue);
        progress?.Report(new NeroSystemAreaRecoveryProgress(finalTested, total, stopwatch.Elapsed, result));
        return result;
    }

    private sealed class NeroSearchWorker
    {
        public uint[] ScalarSchedule { get; } = new uint[16];
        public Vector256<uint>[] VectorSchedule { get; } = new Vector256<uint>[16];
    }

    internal static byte[] BuildNeroSystemAreaForPrivateValue(
        NeroSystemAreaRecoveryInfo info,
        uint privateValue)
    {
        byte[] fileName = Encoding.ASCII.GetBytes(info.ProjectFileName);
        if (fileName.Length != 12 || !IsNeroProjectFileName(info.ProjectFileName))
            throw new InvalidOperationException($"Unsupported hidden Nero project filename '{info.ProjectFileName}'.");

        byte[] systemArea = new byte[NeroSystemAreaBytes];
        Span<byte> record = systemArea.AsSpan(NeroRecordOffset, 32);
        record[0] = checked((byte)fileName.Length);
        fileName.CopyTo(record[1..13]);
        BinaryPrimitives.WriteUInt32LittleEndian(record[13..17], info.ProjectExtentLba);
        BinaryPrimitives.WriteUInt32LittleEndian(record[17..21], info.ProjectDataLength);
        BinaryPrimitives.WriteUInt32BigEndian(systemArea.AsSpan(NeroPrivateValueOffset, 4), privateValue);
        record[31] = 0x20;
        return systemArea;
    }

    private static uint[] CreateNeroRecordBlockWords(NeroSystemAreaRecoveryInfo info)
    {
        byte[] systemArea = BuildNeroSystemAreaForPrivateValue(info, 0);
        ReadOnlySpan<byte> block = systemArea.AsSpan(NeroRecordOffset, 64);
        uint[] words = new uint[16];
        for (int i = 0; i < words.Length; i++)
            words[i] = BinaryPrimitives.ReadUInt32BigEndian(block.Slice(i * 4, 4));
        return words;
    }

    private static void ReportNeroProgress(
        IProgress<NeroSystemAreaRecoveryProgress>? progress,
        Stopwatch stopwatch,
        long tested,
        long total,
        ref long lastReportTimestamp,
        bool force)
    {
        if (progress is null)
            return;

        long now = Stopwatch.GetTimestamp();
        long interval = Math.Max(1, Stopwatch.Frequency / 10);
        long previous = Volatile.Read(ref lastReportTimestamp);
        if (!force && now - previous < interval)
            return;
        if (!force && Interlocked.CompareExchange(ref lastReportTimestamp, now, previous) != previous)
            return;

        progress.Report(new NeroSystemAreaRecoveryProgress(Math.Min(tested, total), total, stopwatch.Elapsed));
    }

    private static bool NeroCandidateMatches(
        uint candidate,
        uint[] templateWords,
        uint[] schedule,
        uint target0,
        uint target1,
        uint target2,
        uint target3,
        uint target4)
    {
        templateWords.CopyTo(schedule, 0);
        schedule[6] = candidate;

        uint h0 = NeroSha1StateBeforeRecord[0];
        uint h1 = NeroSha1StateBeforeRecord[1];
        uint h2 = NeroSha1StateBeforeRecord[2];
        uint h3 = NeroSha1StateBeforeRecord[3];
        uint h4 = NeroSha1StateBeforeRecord[4];
        CompressNeroDynamicBlock(ref h0, ref h1, ref h2, ref h3, ref h4, schedule);
        for (int i = 0; i < 31; i++)
            CompressNeroZeroBlock(ref h0, ref h1, ref h2, ref h3, ref h4);
        CompressNeroFixedSchedule(ref h0, ref h1, ref h2, ref h3, ref h4, NeroSha1PaddingSchedule);

        return h0 == target0 && h1 == target1 && h2 == target2 && h3 == target3 && h4 == target4;
    }

    private static uint? NeroCandidateBatchMatches(
        uint firstCandidate,
        uint[] templateWords,
        Vector256<uint>[] schedule,
        uint target0,
        uint target1,
        uint target2,
        uint target3,
        uint target4)
    {
        for (int i = 0; i < schedule.Length; i++)
            schedule[i] = Vector256.Create(templateWords[i]);
        schedule[6] = Vector256.Create(
            firstCandidate,
            firstCandidate + 1,
            firstCandidate + 2,
            firstCandidate + 3,
            firstCandidate + 4,
            firstCandidate + 5,
            firstCandidate + 6,
            firstCandidate + 7);

        Vector256<uint> h0 = Vector256.Create(NeroSha1StateBeforeRecord[0]);
        Vector256<uint> h1 = Vector256.Create(NeroSha1StateBeforeRecord[1]);
        Vector256<uint> h2 = Vector256.Create(NeroSha1StateBeforeRecord[2]);
        Vector256<uint> h3 = Vector256.Create(NeroSha1StateBeforeRecord[3]);
        Vector256<uint> h4 = Vector256.Create(NeroSha1StateBeforeRecord[4]);
        CompressNeroVectorDynamicBlock(ref h0, ref h1, ref h2, ref h3, ref h4, schedule);
        for (int i = 0; i < 31; i++)
            CompressNeroVectorZeroBlock(ref h0, ref h1, ref h2, ref h3, ref h4);
        CompressNeroVectorFixedSchedule(ref h0, ref h1, ref h2, ref h3, ref h4, NeroSha1PaddingSchedule);

        for (int lane = 0; lane < 8; lane++)
        {
            if (h0.GetElement(lane) == target0 &&
                h1.GetElement(lane) == target1 &&
                h2.GetElement(lane) == target2 &&
                h3.GetElement(lane) == target3 &&
                h4.GetElement(lane) == target4)
            {
                return firstCandidate + (uint)lane;
            }
        }

        return null;
    }

    private static void CompressNeroVectorDynamicBlock(
        ref Vector256<uint> h0,
        ref Vector256<uint> h1,
        ref Vector256<uint> h2,
        ref Vector256<uint> h3,
        ref Vector256<uint> h4,
        Vector256<uint>[] words)
    {
        Vector256<uint> a = h0;
        Vector256<uint> b = h1;
        Vector256<uint> c = h2;
        Vector256<uint> d = h3;
        Vector256<uint> e = h4;
        for (int round = 0; round < 80; round++)
        {
            if (round >= 16)
            {
                words[round & 15] = VectorRotateLeft1(
                    Avx2.Xor(
                        Avx2.Xor(words[(round - 3) & 15], words[(round - 8) & 15]),
                        Avx2.Xor(words[(round - 14) & 15], words[round & 15])));
            }

            Vector256<uint> function;
            uint constant;
            if (round < 20)
            {
                function = Avx2.Or(Avx2.And(b, c), Avx2.AndNot(b, d));
                constant = 0x5A827999u;
            }
            else if (round < 40)
            {
                function = Avx2.Xor(Avx2.Xor(b, c), d);
                constant = 0x6ED9EBA1u;
            }
            else if (round < 60)
            {
                function = Avx2.Or(Avx2.Or(Avx2.And(b, c), Avx2.And(b, d)), Avx2.And(c, d));
                constant = 0x8F1BBCDCu;
            }
            else
            {
                function = Avx2.Xor(Avx2.Xor(b, c), d);
                constant = 0xCA62C1D6u;
            }

            Vector256<uint> next = VectorAdd5(VectorRotateLeft5(a), function, e, Vector256.Create(constant), words[round & 15]);
            e = d; d = c; c = VectorRotateLeft30(b); b = a; a = next;
        }

        h0 = Avx2.Add(h0, a);
        h1 = Avx2.Add(h1, b);
        h2 = Avx2.Add(h2, c);
        h3 = Avx2.Add(h3, d);
        h4 = Avx2.Add(h4, e);
    }

    private static void CompressNeroVectorFixedSchedule(
        ref Vector256<uint> h0,
        ref Vector256<uint> h1,
        ref Vector256<uint> h2,
        ref Vector256<uint> h3,
        ref Vector256<uint> h4,
        uint[] schedule)
    {
        Vector256<uint> a = h0;
        Vector256<uint> b = h1;
        Vector256<uint> c = h2;
        Vector256<uint> d = h3;
        Vector256<uint> e = h4;
        Vector256<uint> next;
        for (int round = 0; round < 20; round++)
        {
            Vector256<uint> function = Avx2.Or(Avx2.And(b, c), Avx2.AndNot(b, d));
            next = VectorAdd5(VectorRotateLeft5(a), function, e, Vector256.Create(0x5A827999u), Vector256.Create(schedule[round]));
            e = d; d = c; c = VectorRotateLeft30(b); b = a; a = next;
        }
        for (int round = 20; round < 40; round++)
        {
            next = VectorAdd5(VectorRotateLeft5(a), Avx2.Xor(Avx2.Xor(b, c), d), e, Vector256.Create(0x6ED9EBA1u), Vector256.Create(schedule[round]));
            e = d; d = c; c = VectorRotateLeft30(b); b = a; a = next;
        }
        for (int round = 40; round < 60; round++)
        {
            Vector256<uint> function = Avx2.Or(Avx2.Or(Avx2.And(b, c), Avx2.And(b, d)), Avx2.And(c, d));
            next = VectorAdd5(VectorRotateLeft5(a), function, e, Vector256.Create(0x8F1BBCDCu), Vector256.Create(schedule[round]));
            e = d; d = c; c = VectorRotateLeft30(b); b = a; a = next;
        }
        for (int round = 60; round < 80; round++)
        {
            next = VectorAdd5(VectorRotateLeft5(a), Avx2.Xor(Avx2.Xor(b, c), d), e, Vector256.Create(0xCA62C1D6u), Vector256.Create(schedule[round]));
            e = d; d = c; c = VectorRotateLeft30(b); b = a; a = next;
        }

        h0 = Avx2.Add(h0, a);
        h1 = Avx2.Add(h1, b);
        h2 = Avx2.Add(h2, c);
        h3 = Avx2.Add(h3, d);
        h4 = Avx2.Add(h4, e);
    }

    private static void CompressNeroVectorZeroBlock(
        ref Vector256<uint> h0,
        ref Vector256<uint> h1,
        ref Vector256<uint> h2,
        ref Vector256<uint> h3,
        ref Vector256<uint> h4)
    {
        Vector256<uint> a = h0;
        Vector256<uint> b = h1;
        Vector256<uint> c = h2;
        Vector256<uint> d = h3;
        Vector256<uint> e = h4;
        Vector256<uint> next;
        Vector256<uint> k = Vector256.Create(0x5A827999u);
        for (int round = 0; round < 20; round++)
        {
            next = VectorAdd4(VectorRotateLeft5(a), Avx2.Or(Avx2.And(b, c), Avx2.AndNot(b, d)), e, k);
            e = d; d = c; c = VectorRotateLeft30(b); b = a; a = next;
        }
        k = Vector256.Create(0x6ED9EBA1u);
        for (int round = 20; round < 40; round++)
        {
            next = VectorAdd4(VectorRotateLeft5(a), Avx2.Xor(Avx2.Xor(b, c), d), e, k);
            e = d; d = c; c = VectorRotateLeft30(b); b = a; a = next;
        }
        k = Vector256.Create(0x8F1BBCDCu);
        for (int round = 40; round < 60; round++)
        {
            next = VectorAdd4(VectorRotateLeft5(a), Avx2.Or(Avx2.Or(Avx2.And(b, c), Avx2.And(b, d)), Avx2.And(c, d)), e, k);
            e = d; d = c; c = VectorRotateLeft30(b); b = a; a = next;
        }
        k = Vector256.Create(0xCA62C1D6u);
        for (int round = 60; round < 80; round++)
        {
            next = VectorAdd4(VectorRotateLeft5(a), Avx2.Xor(Avx2.Xor(b, c), d), e, k);
            e = d; d = c; c = VectorRotateLeft30(b); b = a; a = next;
        }

        h0 = Avx2.Add(h0, a);
        h1 = Avx2.Add(h1, b);
        h2 = Avx2.Add(h2, c);
        h3 = Avx2.Add(h3, d);
        h4 = Avx2.Add(h4, e);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> VectorRotateLeft1(Vector256<uint> value)
        => Avx2.Or(Avx2.ShiftLeftLogical(value, 1), Avx2.ShiftRightLogical(value, 31));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> VectorRotateLeft5(Vector256<uint> value)
        => Avx2.Or(Avx2.ShiftLeftLogical(value, 5), Avx2.ShiftRightLogical(value, 27));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> VectorRotateLeft30(Vector256<uint> value)
        => Avx2.Or(Avx2.ShiftLeftLogical(value, 30), Avx2.ShiftRightLogical(value, 2));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> VectorAdd4(
        Vector256<uint> a,
        Vector256<uint> b,
        Vector256<uint> c,
        Vector256<uint> d)
        => Avx2.Add(Avx2.Add(a, b), Avx2.Add(c, d));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<uint> VectorAdd5(
        Vector256<uint> a,
        Vector256<uint> b,
        Vector256<uint> c,
        Vector256<uint> d,
        Vector256<uint> e)
        => Avx2.Add(VectorAdd4(a, b, c, d), e);

    private static uint[] CreateNeroSha1StateBeforeRecord()
    {
        uint h0 = 0x67452301u;
        uint h1 = 0xEFCDAB89u;
        uint h2 = 0x98BADCFEu;
        uint h3 = 0x10325476u;
        uint h4 = 0xC3D2E1F0u;
        for (int i = 0; i < NeroRecordOffset / 64; i++)
            CompressNeroZeroBlock(ref h0, ref h1, ref h2, ref h3, ref h4);
        return [h0, h1, h2, h3, h4];
    }

    private static uint[] CreateNeroSha1PaddingSchedule()
    {
        uint[] schedule = new uint[80];
        schedule[0] = 0x80000000u;
        schedule[15] = NeroSystemAreaBytes * 8u;
        for (int i = 16; i < schedule.Length; i++)
            schedule[i] = RotateLeft(schedule[i - 3] ^ schedule[i - 8] ^ schedule[i - 14] ^ schedule[i - 16], 1);
        return schedule;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint RotateLeft(uint value, int count) => (value << count) | (value >> (32 - count));

    private static void CompressNeroDynamicBlock(
        ref uint h0,
        ref uint h1,
        ref uint h2,
        ref uint h3,
        ref uint h4,
        uint[] words)
    {
        unchecked
        {
            uint a = h0;
            uint b = h1;
            uint c = h2;
            uint d = h3;
            uint e = h4;
            for (int round = 0; round < 80; round++)
            {
                if (round >= 16)
                {
                    words[round & 15] = RotateLeft(
                        words[(round - 3) & 15] ^ words[(round - 8) & 15] ^
                        words[(round - 14) & 15] ^ words[round & 15],
                        1);
                }

                Sha1Round(round, words[round & 15], ref a, ref b, ref c, ref d, ref e);
            }

            h0 += a;
            h1 += b;
            h2 += c;
            h3 += d;
            h4 += e;
        }
    }

    private static void CompressNeroFixedSchedule(
        ref uint h0,
        ref uint h1,
        ref uint h2,
        ref uint h3,
        ref uint h4,
        uint[] schedule)
    {
        unchecked
        {
            uint a = h0;
            uint b = h1;
            uint c = h2;
            uint d = h3;
            uint e = h4;
            uint next;
            for (int round = 0; round < 20; round++)
            {
                next = RotateLeft(a, 5) + ((b & c) | (~b & d)) + e + 0x5A827999u + schedule[round];
                e = d; d = c; c = RotateLeft(b, 30); b = a; a = next;
            }
            for (int round = 20; round < 40; round++)
            {
                next = RotateLeft(a, 5) + (b ^ c ^ d) + e + 0x6ED9EBA1u + schedule[round];
                e = d; d = c; c = RotateLeft(b, 30); b = a; a = next;
            }
            for (int round = 40; round < 60; round++)
            {
                next = RotateLeft(a, 5) + ((b & c) | (b & d) | (c & d)) + e + 0x8F1BBCDCu + schedule[round];
                e = d; d = c; c = RotateLeft(b, 30); b = a; a = next;
            }
            for (int round = 60; round < 80; round++)
            {
                next = RotateLeft(a, 5) + (b ^ c ^ d) + e + 0xCA62C1D6u + schedule[round];
                e = d; d = c; c = RotateLeft(b, 30); b = a; a = next;
            }

            h0 += a;
            h1 += b;
            h2 += c;
            h3 += d;
            h4 += e;
        }
    }

    private static void CompressNeroZeroBlock(
        ref uint h0,
        ref uint h1,
        ref uint h2,
        ref uint h3,
        ref uint h4)
    {
        unchecked
        {
            uint a = h0;
            uint b = h1;
            uint c = h2;
            uint d = h3;
            uint e = h4;
            uint next;
            for (int round = 0; round < 20; round++)
            {
                next = RotateLeft(a, 5) + ((b & c) | (~b & d)) + e + 0x5A827999u;
                e = d; d = c; c = RotateLeft(b, 30); b = a; a = next;
            }
            for (int round = 20; round < 40; round++)
            {
                next = RotateLeft(a, 5) + (b ^ c ^ d) + e + 0x6ED9EBA1u;
                e = d; d = c; c = RotateLeft(b, 30); b = a; a = next;
            }
            for (int round = 40; round < 60; round++)
            {
                next = RotateLeft(a, 5) + ((b & c) | (b & d) | (c & d)) + e + 0x8F1BBCDCu;
                e = d; d = c; c = RotateLeft(b, 30); b = a; a = next;
            }
            for (int round = 60; round < 80; round++)
            {
                next = RotateLeft(a, 5) + (b ^ c ^ d) + e + 0xCA62C1D6u;
                e = d; d = c; c = RotateLeft(b, 30); b = a; a = next;
            }

            h0 += a;
            h1 += b;
            h2 += c;
            h3 += d;
            h4 += e;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Sha1Round(
        int round,
        uint word,
        ref uint a,
        ref uint b,
        ref uint c,
        ref uint d,
        ref uint e)
    {
        unchecked
        {
            uint function;
            uint constant;
            if (round < 20)
            {
                function = (b & c) | (~b & d);
                constant = 0x5A827999u;
            }
            else if (round < 40)
            {
                function = b ^ c ^ d;
                constant = 0x6ED9EBA1u;
            }
            else if (round < 60)
            {
                function = (b & c) | (b & d) | (c & d);
                constant = 0x8F1BBCDCu;
            }
            else
            {
                function = b ^ c ^ d;
                constant = 0xCA62C1D6u;
            }

            uint next = RotateLeft(a, 5) + function + e + constant + word;
            e = d;
            d = c;
            c = RotateLeft(b, 30);
            b = a;
            a = next;
        }
    }
}
