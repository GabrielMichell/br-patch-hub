using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace BrPatchHub;

public static partial class SteamService
{
    public static bool IsRunning()
    {
        try
        {
            var processes = Process.GetProcessesByName("steam");
            var running = processes.Length > 0;
            foreach (var process in processes) process.Dispose();
            return running;
        }
        catch { return false; }
    }

    public static void Start() => Process.Start(new ProcessStartInfo("steam://open/main") { UseShellExecute = true });

    public static string? FindGame(Translation translation)
    {
        foreach (var library in GetLibraries())
        {
            if (!string.IsNullOrWhiteSpace(translation.SteamAppId))
            {
                var manifest = Path.Combine(library, "steamapps", $"appmanifest_{translation.SteamAppId}.acf");
                if (File.Exists(manifest))
                {
                    var match = InstallDirRegex().Match(File.ReadAllText(manifest));
                    if (match.Success)
                    {
                        var candidate = Path.Combine(library, "steamapps", "common", match.Groups[1].Value);
                        if (Directory.Exists(candidate)) return candidate;
                    }
                }
                continue;
            }
            foreach (var hint in translation.FolderHints)
            {
                var candidate = Path.Combine(library, "steamapps", "common", hint);
                if (Directory.Exists(candidate)) return candidate;
            }
        }
        return null;
    }

    public static void Validate(string appId)
    {
        if (!appId.All(char.IsDigit)) throw new InvalidDataException("O catálogo não possui um Steam App ID válido.");
        Process.Start(new ProcessStartInfo($"steam://validate/{appId}") { UseShellExecute = true });
    }

    private static IEnumerable<string> GetLibraries()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var keyPath in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam" })
        {
            var value = Registry.GetValue(keyPath, keyPath.Contains("CURRENT_USER") ? "SteamPath" : "InstallPath", null) as string;
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value)) roots.Add(Path.GetFullPath(value));
        }
        foreach (var root in roots.ToArray())
        {
            var vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdf)) continue;
            foreach (Match match in LibraryPathRegex().Matches(File.ReadAllText(vdf)))
            {
                var path = match.Groups[1].Value.Replace("\\\\", "\\");
                if (Directory.Exists(path)) roots.Add(Path.GetFullPath(path));
            }
        }
        return roots;
    }

    [GeneratedRegex("\\\"installdir\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase)] private static partial Regex InstallDirRegex();
    [GeneratedRegex("\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase)] private static partial Regex LibraryPathRegex();
}
