using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

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
        Loaded += async (_, _) => { await LoadVideosAsync(); await LoadSeriesAsync(); };
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
            EpisodeVideoPicker.ItemsSource = _videos;
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
        PosterStatusText.Text = string.IsNullOrWhiteSpace(v.PosterPath) ? "لا يوجد غلاف لهذا العمل حالياً." : "يوجد غلاف محفوظ لهذا العمل.";
    }

    private async void UploadPoster_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(IdBox.Text)) { StatusText.Text = "اختر فيديو أولاً."; return; }
        var dialog = new OpenFileDialog { Title = "اختر غلاف الفيلم أو المسلسل", Filter = "صور مدعومة|*.jpg;*.jpeg;*.png;*.webp", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        var contentType = ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", _ => "" };
        var info = new FileInfo(dialog.FileName);
        if (contentType.Length == 0 || info.Length == 0 || info.Length > 5_242_880)
        { StatusText.Text = "اختر صورة JPG أو PNG أو WEBP لا يتجاوز حجمها 5 ميجابايت."; return; }
        try
        {
            StatusText.Text = "جارٍ رفع الغلاف...";
            using var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(dialog.FileName));
            bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            var response = await _http.PostAsync("/api/admin/poster/" + Uri.EscapeDataString(IdBox.Text), bytes);
            if (!response.IsSuccessStatusCode)
            { StatusText.Text = "تعذر رفع الغلاف. تأكد من صيغة الصورة وحجمها."; return; }
            var result = await response.Content.ReadFromJsonAsync<PosterUploadResult>();
            PosterStatusText.Text = "تم رفع الغلاف وحفظه: " + (result?.PosterPath ?? "");
            StatusText.Text = "تم رفع الغلاف بنجاح.";
            await LoadVideosAsync();
            VideosGrid.SelectedItem = _videos.FirstOrDefault(v => v.Id == IdBox.Text);
        }
        catch (Exception ex) { StatusText.Text = "تعذر رفع الغلاف: " + ex.Message; }
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


    private async System.Threading.Tasks.Task LoadSeriesAsync()
    {
        try
        {
            var selectedId = (SeriesPicker.SelectedItem as SeriesAdmin)?.SeriesId;
            var rows = await _http.GetFromJsonAsync<SeriesAdmin[]>("/api/admin/series") ?? Array.Empty<SeriesAdmin>();
            SeriesPicker.ItemsSource = rows;
            SeriesPicker.SelectedItem = rows.FirstOrDefault(x => x.SeriesId == selectedId);
            if (SeriesPicker.SelectedItem is null) SeasonsGrid.ItemsSource = null;
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر تحميل المسلسلات: " + ex.Message; }
    }

    private async void SeriesPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SeriesPicker.SelectedItem is not SeriesAdmin s) return;
        SeriesIdBox.Text = s.SeriesId;
        SeriesTitleBox.Text = s.Title ?? "";
        SeriesDescriptionBox.Text = s.Description ?? "";
        SeriesYearBox.Text = s.Year?.ToString() ?? "";
        SeriesGenreBox.Text = s.Genre ?? "";
        SeriesPosterPathBox.Text = s.PosterPath ?? "";
        await LoadSeasonsAsync(s.SeriesId);
    }

    private async System.Threading.Tasks.Task LoadSeasonsAsync(string seriesId)
    {
        try
        {
            var url = "/api/admin/seasons?seriesId=" + Uri.EscapeDataString(seriesId);
            var rows = await _http.GetFromJsonAsync<SeasonAdmin[]>(url) ?? Array.Empty<SeasonAdmin>();
            SeasonsGrid.ItemsSource = rows;
            SeriesStatusText.Text = $"تم تحميل {rows.Length} موسم/مواسم.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر تحميل المواسم: " + ex.Message; }
    }

    private async void SaveSeries_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SeriesTitleBox.Text)) { SeriesStatusText.Text = "اكتب اسم المسلسل أولاً."; return; }
        int? year = null;
        if (!string.IsNullOrWhiteSpace(SeriesYearBox.Text))
        {
            if (!int.TryParse(SeriesYearBox.Text, out var parsed) || parsed < 1888 || parsed > DateTime.UtcNow.Year + 5)
            { SeriesStatusText.Text = "سنة الإنتاج غير صحيحة."; return; }
            year = parsed;
        }
        var id = string.IsNullOrWhiteSpace(SeriesIdBox.Text) ? "series-" + Guid.NewGuid().ToString("N") : SeriesIdBox.Text.Trim();
        var request = new { seriesId = id, title = SeriesTitleBox.Text.Trim(), description = SeriesDescriptionBox.Text, year, genre = SeriesGenreBox.Text, posterPath = string.IsNullOrWhiteSpace(SeriesPosterPathBox.Text) ? null : SeriesPosterPathBox.Text.Trim() };
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/series", request);
            if (!response.IsSuccessStatusCode) { SeriesStatusText.Text = "لم يتم حفظ المسلسل. تحقق من البيانات."; return; }
            SeriesIdBox.Text = id;
            SeriesStatusText.Text = "تم حفظ بيانات المسلسل.";
            await LoadSeriesAsync();
            SeriesPicker.SelectedItem = (SeriesPicker.ItemsSource as SeriesAdmin[])?.FirstOrDefault(x => x.SeriesId == id);
            if (SeriesPicker.SelectedItem is null) await LoadSeasonsAsync(id);
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر حفظ المسلسل: " + ex.Message; }
    }

    private async void AddSeason_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SeriesIdBox.Text)) { SeriesStatusText.Text = "احفظ المسلسل أولاً."; return; }
        if (!int.TryParse(SeasonNumberBox.Text, out var number) || number < 1 || number > 500)
        { SeriesStatusText.Text = "رقم الموسم يجب أن يكون بين 1 و500."; return; }
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/seasons", new { seriesId = SeriesIdBox.Text.Trim(), seasonNumber = number, title = SeasonTitleBox.Text });
            if (!response.IsSuccessStatusCode) { SeriesStatusText.Text = "لم تتم إضافة الموسم. ربما رقم الموسم مكرر."; return; }
            SeasonTitleBox.Clear(); SeasonNumberBox.Text = (number + 1).ToString();
            await LoadSeasonsAsync(SeriesIdBox.Text.Trim());
            SeriesStatusText.Text = "تمت إضافة الموسم بنجاح.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر إضافة الموسم: " + ex.Message; }
    }

    private void SeasonsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is SeasonAdmin season)
            SeriesStatusText.Text = $"الموسم المحدد: {season.SeasonNumber} — اختر ملف فيديو لإضافته كحلقة.";
    }

    private async void AddEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is not SeasonAdmin season) { SeriesStatusText.Text = "اختر الموسم أولاً."; return; }
        if (EpisodeVideoPicker.SelectedItem is not AdminVideo video) { SeriesStatusText.Text = "اختر ملف الفيديو من المكتبة."; return; }
        if (!int.TryParse(EpisodeNumberBox.Text, out var number) || number < 1 || number > 10000)
        { SeriesStatusText.Text = "رقم الحلقة غير صحيح."; return; }
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/episodes", new { seasonId = season.SeasonId, videoId = video.Id, episodeNumber = number, title = EpisodeTitleBox.Text });
            if (!response.IsSuccessStatusCode) { SeriesStatusText.Text = "لم تتم إضافة الحلقة. قد يكون الملف مرتبطاً بحلقة أخرى أو الرقم مكرراً."; return; }
            EpisodeTitleBox.Clear(); EpisodeNumberBox.Text = (number + 1).ToString();
            SeriesStatusText.Text = $"تم ربط «{video.Title}» كالحلقة رقم {number}.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر إضافة الحلقة: " + ex.Message; }
    }

    private static string GetLanAddress() =>
        Dns.GetHostEntry(Dns.GetHostName()).AddressList
            .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(x))?.ToString()
            ?? "عنوان الشبكة غير معروف";

    private sealed class SeriesAdmin
    {
        public string SeriesId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public int? Year { get; set; }
        public string Genre { get; set; } = "";
        public string? PosterPath { get; set; }
    }
    private sealed class SeasonAdmin
    {
        public long SeasonId { get; set; }
        public string SeriesId { get; set; } = "";
        public int SeasonNumber { get; set; }
        public string Title { get; set; } = "";
    }
    private sealed class PosterUploadResult { public string? PosterPath { get; set; } }
    private sealed class AdminVideo
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int? Year { get; set; }
        public string? Genre { get; set; }
        public string? Kind { get; set; }
        public string? PosterPath { get; set; }
    }
}