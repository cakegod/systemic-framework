namespace Systemic.Content;

internal static class ContentFileReader
{
    public const long MaximumFileBytes = 64 * 1024 * 1024;

    public static byte[] ReadBytes(string path, string displayPath)
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

        return bytes;
    }

    public static byte[] ReadValidatedBytes(
        string path,
        string displayPath,
        string schemaKey,
        string schemaText)
    {
        var bytes = ReadBytes(path, displayPath);
        var diagnostics = ContentSchemaValidator.Validate(schemaKey, schemaText, bytes, displayPath);
        if (!diagnostics.IsEmpty)
        {
            throw new ContentPackException(diagnostics);
        }

        return bytes;
    }
}
