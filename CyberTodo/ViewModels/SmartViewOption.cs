using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CyberTodo.Models;
using CyberTodo.Services;

namespace CyberTodo.ViewModels;

/// <summary>Akıllı liste çipi: etiket + canlı sayaç ("BUGÜN (2)").</summary>
public sealed partial class SmartViewOption : ObservableObject
{
    public SmartViewOption(SmartView value, string key)
    {
        Value = value;
        Key = key;
        PropertyChangedEventManager.AddHandler(Localizer.Instance, OnLanguageChanged, string.Empty); // zayıf abonelik
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => OnPropertyChanged(nameof(Label));

    public SmartView Value { get; }

    public string Key { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Label))]
    private int _count;

    public string Label => Value == SmartView.All
        ? Localizer.Instance[Key]
        : $"{Localizer.Instance[Key]} ({Count})";
}
