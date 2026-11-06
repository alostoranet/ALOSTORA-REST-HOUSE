$ErrorActionPreference = "Stop"
$root = Join-Path $PSScriptRoot ".."
$xamlPath = Join-Path $root "src/Alostora.RestHouse/MainWindow.xaml"
$codePath = Join-Path $root "src/Alostora.RestHouse/MainWindow.xaml.cs"
$appPath = Join-Path $root "src/Alostora.RestHouse/App.xaml.cs"
$htmlPath = Join-Path $root "src/Alostora.RestHouse/wwwroot/index.html"
$metadataPath = Join-Path $root "src/Alostora.RestHouse/VideoMetadataService.cs"
$seriesPath = Join-Path $root "src/Alostora.RestHouse/SeriesCatalogService.cs"

$xaml = Get-Content $xamlPath -Raw
$code = Get-Content $codePath -Raw
$app = Get-Content $appPath -Raw
$html = Get-Content $htmlPath -Raw
$metadata = Get-Content $metadataPath -Raw
$series = Get-Content $seriesPath -Raw

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

# The Home control is hidden only on the home page; internal pages retain Home and a compact contact entry in the persistent header.
if ($html -notmatch 'id="homeView"' -or
    $html -notmatch 'id="contactView"[^>]*hidden' -or
    $html -notmatch 'id="homeNav"[^>]*hidden' -or
    $html -notmatch 'id="contactNav"' -or
    $html -notmatch 'id="continueNav"' -or
    $html -notmatch 'function goCatalogHome\(' -or
    $html -notmatch "showCustomerView\('contact'\)") {
    throw "Customer navigation must hide Home only on the landing page and retain Home/contact controls on inner pages."
}
Write-Host "PASS: Home is hidden only on the landing page; internal pages retain Home and contact navigation."
# The customer home must summarize published library content and surface genuinely popular titles.
if ($html -notmatch 'id="customerOverview"' -or
    $html -notmatch 'id="overviewCategoryCount"' -or
    $html -notmatch 'id="overviewVideoCount"' -or
    $html -notmatch 'id="overviewSeriesCount"' -or
    $html -notmatch 'id="popularSection"' -or
    $html -notmatch 'function renderCustomerOverview\(' -or
    $html -notmatch 'Number\(b\.viewCount\|\|0\)-Number\(a\.viewCount\|\|0\)' -or
    $html -notmatch 'renderCustomerOverview\(\)') {
    throw "Customer home overview and popularity discovery are incomplete."
}
Write-Host "PASS: customer home shows published library counts and popular titles from real view counts."
if (-not $html.Contains("document.querySelector('#customerOverview').hidden=true;") -or
    -not $html.Contains("document.querySelector('#welcomePanel').hidden=true;")) {
    throw "Customer home discovery panels must remain hidden on the category-first landing page."
}
Write-Host "PASS: customer home discovery panels are scoped to the home page."
# Catalog tiles must expose a real keyboard-operable button and must not nest a favorite button inside a role=button article.
if ($html -notmatch "className='catalog-open-button'" -or
    $html -notmatch "open\.setAttribute\('aria-label'" -or
    $html -match "card\.setAttribute\('role','button'\)" -or
    $html -notmatch 'catalog-open-button:focus-visible') {
    throw "Customer catalog cards must use accessible native buttons with visible keyboard focus."
}
Write-Host "PASS: customer catalog cards use accessible native buttons and visible keyboard focus."
# Continue watching is a compact navigation entry and a dedicated page, never an inline home-page shelf.
if ($html -notmatch 'id="continueNav"' -or
    $html -notmatch "continueSection\.hidden=pageType!=='continue'" -or
    $html -notmatch "if\(pageType==='continue'\)" -or
    $html -notmatch "navigateCatalog\(\{type:'continue'" -or
    $html -notmatch "document\.querySelector\('#customerOverview'\)\.hidden=true") {
    throw "Continue watching must open as a separate page and the home page must stay focused on main categories."
}
Write-Host "PASS: continue watching opens from a compact navigation entry; the home page stays focused on main categories."

