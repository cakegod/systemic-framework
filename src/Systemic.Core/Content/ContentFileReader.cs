using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Systemic.Content;

internal static class ContentFileReader
{
    public const long MaximumFileBytes = 64 * 1024 * 1024;

    public static byte[] ReadBytes(string path, string displayPath, ContentFingerprintBuilder? fingerprint = null)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length > MaximumFileBytes)
        {
            throw new ContentPackException($"Content file '{displayPath}' exceeds the 64 MiB limit.");
        }

        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
        {
            throw new ContentPackException($"Content file '{displayPath}' exceeds the 64 MiB limit.");
        }

        fingerprint?.Add(displayPath, bytes);
        return bytes;
    }

    public static byte[] ReadValidatedBytes(
        string path,
        string displayPath,
        string schemaKey,
        string schemaText,
        ContentFingerprintBuilder? fingerprint = null)
    {
        var bytes = ReadBytes(path, displayPath, fingerprint);
        var diagnostics = ContentSchemaValidator.Validate(schemaKey, schemaText, bytes, displayPath);
        if (!diagnostics.IsEmpty)
        {
            throw new ContentPackException(diagnostics);
        }

        return bytes;
    }
}

// Hash exactly the byte arrays consumed by compilation, not a later directory scan.
// Length framing and a domain prefix make paths and byte hashes unambiguous.
internal sealed class ContentFingerprintBuilder(string root)
{
    private readonly SortedDictionary<string, byte[]> _files = new(StringComparer.Ordinal);

    public void Add(string relativePath, byte[] bytes)
    {
        var path = Path.GetRelativePath(root, Path.GetFullPath(Path.Combine(root, relativePath)))
            .Replace(Path.DirectorySeparatorChar, '/');
        var digest = SHA256.HashData(bytes);
        if (_files.TryGetValue(path, out var previous))
        {
            if (!previous.AsSpan().SequenceEqual(digest))
            {
                throw new ContentPackException($"Content file '{relativePath}' changed while the pack was loading.");
            }
            return;
        }

        _files.Add(path, digest);
    }

    public string Finish()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("systemic-content-files-v1\0"u8);
        Span<byte> length = stackalloc byte[4];
        foreach (var (path, digest) in _files)
        {
            var pathBytes = Encoding.UTF8.GetBytes(path);
            BinaryPrimitives.WriteInt32BigEndian(length, pathBytes.Length);
            hash.AppendData(length);
            hash.AppendData(pathBytes);
            hash.AppendData(digest);
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
