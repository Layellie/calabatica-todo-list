using System.IO;
using CyberTodo.Services;
using CyberTodo.ViewModels;

namespace CalabaticaTests;

/// <summary>
/// Testlerin paylaştığı durum: geçici veri klasörü (gerçek kullanıcı verisine dokunulmaz), paylaşılan sahte saat,
/// log klasörü yönlendirmesi ve başarısız test sayacı.
/// </summary>
internal sealed class TestContext
{
    public TestContext()
    {
        Dir = Path.Combine(Path.GetTempPath(), "cybertodo-harness-" + Guid.NewGuid().ToString("N"));
        DataPath = Path.Combine(Dir, "cyber_data.json");
        AppLog.Configure(Path.Combine(Dir, "logs")); // testler gerçek log klasörüne yazmasın
    }

    public int Failures { get; private set; }

    public string Dir { get; }

    /// <summary>Ana senaryonun veri dosyası (<see cref="Fresh"/> bunu açar).</summary>
    public string DataPath { get; }

    /// <summary>Paylaşılan sahte saat (öğlen): "bugün 23:59" gibi testler gün içindeki saatten bağımsız.</summary>
    public ManualTime Clock { get; } = new(DateTime.Today.AddHours(12));

    public void Check(bool ok, string name)
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}");
        if (!ok)
        {
            Failures++;
        }
    }

    public void Fail(string name, Exception exception)
    {
        Console.WriteLine($"FAIL  {name} threw {exception.GetType().Name}");
        Console.WriteLine(exception);
        Failures++;
    }

    /// <summary>Rehber ayrı test edilir; ana senaryolar "rehberi kapatmış kullanıcı" ile çalışır.</summary>
    public async Task<MainViewModel> Fresh()
    {
        var vm = new MainViewModel(new StorageService(DataPath), time: Clock);
        await vm.InitializeAsync();
        vm.CloseOnboardingCommand.Execute(null);
        return vm;
    }
}