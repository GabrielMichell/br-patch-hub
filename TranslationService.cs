using System.IO.Compression;
using System.Net.Http.Headers;

namespace CentralPtBr;

public sealed class TranslationService
{
    private readonly Storage _storage;
    private readonly HttpClient _http;
    private readonly Action<string> _log;

    public TranslationService(Storage storage, Action<string> log)
    {
        _storage = storage;
        _log = log;
        _http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Central-PT-BR/{AppConstants.AppVersion}");
    }

    public bool IsInstalled(string id) => _storage.Installed.ContainsKey(id);

    public Task<bool> IsHealthyAsync(string id)
    {
        if (!_storage.Installed.TryGetValue(id, out var record)) return Task.FromResult(false);
        foreach (var file in record.Files)
        {
            var target = FileTools.ResolveInside(record.GamePath, file.RelativePath);
            if (!File.Exists(target) || new FileInfo(target).Length != file.InstalledSize) return Task.FromResult(false);
        }
        return Task.FromResult(true);
    }

    public async Task InstallAsync(Translation translation, string gameRoot, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken)
    {
        ValidateTranslation(translation);
        gameRoot = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException("A pasta do jogo não existe.");

        if (_storage.Installed.ContainsKey(translation.Id))
        {
            var removal = await RemoveAsync(translation, progress, cancellationToken);
            if (removal.RequiresSteamRestore) throw new InvalidOperationException("A Steam precisa restaurar os arquivos originais antes de atualizar esta tradução.");
        }

        var packages = await DownloadPackagesAsync(translation, progress, cancellationToken);
        var staging = Path.Combine(_storage.TempRoot, $"install-{translation.Id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            for (var i = 0; i < packages.Count; i++) await ExtractZipAsync(packages[i], staging, progress, $"Extraindo pacote {i + 1} de {packages.Count}", cancellationToken);
            var plan = BuildPlan(translation, staging, gameRoot);
            EnsureNoConflicts(translation.Id, plan.Select(x => x.Target));
            var backupRoot = Path.Combine(_storage.BackupRoot, $"{translation.Id}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(backupRoot);
            var installedFiles = new List<InstalledFile>();

            for (var i = 0; i < plan.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = plan[i];
                progress?.Report(new ProgressInfo($"Aplicando arquivo {i + 1} de {plan.Count}: {entry.RelativePath}", (int)(i * 100L / plan.Count)));
                Directory.CreateDirectory(Path.GetDirectoryName(entry.Target)!);
                string? backupPath = null;
                var restoreMethod = "delete";
                if (File.Exists(entry.Target))
                {
                    if (entry.Type == "copy" && new FileInfo(entry.Target).Length == new FileInfo(entry.Source).Length &&
                        await FileTools.Sha256Async(entry.Target, cancellationToken) == await FileTools.Sha256Async(entry.Source, cancellationToken))
                    {
                        restoreMethod = "steam";
                    }
                    else
                    {
                        backupPath = entry.RelativePath;
                        await FileTools.CopyAsync(entry.Target, FileTools.ResolveInside(backupRoot, backupPath), progress, $"Criando backup: {entry.RelativePath}", cancellationToken);
                        restoreMethod = "backup";
                    }
                }

                if (entry.Type == "append")
                {
                    if (!File.Exists(entry.Target)) throw new FileNotFoundException($"O arquivo original '{entry.RelativePath}' não existe.");
                    if (entry.ExpectedSize > 0 && new FileInfo(entry.Target).Length != entry.ExpectedSize) throw new InvalidDataException($"O arquivo '{entry.RelativePath}' pertence a uma versão não suportada do jogo.");
                    await AppendAsync(entry.Source, entry.Target, entry.Alignment, cancellationToken);
                }
                else
                {
                    await FileTools.CopyAsync(entry.Source, entry.Target, progress, $"Instalando: {entry.RelativePath}", cancellationToken);
                }

                installedFiles.Add(new InstalledFile
                {
                    RelativePath = entry.RelativePath,
                    InstalledHash = await FileTools.Sha256Async(entry.Target, cancellationToken),
                    InstalledSize = new FileInfo(entry.Target).Length,
                    BackupPath = backupPath,
                    RestoreMethod = restoreMethod
                });
            }

            _storage.Installed[translation.Id] = new InstalledTranslation
            {
                Id = translation.Id, Game = translation.Game, Version = translation.Version, GamePath = gameRoot,
                BackupRoot = backupRoot, PackageType = translation.PackageType, SteamAppId = translation.SteamAppId,
                InstalledAt = DateTimeOffset.UtcNow, Files = installedFiles
            };
            _storage.Config.GamePaths[translation.Id] = gameRoot;
            _storage.SaveConfig();
            _storage.SaveInstalled();
            progress?.Report(new ProgressInfo("Instalação concluída.", 100));
            _log($"Instalação concluída: {translation.DisplayName}");
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
    }

    public async Task<RemovalResult> RemoveAsync(Translation translation, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken)
    {
        if (!_storage.Installed.TryGetValue(translation.Id, out var record)) return await RestoreExtrasAsync(translation, cancellationToken);
        for (var i = 0; i < record.Files.Count; i++)
        {
            var file = record.Files[i];
            var target = FileTools.ResolveInside(record.GamePath, file.RelativePath);
            if (!File.Exists(target)) continue;
            progress?.Report(new ProgressInfo($"Verificando arquivo {i + 1} de {record.Files.Count}: {file.RelativePath}", (int)(i * 100L / Math.Max(1, record.Files.Count))));
            if (await FileTools.Sha256Async(target, cancellationToken) != file.InstalledHash) throw new InvalidOperationException($"A remoção foi interrompida porque '{file.RelativePath}' foi alterado depois da instalação.");
        }

        var requiresSteam = false;
        for (var i = 0; i < record.Files.Count; i++)
        {
            var file = record.Files[i];
            var target = FileTools.ResolveInside(record.GamePath, file.RelativePath);
            var restoreMethod = file.RestoreMethod.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(restoreMethod))
            {
                if (!string.IsNullOrWhiteSpace(file.BackupPath) && File.Exists(FileTools.ResolveInside(record.BackupRoot, file.BackupPath)))
                {
                    var legacyBackup = FileTools.ResolveInside(record.BackupRoot, file.BackupPath);
                    restoreMethod = await FileTools.Sha256Async(legacyBackup, cancellationToken) == file.InstalledHash ? "steam" : "backup";
                }
                else restoreMethod = "delete";
            }
            switch (restoreMethod)
            {
                case "backup" when !string.IsNullOrWhiteSpace(file.BackupPath):
                    var backup = FileTools.ResolveInside(record.BackupRoot, file.BackupPath);
                    if (!File.Exists(backup)) throw new FileNotFoundException($"O backup de '{file.RelativePath}' não foi encontrado.");
                    await FileTools.CopyAsync(backup, target, progress, $"Restaurando: {file.RelativePath}", cancellationToken);
                    break;
                case "steam":
                    requiresSteam = true;
                    break;
                default:
                    if (File.Exists(target)) File.Delete(target);
                    break;
            }
        }
        _storage.Installed.Remove(translation.Id);
        _storage.SaveInstalled();
        var extras = await RestoreExtrasAsync(translation, cancellationToken);
        _log($"Tradução removida: {translation.Id}");
        return new RemovalResult(requiresSteam || extras.RequiresSteamRestore, extras.CleanedFiles, extras.LanguageRepaired);
    }

    public async Task<RemovalResult> RestoreExtrasAsync(Translation translation, CancellationToken cancellationToken)
    {
        var gameRoot = _storage.Config.GamePaths.GetValueOrDefault(translation.Id) ?? SteamService.FindGame(translation) ?? "";
        var cleaned = 0;
        if (Directory.Exists(gameRoot))
        {
            foreach (var cleanup in translation.SteamCleanup)
            {
                var target = FileTools.ResolveInside(gameRoot, cleanup.Path);
                if (!File.Exists(target)) continue;
                if (await FileTools.Sha256Async(target, cancellationToken) != cleanup.Sha256.ToLowerInvariant()) throw new InvalidOperationException($"O arquivo extra '{cleanup.Path}' foi alterado e não será apagado automaticamente.");
                File.Delete(target);
                cleaned++;
            }
        }
        var repaired = false;
        if (translation.LanguagePreferenceRepair is { } repair)
        {
            var userRoot = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var target = FileTools.ResolveInside(userRoot, repair.Path);
            repaired = FileTools.RepairLengthPrefixedUtf8(target, repair.From, repair.To);
        }
        return new RemovalResult(false, cleaned, repaired);
    }

    private async Task<List<string>> DownloadPackagesAsync(Translation translation, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken)
    {
        var assets = translation.Assets.Where(x => x.Role.Equals("package", StringComparison.OrdinalIgnoreCase)).ToList();
        if (assets.Count == 0) throw new InvalidDataException("A tradução não possui pacotes ZIP.");
        var directory = Path.Combine(_storage.PackageRoot, translation.Id, translation.Version);
        Directory.CreateDirectory(directory);
        var result = new List<string>();
        for (var i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];
            ValidateAsset(asset);
            var path = FileTools.ResolveInside(directory, asset.FileName);
            if (!File.Exists(path) || await FileTools.Sha256Async(path, cancellationToken) != asset.Sha256.ToLowerInvariant())
                await DownloadAsync(asset.DownloadUrl, path, progress, $"Baixando pacote {i + 1} de {assets.Count}", cancellationToken);
            if (await FileTools.Sha256Async(path, cancellationToken) != asset.Sha256.ToLowerInvariant()) throw new InvalidDataException($"SHA-256 inválido para {asset.FileName}.");
            result.Add(path);
        }
        return result;
    }

