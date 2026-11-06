using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Alostora.RestHouse;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using System.Windows;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "AlostoraRestHouse-WpfSmoke-" + Guid.NewGuid().ToString("N"));
        var library = Path.Combine(root, "Library");
        var posters = Path.Combine(root, "Posters");
        WebApplication? fakeApi = null;

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(library);
            Directory.CreateDirectory(posters);

            var portProbe = new TcpListener(IPAddress.Loopback, 0);
            portProbe.Start();
            var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
            portProbe.Stop();

            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            fakeApi = builder.Build();
            fakeApi.MapFallback(() => Results.NotFound());
            fakeApi.StartAsync().GetAwaiter().GetResult();

            // Show the real management window so its Loaded handler runs against a local
            // test API. Missing optional data returns 404; initialization must log/report
            // errors and continue loading the remaining sections.
            var app = new Application();
            var window = new MainWindow(root, library, posters, null, port);
            var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                window.Close();
            };
            window.Closed += (_, _) => app.Shutdown();
            closeTimer.Start();
            app.Run(window);

            Console.WriteLine("PASS: The visible WPF management window ran its Loaded initialization against a local test API.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: The visible WPF management window failed during its Loaded initialization.");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try { fakeApi?.StopAsync().GetAwaiter().GetResult(); } catch { }
            try { fakeApi?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
