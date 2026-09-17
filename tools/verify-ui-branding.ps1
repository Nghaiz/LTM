[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$unityRoot = Join-Path $repoRoot 'Ironfront_Reborn'

function Read-ProjectFile([string] $relativePath) {
    Get-Content -Raw -LiteralPath (Join-Path $unityRoot $relativePath)
}

$settings = Read-ProjectFile 'ProjectSettings/ProjectSettings.asset'
$splash = Read-ProjectFile 'Assets/Scenes/Splash.unity'
$menu = Read-ProjectFile 'Assets/Scenes/Menu.unity'
$legacyMenuCode = Read-ProjectFile 'Assets/Scripts/Assembly-CSharp/MainMenu.cs'

$required = @(
    @{ Name = 'company'; Text = $settings; Pattern = 'companyName: Team 10 LTM' },
    @{ Name = 'product'; Text = $settings; Pattern = 'productName: Ironfront Reborn' },
    @{ Name = 'identifier'; Text = $settings; Pattern = 'Standalone: com.team10ltm.ironfrontreborn' },
    @{ Name = 'splash title'; Text = $splash; Pattern = 'm_Text: IRONFRONT REBORN' },
    @{ Name = 'splash team'; Text = $splash; Pattern = 'm_Text: TEAM 10 LTM PRESENTS' }
)

$forbidden = @(
    @{ Name = 'splash'; Text = $splash },
    @{ Name = 'menu'; Text = $menu },
    @{ Name = 'legacy menu code'; Text = $legacyMenuCode }
)

$errors = [System.Collections.Generic.List[string]]::new()
foreach ($check in $required) {
    if (-not $check.Text.Contains($check.Pattern)) {
        $errors.Add("$($check.Name) is missing '$($check.Pattern)'")
    }
}

foreach ($check in $forbidden) {
    foreach ($oldBrand in @('Ravenfield', 'SteelRaven7', 'Johan Hassel')) {
        if ($check.Text.IndexOf($oldBrand, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            $errors.Add("$($check.Name) still contains inherited brand '$oldBrand'")
        }
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Error $_ }
    exit 1
}

Write-Host '[ui-branding] PASS - Ironfront Reborn / Team 10 LTM identity is clean.'
