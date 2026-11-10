using System.Windows;
using Microsoft.Win32;

namespace CyberTodo.Services;

/// <summary>Dosya seçimi ve onay: ViewModel pencereye bağlı kalmasın, testler sahte uygulama versin diye arayüz.</summary>
public interface IFileDialogs
{
    /// <returns>Seçilen yol; iptal edilirse null.</returns>
    string? PickSavePath(string defaultFileName, string filter);

    /// <returns>Seçilen yol; iptal edilirse null.</returns>
    string? PickOpenPath(string filter);

    bool Confirm(string message);
}

/// <summary>Varsayılan: hiçbir şey seçilmez/onaylanmaz (testlerde ve diyalog bağlanmamışken güvenli).</summary>
public sealed class NullFileDialogs : IFileDialogs
{
    public string? PickSavePath(string defaultFileName, string filter) => null;

    public string? PickOpenPath(string filter) => null;

    public bool Confirm(string message) => false;
}

/// <summary>Windows'un standart açma/kaydetme pencereleri ve onay kutusu.</summary>
public sealed class WpfFileDialogs(Func<Window?> owner) : IFileDialogs
{
    public string? PickSavePath(string defaultFileName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            FileName = defaultFileName,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public string? PickOpenPath(string filter)
    {
        var dialog = new OpenFileDialog
        {
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
    }

    public bool Confirm(string message)
    {
        var window = owner();
        var result = window is null
            ? MessageBox.Show(message, "CALABATICA", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No)
            : MessageBox.Show(window, message, "CALABATICA", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }
}
