using System.IO;
using System.Security.Cryptography;
using CyberTodo.Services;

namespace CalabaticaTests;

/// <summary>Yayın doğrulayıcısının kendisinin testi: iyi paketi geçirir, bozuk her paketi reddeder (yoksa sahte güven verir).</summary>
internal static class PackageVerificationTests
{
    public static async Task Run(TestContext ctx)
    {
        string realExe = Path.Combine(AppContext.BaseDirectory, "CALABATICA.exe"); // ürün adı/sürümü gömülü gerçek exe (1.0.0)
        string root = Path.Combine(ctx.Dir, "verify");
        string setup = Path.Combine(root, "CALABATICA-Setup-1.0.0.exe");

        async Task<(string Dir, int Exit, string Output)> Verify(string name, Func<string, Task>? tamper = null, byte[]? zipBytes = null, string version = "1.0.0")
        {
            string dir = Path.Combine(root, name);
            Directory.CreateDirectory(dir);
            byte[] zip = zipBytes ?? TestSupport.MakeZip(("CALABATICA.exe", await File.ReadAllBytesAsync(realExe)));
            await File.WriteAllBytesAsync(Path.Combine(dir, UpdateService.AssetName), zip);
            await File.WriteAllTextAsync(Path.Combine(dir, UpdateService.ChecksumAssetName),
                Convert.ToHexStringLower(SHA256.HashData(zip)) + "  " + UpdateService.AssetName + "\n");
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(setup, new byte[300 * 1024]);
            await File.WriteAllTextAsync(setup + ".sha256", Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(setup))) + "  setup.exe\n");
            if (tamper is not null)
            {
                await tamper(dir);
            }

            using var writer = new StringWriter();
            int exit = await PackageVerification.RunAsync(["--dir", dir, "--setup", setup, "--version", version], writer);
            return (dir, exit, writer.ToString());
        }

        var good = await Verify("good");
        ctx.Check(good.Exit == 0 && good.Output.Contains("PACKAGE OK"), "verify-package: a correct package passes: " + (good.Exit == 0 ? "ok" : good.Output));

        var badHash = await Verify("bad-hash", tamper: dir => File.WriteAllTextAsync(Path.Combine(dir, UpdateService.ChecksumAssetName), new string('0', 64) + "  x\n"));
        ctx.Check(badHash.Exit > 0 && badHash.Output.Contains("SHA256"), "verify-package: wrong checksum is rejected");

        var wrongVersion = await Verify("wrong-version", version: "9.9.9");
        ctx.Check(wrongVersion.Exit > 0 && wrongVersion.Output.Contains("Version mismatch"), "verify-package: version that does not match the exe is rejected");

        var extraEntry = await Verify("extra-entry", zipBytes: TestSupport.MakeZip(("CALABATICA.exe", await File.ReadAllBytesAsync(realExe)), ("readme.txt", [1])));
        ctx.Check(extraEntry.Exit > 0 && extraEntry.Output.Contains("Unexpected update package content"), "verify-package: zip with extra entries is rejected");

        var notApp = await Verify("not-the-app", zipBytes: TestSupport.MakeZip(("CALABATICA.exe", new byte[2048])));
        ctx.Check(notApp.Exit > 0, "verify-package: a CALABATICA.exe that is not this app is rejected");

        var missingZip = await Verify("missing-zip", tamper: dir => { File.Delete(Path.Combine(dir, UpdateService.AssetName)); return Task.CompletedTask; });
        ctx.Check(missingZip.Exit > 0 && missingZip.Output.Contains("names the updater expects"), "verify-package: renamed/missing package files are caught");

        var bigSetup = await Verify("big-setup", tamper: _ => File.WriteAllBytesAsync(setup, new byte[3 * 1024 * 1024]));
        ctx.Check(bigSetup.Exit > 0 && bigSetup.Output.Contains("small enough"), "verify-package: oversized setup is flagged");
        ctx.Check(await PackageVerification.RunAsync([]) == 2, "verify-package: missing arguments -> usage error (2)");
    }
}