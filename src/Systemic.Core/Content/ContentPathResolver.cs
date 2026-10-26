namespace Systemic.Content;

internal static class ContentPathResolver
{
    public static string ResolveWithinRoot(
        string rootDirectory,
        string relativePath,
        string pathDescription,
        string fileDescription)
    {
        var fullPath = Path.GetFullPath(Path.Combine(rootDirectory, relativePath));
        EnsureWithinRoot(rootDirectory, fullPath, relativePath, pathDescription);

        var resolvedPath = rootDirectory;
        foreach (var part in Path.GetRelativePath(rootDirectory, fullPath)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            resolvedPath = Path.Combine(resolvedPath, part);
            if (!Directory.Exists(resolvedPath) && !File.Exists(resolvedPath))
            {
                continue;
            }

            FileSystemInfo entry = Directory.Exists(resolvedPath)
                ? new DirectoryInfo(resolvedPath)
                : new FileInfo(resolvedPath);
            resolvedPath = Path.GetFullPath(entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? resolvedPath);
            EnsureWithinRoot(rootDirectory, resolvedPath, relativePath, pathDescription);
        }

        if (!File.Exists(resolvedPath))
        {
            throw new ContentPackException($"{fileDescription} '{relativePath}' does not exist.");
        }

        return resolvedPath;
    }

    private static void EnsureWithinRoot(
        string rootDirectory,
        string path,
        string relativePath,
        string pathDescription)
    {
        var rootPrefix = Path.EndsInDirectorySeparator(rootDirectory)
            ? rootDirectory
            : rootDirectory + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(rootPrefix, comparison))
        {
            throw new ContentPackException($"{pathDescription} '{relativePath}' escapes the pack root.");
        }
    }
}
