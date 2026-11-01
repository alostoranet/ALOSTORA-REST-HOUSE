using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Diagnostics;
using System.ComponentModel;
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

    private async void EnableFirewall_Click(object sender, RoutedEventArgs e)
    {
        const string ruleName = "ALOSTORA REST HOUSE TCP 8096";
        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = "advfirewall firewall add rule name=\"" + ruleName + "\" dir=in action=allow protocol=TCP localport=8096 profile=private",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
            {
                MessageBox.Show("تعذر بدء إعداد جدار حماية Windows.", "إعداد الشبكة", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            await process.WaitForExitAsync();
            if (process.ExitCode == 0)
                MessageBox.Show("تم السماح باتصالات TCP على المنفذ 8096 ضمن ملف تعريف الشبكة الخاصة. تأكد أن شبكة Windows مضبوطة على «خاصة».", "إعداد الشبكة", MessageBoxButton.OK, MessageBoxImage.Information);
            else
                MessageBox.Show("لم يتمكن Windows من إضافة قاعدة جدار الحماية. شغّل البرنامج بصلاحية مسؤول وحاول مجدداً.", "إعداد الشبكة", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            MessageBox.Show("تم إلغاء طلب صلاحية المسؤول. لم تتغير إعدادات جدار الحماية.", "إعداد الشبكة", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("تعذر إعداد جدار الحماية: " + ex.Message, "إعداد الشبكة", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        var root = Path.GetDirectoryName(_library)!;
        var databasePath = Path.Combine(root, "resthouse.db");
        var postersPath = Path.Combine(root, "posters");
        var dialog = new SaveFileDialog
        {
            Title = "حفظ نسخة احتياطية من استراحة الأسطورة",
            Filter = "ملف نسخة احتياطية مضغوط|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = "ALOSTORA-REST-HOUSE-Backup-" + DateTime.Now.ToString("yyyyMMdd-HHmm")
        };

        if (dialog.ShowDialog(this) != true) return;
        try
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            await System.Threading.Tasks.Task.Run(() =>
                BackupService.CreateArchive(databasePath, postersPath, dialog.FileName));
            MessageBox.Show(
                "تم إنشاء النسخة الاحتياطية بنجاح.\n\nتتضمن قاعدة البيانات والأغلفة، ولا تتضمن ملفات الفيديو.",
                "النسخ الاحتياطي",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "تعذر إنشاء النسخة الاحتياطية:\n" + ex.Message,
                "النسخ الاحتياطي",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }


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

    private async void UploadSeriesPoster_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SeriesIdBox.Text))
        {
            SeriesStatusText.Text = "احفظ المسلسل أولاً ثم اختر غلافه.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "اختر غلاف المسلسل",
            Filter = "صور مدعومة|*.jpg;*.jpeg;*.png;*.webp",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        var contentType = ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", _ => "" };
        var info = new FileInfo(dialog.FileName);
        if (contentType.Length == 0 || info.Length == 0 || info.Length > 5_242_880)
        {
            SeriesStatusText.Text = "اختر صورة JPG أو PNG أو WEBP لا يتجاوز حجمها 5 ميجابايت.";
            return;
        }

        try
        {
            SeriesStatusText.Text = "جارٍ رفع غلاف المسلسل...";
            using var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(dialog.FileName));
            bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            var response = await _http.PostAsync("/api/admin/series-poster/" + Uri.EscapeDataString(SeriesIdBox.Text.Trim()), bytes);
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذر رفع الغلاف. تحقق من حفظ المسلسل وصيغة الصورة وحجمها.";
                return;
            }

            var result = await response.Content.ReadFromJsonAsync<PosterUploadResult>();
            SeriesPosterPathBox.Text = result?.PosterPath ?? "";
            SeriesStatusText.Text = "تم رفع غلاف المسلسل وحفظه.";
            await LoadSeriesAsync();
            SeriesPicker.SelectedItem = (SeriesPicker.ItemsSource as SeriesAdmin[])?.FirstOrDefault(x => x.SeriesId == SeriesIdBox.Text.Trim());
        }
        catch (Exception ex)
        {
            SeriesStatusText.Text = "تعذر رفع غلاف المسلسل: " + ex.Message;
        }
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

    private static string GetLanAddress()
    {
        static bool IsPrivateIPv4(IPAddress address)
        {
            if (address.AddressFamily != AddressFamily.InterNetwork) return false;
            var b = address.GetAddressBytes();
            return b[0] == 10 ||
                   (b[0] == 172 && b[1] >= 16 && b[1] <= 31) ||
                   (b[0] == 192 && b[1] == 168);
        }

        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .OrderBy(n => n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 ? 0 : 1)
            .ThenByDescending(n => n.GetIPProperties().GatewayAddresses
                .Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork &&
                          !g.Address.Equals(IPAddress.Any)));

        foreach (var adapter in interfaces)
        {
            var address = adapter.GetIPProperties().UnicastAddresses
                .Select(x => x.Address)
                .FirstOrDefault(IsPrivateIPv4);
            if (address is not null) return address.ToString();
        }

        return "عنوان الشبكة غير معروف";
    }

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