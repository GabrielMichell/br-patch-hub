using System.IO.Compression;
using System.Net.Http.Headers;

namespace BrPatchHub;

public sealed class TranslationService
{
    private readonly Storage _storage;
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private readonly string? _luciusPersistentRootOverride;

    public TranslationService(Storage storage, Action<string> log, string? luciusPersistentRootOverride = null)
    {
        _storage = storage;
        _log = log;
        _luciusPersistentRootOverride = luciusPersistentRootOverride;
        _http = new HttpClient { Timeout = TimeSpan.FromHours(2) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"BR-Patch-Hub/{AppConstants.AppVersion}");
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

    public async Task<InstallationHealth> GetInstallationHealthAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!_storage.Installed.TryGetValue(id, out var record) || record.Files.Count == 0) return InstallationHealth.Modified;
        var health = await GetRegularInstallationHealthAsync(record, cancellationToken);
        if (health != InstallationHealth.Healthy)
            return LuciusNotebookMigration.AppliesTo(id) && health == InstallationHealth.OriginalRestored && record.NotebookMigration is not null
                ? InstallationHealth.Modified
                : health;
        if (LuciusNotebookMigration.AppliesTo(id))
        {
            if (record.NotebookMigration is null) return InstallationHealth.Modified;
            var model = FileTools.ResolveInside(record.GamePath, record.NotebookMigration.ModelRelativePath);
            var persistent = await LuciusNotebookMigration.VerifyAsync(model, record.NotebookMigration, _log, cancellationToken);
            if (persistent.Skipped > 0 || persistent.Changed != persistent.Found) return InstallationHealth.Modified;
        }
        return InstallationHealth.Healthy;
    }

    private static async Task<InstallationHealth> GetRegularInstallationHealthAsync(InstalledTranslation record, CancellationToken cancellationToken)
    {
        var allInstalled = true;
        var allOriginal = true;
        foreach (var file in record.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = FileTools.ResolveInside(record.GamePath, file.RelativePath);
            var installedMatch = await MatchesAsync(target, file, cancellationToken);
            allInstalled &= installedMatch;
            if (installedMatch) { allOriginal = false; continue; }

            var restoreMethod = file.RestoreMethod.ToLowerInvariant();
            var backup = string.IsNullOrWhiteSpace(record.BackupRoot) || string.IsNullOrWhiteSpace(file.BackupPath)
                ? null
                : FileTools.ResolveInside(record.BackupRoot, file.BackupPath);
            if (string.IsNullOrWhiteSpace(restoreMethod)) restoreMethod = backup is not null && File.Exists(backup) ? "backup" : "delete";
            var originalMatch = restoreMethod switch
            {
                "delete" => !File.Exists(target),
                "backup" when backup is not null && File.Exists(backup) => await FilesEqualAsync(target, backup, cancellationToken),
                _ => false
            };
            allOriginal &= originalMatch;
        }
        return allInstalled ? InstallationHealth.Healthy : allOriginal ? InstallationHealth.OriginalRestored : InstallationHealth.Modified;
    }

    private static async Task<bool> MatchesAsync(string path, InstalledFile file, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return false;
        var info = new FileInfo(path);
        if (info.Length != file.InstalledSize) return false;
        if (file.VerifiedWriteTimeUtc.HasValue && info.LastWriteTimeUtc == file.VerifiedWriteTimeUtc.Value) return true;
        var matches = string.Equals(await FileTools.Sha256Async(path, cancellationToken), file.InstalledHash, StringComparison.OrdinalIgnoreCase);
        if (matches) file.VerifiedWriteTimeUtc = info.LastWriteTimeUtc;
        return matches;
    }

    private static async Task<bool> FilesEqualAsync(string left, string right, CancellationToken cancellationToken)
    {
        if (!File.Exists(left) || !File.Exists(right) || new FileInfo(left).Length != new FileInfo(right).Length) return false;
        return string.Equals(await FileTools.Sha256Async(left, cancellationToken), await FileTools.Sha256Async(right, cancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    public async Task InstallAsync(Translation translation, string gameRoot, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken)
    {
        ValidateTranslation(translation);
        gameRoot = Path.GetFullPath(gameRoot);
        if (!Directory.Exists(gameRoot)) throw new DirectoryNotFoundException("A pasta do jogo não existe.");
        EnsureLuciusClosed(translation, gameRoot);
        var packages = await DownloadPackagesAsync(translation, progress, cancellationToken);
        var staging = Path.Combine(_storage.TempRoot, $"install-{translation.Id}-{Guid.NewGuid():N}");
        var rollbackRoot = Path.Combine(_storage.TempRoot, $"rollback-{translation.Id}-{Guid.NewGuid():N}");
        var previous = _storage.Installed.GetValueOrDefault(translation.Id);
        List<FileSnapshot>? snapshots = null;
        NotebookMigrationRecord? notebookMigration = null;
        Directory.CreateDirectory(staging);
        try
        {
            for (var i = 0; i < packages.Count; i++) await ExtractZipAsync(packages[i], staging, progress, $"Extraindo pacote {i + 1} de {packages.Count}", cancellationToken);
            var plan = BuildPlan(translation, staging, gameRoot);
            if (LuciusNotebookMigration.AppliesTo(translation.Id))
            {
                LuciusNotebookMigration.ValidateModel(FindSource(staging, LuciusNotebookMigration.ModelRelativePath));
                var english = FindSource(staging, LuciusNotebookMigration.EnglishCsvRelativePath);
                var portuguese = FindSource(staging, LuciusNotebookMigration.PortugueseCsvRelativePath);
                if (await FileTools.Sha256Async(english, cancellationToken) != await FileTools.Sha256Async(portuguese, cancellationToken))
                    throw new InvalidDataException("O pacote do Lucius III possui conteúdos diferentes em English.csv e Português.csv.");
            }
            EnsureNoConflicts(translation.Id, plan.Select(x => x.Target));

            if (previous is not null)
            {
                progress?.Report(new ProgressInfo("Preparando ponto de restauração da versão instalada...", 0));
                snapshots = CaptureSnapshots(TransactionTargets(translation, previous, plan), rollbackRoot);
                var removal = await RemoveAsync(translation, progress, cancellationToken);
                if (removal.RequiresSteamRestore) throw new InvalidOperationException("A Steam precisa restaurar os arquivos originais antes de atualizar esta tradução.");
            }

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
                    VerifiedWriteTimeUtc = File.GetLastWriteTimeUtc(entry.Target),
                    BackupPath = backupPath,
                    RestoreMethod = restoreMethod
                });
            }

            if (LuciusNotebookMigration.AppliesTo(translation.Id))
            {
                progress?.Report(new ProgressInfo("Migrando cadernos persistentes do Lucius III...", null));
                var modelPath = FileTools.ResolveInside(gameRoot, LuciusNotebookMigration.ModelRelativePath);
                var persistentRoot = _luciusPersistentRootOverride ?? LuciusNotebookMigration.DefaultPersistentRoot;
                var migration = await LuciusNotebookMigration.MigrateAsync(modelPath, persistentRoot, Path.GetFileName(backupRoot), _log, cancellationToken);
                notebookMigration = migration.Record;
                progress?.Report(new ProgressInfo($"Migração persistente: {migration.Changed} arquivo(s), {migration.TextsChanged} texto(s)", 100));
            }

            _storage.Installed[translation.Id] = new InstalledTranslation
            {
                Id = translation.Id, Game = translation.Game, Version = translation.Version, GamePath = gameRoot,
                BackupRoot = backupRoot, PackageType = translation.PackageType, SteamAppId = translation.SteamAppId,
                InstalledAt = DateTimeOffset.UtcNow, Files = installedFiles, NotebookMigration = notebookMigration
            };
            _storage.Config.GamePaths[translation.Id] = gameRoot;
            _storage.SaveConfig();
            _storage.SaveInstalled();
            progress?.Report(new ProgressInfo("Instalação concluída.", 100));
            _log($"Instalação concluída: {translation.DisplayName}");
        }
        catch (Exception ex)
        {
            if (snapshots is not null && previous is not null)
            {
                try
                {
                    progress?.Report(new ProgressInfo("Falha detectada. Restaurando a tradução anterior...", null));
                    RestoreSnapshots(snapshots);
                    _storage.Installed[translation.Id] = previous;
                    _storage.SaveInstalled();
                    _log($"Atualização revertida com sucesso: {translation.Id} permaneceu na versão {previous.Version}.");
                }
                catch (Exception rollbackError)
                {
                    _log($"Falha crítica no rollback de {translation.Id}: {rollbackError.Message}");
                    throw new AggregateException("A atualização falhou e o rollback não pôde ser concluído automaticamente.", ex, rollbackError);
                }
                throw new InvalidOperationException($"A atualização falhou. A versão {previous.Version} foi restaurada e continua instalada. Motivo: {ex.Message}", ex);
            }
            if (notebookMigration is not null)
            {
                await LuciusNotebookMigration.RestoreExactBackupsAsync(notebookMigration, _log, cancellationToken);
                _log("Falha na instalação: cadernos persistentes restaurados ao estado anterior.");
            }
            throw;
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            if (Directory.Exists(rollbackRoot)) Directory.Delete(rollbackRoot, true);
        }
    }

    private static IEnumerable<string> TransactionTargets(Translation translation, InstalledTranslation previous, IEnumerable<PlanEntry> plan)
    {
        foreach (var file in previous.Files) yield return FileTools.ResolveInside(previous.GamePath, file.RelativePath);
        foreach (var entry in plan) yield return entry.Target;
        foreach (var cleanup in translation.SteamCleanup) yield return FileTools.ResolveInside(previous.GamePath, cleanup.Path);
        if (translation.LanguagePreferenceRepair is { } repair)
            yield return FileTools.ResolveInside(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), repair.Path);
        if (previous.NotebookMigration is { } notebook)
            foreach (var file in notebook.Files)
                yield return FileTools.ResolveInside(notebook.PersistentRoot, file.RelativePath);
    }

    private static List<FileSnapshot> CaptureSnapshots(IEnumerable<string> targets, string rollbackRoot)
    {
        Directory.CreateDirectory(rollbackRoot);
        var result = new List<FileSnapshot>();
        foreach (var target in targets.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string? copy = null;
            if (File.Exists(target))
            {
                copy = Path.Combine(rollbackRoot, $"{result.Count:D6}.bak");
                File.Copy(target, copy, true);
            }
            result.Add(new FileSnapshot(target, copy));
        }
        return result;
    }

    private static void RestoreSnapshots(IEnumerable<FileSnapshot> snapshots)
    {
        foreach (var snapshot in snapshots)
        {
            if (snapshot.Backup is null)
            {
                if (File.Exists(snapshot.Target)) File.Delete(snapshot.Target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(snapshot.Target)!);
            File.Copy(snapshot.Backup, snapshot.Target, true);
        }
    }

    public async Task<RemovalResult> RemoveAsync(Translation translation, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken)
    {
        if (!_storage.Installed.TryGetValue(translation.Id, out var record)) return await RestoreExtrasAsync(translation, cancellationToken);
        EnsureLuciusClosed(translation, record.GamePath);
        if (await GetRegularInstallationHealthAsync(record, cancellationToken) == InstallationHealth.OriginalRestored)
        {
            await RestorePersistentTextsAsync(translation, record, cancellationToken);
            _storage.Installed.Remove(translation.Id);
            _storage.SaveInstalled();
            _log($"Registro removido: os arquivos originais de {translation.Id} já estavam restaurados.");
            return new RemovalResult(false, 0, false);
        }
        var fileStates = new List<(InstalledFile File, bool Installed, bool Original, string RestoreMethod)>();
        for (var i = 0; i < record.Files.Count; i++)
        {
            var file = record.Files[i];
            var target = FileTools.ResolveInside(record.GamePath, file.RelativePath);
            progress?.Report(new ProgressInfo($"Verificando arquivo {i + 1} de {record.Files.Count}: {file.RelativePath}", (int)(i * 100L / Math.Max(1, record.Files.Count))));
            var installed = await MatchesAsync(target, file, cancellationToken);
            var restoreMethod = await EffectiveRestoreMethodAsync(translation, record, file, cancellationToken);
            var original = !installed && await MatchesOriginalAsync(record, file, target, restoreMethod, cancellationToken);
            if (!installed && !original)
                throw new InvalidOperationException($"A remoção foi interrompida porque '{file.RelativePath}' não corresponde nem ao arquivo traduzido registrado nem ao backup original.");
            fileStates.Add((file, installed, original, restoreMethod));
        }

        await RestorePersistentTextsAsync(translation, record, cancellationToken);

        var requiresSteam = false;
        for (var i = 0; i < fileStates.Count; i++)
        {
            var state = fileStates[i];
            var file = state.File;
            var target = FileTools.ResolveInside(record.GamePath, file.RelativePath);
            if (state.Original) continue;
            switch (state.RestoreMethod)
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

    private static async Task<string> EffectiveRestoreMethodAsync(Translation translation, InstalledTranslation record, InstalledFile file, CancellationToken cancellationToken)
    {
        var restoreMethod = file.RestoreMethod.ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(restoreMethod))
        {
            var backup = string.IsNullOrWhiteSpace(file.BackupPath) ? null : FileTools.ResolveInside(record.BackupRoot, file.BackupPath);
            restoreMethod = backup is not null && File.Exists(backup)
                ? await FileTools.Sha256Async(backup, cancellationToken) == file.InstalledHash ? "steam" : "backup"
                : "delete";
        }
        if (restoreMethod == "steam" && translation.SteamCleanup.Any(cleanup =>
                cleanup.Path.Equals(file.RelativePath, StringComparison.OrdinalIgnoreCase) &&
                cleanup.Sha256.Equals(file.InstalledHash, StringComparison.OrdinalIgnoreCase)))
            return "delete";
        return restoreMethod;
    }

    private static async Task<bool> MatchesOriginalAsync(InstalledTranslation record, InstalledFile file, string target, string restoreMethod, CancellationToken cancellationToken)
    {
        return restoreMethod switch
        {
            "delete" => !File.Exists(target),
            "backup" when !string.IsNullOrWhiteSpace(file.BackupPath) =>
                await FilesEqualAsync(target, FileTools.ResolveInside(record.BackupRoot, file.BackupPath), cancellationToken),
            _ => false
        };
    }

    public async Task<NotebookOperationResult?> RestorePersistentTextsAsync(Translation translation, CancellationToken cancellationToken)
    {
        if (!_storage.Installed.TryGetValue(translation.Id, out var record)) return null;
        EnsureLuciusClosed(translation, record.GamePath);
        var result = await RestorePersistentTextsAsync(translation, record, cancellationToken);
        if (result is not null && result.Skipped == 0)
        {
            record.NotebookMigration = null;
            _storage.SaveInstalled();
        }
        return result;
    }

    private async Task<NotebookOperationResult?> RestorePersistentTextsAsync(Translation translation, InstalledTranslation record, CancellationToken cancellationToken)
    {
        if (!LuciusNotebookMigration.AppliesTo(translation.Id) || record.NotebookMigration is null) return null;
        var model = FileTools.ResolveInside(record.GamePath, record.NotebookMigration.ModelRelativePath);
        if (!File.Exists(model)) { _log("Reversão dos cadernos persistentes ignorada: modelo PT-BR ausente. Nenhum save foi sobrescrito."); return null; }
        var result = await LuciusNotebookMigration.RevertTextsAsync(model, record.NotebookMigration, _log, cancellationToken);
        if (result.Skipped > 0) _log($"Limitação segura: {result.Skipped} Notebook.xml não pôde ser revertido; ele foi preservado integralmente para não apagar progresso.");
        return result;
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

    private static void EnsureLuciusClosed(Translation translation, string gameRoot)
    {
        if (!LuciusNotebookMigration.AppliesTo(translation.Id)) return;
        var running = GameProcessService.FindRunning(translation, gameRoot);
        if (running is not null) throw new InvalidOperationException($"Feche o Lucius III para migrar os cadernos persistentes. Processo encontrado: {running}");
    }

    private static void ValidateAsset(PackageAsset asset)
    {
        if (!Uri.TryCreate(asset.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !(uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("O pacote deve usar uma URL HTTPS do GitHub.");
        if (asset.Sha256.Length != 64 || !asset.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException($"O pacote '{asset.FileName}' não possui SHA-256 válido.");
        if (!asset.FileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(asset.FileName) != asset.FileName) throw new InvalidDataException("Nome de pacote inválido.");
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024 ? $"{bytes / (1024d * 1024 * 1024):0.0} GB" : $"{bytes / (1024d * 1024):0.0} MB";
    private sealed record PlanEntry(string Type, string Source, string Target, string RelativePath, int Alignment, long ExpectedSize);
    private sealed record FileSnapshot(string Target, string? Backup);
}
