$ErrorActionPreference = "Stop"
$root = Join-Path $PSScriptRoot ".."
$xamlPath = Join-Path $root "src/Alostora.RestHouse/MainWindow.xaml"
$codePath = Join-Path $root "src/Alostora.RestHouse/MainWindow.xaml.cs"
$appPath = Join-Path $root "src/Alostora.RestHouse/App.xaml.cs"
$htmlPath = Join-Path $root "src/Alostora.RestHouse/wwwroot/index.html"

$xaml = Get-Content $xamlPath -Raw
$code = Get-Content $codePath -Raw
$app = Get-Content $appPath -Raw
$html = Get-Content $htmlPath -Raw

# Every event wired in XAML must have a real code-behind handler.
$events = [regex]::Matches($xaml, '\b(?:Click|SelectionChanged|Loaded|Checked|Unchecked|TextChanged|Drop|DragOver)="([A-Za-z_]\w*)"') |
    ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
$missing = @($events | Where-Object { $code -notmatch ("(?m)\b(?:async\s+)?(?:void|Task(?:<[^>]+>)?)\s+" + [regex]::Escape($_) + "\s*\(") })
if ($missing.Count -gt 0) {
    throw ("XAML events without code-behind handlers: " + ($missing -join ", "))
}
Write-Host ("PASS: all {0} XAML event handlers are implemented." -f $events.Count)

# The management client and embedded server must share the persisted configurable LAN port.
$settingsPath = Join-Path $root "src/Alostora.RestHouse/AppSettings.cs"
$settings = Get-Content $settingsPath -Raw
if ($code -notmatch '127\.0\.0\.1:\{_port\}' -or
    $app -notmatch 'ListenAnyIP\(settings\.Port\)' -or
    $settings -notmatch 'int Port = 8097' -or
    $settings -notmatch 'SavePort\(' -or
    $code -notmatch 'SavePort\(_root, port\)') {
    throw "Management client/server configurable port persistence is inconsistent."
}
if ($app -notmatch 'ListenAnyIP\(8443,listen=>listen\.UseHttps' -or $app -notmatch 'ALOSTORA_HTTPS_CERT_PATH') {
    throw "Remote administration must use the optional dedicated HTTPS listener."
}
Write-Host "PASS: management client and embedded server share the persisted configurable LAN port."

# Check that key management routes used by the UI exist in the embedded API.
$requiredRoutes = @(
    '/api/admin/catalog-categories',
    '/api/admin/library/folders',
    '/api/admin/library/unregister-folder',
    '/api/admin/profile',
    '/api/admin/profile/logo',
    '/api/profile',
    '/api/customer-settings',
    '/api/admin/customer-settings',
    '/api/admin/series',
    '/api/admin/seasons',
    '/api/admin/register-selected-files',
    '/api/admin/scan-folder',
    '/api/admin/metadata',
    '/api/admin/poster/',
    '/api/admin/series-poster/',
    '/api/admin/episodes',
    '/api/admin/seasons/{seasonId:long}/episodes',
    '/api/admin/episodes/{episodeId:long}',
    '/api/admin/missing-episodes',
    '/api/media-libraries',
    '/api/admin/media-libraries/scan',
    '/api/media-library/',
    '/api/library'
)
foreach ($route in $requiredRoutes) {
    if (-not $app.Contains($route)) { throw ("Missing API route: " + $route) }
}
Write-Host ("PASS: all {0} required management API routes are present." -f $requiredRoutes.Count)

# Parse each inline viewer script with Node.js to catch syntax errors before packaging.
$scripts = [regex]::Matches($html, '(?is)<script\b[^>]*>(.*?)</script>')
if ($scripts.Count -eq 0) { throw "No inline viewer scripts were found." }
$index = 0
foreach ($match in $scripts) {
    $index++
    if ([string]::IsNullOrWhiteSpace($match.Groups[1].Value)) { continue }
    $scriptPath = Join-Path $env:RUNNER_TEMP ("alostora-viewer-{0}.js" -f $index)
    [System.IO.File]::WriteAllText($scriptPath, $match.Groups[1].Value, [System.Text.UTF8Encoding]::new($false))
    node --check $scriptPath
    if ($LASTEXITCODE -ne 0) { throw ("Viewer JavaScript syntax check failed for inline script {0}." -f $index) }
}
Write-Host ("PASS: {0} inline viewer script block(s) passed Node.js syntax validation." -f $scripts.Count)