    private async Task DownloadAsync(string url, string target, IProgress<ProgressInfo>? progress, string message, CancellationToken cancellationToken)
    {
        var temporary = target + ".download";
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        var buffer = new byte[1024 * 1024];
        long downloaded = 0;
        while (true)
        {
            var count = await input.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            downloaded += count;
            progress?.Report(new ProgressInfo($"{message} — {FormatSize(downloaded)} de {(total.HasValue ? FormatSize(total.Value) : "?")}", total.HasValue ? (int)(downloaded * 100 / total.Value) : null));
        }
        output.Close();
        File.Move(temporary, target, true);
    }

    private static async Task ExtractZipAsync(string package, string staging, IProgress<ProgressInfo>? progress, string message, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(package);
        for (var i = 0; i < archive.Entries.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.Entries[i];
            var target = FileTools.ResolveInside(staging, entry.FullName);
            if (string.IsNullOrEmpty(entry.Name)) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var input = entry.Open();
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
            await input.CopyToAsync(output, cancellationToken);
            progress?.Report(new ProgressInfo($"{message} — {entry.Name}", (int)((i + 1) * 100L / archive.Entries.Count)));
        }
    }

    private static List<PlanEntry> BuildPlan(Translation translation, string staging, string gameRoot)
    {
        var plan = new List<PlanEntry>();
        if (translation.Operations.Count > 0)
        {
            foreach (var operation in translation.Operations)
            {
                var source = FindSource(staging, operation.From);
                var target = FileTools.ResolveInside(gameRoot, operation.To);
                plan.Add(new PlanEntry(operation.Type.ToLowerInvariant(), source, target, FileTools.RelativeTo(gameRoot, target), Math.Max(1, operation.Alignment), operation.ExpectedSize));
            }
        }
        else
        {
            foreach (var rule in translation.Install)
            {
                var source = FindSource(staging, rule.From);
                var targetRoot = rule.To.Equals("game", StringComparison.OrdinalIgnoreCase) ? gameRoot : FileTools.ResolveInside(gameRoot, rule.To);
                if (File.Exists(source))
                {
                    var target = Directory.Exists(targetRoot) ? Path.Combine(targetRoot, Path.GetFileName(source)) : targetRoot;
                    plan.Add(new PlanEntry("copy", source, target, FileTools.RelativeTo(gameRoot, target), 1, 0));
                }
                else
                {
                    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
                    {
                        var target = FileTools.ResolveInside(targetRoot, Path.GetRelativePath(source, file));
                        plan.Add(new PlanEntry("copy", file, target, FileTools.RelativeTo(gameRoot, target), 1, 0));
                    }
                }
            }
        }
        if (plan.Count == 0) throw new InvalidDataException("A tradução não possui arquivos para instalar.");
        if (plan.GroupBy(x => x.RelativePath, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)) throw new InvalidDataException("O pacote tenta instalar o mesmo caminho mais de uma vez.");
        return plan;
    }

