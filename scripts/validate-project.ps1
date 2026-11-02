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

# A category may be published without a poster; new category forms default to published.
if ($app -match 'request\.IsPublished\s*&&\s*!PosterExists\(request\.PosterPath,posters\)' -or
    $xaml -notmatch 'CategoryPublishBox"[^>]*IsChecked="True"' -or
    $code -notmatch 'CategoryPublishBox\.IsChecked\s*=\s*true') {
    throw "New customer categories must publish by default and must not require a poster."
}
$categoryTestsPath = Join-Path $root "tests/Alostora.RestHouse.Tests/CatalogCategoryServiceTests.cs"
if (-not (Test-Path $categoryTestsPath)) { throw "Catalog category regression tests are missing." }
$categoryTests = Get-Content $categoryTestsPath -Raw
foreach ($testName in @("PublishedMainAndChildCategoriesAppearWithoutPosterImages", "UnpublishedCategoriesDoNotAppearInCustomerCatalog", "PublishedChildOfUnpublishedParentIsNotExposed")) {
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
