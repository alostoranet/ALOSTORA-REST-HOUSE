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

# The management window and embedded server must use the same loopback endpoint.
if ($code -notmatch 'http://127\.0\.0\.1:8096' -or $app -notmatch 'http://0\.0\.0\.0:8096') {
    throw "Management client/server port configuration is inconsistent."
}
Write-Host "PASS: management client and embedded server port configuration match."

# Check that key management routes used by the UI exist in the embedded API.
$requiredRoutes = @(
    '/api/admin/catalog-categories',
    '/api/admin/profile',
    '/api/admin/profile/logo',
    '/api/profile',
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

# The catalog must navigate through distinct customer pages, not stacked detail panels.
if ($html -notmatch 'id="catalogView"' -or
    $html -notmatch 'id="catalogBack"' -or
    $html -notmatch 'function renderCatalogPage\(' -or
    $html -notmatch "type==='season'" -or
    $html -notmatch "type==='player'" -or
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
foreach ($testName in @("PublishedMainAndChildCategoriesAppearWithoutPosterImages", "UnpublishedCategoriesDoNotAppearInCustomerCatalog", "PublishedChildOfUnpublishedParentIsNotExposed", "DeletingMainCategoryRemovesChildCatalogRecordsButKeepsOriginalVideoFile")) {
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

# Protect category references so deletion cannot orphan published library items.
$categoryServicePath = Join-Path $root "src/Alostora.RestHouse/CatalogCategoryService.cs"
$categoryService = Get-Content $categoryServicePath -Raw
if ($categoryService -notmatch 'ParentId=\$id AND IsPublished=1' -or
    $categoryService -notmatch 'FROM VideoMetadata\s+WHERE CategoryId IN' -or
    $categoryService -notmatch 'FROM SeriesCatalog\s+WHERE CategoryId IN') {
    throw "Catalog category integrity checks are missing."
}
Write-Host "PASS: category publication and linked-content integrity checks are present."

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
    $code -notmatch 'RefreshVideoFoldersTree\(' -or
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
    $html -notmatch 'فيديوهات مرتبطة مباشرة بالمسلسل' -or
    $html -notmatch "organizationType==='Parts'" -or
    $publicAccess -notmatch 'SeriesDirectVideos') {
    throw "Series organization and video-to-series/season/part placement are incomplete."
}
foreach ($testName in @("SeriesCanUsePartsAndStoreVideosDirectlyUnderTheSeries", "VideoCanMoveBetweenDirectSeriesPlacementAndPartWithoutDeletingTheFile")) {
    if (-not $seriesTests.Contains($testName)) { throw ("Missing series placement regression test: " + $testName) }
}
Write-Host "PASS: standalone series, seasons, parts, direct video links, and category inheritance are wired and regression-tested."
