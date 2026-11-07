using System;
using System.IO;
using System.Windows;
using Alostora.RestHouse;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "AlostoraRestHouse-WpfSmoke-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = Path.Combine(root, "Library");
            var posters = Path.Combine(root, "Posters");
            Directory.CreateDirectory(library);
            Directory.CreateDirectory(posters);

            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var window = new MainWindow(root, library, posters, null);
            window.Close();
            app.Shutdown();

            Console.WriteLine("PASS: MainWindow constructed and compiled WPF XAML loaded on an STA thread.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: MainWindow WPF smoke test failed.");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
            catch { }
        }
    }
}
