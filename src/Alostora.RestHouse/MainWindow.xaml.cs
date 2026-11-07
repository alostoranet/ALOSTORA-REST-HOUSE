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
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Alostora.RestHouse;

public partial class MainWindow : Window
{
    private void OpenWhatsApp_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://wa.me/967777824918") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"تعذر فتح واتساب: {ex.Message}", "التواصل عبر واتساب", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CopyWhatsAppNumber_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText("777824918");
            MessageBox.Show("تم نسخ رقم التواصل: 777824918", "نسخ رقم التواصل", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("تعذر نسخ رقم التواصل: " + ex.Message, "نسخ رقم التواصل", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private sealed record MultiMediaScanResult(bool Scanned, string Kind, int Count, MultiMediaAdminItem[] Items);

    private sealed record MultiMediaAdminItem(string Id, string Kind, string Title, string FileName, string RelativePath, long SizeBytes, string ModifiedUtc, string Url)
    {
        public string KindLabel => Kind switch { "Audio" => "الصوتيات", "Books" => "الكتب", "Apps" => "التطبيقات", _ => Kind };
        public string SizeLabel => SizeBytes < 1024 * 1024 ? $"{SizeBytes / 1024.0:0.#} KB" : $"{SizeBytes / (1024.0 * 1024.0):0.##} MB";
    }
    private sealed record MultiMediaRootAdminItem(string Id, string Kind, string Path, bool IsDefault)
    {
        public string KindLabel => Kind switch { "Audio" => "الصوتيات", "Books" => "الكتب", "Apps" => "التطبيقات", _ => Kind };
        public string RootTypeLabel => IsDefault ? "أساسي" : "خارجي";
    }
    private sealed record MediaStorageRootAdmin(string Id, string Kind, string Path, bool IsDefault, int IndexedFiles, long IndexedBytes, long? DriveTotalBytes, long? DriveFreeBytes)
    {
        public string KindLabel => Kind switch { "Audio" => "الصوتيات", "Books" => "الكتب", "Apps" => "التطبيقات", _ => Kind };
        public string RootTypeLabel => IsDefault ? "أساسي" : "خارجي";
        public string IndexedBytesLabel => FormatStorageBytes(IndexedBytes);
        public string DriveTotalLabel => DriveTotalBytes.HasValue ? FormatStorageBytes(DriveTotalBytes.Value) : "غير متاح";
        public string DriveFreeLabel => DriveFreeBytes.HasValue ? FormatStorageBytes(DriveFreeBytes.Value) : "غير متاح";
        public string FolderStatusLabel => Directory.Exists(Path) ? "المجلد متاح" : "المجلد غير متاح";
        public string DriveStatusLabel => DriveTotalBytes.HasValue ? "القرص جاهز" : "القرص غير متاح";
    }
    private sealed record MediaStorageSummaryAdmin(int RootCount, int IndexedFiles, long IndexedBytes, long? DriveTotalBytes, long? DriveFreeBytes, MediaStorageRootAdmin[]? Roots);

    private readonly string _root;
    private string _library;
    private string _posters;
    private string? _ffmpegPath;
    private string? _pendingSeriesPosterPath;
    private string? _pendingVideoPosterPath;
    private string? _pendingCategoryPosterPath;
    private readonly HttpClient _http = new();
    private readonly int _port;
    private string? _dashboardKindFilter;
    private int _dashboardSeriesCount;
    private int _dashboardMediaCount;
    private int _dashboardExternalRootCount;
    private Window? _activeEditorWindow;
    private Expander? _activeEditorExpander;
    private object? _activeEditorContent;
    private readonly ObservableCollection<AdminVideo> _videos = new();
    private readonly ObservableCollection<PlaybackPopularRow> _popularPlaybackRows = new();
    private readonly ObservableCollection<PlaybackRecentRow> _recentPlaybackRows = new();

    private ICollectionView? _videosView;
    private string? _selectedVideoFolderPath;
    private readonly ObservableCollection<EpisodeCandidate> _episodeCandidates = new();
    private CategoryAdmin[] _categories = Array.Empty<CategoryAdmin>();
    private bool _suppressCustomerMessagePopup;
    private readonly HashSet<long> _knownCustomerMessageIds = new();
    private bool _customerMessagesInitialized;
    private bool _suppressVideoSeriesPickerEvent;
    private readonly DispatcherTimer _messageRefreshTimer = new() { Interval = TimeSpan.FromSeconds(30) };
    private bool _isRefreshingCustomerMessages;

    private async System.Threading.Tasks.Task InitializeManagementWindowSafelyAsync()
    {
        var steps = new (string Name, Func<System.Threading.Tasks.Task> Run)[]
        {
            ("ملف تعريف الاستراحة", LoadBrandProfileAsync),
            ("الأقسام والتصنيفات", LoadCategoriesAsync),
            ("مكتبة الفيديوهات", LoadVideosAsync),
            ("المسلسلات", LoadSeriesAsync),
            ("الحلقات الناقصة", LoadMissingEpisodesAsync),
            ("رسائل العملاء", LoadCustomerMessagesAsync),
            ("الإعلانات والتنبيهات", LoadAnnouncementAsync),
            ("الإعلانات المصورة", () => LoadCustomerAdsAsync()),
            ("إعدادات صفحة العملاء ورابط البث", LoadCustomerPageSettingsAsync),
            ("حد سرعة التنزيل", LoadDownloadSpeedLimitAsync),
            ("أدوات الوسائط", CheckMediaToolsAsync),
            ("المكتبات الإضافية", RefreshMultiMediaLibraryAsync),
            ("مسارات تخزين الوسائط", RefreshMediaLibraryRootsAsync)
        };

        foreach (var step in steps)
        {
            try { await step.Run(); }
            catch (Exception ex)
            {
                App.LogDiagnostic("MainWindow.Loaded / " + step.Name, ex);
                try
                {
                    CustomerPageSettingsStatusText.Text = $"تعذر تحميل {step.Name}. بقيت بقية أقسام الإدارة قيد التحميل؛ راجع سجل الأخطاء.";
                }
                catch { }
            }
        }
    }

    private async System.Threading.Tasks.Task RefreshCustomerMessagesSafelyAsync()
    {
        if (_isRefreshingCustomerMessages) return;
        _isRefreshingCustomerMessages = true;
        try { await LoadCustomerMessagesAsync(); }
        catch (Exception ex)
        {
            App.LogDiagnostic("MainWindow.CustomerMessagesTimer", ex);
            try { CustomerPageSettingsStatusText.Text = "تعذر تحديث رسائل العملاء مؤقتاً. سيُعاد المحاولة تلقائياً."; }
            catch { }
        }
        finally { _isRefreshingCustomerMessages = false; }
    }

    public MainWindow(string root, string library, string posters, string? ffmpegPath)
        : this(root, library, posters, ffmpegPath, 8097)
    {
    }

    public MainWindow(string root, string library, string posters, string? ffmpegPath, int port)
    {
        InitializeComponent();
        var logoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "alostora-logo.png");
        var appLogo = BitmapFrame.Create(new Uri(logoPath, UriKind.Absolute), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Icon = appLogo;
        HeaderBrandLogo.Source = appLogo;
        _root = root;
        _port = port;
        _http.BaseAddress = new Uri($"http://127.0.0.1:{_port}");
        PortBox.Text = _port.ToString();
        _library = library;
        _posters = posters;
        _ffmpegPath = ffmpegPath;
        FfmpegPathBox.Text = ffmpegPath ?? "";
        LibraryBox.Text = library;
        MediaFolderBox.Text = library;
        PostersFolderBox.Text = posters;
        AddressBox.Text = $"رابط المشاهدة: http://{GetLanAddress()}:{_port}";
        ViewingUrlText.Text = $"http://{GetLanAddress()}:{_port}";
        _videosView = CollectionViewSource.GetDefaultView(_videos);
        _videosView.Filter = FilterVideosByFolder;
        VideosGrid.ItemsSource = _videosView;
        DashboardPopularGrid.ItemsSource = _popularPlaybackRows;
        DashboardRecentGrid.ItemsSource = _recentPlaybackRows;
        EpisodeCandidatesGrid.ItemsSource = _episodeCandidates;
        Loaded += async (_, _) => await InitializeManagementWindowSafelyAsync();
        _messageRefreshTimer.Tick += async (_, _) => await RefreshCustomerMessagesSafelyAsync();
        _messageRefreshTimer.Start();
        Closed += (_, _) => _messageRefreshTimer.Stop();
    }

    private void Updates_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "سيتم توفير خدمة التحديثات قريباً. سيتم ربط زر التحديث بموقع التحديث الرسمي عند إضافته من الإدارة.",
            "تحديثات ALOSTORA REST HOUSE",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private async void RefreshMultiMediaLibrary_Click(object sender, RoutedEventArgs e)
    {
        await RefreshMultiMediaLibraryAsync();
        await RefreshMediaLibraryRootsAsync();
    }

    private async System.Threading.Tasks.Task RefreshMultiMediaLibraryAsync()
    {
        try
        {
            var items = await _http.GetFromJsonAsync<MultiMediaAdminItem[]>("/api/media-libraries")
                ?? Array.Empty<MultiMediaAdminItem>();
            MultiMediaGrid.ItemsSource = items;
            _dashboardMediaCount = items.Length;
            UpdateDashboardStats();
            MultiMediaLibraryStatusText.Text = $"عدد الملفات المسجلة: {items.Length}. الملفات تبقى في مجلداتها الأصلية.";
        }
        catch (Exception ex)
        {
            if (MultiMediaLibraryStatusText is not null)
                MultiMediaLibraryStatusText.Text = "تعذر تحميل المكتبات الإضافية: " + ex.Message;
        }
    }

    private async void AddAudioLibraryRoot_Click(object sender, RoutedEventArgs e) => await AddMediaLibraryRootAsync("Audio");
    private async void AddBooksLibraryRoot_Click(object sender, RoutedEventArgs e) => await AddMediaLibraryRootAsync("Books");
    private async void AddAppsLibraryRoot_Click(object sender, RoutedEventArgs e) => await AddMediaLibraryRootAsync("Apps");

    private async System.Threading.Tasks.Task AddMediaLibraryRootAsync(string kind)
    {
        var initial = kind switch
        {
            "Audio" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "Books" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            _ => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
        };
        var dialog = new OpenFolderDialog
        {
            Title = kind switch { "Audio" => "اختر مجلداً يحتوي ملفات الصوتيات", "Books" => "اختر مجلداً يحتوي الكتب والمستندات", _ => "اختر مجلداً يحتوي تطبيقات أندرويد" },
            InitialDirectory = Directory.Exists(initial) ? initial : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/media-libraries/roots",
                new { kind, folderPath = Path.GetFullPath(dialog.FolderName) });
            if (!response.IsSuccessStatusCode)
            {
                MultiMediaLibraryStatusText.Text = "تعذر ربط المجلد: " + await response.Content.ReadAsStringAsync();
                return;
            }
            MultiMediaLibraryStatusText.Text = "تم ربط المجلد. جارٍ فحص الملفات...";
            await ScanMultiMediaLibraryAsync(kind);
            await RefreshMediaLibraryRootsAsync();
        }
        catch (Exception ex)
        {
            MultiMediaLibraryStatusText.Text = "تعذر ربط المجلد: " + ex.Message;
        }
    }

    private async void RemoveMediaLibraryRoot_Click(object sender, RoutedEventArgs e)
    {
        if (MediaLibraryRootsGrid.SelectedItem is not MultiMediaRootAdminItem selected)
        {
            MultiMediaLibraryStatusText.Text = "حدد مجلداً خارجياً من القائمة أولاً.";
            return;
        }
        if (selected.IsDefault)
        {
            MultiMediaLibraryStatusText.Text = "لا يمكن إزالة المجلد الأساسي. يمكنك إزالة المجلدات الخارجية فقط.";
            return;
        }
        var confirm = MessageBox.Show(
            "سيتم فك ربط هذا المسار من فهرس الاستراحة فقط. لن يتم حذف أي ملف من القرص. هل تريد المتابعة؟",
            "تأكيد إزالة مسار المكتبة", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var response = await _http.DeleteAsync("/api/admin/media-libraries/roots/" + Uri.EscapeDataString(selected.Id));
            if (!response.IsSuccessStatusCode)
            {
                MultiMediaLibraryStatusText.Text = "تعذرت إزالة المسار: " + await response.Content.ReadAsStringAsync();
                return;
            }
            await RefreshMediaLibraryRootsAsync();
            await RefreshMultiMediaLibraryAsync();
            MultiMediaLibraryStatusText.Text = "تم فك ربط المسار من الفهرس. الملفات الأصلية لم تتغير.";
        }
        catch (Exception ex)
        {
            MultiMediaLibraryStatusText.Text = "تعذرت إزالة المسار: " + ex.Message;
        }
    }

    private async void RefreshMediaLibraryRoots_Click(object sender, RoutedEventArgs e) => await RefreshMediaLibraryRootsAsync();

    private async System.Threading.Tasks.Task RefreshMediaLibraryRootsAsync()
    {
        try
        {
            var roots = await _http.GetFromJsonAsync<MultiMediaRootAdminItem[]>("/api/admin/media-libraries/roots")
                ?? Array.Empty<MultiMediaRootAdminItem>();
            MediaLibraryRootsGrid.ItemsSource = roots;
        }
        catch (Exception ex)
        {
            MultiMediaLibraryStatusText.Text = "تعذر تحميل مسارات المكتبات: " + ex.Message;
        }
    }

    private async void RefreshStorageSummary_Click(object sender, RoutedEventArgs e) => await RefreshStorageSummaryAsync(true);

    private async System.Threading.Tasks.Task RefreshStorageSummaryAsync(bool showStatus)
    {
        try
        {
            var summary = await _http.GetFromJsonAsync<MediaStorageSummaryAdmin>("/api/admin/storage-summary");
            if (summary is null) return;
            var indexed = FormatStorageBytes(summary.IndexedBytes);
            var total = summary.DriveTotalBytes.HasValue ? FormatStorageBytes(summary.DriveTotalBytes.Value) : "غير متاح";
            var free = summary.DriveFreeBytes.HasValue ? FormatStorageBytes(summary.DriveFreeBytes.Value) : "غير متاح";
            StorageSummaryText.Text = $"المجلدات المرتبطة: {summary.RootCount} | الملفات المفهرسة: {summary.IndexedFiles:N0} | حجم المحتوى المفهرس: {indexed} | إجمالي الأقراص المرتبطة: {total} | المساحة الحرة: {free}.";
            MediaStorageRootsGrid.ItemsSource = summary.Roots ?? Array.Empty<MediaStorageRootAdmin>();
            if (showStatus) MultiMediaLibraryStatusText.Text = "تم تحديث ملخص التخزين وتفاصيل كل مجلد وقرص.";
        }
        catch (Exception ex)
        {
            if (showStatus) MultiMediaLibraryStatusText.Text = "تعذر حساب مساحة التخزين: " + ex.Message;
        }
    }

    private static string FormatStorageBytes(long bytes)
    {
        string[] units = ["بايت", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private async void ScanAudioLibrary_Click(object sender, RoutedEventArgs e) => await ScanMultiMediaLibraryAsync("Audio");
    private async void ScanBooksLibrary_Click(object sender, RoutedEventArgs e) => await ScanMultiMediaLibraryAsync("Books");
    private async void ScanAppsLibrary_Click(object sender, RoutedEventArgs e) => await ScanMultiMediaLibraryAsync("Apps");

    private async System.Threading.Tasks.Task ScanMultiMediaLibraryAsync(string kind)
    {
        try
        {
            MultiMediaLibraryStatusText.Text = "جارٍ فحص الملفات وتحديث الفهرس...";
            var response = await _http.PostAsJsonAsync("/api/admin/media-libraries/scan", new { kind });
            if (!response.IsSuccessStatusCode)
            {
                MultiMediaLibraryStatusText.Text = "تعذر فحص المكتبة: " + await response.Content.ReadAsStringAsync();
                return;
            }
            var result = await response.Content.ReadFromJsonAsync<MultiMediaScanResult>();
            await RefreshMultiMediaLibraryAsync();
            MultiMediaLibraryStatusText.Text = $"اكتمل الفحص. الملفات المفهرسة في هذه المكتبة: {result?.Count ?? 0}.";
        }
        catch (Exception ex)
        {
            MultiMediaLibraryStatusText.Text = "تعذر فحص المكتبة: " + ex.Message;
        }
    }

    private bool FilterVideosByFolder(object item)
    {
        if (item is not AdminVideo video) return false;
        if (!string.IsNullOrWhiteSpace(_dashboardKindFilter) && !string.Equals(video.Kind, _dashboardKindFilter, StringComparison.OrdinalIgnoreCase)) return false;
        if (string.IsNullOrWhiteSpace(_selectedVideoFolderPath)) return true;
        if (string.IsNullOrWhiteSpace(video.FolderPath)) return false;
        var selected = _selectedVideoFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var folder = video.FolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(folder, selected, StringComparison.OrdinalIgnoreCase) ||
               folder.StartsWith(selected + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               folder.StartsWith(selected + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private async System.Threading.Tasks.Task RefreshVideoFoldersTreeAsync()
    {
        if (VideoFoldersTree is null) return;
        try
        {
            var previousPath = _selectedVideoFolderPath;
            var folders = await _http.GetFromJsonAsync<LibraryFolderInfo[]>("/api/admin/library/folders")
                ?? Array.Empty<LibraryFolderInfo>();
            _dashboardExternalRootCount = folders.Count(x => x.ParentPath is null && !x.IsManaged);
            UpdateDashboardStats();
            VideoFoldersTree.Items.Clear();
            var all = new TreeViewItem { Header = $"كل المجلدات ({folders.Length})", Tag = null, IsExpanded = true };
            var nodes = new Dictionary<string, TreeViewItem>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folders.OrderBy(x => x.FullPath.Length).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var node = new TreeViewItem
                {
                    Header = folder.Name + (folder.IsManaged ? "" : " (خارجي)"),
                    Tag = folder.FullPath,
                    ToolTip = folder.FullPath
                };
                if (!string.IsNullOrWhiteSpace(folder.ParentPath) && nodes.TryGetValue(folder.ParentPath, out var parent))
                    parent.Items.Add(node);
                else
                    all.Items.Add(node);
                nodes[folder.FullPath] = node;
            }

            VideoFoldersTree.Items.Add(all);
            if (!string.IsNullOrWhiteSpace(previousPath) && nodes.TryGetValue(previousPath, out var selectedNode))
            {
                selectedNode.IsSelected = true;
                selectedNode.BringIntoView();
            }
            else
            {
                _selectedVideoFolderPath = null;
                all.IsSelected = true;
            }
            _videosView?.Refresh();
        }
        catch (Exception ex)
        {
            StatusText.Text = "تعذر تحميل شجرة مجلدات المكتبة: " + ex.Message;
        }
    }

    private void VideoFoldersTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (VideoFoldersTree.SelectedItem is TreeViewItem selected)
            _selectedVideoFolderPath = selected.Tag as string;
        else
            _selectedVideoFolderPath = null;
        if (!string.IsNullOrWhiteSpace(_selectedVideoFolderPath))
            StatusText.Text = "المجلد المحدد: " + _selectedVideoFolderPath;
        _videosView?.Refresh();
    }

    private async void RefreshLibrary_Click(object sender, RoutedEventArgs e)
    {
        await LoadVideosAsync();
        await LoadMissingEpisodesAsync();
    }

    private async void AddVideoFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "اختر ملفات الفيديو التي تريد إضافتها إلى المكتبة",
            Filter = "ملفات الفيديو المدعومة|*.mp4;*.m4v;*.mkv;*.avi;*.mov;*.webm;*.ts;*.m2ts;*.mts;*.flv;*.wmv;*.mpg;*.mpeg;*.3gp",
            CheckFileExists = true,
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            StatusText.Text = $"جارٍ إضافة {dialog.FileNames.Length} ملف فيديو...";
            var response = await _http.PostAsJsonAsync("/api/admin/register-files", new { filePaths = dialog.FileNames });
            if (!response.IsSuccessStatusCode)
            {
                StatusText.Text = "تعذرت إضافة ملفات الفيديو: " + await response.Content.ReadAsStringAsync();
                return;
            }

            var result = await response.Content.ReadFromJsonAsync<RegisterFilesResult>();
            await LoadVideosAsync();
            await LoadMissingEpisodesAsync();
            StatusText.Text = $"تمت إضافة {result?.VideoCount ?? dialog.FileNames.Length} ملف فيديو إلى المكتبة بنجاح.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "تعذرت إضافة ملفات الفيديو: " + ex.Message;
        }
    }

    private async void AddExternalMediaFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "اختر مجلداً إضافياً يحتوي أفلاماً أو حلقات فيديو",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var folderPath = Path.GetFullPath(dialog.FolderName);
            StatusText.Text = "جارٍ فحص المجلد وعرض ملفات الفيديو للاختيار...";
            var previewUrl = "/api/admin/preview-folder?folderPath=" + Uri.EscapeDataString(folderPath);
            var previewResponse = await _http.GetAsync(previewUrl);
            if (!previewResponse.IsSuccessStatusCode)
            {
                StatusText.Text = "تعذر فحص المجلد. تحقق من وجوده وصلاحية الوصول إليه.";
                return;
            }

            var candidates = await previewResponse.Content.ReadFromJsonAsync<VideoFileCandidateAdmin[]>() ?? Array.Empty<VideoFileCandidateAdmin>();
            if (candidates.Length == 0)
            {
                StatusText.Text = "لم يتم العثور على ملفات فيديو مدعومة داخل المجلد المحدد.";
                return;
            }

            var selection = new FolderVideoSelectionWindow(folderPath, candidates);
            if (selection.ShowDialog() != true)
            {
                StatusText.Text = "تم إلغاء إضافة المجلد. لم تتم إضافة أي ملفات جديدة.";
                return;
            }

            var selectedPaths = selection.SelectedPaths;
            if (selectedPaths.Length == 0)
            {
                StatusText.Text = "لم تحدد أي فيديو للإضافة.";
                return;
            }

            StatusText.Text = "جارٍ إضافة ملفات الفيديو التي حددتها فقط...";
            var response = await _http.PostAsJsonAsync("/api/admin/register-selected-files",
                new { folderPath, filePaths = selectedPaths });
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                StatusText.Text = "تعذرت إضافة الملفات المحددة: " + detail;
                return;
            }

            await LoadVideosAsync();
            await LoadMissingEpisodesAsync();
            StatusText.Text = $"تمت إضافة {selectedPaths.Length} ملف فيديو من أصل {candidates.Length} ملفاً موجوداً في المجلد.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "تعذر إضافة ملفات الفيديو: " + ex.Message;
        }
    }

    private async System.Threading.Tasks.Task LoadBrandProfileAsync()
    {
        try
        {
            var profile = await _http.GetFromJsonAsync<RestHouseProfileAdmin>("/api/admin/profile");
            if (profile is null) return;
            RestHouseNameBox.Text = profile.RestHouseName;
            NetworkNameBox.Text = profile.NetworkName;
            BrandLogoPathBox.Text = profile.LogoPath ?? "";
            BrandLogoPreview.Source = null;
            if (!string.IsNullOrWhiteSpace(profile.LogoPath))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(_http.BaseAddress!, profile.LogoPath);
                image.EndInit();
                image.Freeze();
                BrandLogoPreview.Source = image;
            }
            BrandProfileStatusText.Text = "تم تحميل هوية الاستراحة والشبكة.";
        }
        catch (Exception ex)
        {
            BrandProfileStatusText.Text = "تعذر تحميل بيانات الهوية: " + ex.Message;
        }
    }

    private async void SaveBrandProfile_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(RestHouseNameBox.Text) || string.IsNullOrWhiteSpace(NetworkNameBox.Text))
        {
            BrandProfileStatusText.Text = "اكتب اسم الاستراحة واسم الشبكة أولاً.";
            return;
        }
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/profile", new
            {
                restHouseName = RestHouseNameBox.Text.Trim(),
                networkName = NetworkNameBox.Text.Trim(),
                logoPath = string.IsNullOrWhiteSpace(BrandLogoPathBox.Text) ? null : BrandLogoPathBox.Text.Trim()
            });
            if (!response.IsSuccessStatusCode)
            {
                BrandProfileStatusText.Text = "تعذر حفظ الهوية: " + await response.Content.ReadAsStringAsync();
                return;
            }
            BrandProfileStatusText.Text = $"تم حفظ الهوية: {RestHouseNameBox.Text.Trim()} — {NetworkNameBox.Text.Trim()}.";
        }
        catch (Exception ex) { BrandProfileStatusText.Text = "تعذر حفظ الهوية: " + ex.Message; }
    }

    private async void ChooseBrandLogo_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "اختر شعار أو صورة الاستراحة والشبكة",
            Filter = "صور مدعومة|*.jpg;*.jpeg;*.png;*.webp",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        var extension = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        var contentType = extension switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", _ => "" };
        var info = new FileInfo(dialog.FileName);
        if (contentType.Length == 0 || info.Length == 0 || info.Length > 5_242_880)
        {
            BrandProfileStatusText.Text = "اختر صورة JPG أو PNG أو WEBP لا يتجاوز حجمها 5 ميجابايت.";
            return;
        }
        try
        {
            BrandProfileStatusText.Text = "جارٍ رفع شعار الشبكة...";
            using var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(dialog.FileName));
            bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            var response = await _http.PostAsync("/api/admin/profile/logo", bytes);
            if (!response.IsSuccessStatusCode)
            {
                BrandProfileStatusText.Text = "تعذر رفع الشعار: " + await response.Content.ReadAsStringAsync();
                return;
            }
            var result = await response.Content.ReadFromJsonAsync<BrandLogoUploadResult>();
            BrandLogoPathBox.Text = result?.LogoPath ?? "";
            await LoadBrandProfileAsync();
            BrandProfileStatusText.Text = "تم رفع الشعار وحفظه. سيظهر في واجهة العملاء.";
        }
        catch (Exception ex) { BrandProfileStatusText.Text = "تعذر رفع الشعار: " + ex.Message; }
    }

    private void ChooseMediaFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "اختر المجلد الرئيسي الذي يحتوي ملفات الأفلام والمسلسلات",
            InitialDirectory = Directory.Exists(_library) ? _library : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos)
        };
        if (dialog.ShowDialog(this) != true) return;
        _library = Path.GetFullPath(dialog.FolderName);
        SaveFolderSettings();
    }

    private void ChoosePostersFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "اختر المجلد الذي يحتوي صور أغلفة الأفلام والمسلسلات",
            InitialDirectory = Directory.Exists(_posters) ? _posters : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)
        };
        if (dialog.ShowDialog(this) != true) return;
        _posters = Path.GetFullPath(dialog.FolderName);
        SaveFolderSettings();
    }

    private void ChooseSeriesFolder_Click(object sender, RoutedEventArgs e)
    {
        var initial = Directory.Exists(SeriesFolderPathBox.Text) ? SeriesFolderPathBox.Text : _library;
        var dialog = new OpenFolderDialog { Title = "اختر المجلد الذي يحتوي ملفات هذا المسلسل", InitialDirectory = Directory.Exists(initial) ? initial : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
        if (dialog.ShowDialog(this) == true) SeriesFolderPathBox.Text = Path.GetFullPath(dialog.FolderName);
    }

    private void ChooseSeasonFolder_Click(object sender, RoutedEventArgs e)
    {
        var initial = Directory.Exists(SeasonFolderPathBox.Text) ? SeasonFolderPathBox.Text : (Directory.Exists(SeriesFolderPathBox.Text) ? SeriesFolderPathBox.Text : _library);
        var dialog = new OpenFolderDialog { Title = "اختر المجلد الذي يحتوي حلقات هذا الموسم", InitialDirectory = Directory.Exists(initial) ? initial : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos) };
        if (dialog.ShowDialog(this) == true) SeasonFolderPathBox.Text = Path.GetFullPath(dialog.FolderName);
    }

    private void ChooseFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "اختر ملف ffmpeg.exe",
            Filter = "FFmpeg executable|ffmpeg.exe|All executable files|*.exe",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true)
            FfmpegPathBox.Text = Path.GetFullPath(dialog.FileName);
    }

    private void SaveFfmpeg_Click(object sender, RoutedEventArgs e)
    {
        var selected = FfmpegPathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(selected))
        {
            MessageBox.Show("اختر ملف ffmpeg.exe أولاً.", "إعداد محرك الفيديو", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        selected = Path.GetFullPath(selected);
        if (!File.Exists(selected) || !string.Equals(Path.GetFileName(selected), "ffmpeg.exe", StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show("المسار يجب أن يشير إلى ملف ffmpeg.exe موجود.", "إعداد محرك الفيديو", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var ffprobe = Path.Combine(Path.GetDirectoryName(selected)!, "ffprobe.exe");
        try
        {
            AppSettings.SaveFfmpegPath(_root, selected);
            _ffmpegPath = selected;
            FfmpegStatusText.Text = File.Exists(ffprobe)
                ? "تم حفظ المسار. وجدنا ffprobe.exe أيضاً. أعد تشغيل البرنامج لتفعيل تحويل الفيديو واختيار الصوت."
                : "تم حفظ المسار، لكن لم نجد ffprobe.exe بجانب FFmpeg. ضع الملفين في المجلد نفسه ثم أعد تشغيل البرنامج.";
            MessageBox.Show(FfmpegStatusText.Text, "إعداد محرك الفيديو", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("تعذر حفظ إعداد FFmpeg: " + ex.Message, "إعداد محرك الفيديو", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveFolderSettings()
    {
        try
        {
            if (!Directory.Exists(_library) || !Directory.Exists(_posters))
                throw new DirectoryNotFoundException("تأكد أن مجلدي الفيديو والأغلفة موجودان ويمكن الوصول إليهما.");
            AppSettings.Save(_root, _library, _posters);
            MediaFolderBox.Text = _library;
            PostersFolderBox.Text = _posters;
            LibraryBox.Text = _library;
            MessageBox.Show(
                "تم حفظ مسارات المجلدات. لكي يبدأ البرنامج باستخدامها، أغلق ALOSTORA REST HOUSE وافتحه مرة أخرى.\n\nملاحظة: لا يتم نقل ملفات الفيديو أو الصور؛ البرنامج يقرأها من المجلدات التي اخترتها.",
                "تحديث مسارات المكتبة",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("تعذر حفظ مسارات المجلدات: " + ex.Message,
                "مسارات المكتبة", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }


    private void OpenDedicatedEditor(Expander editor, string title)
    {
        if (_activeEditorWindow is not null || editor.Content is null) return;
        _activeEditorExpander = editor;
        _activeEditorContent = editor.Content;
        editor.Content = null;
        var editorBody = _activeEditorContent as UIElement;
        var editorFrame = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(247, 250, 253)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(199, 212, 226)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(22),
            Margin = new Thickness(4),
            Child = editorBody
        };
        var editorScrollViewer = new ScrollViewer
        {
            Content = editorFrame,
            Margin = new Thickness(0),
            Padding = new Thickness(14),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var window = new Window
        {
            Title = title,
            Owner = this,
            Width = 820,
            Height = 700,
            MinWidth = 640,
            MinHeight = 480,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FlowDirection = FlowDirection.RightToLeft,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(243, 246, 250)),
            Content = editorScrollViewer
        };
        _activeEditorWindow = window;
        window.Closed += (_, _) =>
        {
            if (window.Content is ScrollViewer editorScrollViewer) editorScrollViewer.Content = null;
            window.Content = null;
            var closedEditor = _activeEditorExpander;
            if (closedEditor is not null)
            {
                closedEditor.Content = _activeEditorContent;
                closedEditor.IsExpanded = false;
                if (closedEditor == CategoryEditorExpander) CategoriesGrid.SelectedIndex = -1;
                else if (closedEditor == SeriesEditorExpander) SeriesPicker.SelectedIndex = -1;
                else if (closedEditor == EpisodeEditorExpander) EpisodesGrid.SelectedIndex = -1;
            }
            _activeEditorWindow = null;
            _activeEditorExpander = null;
            _activeEditorContent = null;
        };
        window.ShowDialog();
    }

    private void CloseDedicatedEditor() => _activeEditorWindow?.Close();

    private void PortBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (ViewingUrlText is null || PortBox is null) return;
        if (int.TryParse(PortBox.Text.Trim(), out var port) && port is >= 1024 and <= 65535)
        {
            ViewingUrlText.Text = $"http://{GetLanAddress()}:{port} (يُطبّق بعد الحفظ وإعادة التشغيل)";
        }
        else
        {
            ViewingUrlText.Text = "أدخل منفذاً صحيحاً بين 1024 و65535.";
        }
    }

    private void SavePort_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(PortBox.Text.Trim(), out var port) || port is < 1024 or > 65535)
        {
            MessageBox.Show("أدخل منفذاً صحيحاً بين 1024 و65535.", "إعداد المنفذ", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            AppSettings.SavePort(_root, port);
            ViewingUrlText.Text = $"http://{GetLanAddress()}:{port}";
            AddressBox.Text = $"رابط المشاهدة (بعد إعادة التشغيل): http://{GetLanAddress()}:{port}";
            MessageBox.Show($"تم حفظ المنفذ {port}. أغلق البرنامج وافتحه مجدداً لتطبيقه على السيرفر.", "إعداد المنفذ", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("تعذر حفظ المنفذ: " + ex.Message, "إعداد المنفذ", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyAddress_Click(object sender, RoutedEventArgs e)
    {
        var url = $"http://{GetLanAddress()}:{_port}";
        Clipboard.SetText(url);
        MessageBox.Show("تم نسخ رابط المشاهدة: " + url, "نسخ الرابط", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void OpenSeasonEditor_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SeriesIdBox.Text))
        {
            SeriesStatusText.Text = "احفظ المسلسل أولاً قبل إضافة موسم أو جزء.";
            return;
        }
        OpenDedicatedEditor(SeasonEditorExpander, "إضافة موسم أو جزء");
    }

    private void OpenEpisodeEditor_Click(object sender, RoutedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is not SeasonAdmin)
        {
            SeriesStatusText.Text = "اختر الموسم أولاً قبل إضافة حلقة.";
            return;
        }
        UpdateEpisodeInEditorButton.Visibility = Visibility.Collapsed;
        OpenDedicatedEditor(EpisodeEditorExpander, "إضافة حلقة للموسم المحدد");
    }

    private async void EnableFirewall_Click(object sender, RoutedEventArgs e)
    {
        var ruleName = $"ALOSTORA REST HOUSE TCP {_port}";
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"$ErrorActionPreference='Stop'; try { Get-NetFirewallRule -DisplayName '" + ruleName + "' -ErrorAction SilentlyContinue | Remove-NetFirewallRule; New-NetFirewallRule -DisplayName '" + ruleName + "' -Direction Inbound -Action Allow -Protocol TCP -LocalPort " + _port + " -Profile Private | Out-Null; exit 0 } catch { Write-Error $_; exit 1 }\"",
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
                MessageBox.Show($"تم السماح باتصالات TCP على المنفذ {_port} ضمن ملف تعريف الشبكة الخاصة. تأكد أن شبكة Windows مضبوطة على «خاصة».", "إعداد الشبكة", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var databasePath = Path.Combine(_root, "resthouse.db");
        var postersPath = _posters;
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


    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "اختر نسخة احتياطية لاستعادة المكتبة",
            Filter = "نسخ استراحة الأسطورة|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        var confirm = MessageBox.Show(
            "سيتم استبدال قاعدة بيانات المكتبة والأغلفة بمحتويات النسخة المحددة.\n" +
            "سيُنشئ البرنامج نسخة أمان من البيانات الحالية قبل الاستعادة.\n\n" +
            "بعد نجاح الاستعادة أغلق البرنامج وافتحه مرة أخرى. هل تريد المتابعة؟",
            "تأكيد استعادة النسخة الاحتياطية",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            await System.Threading.Tasks.Task.Run(() => BackupService.RestoreArchive(
                Path.Combine(_root, "resthouse.db"), _posters, dialog.FileName, _root));
            MessageBox.Show(
                "تمت استعادة النسخة الاحتياطية وفحص قاعدة البيانات بنجاح.\n" +
                "أغلق ALOSTORA REST HOUSE وافتحه مرة أخرى لتحديث بيانات المكتبة.",
                "استعادة النسخة الاحتياطية",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "لم تكتمل الاستعادة. لم يتم تجاهل الخطأ، وحاول البرنامج إعادة البيانات السابقة عند حدوث مشكلة:\n\n" + ex.Message,
                "تعذر استعادة النسخة",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    private AdminVideo[] GetSelectedVideos() => VideosGrid.SelectedItems.Cast<object>().OfType<AdminVideo>().ToArray();

    private void SelectAllVideos_Click(object sender, RoutedEventArgs e)
    {
        VideosGrid.SelectAll();
        StatusText.Text = $"تم تحديد جميع الملفات: {VideosGrid.SelectedItems.Count}.";
    }

    private void ClearVideoSelection_Click(object sender, RoutedEventArgs e)
    {
        VideosGrid.UnselectAll();
        StatusText.Text = "تم إلغاء تحديد الملفات.";
    }

    private async void CreateLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Window
        {
            Title = "إنشاء مجلد فيديو جديد",
            Width = 430,
            Height = 205,
            MinWidth = 390,
            MinHeight = 190,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            FlowDirection = FlowDirection.RightToLeft,
            Background = System.Windows.Media.Brushes.White
        };
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock
        {
            Text = "اكتب اسم المجلد. سينشأ داخل مجلد التخزين الرئيسي أو داخل المجلد المحدد.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });
        var nameBox = new TextBox { Padding = new Thickness(9), FontSize = 15 };
        panel.Children.Add(nameBox);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0) };
        var create = new Button { Content = "إنشاء المجلد", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 7, 0), IsDefault = true };
        var cancel = new Button { Content = "إلغاء", Padding = new Thickness(14, 7, 14, 7), IsCancel = true };
        create.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(nameBox.Text)) dialog.DialogResult = true; };
        buttons.Children.Add(create);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        dialog.Content = panel;
        if (dialog.ShowDialog() != true) return;

        try
        {
            var parentPath = _selectedVideoFolderPath ?? _library;
            var response = await _http.PostAsJsonAsync("/api/admin/library/folders", new { parentPath, name = nameBox.Text.Trim() });
            if (!response.IsSuccessStatusCode)
            {
                StatusText.Text = "تعذر إنشاء المجلد: " + await response.Content.ReadAsStringAsync();
                return;
            }
            _selectedVideoFolderPath = Path.GetFullPath(Path.Combine(parentPath, nameBox.Text.Trim()));
            await RefreshVideoFoldersTreeAsync();
            StatusText.Text = "تم إنشاء المجلد. يمكنك تحديده وإضافة الفيديوهات إليه.";
        }
        catch (Exception ex) { StatusText.Text = "تعذر إنشاء المجلد: " + ex.Message; }
    }

    private async void RemoveExternalLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        if (VideoFoldersTree.SelectedItem is not TreeViewItem selected ||
            selected.Tag is not string folderPath || string.IsNullOrWhiteSpace(folderPath))
        {
            StatusText.Text = "حدد مسار تخزين خارجي من شجرة المجلدات أولاً.";
            return;
        }

        try
        {
            var folders = await _http.GetFromJsonAsync<LibraryFolderInfo[]>("/api/admin/library/folders")
                ?? Array.Empty<LibraryFolderInfo>();
            var root = folders.FirstOrDefault(x =>
                string.Equals(x.FullPath, folderPath, StringComparison.OrdinalIgnoreCase));
            if (root is null || root.ParentPath is not null || root.IsManaged)
            {
                StatusText.Text = "يمكن إزالة مسار تخزين خارجي مسجل فقط. لا يمكن إزالة مجلد فرعي من المكتبة الرئيسية.";
                return;
            }

            if (MessageBox.Show(
                "سيتم فك تسجيل مسار التخزين الخارجي من مكتبة المشاهدة فقط. ستختفي فيديوهاته من الفهرس، لكن جميع الملفات الأصلية وبياناتها ستبقى محفوظة على القرص. ويمكنك إضافة المسار مجدداً لاحقاً.\n\nهل تريد المتابعة؟",
                "إزالة مسار التخزين من المكتبة",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var response = await _http.PostAsJsonAsync("/api/admin/library/unregister-folder", new { folderPath });
            if (!response.IsSuccessStatusCode)
            {
                StatusText.Text = "تعذر إزالة المسار: " + await response.Content.ReadAsStringAsync();
                return;
            }

            _selectedVideoFolderPath = null;
            await LoadVideosAsync();
            StatusText.Text = "تم فك تسجيل المسار من المكتبة. لم يتم حذف أي ملف فيديو من الجهاز.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "تعذر إزالة مسار التخزين: " + ex.Message;
        }
    }

    private void OpenLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        var path = _selectedVideoFolderPath ?? _library;
        if (!Directory.Exists(path))
        {
            StatusText.Text = "المجلد المحدد غير موجود أو لم يعد متاحاً.";
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) { StatusText.Text = "تعذر فتح المجلد: " + ex.Message; }
    }

    private async void CopyVideosToSelectedFolder_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedVideos();
        if (selected.Length == 0)
        {
            StatusText.Text = "حدد فيديو واحداً أو أكثر من قائمة المكتبة أولاً.";
            return;
        }

        var destination = Path.GetFullPath(_selectedVideoFolderPath ?? _library);
        if (!Directory.Exists(destination))
        {
            StatusText.Text = "المجلد الهدف غير موجود. حدده مرة أخرى.";
            return;
        }

        if (MessageBox.Show(
            $"سيتم نسخ {selected.Length} ملف فيديو إلى المجلد المحدد دون نقل الملفات الأصلية أو استبدال أي ملف موجود. هل تريد المتابعة؟",
            "تأكيد نسخ ملفات الفيديو", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            StatusText.Text = $"جارٍ نسخ {selected.Length} ملف فيديو...";
            var result = await System.Threading.Tasks.Task.Run(() =>
            {
                var copied = 0;
                var skipped = 0;
                var errors = new List<string>();
                foreach (var video in selected)
                {
                    try
                    {
                        if (string.IsNullOrWhiteSpace(video.FolderPath) || string.IsNullOrWhiteSpace(video.FileName))
                        {
                            skipped++;
                            continue;
                        }
                        var source = Path.GetFullPath(Path.Combine(video.FolderPath, video.FileName));
                        var target = Path.GetFullPath(Path.Combine(destination, Path.GetFileName(video.FileName)));
                        if (!File.Exists(source) || string.Equals(source, target, StringComparison.OrdinalIgnoreCase) || File.Exists(target))
                        {
                            skipped++;
                            continue;
                        }
                        File.Copy(source, target, overwrite: false);
                        copied++;
                    }
                    catch (Exception ex) { errors.Add($"{video.FileName}: {ex.Message}"); }
                }
                return (Copied: copied, Skipped: skipped, Errors: errors);
            });

            var refreshResponse = await _http.PostAsJsonAsync("/api/admin/scan-folder", new { folderPath = destination });
            await LoadVideosAsync();
            if (!refreshResponse.IsSuccessStatusCode)
                StatusText.Text = "تم النسخ، لكن تعذر تحديث فهرس المكتبة تلقائياً: " + await refreshResponse.Content.ReadAsStringAsync();
            else
                StatusText.Text = $"اكتمل النسخ: {result.Copied} ملف. تم تجاوز {result.Skipped} ملف.";
            if (result.Errors.Count > 0)
                MessageBox.Show($"تم نسخ {result.Copied} ملف، وتعذر نسخ {result.Errors.Count} ملف:\n\n{string.Join(Environment.NewLine, result.Errors.Take(8))}",
                    "نتيجة نسخ الفيديوهات", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex) { StatusText.Text = "تعذر نسخ ملفات الفيديو: " + ex.Message; }
    }

    private async void RemoveVideoFromLibrary_Click(object sender, RoutedEventArgs e)
    {
        var selected = GetSelectedVideos();
        if (selected.Length == 0) { StatusText.Text = "حدد ملفاً واحداً أو عدة ملفات أولاً."; return; }
        var confirm = MessageBox.Show(
            $"سيتم إخفاء {selected.Length} ملفاً من مكتبة المشاهدة فقط. لن تُحذف الملفات الأصلية من القرص، ويمكن إضافتها مجدداً لاحقاً.\n\nهل تريد المتابعة؟",
            "إزالة الملفات المحددة من المكتبة", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/library/bulk-remove", new { ids = selected.Select(x => x.Id).ToArray() });
            if (!response.IsSuccessStatusCode) { StatusText.Text = "تعذرت الإزالة: " + await response.Content.ReadAsStringAsync(); return; }
            VideosGrid.UnselectAll();
            await LoadVideosAsync();
            StatusText.Text = $"تمت إزالة {selected.Length} ملفاً من المكتبة، والملفات الأصلية محفوظة.";
        }
        catch (Exception ex) { StatusText.Text = "تعذرت إزالة الملفات: " + ex.Message; }
    }

    private void DeleteVideo_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "الحذف النهائي لملفات الفيديو معطّل حفاظاً على ملفاتك الأصلية. استخدم «إزالة المحدد من المكتبة» لإخفائها دون حذفها من القرص.";
    }

    private async System.Threading.Tasks.Task LoadCategoriesAsync()
    {
        try
        {
            var rows = await _http.GetFromJsonAsync<CategoryAdmin[]>("/api/admin/catalog-categories") ?? Array.Empty<CategoryAdmin>();
            _categories = rows;
            UpdateDashboardStats();
            var parents = rows.Where(x => x.ParentId is null && x.ContentKind == "Group").ToArray();
            var parentNames = parents.ToDictionary(x => x.CategoryId, x => x.Title, StringComparer.OrdinalIgnoreCase);
            foreach (var row in rows)
            {
                row.DisplayTitle = row.ParentId is not null && parentNames.TryGetValue(row.ParentId, out var parent)
                    ? parent + "  ←  " + row.Title : row.Title;
                row.ContentKindLabel = row.ContentKind switch { "Group" => "قسم رئيسي", "Movie" => "أفلام", "Series" => "مسلسلات", _ => "محتوى آخر" };
            }
            CategoriesGrid.ItemsSource = rows;
            CategoryParentPicker.ItemsSource = parents;
            VideoMainCategoryPicker.ItemsSource = parents;
            SeriesMainCategoryPicker.ItemsSource = parents;

            if (VideoMainCategoryPicker.SelectedIndex < 0)
                VideoMainCategoryPicker.SelectedValue = parents.FirstOrDefault(x => x.IsPublished)?.CategoryId;
            if (SeriesMainCategoryPicker.SelectedIndex < 0)
                SeriesMainCategoryPicker.SelectedValue = parents.FirstOrDefault(x => x.IsPublished)?.CategoryId;

            RefreshSeriesCategoryPicker(SeriesCategoryPicker.SelectedValue as string);
            RefreshVideoCategoryPicker(VideoCategoryPicker.SelectedValue as string);
        }
        catch (Exception ex) { CategoryStatusText.Text = "تعذر تحميل الأقسام: " + ex.Message; }
    }

    private void SeriesMainCategoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SeriesCategoryPicker is null) return;
        RefreshSeriesCategoryPicker();
    }

    private void VideoMainCategoryPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideoCategoryPicker is null) return;
        RefreshVideoCategoryPicker();
    }

    private void RefreshSeriesCategoryPicker(string? selectedCategoryId = null)
    {
        if (SeriesCategoryPicker is null || SeriesCategoryStatusText is null) return;
        var parentId = SeriesMainCategoryPicker.SelectedValue as string;
        var rows = string.IsNullOrWhiteSpace(parentId)
            ? Array.Empty<CategoryAdmin>()
            : _categories.Where(x => x.ParentId == parentId && x.ContentKind == "Series").ToArray();
        SeriesCategoryPicker.ItemsSource = rows;
        SeriesCategoryPicker.SelectedValue = selectedCategoryId;
        SeriesCategoryStatusText.Text = string.IsNullOrWhiteSpace(parentId)
            ? "اختر القسم الرئيسي أولاً لتظهر الأقسام الفرعية التابعة له."
            : rows.Length == 0
                ? "لا توجد أقسام فرعية من نوع «مسلسلات» تحت هذا القسم الرئيسي. أضف قسماً فرعياً من إدارة الأقسام واختر نوع «مسلسلات»."
                : $"القسم الرئيسي المحدد يحتوي على {rows.Length} قسم فرعي للمسلسلات. اختر القسم المناسب.";
    }

    private void KindBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideoCategoryPicker is null || VideoMainCategoryPicker is null) return;
        RefreshVideoCategoryPicker(VideoCategoryPicker.SelectedValue as string);
    }

    private void RefreshVideoCategoryPicker(string? selectedCategoryId = null)
    {
        if (VideoCategoryPicker is null || VideoCategoryStatusText is null || VideoMainCategoryPicker is null) return;
        var parentId = VideoMainCategoryPicker.SelectedValue as string;
        var kind = (KindBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Movie";
        if (kind != "Series" && VideoSeriesPicker.SelectedIndex >= 0)
        {
            _suppressVideoSeriesPickerEvent = true;
            VideoSeriesPicker.SelectedIndex = -1;
            _suppressVideoSeriesPickerEvent = false;
            VideoSeasonPicker.ItemsSource = null;
            VideoEpisodeNumberBox.Clear();
            VideoEpisodeTitleBox.Clear();
        }
        var allowedKinds = kind == "Series" ? new[] { "Series" } : new[] { "Movie", "Other" };
        var rows = string.IsNullOrWhiteSpace(parentId)
            ? Array.Empty<CategoryAdmin>()
            : _categories.Where(x => x.ParentId == parentId && allowedKinds.Contains(x.ContentKind, StringComparer.Ordinal)).ToArray();
        VideoCategoryPicker.ItemsSource = rows;
        VideoCategoryPicker.SelectedValue = selectedCategoryId;
        VideoCategoryStatusText.Text = string.IsNullOrWhiteSpace(parentId)
            ? "اختر القسم الرئيسي أولاً لتظهر الأقسام الفرعية التابعة له."
            : rows.Length == 0
                ? kind == "Series"
                    ? "لا توجد أقسام مسلسلات تحت هذا القسم الرئيسي. أضف قسماً فرعياً من نوع «مسلسلات»."
                    : "لا توجد أقسام أفلام تحت هذا القسم الرئيسي. أضف قسماً فرعياً من نوع «أفلام» أو «محتوى آخر»."
                : $"القسم الرئيسي المحدد يحتوي على {rows.Length} قسم فرعي مناسب. اختر القسم الذي سيظهر فيه الفيديو.";
    }

    private void NewCategory_Click(object sender, RoutedEventArgs e)
    {
        _pendingCategoryPosterPath = null;
        CategoriesGrid.UnselectAll();
        CategoryIdBox.Clear();
        CategoryTitleBox.Clear();
        CategoryParentPicker.SelectedIndex = -1;
        CategoryKindPicker.SelectedIndex = 0;
        CategoryPosterPathBox.Clear();
        CategoryPublishBox.IsChecked = true;
        CategoryPosterStatusText.Text = "الغلاف اختياري. القسم الرئيسي سيظهر في الصفحة الأولى بعد النشر.";
        CategoryStatusText.Text = "قسم رئيسي جديد: اكتب الاسم ثم احفظ. لإضافة قسم تحته استخدم «قسم فرعي جديد».";
        UpdateCategoryParentState();
        OpenDedicatedEditor(CategoryEditorExpander, "إضافة قسم رئيسي");
    }

    private void NewSubCategory_Click(object sender, RoutedEventArgs e)
    {
        var selectedCategory = CategoriesGrid.SelectedItem as CategoryAdmin;
        var suggestedParentId = selectedCategory is null
            ? null
            : selectedCategory.ContentKind == "Group" ? selectedCategory.CategoryId : selectedCategory.ParentId;
        _pendingCategoryPosterPath = null;
        CategoriesGrid.UnselectAll();
        CategoryIdBox.Clear();
        CategoryTitleBox.Clear();
        CategoryParentPicker.SelectedValue = suggestedParentId;
        CategoryKindPicker.SelectedItem = CategoryKindPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == "Movie");
        CategoryPosterPathBox.Clear();
        CategoryPublishBox.IsChecked = true;
        CategoryPosterStatusText.Text = "غلاف القسم الفرعي اختياري ويمكن رفعه بعد حفظ القسم.";
        CategoryStatusText.Text = "قسم فرعي جديد: اختر القسم الرئيسي الذي يتبعه، ثم حدد هل يحتوي أفلاماً أم مسلسلات أم محتوى آخر.";
        UpdateCategoryParentState();
        OpenDedicatedEditor(CategoryEditorExpander, "إضافة قسم فرعي");
        CategoryParentPicker.Focus();
    }

    private void CategoryKindPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCategoryParentState();

    private void UpdateCategoryParentState()
    {
        if (CategoryParentPicker is null || CategoryKindPicker is null) return;
        var kind = (CategoryKindPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Group";
        var isMain = kind == "Group";
        CategoryParentPicker.IsEnabled = !isMain;
        if (isMain) CategoryParentPicker.SelectedIndex = -1;
    }

    private void CategoriesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CategoriesGrid.SelectedItem is not CategoryAdmin row) return;
        CategoryIdBox.Text = row.CategoryId;
        CategoryTitleBox.Text = row.Title;
        CategoryParentPicker.SelectedValue = row.ParentId;
        CategoryKindPicker.SelectedItem = CategoryKindPicker.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == row.ContentKind);
        _pendingCategoryPosterPath = null;
        CategoryPosterPathBox.Text = row.PosterPath ?? "";
        CategoryPublishBox.IsChecked = row.IsPublished;
        CategoryPosterStatusText.Text = string.IsNullOrWhiteSpace(row.PosterPath) ? "لا يوجد غلاف. يمكنك نشر القسم بدونه أو رفع غلاف اختياري." : "تم حفظ غلاف هذا القسم.";
        OpenDedicatedEditor(CategoryEditorExpander, "تعديل بيانات القسم");
    }

    private async void SaveCategory_Click(object sender, RoutedEventArgs e)
    {
        var kind = (CategoryKindPicker.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Group";
        var parentId = CategoryParentPicker.SelectedValue as string;
        if (string.IsNullOrWhiteSpace(CategoryTitleBox.Text)) { CategoryStatusText.Text = "اكتب اسم القسم أولاً."; return; }
        if (kind == "Group") parentId = null;
        else if (string.IsNullOrWhiteSpace(parentId)) { CategoryStatusText.Text = "اختر القسم الرئيسي الذي سيندرج تحته هذا القسم."; return; }
        var isNew = string.IsNullOrWhiteSpace(CategoryIdBox.Text);
        var title = CategoryTitleBox.Text.Trim();
        var parentTitle = parentId is null ? null : _categories.FirstOrDefault(x => x.CategoryId == parentId)?.Title;
        var id = isNew ? Guid.NewGuid().ToString("N") : CategoryIdBox.Text.Trim();
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/catalog-categories", new { categoryId = id, parentId, title, contentKind = kind, posterPath = _pendingCategoryPosterPath is not null || string.IsNullOrWhiteSpace(CategoryPosterPathBox.Text) ? null : CategoryPosterPathBox.Text.Trim(), isPublished = CategoryPublishBox.IsChecked == true });
            if (!response.IsSuccessStatusCode) { CategoryStatusText.Text = "تعذر حفظ القسم: " + await response.Content.ReadAsStringAsync(); return; }
            CategoryIdBox.Text = id;
            if (!string.IsNullOrWhiteSpace(_pendingCategoryPosterPath))
            {
                var pendingPoster = _pendingCategoryPosterPath;
                if (!await UploadCategoryPosterFileAsync(id, pendingPoster)) return;
            }
            await LoadCategoriesAsync();
            CategoriesGrid.SelectedItem = (CategoriesGrid.ItemsSource as CategoryAdmin[])?.FirstOrDefault(x => x.CategoryId == id);
            var typeName = kind == "Group" ? "القسم الرئيسي" : "القسم الفرعي";
            var location = parentTitle is null ? "" : $" داخل القسم الرئيسي «{parentTitle}»";
            var action = isNew ? "تمت إضافة" : "تم تحديث";
            CategoryStatusText.Text = $"{action} {typeName} «{title}»{location}. الحالة: {(CategoryPublishBox.IsChecked == true ? "منشور" : "مسودة")}.";
        CloseDedicatedEditor();
        }
        catch (Exception ex) { CategoryStatusText.Text = "تعذر حفظ القسم: " + ex.Message; }
    }

    private void PublishCategory_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CategoryIdBox.Text))
        {
            CategoryStatusText.Text = "اختر القسم الذي تريد نشره أولاً.";
            return;
        }
        CategoryPublishBox.IsChecked = true;
        SaveCategory_Click(sender, e);
    }

    private async void UploadCategoryPoster_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "اختر صورة غلاف القسم",
            Filter = "صور مدعومة|*.jpg;*.jpeg;*.png;*.webp",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var info = new FileInfo(path);
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp") || !info.Exists || info.Length == 0 || info.Length > 5_242_880)
        {
            CategoryStatusText.Text = "اختر صورة JPG أو PNG أو WEBP موجودة ولا يتجاوز حجمها 5 ميجابايت.";
            return;
        }

        if (string.IsNullOrWhiteSpace(CategoryIdBox.Text))
        {
            _pendingCategoryPosterPath = path;
            CategoryPosterPathBox.Text = Path.GetFileName(path) + " — سيتم رفعها بعد حفظ القسم";
            CategoryPosterStatusText.Text = "تم اختيار الصورة. احفظ القسم وسيتم رفع الغلاف تلقائياً.";
            return;
        }

        await UploadCategoryPosterFileAsync(CategoryIdBox.Text.Trim(), path);
        await LoadCategoriesAsync();
    }

    private async System.Threading.Tasks.Task<bool> UploadCategoryPosterFileAsync(string categoryId, string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var contentType = ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", _ => "" };
        try
        {
            CategoryPosterStatusText.Text = "جارٍ رفع صورة الغلاف...";
            using var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(path));
            bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            var response = await _http.PostAsync("/api/admin/catalog-categories/" + Uri.EscapeDataString(categoryId) + "/poster", bytes);
            if (!response.IsSuccessStatusCode)
            {
                CategoryStatusText.Text = "تم حفظ القسم، لكن تعذر رفع الغلاف: " + await response.Content.ReadAsStringAsync();
                return false;
            }

            var result = await response.Content.ReadFromJsonAsync<PosterUploadResult>();
            CategoryPosterPathBox.Text = result?.PosterPath ?? "";
            _pendingCategoryPosterPath = null;
            CategoryPosterStatusText.Text = "تم رفع صورة الغلاف وحفظها بنجاح.";
            return true;
        }
        catch (Exception ex)
        {
            CategoryStatusText.Text = "تعذر رفع غلاف القسم: " + ex.Message;
            return false;
        }
    }

    private async void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CategoryIdBox.Text)) { CategoryStatusText.Text = "اختر قسماً من القائمة أولاً."; return; }
        if (MessageBox.Show("سيتم حذف القسم وأقسامه الفرعية، وفصل الأفلام والمسلسلات المرتبطة بها وإخفاؤها عن العملاء. ستبقى بيانات الأعمال وروابط الحلقات وملفات الفيديو الأصلية محفوظة ويمكن إعادة تصنيفها لاحقاً. هل تريد المتابعة؟", "تأكيد حذف القسم ومحتوياته", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        var response = await _http.DeleteAsync("/api/admin/catalog-categories/" + Uri.EscapeDataString(CategoryIdBox.Text.Trim()));
        if (!response.IsSuccessStatusCode) { CategoryStatusText.Text = "تعذر حذف القسم: " + await response.Content.ReadAsStringAsync(); return; }
        CategoryIdBox.Clear(); CategoryTitleBox.Clear(); CategoryPosterPathBox.Clear();
        await LoadCategoriesAsync();
        await LoadSeriesAsync();
        await LoadVideosAsync();
        await LoadMissingEpisodesAsync();
        CategoryStatusText.Text = "تم حذف القسم وأقسامه الفرعية، وفصل المحتوى المرتبط وإخفاؤه عن العملاء دون حذف بياناته أو ملفات الفيديو الأصلية.";
    }

    private async System.Threading.Tasks.Task LoadVideosAsync()
    {
        try
        {
            StatusText.Text = "جارٍ تحديث القائمة...";
            var items = await _http.GetFromJsonAsync<AdminVideo[]>("/api/library") ?? Array.Empty<AdminVideo>();
            _videos.Clear();
            foreach (var item in items)
            {
                if (string.IsNullOrWhiteSpace(item.CategoryTitle) && !string.IsNullOrWhiteSpace(item.CategoryId))
                    item.CategoryTitle = _categories.FirstOrDefault(x => x.CategoryId == item.CategoryId)?.Title ?? "قسم غير موجود";
                _videos.Add(item);
            }
            EpisodeVideoPicker.ItemsSource = _videos;
            MissingEpisodeVideoPicker.ItemsSource = _videos;
            await RefreshVideoFoldersTreeAsync();
            CountText.Text = $"عدد ملفات الفيديو: {_videos.Count}";
            UpdateDashboardStats();
            await LoadDashboardWatchHistoryAsync();
            var incomplete = _videos.Count(x => !x.MetadataSaved);
            StatusText.Text = incomplete == 0
                ? $"سجل الفيديوهات جاهز: {_videos.Count} فيديو، وجميعها لها بيانات محفوظة."
                : $"سجل الفيديوهات: {_videos.Count} فيديو. يوجد {incomplete} فيديو بانتظار استكمال بياناته وتصنيفه.";
        }
        catch (Exception ex) { StatusText.Text = "تعذر تحميل المكتبة: " + ex.Message; }
    }

    private async System.Threading.Tasks.Task CheckMediaToolsAsync()
    {
        try
        {
            var status = await _http.GetFromJsonAsync<MediaToolStatus>("/api/media-tools");
            if (status is null) return;
            FfmpegStatusText.Text = status.Available
                ? "محرك التحويل جاهز. يمكن تشغيل MKV وAVI واختيار المسار الصوتي. " + status.FfmpegPath
                : status.Message;
        }
        catch (Exception ex)
        {
            FfmpegStatusText.Text = "تعذر فحص محرك التحويل: " + ex.Message;
        }
    }

    private async void VideosGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (VideosGrid.SelectedItem is not AdminVideo v) return;
        _pendingVideoPosterPath = null;
        IdBox.Text = v.Id;
        TitleBox.Text = v.Title ?? "";
        DescriptionBox.Text = v.Description ?? "";
        YearBox.Text = v.Year?.ToString() ?? "";
        GenreBox.Text = v.Genre ?? "";
        DirectorBox.Text = v.Director ?? "";
        CastBox.Text = v.Cast ?? "";
        RatingBox.Text = v.Rating?.ToString() ?? "";
        RuntimeBox.Text = v.RuntimeMinutes?.ToString() ?? "";
        ReleaseDateBox.Text = v.ReleaseDate ?? "";
        TagsBox.Text = v.Tags ?? "";
        SectionBox.Text = v.Section ?? "عام";
        KindBox.SelectedIndex = string.Equals(v.Kind, "Series", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        var selectedVideoCategory = _categories.FirstOrDefault(x => x.CategoryId == v.CategoryId);
        VideoMainCategoryPicker.SelectedValue = selectedVideoCategory?.ParentId;
        RefreshVideoCategoryPicker(v.CategoryId);
        _suppressVideoSeriesPickerEvent = true;
        VideoSeriesPicker.SelectedValue = v.LinkedSeriesId;
        _suppressVideoSeriesPickerEvent = false;
        await LoadVideoSeasonsForLinkAsync(v.LinkedSeriesId, v.LinkedSeasonId);
        VideoEpisodeNumberBox.Text = v.LinkedEpisodeNumber?.ToString() ?? "";
        VideoEpisodeTitleBox.Text = v.LinkedEpisodeTitle ?? "";
        PublishBox.IsChecked = v.IsPublished || !v.MetadataSaved;
        PosterStatusText.Text = string.IsNullOrWhiteSpace(v.PosterPath) ? "لا يوجد غلاف لهذا العمل حالياً." : "يوجد غلاف محفوظ لهذا العمل.";
    }

    private async void VideoSeriesPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressVideoSeriesPickerEvent) return;
        var seriesId = VideoSeriesPicker.SelectedValue as string;
        if (!string.IsNullOrWhiteSpace(seriesId) && VideoSeriesPicker.SelectedItem is SeriesAdmin selectedSeries)
        {
            KindBox.SelectedIndex = 1;
            var category = _categories.FirstOrDefault(x => x.CategoryId == selectedSeries.CategoryId);
            VideoMainCategoryPicker.SelectedValue = category?.ParentId;
            RefreshVideoCategoryPicker(selectedSeries.CategoryId);
        }
        await LoadVideoSeasonsForLinkAsync(seriesId);
    }

    private async System.Threading.Tasks.Task LoadVideoSeasonsForLinkAsync(string? seriesId, long? selectedSeasonId = null)
    {
        VideoSeasonPicker.ItemsSource = null;
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            VideoPlacementStatusText.Text = "اختر مسلسلاً لعرض مواسمه أو أجزائه، أو اتركه فارغاً إذا كان الفيديو فيلماً مستقلاً.";
            return;
        }
        try
        {
            var url = "/api/admin/seasons?seriesId=" + Uri.EscapeDataString(seriesId);
            var rows = await _http.GetFromJsonAsync<SeasonAdmin[]>(url) ?? Array.Empty<SeasonAdmin>();
            var selectedSeries = (VideoSeriesPicker.ItemsSource as SeriesAdmin[])?.FirstOrDefault(x => x.SeriesId == seriesId);
            var label = selectedSeries?.OrganizationType == "Parts" ? "الجزء" : "الموسم";
            foreach (var row in rows) row.DisplayTitle = row.Title.Length > 0 ? $"{label} {row.SeasonNumber} — {row.Title}" : $"{label} {row.SeasonNumber}";
            VideoSeasonPicker.ItemsSource = rows;
            VideoSeasonPicker.SelectedValue = selectedSeasonId;
            VideoPlacementStatusText.Text = rows.Length == 0
                ? "لا توجد مواسم أو أجزاء لهذا المسلسل. يمكنك ربط الفيديو بالمسلسل مباشرة، أو أضف موسماً/جزءاً من إدارة المسلسلات."
                : $"تم تحميل {rows.Length} {label}. اختر واحداً لربط الفيديو به، أو اترك الاختيار فارغاً لربطه بالمسلسل مباشرة.";
        }
        catch (Exception ex) { VideoPlacementStatusText.Text = "تعذر تحميل مواسم/أجزاء المسلسل: " + ex.Message; }
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
        double? rating = null;
        if (!string.IsNullOrWhiteSpace(RatingBox.Text))
        {
            if (!double.TryParse(RatingBox.Text, out var parsedRating) || parsedRating < 0 || parsedRating > 10)
            { StatusText.Text = "التقييم يجب أن يكون رقماً من 0 إلى 10."; return; }
            rating = parsedRating;
        }
        int? runtime = null;
        if (!string.IsNullOrWhiteSpace(RuntimeBox.Text))
        {
            if (!int.TryParse(RuntimeBox.Text, out var parsedRuntime) || parsedRuntime < 1 || parsedRuntime > 1000)
            { StatusText.Text = "المدة يجب أن تكون بالدقائق من 1 إلى 1000."; return; }
            runtime = parsedRuntime;
        }
        var kindItem = KindBox.SelectedItem as ComboBoxItem;
        var previousMetadata = _videos.FirstOrDefault(v => v.Id == IdBox.Text);
        var isNewMetadata = previousMetadata?.MetadataSaved != true;
        var selectedCategory = VideoCategoryPicker.SelectedItem as CategoryAdmin;
        if (PublishBox.IsChecked == true && selectedCategory is null)
        {
            StatusText.Text = "لا يمكن نشر الفيديو دون قسم فرعي. إذا كان تابعاً لمسلسل، اختر المسلسل أولاً ليتم تحديد قسمه تلقائياً.";
            VideoMainCategoryPicker.Focus();
            return;
        }
        int? linkedEpisodeNumber = null;
        if (VideoSeasonPicker.SelectedValue is not null)
        {
            if (!int.TryParse(VideoEpisodeNumberBox.Text, out var parsedEpisode) || parsedEpisode < 1 || parsedEpisode > 10000)
            { StatusText.Text = "أدخل رقم الحلقة أو الفيديو داخل الموسم/الجزء."; return; }
            linkedEpisodeNumber = parsedEpisode;
        }
        var request = new
        {
            videoId = IdBox.Text,
            title = TitleBox.Text,
            description = DescriptionBox.Text,
            year,
            genre = GenreBox.Text,
            kind = kindItem?.Tag?.ToString() ?? "Movie",
            posterPath = _pendingVideoPosterPath,
            director = DirectorBox.Text,
            cast = CastBox.Text,
            rating,
            runtimeMinutes = runtime,
            releaseDate = string.IsNullOrWhiteSpace(ReleaseDateBox.Text) ? null : ReleaseDateBox.Text.Trim(),
            tags = TagsBox.Text,
            section = string.IsNullOrWhiteSpace(SectionBox.Text) ? "عام" : SectionBox.Text.Trim(),
            categoryId = VideoCategoryPicker.SelectedValue as string,
            isPublished = PublishBox.IsChecked == true,
            seriesId = (VideoSeriesPicker.SelectedValue as string),
            seasonId = VideoSeasonPicker.SelectedValue is long seasonId ? seasonId : (long?)null,
            episodeNumber = linkedEpisodeNumber,
            episodeTitle = string.IsNullOrWhiteSpace(VideoEpisodeTitleBox.Text) ? TitleBox.Text.Trim() : VideoEpisodeTitleBox.Text.Trim()
        };
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/metadata", request);
            if (!response.IsSuccessStatusCode) { StatusText.Text = "لم يتم الحفظ: " + await response.Content.ReadAsStringAsync(); return; }
            var videoTitle = TitleBox.Text.Trim();
            var categoryName = selectedCategory?.Title ?? "غير مصنف";
            var publication = PublishBox.IsChecked == true ? "منشور للعميل" : "محفوظ كمسودة";
            StatusText.Text = $"{(isNewMetadata ? "تمت إضافة بيانات الفيديو" : "تم تحديث بيانات الفيديو")} «{videoTitle}» إلى سجل الفيديوهات. القسم: {categoryName}. الحالة: {publication}.";
            _pendingVideoPosterPath = null;
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
            foreach (var row in rows)
            {
                row.CategoryTitle = _categories.FirstOrDefault(x => x.CategoryId == row.CategoryId)?.Title ?? "غير مصنف";
                row.PublishStatus = row.IsPublished ? "منشور" : "مسودة";
            }
            _dashboardSeriesCount = rows.Length;
            UpdateDashboardStats();
            SeriesPicker.ItemsSource = rows;
            var linkedSeriesId = VideoSeriesPicker.SelectedValue as string;
            VideoSeriesPicker.ItemsSource = rows;
            VideoSeriesPicker.SelectedValue = linkedSeriesId;
            SeriesPicker.SelectedItem = rows.FirstOrDefault(x => x.SeriesId == selectedId);
            if (SeriesPicker.SelectedItem is null)
            {
                SeasonsGrid.ItemsSource = null;
                EpisodesGrid.ItemsSource = null;
                if (rows.Length == 0) SeriesStatusText.Text = "سجل المسلسلات فارغ. أضف مسلسلاً جديداً أو أنشئه من فيديو موجود.";
            }
            else
            {
                SeriesStatusText.Text = $"سجل المسلسلات: {rows.Length} مسلسل. اختر أي مسلسل من الجدول لتعديل بياناته.";
            }
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر تحميل المسلسلات: " + ex.Message; }
    }

    private void SeriesStructureBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSeriesStructureLabels();

    private void UpdateSeriesStructureLabels()
    {
        if (SeriesStructureBox is null || SeasonSectionTitle is null) return;
        var type = (SeriesStructureBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Seasons";
        var isParts = type == "Parts";
        var isNone = type == "None";
        SeasonManagementPanel.Visibility = isNone ? Visibility.Collapsed : Visibility.Visible;
        SeasonSectionTitle.Text = isParts ? "الأجزاء" : "المواسم";
        AddSeasonButton.Content = isParts ? "إضافة الجزء" : "إضافة الموسم";
        if (SeasonNumberBox is not null) SeasonNumberBox.ToolTip = isParts ? "رقم الجزء" : "رقم الموسم";
        if (SeasonTitleBox is not null) SeasonTitleBox.ToolTip = isParts ? "اسم الجزء (اختياري)" : "اسم الموسم (اختياري)";
        if (SeasonsGrid.Columns.Count >= 2) { SeasonsGrid.Columns[0].Header = isParts ? "رقم الجزء" : "رقم الموسم"; SeasonsGrid.Columns[1].Header = isParts ? "اسم الجزء" : "اسم الموسم"; }
        if (MissingEpisodesGrid.Columns.Count >= 2) MissingEpisodesGrid.Columns[1].Header = isParts ? "الجزء" : "الموسم";
        SeasonFolderButton.Content = isParts ? "مجلد الجزء" : "مجلد الموسم";
        SaveSeasonFolderButton.Content = isParts ? "حفظ مسار الجزء وفحص ملفاته" : "حفظ مسار الموسم المحدد وفحص ملفاته";
        DeleteSeasonButton.Content = isParts ? "حذف الجزء المحدد" : "حذف الموسم المحدد";
        EpisodesSectionTitle.Text = isParts ? "حلقات الجزء المحدد" : "حلقات الموسم المحدد";
        PreviewSeasonFolderButton.Content = isParts ? "اكتشاف حلقات الجزء" : "اكتشاف حلقات الموسم";
        AddEpisodeSectionTitle.Text = isParts ? "إضافة حلقة إلى الجزء المحدد" : "إضافة حلقة إلى الموسم المحدد";
        VideoPlacementStatusText.Text = isNone
            ? "هذا البرنامج لا يستخدم مواسم أو أجزاء؛ اربط الفيديوهات به مباشرة من بيانات الفيديو."
            : isParts ? "يمكنك إضافة أجزاء متعددة، ثم ربط كل فيديو بالجزء ورقم الحلقة المناسب." : "يمكنك إضافة مواسم متعددة، ثم ربط كل فيديو بالموسم ورقم الحلقة المناسب.";
    }

    private void NewSeries_Click(object sender, RoutedEventArgs e)
    {
        SeriesPicker.SelectedIndex = -1;
        _pendingSeriesPosterPath = null;
        SeriesIdBox.Clear();
        SeriesTitleBox.Clear();
        SeriesDescriptionBox.Clear();
        SeriesYearBox.Clear();
        SeriesGenreBox.Clear();
        SeriesSectionBox.Text = "عام";
        SeriesStructureBox.SelectedIndex = 1;
        UpdateSeriesStructureLabels();
        var defaultParent = _categories.FirstOrDefault(x => x.ParentId is null && x.ContentKind == "Group" && x.IsPublished);
        SeriesMainCategoryPicker.SelectedValue = defaultParent?.CategoryId;
        RefreshSeriesCategoryPicker();
        SeriesCategoryPicker.SelectedIndex = -1;
        SeriesPublishBox.IsChecked = true;
        SeriesPosterPathBox.Clear();
        UpdateSeriesPosterPreview(null);
        SeriesFolderPathBox.Clear();
        SeasonsGrid.ItemsSource = null;
        EpisodesGrid.ItemsSource = null;
        SeasonTitleBox.Clear();
        SeasonNumberBox.Text = "1";
        SeasonFolderPathBox.Clear();
        EpisodeVideoPicker.SelectedIndex = -1;
        EpisodeTitleBox.Clear();
        EpisodeNumberBox.Text = "1";
        _episodeCandidates.Clear();
        MissingEpisodesGrid.SelectedIndex = -1;
        SeriesStatusText.Text = "نموذج مسلسل جديد. أدخل بياناته واحفظه أولاً.";
        OpenDedicatedEditor(SeriesEditorExpander, "إضافة مسلسل جديد");
    }

    private async void CreateSeriesFromVideo_Click(object sender, RoutedEventArgs e)
    {
        if (VideosGrid.SelectedItem is not AdminVideo video)
        {
            SeriesStatusText.Text = "حدد ملف الفيديو أولاً من قائمة ملفات المكتبة.";
            return;
        }
        if (!string.Equals(video.Kind, "Series", StringComparison.OrdinalIgnoreCase))
        {
            SeriesStatusText.Text = "هذا الملف مصنف كفيلم. غيّر نوع المحتوى إلى «مسلسل» واحفظه أولاً، ثم أنشئ المسلسل منه.";
            return;
        }
        var id = "series-" + video.Id;
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/series", new
            {
                seriesId = id,
                title = video.Title ?? video.Id,
                description = video.Description ?? "",
                year = video.Year,
                genre = video.Genre ?? "",
                posterPath = video.PosterPath,
                folderPath = (string?)null,
                categoryId = SeriesCategoryPicker.SelectedValue as string,
                section = video.Section,
                isPublished = video.IsPublished
            });
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذر إنشاء المسلسل من الملف المحدد: " + await response.Content.ReadAsStringAsync();
                return;
            }
            await LoadSeriesAsync();
            SeriesPicker.SelectedItem = (SeriesPicker.ItemsSource as SeriesAdmin[])?.FirstOrDefault(x => x.SeriesId == id);
            SeriesStatusText.Text = "تم إنشاء سجل المسلسل. اختر مجلد الموسم ثم اكتشف الحلقات واربط الملفات المناسبة.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر إنشاء المسلسل: " + ex.Message; }
    }

    private async void SeriesPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SeriesPicker.SelectedItem is not SeriesAdmin s) return;
        _pendingSeriesPosterPath = null;
        SeriesIdBox.Text = s.SeriesId;
        SeriesTitleBox.Text = s.Title ?? "";
        SeriesDescriptionBox.Text = s.Description ?? "";
        SeriesYearBox.Text = s.Year?.ToString() ?? "";
        SeriesGenreBox.Text = s.Genre ?? "";
        SeriesSectionBox.Text = s.Section ?? "عام";
        var selectedSeriesCategory = _categories.FirstOrDefault(x => x.CategoryId == s.CategoryId);
        SeriesMainCategoryPicker.SelectedValue = selectedSeriesCategory?.ParentId;
        RefreshSeriesCategoryPicker(s.CategoryId);
        SeriesPublishBox.IsChecked = s.IsPublished;
        SeriesPosterPathBox.Text = s.PosterPath ?? "";
        UpdateSeriesPosterPreview(s.PosterPath);
        SeriesFolderPathBox.Text = s.FolderPath ?? "";
        SeriesStructureBox.SelectedItem = SeriesStructureBox.Items.OfType<ComboBoxItem>().FirstOrDefault(x => x.Tag?.ToString() == s.OrganizationType);
        UpdateSeriesStructureLabels();
        await LoadSeasonsAsync(s.SeriesId);
        OpenDedicatedEditor(SeriesEditorExpander, "تعديل بيانات المسلسل");
    }

    private async System.Threading.Tasks.Task LoadSeasonsAsync(string seriesId)
    {
        try
        {
            var selectedSeasonId = (SeasonsGrid.SelectedItem as SeasonAdmin)?.SeasonId;
            var url = "/api/admin/seasons?seriesId=" + Uri.EscapeDataString(seriesId);
            var rows = await _http.GetFromJsonAsync<SeasonAdmin[]>(url) ?? Array.Empty<SeasonAdmin>();
            SeasonsGrid.ItemsSource = rows;
            var selected = rows.FirstOrDefault(x => x.SeasonId == selectedSeasonId && x.SeriesId == seriesId);
            if (selected is not null)
            {
                SeasonsGrid.SelectedItem = selected;
                await LoadEpisodesAsync(selected.SeasonId);
            }
            else
            {
                EpisodesGrid.ItemsSource = null;
            }
            SeriesStatusText.Text = $"تم تحميل {rows.Length} موسم/مواسم.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر تحميل المواسم: " + ex.Message; }
    }

    private void UpdateSeriesPosterPreview(string? path, bool isLocalFile = false)
    {
        if (SeriesPosterPreview is null) return;
        SeriesPosterPreview.Source = null;
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            Uri uri;
            if (isLocalFile)
                uri = new Uri(Path.GetFullPath(path), UriKind.Absolute);
            else if (Uri.TryCreate(path, UriKind.Absolute, out var absolute) &&
                     (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
                uri = absolute;
            else
                uri = new Uri(_http.BaseAddress!, path.TrimStart('/'));

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = uri;
            image.EndInit();
            image.Freeze();
            SeriesPosterPreview.Source = image;
        }
        catch
        {
            // Preview errors must not prevent the series metadata from being saved.
        }
    }

    private async void UploadSeriesPoster_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "اختر غلاف المسلسل",
            Filter = "صور مدعومة|*.jpg;*.jpeg;*.png;*.webp",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        if (!ValidateSeriesPoster(path, out var error))
        {
            SeriesStatusText.Text = error;
            return;
        }

        UpdateSeriesPosterPreview(path, isLocalFile: true);

        if (string.IsNullOrWhiteSpace(SeriesIdBox.Text))
        {
            _pendingSeriesPosterPath = path;
            SeriesPosterPathBox.Text = Path.GetFileName(path) + " — بانتظار حفظ المسلسل";
            SeriesStatusText.Text = "تم اختيار الغلاف. احفظ بيانات المسلسل الآن وسيتم رفع الصورة تلقائياً دون الحاجة لاختيارها مرة أخرى.";
            return;
        }

        await UploadSeriesPosterFileAsync(SeriesIdBox.Text.Trim(), path);
    }

    private static bool ValidateSeriesPoster(string path, out string error)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var info = new FileInfo(path);
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp") || !info.Exists || info.Length == 0 || info.Length > 5_242_880)
        {
            error = "اختر صورة JPG أو PNG أو WEBP موجودة ولا يتجاوز حجمها 5 ميجابايت.";
            return false;
        }
        error = "";
        return true;
    }

    private async System.Threading.Tasks.Task UploadSeriesPosterFileAsync(string seriesId, string path)
    {
        if (!ValidateSeriesPoster(path, out var error))
        {
            SeriesStatusText.Text = error;
            return;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        var contentType = ext switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", _ => "" };
        try
        {
            SeriesStatusText.Text = "جارٍ رفع غلاف المسلسل وحفظه...";
            using var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(path));
            bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            var response = await _http.PostAsync("/api/admin/series-poster/" + Uri.EscapeDataString(seriesId), bytes);
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تم حفظ بيانات المسلسل، لكن تعذر رفع الغلاف: " + await response.Content.ReadAsStringAsync();
                return;
            }

            var result = await response.Content.ReadFromJsonAsync<PosterUploadResult>();
            SeriesPosterPathBox.Text = result?.PosterPath ?? "";
            UpdateSeriesPosterPreview(result?.PosterPath);
            _pendingSeriesPosterPath = null;
            SeriesStatusText.Text = "تم رفع غلاف المسلسل وحفظه بنجاح.";
            await LoadSeriesAsync();
            SeriesPicker.SelectedItem = (SeriesPicker.ItemsSource as SeriesAdmin[])?.FirstOrDefault(x => x.SeriesId == seriesId);
        }
        catch (Exception ex)
        {
            SeriesStatusText.Text = "تعذر رفع غلاف المسلسل: " + ex.Message;
        }
    }

    private async void LookupSeriesMetadata_Click(object sender, RoutedEventArgs e)
    {
        var query = SeriesTitleBox.Text.Trim();
        if (query.Length < 2)
        {
            SeriesMetadataLookupStatusText.Text = "اكتب اسم المسلسل أولاً.";
            return;
        }
        try
        {
            SeriesMetadataLookupStatusText.Text = "جارٍ البحث عن بيانات المسلسل والغلاف...";
            var url = "/api/admin/metadata/lookup?query=" + Uri.EscapeDataString(query) + "&kind=Series";
            var result = await _http.GetFromJsonAsync<MetadataLookupAdmin>(url);
            if (result is null)
            {
                SeriesMetadataLookupStatusText.Text = "لم يتم العثور على بيانات مطابقة.";
                return;
            }
            SeriesTitleBox.Text = result.Title ?? query;
            SeriesDescriptionBox.Text = result.Description ?? "";
            SeriesYearBox.Text = result.Year?.ToString() ?? "";
            SeriesGenreBox.Text = result.Genre ?? "";
            _pendingSeriesPosterPath = null;
            SeriesPosterPathBox.Text = result.PosterPath ?? "";
            UpdateSeriesPosterPreview(result.PosterPath);
            SeriesMetadataLookupStatusText.Text = "تم جلب البيانات من " + (result.Provider ?? "TVmaze") +
                (string.IsNullOrWhiteSpace(result.PosterPath) ? " دون غلاف متاح." : " وتم حفظ الغلاف محلياً. راجع البيانات ثم احفظ المسلسل.");
        }
        catch (Exception ex)
        {
            SeriesMetadataLookupStatusText.Text = "تعذر جلب بيانات المسلسل: " + ex.Message;
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
        var categoryId = SeriesCategoryPicker.SelectedValue as string;
        if (SeriesPublishBox.IsChecked == true && string.IsNullOrWhiteSpace(categoryId))
        {
            SeriesStatusText.Text = "اختر القسم الفرعي الذي سيظهر فيه المسلسل، أو احفظه كمسودة حتى تحدد القسم لاحقاً.";
            return;
        }
        var id = string.IsNullOrWhiteSpace(SeriesIdBox.Text) ? "series-" + Guid.NewGuid().ToString("N") : SeriesIdBox.Text.Trim();
        var folderPath = string.IsNullOrWhiteSpace(SeriesFolderPathBox.Text) ? null : Path.GetFullPath(SeriesFolderPathBox.Text.Trim());
        if (folderPath is not null && !Directory.Exists(folderPath)) { SeriesStatusText.Text = "مجلد المسلسل المحدد غير موجود."; return; }
        var request = new { seriesId = id, title = SeriesTitleBox.Text.Trim(), description = SeriesDescriptionBox.Text, year, genre = SeriesGenreBox.Text, posterPath = _pendingSeriesPosterPath is not null ? null : (string.IsNullOrWhiteSpace(SeriesPosterPathBox.Text) ? null : SeriesPosterPathBox.Text.Trim()), folderPath, section = string.IsNullOrWhiteSpace(SeriesSectionBox.Text) ? "عام" : SeriesSectionBox.Text.Trim(), categoryId, isPublished = SeriesPublishBox.IsChecked == true, organizationType = (SeriesStructureBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Seasons" };
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/series", request);
            if (!response.IsSuccessStatusCode) { SeriesStatusText.Text = "لم يتم حفظ المسلسل: " + await response.Content.ReadAsStringAsync(); return; }
            var wasNewSeries = !((SeriesPicker.ItemsSource as SeriesAdmin[]) ?? Array.Empty<SeriesAdmin>()).Any(x => x.SeriesId == id);
            SeriesIdBox.Text = id;
            string? posterUploadError = null;
            if (!string.IsNullOrWhiteSpace(_pendingSeriesPosterPath))
            {
                await UploadSeriesPosterFileAsync(id, _pendingSeriesPosterPath);
                if (!string.IsNullOrWhiteSpace(_pendingSeriesPosterPath))
                    posterUploadError = SeriesStatusText.Text;
            }
            var seriesCategoryName = (SeriesCategoryPicker.SelectedItem as CategoryAdmin)?.Title ?? "غير مصنف";
            var saveMessage = posterUploadError ?? $"{(wasNewSeries ? "تمت إضافة المسلسل" : "تم تحديث المسلسل")} «{SeriesTitleBox.Text.Trim()}» إلى سجل المسلسلات. القسم: {seriesCategoryName}. الحالة: {(SeriesPublishBox.IsChecked == true ? "منشور" : "مسودة")}.";
            await LoadSeriesAsync();
            SeriesPicker.SelectedItem = (SeriesPicker.ItemsSource as SeriesAdmin[])?.FirstOrDefault(x => x.SeriesId == id);
            if (SeriesPicker.SelectedItem is null) await LoadSeasonsAsync(id);
            await LoadVideosAsync();
            SeriesStatusText.Text = saveMessage;
        CloseDedicatedEditor();
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر حفظ المسلسل: " + ex.Message; }
    }

    private async void DeleteSeries_Click(object sender, RoutedEventArgs e)
    {
        if (SeriesPicker.SelectedItem is not SeriesAdmin series)
        {
            SeriesStatusText.Text = "اختر المسلسل الذي تريد حذفه أولاً.";
            return;
        }
        var confirm = MessageBox.Show(
            $"سيتم حذف بيانات «{series.Title}» وجميع مواسمه وروابط حلقاته فقط. لن تُحذف ملفات الفيديو الأصلية من القرص.\n\nهل تريد المتابعة؟",
            "حذف بيانات المسلسل", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var response = await _http.DeleteAsync("/api/admin/series/" + Uri.EscapeDataString(series.SeriesId));
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذر حذف المسلسل: " + await response.Content.ReadAsStringAsync();
                return;
            }
            SeriesPicker.SelectedItem = null;
            SeriesIdBox.Clear(); SeriesTitleBox.Clear(); SeriesDescriptionBox.Clear(); SeriesYearBox.Clear();
            SeriesGenreBox.Clear(); SeriesPosterPathBox.Clear(); SeriesFolderPathBox.Clear();
            SeasonsGrid.ItemsSource = null;
            EpisodesGrid.ItemsSource = null;
            await LoadSeriesAsync();
            await LoadVideosAsync();
            await LoadMissingEpisodesAsync();
            SeriesStatusText.Text = "تم حذف بيانات المسلسل ومواسمه، وملفات الفيديو الأصلية ما زالت محفوظة.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر حذف المسلسل: " + ex.Message; }
    }

    private async void DeleteSeason_Click(object sender, RoutedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is not SeasonAdmin season)
        {
            SeriesStatusText.Text = "اختر الموسم الذي تريد حذفه أولاً.";
            return;
        }
        var confirm = MessageBox.Show(
            $"سيتم حذف الموسم {season.SeasonNumber} وروابط حلقاته من بيانات المسلسل فقط، ولن تُحذف ملفات الفيديو الأصلية.\n\nهل تريد المتابعة؟",
            "حذف الموسم", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        try
        {
            var response = await _http.DeleteAsync("/api/admin/seasons/" + season.SeasonId);
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذر حذف الموسم: " + await response.Content.ReadAsStringAsync();
                return;
            }
            await LoadSeasonsAsync(season.SeriesId);
            await LoadMissingEpisodesAsync();
            SeriesStatusText.Text = "تم حذف الموسم وروابط حلقاته، وملفات الفيديو الأصلية لم تُمس.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر حذف الموسم: " + ex.Message; }
    }

    private async void AddSeason_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SeriesIdBox.Text)) { SeriesStatusText.Text = "احفظ المسلسل أولاً."; return; }
        if (!int.TryParse(SeasonNumberBox.Text, out var number) || number < 1 || number > 500)
        { SeriesStatusText.Text = "رقم الموسم يجب أن يكون بين 1 و500."; return; }
        try
        {
            var seasonFolder = string.IsNullOrWhiteSpace(SeasonFolderPathBox.Text) ? null : Path.GetFullPath(SeasonFolderPathBox.Text.Trim());
            if (seasonFolder is not null && !Directory.Exists(seasonFolder)) { SeriesStatusText.Text = "مجلد الموسم المحدد غير موجود."; return; }
            var response = await _http.PostAsJsonAsync("/api/admin/seasons", new { seriesId = SeriesIdBox.Text.Trim(), seasonNumber = number, title = SeasonTitleBox.Text, folderPath = seasonFolder });
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                SeriesStatusText.Text = "لم تتم إضافة الموسم: " + detail;
                return;
            }
            var seriesId = SeriesIdBox.Text.Trim();
            SeasonTitleBox.Clear();
            SeasonFolderPathBox.Clear();
            SeasonNumberBox.Text = (number + 1).ToString();
            await LoadSeasonsAsync(seriesId);
            var addedSeason = (SeasonsGrid.ItemsSource as SeasonAdmin[])?.FirstOrDefault(x => x.SeasonNumber == number);
            if (addedSeason is not null) SeasonsGrid.SelectedItem = addedSeason;
            await LoadVideosAsync();
            SeriesStatusText.Text = $"تمت إضافة {( (SeriesStructureBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() == "Parts" ? "الجزء" : "الموسم")} رقم {number} بنجاح. أصبح محدداً الآن ويمكنك إضافة الحلقات إليه.";
            CloseDedicatedEditor();
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر إضافة الموسم: " + ex.Message; }
    }

    private async void SeasonsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is SeasonAdmin season)
        {
            SeasonFolderPathBox.Text = season.FolderPath ?? "";
            EpisodesGrid.ItemsSource = null;
            await LoadEpisodesAsync(season.SeasonId);
            SeriesStatusText.Text = $"الموسم المحدد: {season.SeasonNumber} — يمكنك إضافة الحلقات أو تعديلها أو فك ربطها.";
        }
        else
        {
            EpisodesGrid.ItemsSource = null;
        }
    }

    private async System.Threading.Tasks.Task LoadEpisodesAsync(long seasonId)
    {
        try
        {
            var rows = await _http.GetFromJsonAsync<EpisodeAdmin[]>(
                "/api/admin/seasons/" + seasonId + "/episodes") ?? Array.Empty<EpisodeAdmin>();
            EpisodesGrid.ItemsSource = rows;
        }
        catch (Exception ex)
        {
            EpisodesGrid.ItemsSource = null;
            SeriesStatusText.Text = "تعذر تحميل حلقات الموسم: " + ex.Message;
        }
    }

    private void EpisodesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EpisodesGrid.SelectedItem is not EpisodeAdmin episode) return;
        EpisodeNumberBox.Text = episode.EpisodeNumber.ToString();
        EpisodeTitleBox.Text = episode.Title;
        EpisodePosterStatusText.Text = string.IsNullOrWhiteSpace(episode.PosterPath)
            ? "لا توجد صورة خاصة؛ سيظهر غلاف المسلسل تلقائياً."
            : "للحلقة صورة خاصة محفوظة. يمكنك اختيار صورة أخرى لتحديثها.";
        SeriesStatusText.Text = $"تم تحديد الحلقة رقم {episode.EpisodeNumber}. عدّل الرقم أو الاسم ثم اضغط حفظ التعديل.";
        UpdateEpisodeInEditorButton.Visibility = Visibility.Visible;
        OpenDedicatedEditor(EpisodeEditorExpander, "تعديل بيانات الحلقة");
    }

    private async void UploadEpisodePoster_Click(object sender, RoutedEventArgs e)
    {
        if (EpisodesGrid.SelectedItem is not EpisodeAdmin episode)
        {
            EpisodePosterStatusText.Text = "حدد الحلقة من جدول حلقات الموسم أولاً، ثم اختر صورتها.";
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "اختر صورة خاصة للحلقة",
            Filter = "صور مدعومة|*.jpg;*.jpeg;*.png;*.webp",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!ValidateSeriesPoster(dialog.FileName, out var error))
        {
            EpisodePosterStatusText.Text = error;
            return;
        }

        var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        var contentType = ext switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => ""
        };
        try
        {
            EpisodePosterStatusText.Text = "جارٍ رفع صورة الحلقة...";
            using var bytes = new ByteArrayContent(await File.ReadAllBytesAsync(dialog.FileName));
            bytes.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            var response = await _http.PostAsync("/api/admin/episode-poster/" + episode.EpisodeId, bytes);
            if (!response.IsSuccessStatusCode)
            {
                EpisodePosterStatusText.Text = "تعذر حفظ صورة الحلقة: " + await response.Content.ReadAsStringAsync();
                return;
            }

            EpisodePosterStatusText.Text = "تم حفظ الصورة الخاصة للحلقة. ستظهر بدلاً من غلاف المسلسل.";
            await LoadEpisodesAsync(episode.SeasonId);
            SeriesStatusText.Text = "تم تحديث صورة الحلقة بنجاح.";
        }
        catch (Exception ex)
        {
            EpisodePosterStatusText.Text = "تعذر رفع صورة الحلقة: " + ex.Message;
        }
    }

    private async void UpdateEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (EpisodesGrid.SelectedItem is not EpisodeAdmin episode)
        {
            SeriesStatusText.Text = "حدد الحلقة المراد تعديلها من جدول حلقات الموسم.";
            return;
        }
        if (!int.TryParse(EpisodeNumberBox.Text, out var number) || number < 1 || number > 10000)
        {
            SeriesStatusText.Text = "رقم الحلقة يجب أن يكون بين 1 و10000.";
            return;
        }
        try
        {
            var response = await _http.PutAsJsonAsync(
                "/api/admin/episodes/" + episode.EpisodeId,
                new { episodeNumber = number, title = EpisodeTitleBox.Text });
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                SeriesStatusText.Text = response.StatusCode == System.Net.HttpStatusCode.Conflict
                    ? "رقم الحلقة مستخدم في هذا الموسم بالفعل."
                    : "تعذر تعديل الحلقة: " + detail;
                return;
            }
            await LoadEpisodesAsync(episode.SeasonId);
            SeriesStatusText.Text = $"تم تحديث بيانات الحلقة رقم {number} دون تغيير ملف الفيديو.";
            CloseDedicatedEditor();
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر تعديل الحلقة: " + ex.Message; }
    }

    private async void UnlinkEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (EpisodesGrid.SelectedItem is not EpisodeAdmin episode)
        {
            SeriesStatusText.Text = "حدد الحلقة المراد فك ربطها أولاً.";
            return;
        }
        if (MessageBox.Show(
            $"سيتم فك ربط الحلقة «{episode.Title}» من الموسم فقط. لن يُحذف ملف الفيديو أو بياناته من المكتبة. هل تريد المتابعة؟",
            "فك ربط الحلقة", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var response = await _http.DeleteAsync("/api/admin/episodes/" + episode.EpisodeId);
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذر فك ربط الحلقة: " + await response.Content.ReadAsStringAsync();
                return;
            }
            await LoadEpisodesAsync(episode.SeasonId);
            await LoadVideosAsync();
            await LoadMissingEpisodesAsync();
            SeriesStatusText.Text = "تم فك ربط الحلقة فقط. ملف الفيديو الأصلي ما زال محفوظاً في المكتبة.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر فك ربط الحلقة: " + ex.Message; }
    }

    private async void SaveSeasonFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is not SeasonAdmin season)
        {
            SeriesStatusText.Text = "اختر الموسم الذي تريد تحديث مساره أولاً.";
            return;
        }

        var folder = string.IsNullOrWhiteSpace(SeasonFolderPathBox.Text)
            ? null
            : Path.GetFullPath(SeasonFolderPathBox.Text.Trim());
        if (folder is not null && !Directory.Exists(folder))
        {
            SeriesStatusText.Text = "مجلد الموسم المحدد غير موجود.";
            return;
        }

        try
        {
            SeriesStatusText.Text = "جارٍ حفظ المسار وفحص ملفات الفيديو...";
            var response = await _http.PostAsJsonAsync(
                "/api/admin/seasons/" + season.SeasonId + "/folder",
                new { folderPath = folder });
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذر حفظ مسار الموسم. تحقق من المجلد المحدد.";
                return;
            }

            await LoadSeasonsAsync(season.SeriesId);
            SeriesStatusText.Text = folder is null
                ? "تم مسح مسار الموسم. لم يتم حذف أي ملفات من القرص أو المكتبة."
                : "تم حفظ مجلد الموسم. لم تُضف ملفاته إلى المكتبة تلقائياً؛ اضغط «اكتشاف حلقات الموسم» لمراجعة الملفات ثم اختر الحلقات التي تريد ربطها.";
        }
        catch (Exception ex)
        {
            SeriesStatusText.Text = "تعذر حفظ مجلد الموسم: " + ex.Message;
        }
    }

    private async void PreviewSeasonFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is not SeasonAdmin season)
        {
            SeriesStatusText.Text = "اختر الموسم أولاً.";
            return;
        }
        try
        {
            SeriesStatusText.Text = "جارٍ فحص أسماء ملفات الموسم...";
            var rows = await _http.GetFromJsonAsync<EpisodeCandidate[]>(
                "/api/admin/seasons/" + season.SeasonId + "/preview") ?? Array.Empty<EpisodeCandidate>();
            _episodeCandidates.Clear();
            foreach (var row in rows)
            {
                row.Status = row.IsAlreadyLinked ? "مربوط مسبقاً"
                    : row.EpisodeNumber is null or <= 0 ? "لم يتم التعرف على الرقم"
                    : row.IsNumberUsed ? "رقم مكرر"
                    : "جاهز للمراجعة";
                row.Selected = false;
                _episodeCandidates.Add(row);
            }
            SeriesStatusText.Text = $"تم العثور على {rows.Length} ملفاً. راجع أرقام الحلقات قبل اعتماد الربط.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر فحص الموسم: " + ex.Message; }
    }

    private void SelectAllEpisodeCandidates_Click(object sender, RoutedEventArgs e)
    {
        EpisodeCandidatesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        EpisodeCandidatesGrid.CommitEdit(DataGridEditingUnit.Row, true);

        // Select every file that is not already linked. Do not silently skip
        // files whose episode number still needs manual correction.
        foreach (var row in _episodeCandidates)
            row.Selected = !row.IsAlreadyLinked;

        EpisodeCandidatesGrid.Items.Refresh();
        var selectedCount = _episodeCandidates.Count(x => x.Selected);
        var needsReview = _episodeCandidates.Count(x => x.Selected &&
            (x.EpisodeNumber is null or < 1 or > 10000 || x.IsNumberUsed));
        EpisodeSelectionSummaryText.Text = needsReview == 0
            ? $"تم تحديد {selectedCount} ملف حلقة غير مربوط."
            : $"تم تحديد {selectedCount} ملفاً؛ منها {needsReview} يحتاج مراجعة رقم الحلقة قبل الربط.";
    }

    private void ClearEpisodeCandidatesSelection_Click(object sender, RoutedEventArgs e)
    {
        EpisodeCandidatesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        EpisodeCandidatesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        foreach (var row in _episodeCandidates) row.Selected = false;
        EpisodeCandidatesGrid.Items.Refresh();
        EpisodeSelectionSummaryText.Text = "تم إلغاء تحديد جميع الحلقات. اختر ما تريد ربطه يدوياً.";
    }

    private async void LinkDetectedEpisodes_Click(object sender, RoutedEventArgs e)
    {
        if (SeasonsGrid.SelectedItem is not SeasonAdmin season)
        {
            SeriesStatusText.Text = "اختر الموسم أولاً.";
            return;
        }
        EpisodeCandidatesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        EpisodeCandidatesGrid.CommitEdit(DataGridEditingUnit.Row, true);
        var selected = _episodeCandidates.Where(x => x.Selected).ToArray();
        if (selected.Length == 0)
        {
            SeriesStatusText.Text = "حدد ملفاً واحداً على الأقل للربط.";
            return;
        }
        if (selected.Any(x => x.EpisodeNumber is null or < 1 or > 10000))
        {
            SeriesStatusText.Text = "صحح أرقام الحلقات المحددة أولاً.";
            return;
        }
        if (MessageBox.Show($"سيتم ربط {selected.Length} ملفاً بالموسم رقم {season.SeasonNumber}. هل راجعت الأرقام؟",
            "تأكيد ربط الحلقات", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        try
        {
            var payload = new
            {
                episodes = selected.Select(x => new
                {
                    videoId = x.VideoId,
                    filePath = x.FilePath,
                    episodeNumber = x.EpisodeNumber,
                    title = x.Title,
                    selected = true
                }).ToArray()
            };
            var response = await _http.PostAsJsonAsync(
                "/api/admin/seasons/" + season.SeasonId + "/link-detected", payload);
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذر اعتماد ربط الحلقات المحددة: " + await response.Content.ReadAsStringAsync();
                return;
            }
            var result = await response.Content.ReadFromJsonAsync<LinkDetectedResult>();
            await LoadSeasonsAsync(season.SeriesId);
            await LoadEpisodesAsync(season.SeasonId);
            var rows = await _http.GetFromJsonAsync<EpisodeCandidate[]>(
                "/api/admin/seasons/" + season.SeasonId + "/preview") ?? Array.Empty<EpisodeCandidate>();
            _episodeCandidates.Clear();
            foreach (var row in rows)
            {
                row.Status = row.IsAlreadyLinked ? "مربوط مسبقاً"
                    : row.EpisodeNumber is null or <= 0 ? "لم يتم التعرف على الرقم"
                    : row.IsNumberUsed ? "رقم مكرر"
                    : "جاهز للمراجعة";
                row.Selected = false;
                _episodeCandidates.Add(row);
            }
            SeriesStatusText.Text = $"تم ربط {result?.LinkedCount ?? 0} حلقة، وتخطي {result?.Skipped ?? 0} ملف.";
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر ربط الحلقات: " + ex.Message; }
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
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync();
                SeriesStatusText.Text = response.StatusCode == System.Net.HttpStatusCode.Conflict
                    ? "لم تتم إضافة الحلقة: الملف مربوط مسبقاً أو رقم الحلقة مكرر في هذا الموسم."
                    : "تعذر إضافة الحلقة: " + detail;
                return;
            }
            EpisodeTitleBox.Clear(); EpisodeNumberBox.Text = (number + 1).ToString();
            var rows = await _http.GetFromJsonAsync<EpisodeCandidate[]>(
                "/api/admin/seasons/" + season.SeasonId + "/preview") ?? Array.Empty<EpisodeCandidate>();
            _episodeCandidates.Clear();
            foreach (var row in rows)
            {
                row.Status = row.IsAlreadyLinked ? "مربوط مسبقاً"
                    : row.EpisodeNumber is null or <= 0 ? "لم يتم التعرف على الرقم"
                    : row.IsNumberUsed ? "رقم مكرر"
                    : "جاهز للمراجعة";
                row.Selected = false;
                _episodeCandidates.Add(row);
            }
            await LoadEpisodesAsync(season.SeasonId);
            await LoadMissingEpisodesAsync();
            SeriesStatusText.Text = $"تم ربط «{video.Title}» كالحلقة رقم {number} وتحديث قائمة ملفات الموسم.";
            CloseDedicatedEditor();
        }
        catch (Exception ex) { SeriesStatusText.Text = "تعذر إضافة الحلقة: " + ex.Message; }
    }

    private async void RefreshMissingEpisodes_Click(object sender, RoutedEventArgs e)
    {
        await LoadMissingEpisodesAsync();
    }

    private async System.Threading.Tasks.Task LoadMissingEpisodesAsync()
    {
        try
        {
            var rows = await _http.GetFromJsonAsync<MissingEpisodeAdmin[]>("/api/admin/missing-episodes")
                ?? Array.Empty<MissingEpisodeAdmin>();
            MissingEpisodesGrid.ItemsSource = rows;
            SeriesStatusText.Text = rows.Length == 0
                ? "فحص الملفات مكتمل: جميع ملفات الحلقات المرتبطة موجودة."
                : $"تم العثور على {rows.Length} حلقة لا يوجد ملفها في المسارات المسجلة. أضف المجلد الجديد ثم أعد الربط.";
        }
        catch (Exception ex)
        {
            SeriesStatusText.Text = "تعذر فحص الملفات المفقودة: " + ex.Message;
        }
    }

    private async void RelinkMissingEpisode_Click(object sender, RoutedEventArgs e)
    {
        if (MissingEpisodesGrid.SelectedItem is not MissingEpisodeAdmin episode)
        {
            SeriesStatusText.Text = "حدد الحلقة المفقودة أولاً.";
            return;
        }
        if (MissingEpisodeVideoPicker.SelectedItem is not AdminVideo video)
        {
            SeriesStatusText.Text = "أضف مجلد الملف الجديد إلى المكتبة ثم اختر الفيديو البديل من قائمة الملفات.";
            return;
        }
        if (MessageBox.Show(
            $"هل تريد ربط الحلقة «{episode.Title}» بالملف «{video.Title}»؟",
            "تأكيد إعادة الربط",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            var response = await _http.PostAsJsonAsync(
                "/api/admin/episodes/" + episode.EpisodeId + "/relink",
                new { videoId = video.Id });
            if (!response.IsSuccessStatusCode)
            {
                SeriesStatusText.Text = "تعذرت إعادة الربط: " + await response.Content.ReadAsStringAsync();
                return;
            }
            await LoadMissingEpisodesAsync();
            SeriesStatusText.Text = $"تم ربط الحلقة «{episode.Title}» بالملف الجديد.";
        }
        catch (Exception ex)
        {
            SeriesStatusText.Text = "تعذر إعادة ربط الحلقة: " + ex.Message;
        }
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

    private async void LoadCustomerPageSettings_Click(object sender, RoutedEventArgs e) => await LoadCustomerPageSettingsAsync();

    private async System.Threading.Tasks.Task LoadCustomerPageSettingsAsync()
    {
        try
        {
            var settings = await _http.GetFromJsonAsync<CustomerPageSettingsAdmin>("/api/admin/customer-settings");
            if (settings is null) return;
            CustomerShowCatalogBox.IsChecked = settings.ShowCatalog;
            CustomerShowContinueBox.IsChecked = settings.ShowContinueWatching;
            CustomerShowAudioBox.IsChecked = settings.ShowAudio;
            CustomerShowBooksBox.IsChecked = settings.ShowBooks;
            CustomerShowAppsBox.IsChecked = settings.ShowApps;
            CustomerShowContactBox.IsChecked = settings.ShowContact;
            CustomerShowWelcomeBox.IsChecked = settings.ShowWelcome;
            CustomerShowAnnouncementBox.IsChecked = settings.ShowAnnouncement;
            CustomerShowDownloadsBox.IsChecked = settings.ShowDownloads;
            CustomerShowImageAdsBox.IsChecked = settings.ShowImageAds;
            CustomerLiveBroadcastUrlBox.Text = settings.LiveBroadcastUrl ?? string.Empty;
            CustomerPageSettingsStatusText.Text = "تم تحميل إعدادات ظهور أقسام صفحة العملاء.";
        }
        catch (Exception ex)
        {
            CustomerPageSettingsStatusText.Text = "تعذر تحميل إعدادات صفحة العملاء: " + ex.Message;
        }
    }

    private async void SaveCustomerPageSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var liveBroadcastUrl = string.IsNullOrWhiteSpace(CustomerLiveBroadcastUrlBox.Text)
                ? null
                : CustomerLiveBroadcastUrlBox.Text.Trim();
            if (liveBroadcastUrl is not null &&
                (!Uri.TryCreate(liveBroadcastUrl, UriKind.Absolute, out var liveUri) ||
                 (liveUri.Scheme != Uri.UriSchemeHttp && liveUri.Scheme != Uri.UriSchemeHttps) ||
                 string.IsNullOrWhiteSpace(liveUri.Host)))
            {
                CustomerPageSettingsStatusText.Text = "رابط البث غير صالح. أدخل رابطاً كاملاً يبدأ بـ http:// أو https:// ثم احفظ.";
                CustomerLiveBroadcastUrlBox.Focus();
                return;
            }

            var request = new
            {
                showCatalog = CustomerShowCatalogBox.IsChecked == true,
                showContinueWatching = CustomerShowContinueBox.IsChecked == true,
                showAudio = CustomerShowAudioBox.IsChecked == true,
                showBooks = CustomerShowBooksBox.IsChecked == true,
                showApps = CustomerShowAppsBox.IsChecked == true,
                showContact = CustomerShowContactBox.IsChecked == true,
                showWelcome = CustomerShowWelcomeBox.IsChecked == true,
                showAnnouncement = CustomerShowAnnouncementBox.IsChecked == true,
                showDownloads = CustomerShowDownloadsBox.IsChecked == true,
                showImageAds = CustomerShowImageAdsBox.IsChecked == true,
                liveBroadcastUrl
            };
            var response = await _http.PostAsJsonAsync("/api/admin/customer-settings", request);
            if (!response.IsSuccessStatusCode)
            {
                CustomerPageSettingsStatusText.Text = "تعذر حفظ الإعدادات: " + await response.Content.ReadAsStringAsync();
                return;
            }
            CustomerPageSettingsStatusText.Text = "تم حفظ الإعدادات. ستصل التغييرات إلى صفحات العملاء المفتوحة خلال دقيقة أو عند تحديث الصفحة.";
        }
        catch (Exception ex)
        {
            CustomerPageSettingsStatusText.Text = "تعذر حفظ إعدادات صفحة العملاء: " + ex.Message;
        }
    }

    private async System.Threading.Tasks.Task LoadDownloadSpeedLimitAsync()
    {
        try
        {
            var settings = await _http.GetFromJsonAsync<DownloadSpeedLimitSettings>("/api/admin/download-speed-limit");
            if (settings is null) return;
            DownloadSpeedLimitBox.Text = settings.LimitMbps.ToString();
            DownloadSpeedLimitStatusText.Text = settings.LimitMbps == 0
                ? "التنزيلات دون حد للسرعة."
                : $"الحد الإجمالي الحالي لجميع التنزيلات: {settings.LimitMbps} ميجابت/ثانية.";
        }
        catch (Exception ex)
        {
            DownloadSpeedLimitStatusText.Text = "تعذر تحميل حد السرعة: " + ex.Message;
        }
    }

    private async void SaveDownloadSpeedLimit_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(DownloadSpeedLimitBox.Text.Trim(), out var limit) || limit is < 0 or > 1000)
        {
            DownloadSpeedLimitStatusText.Text = "أدخل رقماً من 0 إلى 1000. الصفر يعني دون حد.";
            return;
        }
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/download-speed-limit", new DownloadSpeedLimitSettings(limit));
            if (!response.IsSuccessStatusCode)
            {
                DownloadSpeedLimitStatusText.Text = "تعذر حفظ حد السرعة: " + await response.Content.ReadAsStringAsync();
                return;
            }
            DownloadSpeedLimitStatusText.Text = limit == 0
                ? "تم إلغاء حد السرعة؛ التنزيلات تعمل بأقصى سرعة متاحة."
                : $"تم تطبيق حد إجمالي قدره {limit} ميجابت/ثانية على جميع التنزيلات.";
        }
        catch (Exception ex)
        {
            DownloadSpeedLimitStatusText.Text = "تعذر حفظ حد السرعة: " + ex.Message;
        }
    }

    private async void LoadAnnouncement_Click(object sender, RoutedEventArgs e) => await LoadAnnouncementAsync();

    private async System.Threading.Tasks.Task LoadAnnouncementAsync()
    {
        try
        {
            var item = await _http.GetFromJsonAsync<AnnouncementAdmin>("/api/admin/announcement");
            if (item is null) return;
            AnnouncementTitleBox.Text = item.Title ?? "";
            AnnouncementMessageBox.Text = item.Message ?? "";
            AnnouncementActiveBox.IsChecked = item.IsActive;
            AnnouncementSpeedSecondsBox.Text = Math.Clamp(item.SpeedSeconds, 5, 60).ToString();
            AnnouncementAdRadio.IsChecked = item.Kind == "ad";
            AnnouncementNoticeRadio.IsChecked = item.Kind != "ad";
            AnnouncementStatusText.Text = item.IsActive ? "الإعلان منشور للعملاء." : "الإعلان محفوظ لكنه غير ظاهر للعملاء.";
        }
        catch (Exception ex) { AnnouncementStatusText.Text = "تعذر تحميل الإعلان: " + ex.Message; }
    }

    private async void SaveAnnouncement_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(AnnouncementSpeedSecondsBox.Text.Trim(), out var speedSeconds) || speedSeconds < 5 || speedSeconds > 60)
        {
            AnnouncementStatusText.Text = "أدخل سرعة صحيحة من 5 إلى 60 ثانية. الرقم الأقل يعني سرعة أعلى.";
            return;
        }
        var request = new
        {
            title = AnnouncementTitleBox.Text.Trim(),
            message = AnnouncementMessageBox.Text.Trim(),
            isActive = AnnouncementActiveBox.IsChecked == true,
            kind = AnnouncementAdRadio.IsChecked == true ? "ad" : "notice",
            speedSeconds,
            updatedUtc = DateTime.UtcNow
        };
        if (request.isActive && string.IsNullOrWhiteSpace(request.message))
        {
            AnnouncementStatusText.Text = "اكتب نص الإعلان قبل نشره.";
            return;
        }
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/announcement", request);
            if (!response.IsSuccessStatusCode)
            {
                AnnouncementStatusText.Text = "تعذر حفظ الإعلان: " + await response.Content.ReadAsStringAsync();
                return;
            }
            AnnouncementStatusText.Text = request.isActive ? "تم حفظ الإعلان ونشره للعملاء." : "تم حفظ الإعلان وإيقاف ظهوره.";
        }
        catch (Exception ex) { AnnouncementStatusText.Text = "تعذر حفظ الإعلان: " + ex.Message; }
    }

    private async void LoadCustomerAds_Click(object sender, RoutedEventArgs e) => await LoadCustomerAdsAsync();

    private async System.Threading.Tasks.Task LoadCustomerAdsAsync(string? selectId = null)
    {
        try
        {
            var rows = await _http.GetFromJsonAsync<CustomerAdItem[]>("/api/admin/customer-ads") ?? Array.Empty<CustomerAdItem>();
            CustomerAdsGrid.ItemsSource = rows;
            var selected = rows.FirstOrDefault(x => x.Id == (selectId ?? (CustomerAdsGrid.SelectedItem as CustomerAdItem)?.Id));
            if (selected is not null) CustomerAdsGrid.SelectedItem = selected;
            CustomerAdsStatusText.Text = $"الإعلانات المصورة المسجلة: {rows.Length} من 50. الصور تحفظ محلياً داخل مجلد أغلفة الاستراحة.";
        }
        catch (Exception ex) { CustomerAdsStatusText.Text = "تعذر تحميل الإعلانات المصورة: " + ex.Message; }
    }

    private void CustomerAdsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomerAdsGrid.SelectedItem is not CustomerAdItem item) return;
        CustomerAdTitleBox.Text = item.Title;
        CustomerAdLinkBox.Text = item.LinkUrl;
        CustomerAdDurationBox.Text = item.DurationSeconds.ToString();
        CustomerAdActiveBox.IsChecked = item.IsActive;
    }

    private async void AddCustomerAd_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "اختر صورة الإعلان",
            Filter = "صور الإعلانات|*.jpg;*.jpeg;*.png;*.webp",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        if (!int.TryParse(CustomerAdDurationBox.Text.Trim(), out var duration) || duration is < 5 or > 120)
        {
            CustomerAdsStatusText.Text = "حدد مدة عرض بين 5 و120 ثانية أولاً.";
            return;
        }
        try
        {
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            if (bytes.LongLength > CustomerAdService.MaximumImageBytes)
            {
                CustomerAdsStatusText.Text = "حجم الصورة يتجاوز 5 ميجابايت.";
                return;
            }
            using var form = new MultipartFormDataContent();
            var image = new ByteArrayContent(bytes);
            var contentType = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".webp" => "image/webp",
                _ => "image/jpeg"
            };
            image.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            form.Add(image, "image", Path.GetFileName(dialog.FileName));
            form.Add(new StringContent(CustomerAdTitleBox.Text.Trim()), "title");
            form.Add(new StringContent(CustomerAdLinkBox.Text.Trim()), "linkUrl");
            form.Add(new StringContent(duration.ToString()), "durationSeconds");
            var response = await _http.PostAsync("/api/admin/customer-ads", form);
            if (!response.IsSuccessStatusCode)
            {
                CustomerAdsStatusText.Text = "تعذرت إضافة الإعلان: " + await response.Content.ReadAsStringAsync();
                return;
            }
            var result = await response.Content.ReadFromJsonAsync<CustomerAdUploadResult>();
            CustomerAdsStatusText.Text = "تمت إضافة الصورة إلى معرض الإعلانات.";
            await LoadCustomerAdsAsync(result?.Ad?.Id);
        }
        catch (Exception ex) { CustomerAdsStatusText.Text = "تعذرت إضافة صورة الإعلان: " + ex.Message; }
    }

    private async void SaveCustomerAd_Click(object sender, RoutedEventArgs e)
    {
        if (CustomerAdsGrid.SelectedItem is not CustomerAdItem selected)
        {
            CustomerAdsStatusText.Text = "حدد إعلاناً من القائمة أولاً.";
            return;
        }
        if (!int.TryParse(CustomerAdDurationBox.Text.Trim(), out var duration) || duration is < 5 or > 120)
        {
            CustomerAdsStatusText.Text = "مدة العرض يجب أن تكون بين 5 و120 ثانية.";
            return;
        }
        try
        {
            var request = new CustomerAdUpdateRequest(CustomerAdTitleBox.Text.Trim(), CustomerAdLinkBox.Text.Trim(), duration, CustomerAdActiveBox.IsChecked == true);
            var response = await _http.PostAsJsonAsync("/api/admin/customer-ads/" + selected.Id, request);
            if (!response.IsSuccessStatusCode)
            {
                CustomerAdsStatusText.Text = "تعذر حفظ الإعلان: " + await response.Content.ReadAsStringAsync();
                return;
            }
            await LoadCustomerAdsAsync(selected.Id);
            CustomerAdsStatusText.Text = "تم حفظ إعدادات الإعلان.";
        }
        catch (Exception ex) { CustomerAdsStatusText.Text = "تعذر حفظ الإعلان: " + ex.Message; }
    }

    private async System.Threading.Tasks.Task MoveCustomerAdAsync(string direction)
    {
        if (CustomerAdsGrid.SelectedItem is not CustomerAdItem selected)
        {
            CustomerAdsStatusText.Text = "حدد إعلاناً أولاً.";
            return;
        }
        try
        {
            var response = await _http.PostAsJsonAsync("/api/admin/customer-ads/" + selected.Id + "/move", new CustomerAdMoveRequest(direction));
            if (!response.IsSuccessStatusCode)
            {
                CustomerAdsStatusText.Text = "تعذر تغيير الترتيب: " + await response.Content.ReadAsStringAsync();
                return;
            }
            await LoadCustomerAdsAsync(selected.Id);
            CustomerAdsStatusText.Text = "تم تحديث ترتيب الإعلانات.";
        }
        catch (Exception ex) { CustomerAdsStatusText.Text = "تعذر تغيير الترتيب: " + ex.Message; }
    }

    private async void MoveCustomerAdUp_Click(object sender, RoutedEventArgs e) => await MoveCustomerAdAsync("up");
    private async void MoveCustomerAdDown_Click(object sender, RoutedEventArgs e) => await MoveCustomerAdAsync("down");

    private async void DeleteCustomerAd_Click(object sender, RoutedEventArgs e)
    {
        if (CustomerAdsGrid.SelectedItem is not CustomerAdItem selected)
        {
            CustomerAdsStatusText.Text = "حدد إعلاناً أولاً.";
            return;
        }
        if (MessageBox.Show("سيتم حذف صورة هذا الإعلان من مجلد الإعلانات المصورة داخل البرنامج فقط. لن يتم حذف ملف الصورة الأصلي الذي اخترته من مكانه. هل تريد المتابعة؟",
            "حذف صورة الإعلان", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            var response = await _http.DeleteAsync("/api/admin/customer-ads/" + selected.Id);
            if (!response.IsSuccessStatusCode)
            {
                CustomerAdsStatusText.Text = "تعذر حذف الإعلان: " + await response.Content.ReadAsStringAsync();
                return;
            }
            await LoadCustomerAdsAsync();
            CustomerAdsStatusText.Text = "تم حذف صورة الإعلان من معرض الاستراحة.";
        }
        catch (Exception ex) { CustomerAdsStatusText.Text = "تعذر حذف الإعلان: " + ex.Message; }
    }

    private sealed class CustomerAdUploadResult
    {
        public CustomerAdItem? Ad { get; set; }
    }

    private async void LookupMetadata_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(IdBox.Text))
        {
            MetadataLookupStatusText.Text = "اختر ملف فيديو من المكتبة أولاً حتى تُحفظ البيانات له.";
            return;
        }
        var query = TitleBox.Text.Trim();
        if (query.Length < 2)
        {
            MetadataLookupStatusText.Text = "اكتب اسم الفيلم أو المسلسل أولاً.";
            return;
        }
        try
        {
            MetadataLookupStatusText.Text = "جارٍ البحث عن البيانات والغلاف...";
            var kind = (KindBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Movie";
            var url = "/api/admin/metadata/lookup?query=" + Uri.EscapeDataString(query) + "&kind=" + Uri.EscapeDataString(kind);
            var result = await _http.GetFromJsonAsync<MetadataLookupAdmin>(url);
            if (result is null)
            {
                MetadataLookupStatusText.Text = "لم يتم العثور على بيانات مطابقة.";
                return;
            }
            TitleBox.Text = result.Title ?? query;
            DescriptionBox.Text = result.Description ?? "";
            YearBox.Text = result.Year?.ToString() ?? "";
            GenreBox.Text = result.Genre ?? "";
            DirectorBox.Text = result.Director ?? "";
            RatingBox.Text = result.Rating?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
            RuntimeBox.Text = result.RuntimeMinutes?.ToString() ?? "";
            ReleaseDateBox.Text = result.ReleaseDate ?? "";
            _pendingVideoPosterPath = result.PosterPath;
            PosterStatusText.Text = string.IsNullOrWhiteSpace(_pendingVideoPosterPath)
                ? "تم جلب البيانات، ولم يتوفر غلاف قابل للحفظ."
                : "تم تنزيل الغلاف وحفظه محلياً: " + _pendingVideoPosterPath;
            MetadataLookupStatusText.Text = "تم جلب البيانات من " + (result.Provider ?? "المصدر") + ". راجعها ثم اضغط حفظ بيانات الفيديو.";
        }
        catch (Exception ex)
        {
            MetadataLookupStatusText.Text = "تعذر جلب البيانات: " + ex.Message;
        }
    }

    private async void RefreshWatchHistory_Click(object sender, RoutedEventArgs e)
    {
        await LoadDashboardWatchHistoryAsync();
    }

    private async System.Threading.Tasks.Task LoadDashboardWatchHistoryAsync()
    {
        try
        {
            var summary = await _http.GetFromJsonAsync<WatchCountsAdmin>("/api/admin/watch-counts") ?? new WatchCountsAdmin();
            var history = await _http.GetFromJsonAsync<WatchPlaybackAdmin[]>("/api/admin/watch-history")
                ?? Array.Empty<WatchPlaybackAdmin>();
            var titles = _videos.ToDictionary(v => v.Id, v => v.Title ?? v.FileName ?? v.Id);
            _popularPlaybackRows.Clear();
            foreach (var item in summary.ViewCounts
                .Select(pair => new PlaybackPopularRow
                {
                    VideoId = pair.Key,
                    Title = titles.TryGetValue(pair.Key, out var title) ? title : "فيديو غير موجود في المكتبة",
                    PlayCount = pair.Value
                })
                .OrderByDescending(x => x.PlayCount)
                .ThenBy(x => x.Title, StringComparer.OrdinalIgnoreCase)
                .Take(10))
                _popularPlaybackRows.Add(item);

            _recentPlaybackRows.Clear();
            foreach (var item in history.Take(20))
            {
                var title = titles.TryGetValue(item.VideoId, out var found) ? found : "فيديو غير موجود في المكتبة";
                var localTime = DateTime.TryParse(item.StartedUtc, out var started)
                    ? started.ToLocalTime().ToString("yyyy/MM/dd HH:mm")
                    : item.StartedUtc;
                _recentPlaybackRows.Add(new PlaybackRecentRow { Title = title, StartedLocal = localTime });
            }

            var total = summary.Total;
            DashboardTotalViewsCount.Text = total.ToString("N0");
            DashboardWatchHistoryStatus.Text = total == 0
                ? "لا توجد مشاهدات مسجلة بعد. سيظهر العدد هنا عند تشغيل العملاء للفيديوهات."
                : $"إجمالي مرات التشغيل منذ بدء التسجيل: {total:N0}. ترتيب الأكثر مشاهدة محسوب من كامل السجل، وآخر المشاهدات من أحدث 200 عملية.";
        }
        catch (Exception ex)
        {
            DashboardTotalViewsCount.Text = "—";
            DashboardWatchHistoryStatus.Text = "تعذر تحميل سجل المشاهدة: " + ex.Message;
            _popularPlaybackRows.Clear();
            _recentPlaybackRows.Clear();
        }
    }

    private void UpdateDashboardStats()
    {
        if (DashboardVideosCount is null) return;
        DashboardVideosCount.Text = _videos.Count.ToString("N0");
        DashboardSeriesCount.Text = _dashboardSeriesCount.ToString("N0");
        DashboardCategoriesCount.Text = _categories.Count(x => x.ParentId is not null || x.ContentKind == "Group").ToString("N0");
        DashboardIncompleteCount.Text = _videos.Count(x => !x.MetadataSaved).ToString("N0");
        DashboardStorageRootsCount.Text = _dashboardExternalRootCount.ToString("N0");
        DashboardPublishedCount.Text = _videos.Count(x => x.IsPublished).ToString("N0");
        DashboardMediaCount.Text = _dashboardMediaCount.ToString("N0");
    }

    private void DashboardCard_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element)
            return;

        var parts = element.Tag?.ToString()?.Split(':');
        if (parts is null || parts.Length is < 2 or > 3 ||
            !int.TryParse(parts[0], out var tabIndex) ||
            !int.TryParse(parts[1], out var subTabIndex) ||
            tabIndex < 1 || tabIndex >= MainNavigationTabs.Items.Count)
            return;

        MainNavigationTabs.SelectedIndex = tabIndex;
        if (tabIndex == 1)
        {
            _dashboardKindFilter = null;
            _selectedVideoFolderPath = null;
            _videosView?.Refresh();
            VideosGrid.SelectedIndex = -1;
            if (subTabIndex >= 0 && subTabIndex < LibraryTabs.Items.Count)
                LibraryTabs.SelectedIndex = subTabIndex;
        }
        else if (tabIndex == 5 && subTabIndex >= 0 && subTabIndex < SettingsTabs.Items.Count)
        {
            SettingsTabs.SelectedIndex = subTabIndex;
        }

        e.Handled = true;
    }

    private void ShowNewCustomerMessageToast(CustomerMessageAdmin message, int count)
    {
        var toast = new Window
        {
            Title = "رسالة جديدة من عميل",
            Width = 370,
            Height = 142,
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.WorkArea.Right - 390,
            Top = SystemParameters.WorkArea.Top + 18,
            FlowDirection = FlowDirection.RightToLeft,
            Background = System.Windows.Media.Brushes.Transparent,
            AllowsTransparency = true
        };

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = count > 1 ? $"وصلت {count} رسائل جديدة من العملاء" : "وصلت رسالة جديدة من عميل",
            FontSize = 16,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{message.Category} — {message.Title}",
            FontSize = 13,
            Foreground = System.Windows.Media.Brushes.White,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0)
        });
        var card = new Border
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20, 45, 74)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(201, 54, 64)),
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(12),
            Child = panel,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        card.MouseLeftButtonUp += (_, _) =>
        {
            MainNavigationTabs.SelectedIndex = 4;
            toast.Close();
            ShowCustomerMessagePopup(message);
        };
        toast.Content = card;
        toast.Show();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += (_, _) => { timer.Stop(); if (toast.IsVisible) toast.Close(); };
        timer.Start();
    }

    private async System.Threading.Tasks.Task LoadCustomerMessagesAsync()
    {
        try
        {
            var selectedId = (CustomerMessagesGrid.SelectedItem as CustomerMessageAdmin)?.Id;
            var rows = await _http.GetFromJsonAsync<CustomerMessageAdmin[]>("/api/admin/messages") ?? Array.Empty<CustomerMessageAdmin>();
            var newlyArrived = _customerMessagesInitialized
                ? rows.Where(x => x.Status == "جديد" && !_knownCustomerMessageIds.Contains(x.Id))
                    .OrderBy(x => x.Id).ToArray()
                : Array.Empty<CustomerMessageAdmin>();
            foreach (var row in rows) _knownCustomerMessageIds.Add(row.Id);
            _customerMessagesInitialized = true;

            _suppressCustomerMessagePopup = true;
            CustomerMessagesGrid.ItemsSource = rows;
            LogoReportsGrid.ItemsSource = rows.Where(x => string.Equals(x.Category, "بلاغ شعار أو علامة تجارية", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (selectedId is not null)
                CustomerMessagesGrid.SelectedItem = rows.FirstOrDefault(x => x.Id == selectedId);
            _suppressCustomerMessagePopup = false;
            var fresh = rows.Count(x => x.Status == "جديد");
            ContactTab.Header = fresh > 0 ? $"✉️ التواصل ({fresh})" : "✉️ التواصل";
            CustomerMessageStatusText.Text = $"إجمالي الرسائل: {rows.Length} — رسائل جديدة: {fresh}. اضغط على الرسالة لفتح تفاصيلها.";
            if (newlyArrived.Length > 0)
            {
                try { System.Media.SystemSounds.Exclamation.Play(); }
                catch (Exception soundError) { App.LogDiagnostic("MainWindow.CustomerMessageNotificationSound", soundError); }
                ShowNewCustomerMessageToast(newlyArrived[^1], newlyArrived.Length);
            }
        }
        catch (Exception ex)
        {
            _suppressCustomerMessagePopup = false;
            CustomerMessageStatusText.Text = "تعذر تحميل رسائل العملاء: " + ex.Message;
        }
    }

    private void CustomerMessagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressCustomerMessagePopup || CustomerMessagesGrid.SelectedItem is not CustomerMessageAdmin message)
            return;
        ShowCustomerMessagePopup(message);
    }

    private void CustomerMessagesGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (CustomerMessagesGrid.SelectedItem is CustomerMessageAdmin message) ShowCustomerMessagePopup(message);
    }

    private void LogoReportsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressCustomerMessagePopup && LogoReportsGrid.SelectedItem is CustomerMessageAdmin message) ShowCustomerMessagePopup(message);
    }

    private void LogoReportsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LogoReportsGrid.SelectedItem is CustomerMessageAdmin message) ShowCustomerMessagePopup(message);
    }

    private void ShowCustomerMessagePopup(CustomerMessageAdmin message)
    {
        var dialog = new Window
        {
            Title = $"رسالة عميل رقم {message.Id}",
            Width = 560,
            Height = 540,
            MinWidth = 430,
            MinHeight = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            FlowDirection = FlowDirection.RightToLeft,
            Background = System.Windows.Media.Brushes.White,
            ResizeMode = ResizeMode.CanResize
        };
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(new TextBlock
        {
            Text = message.Title,
            FontSize = 21,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.DarkSlateGray,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });
        root.Children.Add(new TextBlock
        {
            Text = $"النوع: {message.Category}     |     رقم الرسالة: {message.Id}",
            Foreground = System.Windows.Media.Brushes.SlateGray,
            Margin = new Thickness(0, 0, 0, 8)
        });
        root.Children.Add(new TextBlock
        {
            Text = $"تاريخ الإرسال: {FormatMessageDate(message.CreatedUtc)}",
            Foreground = System.Windows.Media.Brushes.SlateGray,
            Margin = new Thickness(0, 0, 0, 10)
        });
        root.Children.Add(new TextBlock
        {
            Text = "تفاصيل رسالة العميل",
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 5)
        });
        root.Children.Add(new TextBox
        {
            Text = message.Details,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 205,
            Padding = new Thickness(10),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 247, 250))
        });
        root.Children.Add(new TextBlock
        {
            Text = $"وسيلة التواصل: {(string.IsNullOrWhiteSpace(message.Contact) ? "لم يحدد العميل" : message.Contact)}",
            Margin = new Thickness(0, 10, 0, 3)
        });
        root.Children.Add(new TextBlock
        {
            Text = $"معرّف جهاز العميل: {(string.IsNullOrWhiteSpace(message.ViewerId) ? "غير متوفر" : message.ViewerId)}",
            Foreground = System.Windows.Media.Brushes.SlateGray,
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 12)
        });
        root.Children.Add(new TextBlock { Text = "حالة الرسالة", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) });
        var statusPicker = new ComboBox
        {
            ItemsSource = new[] { "جديد", "قيد المتابعة", "تمت المعالجة" },
            SelectedItem = message.Status,
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 14)
        };
        root.Children.Add(statusPicker);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left };
        var save = new Button
        {
            Content = "حفظ الحالة",
            Padding = new Thickness(18, 9, 18, 9),
            Margin = new Thickness(0, 0, 8, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(201, 54, 64)),
            Foreground = System.Windows.Media.Brushes.White,
            FontWeight = FontWeights.Bold
        };
        var close = new Button { Content = "إغلاق", Padding = new Thickness(18, 9, 18, 9), IsCancel = true };
        save.Click += async (_, _) =>
        {
            if (statusPicker.SelectedItem is string status)
                await SetCustomerMessageStatusAsync(message.Id, status, dialog);
        };
        actions.Children.Add(save);
        actions.Children.Add(close);
        root.Children.Add(actions);
        dialog.Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        dialog.ShowDialog();
    }

    private static string FormatMessageDate(string value)
    {
        if (DateTime.TryParse(value, out var date))
            return date.ToLocalTime().ToString("yyyy/MM/dd  hh:mm tt");
        return value;
    }

    private async System.Threading.Tasks.Task SetCustomerMessageStatusAsync(long id, string status, Window? popup = null)
    {
        try
        {
            var response = await _http.PostAsJsonAsync($"/api/admin/messages/{id}/status", new { status });
            if (!response.IsSuccessStatusCode)
            {
                CustomerMessageStatusText.Text = "تعذر تحديث حالة الرسالة: " + await response.Content.ReadAsStringAsync();
                return;
            }
            popup?.Close();
            await LoadCustomerMessagesAsync();
            CustomerMessageStatusText.Text = $"تم تحديث الرسالة رقم {id} إلى «{status}».";
        }
        catch (Exception ex) { CustomerMessageStatusText.Text = "تعذر تحديث الرسالة: " + ex.Message; }
    }

    private async System.Threading.Tasks.Task SetSelectedCustomerMessageStatusAsync(string status)
    {
        if (CustomerMessagesGrid.SelectedItem is not CustomerMessageAdmin message)
        {
            CustomerMessageStatusText.Text = "اختر رسالة من القائمة أولاً.";
            return;
        }
        await SetCustomerMessageStatusAsync(message.Id, status);
    }

    private async void RefreshCustomerMessages_Click(object sender, RoutedEventArgs e) =>
        await LoadCustomerMessagesAsync();

    private async void MarkCustomerMessageInProgress_Click(object sender, RoutedEventArgs e) =>
        await SetSelectedCustomerMessageStatusAsync("قيد المتابعة");

    private async void ResolveCustomerMessage_Click(object sender, RoutedEventArgs e) =>
        await SetSelectedCustomerMessageStatusAsync("تمت المعالجة");

    private async void DeleteCustomerMessage_Click(object sender, RoutedEventArgs e)
    {
        if (CustomerMessagesGrid.SelectedItem is not CustomerMessageAdmin message)
        {
            CustomerMessageStatusText.Text = "اختر رسالة تريد حذفها أولاً.";
            return;
        }
        if (MessageBox.Show($"هل تريد حذف رسالة العميل رقم {message.Id}؟", "تأكيد حذف الرسالة", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            var response = await _http.DeleteAsync($"/api/admin/messages/{message.Id}");
            if (!response.IsSuccessStatusCode)
            {
                CustomerMessageStatusText.Text = "تعذر حذف الرسالة: " + await response.Content.ReadAsStringAsync();
                return;
            }
            await LoadCustomerMessagesAsync();
            CustomerMessageStatusText.Text = $"تم حذف الرسالة رقم {message.Id}.";
        }
        catch (Exception ex) { CustomerMessageStatusText.Text = "تعذر حذف الرسالة: " + ex.Message; }
    }

    private sealed class RestHouseProfileAdmin
    {
        public string RestHouseName { get; set; } = "استراحة الأسطورة";
        public string NetworkName { get; set; } = "شبكة الأسطورة نت";
        public string? LogoPath { get; set; }
    }
    private sealed class BrandLogoUploadResult
    {
        public string? LogoPath { get; set; }
    }

    private sealed class CustomerMessageAdmin
    {
        public long Id { get; set; }
        public string Category { get; set; } = "";
        public string Title { get; set; } = "";
        public string Details { get; set; } = "";
        public string? Contact { get; set; }
        public string? ViewerId { get; set; }
        public string Status { get; set; } = "";
        public string CreatedUtc { get; set; } = "";
        public string UpdatedUtc { get; set; } = "";
    }

    private sealed class MediaToolStatus
    {
        public bool Available { get; set; }
        public string FfmpegPath { get; set; } = "";
        public string FfprobePath { get; set; } = "";
        public string Message { get; set; } = "";
    }
    private sealed class SeriesAdmin
    {
        public string SeriesId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public int? Year { get; set; }
        public string Genre { get; set; } = "";
        public string? PosterPath { get; set; }
        public string? FolderPath { get; set; }
        public string? CategoryId { get; set; }
        public string Section { get; set; } = "عام";
        public bool IsPublished { get; set; }
        public string CategoryTitle { get; set; } = "غير مصنف";
        public string PublishStatus { get; set; } = "";
        public string OrganizationType { get; set; } = "Seasons";
        public string OrganizationTypeLabel => OrganizationType switch { "None" => "مباشر", "Parts" => "أجزاء", _ => "مواسم" };
    }
    private sealed class SeasonAdmin
    {
        public long SeasonId { get; set; }
        public string SeriesId { get; set; } = "";
        public int SeasonNumber { get; set; }
        public string Title { get; set; } = "";
        public string? FolderPath { get; set; }
        public string DisplayTitle { get; set; } = "";
    }
    private sealed class EpisodeAdmin
    {
        public long EpisodeId { get; set; }
        public long SeasonId { get; set; }
        public string VideoId { get; set; } = "";
        public int EpisodeNumber { get; set; }
        public string Title { get; set; } = "";
        public string? PosterPath { get; set; }
    }
    private sealed class PosterUploadResult { public string? PosterPath { get; set; } }
    private sealed class LinkDetectedResult { public int LinkedCount { get; set; } public int Skipped { get; set; } }
    private sealed class MissingEpisodeAdmin
    {
        public long EpisodeId { get; set; }
        public string SeriesId { get; set; } = "";
        public string SeriesTitle { get; set; } = "";
        public int SeasonNumber { get; set; }
        public int EpisodeNumber { get; set; }
        public string Title { get; set; } = "";
        public string VideoId { get; set; } = "";
    }
    private sealed class EpisodeCandidate : INotifyPropertyChanged
    {
        private int? _episodeNumber;
        private bool _selected;

        public string VideoId { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string Title { get; set; } = "";
        public int? EpisodeNumber
        {
            get => _episodeNumber;
            set
            {
                if (_episodeNumber == value) return;
                _episodeNumber = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(EpisodeNumber)));
            }
        }
        public bool IsAlreadyLinked { get; set; }
        public bool IsNumberUsed { get; set; }
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
            }
        }
        public string Status { get; set; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private sealed class FolderScanResult { public bool Registered { get; set; } public int VideoCount { get; set; } }
    private sealed class RegisterFilesResult { public bool Registered { get; set; } public int VideoCount { get; set; } public string? Message { get; set; } }
    private sealed class CategoryAdmin
    {
        public string CategoryId { get; set; } = "";
        public string? ParentId { get; set; }
        public string Title { get; set; } = "";
        public string ContentKind { get; set; } = "Group";
        public string? PosterPath { get; set; }
        public bool IsPublished { get; set; }
        public string CreatedUtc { get; set; } = "";
        public string DisplayTitle { get; set; } = "";
        public string ContentKindLabel { get; set; } = "";
    }
    private sealed class PlaybackPopularRow
    {
        public string VideoId { get; set; } = "";
        public string Title { get; set; } = "";
        public int PlayCount { get; set; }
    }

    private sealed class PlaybackRecentRow
    {
        public string Title { get; set; } = "";
        public string StartedLocal { get; set; } = "";
    }

    private sealed class WatchPlaybackAdmin
    {
        public string VideoId { get; set; } = "";
        public string ViewerId { get; set; } = "";
        public string StartedUtc { get; set; } = "";
    }
    
    private sealed class WatchCountsAdmin
    {
        public int Total { get; set; }
        public Dictionary<string, int> ViewCounts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CustomerPageSettingsAdmin
    {
        public bool ShowCatalog { get; set; } = true;
        public bool ShowContinueWatching { get; set; } = true;
        public bool ShowAudio { get; set; } = true;
        public bool ShowBooks { get; set; } = true;
        public bool ShowApps { get; set; } = true;
        public bool ShowContact { get; set; } = true;
        public bool ShowWelcome { get; set; } = true;
        public bool ShowAnnouncement { get; set; } = true;
        public bool ShowDownloads { get; set; } = true;
        public bool ShowImageAds { get; set; } = true;
        public string? LiveBroadcastUrl { get; set; }
    }

    private sealed class AnnouncementAdmin
    {
        public string Title { get; set; } = "";
        public string Message { get; set; } = "";
        public bool IsActive { get; set; }
        public string Kind { get; set; } = "notice";
        public int SpeedSeconds { get; set; } = 18;
    }

    private sealed class MetadataLookupAdmin
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public int? Year { get; set; }
        public string? Genre { get; set; }
        public string? Kind { get; set; }
        public string? PosterPath { get; set; }
        public string? Director { get; set; }
        public double? Rating { get; set; }
        public int? RuntimeMinutes { get; set; }
        public string? ReleaseDate { get; set; }
        public string? Provider { get; set; }
    }

    private sealed class AdminVideo
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public string? FileName { get; set; }
        public string? FolderId { get; set; }
        public string FolderName { get; set; } = "المكتبة";
        public string? FolderPath { get; set; }
        public string? Description { get; set; }
        public int? Year { get; set; }
        public string? Genre { get; set; }
        public string? Kind { get; set; }
        public string? PosterPath { get; set; }
        public string? Director { get; set; }
        public string? Cast { get; set; }
        public double? Rating { get; set; }
        public int? RuntimeMinutes { get; set; }
        public string? ReleaseDate { get; set; }
        public string? Tags { get; set; }
        public string? CategoryId { get; set; }
        public string Section { get; set; } = "عام";
        public bool IsPublished { get; set; }
        public bool MetadataSaved { get; set; }
        public string? CategoryTitle { get; set; }
        public string? LinkedSeriesId { get; set; }
        public long? LinkedSeasonId { get; set; }
        public int? LinkedEpisodeNumber { get; set; }
        public string? LinkedEpisodeTitle { get; set; }
        public int ViewCount { get; set; }
        public string KindLabel => string.Equals(Kind, "Series", StringComparison.OrdinalIgnoreCase) ? "مسلسل" : "فيلم";
        public string MetadataStatus => MetadataSaved ? "بيانات محفوظة" : "بانتظار البيانات";
        public string PublishStatus => IsPublished ? "منشور" : "غير منشور";
    }
}