# The customer page must open on the accommodation catalog and show contact only on demand.
if ($html -notmatch 'id="homeNav"' -or
    $html -notmatch 'id="contactNav"' -or
    $html -notmatch 'id="homeView"' -or
    $html -notmatch 'id="contactView"[^>]*hidden' -or
    $html -notmatch "showCustomerView\('contact'\)" -or
    $html -notmatch "showCustomerView\('home'\)") {
    throw "Customer navigation must separate the home catalog from the contact form."
}
Write-Host "PASS: customer home and contact form have separate navigation states."

# Audio, books, and Android apps are first-class local-network customer libraries.
if ($html -notmatch 'data-kind="Audio"' -or
    $html -notmatch 'data-kind="Books"' -or
    $html -notmatch 'data-kind="Apps"' -or
    $html -notmatch 'id="mediaLibrary"' -or
    $html -notmatch 'id="mediaGrid"' -or
    $html -notmatch 'function renderMediaLibraries\(' -or
    $html -notmatch "fetch\('/api/media-libraries'\)" -or
    $html -notmatch 'sharedAudioPlayer' -or
    $xaml -notmatch 'Header="🎧 الصوتيات والكتب والتطبيقات"') {
    throw "Audio, books, and Android apps must be available to customers and manageable from the admin panel."
}
$multiMediaTestsPath = Join-Path $root "tests/Alostora.RestHouse.Tests/MultiMediaLibraryServiceTests.cs"
if (-not (Test-Path $multiMediaTestsPath)) { throw "Multimedia library regression tests are missing." }
Write-Host "PASS: customer audio, books, and apps libraries are wired to the local media API and admin controls."

# Every customer-facing section can be toggled from the Windows management application.
$customerSettingsTestsPath = Join-Path $root "tests/Alostora.RestHouse.Tests/CustomerPageSettingsServiceTests.cs"
if (-not (Test-Path $customerSettingsTestsPath) -or
    $xaml -notmatch 'Header="🖥️ واجهة العميل"' -or
    $xaml -notmatch 'Header="📢 الإعلانات"' -or
    $xaml -notmatch 'x:Name="SettingsTabs"' -or
    $xaml -notmatch 'Tag="5:1"' -or
    $xaml -notmatch 'Tag="5:3"' -or
    $xaml -notmatch 'CustomerShowCatalogBox' -or
    $xaml -notmatch 'CustomerShowContinueBox' -or
    $xaml -notmatch 'CustomerShowAudioBox' -or
    $xaml -notmatch 'CustomerShowBooksBox' -or
    $xaml -notmatch 'CustomerShowAppsBox' -or
    $xaml -notmatch 'CustomerShowContactBox' -or
    $xaml -notmatch 'CustomerShowWelcomeBox' -or
    $xaml -notmatch 'CustomerShowAnnouncementBox' -or
    $code -notmatch 'SaveCustomerPageSettings_Click' -or
    $code -notmatch 'LoadCustomerPageSettingsAsync' -or
    $app -notmatch 'MapGet\("\/api\/customer-settings"' -or
    $app -notmatch 'MapPost\("\/api\/admin\/customer-settings"' -or
    $html -notmatch 'function applyCustomerSettings\(' -or
    $html -notmatch 'class="header-search"' -or
    $html -notmatch 'id="search"' -or
    $html -notmatch 'customerSettings\.showApps' -or
    $html -notmatch 'customerSettings\.showBooks' -or
    $html -notmatch 'customerSettings\.showAudio') {
    throw "Customer page section visibility controls are incomplete."
}
if ($html -notmatch 'announcement-marquee' -or
    $html -notmatch 'animationDuration' -or
    $xaml -notmatch 'الشريط الإعلاني المتحرك للعملاء') {
    throw "The customer announcement must be a styled scrolling banner managed by the admin."
}
Write-Host "PASS: customer page sections are admin-controlled and the announcement is a scrolling banner."

