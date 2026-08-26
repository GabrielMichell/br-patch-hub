using System.Text;
using System.Text.Json;
using System.IO.Compression;

namespace CentralPtBr;

public static class SelfTest
{
    public static int Render(string output, int width = 1520, int height = 940)
    {
        var root = Path.Combine(Path.GetTempPath(), $"central-ptbr-render-{Guid.NewGuid():N}");
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
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "central-ptbr-render-error.txt"), ex.ToString());
            return 1;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    public static int Run()
    {
        var root = Path.Combine(Path.GetTempPath(), $"central-ptbr-test-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var safe = FileTools.ResolveInside(root, "dados/arquivo.txt");
            if (!safe.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new Exception("Falha na resolução segura de caminhos.");
            try { FileTools.ResolveInside(root, "../fora.txt"); throw new Exception("Path traversal não foi bloqueado."); }
            catch (InvalidDataException) { }

            var config = Path.Combine(root, "config.sav");
            var oldValue = Encoding.UTF8.GetBytes("Português");
            var bytes = new byte[oldValue.Length + 5];
            bytes[0] = 1; bytes[1] = (byte)oldValue.Length; Array.Copy(oldValue, 0, bytes, 2, oldValue.Length); bytes[^2] = 2; bytes[^1] = 3;
            File.WriteAllBytes(config, bytes);
            if (!FileTools.RepairLengthPrefixedUtf8(config, "Português", "English")) throw new Exception("Correção de idioma não executada.");
            if (!Encoding.UTF8.GetString(File.ReadAllBytes(config)).Contains("English")) throw new Exception("Correção de idioma inválida.");

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
            var removal = service.RemoveAsync(translation, null, CancellationToken.None).GetAwaiter().GetResult();
            if (removal.RequiresSteamRestore || File.ReadAllText(gameFile) != "original") throw new Exception("Restauração temporária falhou.");

            var updateSource = Path.Combine(root, "update-source.exe");
            var updateTarget = Path.Combine(root, "installed", "Central PT-BR.exe");
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
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "central-ptbr-self-test-error.txt"), ex.ToString());
            return 1;
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
