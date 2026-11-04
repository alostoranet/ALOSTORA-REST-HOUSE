using System;
using System.IO;
using Alostora.RestHouse;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        var root = Path.Combine(Path.GetTempPath(), "AlostoraRestHouse-WpfSmoke-" + Guid.NewGuid().ToString("N"));
        var library = Path.Combine(root, "Library");
        var posters = Path.Combine(root, "Posters");

        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(library);
            Directory.CreateDirectory(posters);

            // Constructing the window loads the compiled WPF BAML/XAML without
            // showing it or invoking its Loaded handler (which contacts the server).
            var window = new MainWindow(root, library, posters, null);
            window.Close();

            Console.WriteLine("PASS: MainWindow constructed and its compiled WPF XAML loaded successfully.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL: MainWindow could not load its compiled WPF XAML.");
            Console.Error.WriteLine(ex);
            return 1;
        }
        finally
        {
            try
            {
                if (Directory.Exists(root))
                    Directory.Delete(root, recursive: true);
            }
            catch
            {
                // Temporary test data cleanup must not hide the smoke-test result.
            }
        }
    }
}