# Dashboard statistics include all registered local media, not only videos.
if ($xaml -notmatch 'x:Name="DashboardMediaCount"' -or
    $code -notmatch '_dashboardMediaCount\s*=\s*items\.Length' -or
    $code -notmatch 'DashboardMediaCount\.Text\s*=\s*_dashboardMediaCount') {
    throw "The admin dashboard must count audio, books, and apps alongside videos."
}
Write-Host "PASS: admin dashboard counts all registered local media libraries."

# The catalog must navigate through distinct customer pages, not stacked detail panels.
if ($html -notmatch 'id="catalogView"' -or
    $html -notmatch 'id="catalogBack"' -or
    $html -notmatch 'function renderCatalogPage\(' -or
    $html -notmatch "pageType==='season'" -or
    $html -notmatch "pageType==='player'" -or
    $html -match 'id="seriesDetails"' -or
    $html -match 'id="movieDetails"') {
    throw "Customer catalog must use separate category, program, season, and player pages."
}
Write-Host "PASS: customer catalog uses separate navigation pages and a dedicated player view."


# A category may be published without a poster; new category forms default to published.
if ($app -match 'request\.IsPublished\s*&&\s*!PosterExists\(request\.PosterPath,posters\)' -or
    $app -match 'if\(!PosterExists\(poster,posters\)\)' -or
    $xaml -notmatch 'CategoryPublishBox"[^>]*IsChecked="True"' -or
    $xaml -notmatch 'PublishBox"[^>]*IsChecked="True"' -or
    $xaml -notmatch 'SeriesPublishBox"[^>]*IsChecked="True"' -or
    $code -notmatch 'CategoryPublishBox\.IsChecked\s*=\s*true' -or
    $code -notmatch 'SeriesPublishBox\.IsChecked\s*=\s*true' -or
    $code -notmatch 'PublishBox\.IsChecked\s*=\s*v\.IsPublished\s*\|\|\s*!v\.MetadataSaved') {
    throw "New categories, movies, and series must publish by default without mandatory poster images."
}
$categoryTestsPath = Join-Path $root "tests/Alostora.RestHouse.Tests/CatalogCategoryServiceTests.cs"
if (-not (Test-Path $categoryTestsPath)) { throw "Catalog category regression tests are missing." }
$categoryTests = Get-Content $categoryTestsPath -Raw
foreach ($testName in @("PublishedMainAndChildCategoriesAppearWithoutPosterImages", "UnpublishedCategoriesDoNotAppearInCustomerCatalog", "PublishedChildOfUnpublishedParentIsNotExposed", "DeletingMainCategoryRemovesCategoryTreeAndUnpublishesLinkedContentWithoutDeletingMedia")) {
    if (-not $categoryTests.Contains($testName)) { throw ("Missing category regression test: " + $testName) }
}
Write-Host "PASS: customer category publication and optional-poster behavior are regression-tested."

# The metadata List projection must match the ordinal layout consumed by VideoMetadataService.Read.
$metadataPath = Join-Path $root "src/Alostora.RestHouse/VideoMetadataService.cs"
$metadata = Get-Content $metadataPath -Raw
$listQuery = [regex]::Match($metadata, '(?s)public IReadOnlyList<VideoMetadata> List\(\).*?cmd\.CommandText\s*=\s*"""(.*?)"""')
if (-not $listQuery.Success -or $listQuery.Groups[1].Value -notmatch '\bCategoryId\b') {
    throw "VideoMetadataService.List must select CategoryId before Section and IsPublished."
}
if ($metadata -notmatch 'r\.IsDBNull\(13\)\s*\?\s*null\s*:\s*r\.GetString\(13\),\s*r\.GetString\(14\),\s*r\.GetInt32\(15\)') {
    throw "VideoMetadataService.Read ordinal mapping changed; review the List projection."
}
Write-Host "PASS: metadata List query includes CategoryId and matches the reader column layout."

