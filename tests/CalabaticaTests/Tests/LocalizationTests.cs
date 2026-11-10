using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CyberTodo.Models;
using CyberTodo.Services;

namespace CalabaticaTests;

/// <summary>
/// Yerelleştirme anahtarlarının kullanım taraması: kaynak koddaki her anahtar kullanımı sözlükte var mı (eksik anahtar
/// ekranda "!anahtar!" olur) ve hangi anahtarlar hiç kullanılmıyor (ölü çeviri). Kaynak dosyalar bu dosyanın yolundan bulunur.
/// </summary>
internal static partial class LocalizationTests
{
    // Anahtar biçimi: Onek_Ad (ör. Tasks_Header, Err_NewerData).
    private const string KeyShape = "[A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)+";

    // Kesin kullanım kalıpları: bunlardaki anahtar sözlükte OLMAK zorunda.
    [GeneratedRegex("(?:_loc|Instance|loc|Localizer)\\[\\s*\"(" + KeyShape + ")\"\\s*\\]|\\.Format\\(\\s*\"(" + KeyShape + ")\"|\\{loc:Tr\\s+(" + KeyShape + ")\\s*\\}|Key\\s*=\\s*\"(" + KeyShape + ")\"")]
    private static partial Regex StrictUsage();

    // Gevşek: herhangi bir anahtar biçimli string (dolaylı kullanım: seçenek listeleri, yardımcılar). Kullanım sayılır.
    [GeneratedRegex("\"(" + KeyShape + ")\"")]
    private static partial Regex AnyLiteral();

    [GeneratedRegex("\\{loc:Tr\\s+(" + KeyShape + ")")]
    private static partial Regex XamlUsage();

    // $"Onb_{n}_Title" gibi dinamik anahtarlar: yalnızca sabit önek/sonek görülür.
    // ve "Habit_State_" + state gibi birleştirmeler.
    [GeneratedRegex("\\$\"(" + "[A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)*_" + ")\\{[^}]+\\}(_[A-Za-z0-9]+)?\"|\"([A-Z][A-Za-z0-9]*(?:_[A-Za-z0-9]+)*_)\"\\s*\\+")]
    private static partial Regex DynamicUsage();

    public static Task Run(TestContext ctx, [CallerFilePath] string thisFile = "")
    {
        string repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
        string appDir = Path.Combine(repoRoot, "CyberTodo");
        if (!Directory.Exists(appDir))
        {
            ctx.Check(false, $"l10n: app sources found at {appDir}");
            return Task.CompletedTask;
        }

        var files = Directory.EnumerateFiles(appDir, "*.*", SearchOption.AllDirectories)
            .Where(static f => (f.EndsWith(".cs", StringComparison.Ordinal) || f.EndsWith(".xaml", StringComparison.Ordinal))
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        string localizerFile = Path.Combine(appDir, "Services", "Localizer.cs");

        var tr = Localizer.Keys(AppLanguage.Turkish).ToHashSet(StringComparer.Ordinal);
        var en = Localizer.Keys(AppLanguage.English).ToHashSet(StringComparer.Ordinal);
        ctx.Check(files.Count > 20 && tr.Count > 100, $"l10n: scanner sees the sources ({files.Count} files) and dictionaries ({tr.Count} keys)");

        var strictMissing = new SortedSet<string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var dynamicParts = new List<(string Prefix, string? Suffix)>();
        foreach (string file in files)
        {
            string text = File.ReadAllText(file);
            bool isLocalizer = string.Equals(file, localizerFile, StringComparison.OrdinalIgnoreCase);
            if (!isLocalizer)
            {
                foreach (Match m in StrictUsage().Matches(text))
                {
                    string key = m.Groups.Cast<Group>().Skip(1).First(static g => g.Success).Value;
                    used.Add(key);
                    if (!tr.Contains(key) || !en.Contains(key))
                    {
                        strictMissing.Add($"{Path.GetFileName(file)}: {key}");
                    }
                }

                foreach (Match m in AnyLiteral().Matches(text))
                {
                    used.Add(m.Groups[1].Value);
                }

                foreach (Match m in DynamicUsage().Matches(text))
                {
                    dynamicParts.Add(m.Groups[1].Success
                        ? (m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null)
                        : (m.Groups[3].Value, null));
                }
            }

            foreach (Match m in XamlUsage().Matches(text))
            {
                used.Add(m.Groups[1].Value);
                if (!tr.Contains(m.Groups[1].Value) || !en.Contains(m.Groups[1].Value))
                {
                    strictMissing.Add($"{Path.GetFileName(file)}: {m.Groups[1].Value}");
                }
            }
        }

        ctx.Check(strictMissing.Count == 0, $"l10n: every key used in code/XAML exists in both languages ({strictMissing.Count} missing)" +
            (strictMissing.Count > 0 ? "\n      " + string.Join("\n      ", strictMissing.Take(30)) : string.Empty));

        // Ölü çeviriler: hiçbir yerde anılmayan anahtarlar (dinamik kalıplara uyanlar sayılmaz). Bilgi; başarısızlık değil.
        var unused = tr.Where(k => !used.Contains(k) && !dynamicParts.Any(d => k.StartsWith(d.Prefix, StringComparison.Ordinal) && (d.Suffix is null || k.EndsWith(d.Suffix, StringComparison.Ordinal))))
            .Order(StringComparer.Ordinal).ToList();
        Console.WriteLine($"INFO  l10n: {unused.Count} of {tr.Count} keys are not referenced anywhere{(unused.Count > 0 ? ": " + string.Join(", ", unused.Take(60)) : string.Empty)}");
        return Task.CompletedTask;
    }
}
