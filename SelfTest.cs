using System.Text;
using System.Text.Json;
using System.IO.Compression;

namespace BrPatchHub;

public static class SelfTest
{
    public static int RunLuciusNotebookFixture(string modelSource, string fixtureSource)
    {
        var root = Path.Combine(Path.GetTempPath(), $"br-patch-hub-lucius-fixture-{Guid.NewGuid():N}");
        try
        {
            var model = Path.Combine(root, "game", "Lucius3_Data", "StreamingAssets", "Notebook.xml");
            var fixture = Path.Combine(root, "persistent", "Game", "Notebook.xml");
            Directory.CreateDirectory(Path.GetDirectoryName(model)!);
            Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
            var sourceModelHash = FileTools.Sha256Async(modelSource).GetAwaiter().GetResult();
            var sourceFixtureHash = FileTools.Sha256Async(fixtureSource).GetAwaiter().GetResult();
            File.Copy(modelSource, model);
            File.Copy(fixtureSource, fixture);
            var result = LuciusNotebookMigration.MigrateAsync(model, Path.Combine(root, "persistent"), "fixture-test", _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            if (result.Found != 1 || result.Changed is < 0 or > 1 || result.Skipped != 0 || result.Record?.Files.Single().TextCount != LuciusNotebookMigration.ExpectedTextCount)
                throw new Exception("O fixture real não produziu uma migração compatível de 360 textos.");
            var verification = LuciusNotebookMigration.VerifyAsync(model, result.Record, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            if (verification.Changed != 1 || verification.Skipped != 0) throw new Exception("O fixture real falhou na verificação posterior.");
            var migratedHash = FileTools.Sha256Async(fixture).GetAwaiter().GetResult();
            var migratedWriteTime = File.GetLastWriteTimeUtc(fixture);
            Thread.Sleep(1100);
            var repeated = LuciusNotebookMigration.MigrateAsync(model, Path.Combine(root, "persistent"), "fixture-test", _ => { }, CancellationToken.None).GetAwaiter().GetResult();
            if (repeated.Changed != 0 || repeated.TextsChanged != 0 || FileTools.Sha256Async(fixture).GetAwaiter().GetResult() != migratedHash || File.GetLastWriteTimeUtc(fixture) != migratedWriteTime)
                throw new Exception("A segunda migração do fixture real regravou um Notebook.xml que já estava correto.");
            if (FileTools.Sha256Async(modelSource).GetAwaiter().GetResult() != sourceModelHash || FileTools.Sha256Async(fixtureSource).GetAwaiter().GetResult() != sourceFixtureHash)
                throw new Exception("Um arquivo-fonte do fixture foi modificado.");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "br-patch-hub-lucius-fixture-error.txt"), ex.ToString());
            return 1;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static int Render(string output, int width = 1520, int height = 940)
    {
        var root = Path.Combine(Path.GetTempPath(), $"br-patch-hub-render-{Guid.NewGuid():N}");
        try
        {
            using var form = new MainForm(new Storage(root), false) { Size = new Size(width, height), Opacity = 0 };
            form.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(form.Width, form.Height);
            form.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
            bitmap.Save(output, System.Drawing.Imaging.ImageFormat.Png);
            form.Close();
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "br-patch-hub-render-error.txt"), ex.ToString());
            return 1;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"br-patch-hub-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var safe = FileTools.ResolveInside(root, "dados/arquivo.txt");
            if (!safe.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new Exception("Falha na resolução segura de caminhos.");
            try { FileTools.ResolveInside(root, "../fora.txt"); throw new Exception("Path traversal não foi bloqueado."); }
            catch (InvalidDataException) { }

            if (GameProcessService.IsActiveSnapshot(false, 0, 0)) throw new Exception("Processo residual foi considerado jogo ativo.");
            if (!GameProcessService.IsActiveSnapshot(false, 4, 20)) throw new Exception("Processo ativo não foi reconhecido.");
            if (GameProcessService.IsActiveSnapshot(true, 4, 20)) throw new Exception("Processo encerrado foi considerado ativo.");

            var config = Path.Combine(root, "config.sav");
            var oldValue = Encoding.UTF8.GetBytes("Português");
            var bytes = new byte[oldValue.Length + 5];
            bytes[0] = 1; bytes[1] = (byte)oldValue.Length; Array.Copy(oldValue, 0, bytes, 2, oldValue.Length); bytes[^2] = 2; bytes[^1] = 3;
            File.WriteAllBytes(config, bytes);
            if (!FileTools.RepairLengthPrefixedUtf8(config, "Português", "English")) throw new Exception("Correção de idioma não executada.");
            if (!Encoding.UTF8.GetString(File.ReadAllBytes(config)).Contains("English")) throw new Exception("Correção de idioma inválida.");

            RunLuciusNotebookMigrationTests(root);

            var migrationRoot = Path.Combine(root, "migration-state");
            var migratedBackup = Path.Combine(migrationRoot, "backups", "translation-backup-1");
            Directory.CreateDirectory(migratedBackup);
            var legacyRecords = new Dictionary<string, InstalledTranslation> { ["migration-test"] = new() { Id = "migration-test", BackupRoot = Path.Combine(Path.GetTempPath(), "TradutorHub", "backups", "translation-backup-1") } };
            File.WriteAllText(Path.Combine(migrationRoot, "installed.json"), JsonSerializer.Serialize(legacyRecords));
            var migratedStorage = new Storage(migrationRoot);
            if (migratedStorage.Installed["migration-test"].BackupRoot != migratedBackup) throw new Exception("Caminho antigo de backup não foi migrado.");

            var storage = new Storage(Path.Combine(root, "state"));
            var game = Path.Combine(root, "game");
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(Path.Combine(source, "Game_Data"));
            Directory.CreateDirectory(Path.Combine(game, "Game_Data"));
            var sourceFile = Path.Combine(source, "Game_Data", "text.bin");
            var gameFile = Path.Combine(game, "Game_Data", "text.bin");
            File.WriteAllText(sourceFile, "traduzido");
            File.WriteAllText(gameFile, "original");
            var zip = Path.Combine(root, "package.zip");
            ZipFile.CreateFromDirectory(source, zip);
            var hash = FileTools.Sha256Async(zip).GetAwaiter().GetResult();
            var packageDir = Path.Combine(storage.PackageRoot, "self-test", "1.0.0");
            Directory.CreateDirectory(packageDir);
            File.Copy(zip, Path.Combine(packageDir, "package.zip"));
            var translation = new Translation
            {
                Id = "self-test", Game = "Teste", Version = "1.0.0", PackageType = "zip",
                Install = [new InstallRule { From = "Game_Data", To = "Game_Data" }],
                Assets = [new PackageAsset { Role = "package", FileName = "package.zip", DownloadUrl = "https://github.com/example/test/releases/download/v1/package.zip", Sha256 = hash }]
            };
            var service = new TranslationService(storage, _ => { });
            service.InstallAsync(translation, game, null, CancellationToken.None).GetAwaiter().GetResult();
            if (File.ReadAllText(gameFile) != "traduzido") throw new Exception("Instalação temporária falhou.");
            if (service.GetInstallationHealthAsync(translation.Id).GetAwaiter().GetResult() != InstallationHealth.Healthy) throw new Exception("Tradução íntegra não foi reconhecida.");
            File.WriteAllText(gameFile, "alterado externamente"); File.SetLastWriteTimeUtc(gameFile, DateTime.UtcNow.AddSeconds(2));
            if (service.GetInstallationHealthAsync(translation.Id).GetAwaiter().GetResult() != InstallationHealth.Modified) throw new Exception("Arquivo alterado não exigiu verificação.");
            File.WriteAllText(gameFile, "traduzido"); File.SetLastWriteTimeUtc(gameFile, DateTime.UtcNow.AddSeconds(4));

            if (!SemanticVersion.TryCompare("1.0.0", "1.0.0", out var equal) || equal != 0) throw new Exception("Cenário 1 de versão falhou.");
            if (!SemanticVersion.TryCompare("1.0.0", "1.1.0", out var newer) || newer >= 0) throw new Exception("Cenário 2 de versão falhou.");
            if (!SemanticVersion.TryCompare("1.10.0", "1.9.0", out var olderCatalog) || olderCatalog <= 0) throw new Exception("Cenário 4 de versão falhou.");
            if (!SemanticVersion.TryCompare("v1.0", "1.0.0", out var normalized) || normalized != 0) throw new Exception("Normalização semântica falhou.");

            var updateFiles = Path.Combine(root, "update-package");
            Directory.CreateDirectory(updateFiles);
            File.WriteAllText(Path.Combine(updateFiles, "text.bin"), "versão 1.1");
            File.WriteAllText(Path.Combine(updateFiles, "payload.bin"), "dados");
            var guardFile = Path.Combine(game, "guard.bin");
            File.WriteAllText(guardFile, "original protegido");
            var updateZip = Path.Combine(root, "update-package.zip");
            ZipFile.CreateFromDirectory(updateFiles, updateZip);
            var updatePackageHash = FileTools.Sha256Async(updateZip).GetAwaiter().GetResult();
            var updatePackageDir = Path.Combine(storage.PackageRoot, "self-test", "1.1.0");
            Directory.CreateDirectory(updatePackageDir);
            File.Copy(updateZip, Path.Combine(updatePackageDir, "update-package.zip"));
            var failingUpdate = new Translation
            {
                Id = "self-test", Game = "Teste", Version = "1.1.0", PackageType = "zip",
                Operations =
                [
                    new InstallOperation { Type = "copy", From = "text.bin", To = "Game_Data/text.bin" },
                    new InstallOperation { Type = "append", From = "payload.bin", To = "guard.bin", ExpectedSize = 999 }
                ],
                Assets = [new PackageAsset { Role = "package", FileName = "update-package.zip", DownloadUrl = "https://github.com/example/test/releases/download/v1.1/update-package.zip", Sha256 = updatePackageHash }]
            };
            try { service.InstallAsync(failingUpdate, game, null, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("A atualização inválida não foi bloqueada."); }
            catch (InvalidOperationException ex) when (ex.Message.Contains("versão 1.0.0 foi restaurada", StringComparison.OrdinalIgnoreCase)) { }
            if (File.ReadAllText(gameFile) != "traduzido" || File.ReadAllText(guardFile) != "original protegido" || storage.Installed[translation.Id].Version != "1.0.0") throw new Exception("Cenário 6: rollback não preservou a tradução anterior.");

            var successfulUpdate = new Translation
            {
                Id = "self-test", Game = "Teste", Version = "1.1.0", PackageType = "zip",
                Operations = [new InstallOperation { Type = "copy", From = "text.bin", To = "Game_Data/text.bin" }],
                Assets = failingUpdate.Assets
            };
            service.InstallAsync(successfulUpdate, game, null, CancellationToken.None).GetAwaiter().GetResult();
            if (File.ReadAllText(gameFile) != "versão 1.1" || storage.Installed[translation.Id].Version != "1.1.0") throw new Exception("Cenário 5: atualização concluída não foi registrada.");

            var updatedRecord = storage.Installed[translation.Id];
            var updatedFile = updatedRecord.Files.Single();
            File.Copy(FileTools.ResolveInside(updatedRecord.BackupRoot, updatedFile.BackupPath!), gameFile, true);
            if (service.GetInstallationHealthAsync(translation.Id).GetAwaiter().GetResult() != InstallationHealth.OriginalRestored) throw new Exception("Arquivos originais restaurados não foram reconhecidos.");

            var removal = service.RemoveAsync(successfulUpdate, null, CancellationToken.None).GetAwaiter().GetResult();
            if (removal.RequiresSteamRestore || File.ReadAllText(gameFile) != "original") throw new Exception("Restauração temporária falhou.");

            var updateSource = Path.Combine(root, "update-source.exe");
            var updateTarget = Path.Combine(root, "installed", "BR Patch Hub.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(updateTarget)!);
            File.WriteAllText(updateSource, "versão nova");
            File.WriteAllText(updateTarget, "versão antiga");
            var updateHash = FileTools.Sha256Async(updateSource).GetAwaiter().GetResult();
            UpdateInstaller.Apply(updateSource, updateTarget, updateHash);
            if (File.ReadAllText(updateTarget) != "versão nova" || File.Exists(updateTarget + ".old")) throw new Exception("Substituição automática do executável falhou.");

            var catalogPath = Path.Combine(AppContext.BaseDirectory, "catalog.json");
            if (File.Exists(catalogPath))
            {
                var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(catalogPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (catalog is null || catalog.SchemaVersion < 1 || catalog.Translations.Count == 0) throw new Exception("Catálogo embarcado inválido.");
                var luciusCatalog = catalog.Translations.SingleOrDefault(x => LuciusNotebookMigration.AppliesTo(x.Id));
                if (luciusCatalog?.LanguagePreferenceRepair is not null) throw new Exception("O catálogo de Lucius III ainda permite modificar config.sav.");
                var staleStorage = new Storage(Path.Combine(root, "stale-state")); var stale = catalog.Translations[0]; staleStorage.Installed[stale.Id] = new InstalledTranslation { Id = stale.Id, Game = stale.Game, Version = stale.Version, GamePath = Path.Combine(root, "jogo-removido") }; staleStorage.Config.GamePaths[stale.Id] = Path.Combine(root, "jogo-removido"); staleStorage.SaveInstalled(); staleStorage.SaveConfig(); using var staleForm = new MainForm(staleStorage, false); var reconcile = typeof(MainForm).GetMethod("ReconcileUninstalledGames", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic) ?? throw new Exception("Rotina de reconciliação ausente."); reconcile.Invoke(staleForm, null); if (staleStorage.Installed.ContainsKey(stale.Id) || staleStorage.Config.GamePaths.ContainsKey(stale.Id)) throw new Exception("Registro de jogo desinstalado não foi limpo.");
            }
            using var form = new MainForm(new Storage(Path.Combine(root, "ui-state")));
            form.CreateControl();
            form.PerformLayout();
            if (form.Text != AppConstants.AppName || form.MinimumSize.Width < 800) throw new Exception("A interface principal não foi criada corretamente.");
            return 0;
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "br-patch-hub-self-test-error.txt"), ex.ToString());
            return 1;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static void RunLuciusNotebookMigrationTests(string root)
    {
        var testRoot = Path.Combine(root, "lucius-notebook-migration");
        var gameRoot = Path.Combine(testRoot, "game");
        var model = Path.Combine(gameRoot, "Lucius3_Data", "StreamingAssets", "Notebook.xml");
        var persistent = Path.Combine(testRoot, "persistent");
        var gameNotebook = Path.Combine(persistent, "Game", "Notebook.xml");
        var autoNotebook = Path.Combine(persistent, "AUTOSAVE", "Notebook.xml");
        var saveFile = Path.Combine(persistent, "AUTOSAVE", "Notebook_MainPage.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(model)!);
        Directory.CreateDirectory(Path.GetDirectoryName(gameNotebook)!);
        Directory.CreateDirectory(Path.GetDirectoryName(autoNotebook)!);
        File.WriteAllText(model, BuildNotebookXml("PT-BR", "false", "Lucius", "Linha inicial"), new UTF8Encoding(false));
        File.WriteAllText(gameNotebook, BuildNotebookXml("EN", "true", "Lucius", "Diálogo salvo"), new UTF8Encoding(false));
        File.Copy(gameNotebook, autoNotebook);
        File.WriteAllBytes(saveFile, [1, 2, 3, 4, 5, 6]);
        var saveHash = FileTools.Sha256Async(saveFile).GetAwaiter().GetResult();
        var logs = new List<string>();

        var migration = LuciusNotebookMigration.MigrateAsync(model, persistent, "install-test", logs.Add, CancellationToken.None).GetAwaiter().GetResult();
        if (migration.Found != 2 || migration.Changed != 2 || migration.Skipped != 0 || migration.TextsChanged != 720 || migration.Record?.Files.Count != 2)
            throw new Exception("Migração dos 360 textos persistentes falhou.");
        var migratedText = File.ReadAllText(gameNotebook);
        if (!migratedText.Contains("PT-BR 359") || !migratedText.Contains("<active>true</active>") || !migratedText.Contains("Diálogo salvo"))
            throw new Exception("Migração alterou ou perdeu dados dinâmicos.");
        if (FileTools.Sha256Async(saveFile).GetAwaiter().GetResult() != saveHash) throw new Exception("Arquivo .sav foi modificado pela migração.");

        var backup = FileTools.ResolveInside(migration.Record.BackupRoot, "Game/Notebook.xml");
        var backupHash = FileTools.Sha256Async(backup).GetAwaiter().GetResult();
        var gameNotebookHash = FileTools.Sha256Async(gameNotebook).GetAwaiter().GetResult();
        var gameNotebookWriteTime = File.GetLastWriteTimeUtc(gameNotebook);
        Thread.Sleep(1100);
        var repeat = LuciusNotebookMigration.MigrateAsync(model, persistent, "install-test", logs.Add, CancellationToken.None).GetAwaiter().GetResult();
        if (repeat.Changed != 0 || repeat.TextsChanged != 0 || FileTools.Sha256Async(backup).GetAwaiter().GetResult() != backupHash || FileTools.Sha256Async(gameNotebook).GetAwaiter().GetResult() != gameNotebookHash || File.GetLastWriteTimeUtc(gameNotebook) != gameNotebookWriteTime)
            throw new Exception("Migração repetida sobrescreveu o backup ou não foi idempotente.");

        var invalidModel = Path.Combine(testRoot, "invalid-model.xml");
        File.WriteAllText(invalidModel, BuildNotebookXml("PT-BR", "false", "Lucius", "Linha", 359), new UTF8Encoding(false));
        try { LuciusNotebookMigration.ValidateModel(invalidModel); throw new Exception("Modelo com 359 textos não foi rejeitado."); }
        catch (InvalidDataException ex) when (ex.Message.Contains("exatamente 360", StringComparison.OrdinalIgnoreCase)) { }

        var incompatible = Path.Combine(persistent, "SaveIncompativel", "Notebook.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(incompatible)!);
        File.WriteAllText(incompatible, "<NotebookData version=\"999\"><text>não alterar</text></NotebookData>");
        var incompatibleHash = FileTools.Sha256Async(incompatible).GetAwaiter().GetResult();
        var withInvalid = LuciusNotebookMigration.MigrateAsync(model, persistent, "install-test", logs.Add, CancellationToken.None).GetAwaiter().GetResult();
        if (withInvalid.Skipped != 1 || FileTools.Sha256Async(incompatible).GetAwaiter().GetResult() != incompatibleHash || !logs.Any(x => x.Contains("Arquivo incompatível ignorado")))
            throw new Exception("Notebook incompatível não foi preservado corretamente.");

        var verified = LuciusNotebookMigration.VerifyAsync(model, migration.Record, logs.Add, CancellationToken.None).GetAwaiter().GetResult();
        if (verified.Changed != 2 || verified.Skipped != 0) throw new Exception("Verificação dos notebooks persistentes falhou.");

        var dynamic = File.ReadAllText(gameNotebook).Replace("<active>true</active>", "<active>false</active>").Replace("Diálogo salvo", "Diálogo novo após instalação");
        File.WriteAllText(gameNotebook, dynamic, new UTF8Encoding(false));
        var reverted = LuciusNotebookMigration.RevertTextsAsync(model, migration.Record, logs.Add, CancellationToken.None).GetAwaiter().GetResult();
        var revertedText = File.ReadAllText(gameNotebook);
        if (reverted.Changed != 2 || !revertedText.Contains("EN 359") || !revertedText.Contains("Diálogo novo após instalação") || !revertedText.Contains("<active>false</active>"))
            throw new Exception("Reversão não preservou progresso e diálogo novos.");
        if (FileTools.Sha256Async(saveFile).GetAwaiter().GetResult() != saveHash) throw new Exception("Arquivo .sav foi modificado pela reversão.");
        if (Directory.EnumerateFiles(persistent, "*.tmp", SearchOption.AllDirectories).Any()) throw new Exception("Arquivo temporário residual encontrado.");

        var cancelledRoot = Path.Combine(testRoot, "cancelled-persistent");
        var cancelledA = Path.Combine(cancelledRoot, "Game", "Notebook.xml");
        var cancelledB = Path.Combine(cancelledRoot, "SAVE", "Notebook.xml");
        Directory.CreateDirectory(Path.GetDirectoryName(cancelledA)!);
        Directory.CreateDirectory(Path.GetDirectoryName(cancelledB)!);
        File.WriteAllText(cancelledA, BuildNotebookXml("EN", "true", "A", "A"), new UTF8Encoding(false));
        File.WriteAllText(cancelledB, BuildNotebookXml("EN", "true", "B", "B"), new UTF8Encoding(false));
        using (var cancellation = new CancellationTokenSource())
        {
            try
            {
                LuciusNotebookMigration.MigrateAsync(model, cancelledRoot, "cancel-test", message => { if (message.Contains("textos atualizados")) cancellation.Cancel(); }, cancellation.Token).GetAwaiter().GetResult();
                throw new Exception("Cancelamento da migração não foi respeitado.");
            }
            catch (OperationCanceledException) { }
        }
        if (!File.ReadAllText(cancelledA).Contains("EN 359") || !File.ReadAllText(cancelledB).Contains("EN 359"))
            throw new Exception("Rollback de uma migração interrompida não restaurou os notebooks.");

        RunLuciusServiceIntegrationTest(testRoot);
    }

    private static void RunLuciusServiceIntegrationTest(string root)
    {
        var integration = Path.Combine(root, "service-integration");
        var state = new Storage(Path.Combine(integration, "state"));
        var game = Path.Combine(integration, "game");
        var persistent = Path.Combine(integration, "persistent");
        var source = Path.Combine(integration, "package");
        var sourceModel = Path.Combine(source, "Lucius3_Data", "StreamingAssets", "Notebook.xml");
        var gameModel = Path.Combine(game, "Lucius3_Data", "StreamingAssets", "Notebook.xml");
        var persistentNotebook = Path.Combine(persistent, "Game", "Notebook.xml");
        var configSave = Path.Combine(persistent, "config.sav");
        var binarySave = Path.Combine(persistent, "Game", "Notebook_MainPage.sav");
        Directory.CreateDirectory(Path.GetDirectoryName(sourceModel)!);
        Directory.CreateDirectory(Path.GetDirectoryName(gameModel)!);
        Directory.CreateDirectory(Path.GetDirectoryName(persistentNotebook)!);
        File.WriteAllText(sourceModel, BuildNotebookXml("PT-BR", "false", "Modelo", "Modelo"), new UTF8Encoding(false));
        var sourceLocalization = Path.Combine(source, "Lucius3_Data", "StreamingAssets", "Localization");
        Directory.CreateDirectory(sourceLocalization);
        File.WriteAllText(Path.Combine(sourceLocalization, "English.csv"), "1909;Não é possível usar o coração nesta área", new UTF8Encoding(false));
        File.Copy(Path.Combine(sourceLocalization, "English.csv"), Path.Combine(sourceLocalization, "Português.csv"));
        File.WriteAllText(gameModel, BuildNotebookXml("EN", "false", "Jogo", "Jogo"), new UTF8Encoding(false));
        File.WriteAllText(persistentNotebook, BuildNotebookXml("EN", "true", "Save", "Antes"), new UTF8Encoding(false));
        File.WriteAllBytes(configSave, [10, 20, 30, 40, 50]);
        File.WriteAllBytes(binarySave, [60, 70, 80, 90]);
        var configHash = FileTools.Sha256Async(configSave).GetAwaiter().GetResult();
        var binarySaveHash = FileTools.Sha256Async(binarySave).GetAwaiter().GetResult();
        var zip = Path.Combine(integration, "lucius-package.zip");
        ZipFile.CreateFromDirectory(source, zip);
        var hash = FileTools.Sha256Async(zip).GetAwaiter().GetResult();
        var packageDirectory = Path.Combine(state.PackageRoot, LuciusNotebookMigration.TranslationId, "1.0.0");
        Directory.CreateDirectory(packageDirectory);
        File.Copy(zip, Path.Combine(packageDirectory, "lucius.zip"));
        var translation = new Translation
        {
            Id = LuciusNotebookMigration.TranslationId, Game = "Lucius III", Version = "1.0.0", PackageType = "zip",
            Install = [new InstallRule { From = "Lucius3_Data", To = "Lucius3_Data" }],
            Assets = [new PackageAsset { Role = "package", FileName = "lucius.zip", DownloadUrl = "https://github.com/example/lucius/releases/download/v1/lucius.zip", Sha256 = hash }]
        };
        var service = new TranslationService(state, _ => { }, persistent);
        service.InstallAsync(translation, game, null, CancellationToken.None).GetAwaiter().GetResult();
        if (state.Installed[translation.Id].NotebookMigration?.Files.Count != 1 || !File.ReadAllText(persistentNotebook).Contains("PT-BR 359"))
            throw new Exception("Instalação normal não integrou a migração persistente.");
        if (service.GetInstallationHealthAsync(translation.Id).GetAwaiter().GetResult() != InstallationHealth.Healthy)
            throw new Exception("Verificação normal não incluiu o caderno persistente.");
        service.InstallAsync(translation, game, null, CancellationToken.None).GetAwaiter().GetResult();
        if (service.GetInstallationHealthAsync(translation.Id).GetAwaiter().GetResult() != InstallationHealth.Healthy)
            throw new Exception("Atualização/reinstalação do Lucius III não permaneceu íntegra.");
        File.WriteAllText(persistentNotebook, File.ReadAllText(persistentNotebook).Replace("<active>true</active>", "<active>false</active>").Replace("Antes", "Depois"), new UTF8Encoding(false));
        service.RemoveAsync(translation, null, CancellationToken.None).GetAwaiter().GetResult();
        var result = File.ReadAllText(persistentNotebook);
        if (!result.Contains("EN 359") || !result.Contains("<active>false</active>") || !result.Contains("Depois") || state.Installed.ContainsKey(translation.Id))
            throw new Exception("Desinstalação normal não reverteu somente os textos persistentes.");
        if (FileTools.Sha256Async(configSave).GetAwaiter().GetResult() != configHash || FileTools.Sha256Async(binarySave).GetAwaiter().GetResult() != binarySaveHash)
            throw new Exception("Instalação, atualização ou desinstalação modificou um arquivo .sav.");
    }

    private static string BuildNotebookXml(string prefix, string active, string character, string dialogue, int textCount = 360)
    {
        var builder = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><NotebookData version=\"320\"><Chapters><Chapter id=\"Test\"><active>");
        builder.Append(active).Append("</active><Entries>");
        for (var i = 0; i < textCount; i++) builder.Append("<Entry id=\"").Append(i).Append("\"><text>").Append(prefix).Append(' ').Append(i).Append("</text><progress>").Append(i).Append("</progress></Entry>");
        builder.Append("</Entries><nameOfCharacter>").Append(character).Append("</nameOfCharacter><dialogueLine>").Append(dialogue).Append("</dialogueLine></Chapter></Chapters></NotebookData>");
        return builder.ToString();
    }
}