# Category deletion must detach and unpublish content, never erase metadata or media links.
$categoryServicePath = Join-Path $root "src/Alostora.RestHouse/CatalogCategoryService.cs"
$categoryService = Get-Content $categoryServicePath -Raw
$deleteMethod = [regex]::Match($categoryService, '(?s)public void Delete\(string id\)(.*?)private static bool TableExists')
if (-not $deleteMethod.Success -or
    $deleteMethod.Groups[1].Value -notmatch 'UPDATE VideoMetadata\s+SET CategoryId\s*=\s*NULL, IsPublished\s*=\s*0' -or
    $deleteMethod.Groups[1].Value -notmatch 'UPDATE SeriesCatalog\s+SET CategoryId\s*=\s*NULL, IsPublished\s*=\s*0' -or
    $deleteMethod.Groups[1].Value -match 'DELETE FROM (VideoMetadata|SeriesCatalog)') {
    throw "Category deletion must detach and unpublish content without deleting its metadata."
}
Write-Host "PASS: category deletion safely detaches and unpublishes content while preserving its records."

$libraryServicePath = Join-Path $root "src/Alostora.RestHouse/LibraryService.cs"
$libraryService = Get-Content $libraryServicePath -Raw
$libraryFolderTestsPath = Join-Path $root "tests/Alostora.RestHouse.Tests/LibraryServiceFolderTests.cs"
if ($libraryService -notmatch 'public IReadOnlyList<LibraryFolderInfo> ListFolders\(' -or
    $libraryService -notmatch 'public string CreateFolder\(' -or
    $code -notmatch 'RefreshVideoFoldersTreeAsync\(' -or
    $code -notmatch 'CreateLibraryFolder_Click' -or
    $code -notmatch 'CopyVideosToSelectedFolder_Click' -or
    $xaml -notmatch 'Click="CreateLibraryFolder_Click"' -or
    $xaml -notmatch 'Click="CopyVideosToSelectedFolder_Click"' -or
    -not (Test-Path $libraryFolderTestsPath)) {
    throw "Nested library folder management or safe copy controls are incomplete."
}
$libraryFolderTests = Get-Content $libraryFolderTestsPath -Raw
foreach ($testName in @("CreatesAndListsNestedManagedFolders", "RejectsPathTraversalAndFolderCreationOutsideManagedLibrary")) {
    if (-not $libraryFolderTests.Contains($testName)) { throw ("Missing library folder regression test: " + $testName) }
}
Write-Host "PASS: nested folder management, path traversal protection, and non-destructive copy controls are wired."

if ($libraryService -notmatch 'public int UnregisterFolder' -or
    $libraryService -notmatch 'RegisteredExternalFolders' -or
    $code -notmatch 'RemoveExternalLibraryFolder_Click' -or
    $xaml -notmatch 'Click="RemoveExternalLibraryFolder_Click"' -or
    $xaml -notmatch 'Header="🎧 الصوتيات والكتب والتطبيقات"' -or
    $xaml -match 'Header="🎧 الصوتيات والكتب والتطبيقات"[^>]*Visibility="Collapsed"' -or
    $xaml -match 'Click="DeleteVideo_Click"' -or
    $app -notmatch 'حذف ملفات الفيديو الأصلية معطّل') {
    throw "External storage removal must preserve original files, audio/books/apps administration must remain visible, and permanent deletion must be disabled."
}
foreach ($testName in @("UnregistersSelectedExternalFolderWithoutDeletingOriginalVideos", "CannotUnregisterTheManagedLibraryRoot")) {
    if (-not $libraryFolderTests.Contains($testName)) { throw ("Missing safe storage removal regression test: " + $testName) }
}
Write-Host "PASS: external storage paths can be unregistered without deleting source videos; destructive deletion is disabled."


