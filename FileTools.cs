using System.Security.Cryptography;
using System.Text;

namespace CentralPtBr;

public static class FileTools
{
    public static bool IsInside(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveInside(string root, string relative)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(fullRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"O caminho '{relative}' sai do diretório permitido.");
        return full;
    }

    public static string RelativeTo(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    public static async Task<string> Sha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    public static async Task CopyAsync(string source, string target, IProgress<ProgressInfo>? progress, string message, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var total = new FileInfo(source).Length;
        long copied = 0;
        var buffer = new byte[1024 * 1024];
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length, true);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length, true);
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            copied += count;
            progress?.Report(new ProgressInfo(message, total == 0 ? 100 : (int)(copied * 100 / total)));
        }
    }

    public static bool RepairLengthPrefixedUtf8(string path, string from, string to)
    {
        if (!File.Exists(path)) return false;
        var bytes = File.ReadAllBytes(path);
        var oldValue = Encoding.UTF8.GetBytes(from);
        var newValue = Encoding.UTF8.GetBytes(to);
        if (oldValue.Length > 127 || newValue.Length > 127) throw new InvalidDataException("A preferência de idioma é longa demais.");
        var matches = new List<int>();
        for (var i = 1; i <= bytes.Length - oldValue.Length; i++)
        {
            if (bytes[i - 1] != oldValue.Length) continue;
            if (bytes.AsSpan(i, oldValue.Length).SequenceEqual(oldValue)) matches.Add(i);
        }
        if (matches.Count == 0) return false;
        if (matches.Count != 1) throw new InvalidDataException("A preferência de idioma apareceu mais de uma vez; a correção foi interrompida.");
        var index = matches[0];
        var result = new byte[bytes.Length - oldValue.Length + newValue.Length];
        Array.Copy(bytes, 0, result, 0, index - 1);
        result[index - 1] = (byte)newValue.Length;
        Array.Copy(newValue, 0, result, index, newValue.Length);
        Array.Copy(bytes, index + oldValue.Length, result, index + newValue.Length, bytes.Length - index - oldValue.Length);
        var backup = path + ".central-ptbr.bak";
        if (!File.Exists(backup)) File.Copy(path, backup);
        File.WriteAllBytes(path, result);
        return true;
    }
}