$watchService = Get-Content (Join-Path $root "src/Alostora.RestHouse/WatchProgressService.cs") -Raw
$appSource = Get-Content (Join-Path $root "src/Alostora.RestHouse/App.xaml.cs") -Raw
if (-not $html.Contains('id="watchedSection"') -or
    -not $html.Contains('id="watchedGrid"') -or
    -not $html.Contains('مشاهداتي') -or
    -not $html.Contains('أكمل المشاهدة') -or
    -not $html.Contains('شاهدتها مؤخرًا') -or
    -not $html.Contains('watchHistoryRows') -or
    -not $watchService.Contains('GetViewerHistory') -or
    -not $appSource.Contains('"/api/watch-history/{viewerId}"') -or
    -not $html.Contains('id="contactNav"') -or
    -not $html.Contains('تواصل مع الإدارة') -or
    -not $html.Contains('class="credit-pair"') -or
    -not $html.Contains('id="contactCta"') -or
    -not $html.Contains('class="footer-contact-button"') -or
    $html.IndexOf('id="contactCta"') -gt $html.IndexOf('class="developer-credit"') -or
    -not $html.Contains("document.querySelector('#contactCta').addEventListener('click',openCustomerContact)")) {
    throw "The customer page must provide a viewer-scoped watched history and compact contact navigation above the content."
}
Write-Host "PASS: customer watch hub shows resume and watched videos; compact contact navigation is in the header."






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
if ($html -notmatch 'announcement-message-marquee' -or
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
    $html -match 'id="seriesDetails"' -or    $html -match 'id="movieDetails"') {
    throw "Customer catalog must use separate category, program, season, and player pages."
}
Write-Host "PASS: customer catalog uses separate navigation pages and a dedicated player view."
# The landing page hides Home; inside catalog pages Home and Back remain adjacent and functional.
if (-not $html.Contains('<video id="player" controls playsinline preload="metadata"></video>') -or
    $html.Contains('id="nowPlayingTitle"') -or
    -not $html.Contains('تصميم وبرمجة الاستراحة') -or
    -not $html.Contains('م/ أبو عبيدة محمد النظاري') -or
    -not $html.Contains('href="https://wa.me/967777824918"') -or
    -not $html.Contains('<bdi dir="ltr">777824918</bdi>')) {
    throw "Customer player must show only the single breadcrumb title above the video, and footer must include the designer credit with a WhatsApp link displaying the local number only."
}

if (-not $html.Contains('id="catalogBack"') -or
    -not $html.Contains("pageHead.hidden=pageType==='home'") -or
    -not $html.Contains("customerNav.hidden=false") -or
    -not $html.Contains("homeNav.hidden=pageType==='home'&&document.querySelector('#contactView').hidden") -or
    -not $html.Contains("toolbar.hidden=pageType!=='category'") -or
    -not $html.Contains("crumb.textContent=catalogPage.title") -or
    ($html -notmatch 'catalog-player-page video|#playerSection video')) {
    throw "Customer navigation must hide Home only on the landing page and keep Home/Back controls functional inside catalog pages."
}
if ($html -notmatch 'catalog-back' -or $html -notmatch 'catalog-home' -or $html -notmatch 'customer-ad-stage\{aspect-ratio:16/7;min-height:95px') {
    throw "Mobile catalog controls or compact ad spacing are missing."
}
Write-Host "PASS: Home is hidden on landing only; inner pages expose Home and Back together with compact category filters."

$hasDuplicateSeriesHeading = $html.Contains('content.append(catalogSectionTitle(main.title))') -or $html.Contains('content.append(catalogSectionTitle(category.title))')
$hasSeriesHero = $html.Contains('catalogHero(show.title,show.posterPath,seriesMeta,show.description,false)') -or $html.Contains('catalogHero(name,season.posterPath||show.posterPath,seasonMeta,show.description,true)')
$hasCompactFilters = $html.Contains('id="categoryFilter" class="header-filter"') -and $html.Contains('id="sort" class="header-filter"')
$filtersOutsideSearch = $html.Contains('<div class="header-search">') -and $html.Substring($html.IndexOf('<div class="header-search">'), $html.IndexOf('</header>') - $html.IndexOf('<div class="header-search">')).Contains('id="sort"')
$filtersInContentHeader = $html.Contains('id="catalogContentFilters"') -and $html.Contains("contentFilters.hidden=!(pageType==='category'||pageType==='series'||pageType==='season'||pageType==='continue')")
if ($hasDuplicateSeriesHeading -or $hasSeriesHero -or -not $hasCompactFilters -or $filtersOutsideSearch -or -not $filtersInContentHeader -or -not $html.Contains("categoryFilter.value=(pageType==='main'||pageType==='category')")) {
    throw "Filters must be outside search, inside the content header only, and hidden on the main sections page; series/season pages must not repeat hero posters."
}
Write-Host "PASS: filters are placed with content, not beside search; main sections stay clean and series/season pages avoid duplicate hero posters."



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
if ($series -notmatch 'allowedVideoIds is null \|\| allowedVideoIds\.Contains\(videoId\)' -or
    $html -match 'catalogHero\(show\.title,show\.posterPath,seriesMeta,show\.description,false\)' -or
    $html -notmatch 'episodeCount=seasons\.reduce') {
    throw "Customer series navigation must avoid a duplicate hero poster while preserving public episode publication filtering."
}
if ($seriesTests -notmatch 'PublicSeriesCatalogOmitsEpisodesWhoseVideosAreNotPublished') {
    throw "Public series episode visibility regression test is missing."
}
Write-Host "PASS: series details show useful metadata and public catalogs omit unpublished episodes."

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
if ($xaml -notmatch 'مساعد توافق صيغ الفيديو \(FFmpeg\) — اختياري' -or    $xaml -notmatch 'لا يغيّر ملف الفيديو الأصلي') {
    throw "The FFmpeg purpose and optional status must be explained in the advanced settings."
}
Write-Host "PASS: FFmpeg is documented as an optional non-destructive playback helper."