# Public catalog and media routes must enforce publication on the server, not only in browser JavaScript.
$publicAccessPath = Join-Path $root "src/Alostora.RestHouse/PublicCatalogAccessService.cs"
$publicAccess = Get-Content $publicAccessPath -Raw
$seriesPath = Join-Path $root "src/Alostora.RestHouse/SeriesCatalogService.cs"
$series = Get-Content $seriesPath -Raw
$mediaPath = Join-Path $root "src/Alostora.RestHouse/MediaConversionService.cs"
$media = Get-Content $mediaPath -Raw
if ($publicAccess -notmatch 'GetPublishedVideoIds' -or
    $publicAccess -notmatch 'GetPublishedSeriesIds' -or
    $publicAccess -notmatch 'IsVideoPublished' -or
    $app -notmatch 'publicCatalog\.IsVideoPublished' -or
    $app -notmatch 'publicCatalog\.GetPublishedVideoIds\(\)' -or
    $app -notmatch 'publicCatalog\.GetPublishedSeriesIds\(\)' -or
    $series -notmatch 'allowedSeriesIds' -or
    $media -notmatch 'GetSourceVideoId') {
    throw "Public catalog, media, subtitle, or conversion routes are missing publication guards."
}
Write-Host "PASS: public catalog, media, subtitle, and conversion routes enforce publication."

# Episode editing and unlinking must remain non-destructive and covered by regression tests.
if ($series -notmatch 'public void UpdateEpisode\(' -or
    $series -notmatch 'public string UnlinkEpisode\(' -or
    $app -notmatch 'MapPut\("/api/admin/episodes/' -or
    $app -notmatch 'MapDelete\("/api/admin/episodes/' -or
    $code -notmatch 'UpdateEpisode_Click' -or
    $code -notmatch 'UnlinkEpisode_Click') {
    throw "Episode edit/unlink implementation is incomplete."
}
$seriesTestsPath = Join-Path $root "tests/Alostora.RestHouse.Tests/SeriesCatalogServiceTests.cs"
if (-not (Test-Path $seriesTestsPath)) {
    throw "Series catalog regression tests are missing."
}
$seriesTests = Get-Content $seriesTestsPath -Raw
foreach ($testName in @("UpdateEpisodeChangesNumberAndTitleWithoutChangingVideoLink", "UnlinkEpisodeRemovesOnlyTheSeriesLinkAndKeepsOriginalVideoFile", "MissingPhysicalVideoFileIsReportedWithoutRemovingEpisodeRecord", "DuplicateEpisodeNumberAndDuplicateVideoLinksAreRejected")) {
    if (-not $seriesTests.Contains($testName)) { throw ("Missing regression test: " + $testName) }
}
Write-Host "PASS: episode editing, non-destructive unlinking, and duplicate-link regression tests are present."

# Keep one canonical build definition; the manual workflow must reuse it.
$manualWorkflowPath = Join-Path $root ".github/workflows/build-windows.yml"
$mainWorkflowPath = Join-Path $root ".github/workflows/build.yml"
$manualWorkflow = Get-Content $manualWorkflowPath -Raw
$mainWorkflow = Get-Content $mainWorkflowPath -Raw
if ($manualWorkflow -notmatch 'uses:\s*\./\.github/workflows/build\.yml' -or
    $mainWorkflow -notmatch '(?m)^\s*workflow_call:\s*$') {
    throw "Windows build workflows are not using one canonical build definition."
}
Write-Host "PASS: manual and automatic Windows builds share one workflow."

if (Test-Path (Join-Path $root "Mo")) {
    throw "The confirmed empty placeholder file Mo should not be present."
}
if ($series -notmatch 'seasonCount = seasons\.Length' -or
    $series -notmatch 'episodeCount = seasons\.Sum' -or
    $series -notmatch 'rating = ratings\.Length') {
    throw "Series cards are missing their season count, episode count, or average episode rating."
}
Write-Host "PASS: series catalog exposes counts and average rating; empty placeholder is absent."

