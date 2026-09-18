param(
    [string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$failures = [System.Collections.Generic.List[string]]::new()
$packRoot = Join-Path $RepositoryRoot 'ironfront-reborn-ui-pack (1)'
$unityRoot = Join-Path $RepositoryRoot 'Ironfront_Reborn/Assets/UI/IronfrontReborn'
$htmlPath = Join-Path $packRoot 'index.html'
$catalogPath = Join-Path $RepositoryRoot 'Ironfront_Reborn/Assets/Editor/NetVerification/IronfrontRebornUiAssetCatalog.cs'
$builderPath = Join-Path $RepositoryRoot 'Ironfront_Reborn/Assets/Editor/NetVerification/BuildMenuCanvas.cs'
$settingsPath = Join-Path $RepositoryRoot 'Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuSettingsScreen.cs'
$createRoomPath = Join-Path $RepositoryRoot 'Ironfront_Reborn/Assets/Scripts/Net/Client/Menu/MenuCreateRoomScreen.cs'
$scenePath = Join-Path $RepositoryRoot 'Ironfront_Reborn/Assets/Scenes/Menu.unity'

$html = Get-Content -Raw -LiteralPath $htmlPath
if ($html -match '\.svg') { $failures.Add('HTML still references SVG') }
$imageMatches = [regex]::Matches($html, '<img[^>]+src="([^"]+)"')
foreach ($match in $imageMatches) {
    $relative = $match.Groups[1].Value.Replace('/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path -LiteralPath (Join-Path $packRoot $relative))) {
        $failures.Add("Missing HTML image: $($match.Groups[1].Value)")
    }
}

$catalog = Get-Content -Raw -LiteralPath $catalogPath
if ($catalog -match '\.svg') { $failures.Add('Unity UI catalogue still references SVG') }
if ($catalog -notmatch 'textureCompression\s*=\s*TextureImporterCompression\.Uncompressed') {
    $failures.Add('Unity UI catalogue does not force uncompressed textures')
}
if ($catalog -notmatch 'mipmapEnabled\s*=\s*false') {
    $failures.Add('Unity UI catalogue does not disable mipmaps')
}

$builder = Get-Content -Raw -LiteralPath $builderPath
if ($builder -notmatch 'canvas\.pixelPerfect\s*=\s*true') {
    $failures.Add('Generated menu Canvas is not pixel-perfect')
}
if ($builder -notmatch 'Assets/Font/Roboto-Regular\.ttf') {
    $failures.Add('Menu labels do not load the bundled Roboto font')
}
if ($builder -notmatch 'Assets/Font/Roboto-Bold\.ttf') {
    $failures.Add('Menu headings do not load the bundled Roboto Bold font')
}
if ($builder -notmatch '"Versus"') {
    $failures.Add('Waiting Room does not author the central versus treatment')
}

$settings = Get-Content -Raw -LiteralPath $settingsPath
if ($settings -notmatch '_categoryGroups') {
    $failures.Add('Settings tabs do not switch real category groups')
}

$createRoom = Get-Content -Raw -LiteralPath $createRoomPath
foreach ($field in @('_mapPreviewCapacity', '_mapPreviewBots', '_mapPreviewSecurity')) {
    if ($createRoom -notmatch [regex]::Escape($field)) {
        $failures.Add("Create Room preview is missing runtime field $field")
    }
}

$scene = Get-Content -Raw -LiteralPath $scenePath
if ($scene -match '\.svg') { $failures.Add('Generated Menu scene still references SVG') }
if ($scene -notmatch 'm_PixelPerfect: 1') { $failures.Add('Generated Menu scene is not pixel-perfect') }
foreach ($screenName in @('Main Menu', 'Sign In', 'Create Account', 'Practice', 'Settings', 'Rooms', 'Create Room', 'Waiting Room')) {
    if ($scene -notmatch ('m_Name: ' + [regex]::Escape($screenName) + '(\r?\n)')) {
        $failures.Add("Generated Menu scene is missing screen $screenName")
    }
}
foreach ($nodeName in @('ForgotPassword', 'DisplayGroup', 'AudioGroup', 'GameplayGroup', 'Versus')) {
    if ($scene -notmatch ('m_Name: ' + [regex]::Escape($nodeName) + '(\r?\n)')) {
        $failures.Add("Generated Menu scene is missing node $nodeName")
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Output "Menu UI asset verification passed ($($imageMatches.Count) HTML image references)."
