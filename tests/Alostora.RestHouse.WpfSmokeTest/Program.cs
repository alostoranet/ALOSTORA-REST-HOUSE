using System;
using System.IO;
using System.Linq;
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

            var enterAndSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            enterAndSaveTimer.Tick += (_, _) =>
            {
                var settingsStatus = window.FindName("CustomerPageSettingsStatusText") as TextBlock;
                if (settingsStatus?.Text != "تم تحميل إعدادات ظهور أقسام صفحة العملاء.") return;
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
                    verifyTimer.Stop();
                    try
                    {
                        saveVerified = window.IsVisible;
                        var openEditor = typeof(MainWindow).GetMethod(
                            "OpenDedicatedEditor",
                            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                            ?? throw new InvalidOperationException("The dedicated editor opener was not found.");

                        // Exercise every dedicated form used by the management pages.
                        // Reopen each one repeatedly to catch stale-parent/closed-window state.
                        var editors = new[]
                        {
                            ("SeriesEditorExpander", "Series editor"),
                            ("CategoryEditorExpander", "Category editor"),
                            ("SeasonEditorExpander", "Season editor"),
                            ("EpisodeEditorExpander", "Episode editor"),
                            ("MovieEditorExpander", "Movie editor")
                        };
                        foreach (var (name, title) in editors)
                        {
                            var editor = window.FindName(name) as Expander
                                ?? throw new InvalidOperationException($"The {title} container was not found.");
                            for (var cycle = 0; cycle < 3; cycle++)
                            {
                                openEditor.Invoke(window, new object?[] { editor, title + " reopen smoke test" });
                                var editorWindow = app.Windows.OfType<Window>().FirstOrDefault(w => !ReferenceEquals(w, window) && w.IsVisible)
                                    ?? throw new InvalidOperationException($"{title} did not open on cycle {cycle + 1}.");
                                editorWindow.Close();
                                if (editor.Content is null)
                                    throw new InvalidOperationException($"{title} content was not restored after cycle {cycle + 1}.");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException("Repeated editor open/close smoke test failed.", ex);
                    }
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
                throw new InvalidOperationException("The visible management window failed to save customer settings and the optional external direct-link URL.");
            Console.WriteLine("PASS: the visible management window loaded and saved customer settings; the optional external direct-link URL was stored; and the series, category, season, episode, and movie editors each opened/closed/reopened three times without losing their forms.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: the visible WPF management/customer settings smoke test failed.");
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
