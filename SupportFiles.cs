using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace WindowsDefenderPerformanceTool;

/// <summary>
/// Manages unpacking of DLLs embedded as resources in the EXE on first run,
/// then resolves assembly loads from the unpacked directory.
///
/// Modeled after PerfView's SupportFiles pattern:
///   - .csproj embeds all copy-local DLLs with LogicalName starting with ".\"
///   - On startup (before any external assembly loads), call UnpackResourcesIfNeeded()
///   - Resources are extracted to %APPDATA%\WindowsDefenderPerformanceTool\VER.x.x.x.x\
///   - AppDomain.AssemblyResolve loads managed DLLs from that directory
///   - PATH is extended so native DLLs in subdirectories are found
/// </summary>
internal static class SupportFiles
{
    private static string? _supportFileDir;

    public static string SupportFileDir
    {
        get
        {
            if (_supportFileDir == null)
            {
                var version = typeof(SupportFiles).Assembly.GetName().Version!;
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                _supportFileDir = Path.Combine(
                    appData, "WindowsDefenderPerformanceTool",
                    $"VER.{version.Major}.{version.Minor}.{version.Build}.{version.Revision}");
            }
            return _supportFileDir;
        }
    }

    /// <summary>
    /// Call at the very start of Main(), before any external assembly is referenced.
    /// Registers AssemblyResolve and unpacks embedded DLLs if not already done.
    /// </summary>
    public static void UnpackResourcesIfNeeded()
    {
        // Register FIRST — before any dependent type is loaded
        AppDomain.CurrentDomain.AssemblyResolve += ResolveAssembly;

        if (!Directory.Exists(SupportFileDir))
            UnpackResources();

        // Extend PATH so native DLLs in subdirectories are found via P/Invoke
        var paths = new List<string> { SupportFileDir };
        foreach (var subDir in Directory.GetDirectories(SupportFileDir))
            paths.Add(subDir);

        var currentPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var prefix = string.Join(";", paths);
        if (!currentPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", prefix + ";" + currentPath);
    }

    private static Assembly? ResolveAssembly(object sender, ResolveEventArgs args)
    {
        var name = args.Name;
        var comma = name.IndexOf(',');
        if (comma >= 0)
            name = name.Substring(0, comma);

        // Check root support directory
        var path = Path.Combine(SupportFileDir, name + ".dll");
        if (File.Exists(path))
            return Assembly.LoadFrom(path);

        // Check architecture-specific subdirectory
        var archSubDir = Environment.Is64BitProcess ? "amd64" : "x86";
        path = Path.Combine(SupportFileDir, archSubDir, name + ".dll");
        if (File.Exists(path))
            return Assembly.LoadFrom(path);

        return null;
    }

    private static void UnpackResources()
    {
        var prepDir = SupportFileDir + ".new";
        if (Directory.Exists(prepDir))
            Directory.Delete(prepDir, recursive: true);
        Directory.CreateDirectory(prepDir);

        var assembly = Assembly.GetExecutingAssembly();
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(@".\") && !resourceName.StartsWith(@"./"))
                continue;

            // Strip leading .\ or ./
            var relativePath = resourceName.Substring(2).Replace('/', Path.DirectorySeparatorChar);
            var targetPath = Path.Combine(prepDir, relativePath);

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            using var source = assembly.GetManifestResourceStream(resourceName)!;
            using var dest = File.Create(targetPath);
            source.CopyTo(dest);
        }

        // Atomic commit: rename .new → final
        Directory.Move(prepDir, SupportFileDir);
    }
}
