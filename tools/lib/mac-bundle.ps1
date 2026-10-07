# tools/lib/mac-bundle.ps1 -- facts about a macOS .app bundle, read on Windows.
#
# Dot-sourced by build-player.ps1 and package-release.ps1.

Set-StrictMode -Version Latest

<#
.SYNOPSIS
The path of the executable an .app bundle starts, as its Info.plist names it.
.DESCRIPTION
Unity names Contents/MacOS/<file> after PlayerSettings.productName (IronfrontReborn), not after
the .app (Ironfront.app), so the name is read from CFBundleExecutable -- the key macOS itself
uses -- rather than guessed. A regex rather than an XML parser: the plist carries Apple's DOCTYPE,
which .NET refuses to process by default.
#>
function Get-MacBundleExecutable {
    param([Parameter(Mandatory)][string] $AppPath)

    $plist = Join-Path $AppPath "Contents/Info.plist"
    if (-not (Test-Path -LiteralPath $plist -PathType Leaf)) {
        throw "$AppPath has no Contents/Info.plist; it is not an app bundle."
    }
    $text = Get-Content -LiteralPath $plist -Raw
    $match = [regex]::Match($text, '<key>CFBundleExecutable</key>\s*<string>([^<]+)</string>')
    if (-not $match.Success) { throw "$plist names no CFBundleExecutable." }

    return Join-Path $AppPath ("Contents/MacOS/" + $match.Groups[1].Value)
}
