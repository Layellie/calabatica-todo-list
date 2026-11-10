using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;

namespace CyberTodo;

[Flags]
public enum ChamferCorners
{
    None = 0,
    TopLeft = 1,
    TopRight = 2,
    BottomRight = 4,
    BottomLeft = 8,
    All = TopLeft | TopRight | BottomRight | BottomLeft,
}

/// <summary>
/// 45° kesik köşeli HUD çerçevesi. Path + Stretch="Fill" yeniden boyutlanınca açıları bozduğu için
/// geometri her boyutta gerçek piksel ölçüsüyle (StreamGeometry, dondurulmuş) yeniden üretilir.
/// Sadece boyut veya özellik değişince render olur; içerik değişimleri bu elemanı tetiklemez.
/// </summary>
public sealed class ChamferFrame : FrameworkElement
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ChamferFrame),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(ChamferFrame),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
        nameof(StrokeThickness), typeof(double), typeof(ChamferFrame),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender),
        static v => v is double d && double.IsFinite(d) && d >= 0);

    public static readonly DependencyProperty ChamferProperty = DependencyProperty.Register(
        nameof(Chamfer), typeof(double), typeof(ChamferFrame),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender),
        static v => v is double d && double.IsFinite(d) && d >= 0);

    public static readonly DependencyProperty CornersProperty = DependencyProperty.Register(
        nameof(Corners), typeof(ChamferCorners), typeof(ChamferFrame),
        new FrameworkPropertyMetadata(ChamferCorners.TopLeft | ChamferCorners.BottomRight, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>
    /// Doluysa kesik köşelerin DIŞINDA kalan üçgenler bu renkle boyanır (kontur üstte kalır).
    /// Böylece pencere dikdörtgen kalır ama köşeler renkli aksan olarak görünür.
    /// </summary>
    public static readonly DependencyProperty CornerFillProperty = DependencyProperty.Register(
        nameof(CornerFill), typeof(Brush), typeof(ChamferFrame),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush? CornerFill
    {
        get => (Brush?)GetValue(CornerFillProperty);
        set => SetValue(CornerFillProperty, value);
    }

    public Brush? Fill
    {
        get => (Brush?)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush? Stroke
    {
        get => (Brush?)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public double Chamfer
    {
        get => (double)GetValue(ChamferProperty);
        set => SetValue(ChamferProperty, value);
    }

    public ChamferCorners Corners
    {
        get => (ChamferCorners)GetValue(CornersProperty);
        set => SetValue(CornersProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double thickness = StrokeThickness;
        double half = thickness / 2; // kontur dışarı taşmasın diye yarım kalınlık içeri
        double left = half, top = half, right = width - half, bottom = height - half;
        if (right <= left || bottom <= top)
        {
            return;
        }

        double c = Math.Min(Chamfer, Math.Min(right - left, bottom - top) / 2);
        var corners = Corners;
        bool tl = corners.HasFlag(ChamferCorners.TopLeft);
        bool tr = corners.HasFlag(ChamferCorners.TopRight);
        bool br = corners.HasFlag(ChamferCorners.BottomRight);
        bool bl = corners.HasFlag(ChamferCorners.BottomLeft);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(left + (tl ? c : 0), top), isFilled: true, isClosed: true);
            ctx.LineTo(new Point(right - (tr ? c : 0), top), true, false);
            if (tr)
            {
                ctx.LineTo(new Point(right, top + c), true, false);
            }

            ctx.LineTo(new Point(right, bottom - (br ? c : 0)), true, false);
            if (br)
            {
                ctx.LineTo(new Point(right - c, bottom), true, false);
            }

            ctx.LineTo(new Point(left + (bl ? c : 0), bottom), true, false);
            if (bl)
            {
                ctx.LineTo(new Point(left, bottom - c), true, false);
            }

            ctx.LineTo(new Point(left, top + (tl ? c : 0)), true, false);
        }

        geometry.Freeze();

        Pen? pen = null;
        if (Stroke is { } stroke && thickness > 0)
        {
            pen = new Pen(stroke, thickness) { LineJoin = PenLineJoin.Miter };
            pen.Freeze();
        }

        // Köşe üçgenleri önce çizilir; ana kontur üstüne gelip kesik çizgiyi temiz bırakır.
        // Üçgenin hipotenüsü kontur merkez çizgisinin biraz içine uzanır → arada boşluk/aliasing kalmaz.
        if (CornerFill is { } cornerBrush && c > 0)
        {
            double reach = c + thickness;
            var cornerGeometry = new StreamGeometry();
            using (var ctx = cornerGeometry.Open())
            {
                void Triangle(Point a, Point b, Point d)
                {
                    ctx.BeginFigure(a, isFilled: true, isClosed: true);
                    ctx.LineTo(b, false, false);
                    ctx.LineTo(d, false, false);
                }

                if (tl) Triangle(new Point(0, 0), new Point(reach, 0), new Point(0, reach));
                if (tr) Triangle(new Point(width, 0), new Point(width, reach), new Point(width - reach, 0));
                if (br) Triangle(new Point(width, height), new Point(width - reach, height), new Point(width, height - reach));
                if (bl) Triangle(new Point(0, height), new Point(0, height - reach), new Point(reach, height));
            }

            cornerGeometry.Freeze();
            drawingContext.DrawGeometry(cornerBrush, null, cornerGeometry);
        }

        drawingContext.DrawGeometry(Fill, pen, geometry);
    }
}
