using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using LaserConsole.Services;

namespace LaserConsole;

class Program
{
    [System.STAThread]
    public static void Main(string[] args)
    {
        if (args.Contains("--test-jog"))
        {
            TestJogAsync().GetAwaiter().GetResult();
            return;
        }
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    static async Task TestJogAsync()
    {
        var svc = new LaserService();
        try
        {
            Console.WriteLine("=== Jog test ===");
            Console.WriteLine("Connecting...");
            await svc.ConnectAsync();
            Console.WriteLine("Connected. Trying Jog(3) = fast up...");
            await svc.JogAsync(LaserService.JogFastUp);
            Console.WriteLine("Jog(3) succeeded. Stopping...");
            await svc.JogAsync(LaserService.JogStop);
            Console.WriteLine("Stop sent. Test complete.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED: {ex.Message}");
        }
        finally
        {
            svc.Disconnect();
            svc.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
