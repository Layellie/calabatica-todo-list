using System.Windows;
using System.Windows.Controls;

namespace CyberTodo.Views;

/// <summary>
/// İlk kez gösterilene kadar içeriğini oluşturmayan yer tutucu: <c>IsOpen</c> ilk true olduğunda
/// <see cref="ViewType"/> türünden bir görünüm (parametresiz kurucu) yaratılır ve Visible olur; false → Collapsed.
/// Açılışta gösterilmeyen sekme panelleri ve katmanlar (ayarlar, veri, şablonlar, rehber, istatistik, odak,
/// alışkanlık, takvim) böylece XAML ayrıştırma/oluşturma maliyetini ancak kullanılınca öder.
/// Oluşturulan görünüm kapanınca atılmaz (tekrar açılış anlık, durum korunur). DataContext mantıksal ebeveynden miras alınır.
/// </summary>
public sealed class DeferredView : ContentControl
{
    public static readonly DependencyProperty ViewTypeProperty = DependencyProperty.Register(
        nameof(ViewType), typeof(Type), typeof(DeferredView));

    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(
        nameof(IsOpen), typeof(bool), typeof(DeferredView), new PropertyMetadata(false, OnIsOpenChanged));

    public DeferredView()
    {
        Visibility = Visibility.Collapsed;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Focusable = false;
    }

    public Type? ViewType
    {
        get => (Type?)GetValue(ViewTypeProperty);
        set => SetValue(ViewTypeProperty, value);
    }

    public bool IsOpen
    {
        get => (bool)GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    /// <summary>İçerik oluşturuldu mu (test/ölçüm için).</summary>
    public bool IsCreated => Content is not null;

    private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var host = (DeferredView)d;
        if ((bool)e.NewValue)
        {
            host.EnsureCreated();
            host.Visibility = Visibility.Visible;
        }
        else
        {
            host.Visibility = Visibility.Collapsed;
        }
    }

    private void EnsureCreated()
    {
        if (Content is not null)
        {
            return;
        }

        var type = ViewType ?? throw new InvalidOperationException("DeferredView.ViewType is not set.");
        if (!typeof(FrameworkElement).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes) is null)
        {
            throw new InvalidOperationException($"{type.FullName} must be a FrameworkElement with a public parameterless constructor.");
        }

        Content = Activator.CreateInstance(type);
    }
}
