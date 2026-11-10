using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace CyberTodo.Services;

/// <summary>
/// Otomatik güncelleme kurulumu:
///  1) Yayındaki CALABATICA-win-x64.zip ve .sha256 indirilir (yalnızca https, github.com / *.githubusercontent.com),
///  2) SHA256 doğrulanır (bozuk/yarım indirme kurulmaz),
///  3) zip'ten yalnızca "CALABATICA.exe" sabit bir hedefe çıkarılır (yol enjeksiyonu yok), sürüm bilgisi kontrol edilir,
///  4) çalışan exe yeniden adlandırılır (Windows çalışan exe'nin adını değiştirmeye izin verir), yenisi yerine konur,
///  5) uygulama yeniden başlatılır; eski dosya (.old) bir sonraki açılışta silinir.
/// Not: SHA256 aynı yayından geldiği için bütünlüğü korur, yayıncı kimliğini değil (exe imzalı değil).
/// </summary>
public sealed partial class UpdateInstaller
{
    public const string ExeName = "CALABATICA.exe";
    public const string ProductName = "CALABATICA";
    private const string InstalledMarker = "Uninstall.exe"; // setup'ın kurduğu kopyada bulunur
    private const long MaxPackageBytes = 100L * 1024 * 1024;
    private const long MaxExeBytes = 200L * 1024 * 1024;
    private const int MaxChecksumBytes = 4096;

    private readonly HttpClient _http;