# Backup documentation must distinguish application data from original media files.
$backupPath = Join-Path $root "src/Alostora.RestHouse/BackupService.cs"
$backup = Get-Content $backupPath -Raw
if ($backup -notmatch 'customer-page-settings.json' -or
    $backup -notmatch 'settings\.json' -or
    $backup -notmatch 'Original media files \(videos, audio, books, and apps\) are not included' -or
    $xaml -notmatch 'النسخة الاحتياطية لا تحتوي ملفات الوسائط الأصلية') {
    throw "Backup scope must preserve application settings and clearly state that original media files require a separate copy."
}
Write-Host "PASS: backup preserves customer visibility and application settings, and clearly excludes original media files."

# Runtime diagnostics and the visible WPF management workflow must remain covered.
$appSource = Get-Content (Join-Path $root "src/Alostora.RestHouse/App.xaml.cs") -Raw
$wpfSmoke = Get-Content (Join-Path $root "tests/Alostora.RestHouse.WpfSmokeTest/Program.cs") -Raw
if ($appSource -notmatch 'DispatcherUnhandledException' -or
    $appSource -notmatch 'AppDomain\.CurrentDomain\.UnhandledException' -or
    $appSource -notmatch 'TaskScheduler\.UnobservedTaskException' -or
    $appSource -notmatch 'application-errors\.log' -or
    $code -notmatch 'InitializeManagementWindowSafelyAsync' -or
    $code -notmatch 'RefreshCustomerMessagesSafelyAsync' -or
    $wpfSmoke -notmatch 'app\.Run\(window\)' -or
    $wpfSmoke -notmatch 'SaveCustomerPageSettings_Click') {
    throw "Global diagnostics, safe management initialization, or visible live URL save smoke coverage is incomplete."
}
Write-Host "PASS: global exception diagnostics, safe management initialization, and visible live URL save smoke coverage are present."


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
if ($app -notmatch 'viewCount=viewCounts' -or $html -notmatch 'viewCount' -or
    (Get-Content (Join-Path $root "src/Alostora.RestHouse/WatchProgressService.cs") -Raw) -notmatch 'GetViewCounts') {
    throw "Per-video viewer counts are not wired from storage to the customer catalog."
}
if ($html -notmatch 'تصميم وبرمجة الاستراحة' -or $html -notmatch 'م/ أبو عبيدة محمد النظاري' -or $html -notmatch 'https://wa.me/967777824918' -or $html -notmatch '<bdi dir="ltr">777824918</bdi>') { throw "Developer attribution and WhatsApp footer are missing." }
Write-Host "PASS: hierarchical categories, logo reports, reopenable messages, view counts, and developer attribution are wired."

# The admin media register must support selecting a folder and filtering its videos.
if ($xaml -notmatch 'VideoFoldersTree' -or
    $code -notmatch 'RefreshVideoFoldersTreeAsync\(' -or
    $code -notmatch 'FilterVideosByFolder' -or
    $app -notmatch 'folderId,folderName') {
    throw "The admin folder browser is not connected to the video register."
}
Write-Host "PASS: admin folder browser filters the video register by folder."
$librarySelectionTests = Get-Content (Join-Path $root "tests/Alostora.RestHouse.Tests/LibraryServiceFolderTests.cs") -Raw
if ($librarySelectionTests -notmatch 'SelectingAnExternalFolderRegistersOnlyTheFilesTheUserChecked' -or
    $code -notmatch 'register-selected-files' -or
    $app -notmatch 'files\.RegisterSelectedFiles' -or
    $xaml -notmatch 'AddExternalMediaFolder_Click') {
    throw "External folder selection must add only the files explicitly selected by the user."
}
Write-Host "PASS: external folder import is covered by a selected-files-only regression test."




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
# Each registered media root must have its own size and availability row in the admin panel.
$multiMediaTests = Get-Content (Join-Path $root "tests/Alostora.RestHouse.Tests/MultiMediaLibraryServiceTests.cs") -Raw
if ($xaml -notmatch 'MediaStorageRootsGrid' -or
    $code -notmatch 'MediaStorageRootAdmin' -or
    $code -notmatch 'FolderStatusLabel' -or
    $code -notmatch 'DriveStatusLabel' -or
    $code -notmatch 'MediaStorageRootsGrid.ItemsSource' -or
    $multiMediaTests -notmatch 'StorageSummaryReportsIndexedSizeForEachRegisteredRoot') {
    throw "Per-folder indexed size and drive/folder availability reporting is incomplete."
}
Write-Host "PASS: each registered media folder reports its own size and availability status."

