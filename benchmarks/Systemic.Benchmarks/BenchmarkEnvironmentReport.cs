using System.Runtime;
using System.Runtime.InteropServices;

namespace Systemic.Benchmarks;

internal static class BenchmarkEnvironmentReport
{
    public static void Write()
    {
        var cpu = ReadProcValue("/proc/cpuinfo", "model name")
            ?? Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "unavailable";
        var allowed = ReadProcValue("/proc/self/status", "Cpus_allowed_list") ?? "unavailable";
        Console.WriteLine($"environment cpu=\"{cpu}\" os=\"{RuntimeInformation.OSDescription}\" "
            + $"runtime=\"{RuntimeInformation.FrameworkDescription}\" osArch={RuntimeInformation.OSArchitecture} "
            + $"processArch={RuntimeInformation.ProcessArchitecture} processorCount={Environment.ProcessorCount} "
            + $"allowedCpus=\"{allowed}\" serverGc={GCSettings.IsServerGC}");
        WriteCgroupV2();
    }

    private static void WriteCgroupV2()
    {
        if (!OperatingSystem.IsLinux())
        {
            Console.WriteLine("cgroup available=false reason=non-linux");
            return;
        }

        try
        {
            var membership = File.ReadLines("/proc/self/cgroup")
                .FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal))?[3..];
            var mount = File.ReadLines("/proc/self/mountinfo")
                .Select(line => line.Split(" - ", 2, StringSplitOptions.None))
                .FirstOrDefault(parts => parts.Length == 2 && parts[1].StartsWith("cgroup2 ", StringComparison.Ordinal));
            if (membership is null || mount is null)
            {
                Console.WriteLine("cgroup available=false reason=cgroup-v2-not-found");
                return;
            }

            var fields = mount[0].Split(' ');
            if (fields.Length < 5) throw new InvalidDataException("Malformed cgroup mount information.");
            var mountRoot = Unescape(fields[3]);
            var mountPath = Path.GetFullPath(Unescape(fields[4]));
            var current = ResolveV2Path(mountRoot, mountPath, membership);

            while (true)
            {
                Console.WriteLine($"cgroup path=\"{current}\" cpuMax=\"{ReadSmallFile(Path.Combine(current, "cpu.max"))}\" "
                    + $"cpusetEffective=\"{ReadSmallFile(Path.Combine(current, "cpuset.cpus.effective"))}\" "
                    + $"memoryMax=\"{ReadSmallFile(Path.Combine(current, "memory.max"))}\"");
                if (current == mountPath) break;
                current = Path.GetDirectoryName(current) ?? throw new InvalidDataException("Invalid cgroup path.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or InvalidDataException or ArgumentException or NotSupportedException)
        {
            Console.WriteLine($"cgroup available=false reason=\"{exception.GetType().Name}\"");
        }
    }

    // A cgroup namespace may expose membership '/' even when mountinfo names a
    // non-root host-side mount root. In that case the mount itself is the visible
    // cgroup root; do not interpret the host-side root as a local filesystem path.
    internal static string ResolveV2Path(string mountRoot, string mountPath, string membership)
    {
        mountPath = Path.GetFullPath(mountPath);
        if (membership == "/") return mountPath;
        if (mountRoot != "/" && membership != mountRoot
            && !membership.StartsWith(mountRoot + "/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("Cgroup membership is outside the visible cgroup mount.");
        }
        var relative = mountRoot == "/" ? membership.TrimStart('/') : membership[mountRoot.Length..].TrimStart('/');
        var current = Path.GetFullPath(Path.Combine(mountPath, relative));
        var within = Path.GetRelativePath(mountPath, current);
        if (within == ".." || within.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Cgroup path escapes its mount.");
        }
        return current;
    }

    private static string? ReadProcValue(string path, string key)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var separator = line.IndexOf(':');
                if (separator >= 0 && line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    return line[(separator + 1)..].Trim();
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return null;
    }

    private static string ReadSmallFile(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return "unavailable"; }
    }

    private static string Unescape(string value) => value.Replace("\\040", " ", StringComparison.Ordinal)
        .Replace("\\011", "\t", StringComparison.Ordinal).Replace("\\012", "\n", StringComparison.Ordinal)
        .Replace("\\134", "\\", StringComparison.Ordinal);
}
