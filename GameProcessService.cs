using System.Diagnostics;

namespace CentralPtBr;

public static class GameProcessService
{
    public static string? FindRunning(Translation translation, string? gameRoot)
    {
        foreach (var hint in translation.ExecutableHints.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var executable = Path.GetFileName(hint);
            var processName = Path.GetFileNameWithoutExtension(executable);
            if (string.IsNullOrWhiteSpace(processName)) continue;
            foreach (var process in Process.GetProcessesByName(processName))
            {
                try
                {
                    if (process.HasExited) continue;
                    if (!string.IsNullOrWhiteSpace(gameRoot))
                    {
                        try
                        {
                            var processPath = process.MainModule?.FileName;
                            if (!string.IsNullOrWhiteSpace(processPath) && !FileTools.IsInside(gameRoot, processPath)) continue;
                        }
                        catch { /* Nome exato é suficiente quando o Windows não permite ler o caminho. */ }
                    }
                    return $"{executable} (PID {process.Id})";
                }
                finally { process.Dispose(); }
            }
        }
        return null;
    }
}