# The optional live entry is URL-only: admin stores an external HTTP(S) link and customers open it in a new tab.
if ($xaml -notmatch 'CustomerLiveBroadcastUrlBox' -or
    $code -notmatch 'CustomerLiveBroadcastUrlBox\.Text = settings\.LiveBroadcastUrl' -or
    $code -notmatch 'liveBroadcastUrl = ' -or
    $app -notmatch 'request\.LiveBroadcastUrl' -or
    $html -notmatch 'id="liveBroadcastNav"' -or
    $html -notmatch 'liveBroadcastNav\.href=validLiveUrl\?liveUrl') {
    throw "The optional external live URL setting is not fully wired from admin to customer page."
}
# Do not add an in-app live server, stream routes, player page, or HLS playback assets.
if ($app -match 'MapGet\("/live"' -or
    $app -match 'Map(Get|Post)\("/api/live' -or
    $xaml -match 'CopyLiveAddress_Click|LiveUrlText' -or
    $code -match 'CopyLiveAddress_Click|LiveUrlText' -or
    (Test-Path (Join-Path $root "src/Alostora.RestHouse/wwwroot/live.html")) -or
    (Test-Path (Join-Path $root "src/Alostora.RestHouse/wwwroot/hls.min.js")) -or
    (Test-Path (Join-Path $root "src/Alostora.RestHouse/wwwroot/HLSJS-LICENSE.txt"))) {
    throw "In-app live streaming routes, player controls, or HLS assets must not be included."
}
Write-Host "PASS: external live URL only; no in-app live server, player, or HLS assets."
# Series cover selection must preview immediately, after upload/lookup, and when an existing series is selected.
if ($xaml -notmatch 'x:Name="SeriesPosterPreview"' -or
    $code -notmatch 'UpdateSeriesPosterPreview\(s\.PosterPath\)' -or
    $code -notmatch 'UpdateSeriesPosterPreview\(path,\s*isLocalFile:\s*true\)' -or
    $code -notmatch 'UpdateSeriesPosterPreview\(result\?\.PosterPath\)' -or
    $code -notmatch 'UpdateSeriesPosterPreview\(result\.PosterPath\)') {
    throw "Series poster selection is missing its visible preview workflow."
}
Write-Host "PASS: series posters preview on selection, before upload, after upload, and after metadata lookup."

# Published standalone videos must be assigned a customer-facing subcategory before saving.
if ($code -notmatch 'PublishBox\.IsChecked\s*==\s*true\s*&&\s*selectedCategory\s+is\s+null') {
    throw "Published videos must be linked to a valid customer-facing subcategory."
}
Write-Host "PASS: published videos cannot be saved without a customer-facing subcategory."

# Announcement ticker is a centered, red breaking-news strip with continuous readable motion.
if ($html -notmatch 'announcement-message-marquee 18s linear .3s infinite' -or
    $html -notmatch 'background:linear-gradient\(100deg,#9f1420,#d3232d 52%,#a91420\)' -or
    $html -notmatch 'text-align:center' -or
    $html -notmatch 'const changed=banner.dataset.contentKey!==contentKey' -or
    $html -notmatch 'prefers-reduced-motion:reduce' -or
    $html -match 'announcement-banner:hover #announcementMessage\\{animation-play-state:paused\\}' -or
    $html -notmatch '<span id="announcementMessage"></span>') {
    throw "Customer red breaking-news ticker, refresh behavior, or reduced-motion support is incomplete."
}
Write-Host "PASS: announcement ticker uses a red breaking-news strip with centered scrolling text and reduced-motion support."


