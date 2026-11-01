using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Diagnostics;

namespace Alostora.RestHouse;

public partial class MainWindow : Window
{
    private readonly string _library;
    private readonly HttpClient _http = new() { BaseAddress = new Uri("http://127.0.0.1:8096") };
    private readonly ObservableCollection<AdminVideo> _videos = new();

    public MainWindow(string library)
    {
        InitializeComponent();
        _library = library;
        LibraryBox.Text = library;
        AddressBox.Text = $"رابط المشاهدة: http://{GetLanAddress()}:8096";
        VideosGrid.ItemsSource = _videos;
        Loaded += async (_, _) => await LoadVideosAsync();
    }

    private async void RefreshLibrary_Click(object sender, RoutedEventArgs e) => await LoadVideosAsync();

    private async System.Threading.Tasks.Task LoadVideosAsync()
    {
        try
        {
            StatusText.Text = "جارٍ تحديث القائمة...";
            var items = await _http.GetFromJsonAsync<AdminVideo[]>("/api/library") ?? Array.Empty<AdminVideo>();
            _videos.Clear();
            foreach (var item in items) _videos.Add(item);
            CountText.Text = $"عدد ملفات الفيديو: {_videos.Count}";
            StatusText.Text = "تم تحديث القائمة. اختر فيديو لتعديل بياناته.";
        }
        catch (Exception ex) { StatusText.Text = "تعذر تحميل المكتبة: " + ex.Message; }
    }

    private void VideosGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideosGrid.SelectedItem is not AdminVideo v) return;
        IdBox.Text = v.Id;
        TitleBox.Text = v.Title ?? "";
        DescriptionBox.Text = v.Description ?? "";
        YearBox.Text = v.Year?.ToString() ?? "";
        GenreBox.Text = v.Genre ?? "";
        KindBox.SelectedIndex = string.Equals(v.Kind, "Series", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    private async void SaveMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(IdBox.Text)) { StatusText.Text = "اختر فيديو أولاً."; return; }
        int? year = null;
        if (!string.IsNullOrWhiteSpace(YearBox.Text))
        {
            if (!int.TryParse(YearBox.Text, out var parsed)) { StatusText.Text = "السنة يجب أن تكون رقماً صحيحاً."; return; }
            year = parsed;
        }
        var kindItem = KindBox.SelectedItem as ComboBoxItem;
        var request = new
        {
            videoId = IdBox.Text,
            title = TitleBox.Text,
            description = DescriptionBox.Text,
            year,
            genre = GenreBox.Text,
            kind = kindItem?.Tag?.ToString() ?? "Movie",
            posterPath = (string?)null
        };
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/metadata", request);
            if (!response.IsSuccessStatusCode) { StatusText.Text = "لم يتم الحفظ. تحقق من العنوان والسنة."; return; }
            StatusText.Text = "تم حفظ بيانات الفيديو بنجاح.";
            await LoadVideosAsync();
            VideosGrid.SelectedItem = _videos.FirstOrDefault(v => v.Id == IdBox.Text);
        }
        catch (Exception ex) { StatusText.Text = "تعذر الحفظ: " + ex.Message; }
    }

    private static string GetLanAddress() =>
        Dns.GetHostEntry(Dns.GetHostName()).AddressList
            .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x))?.ToString()
            ?? "عنوان الشبكة غير معروف";

    private sealed class AdminVideo
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int? Year { get; set; }
        public string? Genre { get; set; }
        public string? Kind { get; set; }
    }
}
