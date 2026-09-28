# tools/package-release.ps1 -- turn a built player into the zip players download, and
# optionally publish it as a GitHub release.
#
# WHAT A PLAYER GETS. One zip. Unzip, double-click Ironfront.exe, sign in. No .env, no launcher,
# no environment variables: the master endpoint is serialized on ClientFlowBootstrap in
# Menu.unity (ReleasedClientMasterEndpointTests pins it), the master hands out the game server
# address with the room join, and the shipped flow declares the client role itself on the way
# into a match. So everything this script adds is the README and the checks below.
#
# THE CHECKS, and why each one refuses rather than warns:
#   - the build's stamp must name the commit being released. A zip whose DLLs say another SHA is
#     the "MIXED BUILD FOLDER" failure one level up, and nobody downloading it can tell.
#   - no .env, key, certificate or log file may be in the folder. The repo's .env carries
#     IRONFRONT_SHARED_SECRET, which signs match tickets; a copy in a public zip lets anyone mint
#     one. A client never needs it (EnvRegistry lists it for master and game server only).
#   - the *_DoNotShip Burst debug folder is dropped. Unity names it that for a reason, and it is
#     most of the unneeded size.
#
# Usage:
#   pwsh tools/build-player.ps1                                   # first, from a clean checkout
#   pwsh tools/package-release.ps1 -Version v1.1.0                # zip into artifacts/release/
#   pwsh tools/package-release.ps1 -Version v1.1.0 -Publish       # ...and create the GitHub release
#
# Full procedure: docs/releasing.md.

[CmdletBinding()]
param(
    # The release tag, e.g. v1.1.0. Also names the zip and the folder inside it.
    [Parameter(Mandatory)]
    [ValidatePattern('^v\d+\.\d+\.\d+([-.][0-9A-Za-z.]+)?$')]
    [string] $Version,

    [string] $BuildDirectory = "build/windows",

    [string] $OutputDirectory = "artifacts/release",

    # The commit the build must be stamped with. Defaults to HEAD.
    [string] $Commit = "",

    # Create the GitHub release and upload the zip. The tag is created on -Target.
    [switch] $Publish,

    [string] $Target = "main",

    # Markdown file used as the release notes. Default: a short generated note.
    [string] $NotesFile = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot

function Resolve-RepoPath([string] $path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return $path }
    return Join-Path $repoRoot $path
}

$buildDir = Resolve-RepoPath $BuildDirectory
$outDir   = Resolve-RepoPath $OutputDirectory
$exe      = Join-Path $buildDir "Ironfront.exe"
$sharedDll = Join-Path $buildDir "Ironfront_Data/Managed/Ironfront.Net.Unity.Shared.dll"

if (-not (Test-Path $exe))       { throw "No player at $exe. Run tools/build-player.ps1 first." }
if (-not (Test-Path $sharedDll)) { throw "No ${sharedDll}: this folder is not a complete player build." }

# --- stamp -----------------------------------------------------------------------------------
if (-not $Commit) { $Commit = (& git -C $repoRoot rev-parse --short HEAD).Trim() }
if (-not $Commit) { throw "Could not read the commit to release." }

# The stamp is a C# string literal, so it sits in the assembly as UTF-16. Reading it as ASCII
# finds nothing, which would look exactly like an unstamped build.
$dllText = [System.Text.Encoding]::Unicode.GetString([System.IO.File]::ReadAllBytes($sharedDll))
if (-not $dllText.Contains($Commit)) {
    throw ("The build in $BuildDirectory is not stamped with $Commit. Rebuild it with " +
           "tools/build-player.ps1 from that commit, or pass -Commit with the one it was built from.")
}
Write-Host "[release] build stamped with $Commit"

# --- forbidden files -------------------------------------------------------------------------
$forbidden = Get-ChildItem -Path $buildDir -Recurse -File -Force | Where-Object {
    $_.Name -like ".env*" -or $_.Extension -in @(".pem", ".key", ".pfx", ".p12", ".log") -or
    $_.Name -like "credentials*"
}
if ($forbidden) {
    $list = ($forbidden | ForEach-Object { "  " + $_.FullName }) -join "`n"
    throw "Refusing to package: these must never ship in a public zip:`n$list"
}

# --- stage -----------------------------------------------------------------------------------
$name     = "IronfrontReborn-$Version"
$staging  = Join-Path $outDir $name
$zipPath  = Join-Path $outDir "$name-windows-x64.zip"

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }

Write-Host "[release] staging $name"
robocopy $buildDir $staging /E /NFL /NDL /NJH /NJS /NP /XD "*_DoNotShip" | Out-Null
# robocopy: 0-7 are success codes (1 = files copied), 8+ is a failure.
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE." }
$global:LASTEXITCODE = 0

$readme = Get-Content -Raw (Join-Path $PSScriptRoot "release/README.txt")
$readme = $readme.Replace("{{VERSION}}", $Version).Replace("{{COMMIT}}", $Commit)
# CRLF so Notepad on an old Windows shows lines, not one run-on paragraph; UTF-8 WITH a BOM so
# it does not guess a legacy code page and mangle the Vietnamese.
$readme = ($readme -replace "`r?`n", "`r`n")
[System.IO.File]::WriteAllText((Join-Path $staging "README.txt"), $readme,
                               [System.Text.UTF8Encoding]::new($true))

# --- zip -------------------------------------------------------------------------------------
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory(
    $staging, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
Remove-Item -Recurse -Force $staging

$zip  = Get-Item $zipPath
$hash = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLowerInvariant()
$sizeMb = [math]::Round($zip.Length / 1MB, 1)
Write-Host "[release] $($zip.FullName)"
Write-Host "[release] $sizeMb MB, sha256 $hash"

if (-not $Publish) { return }

# --- publish ---------------------------------------------------------------------------------
if (-not $NotesFile) {
    $NotesFile = Join-Path $outDir "$name-notes.md"
    @"
Ironfront: Reborn $Version for Windows 64-bit, built from ``$Commit``.

**Cài đặt:** tải ``$($zip.Name)``, giải nén ra một thư mục mới, mở ``Ironfront.exe``. Không cần cấu hình gì: địa chỉ server đã có sẵn trong game. Lần đầu chơi hãy tạo tài khoản ở màn hình Create Account; trận bắt đầu khi có ít nhất 2 người Ready trong một phòng. Hướng dẫn đầy đủ nằm trong ``README.txt`` bên trong file zip.

**Install:** download ``$($zip.Name)``, extract it into a new folder, run ``Ironfront.exe``, create an account and join a room. See ``README.txt`` inside the zip.

SHA-256: ``$hash``
"@ | Set-Content -Encoding utf8NoBOM $NotesFile
}

& gh release create $Version $zipPath --target $Target --title "Ironfront: Reborn $Version" --notes-file $NotesFile
if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)." }
& gh release view $Version --json url -q .url
