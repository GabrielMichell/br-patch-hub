using System.Diagnostics;

namespace BrPatchHub;

public static class UpdateInstaller
{
    public static void Start(string downloadedExecutable, string targetExecutable, string sha256)
    {
        var arguments = $"--apply-update {Quote(downloadedExecutable)} {Quote(targetExecutable)} {Environment.ProcessId} {sha256}";
        Process.Start(new ProcessStartInfo(downloadedExecutable, arguments) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(downloadedExecutable)! });
    }

    public static int ApplyAndRestart(string sourceExecutable, string targetExecutable, int processId, string sha256)
    {
        try
        {
            if (processId > 0)
            {
                try { Process.GetProcessById(processId).WaitForExit(30000); } catch (ArgumentException) { }
            }
            Apply(sourceExecutable, targetExecutable, sha256);
            Process.Start(new ProcessStartInfo(targetExecutable) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(targetExecutable)! });
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Não foi possível concluir a atualização.\r\n\r\n{ex.Message}\r\n\r\nA versão anterior foi preservada.", "BR Patch Hub — Falha na atualização", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    public static void Apply(string sourceExecutable, string targetExecutable, string sha256)
    {
        sourceExecutable = Path.GetFullPath(sourceExecutable);
        targetExecutable = Path.GetFullPath(targetExecutable);
        if (!File.Exists(sourceExecutable)) throw new FileNotFoundException("O executável da atualização não foi encontrado.");
        if (sha256.Length != 64 || !sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("SHA-256 da atualização inválido.");
        if (!FileTools.Sha256Async(sourceExecutable).GetAwaiter().GetResult().Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("O executável da atualização não corresponde ao SHA-256 publicado.");

        Directory.CreateDirectory(Path.GetDirectoryName(targetExecutable)!);
        var incoming = targetExecutable + ".new";
        var backup = targetExecutable + ".old";
        File.Copy(sourceExecutable, incoming, true);
        if (!FileTools.Sha256Async(incoming).GetAwaiter().GetResult().Equals(sha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(incoming); throw new InvalidDataException("A cópia da atualização foi corrompida."); }
        if (File.Exists(targetExecutable)) File.Copy(targetExecutable, backup, true);
        try
        {
            File.Move(incoming, targetExecutable, true);
            if (!FileTools.Sha256Async(targetExecutable).GetAwaiter().GetResult().Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("A validação final da atualização falhou.");
            if (File.Exists(backup)) File.Delete(backup);
        }
        catch
        {
            if (File.Exists(backup)) File.Copy(backup, targetExecutable, true);
            throw;
        }
    }

    private static string Quote(string value) => $"\"{value.Replace("\"", "\\\"")}\"";
}