# The admin experience must expose separate editable registers for videos, series, and categories.
if ($xaml -notmatch '<DataGrid x:Name="VideosGrid"' -or
    $xaml -notmatch '<DataGrid x:Name="SeriesPicker"' -or
    $xaml -notmatch '<DataGrid x:Name="CategoriesGrid"' -or
    $code -notmatch 'MetadataStatus => MetadataSaved' -or
    $code -notmatch 'تمت إضافة بيانات الفيديو' -or
    $code -notmatch 'تمت إضافة المسلسل' -or
    $code -notmatch 'isNew\s*\?\s*"تمت إضافة"' -or $code -notmatch 'var typeName = kind == "Group" \? "القسم الرئيسي" : "القسم الفرعي"') {
    throw "The editable video, series, or category registers are incomplete."
}
Write-Host "PASS: videos, series, and categories have separate editable registers and save confirmations."

# The customer-message list must open details in a modal popup instead of a bottom details panel.
if ($xaml -match 'CustomerMessageDetailsText' -or
    $code -notmatch 'ShowCustomerMessagePopup\(' -or
    $code -notmatch 'SetCustomerMessageStatusAsync\(long id, string status, Window\? popup') {
    throw "Customer message popup workflow is incomplete."
}
Write-Host "PASS: selecting a customer message opens its details and status controls in a popup."

# Branding settings must persist locally and be served to the customer-facing page.
$profileServicePath = Join-Path $root "src/Alostora.RestHouse/RestHouseProfileService.cs"
$profileTestsPath = Join-Path $root "tests/Alostora.RestHouse.Tests/RestHouseProfileServiceTests.cs"
if (-not (Test-Path $profileServicePath) -or -not (Test-Path $profileTestsPath) -or
    $xaml -notmatch 'RestHouseNameBox' -or $xaml -notmatch 'NetworkNameBox' -or
    $xaml -notmatch 'BrandLogoPreview' -or $html -notmatch 'loadBrandProfile\(' -or
    $html -notmatch 'id="brandLogo"' -or
    $code -notmatch 'SaveBrandProfile_Click' -or $app -notmatch 'MapPost\("/api/admin/profile/logo"') {
    throw "Rest house branding settings and customer-facing identity are incomplete."
}
Write-Host "PASS: rest house name, network name, and logo settings are persisted and displayed."

# Advanced media tools are optional helpers, not mandatory customer-facing setup.
if ($xaml -notmatch 'مساعد توافق صيغ الفيديو \(FFmpeg\) — اختياري' -or
    $xaml -notmatch 'لا يغيّر ملف الفيديو الأصلي') {
    throw "The FFmpeg purpose and optional status must be explained in the advanced settings."
}
Write-Host "PASS: FFmpeg is documented as an optional non-destructive playback helper."

# Backup documentation must distinguish application data from original media files.
$backupPath = Join-Path $root "src/Alostora.RestHouse/BackupService.cs"
$backup = Get-Content $backupPath -Raw
if ($backup -notmatch 'customer-page-settings.json' -or
    $backup -notmatch 'Original media files \(videos, audio, books, and apps\) are not included' -or
    $xaml -notmatch 'النسخة الاحتياطية لا تحتوي ملفات الوسائط الأصلية') {
    throw "Backup scope must clearly state that original media files require a separate copy."
}
Write-Host "PASS: backup preserves customer visibility settings and clearly excludes original media files."


# Regression checks for hierarchical category selection, logo reports, and view counts.
if ($xaml -notmatch 'VideoMainCategoryPicker' -or $xaml -notmatch 'SeriesMainCategoryPicker' -or
    $code -notmatch 'RefreshSeriesCategoryPicker\(' -or $code -notmatch 'RefreshVideoCategoryPicker\(') {
    throw "Main-category and dependent subcategory selectors are incomplete."
}
if ($xaml -notmatch 'LogoReportsGrid' -or $html -notmatch 'بلاغ شعار أو علامة تجارية' -or
    $app -notmatch 'بلاغ شعار أو علامة تجارية' -or $code -notmatch 'LogoReportsGrid_SelectionChanged') {
    throw "Logo and trademark reports are not exposed in the customer and admin interfaces."
}
if ($code -notmatch 'CustomerMessagesGrid_MouseDoubleClick' -or $code -notmatch 'LogoReportsGrid_MouseDoubleClick') {
    throw "Customer messages must support reopening details by double-clicking."
}
if ($app -notmatch 'viewCount=viewCounts' -or $html -notmatch 'المشاهدات: ' -or
    (Get-Content (Join-Path $root "src/Alostora.RestHouse/WatchProgressService.cs") -Raw) -notmatch 'GetViewCounts') {
    throw "Per-video viewer counts are not wired from storage to the customer catalog."
}
if ($html -notmatch 'تصميم وبرمجة: محمد النظاري') { throw "Developer attribution footer is missing." }
Write-Host "PASS: hierarchical categories, logo reports, reopenable messages, view counts, and developer attribution are wired."

