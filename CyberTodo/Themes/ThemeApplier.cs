using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;

namespace CyberTodo.Themes;

/// <summary>Tema paletini pencere kaynak sözlüğüne uygular (ana pencere ve mini pencere).</summary>
internal static class ThemeApplier
{
    /// <summary>
    /// Tema: DynamicResource kullanan tüm fırçaları yeni (dondurulmuş) fırçalarla değiştirir.
    /// Primary = başlık/vurgu ("Yellow" anahtarları), Secondary = çerçeve/ikincil ("Cyan" anahtarları).
    /// Ana pencere ve mini pencere sözlüğüne ayrı ayrı uygulanır.
    /// </summary>
    internal static void Apply(ResourceDictionary resources, ThemePalette palette)
    {
        static SolidColorBrush Brush(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        static Color WithAlpha(Color c, byte alpha) => Color.FromArgb(alpha, c.R, c.G, c.B);

        static Color Mix(Color a, Color b, double t) => Color.FromRgb(
            (byte)Math.Round(a.R + ((b.R - a.R) * t)),
            (byte)Math.Round(a.G + ((b.G - a.G) * t)),
            (byte)Math.Round(a.B + ((b.B - a.B) * t)));

        resources["PrimaryColor"] = palette.Primary;
        resources["SecondaryColor"] = palette.Secondary;
        resources["YellowBrush"] = Brush(palette.Primary);
        resources["CyanBrush"] = Brush(palette.Secondary);
        // Yüksek kontrast: soluk/gri tonlar okunabilirlik için tam renge ya da açık griye çekilir.
        bool hc = palette.IsHighContrast;
        resources["YellowDimBrush"] = Brush(hc ? palette.Primary : WithAlpha(palette.Primary, 0x55));
        resources["CyanDimBrush"] = Brush(hc ? palette.Secondary : WithAlpha(palette.Secondary, 0x66));
        resources["CyanFaintBrush"] = Brush(WithAlpha(palette.Secondary, hc ? (byte)0x40 : (byte)0x1F));
        resources["AxisTextBrush"] = Brush(hc ? Colors.White : Mix(palette.Secondary, Color.FromRgb(0x8A, 0x94, 0xA6), 0.55));
        resources["GreyBrush"] = Brush(hc ? Color.FromRgb(0xC8, 0xC8, 0xC8) : Color.FromRgb(0x4A, 0x55, 0x68));
        resources["SubtleTextBrush"] = Brush(hc ? Colors.White : Color.FromRgb(0x8A, 0x94, 0xA6));
        resources["PrimaryDeepBrush"] = Brush(Mix(palette.Primary, Colors.Black, 0.9));
    }
}
