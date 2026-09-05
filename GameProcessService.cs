using System.Diagnostics;

namespace BrPatchHub;

public static class GameProcessService
{
    public static string? FindRunning(Translation translation, string? gameRoot)
    {
        var first = FindRunningOnce(translation, gameRoot);
        if (first is null) return null;
        Thread.Sleep(600);
        var confirmed = FindRunningOnce(translation, gameRoot);
        return confirmed is null ? null : $"{confirmed.Executable} (PID {confirmed.ProcessId})";
    }

    private static RunningProcess? FindRunningOnce(Translation translation, string? gameRoot)
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
                    var threadCount = 1;
                    var handleCount = 1;
                    try { threadCount = process.Threads.Count; handleCount = process.HandleCount; }
                    catch { /* Sem acesso aos detalhes: mantém a decisão conservadora pelo nome exato. */ }
                    if (!IsActiveSnapshot(process.HasExited, threadCount, handleCount)) continue;
                    if (!string.IsNullOrWhiteSpace(gameRoot))
                    {
                        try
                        {
                            var processPath = process.MainModule?.FileName;
                            if (!string.IsNullOrWhiteSpace(processPath) && !FileTools.IsInside(gameRoot, processPath)) continue;
                        }
                        catch { /* Nome exato é suficiente quando o Windows não permite ler o caminho. */ }
                    }
                    return new RunningProcess(executable, process.Id);
                }
                finally { process.Dispose(); }
            }
        }
        return null;
    }

    internal static bool IsActiveSnapshot(bool hasExited, int threadCount, int handleCount) => !hasExited && threadCount > 0 && handleCount > 0;

    private sealed record RunningProcess(string Executable, int ProcessId);
}
