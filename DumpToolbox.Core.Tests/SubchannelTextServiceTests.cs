namespace DumpToolbox.Core.Tests;

public sealed class SubchannelTextServiceTests
{
    [Fact]
    public async Task RenderAsync_ProducesDicCompatibleReadableLines()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sub2txt-{Guid.NewGuid():N}.sub");
        try
        {
            byte[] data = new byte[SubchannelTextService.BytesPerSector * 2];
            data.AsSpan(0, 12).Fill(0xff);
            byte[] firstQ = [0x41, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x28, 0x32];
            byte[] secondQ = [0x41, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x00, 0x02, 0x01, 0x92, 0x42];
            firstQ.CopyTo(data, 12);
            secondQ.CopyTo(data, SubchannelTextService.BytesPerSector + 12);
            await File.WriteAllBytesAsync(path, data);

            var service = new SubchannelTextService();
            SubchannelTextResult result = await service.RenderAsync(path);

            const string expected =
                "LBA[000000, 0000000]: P[ff], Q[410101000000000002002832]{ Data,      Copy NG,                  Track[01], Idx[01], RMSF[00:00:00], AMSF[00:02:00]}, RtoW[0, 0, 0, 0]\r\n" +
                "LBA[000001, 0x00001]: P[00], Q[410101000001000002019242]{ Data,      Copy NG,                  Track[01], Idx[01], RMSF[00:00:01], AMSF[00:02:01]}, RtoW[0, 0, 0, 0]\r\n";

            Assert.Equal(2, result.SectorCount);
            Assert.Equal(data.Length, result.InputLength);
            Assert.Equal(SubchannelFileFormat.DicDeinterleaved, result.Format);
            Assert.Equal(expected, result.Text);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".subcode")]
    [InlineData(".subchannel")]
    public async Task RenderAsync_DecodesRedumperMultiplexedSubcodeAndUsesPhysicalLba(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"sub2txt-{Guid.NewGuid():N}{extension}");
        try
        {
            byte[] row = new byte[SubchannelTextService.BytesPerSector];
            row.AsSpan(0, 12).Fill(0xff);
            byte[] q = [0x41, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x28, 0x32];
            q.CopyTo(row, 12);
            await File.WriteAllBytesAsync(path, Multiplex(row));

            var service = new SubchannelTextService();
            SubchannelTextResult result = await service.RenderAsync(path);

            const string expected =
                "LBA[-45150, 0xffff4fa2]: P[ff], Q[410101000000000002002832]{ Data,      Copy NG,                  Track[01], Idx[01], RMSF[00:00:00], AMSF[00:02:00]}, RtoW[0, 0, 0, 0]\r\n";

            Assert.Equal(SubchannelFileFormat.RedumperMultiplexed, result.Format);
            Assert.Equal(1, result.SectorCount);
            Assert.Equal(expected, result.Text);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task RenderAsync_DetectsMultiplexedDataWithoutKnownExtension()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sub2txt-{Guid.NewGuid():N}.bin");
        try
        {
            byte[] row = new byte[SubchannelTextService.BytesPerSector];
            row.AsSpan(0, 12).Fill(0xff);
            byte[] q = [0x41, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x28, 0x32];
            q.CopyTo(row, 12);
            await File.WriteAllBytesAsync(path, Multiplex(row));

            var service = new SubchannelTextService();
            SubchannelTextResult result = await service.RenderAsync(path);

            Assert.Equal(SubchannelFileFormat.RedumperMultiplexed, result.Format);
            Assert.Contains("Q[410101000000000002002832]", result.Text, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task RenderToFileAsync_StreamsTheCompleteReadableOutput()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), $"sub2txt-{Guid.NewGuid():N}.sub");
        string outputPath = Path.Combine(Path.GetTempPath(), $"sub2txt-{Guid.NewGuid():N}.txt");
        try
        {
            byte[] data = new byte[SubchannelTextService.BytesPerSector];
            data.AsSpan(0, 12).Fill(0xff);
            byte[] q = [0x41, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x28, 0x32];
            q.CopyTo(data, 12);
            await File.WriteAllBytesAsync(inputPath, data);

            var service = new SubchannelTextService();
            SubchannelTextResult inMemory = await service.RenderAsync(inputPath);
            SubchannelTextFileResult streamed = await service.RenderToFileAsync(inputPath, outputPath);

            Assert.Equal(inMemory.Text, await File.ReadAllTextAsync(outputPath));
            Assert.Equal(inMemory.SectorCount, streamed.SectorCount);
            Assert.Equal(inMemory.InputLength, streamed.InputLength);
            Assert.Equal(inMemory.Format, streamed.Format);
            Assert.Equal(new FileInfo(outputPath).Length, streamed.OutputLength);
        }
        finally
        {
            if (File.Exists(inputPath))
                File.Delete(inputPath);
            if (File.Exists(outputPath))
                File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task RenderAsync_RejectsIncompleteSubchannelSector()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sub2txt-{Guid.NewGuid():N}.sub");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[SubchannelTextService.BytesPerSector + 1]);
            var service = new SubchannelTextService();

            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => service.RenderAsync(path));

            Assert.Contains("96 bytes per sector", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task RenderAsync_RetainsFirstUsefulIsrcForLaterEmptyPackets()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sub2txt-{Guid.NewGuid():N}.sub");
        try
        {
            byte[] data = new byte[SubchannelTextService.BytesPerSector * 3];
            byte[] positionQ = [0x41, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x28, 0x32];
            byte[] usefulIsrcQ = [0x43, 0x65, 0x68, 0x19, 0x03, 0x00, 0x27, 0x18, 0x50, 0x55, 0x7a, 0xb8];
            byte[] emptyIsrcQ = [0x43, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x05, 0x6c, 0x8f];
            positionQ.CopyTo(data, 12);
            usefulIsrcQ.CopyTo(data, SubchannelTextService.BytesPerSector + 12);
            emptyIsrcQ.CopyTo(data, SubchannelTextService.BytesPerSector * 2 + 12);
            await File.WriteAllBytesAsync(path, data);

            var service = new SubchannelTextService();
            SubchannelTextResult result = await service.RenderAsync(path);
            string[] lines = result.Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

            Assert.Contains("ItnStdRecordingCode [IFPI00027185]", lines[1], StringComparison.Ordinal);
            Assert.Contains("ItnStdRecordingCode [IFPI00027185]", lines[2], StringComparison.Ordinal);
            Assert.Null(result.Mcn);
            Assert.Equal("IFPI00027185", result.IsrcByTrack[1]);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public async Task RenderAsync_ReportsValidNonZeroMcnAndIgnoresBlankPacket()
    {
        string path = Path.Combine(Path.GetTempPath(), $"sub2txt-{Guid.NewGuid():N}.sub");
        try
        {
            byte[] data = new byte[SubchannelTextService.BytesPerSector * 2];
            byte[] blankQ = new byte[12];
            blankQ[0] = 0x42;
            blankQ[9] = 0x05;
            ApplyQChannelCrc(blankQ);
            blankQ.CopyTo(data, 12);

            byte[] validQ = [0x42, 0x01, 0x23, 0x45, 0x67, 0x89, 0x01, 0x20, 0x00, 0x05, 0x00, 0x00];
            ApplyQChannelCrc(validQ);
            validQ.CopyTo(data, SubchannelTextService.BytesPerSector + 12);
            await File.WriteAllBytesAsync(path, data);

            var service = new SubchannelTextService();
            SubchannelTextResult result = await service.RenderAsync(path);

            Assert.Equal("0123456789012", result.Mcn);
            Assert.Empty(result.IsrcByTrack);
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    [Fact]
    public void SuggestOutputPath_UsesDicReadableFilename()
    {
        string input = Path.Combine(Path.GetTempPath(), "Example Disc.sub");

        string output = SubchannelTextService.SuggestOutputPath(input);

        Assert.Equal(Path.Combine(Path.GetTempPath(), "Example Disc_subReadable.txt"), output);
    }

    private static byte[] Multiplex(ReadOnlySpan<byte> row)
    {
        byte[] column = new byte[SubchannelTextService.BytesPerSector];
        for (int channel = 0; channel < 8; channel++)
        {
            int destinationMask = 0x80 >> channel;
            int rowOffset = channel * 12;
            for (int bit = 0; bit < SubchannelTextService.BytesPerSector; bit++)
            {
                if ((row[rowOffset + bit / 8] & (0x80 >> bit % 8)) != 0)
                    column[bit] |= (byte)destinationMask;
            }
        }
        return column;
    }

    private static void ApplyQChannelCrc(Span<byte> q)
    {
        ushort crc = 0;
        for (int i = 0; i < 10; i++)
        {
            crc ^= (ushort)(q[i] << 8);
            for (int bit = 0; bit < 8; bit++)
                crc = (ushort)((crc & 0x8000) != 0 ? crc << 1 ^ 0x1021 : crc << 1);
        }

        crc ^= 0xffff;
        q[10] = (byte)(crc >> 8);
        q[11] = (byte)crc;
    }
}
