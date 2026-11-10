using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberTodo.Services;

public enum UpdateCheckStatus
{
    NotConfigured,
    UpToDate,
    UpdateAvailable,
    Failed,
}

/// <summary>GitHub'daki en son yayın. AssetUrl/ChecksumUrl ileride otomatik kurulum için kullanılacak.</summary>
public sealed record ReleaseInfo(Version Version, string Tag, Uri PageUrl, Uri? AssetUrl, Uri? ChecksumUrl, DateTimeOffset? PublishedAt);

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Latest, string? Error);

/// <summary>
/// GitHub Releases üzerinden sürüm denetimi (sadece okuma; indirme/kurulum henüz YOK).
/// Depo derleme anında exe'ye gömülür (AssemblyMetadata "GitHubRepository"); boşsa denetim kapalıdır.
/// </summary>
public sealed partial class UpdateService
{
    /// <summary>Yayın akışının ürettiği paket adı (otomatik güncelleme bu dosyayı indirecek).</summary>
    public const string AssetName = "CALABATICA-win-x64.zip";
    public const string ChecksumAssetName = AssetName + ".sha256";

    private static readonly HttpClient s_http = CreateClient();

    private readonly Func<CancellationToken, Task<string>>? _fetchOverride;

    public UpdateService()
        : this(ReadConfiguredRepository(), ReadCurrentVersion())
    {
    }

    /// <param name="fetchOverride">Test için: ağ yerine sabit JSON döndürür.</param>
    public UpdateService(string? repository, Version currentVersion, Func<CancellationToken, Task<string>>? fetchOverride = null)
    {
        CurrentVersion = currentVersion ?? throw new ArgumentNullException(nameof(currentVersion));
        Repository = IsValidRepository(repository) ? repository : null;
        _fetchOverride = fetchOverride;
    }

    /// <summary>"kullanici/depo" ya da null (geçersiz/boş).</summary>
    public string? Repository { get; }

    public Version CurrentVersion { get; }

    public bool IsConfigured => Repository is not null;

    /// <summary>"v2.0" / "v2.1.3" — yama 0 ise gösterilmez.</summary>
    public static string FormatVersion(Version version) =>
        version.Build > 0 ? $"v{version.Major}.{version.Minor}.{version.Build}" : $"v{version.Major}.{version.Minor}";

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new UpdateCheckResult(UpdateCheckStatus.NotConfigured, null, null);
        }

        try
        {
            string json = _fetchOverride is not null
                ? await _fetchOverride(cancellationToken).ConfigureAwait(false)
                : await s_http.GetStringAsync($"https://api.github.com/repos/{Repository}/releases/latest", cancellationToken).ConfigureAwait(false);

            var latest = ParseRelease(json);
            if (latest is null)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, null, "Yayın bilgisi okunamadı.");
            }

            var status = latest.Version > CurrentVersion ? UpdateCheckStatus.UpdateAvailable : UpdateCheckStatus.UpToDate;
            return new UpdateCheckResult(status, latest, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Ağ yok / zaman aşımı / depo yok (404) / hız sınırı (403): kullanıcıya gösterilir, uygulama etkilenmez.
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null, ex.Message);
        }
    }

    /// <summary>GitHub "latest release" JSON'unu çözer. Taslak/ön sürüm ve güvensiz URL'ler reddedilir.</summary>
    public static ReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        if (root.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True)
        {
            return null;
        }

        string? tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (tag is null || !TryParseVersion(tag, out var version))
        {
            return null;
        }

        // Sayfa yalnızca https://github.com/... olabilir (tarayıcıda açılacak).
        if (!root.TryGetProperty("html_url", out var html)
            || !Uri.TryCreate(html.GetString(), UriKind.Absolute, out var pageUrl)
            || !IsGitHubUrl(pageUrl))
        {
            return null;
        }

        Uri? assetUrl = null, checksumUrl = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (name is null || !Uri.TryCreate(url, UriKind.Absolute, out var parsed) || !IsGitHubUrl(parsed))
                {
                    continue;
                }

                if (name == AssetName)
                {
                    assetUrl = parsed;
                }
                else if (name == ChecksumAssetName)
                {
                    checksumUrl = parsed;
                }
            }
        }

        DateTimeOffset? published = root.TryGetProperty("published_at", out var p) && p.TryGetDateTimeOffset(out var dto) ? dto : null;
        return new ReleaseInfo(version, tag, pageUrl, assetUrl, checksumUrl, published);
    }

    /// <summary>"v2.1.0", "2.1", "V3" → Version (eksik kısımlar 0). Ön sürüm eki ("-beta") kabul edilmez.</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim().TrimStart('v', 'V');
        var match = VersionRegex().Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        int major = int.Parse(match.Groups[1].Value);
        int minor = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        int patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        version = new Version(major, minor, patch);
        return true;
    }

    public static Version ReadCurrentVersion()
    {
        var assembly = Assembly.GetEntryAssembly() ?? typeof(UpdateService).Assembly;
        string? informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (TryParseVersion(informational?.Split('+')[0], out var version))
        {
            return version;
        }

        var v = assembly.GetName().Version ?? new Version(0, 0, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public static string? ReadConfiguredRepository() =>
        typeof(UpdateService).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(static a => a.Key == "GitHubRepository")?.Value;

    /// <summary>URL'ye eklenecek değer: sadece "kullanici/depo" biçimine izin (yol/sorgu enjeksiyonu yok).</summary>
    public static bool IsValidRepository(string? repository) =>
        !string.IsNullOrWhiteSpace(repository)
        && RepositoryRegex().IsMatch(repository)
        && repository.Split('/')[1].Trim('.').Length > 0; // "kullanici/.." ile URL yolu kaydırılamaz

    private static bool IsGitHubUrl(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // GitHub API User-Agent ister; kişisel bilgi içermeyen genel bir ad.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CALABATICA-Updater", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    [GeneratedRegex(@"^(\d{1,5})(?:\.(\d{1,5}))?(?:\.(\d{1,5}))?$")]
    private static partial Regex VersionRegex();

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepositoryRegex();
}