# Customer search stays below navigation and new arrivals appear only when recently updated content exists.
if ($html.IndexOf('id="continueNav"') -gt $html.IndexOf('class="header-search"') -or
    $html -notmatch 'id="newArrivals"' -or
    $html -notmatch 'function isRecentlyAdded\(' -or
    $html -notmatch 'isRecentlyAdded\(x\.updatedUtc\)' -or
    $html -notmatch 'recentEpisodes\.push' -or
    $html -notmatch 'section\.hidden=rows\.length===0' -or
    $metadata -notmatch 'bool IsPublished, string UpdatedUtc' -or
    $series -notmatch 'string OrganizationType, string UpdatedUtc') {
    throw "Customer search placement or dynamic new-arrivals metadata is incomplete."
}
Write-Host "PASS: compact search sits below navigation; new movies, series, and episodes are shown only while recently updated."
# Customer-facing category cards show real names only; no implementation labels leak into the catalog.
if ($html -match 'الأقسام الداخلية|قسم مسلسلات|قسم فيديوهات|أقسام داخلية' -or
    $html -notmatch 'class="media-quick-nav"' -or
    $html -notmatch 'class="customer-nav" aria-label="التنقل الرئيسي"' -or
    $html -notmatch 'catalog-card h3\{[^}]*font-size:19px') {
    throw "Customer catalog still exposes internal labels or cluttered navigation, or lacks clear section titles."
}
Write-Host "PASS: customer home uses clean primary navigation, named sections, and large readable category titles."





# Customer playback keeps its LAN fallback; season pages show episodes directly without a duplicate hero poster.
if ($html -notmatch "item=\{\.\.\.item,url:'/media/'\+encodeURIComponent\(item\.id\)\}" -or
    $html -match "catalogHero\(name,season\.posterPath\|\|show\.posterPath" -or
    $html -notmatch "catalogSectionTitle\('حلقات '\+name\)") {
    throw "Customer playback fallback or single-poster season layout regression detected."
}
Write-Host "PASS: catalog playback has a canonical LAN media URL fallback; season pages show episodes without duplicate hero posters."
# Contact is available in the header and again on every customer page directly above the enlarged developer credit.
if ($html -notmatch '<nav class="customer-nav"[^>]*>\s*<button id="homeNav"' -or
    $html -notmatch 'id="contactNav"' -or
    $html -notmatch 'id="contactCta" class="footer-contact-button"' -or
    $html -notmatch 'تواصل مع الإدارة للطلب والاستفسار' -or
    $html -notmatch "document\.querySelector\('#contactCta'\)\.addEventListener\('click',openCustomerContact\)" -or
    $html -notmatch 'class="credit-pair"' -or
    $html -notmatch 'developer-credit strong\{color:#e4d1a8;font-size:14px' -or
    $html -notmatch 'compact-top-nav') {
    throw "Customer contact buttons or enlarged developer credit layout regressed."
}
Write-Host "PASS: contact actions are available in the header and above the developer credit on every customer page."
# Announcement ticker text must have vertical room and must not replace its continuous marquee animation with an entry animation.
if ($html -notmatch 'announcement-banner p\{display:flex;align-items:center' -or
    $html -notmatch 'announcement-message-marquee 18s linear .3s infinite' -or
    $html -match 'announcement-banner\.is-entering #announcementMessage\{animation:announcement-message-reveal') {
    throw "Announcement ticker vertical clipping or continuous motion regression detected."
}
Write-Host "PASS: announcement ticker text has vertical padding and retains its continuous marquee."
# The Windows management app exposes a safe placeholder for future official updates.
if ($xaml -notmatch 'Content="التحديثات" Click="Updates_Click"' -or
    $code -notmatch 'void Updates_Click\(' -or
    $code -notmatch 'سيتم توفير خدمة التحديثات قريباً') {
    throw "The Windows app must expose the prepared updates action without requiring an update URL yet."
}
Write-Host "PASS: the Windows app includes a prepared updates button and a clear not-yet-available message."

# App updates belong only to the Windows management app; the customer page must not expose update controls.
if ($xaml -notmatch 'Content="التحديثات" Click="Updates_Click"' -or
    $code -notmatch 'void Updates_Click\(' -or
    $code -notmatch 'سيتم توفير خدمة التحديثات قريباً') {
    throw "The Windows management app must retain its prepared updates action."
}
if ($html -match 'updatesCta|footer-updates-button|سيتم توفير خدمة التحديثات قريباً' -or
    $html -match "document\.querySelector\('#updatesCta'\)") {
    throw "The customer page must not expose application update controls or update placeholder messages."
}
Write-Host "PASS: app updates are available in Windows management only; the customer page has no update control or placeholder."