    private static string FindSource(string staging, string relative)
    {
        if (relative is "." or "") return staging;
        var direct = FileTools.ResolveInside(staging, relative);
        if (File.Exists(direct) || Directory.Exists(direct)) return direct;
        var normalized = relative.Replace('\\', '/').Trim('/');
        var candidates = Directory.EnumerateFileSystemEntries(staging, "*", SearchOption.AllDirectories)
            .Where(x => FileTools.RelativeTo(staging, x).EndsWith(normalized, StringComparison.OrdinalIgnoreCase)).ToList();
        return candidates.Count == 1 ? candidates[0] : throw new FileNotFoundException($"A origem '{relative}' não existe dentro do pacote.");
    }

    private void EnsureNoConflicts(string id, IEnumerable<string> targets)
    {
        var claims = _storage.Installed.Where(x => !x.Key.Equals(id, StringComparison.OrdinalIgnoreCase))
            .SelectMany(x => x.Value.Files.Select(f => (Path: FileTools.ResolveInside(x.Value.GamePath, f.RelativePath), Owner: x.Key)))
            .ToDictionary(x => x.Path, x => x.Owner, StringComparer.OrdinalIgnoreCase);
        foreach (var target in targets) if (claims.TryGetValue(Path.GetFullPath(target), out var owner)) throw new InvalidOperationException($"O arquivo '{target}' já pertence à tradução '{owner}'.");
    }

    private static async Task AppendAsync(string source, string target, int alignment, CancellationToken cancellationToken)
    {
        await using var output = new FileStream(target, FileMode.Append, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        var padding = (alignment - output.Length % alignment) % alignment;
        if (padding > 0) await output.WriteAsync(new byte[(int)padding], cancellationToken);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static void ValidateTranslation(Translation translation)
    {
        if (string.IsNullOrWhiteSpace(translation.Id) || translation.Id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))) throw new InvalidDataException("A tradução possui um ID inválido.");
        if (translation.PackageType is not ("zip" or "multi-zip" or "internal")) throw new NotSupportedException("Formato de tradução não suportado.");
    }

    private static void ValidateAsset(PackageAsset asset)
    {
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("O pacote deve usar uma URL HTTPS do GitHub.");
        if (asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException($"O pacote '{asset.FileName}' não possui SHA-256 válido.");
        if (!asset.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(asset.FileName) != asset.FileName) throw new InvalidDataException("Nome de pacote inválido.");
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : $"{bytes / (1024d * 1024):0.0} MB";
    private sealed record PlanEntry(string Type, string Source, string Target, string RelativePath, int Alignment, long ExpectedSize);
}
