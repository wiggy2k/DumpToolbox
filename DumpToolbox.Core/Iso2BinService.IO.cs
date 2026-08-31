using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace DumpToolbox.Core;

public sealed partial class Iso2BinService
{
    private static FileStream OpenRead(string path, FileOptions options, int bufferSize) => new(
        path,
        FileMode.Open,
        FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete,
        bufferSize,
        options);

    private static FileStream OpenNewOutput(string path, int bufferSize) => new(
        path,
        FileMode.CreateNew,
        FileAccess.Write,
        FileShare.None,
        bufferSize,
        FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void ValidateDifferentPaths(string a, string b)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.GetFullPath(a).Equals(Path.GetFullPath(b), comparison))
            throw new InvalidOperationException("Input and output filenames must be different.");
    }

    private static void EnsureDestinationDirectory(string destination)
    {
        string? directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }

    private static void PreparePartial(string source, string partial)
    {
        ValidateDifferentPaths(source, partial);
        DeleteQuietly(partial);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Cleanup should not hide the original failure.
        }
    }

    private static async Task ReadExactlyAsync(FileStream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[total..], cancellationToken);
            if (read == 0)
                throw new EndOfStreamException("Unexpected end of input image.");
            total += read;
        }
    }
}
