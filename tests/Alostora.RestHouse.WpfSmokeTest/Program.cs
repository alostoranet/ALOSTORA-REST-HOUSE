using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Alostora.RestHouse;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "AlostoraRestHouse-WpfSmoke-" + Guid.NewGuid().ToString("N"));
        var library = Path.Combine(root, "Library");
        var posters = Path.Combine(root, "Posters");
        WebApplication? fakeApi = null;
        string? savedLiveUrl = null;
        const string expectedLiveUrl = "https://example.com/live/channel";
        var saveVerified = false;

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
            fakeApi.MapGet("/api/admin/customer-settings", () =>
                Results.Json(new CustomerPageSettings(LiveBroadcastUrl: savedLiveUrl)));
            fakeApi.MapPost("/api/admin/customer-settings", async (HttpContext context) =>
            {
                var settings = await context.Request.ReadFromJsonAsync<CustomerPageSettings>();
                savedLiveUrl = settings?.LiveBroadcastUrl;
                return Results.Ok(new { saved = true });
            });
            fakeApi.MapFallback(() => Results.NotFound());
            fakeApi.StartAsync().GetAwaiter().GetResult();

            // Display the real management window so Loaded runs against a local test API.
            var app = new Application();
            var window = new MainWindow(root, library, posters, null, port);

            var enterAndSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            enterAndSaveTimer.Tick += (_, _) =>
            {
                enterAndSaveTimer.Stop();
                if (window.FindName("CustomerLiveBroadcastUrlBox") is not TextBox liveUrlBox)
                    throw new InvalidOperationException("The external live URL field was not found.");
                liveUrlBox.Text = expectedLiveUrl;
                var saveHandler = typeof(MainWindow).GetMethod(
                    "SaveCustomerPageSettings_Click",
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                if (saveHandler is null)
                    throw new InvalidOperationException("The customer settings save handler was not found.");
                saveHandler.Invoke(window, new object?[] { null, new RoutedEventArgs() });
            };

            var verifyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            verifyTimer.Tick += (_, _) =>
            {
                if (savedLiveUrl == expectedLiveUrl)
                {
                    saveVerified = window.IsVisible;
                    verifyTimer.Stop();
                    window.Close();
                }
            };

            var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                window.Close();
            };

            window.Closed += (_, _) => app.Shutdown();
            enterAndSaveTimer.Start();
            verifyTimer.Start();
            closeTimer.Start();
            app.Run(window);

            if (!saveVerified)
                throw new InvalidOperationException("The visible management window failed to save the external live URL.");
            Console.WriteLine("PASS: the visible management window loaded, accepted an external live URL, saved it through the API, and remained responsive.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: the visible WPF management/live URL smoke test failed.");
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