    /// <param name="exePath">Güncellenecek exe (varsayılan: çalışan süreç).</param>
    /// <param name="handler">Test için sahte HTTP.</param>
    public UpdateInstaller(string? exePath = null, HttpMessageHandler? handler = null)
    {
        ExePath = exePath ?? Environment.ProcessPath;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("CALABATICA-Updater", "1.0"));
    }

    public string? ExePath { get; }

    /// <summary>
    /// Kendini güncelleyebilir mi: setup ile kurulmuş (yanında Uninstall.exe var) ve adı CALABATICA.exe.
    /// Geliştirme derlemesi (bin\...) ya da "dotnet run" güncellenmez; kullanıcıya sayfa açma seçeneği kalır.
    /// </summary>
    public bool CanSelfUpdate =>
        ExePath is { } path
        && string.Equals(Path.GetFileName(path), ExeName, StringComparison.OrdinalIgnoreCase)
        && File.Exists(Path.Combine(Path.GetDirectoryName(path)!, InstalledMarker));

    /// <summary>İndirir, doğrular ve yeni exe'yi "CALABATICA.exe.new" olarak hazırlar. Hazırlanan yolu döndürür.</summary>
    /// <exception cref="InvalidDataException">Sağlama/paket/sürüm uyuşmazlığı.</exception>
    public async Task<string> DownloadAndStageAsync(ReleaseInfo release, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!CanSelfUpdate)
        {
            throw new InvalidOperationException("Not an installed copy.");
        }

        if (release.AssetUrl is not { } assetUrl || release.ChecksumUrl is not { } checksumUrl)
        {
            throw new InvalidDataException("Release has no update package.");
        }

        string directory = Path.GetDirectoryName(ExePath!)!;
        string zipPath = Path.Combine(directory, "CALABATICA.update.zip");
        string stagedPath = ExePath + ".new";

        try
        {
            string expected = await DownloadChecksumAsync(checksumUrl, cancellationToken).ConfigureAwait(false);
            string actual = await DownloadFileAsync(assetUrl, zipPath, progress, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SHA256 mismatch.");
            }

            ExtractExe(zipPath, stagedPath);
            VerifyVersion(stagedPath, release.Version);
            return stagedPath;
        }
        catch
        {
            TryDelete(stagedPath); // yarım/geçersiz dosya kalmasın; hata çağırana iletilir
            throw;
        }
        finally
        {
            TryDelete(zipPath);
        }
    }

    /// <summary>Çalışan exe'yi .old yapar, hazırlananı yerine koyar. İkinci adım başarısızsa eskisini geri koyar.</summary>
    public void Swap(string stagedPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedPath);
        string exe = ExePath ?? throw new InvalidOperationException("No executable path.");
        string old = exe + ".old";

        File.Delete(old); // yoksa sorun değil
        File.Move(exe, old);
        try
        {
            File.Move(stagedPath, exe);
        }
        catch
        {
            File.Move(old, exe); // uygulama çalışır durumda kalsın
            throw;
        }
    }

    /// <summary>
    /// "Programlar ve Özellikler"deki sürümü günceller. Yalnızca setup'ın yazdığı kayıt bu exe'nin klasörünü
    /// gösteriyorsa dokunur (başka kopya / test klasörü kayda yazmaz).
    /// </summary>
    public void RecordInstalledVersion(Version version)
    {
        ArgumentNullException.ThrowIfNull(version);
        if (!CanSelfUpdate)
        {
            return;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(UninstallKeyPath, writable: true);
            if (key?.GetValue("InstallLocation") is string location
                && string.Equals(Path.GetFullPath(location).TrimEnd('\\'), Path.GetDirectoryName(ExePath!)!.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                key.SetValue("DisplayVersion", $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}");
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Bilinçli: yalnızca listede görünen sürüm etiketi; güncelleme yine de geçerli. Bir sonraki setup düzeltir.
        }
    }

    private const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\CALABATICA";

    /// <summary>Önceki güncellemeden kalan dosyalar (açılışta). Silinemezse (ör. eski süreç hâlâ kapanıyor) bir sonraki açılışa kalır.</summary>
    public static void CleanupLeftovers(string? exePath)
    {
        if (exePath is null)
        {
            return;
        }

        TryDelete(exePath + ".old");
        TryDelete(exePath + ".new");
        TryDelete(Path.Combine(Path.GetDirectoryName(exePath) ?? string.Empty, "CALABATICA.update.zip"));
    }

    /// <summary>İndirmeye yalnızca GitHub'ın kendi alanlarında izin (yönlendirme sonrası adres de denetlenir).</summary>
    public static bool IsAllowedDownloadUri(Uri? uri) =>
        uri is not null
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>"abc123…  dosya.zip" ya da yalnızca hash → 64 haneli küçük harf hex.</summary>
    public static string ParseChecksum(string text)
    {
        var match = ChecksumRegex().Match(text ?? string.Empty);
        if (!match.Success)
        {
            throw new InvalidDataException("Invalid checksum file.");
        }

        return match.Groups[1].Value.ToLowerInvariant();
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (!IsAllowedDownloadUri(uri))
        {
            throw new InvalidDataException($"Download host not allowed: {uri.Host}");
        }

        var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        try
        {
            response.EnsureSuccessStatusCode();
            if (!IsAllowedDownloadUri(response.RequestMessage?.RequestUri))
            {
                throw new InvalidDataException($"Redirected to a host that is not allowed: {response.RequestMessage?.RequestUri?.Host}");
            }

            return response;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private async Task<string> DownloadChecksumAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(uri, cancellationToken).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[MaxChecksumBytes];
        int total = 0;
        int read;
        while (total < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
        }

        return ParseChecksum(System.Text.Encoding.ASCII.GetString(buffer, 0, total));
    }

    /// <returns>İndirilen dosyanın SHA256'sı (küçük harf hex).</returns>
    private async Task<string> DownloadFileAsync(Uri uri, string path, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        using var response = await GetAsync(uri, cancellationToken).ConfigureAwait(false);
        long? length = response.Content.Headers.ContentLength;
        if (length > MaxPackageBytes)
        {
            throw new InvalidDataException("Update package too large.");
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long total = 0;
            int lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > MaxPackageBytes)
                {
                    throw new InvalidDataException("Update package too large.");
                }

                sha.AppendData(buffer, 0, read);
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);

                if (length is > 0 && progress is not null)
                {
                    int percent = (int)(total * 100 / length.Value);
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(percent);
                    }
                }
            }
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }

    /// <summary>Paket tam olarak tek giriş içermeli: "CALABATICA.exe" (klasörsüz). Başka her şey reddedilir.</summary>
    private static void ExtractExe(string zipPath, string targetPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var files = archive.Entries.Where(static e => e.FullName.Length > 0 && !e.FullName.EndsWith('/')).ToList();
        if (files.Count != 1 || !string.Equals(files[0].FullName, ExeName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Unexpected update package content.");
        }

        var entry = files[0];
        if (entry.Length <= 0 || entry.Length > MaxExeBytes)
        {
            throw new InvalidDataException("Unexpected executable size.");
        }

        using var source = entry.Open();
        using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        source.CopyTo(target);
        if (target.Length != entry.Length)
        {
            throw new InvalidDataException("Executable extraction incomplete.");
        }
    }

    /// <summary>Çıkarılan dosya gerçekten bu uygulama ve beklenen sürüm mü (yanlış paket kurulmasın).</summary>
    private static void VerifyVersion(string path, Version expected)
    {
        var info = FileVersionInfo.GetVersionInfo(path);
        if (!string.Equals(info.ProductName, ProductName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Package is not CALABATICA.");
        }

        string? product = info.ProductVersion?.Split('+')[0];
        if (!UpdateService.TryParseVersion(product, out var actual) || actual != expected)
        {
            throw new InvalidDataException($"Version mismatch: expected {expected}, got {product}.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Bilinçli: geçici dosya temizliği; kalan dosya bir sonraki açılışta CleanupLeftovers ile silinir.
        }
    }

    [GeneratedRegex(@"^\s*([0-9a-fA-F]{64})\b")]
    private static partial Regex ChecksumRegex();
}
