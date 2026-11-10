using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using CyberTodo.Services;

namespace CalabaticaTests;

/// <summary>
/// <c>verify-package</c>: yayın çıktılarını (setup + güncelleme zip'i) üretildikleri hâliyle, gerçek
/// <see cref="UpdateInstaller"/> koduyla doğrular. CI yayın provasında ve release.yml'de çalışır.
/// Kullanım: verify-package --dir &lt;zip ve .sha256 klasörü&gt; --setup &lt;setup.exe&gt; --version &lt;1.2.3&gt; [--max-setup-mb 2]
/// Çıkış kodu = başarısız denetim sayısı (2 = hatalı kullanım).
/// </summary>
internal static class PackageVerification
{
    public static async Task<int> RunAsync(string[] args, TextWriter? output = null)
    {
        var o = output ?? Console.Out;
        string? Arg(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
        string? dir = Arg("--dir"), setup = Arg("--setup"), versionText = Arg("--version");
        double maxSetupMb = double.TryParse(Arg("--max-setup-mb"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mb) ? mb : 2;

        if (dir is null || setup is null || !UpdateService.TryParseVersion(versionText, out var version))
        {
            o.WriteLine("usage: verify-package --dir <folder> --setup <setup.exe> --version <1.2.3> [--max-setup-mb 2]");
            return 2;
        }

        int failures = 0;
        void Check(bool ok, string name)
        {
            o.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
            if (!ok)
            {
                failures++;
            }
        }

        // ── setup.exe ──
        bool setupExists = File.Exists(setup);
        Check(setupExists, $"setup exists: {setup}");
        if (setupExists)
        {
            double size = new FileInfo(setup).Length / 1048576.0;
            Check(size <= maxSetupMb, $"setup is small enough ({size:F2} MB <= {maxSetupMb:F1} MB)");
            string sidecar = setup + ".sha256";
            bool hasSidecar = File.Exists(sidecar);
            Check(hasSidecar, "setup has a .sha256 file");
            if (hasSidecar)
            {
                string expected = ParseOrEmpty(await File.ReadAllTextAsync(sidecar));
                Check(expected == Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(setup))), "setup checksum file matches the setup");
            }
        }

        // ── güncelleme paketi: gerçek güncelleyici kodundan geçir ──
        string zipPath = Path.Combine(dir, UpdateService.AssetName);
        string shaPath = Path.Combine(dir, UpdateService.ChecksumAssetName);
        bool zipOk = File.Exists(zipPath) && File.Exists(shaPath);
        Check(zipOk, $"update package files use the names the updater expects ({UpdateService.AssetName}, {UpdateService.ChecksumAssetName})");
        if (!zipOk)
        {
            return failures;
        }

        string sandbox = Path.Combine(Path.GetTempPath(), "calabatica-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Kurulu kopyayı taklit et: setup'ın bıraktığı Uninstall.exe + eski bir CALABATICA.exe.
            Directory.CreateDirectory(sandbox);
            string installed = Path.Combine(sandbox, UpdateInstaller.ExeName);
            await File.WriteAllTextAsync(installed, "previous build");
            await File.WriteAllTextAsync(Path.Combine(sandbox, "Uninstall.exe"), "marker");

            var assetUri = new Uri("https://github.com/o/r/releases/download/v" + version + "/" + UpdateService.AssetName);
            var shaUri = new Uri(assetUri + ".sha256");
            var http = new FakeHttp { [assetUri] = await File.ReadAllBytesAsync(zipPath), [shaUri] = await File.ReadAllBytesAsync(shaPath) };
            var installer = new UpdateInstaller(installed, http);
            var release = new ReleaseInfo(version, "v" + version, new Uri("https://github.com/o/r/releases/tag/v" + version), assetUri, shaUri, null);

            Check(installer.CanSelfUpdate, "sandbox looks like an installed copy");
            string? staged = null;
            string failure = string.Empty;
            try
            {
                staged = await installer.DownloadAndStageAsync(release);
            }
            catch (InvalidDataException ex)
            {
                failure = ex.Message;
            }

            Check(staged is not null, "updater accepts the package (checksum, single CALABATICA.exe, product name, version): " + (staged is null ? failure : "ok"));
            if (staged is not null)
            {
                var info = FileVersionInfo.GetVersionInfo(staged);
                Check(info.ProductName == UpdateInstaller.ProductName && UpdateService.TryParseVersion(info.ProductVersion?.Split('+')[0], out var actual) && actual == version,
                    $"staged exe is CALABATICA {version} (found {info.ProductName} {info.ProductVersion})");
                byte[] stagedBytes = await File.ReadAllBytesAsync(staged);
                installer.Swap(staged);
                Check(stagedBytes.Length > 0 && (await File.ReadAllBytesAsync(installed)).AsSpan().SequenceEqual(stagedBytes)
                    && await File.ReadAllTextAsync(installed + ".old") == "previous build",
                    "swap puts the verified exe in place and keeps the previous one as .old");
            }
        }
        finally
        {
            try
            {
                Directory.Delete(sandbox, recursive: true);
            }
            catch (IOException)
            {
                // Bilinçli: geçici klasör temizliği; bir sonraki temp temizliğinde silinir.
            }
        }

        o.WriteLine(failures == 0 ? "PACKAGE OK" : $"{failures} CHECK(S) FAILED");
        return failures;
    }

    private static string ParseOrEmpty(string text)
    {
        try
        {
            return UpdateInstaller.ParseChecksum(text);
        }
        catch (InvalidDataException)
        {
            return string.Empty;
        }
    }
}
