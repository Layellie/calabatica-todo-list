using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace CalabaticaSetup;

/// <summary>
/// .NET 10 Desktop Runtime (Microsoft.WindowsDesktop.App 10.x) kontrolü ve gerekirse kurulumu.
/// Uygulama framework-dependent olduğu için çalışmak için buna ihtiyaç duyar.
/// </summary>
public static class RuntimeInstaller
{
    public const int RequiredMajor = 10;

    /// <summary>Microsoft'un resmi "en son 10.0 yaması" yönlendirmesi (builds.dotnet.microsoft.com'a gider).</summary>
    private const string RuntimeUrl = "https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe";

    private const string SharedFxName = "Microsoft.WindowsDesktop.App";

    /// <summary>Kararlı (ön sürüm olmayan) 10.x Desktop Runtime kurulu mu?</summary>
    public static bool IsInstalled()
    {
        // 1) Dosya sistemi: %ProgramW6432%\dotnet\shared\Microsoft.WindowsDesktop.App\10.x.y
        foreach (string? root in new[] { Environment.GetEnvironmentVariable("ProgramW6432"), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
        {
            if (string.IsNullOrEmpty(root))
            {
                continue;
            }

            string dir = Path.Combine(root, "dotnet", "shared", SharedFxName);
            if (Directory.Exists(dir) && Directory.EnumerateDirectories(dir).Select(Path.GetFileName).Any(IsSupportedVersion))
            {
                return true;
            }
        }

        // 2) Kayıt defteri (Microsoft kurulumu buraya yazar)
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = baseKey.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\" + SharedFxName);
        return key?.GetValueNames().Any(IsSupportedVersion) == true;
    }

    /// <summary>"10.0.13" evet; "10.0.0-rc.2..." (ön sürüm) ve "9.0.x"/"11.0.x" hayır.</summary>
    private static bool IsSupportedVersion(string? name) =>
        name is not null
        && name.IndexOf('-') < 0
        && Version.TryParse(name, out var version)
        && version.Major == RequiredMajor;

    /// <summary>
    /// İndir → Microsoft imzasını doğrula → Microsoft kurulumunu (ilerleme penceresiyle) çalıştır.
    /// Yönetici onayı (UAC) Microsoft kurulumunun kendisi tarafından istenir; iptal edilirse Win32Exception fırlar.
    /// </summary>
    public static async Task InstallAsync(Action<string> report, Action<int> downloadProgress)
    {
        ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
        string path = Path.Combine(Path.GetTempPath(), $"calabatica-windowsdesktop-runtime-{Guid.NewGuid():N}.exe");

        try
        {
            report("runtime-download");
            using (var client = new WebClient())
            {
                client.Headers.Add(HttpRequestHeader.UserAgent, "CALABATICA-Setup");
                client.DownloadProgressChanged += (_, e) => downloadProgress(e.ProgressPercentage);
                await client.DownloadFileTaskAsync(new Uri(RuntimeUrl), path).ConfigureAwait(false);
            }

            report("runtime-verify");
            VerifyMicrosoftSignature(path);

            report("runtime-install");
            var start = new ProcessStartInfo(path, "/install /passive /norestart") { UseShellExecute = true };
            using var process = Process.Start(start) ?? throw new InvalidOperationException(".NET kurulumu başlatılamadı.");
            await Task.Run(process.WaitForExit).ConfigureAwait(false);

            // 0 = başarılı, 3010 = başarılı (yeniden başlatma önerilir), 1638 = bu/daha yeni sürüm zaten kurulu
            if (process.ExitCode is not (0 or 3010 or 1638))
            {
                throw new InvalidOperationException($".NET 10 Desktop Runtime kurulamadı (çıkış kodu {process.ExitCode}).");
            }

            if (!IsInstalled())
            {
                throw new InvalidOperationException(".NET 10 Desktop Runtime kurulumdan sonra bulunamadı.");
            }
        }
        finally
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
                // Bilinçli: geçici dosya kilitliyse Windows temizler; kurulum sonucunu etkilemez.
            }
        }
    }

    /// <summary>
    /// İndirilen exe çalıştırılmadan önce:
    ///   1) WinVerifyTrust: Authenticode imzası bozulmamış ve güvenilir köke zincirli (Explorer'ın yaptığı kontrol),
    ///   2) İmzalayan sertifika "O=Microsoft Corporation".
    /// (PowerShell'e devretmek kırılgandı: pwsh 7'den başlatılınca Windows PowerShell modülü yüklenemiyor.)
    /// </summary>
    public static void VerifyMicrosoftSignature(string path)
    {
        int result = WinTrust.Verify(path);
        if (result != 0)
        {
            throw new InvalidOperationException($"İndirilen .NET kurulumunun imzası geçersiz, çalıştırılmadı (WinVerifyTrust 0x{result:X8}).");
        }

        string subject;
        try
        {
            subject = new X509Certificate2(X509Certificate.CreateFromSignedFile(path)).Subject;
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("İndirilen .NET kurulumunun imzası okunamadı, çalıştırılmadı.", ex);
        }

        if (subject.IndexOf("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) < 0)
        {
            throw new InvalidOperationException($"İndirilen dosya Microsoft tarafından imzalanmamış, çalıştırılmadı ({subject}).");
        }
    }

    /// <summary>wintrust.dll WinVerifyTrust (WINTRUST_ACTION_GENERIC_VERIFY_V2), arayüzsüz.</summary>
    private static class WinTrust
    {
        private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

        private const uint WtdUiNone = 2;
        private const uint WtdRevokeNone = 0;
        private const uint WtdChoiceFile = 1;
        private const uint WtdStateActionIgnore = 0;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct FileInfo
        {
            public uint CbStruct;
            [MarshalAs(UnmanagedType.LPWStr)] public string FilePath;
            public IntPtr HFile;
            public IntPtr KnownSubject;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrustData
        {
            public uint CbStruct;
            public IntPtr PolicyCallbackData;
            public IntPtr SipClientData;
            public uint UiChoice;
            public uint RevocationChecks;
            public uint UnionChoice;
            public IntPtr File;
            public uint StateAction;
            public IntPtr StateData;
            public IntPtr UrlReference;
            public uint ProvFlags;
            public uint UiContext;
            public IntPtr SignatureSettings;
        }

        [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid actionId, IntPtr data);

        /// <returns>0 = güvenilir; diğerleri HRESULT (ör. 0x800B0100 imza yok).</returns>
        public static int Verify(string path)
        {
            var file = new FileInfo { CbStruct = (uint)Marshal.SizeOf<FileInfo>(), FilePath = path };
            IntPtr filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<FileInfo>());
            IntPtr dataPtr = Marshal.AllocHGlobal(Marshal.SizeOf<TrustData>());
            try
            {
                Marshal.StructureToPtr(file, filePtr, false);
                var data = new TrustData
                {
                    CbStruct = (uint)Marshal.SizeOf<TrustData>(),
                    UiChoice = WtdUiNone,
                    RevocationChecks = WtdRevokeNone,
                    UnionChoice = WtdChoiceFile,
                    File = filePtr,
                    StateAction = WtdStateActionIgnore,
                };
                Marshal.StructureToPtr(data, dataPtr, false);
                return WinVerifyTrust(IntPtr.Zero, GenericVerifyV2, dataPtr);
            }
            finally
            {
                Marshal.DestroyStructure<FileInfo>(filePtr);
                Marshal.FreeHGlobal(filePtr);
                Marshal.FreeHGlobal(dataPtr);
            }
        }
    }
}
