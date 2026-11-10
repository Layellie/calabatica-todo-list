using System.IO;
using System.Windows.Threading;
using CalabaticaTests;

// CALABATICA test çalıştırıcısı (bağımlılıksız). Çıkış kodu = başarısız test sayısı.
// Paketler Tests\ altında özelliğe göre ayrılmıştır; sıra ve liste Support\Suites.cs'te.
var context = new TestContext();

var thread = new Thread(() =>
{
    SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
    var frame = new DispatcherFrame();
    Dispatcher.CurrentDispatcher.InvokeAsync(async () =>
    {
        foreach (var (name, run) in Suites.All)
        {
            try
            {
                await run(context);
            }
            catch (Exception ex)
            {
                context.Fail(name, ex); // bir paket çökse de diğerleri çalışır
            }
        }

        frame.Continue = false;
    });
    Dispatcher.PushFrame(frame);
});
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();

Directory.Delete(context.Dir, true);
Console.WriteLine(context.Failures == 0 ? "ALL PASSED" : $"{context.Failures} FAILED");
return context.Failures;