# The admin media register must support selecting a folder and filtering its videos.
if ($xaml -notmatch 'VideoFoldersTree' -or
    $code -notmatch 'RefreshVideoFoldersTreeAsync\(' -or
    $code -notmatch 'FilterVideosByFolder' -or
    $app -notmatch 'folderId,folderName') {
    throw "The admin folder browser is not connected to the video register."
}
Write-Host "PASS: admin folder browser filters the video register by folder."



# Series organization must support direct videos, seasons, and parts with inherited category placement.
if ($xaml -notmatch 'SeriesStructureBox' -or
    $xaml -notmatch 'VideoSeriesPicker' -or
    $xaml -notmatch 'VideoSeasonPicker' -or
    $code -notmatch 'organizationType\s*=' -or
    $code -notmatch 'seriesId\s*=\s*\(VideoSeriesPicker\.SelectedValue as string\)' -or
    $series -notmatch 'OrganizationType' -or
    $series -notmatch 'SeriesDirectVideos' -or
    $series -notmatch 'SetVideoPlacement\(' -or
    $html -notmatch 'فيديوهات مباشرة' -or
    $html -notmatch "organizationType==='Parts'" -or
    $publicAccess -notmatch 'SeriesDirectVideos') {
    throw "Series organization and video-to-series/season/part placement are incomplete."
}
foreach ($testName in @("SeriesCanUsePartsAndStoreVideosDirectlyUnderTheSeries", "VideoCanMoveBetweenDirectSeriesPlacementAndPartWithoutDeletingTheFile")) {
    if (-not $seriesTests.Contains($testName)) { throw ("Missing series placement regression test: " + $testName) }
}
Write-Host "PASS: standalone series, seasons, parts, direct video links, and category inheritance are wired and regression-tested."

