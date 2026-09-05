using System.Text.Json;

namespace BrPatchHub;

public sealed class Storage
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public string Root { get; }
    public string TempRoot => Path.Combine(Root, "temp");
    public string BackupRoot => Path.Combine(Root, "backups");
    public string PackageRoot => Path.Combine(Root, "packages");
    public string ConfigPath => Path.Combine(Root, "config.json");
    public string CatalogCachePath => Path.Combine(Root, "catalog-cache.json");
    public string InstalledPath => Path.Combine(Root, "installed.json");

    public AppConfig Config { get; private set; } = new();
    public Dictionary<string, InstalledTranslation> Installed { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public Storage(string? rootOverride = null)
    {
        Root = rootOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppConstants.AppName);
        if (rootOverride is null) MigrateLegacyData();
        Directory.CreateDirectory(TempRoot);
        Directory.CreateDirectory(BackupRoot);
        Directory.CreateDirectory(PackageRoot);
        Config = Read<AppConfig>(ConfigPath) ?? new AppConfig();
        Installed = Read<Dictionary<string, InstalledTranslation>>(InstalledPath) ?? new(StringComparer.OrdinalIgnoreCase);
        SaveConfig();
        SaveInstalled();
    }

    private void MigrateLegacyData()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var legacyName in new[] { "Central PT-BR", "TradutorHub" })
        {
            var legacy = Path.Combine(localData, legacyName);
            if (Directory.Exists(legacy) && !Directory.Exists(Root))
            {
                Directory.Move(legacy, Root);
                break;
            }
        }
        Directory.CreateDirectory(Root);
    }

    public T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
    }

    public void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(temporary, path, true);
    }

    public void SaveConfig() => Write(ConfigPath, Config);
    public void SaveInstalled() => Write(InstalledPath, Installed);
    public Catalog? LoadCatalogCache() => Read<Catalog>(CatalogCachePath);
    public void SaveCatalog(Catalog catalog) => Write(CatalogCachePath, catalog);
}
