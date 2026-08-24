using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Security.Cryptography;

namespace CentralPtBr;

public sealed class OnlineServices
{
    private readonly Storage _storage;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public OnlineServices(Storage storage)
    {
        _storage = storage;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Central-PT-BR/{AppConstants.AppVersion}");
    }

    public async Task<Catalog> RefreshCatalogAsync(CancellationToken cancellationToken = default)
    {
        var catalog = await _http.GetFromJsonAsync<Catalog>(_storage.Config.CatalogUrl, JsonOptions, cancellationToken) ?? throw new InvalidDataException("O catálogo retornou vazio.");
        if (catalog.SchemaVersion < 1) throw new InvalidDataException("O catálogo possui uma versão de estrutura incompatível.");
        _storage.Config.LastCatalogCheck = DateTimeOffset.UtcNow;
        _storage.SaveConfig();
        _storage.SaveCatalog(catalog);
        return catalog;
    }

    public async Task<AppVersionManifest?> CheckAppUpdateAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var manifest = await _http.GetFromJsonAsync<AppVersionManifest>(AppConstants.VersionUrl, JsonOptions, cancellationToken);
            if (manifest is null || !Version.TryParse(manifest.Version, out var latest) || !Version.TryParse(AppConstants.AppVersion, out var current)) return null;
            return latest > current ? manifest : null;
        }
        catch { return null; }
    }

    public static void OpenUpdate(AppVersionManifest manifest)
    {
        var url = !string.IsNullOrWhiteSpace(manifest.DownloadUrl) ? manifest.DownloadUrl : manifest.ReleaseUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("A atualização não possui um endereço válido do GitHub.");
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public async Task<string> DownloadUpdateAsync(AppVersionManifest manifest, string destinationRoot, IProgress<ProgressInfo>? progress, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(manifest.DownloadUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A atualização não possui um download direto válido do GitHub.");
        if (manifest.Sha256.Length != 64 || !manifest.Sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("A atualização não possui um SHA-256 válido.");
        var directory = Path.Combine(destinationRoot, "updates", manifest.Version);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "Central PT-BR.exe");
        var temporary = target + ".download";
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true))
        {
            var buffer = new byte[1024 * 1024];
            long downloaded = 0;
            while (true)
            {
                var count = await input.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                downloaded += count;
                progress?.Report(new ProgressInfo($"Baixando atualização — {downloaded / (1024d * 1024):0.0} MB", total.HasValue ? (int)(downloaded * 100 / total.Value) : null));
            }
        }
        var hash = await FileTools.Sha256Async(temporary, cancellationToken);
        if (!hash.Equals(manifest.Sha256, StringComparison.OrdinalIgnoreCase)) { File.Delete(temporary); throw new InvalidDataException("A atualização baixada falhou na validação SHA-256."); }
        File.Move(temporary, target, true);
        return target;
    }
}