# Additional audio, book and application libraries support independently registered folders.
$multiMedia = Get-Content (Join-Path $root "src/Alostora.RestHouse/MultiMediaLibraryService.cs") -Raw
$mediaTests = Get-Content (Join-Path $root "tests/Alostora.RestHouse.Tests/MultiMediaLibraryServiceTests.cs") -Raw
if ($app -notmatch '/api/admin/media-libraries/roots' -or
    $app -notmatch '/api/admin/storage-summary' -or
    $xaml -notmatch 'MediaLibraryRootsGrid' -or
    $xaml -notmatch 'StorageSummaryText' -or
    $code -notmatch 'AddMediaLibraryRootAsync' -or
    $code -notmatch 'RemoveMediaLibraryRoot_Click' -or
    $multiMedia -notmatch 'AddRoot\(' -or
    $multiMedia -notmatch 'GetStorageSummary\(' -or
    $mediaTests -notmatch 'SupportsMultipleIndependentRootsForEachMediaKind' -or
    $mediaTests -notmatch 'RemovingExternalRootUnlinksItWithoutDeletingOriginalFiles' -or
    $mediaTests -notmatch 'RejectsOverlappingRootsToAvoidDuplicateCatalogEntries') {
    throw "Multiple media source folders, safe unlinking, and storage reporting are incomplete."
}
Write-Host "PASS: audio, books and apps support multiple source folders, safe unlinking, and storage reporting."
# Customer download center supports tracked downloads and HTTP byte-range resumption.
$downloadService = Get-Content (Join-Path $root "src/Alostora.RestHouse/DownloadProgressService.cs") -Raw
$downloadTests = Get-Content (Join-Path $root "tests/Alostora.RestHouse.Tests/DownloadProgressServiceTests.cs") -Raw
if ($app -notmatch '/api/downloads/\{clientId\}' -or
    $app -notmatch '/api/media-library/\{id\}/download' -or
    $app -notmatch 'Accept-Ranges' -or
    $html -notmatch 'id="downloadCenter"' -or
    $html -notmatch 'function renderDownloads\(' -or
    $html -notmatch 'showDownloads' -or
    $app -notmatch '/api/downloads/\{clientId\}/\{downloadId\}' -or
    $xaml -notmatch 'CustomerShowDownloadsBox' -or
    $downloadService -notmatch 'Interrupt\(' -or
    $downloadTests -notmatch 'TracksProgressAndCompletesDownload' -or
    $downloadTests -notmatch 'ResumeStartsFromRequestedRangeAndKeepsClientHistoriesSeparate') {
    throw "Customer download progress, visibility controls, or resumable range support are incomplete."
}
Write-Host "PASS: customer downloads expose progress, resume-friendly byte ranges, and admin visibility control."
# Optional aggregate download bandwidth limit is managed by the rest-house admin app.
$speedService = Get-Content (Join-Path $root "src/Alostora.RestHouse/DownloadSpeedLimitService.cs") -Raw
$speedTests = Get-Content (Join-Path $root "tests/Alostora.RestHouse.Tests/DownloadSpeedLimitServiceTests.cs") -Raw
if ($app -notmatch '/api/admin/download-speed-limit' -or
    $app -notmatch 'speedLimit.WaitAsync' -or
    $xaml -notmatch 'DownloadSpeedLimitBox' -or
    $code -notmatch 'SaveDownloadSpeedLimit_Click' -or
    $speedService -notmatch 'WaitAsync\(' -or
    $speedTests -notmatch 'DefaultsToUnlimitedAndPersistsConfiguredLimit' -or
    $backup -notmatch 'download-speed-settings.json') {
    throw "Optional download speed limit, admin controls, or backup coverage are incomplete."
}
Write-Host "PASS: aggregate download speed limit is optional, admin-controlled, and backed up."
# Customer image-ad gallery is local, admin-managed, and independently visible.
$customerAdsService = Get-Content (Join-Path $root "src/Alostora.RestHouse/CustomerAdService.cs") -Raw
$customerAdsTests = Get-Content (Join-Path $root "tests/Alostora.RestHouse.Tests/CustomerAdServiceTests.cs") -Raw
if ($app -notmatch '/api/customer-ads' -or
    $app -notmatch '/api/admin/customer-ads' -or
    $xaml -notmatch 'CustomerAdsGrid' -or
    $xaml -notmatch 'CustomerShowImageAdsBox' -or
    $code -notmatch 'AddCustomerAd_Click' -or
    $html -notmatch 'id="customerAds"' -or
    $html -notmatch 'function renderCustomerAd\(' -or
    $customerAdsService -notmatch 'MaximumImageBytes' -or
    $customerAdsTests -notmatch 'RejectsUnsupportedOrMismatchedImagesAndUnsafeLinks' -or
    $backup -notmatch 'customer-ads.json') {
    throw "Customer image-ad gallery, admin controls, or backup support are incomplete."
}
Write-Host "PASS: customer image ads support safe local upload, carousel display, admin control, and backup."
# This product build intentionally excludes all live-broadcast routes, controls, and player assets.
if ($app -match 'MapGet\("/live"' -or
    $xaml -match 'CopyLiveAddress_Click|LiveUrlText' -or
    $code -match 'CopyLiveAddress_Click|LiveUrlText' -or
    (Test-Path (Join-Path $root "src/Alostora.RestHouse/wwwroot/live.html")) -or
    (Test-Path (Join-Path $root "src/Alostora.RestHouse/wwwroot/hls.min.js")) -or
    (Test-Path (Join-Path $root "src/Alostora.RestHouse/wwwroot/HLSJS-LICENSE.txt"))) {
    throw "Live broadcast routes, controls, or assets must not be included in this ALOSTORA REST HOUSE build."
}
Write-Host "PASS: no live-broadcast route, admin control, player page, or HLS assets are included."
