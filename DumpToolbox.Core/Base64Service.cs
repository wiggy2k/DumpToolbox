using System.Security.Cryptography;
using System.Text;

namespace DumpToolbox.Core;

public sealed class Base64Service
{
    private const int BufferSize = 1024 * 1024;

    public static string EncodeText(string text) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? string.Empty));

    public static string DecodeText(string base64)
    {
        byte[] bytes = Convert.FromBase64String(base64 ?? string.Empty);
        return Encoding.UTF8.GetString(bytes);
    }

    public Task EncodeFileAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => EncodeFileCore(inputPath, outputPath, cancellationToken), cancellationToken);

    public Task DecodeFileAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default) =>
        Task.Run(() => DecodeFileCore(inputPath, outputPath, cancellationToken), cancellationToken);

    private static void EncodeFileCore(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        ValidatePaths(inputPath, outputPath);
        string partial = outputPath + ".partial";
        try
        {
            using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            using (var transform = new ToBase64Transform())
            using (var crypto = new CryptoStream(output, transform, CryptoStreamMode.Write, leaveOpen: true))
            {
                byte[] buffer = new byte[BufferSize];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    crypto.Write(buffer, 0, read);
                }
                crypto.FlushFinalBlock();
                output.Flush(true);
            }
            File.Move(partial, outputPath, true);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
    }

    private static void DecodeFileCore(string inputPath, string outputPath, CancellationToken cancellationToken)
    {
        ValidatePaths(inputPath, outputPath);
        string partial = outputPath + ".partial";
        try
        {
            using var input = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
            using var transform = new FromBase64Transform(FromBase64TransformMode.IgnoreWhiteSpaces);
            using var crypto = new CryptoStream(input, transform, CryptoStreamMode.Read, leaveOpen: true);
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, FileOptions.SequentialScan))
            {
                byte[] buffer = new byte[BufferSize];
                int read;
                while ((read = crypto.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    output.Write(buffer, 0, read);
                }
                output.Flush(true);
            }
            File.Move(partial, outputPath, true);
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
            throw;
        }
    }

    private static void ValidatePaths(string inputPath, string outputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath) || !File.Exists(inputPath))
            throw new FileNotFoundException("Input file not found.", inputPath);
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("An output file is required.", nameof(outputPath));

        string inputFull = Path.GetFullPath(inputPath);
        string outputFull = Path.GetFullPath(outputPath);
        if (string.Equals(inputFull, outputFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Input and output files must be different.");

        string? directory = Path.GetDirectoryName(outputFull);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
    }
}
