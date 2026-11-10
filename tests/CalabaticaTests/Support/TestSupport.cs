using System.IO;
using System.Windows.Threading;
using CyberTodo.Models;
using CyberTodo.Services;
using CyberTodo.ViewModels;

namespace CalabaticaTests;

/// <summary>Testlerin ortak yardımcıları.</summary>
internal static class TestSupport
{
    public static List<T> FindAll<T>(System.Windows.DependencyObject root) where T : System.Windows.DependencyObject
    {
        var found = new List<T>();
        void Walk(System.Windows.DependencyObject d)
        {
            if (d is T match) { found.Add(match); }
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(d); i++) { Walk(System.Windows.Media.VisualTreeHelper.GetChild(d, i)); }
        }

        Walk(root);
        return found;
    }

    public static byte[] MakeZip(params (string Name, byte[] Content)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var stream = archive.CreateEntry(name).Open();
                stream.Write(content);
            }
        }

        return memory.ToArray();
    }

    /// <summary>Dosya diyalogları yerine: önceden belirlenen yol / onay yanıtı.</summary>
}

sealed class FakeDialogs : IFileDialogs
{
    public string? SavePath { get; set; }
    public string? OpenPath { get; set; }
    public bool ConfirmResult { get; set; } = true;
    public string LastConfirm { get; private set; } = string.Empty;

    public string? PickSavePath(string defaultFileName, string filter) => SavePath;
    public string? PickOpenPath(string filter) => OpenPath;
    public bool Confirm(string message) { LastConfirm = message; return ConfirmResult; }
}

/// <summary>Ağ yerine: adres → içerik. RedirectTo verilirse yanıt o adrese yönlendirilmiş gibi görünür.</summary>
sealed class FakeHttp : System.Net.Http.HttpMessageHandler
{
    private readonly Dictionary<Uri, byte[]> _content = [];
    public Uri? RedirectTo { get; set; }
    public byte[] this[Uri uri] { set => _content[uri] = value; }

    protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = _content.TryGetValue(request.RequestUri!, out var bytes)
            ? new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new System.Net.Http.ByteArrayContent(bytes) }
            : new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        response.RequestMessage = RedirectTo is null ? request : new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, RedirectTo);
        return Task.FromResult(response);
    }
}

/// <summary>WPF binding hata izlerini toplar.</summary>
sealed class CollectingTraceListener(List<string> sink) : System.Diagnostics.TraceListener
{
    private readonly System.Text.StringBuilder _line = new();

    public override void Write(string? message) => _line.Append(message);

    public override void WriteLine(string? message)
    {
        _line.Append(message);
        lock (sink)
        {
            sink.Add(_line.ToString());
        }

        _line.Clear();
    }
}

/// <summary>Elle ilerletilen saat (Microsoft.Extensions.TimeProvider.Testing paketi yerine; bağımlılıksız).</summary>
sealed class ManualTime(DateTime localStart) : TimeProvider
{
    private DateTimeOffset _utc = new DateTimeOffset(localStart, TimeZoneInfo.Local.GetUtcOffset(localStart)).ToUniversalTime();

    public DateTime LocalNow => GetLocalNow().DateTime;

    public override DateTimeOffset GetUtcNow() => _utc;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Local;

    public void Advance(TimeSpan span) => _utc += span;
}