public sealed class VideoFileCandidateAdmin
{
    public string FullPath { get; set; } = "";
    public string FileName { get; set; } = "";
    public long SizeBytes { get; set; }
}

public sealed class FolderVideoSelectionWindow : Window
{
    private readonly ObservableCollection<SelectableVideoFile> _items;
    public string[] SelectedPaths => _items.Where(x => x.Selected).Select(x => x.FullPath).ToArray();

    public FolderVideoSelectionWindow(string folderPath, IEnumerable<VideoFileCandidateAdmin> candidates)
    {
        Title = "حدد ملفات الفيديو التي تريد إضافتها";
        Width = 760;
        Height = 620;
        MinWidth = 600;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = FlowDirection.RightToLeft;
        Background = System.Windows.Media.Brushes.White;
        Owner = Application.Current.MainWindow;

        _items = new ObservableCollection<SelectableVideoFile>(candidates.Select(x => new SelectableVideoFile
        {
            FullPath = x.FullPath,
            FileName = x.FileName,
            SizeText = FormatSize(x.SizeBytes),
            Selected = false
        }));

        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = "حدد الفيديوهات التي تريد إضافتها فقط، ويمكنك استخدام تحديد الكل أو إلغاء التحديد.", FontSize = 19, FontWeight = FontWeights.Bold, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(20,45,74)) });
        header.Children.Add(new TextBlock { Text = folderPath, TextWrapping = TextWrapping.Wrap, Foreground = System.Windows.Media.Brushes.DimGray, Margin = new Thickness(0,6,0,8) });
        var selectionButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var allButton = new Button { Content = "تحديد الكل", Padding = new Thickness(12,6,12,6), Margin = new Thickness(0,0,6,0) };
        var noneButton = new Button { Content = "إلغاء التحديد", Padding = new Thickness(12,6,12,6) };
        allButton.Click += (_, _) => { foreach (var item in _items) item.Selected = true; };
        noneButton.Click += (_, _) => { foreach (var item in _items) item.Selected = false; };
        selectionButtons.Children.Add(allButton);
        selectionButtons.Children.Add(noneButton);
        header.Children.Add(selectionButtons);
        var selectionCount = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(36, 91, 135)),
            FontWeight = FontWeights.SemiBold
        };
        header.Children.Add(selectionCount);
        void UpdateSelectionCount() =>
            selectionCount.Text = $"تم تحديد {_items.Count(x => x.Selected)} من أصل {_items.Count} فيديو";
        foreach (var item in _items)
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SelectableVideoFile.Selected))
                    UpdateSelectionCount();
            };
        UpdateSelectionCount();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,12,0,0) };
        var addButton = new Button { Content = "إضافة المحدد", IsDefault = true, Padding = new Thickness(18,8,18,8), Margin = new Thickness(0,0,8,0), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(36,91,135)), Foreground = System.Windows.Media.Brushes.White };
        var cancelButton = new Button { Content = "إلغاء", IsCancel = true, Padding = new Thickness(18,8,18,8) };
        addButton.Click += (_, _) => { DialogResult = true; };
        footer.Children.Add(addButton);
        footer.Children.Add(cancelButton);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var grid = new DataGrid { ItemsSource = _items, AutoGenerateColumns = false, CanUserAddRows = false, IsReadOnly = false, SelectionMode = DataGridSelectionMode.Extended, HeadersVisibility = DataGridHeadersVisibility.Column, FlowDirection = FlowDirection.RightToLeft };
        grid.Columns.Add(new DataGridCheckBoxColumn { Header = "اختيار", Binding = new Binding(nameof(SelectableVideoFile.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged }, Width = 65 });
        grid.Columns.Add(new DataGridTextColumn { Header = "اسم ملف الفيديو", Binding = new Binding(nameof(SelectableVideoFile.FileName)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        grid.Columns.Add(new DataGridTextColumn { Header = "الحجم", Binding = new Binding(nameof(SelectableVideoFile.SizeText)), IsReadOnly = true, Width = 100 });
        root.Children.Add(grid);
        Content = root;
    }

    private static string FormatSize(long bytes) => bytes >= 1024L * 1024 * 1024
        ? (bytes / (1024d * 1024 * 1024)).ToString("0.00") + " GB"
        : bytes >= 1024L * 1024 ? (bytes / (1024d * 1024)).ToString("0.0") + " MB"
        : (bytes / 1024d).ToString("0") + " KB";

    private sealed class SelectableVideoFile : INotifyPropertyChanged
    {
        private bool _selected;
        public string FullPath { get; set; } = "";
        public string FileName { get; set; } = "";
        public string SizeText { get; set; } = "";
        public bool Selected { get => _selected; set { if (_selected == value) return; _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
}
