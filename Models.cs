using System.Text.Json.Serialization;

namespace CentralPtBr;

public sealed class Catalog
{
    public int SchemaVersion { get; set; }
    public string CatalogName { get; set; } = "";
    public DateTimeOffset? UpdatedAt { get; set; }
    public List<Translation> Translations { get; set; } = [];
}

public sealed class Translation
{
    public string Id { get; set; } = "";
    public string Game { get; set; } = "";
    public string Language { get; set; } = "pt-BR";
    public string Version { get; set; } = "";
    public string PackageType { get; set; } = "zip";
    public string Description { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public string SteamAppId { get; set; } = "";
    public List<string> FolderHints { get; set; } = [];
    public List<string> ExecutableHints { get; set; } = [];
    public List<InstallRule> Install { get; set; } = [];
    public List<InstallOperation> Operations { get; set; } = [];
    public List<PackageAsset> Assets { get; set; } = [];
    public List<CleanupFile> SteamCleanup { get; set; } = [];
    public LanguagePreferenceRepair? LanguagePreferenceRepair { get; set; }

    [JsonIgnore] public string DisplayName => $"{Game} | {Language} | v{Version}";
    public override string ToString() => DisplayName;
}

public sealed class InstallRule { public string From { get; set; } = "."; public string To { get; set; } = "game"; }
public sealed class InstallOperation
{
    public string Type { get; set; } = "copy";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public int Alignment { get; set; } = 1;
    public long ExpectedSize { get; set; }
}
public sealed class PackageAsset { public string Role { get; set; } = "package"; public string FileName { get; set; } = ""; public string DownloadUrl { get; set; } = ""; public string Sha256 { get; set; } = ""; }
public sealed class CleanupFile { public string Path { get; set; } = ""; public string Sha256 { get; set; } = ""; }
public sealed class LanguagePreferenceRepair { public string Path { get; set; } = ""; public string From { get; set; } = ""; public string To { get; set; } = ""; }

public sealed class AppConfig
{
    public string CatalogUrl { get; set; } = AppConstants.CatalogUrl;
    public Dictionary<string, string> GamePaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public DateTimeOffset? LastCatalogCheck { get; set; }
}

public sealed class InstalledTranslation
{
    public string Id { get; set; } = "";
    public string Game { get; set; } = "";
    public string Version { get; set; } = "";
    public string GamePath { get; set; } = "";
    public string BackupRoot { get; set; } = "";
    public string PackageType { get; set; } = "zip";
    public string SteamAppId { get; set; } = "";
    public DateTimeOffset InstalledAt { get; set; }
    public List<InstalledFile> Files { get; set; } = [];
}

public sealed class InstalledFile
{
    public string RelativePath { get; set; } = "";
    public string InstalledHash { get; set; } = "";
    public long InstalledSize { get; set; }
    public string? BackupPath { get; set; }
    public string RestoreMethod { get; set; } = "";
}

public sealed class AppVersionManifest { public string Version { get; set; } = ""; public string ReleaseUrl { get; set; } = ""; public string DownloadUrl { get; set; } = ""; public string Sha256 { get; set; } = ""; public string Notes { get; set; } = ""; }
public sealed record ProgressInfo(string Message, int? Percent = null);
public sealed record RemovalResult(bool RequiresSteamRestore, int CleanedFiles, bool LanguageRepaired);

public static class AppConstants
{
    public const string AppName = "Central PT-BR";
    public const string AppVersion = "2.0.3";
    public const string OfficialRepositoryUrl = "https://github.com/GabrielMichell/central-pt-br";
    public const string CatalogUrl = "https://raw.githubusercontent.com/GabrielMichell/central-pt-br/main/catalog.json";
    public const string VersionUrl = "https://raw.githubusercontent.com/GabrielMichell/central-pt-br/main/app-version.json";
}
