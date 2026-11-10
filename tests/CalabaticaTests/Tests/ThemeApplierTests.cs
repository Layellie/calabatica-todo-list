using System.Windows;
using System.Windows.Media;
using CyberTodo.Models;
using CyberTodo.Themes;

namespace CalabaticaTests;

/// <summary>ThemeApplier: her palet için fırça/renk anahtarları, donmuş fırçalar, soluk tonlar ve yüksek kontrast kuralları.</summary>
internal static class ThemeApplierTests
{
    private static readonly string[] s_brushKeys =
    [
        "YellowBrush", "CyanBrush", "YellowDimBrush", "CyanDimBrush", "CyanFaintBrush", "AxisTextBrush", "GreyBrush", "SubtleTextBrush", "PrimaryDeepBrush",
    ];

    public static Task Run(TestContext ctx)
    {
        static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)Math.Round(a.R + ((b.R - a.R) * t)), (byte)Math.Round(a.G + ((b.G - a.G) * t)), (byte)Math.Round(a.B + ((b.B - a.B) * t)));

        foreach (var palette in ThemePalette.All)
        {
            var res = new ResourceDictionary();
            ThemeApplier.Apply(res, palette);
            var brushes = s_brushKeys.ToDictionary(static k => k, k => res[k] as SolidColorBrush);
            ctx.Check(brushes.Values.All(static b => b is not null && b.IsFrozen) && res["PrimaryColor"] is Color && res["SecondaryColor"] is Color,
                $"theme {palette.Id}: all brush/color keys are set and every brush is frozen");

            Color Col(string key) => brushes[key]!.Color;
            bool core = (Color)res["PrimaryColor"] == palette.Primary && (Color)res["SecondaryColor"] == palette.Secondary
                && Col("YellowBrush") == palette.Primary && Col("CyanBrush") == palette.Secondary
                && Col("PrimaryDeepBrush") == Mix(palette.Primary, Colors.Black, 0.9);
            ctx.Check(core, $"theme {palette.Id}: primary/secondary map to the yellow/cyan brushes; deep brush is 90 % toward black");

            if (palette.IsHighContrast)
            {
                ctx.Check(Col("YellowDimBrush") == palette.Primary && Col("CyanDimBrush") == palette.Secondary && Col("CyanFaintBrush").A == 0x40
                    && Col("AxisTextBrush") == Colors.White && Col("SubtleTextBrush") == Colors.White && Col("GreyBrush") == Color.FromRgb(0xC8, 0xC8, 0xC8),
                    $"theme {palette.Id}: high contrast pulls dim/grey tones to full colour or light grey");
            }
            else
            {
                ctx.Check(Col("YellowDimBrush") == Color.FromArgb(0x55, palette.Primary.R, palette.Primary.G, palette.Primary.B)
                    && Col("CyanDimBrush") == Color.FromArgb(0x66, palette.Secondary.R, palette.Secondary.G, palette.Secondary.B)
                    && Col("CyanFaintBrush").A == 0x1F && Col("GreyBrush") == Color.FromRgb(0x4A, 0x55, 0x68) && Col("SubtleTextBrush") == Color.FromRgb(0x8A, 0x94, 0xA6)
                    && Col("AxisTextBrush") == Mix(palette.Secondary, Color.FromRgb(0x8A, 0x94, 0xA6), 0.55),
                    $"theme {palette.Id}: normal themes keep translucent dim tones and blended axis text");
            }
        }

        // Yeniden uygulama kaynakları yerinde değiştirir (aynı sözlükte tema değişimi): eski fırça donuk kalır, yenisi gelir.
        var swap = new ResourceDictionary();
        ThemeApplier.Apply(swap, ThemePalette.Get(ThemeId.Neon));
        var oldBrush = (SolidColorBrush)swap["YellowBrush"];
        ThemeApplier.Apply(swap, ThemePalette.Get(ThemeId.Matrix));
        var newBrush = (SolidColorBrush)swap["YellowBrush"];
        ctx.Check(!ReferenceEquals(oldBrush, newBrush) && oldBrush.Color == ThemePalette.Get(ThemeId.Neon).Primary && newBrush.Color == ThemePalette.Get(ThemeId.Matrix).Primary
            && swap.Count == s_brushKeys.Length + 2,
            "theme: re-applying replaces the entries in place (no duplicates), old brush is untouched");
        return Task.CompletedTask;
    }
}
