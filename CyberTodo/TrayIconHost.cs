using System.ComponentModel;
using System.Windows;
using CyberTodo.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace CyberTodo;

/// <summary>
/// Sistem tepsisi simgesi + Windows bildirimleri (balon → Windows 10/11'de bildirim olarak görünür).
/// View katmanı: ViewModel bunu bilmez; MainWindow olaylarla bağlar.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _showItem;
    private readonly Forms.ToolStripMenuItem _miniItem;
    private readonly Forms.ToolStripMenuItem _exitItem;
    private readonly Drawing.Icon _trayIcon;
    private bool _disposed;

    public TrayIconHost()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/CALABATICA;component/Assets/calabatica.ico"))
            ?? throw new InvalidOperationException("Simge kaynağı bulunamadı.");
        using (var stream = resource.Stream)
        {
            _trayIcon = new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }

        _showItem = new Forms.ToolStripMenuItem();
        _exitItem = new Forms.ToolStripMenuItem();
        _miniItem = new Forms.ToolStripMenuItem();
        _miniItem.Click += (_, _) => MiniRequested?.Invoke(this, EventArgs.Empty);
        _showItem.Click += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);
        _exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);

        _menu = new Forms.ContextMenuStrip
        {
            Renderer = new Forms.ToolStripProfessionalRenderer(new HudColorTable()),
            BackColor = Drawing.Color.FromArgb(0x07, 0x0D, 0x12),
            ForeColor = Drawing.Color.FromArgb(0xFC, 0xEE, 0x0A),
            ShowImageMargin = false,
        };
        _menu.Items.Add(_showItem);
        _menu.Items.Add(_miniItem);
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        _icon = new Forms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "CALABATICA",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                ShowRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        _icon.BalloonTipClicked += (_, _) => ShowRequested?.Invoke(this, EventArgs.Empty);

        UpdateTexts();
        Localizer.Instance.PropertyChanged += OnLanguageChanged;
    }

    public event EventHandler? ShowRequested;

    public event EventHandler? ExitRequested;

    public event EventHandler? MiniRequested;

    /// <summary>Windows bildirimi (uygulama gizli/arka plandayken hatırlatmalar için).</summary>
    public void Notify(string message)
    {
        if (_disposed || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        // Windows balon metnini ~255 karakterde keser; taşmayı biz yönetelim.
        string text = message.Length > 240 ? message[..240] + "…" : message;
        _icon.ShowBalloonTip(8000, "CALABATICA", text, Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Localizer.Instance.PropertyChanged -= OnLanguageChanged;
        _icon.Visible = false; // aksi halde simge, fare üzerine gelene kadar tepside "hayalet" kalır
        _icon.Dispose();
        _showItem.Dispose();
        _miniItem.Dispose();
        _exitItem.Dispose();
        _menu.Dispose();
        _trayIcon.Dispose();
    }

    private void OnLanguageChanged(object? sender, PropertyChangedEventArgs e) => UpdateTexts();

    private void UpdateTexts()
    {
        var loc = Localizer.Instance;
        _showItem.Text = loc["Tray_Show"];
        _miniItem.Text = loc["Tray_Mini"];
        _exitItem.Text = loc["Tray_Exit"];
    }

    /// <summary>Tepsi menüsü HUD renklerinde (siyah zemin, sarı metin, cyan seçim çerçevesi).</summary>
    private sealed class HudColorTable : Forms.ProfessionalColorTable
    {
        private static readonly Drawing.Color Panel = Drawing.Color.FromArgb(0x07, 0x0D, 0x12);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x10, 0x26, 0x2C);
        private static readonly Drawing.Color Cyan = Drawing.Color.FromArgb(0x00, 0xF0, 0xFF);

        public override Drawing.Color ToolStripDropDownBackground => Panel;
        public override Drawing.Color MenuBorder => Cyan;
        public override Drawing.Color MenuItemBorder => Cyan;
        public override Drawing.Color MenuItemSelected => Hover;
        public override Drawing.Color MenuItemSelectedGradientBegin => Hover;
        public override Drawing.Color MenuItemSelectedGradientEnd => Hover;
        public override Drawing.Color SeparatorDark => Drawing.Color.FromArgb(0x1E, 0x26, 0x30);
        public override Drawing.Color SeparatorLight => Panel;
    }
}
