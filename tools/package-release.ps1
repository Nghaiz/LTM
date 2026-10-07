# tools/package-release.ps1 -- turn a built player into the zip players download, and
# optionally publish it as a GitHub release.
#
# WHAT A PLAYER GETS. One zip. Unzip, double-click Ironfront.exe (or Ironfront.app), sign in. No
# .env, no launcher, no environment variables: the master endpoint is serialized on
# ClientFlowBootstrap in Menu.unity (ReleasedClientMasterEndpointTests pins it), the master hands
# out the game server address with the room join, and the shipped flow declares the client role
# itself on the way into a match. So everything this script adds is the README and the checks below.
#
# THE CHECKS, and why each one refuses rather than warns:
#   - the build must be the release player: not a Development build, Net/Diagnostics compiled out,
#     and on Windows IL2CPP. Until v3.1.1 every zip was the lane-B harness's Development build on
#     Mono, with "Development Build" printed on every screen and the harness's scripted aim and
#     input inside, reachable through environment variables. tools/build-player.ps1 builds the
#     release player by default since 2026-10-02.
#   - the build's stamp must name the commit being released. A zip whose DLLs say another SHA is
#     the "MIXED BUILD FOLDER" failure one level up, and nobody downloading it can tell.
#   - no .env, key, certificate or log file may be in the folder. The repo's .env carries
#     IRONFRONT_SHARED_SECRET, which signs match tickets; a copy in a public zip lets anyone mint
#     one. A client never needs it (EnvRegistry lists it for master and game server only).
#   - the *_DoNotShip Burst debug folder and IL2CPP's *_BackUpThisFolder_ButDontShipItWithYourGame
#     (generated C++ and symbols) are dropped. Unity names them that for a reason, and they are most
#     of the unneeded size.
#
# -Platform macos (since 2026-10-07) packages build/macos/Ironfront.app. That player is Mono by
# necessity (IL2CPP for macOS can only be built on a Mac), so instead of the IL2CPP check it must
# be a universal binary (Intel + Apple silicon) and carry the ad-hoc code signature Unity writes,
# without which an Apple silicon Mac kills it at launch. The zip is written by
# tools/release/zip_macos_bundle.py, which keeps the executable bit a Windows zip would lose.
#
# Usage:
#   pwsh tools/build-player.ps1                                   # first, from a clean checkout
#   pwsh tools/package-release.ps1 -Version v1.1.0                # zip into artifacts/release/
#   pwsh tools/package-release.ps1 -Version v1.1.0 -Publish       # ...and create the GitHub release
#   pwsh tools/build-player.ps1 -Platform macos
#   pwsh tools/package-release.ps1 -Version v1.1.0 -Platform macos
#
# Full procedure: docs/releasing.md.

[CmdletBinding()]
param(
    # The release tag, e.g. v1.1.0. Also names the zip and the folder inside it.
    [Parameter(Mandatory)]
    [ValidatePattern('^v\d+\.\d+\.\d+([-.][0-9A-Za-z.]+)?$')]
    [string] $Version,

    # windows (build/windows/Ironfront.exe) or macos (build/macos/Ironfront.app).
    [ValidateSet("windows", "macos")]
    [string] $Platform = "windows",

    # Default build/<platform>.
    [string] $BuildDirectory = "",

    [string] $OutputDirectory = "artifacts/release",

    # The commit the build must be stamped with. Defaults to HEAD.
    [string] $Commit = "",

    # Create the GitHub release and upload the zip. The tag is created on -Target. If the release
    # already exists (the other platform's zip went first), the zip is added to it instead.
    [switch] $Publish,

    [string] $Target = "main",

    # Markdown file used as the release notes. Default: a short generated note.
    [string] $NotesFile = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
$repoRoot = Split-Path -Parent $PSScriptRoot
$isMac = $Platform -eq "macos"
if (-not $BuildDirectory) { $BuildDirectory = "build/$Platform" }

function Resolve-RepoPath([string] $path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return $path }
    return Join-Path $repoRoot $path
}

# Latin-1 maps every byte to one char, so ASCII names and literals are found where they are.
function Read-Latin1([string] $path) {
    return [System.Text.Encoding]::Latin1.GetString([System.IO.File]::ReadAllBytes($path))
}

# A development player listens for the profiler; the release player has no player connection.
function Assert-NotDevelopment([string] $bootConfig) {
    if ((Test-Path $bootConfig) -and (Select-String -LiteralPath $bootConfig -Pattern '^player-connection-' -Quiet)) {
        throw "$BuildDirectory is a Development build (boot.config has player-connection settings). Rebuild without -Development."
    }
}

# The CPU types a Mach-O executable holds. A universal ("fat") file starts with a big-endian
# header listing one entry per architecture; a thin one is a single little-endian header.
function Get-MachOCpuTypes([string] $path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    function BigEndian([int] $at) { return ([uint32]$bytes[$at] -shl 24) -bor ([uint32]$bytes[$at + 1] -shl 16) -bor ([uint32]$bytes[$at + 2] -shl 8) -bor [uint32]$bytes[$at + 3] }
    $magic = BigEndian 0
    if ($magic -eq 0xCAFEBABEu -or $magic -eq 0xCAFEBABFu) {
        $entrySize = if ($magic -eq 0xCAFEBABEu) { 20 } else { 32 }
        $count = BigEndian 4
        return @(for ($i = 0; $i -lt $count; $i++) { BigEndian (8 + $i * $entrySize) })
    }
    if ($magic -eq 0xCFFAEDFEu) { return @([BitConverter]::ToUInt32($bytes, 4)) }
    throw "$path is not a Mach-O executable."
}

$buildDir = Resolve-RepoPath $BuildDirectory
$outDir   = Resolve-RepoPath $OutputDirectory

if (-not $Commit) { $Commit = (& git -C $repoRoot rev-parse --short HEAD).Trim() }
if (-not $Commit) { throw "Could not read the commit to release." }

if ($isMac) {
    # --- macOS release player -------------------------------------------------------------------
    $app     = Join-Path $buildDir "Ironfront.app"
    $macExe  = Join-Path $app "Contents/MacOS/Ironfront"
    $dataDir = Join-Path $app "Contents/Resources/Data"
    $managed = Join-Path $dataDir "Managed"

    if (-not (Test-Path $macExe)) { throw "No macOS player at $app. Run tools/build-player.ps1 -Platform macos first." }
    if (-not (Test-Path (Join-Path $managed "Assembly-CSharp.dll"))) {
        throw "$app has no Contents/Resources/Data/Managed/Assembly-CSharp.dll; it is not the Mono player build-player.ps1 makes."
    }
    Assert-NotDevelopment (Join-Path $dataDir "boot.config")

    # The type names LaneBHarness and friends are UTF-8 in the assemblies' metadata, so the
    # Latin-1 view finds them; the assembly that holds them must not be there at all.
    if (Test-Path (Join-Path $managed "Ironfront.Net.Unity.Diagnostics.dll")) {
        throw "$app ships Ironfront.Net.Unity.Diagnostics.dll. That is a -KeepDiagnostics measuring build; rebuild with plain tools/build-player.ps1 -Platform macos."
    }
    $withHarness = @(Get-ChildItem -LiteralPath $managed -Filter *.dll | Where-Object { (Read-Latin1 $_.FullName).Contains("LaneBHarness") })
    if ($withHarness) { throw "$($withHarness[0].Name) still contains Net/Diagnostics (LaneBHarness). Rebuild without -KeepDiagnostics." }
    Write-Host "[release] Mono release player, diagnostics compiled out"

    # x86_64 = 0x01000007, arm64 = 0x0100000C (mach/machine.h).
    $cpuTypes = Get-MachOCpuTypes $macExe
    if (-not ($cpuTypes -contains 0x01000007) -or -not ($cpuTypes -contains 0x0100000C)) {
        $found = ($cpuTypes | ForEach-Object { '0x{0:X8}' -f $_ }) -join ", "
        throw "$macExe is not a universal binary (cpu types: $found). It must hold both x86_64 and arm64."
    }
    Write-Host "[release] universal binary: x86_64 + arm64"

    # Unity signs every macOS build ad hoc, on Windows too (MacOSCodeSigning.dll in the module).
    # Without it an Apple silicon Mac kills the player at launch with no message. Whether the
    # signature VERIFIES can only be judged on a Mac: .github/workflows/macos-smoke.yml.
    if (-not (Test-Path (Join-Path $app "Contents/_CodeSignature/CodeResources"))) {
        throw "$app has no Contents/_CodeSignature/CodeResources: it is not code-signed, and Apple silicon Macs refuse to start unsigned code."
    }
    Write-Host "[release] ad-hoc code signature present"

    # A Mono assembly keeps string literals as UTF-16, which is where the stamp's Commit lives.
    $sharedDll = Join-Path $managed "Ironfront.Net.Unity.Shared.dll"
    if (-not (Test-Path $sharedDll)) { throw "$app has no Managed/Ironfront.Net.Unity.Shared.dll, which carries the build stamp." }
    $stampText = [System.Text.Encoding]::Unicode.GetString([System.IO.File]::ReadAllBytes($sharedDll))
    if (-not $stampText.Contains($Commit)) {
        throw ("The build in $BuildDirectory is not stamped with $Commit. Rebuild it with " +
               "tools/build-player.ps1 -Platform macos from that commit, or pass -Commit with the one it was built from.")
    }
    Write-Host "[release] build stamped with $Commit"
    $shipFrom = $app
}
else {
    # --- Windows release player -----------------------------------------------------------------
    $exe      = Join-Path $buildDir "Ironfront.exe"
    $metadata = Join-Path $buildDir "Ironfront_Data/il2cpp_data/Metadata/global-metadata.dat"

    if (-not (Test-Path $exe)) { throw "No player at $exe. Run tools/build-player.ps1 first." }

    # IL2CPP compiles every assembly into GameAssembly.dll and keeps the string literals and type
    # names in global-metadata.dat; a Mono player has Ironfront_Data/Managed/ instead.
    if (-not (Test-Path (Join-Path $buildDir "GameAssembly.dll")) -or -not (Test-Path $metadata)) {
        throw ("$BuildDirectory is not an IL2CPP player (no GameAssembly.dll / global-metadata.dat). " +
               "Build the release player with tools/build-player.ps1, without -Development.")
    }
    if (Test-Path (Join-Path $buildDir "Ironfront_Data/Managed")) {
        throw "$BuildDirectory also holds a Mono player's Managed/ folder; rebuild it into an empty folder."
    }

    Assert-NotDevelopment (Join-Path $buildDir "Ironfront_Data/boot.config")

    $metadataText = Read-Latin1 $metadata
    if ($metadataText.Contains("LaneBHarness")) {
        throw ("$BuildDirectory still contains Net/Diagnostics (LaneBHarness). That is a -KeepDiagnostics " +
               "measuring build; rebuild with plain tools/build-player.ps1.")
    }
    Write-Host "[release] IL2CPP release player, diagnostics compiled out"

    # IL2CPP stores string literals in global-metadata.dat as UTF-8, so the ASCII commit is found as
    # bytes. (A Mono assembly keeps them as UTF-16, which is why the macOS check decodes Unicode.)
    if (-not $metadataText.Contains($Commit)) {
        throw ("The build in $BuildDirectory is not stamped with $Commit. Rebuild it with " +
               "tools/build-player.ps1 from that commit, or pass -Commit with the one it was built from.")
    }
    Write-Host "[release] build stamped with $Commit"
    $shipFrom = $buildDir
}

# --- forbidden files -------------------------------------------------------------------------
$forbidden = Get-ChildItem -Path $shipFrom -Recurse -File -Force | Where-Object {
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
$zipPath  = Join-Path $outDir $(if ($isMac) { "$name-macos.zip" } else { "$name-windows-x64.zip" })

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
if (Test-Path $staging) { Remove-Item -Recurse -Force $staging }
if (Test-Path $zipPath) { Remove-Item -Force $zipPath }

Write-Host "[release] staging $name"
# The macOS build folder also holds Ironfront_BurstDebugInformation_DoNotShip beside the .app;
# copying the .app alone leaves it out.
$copyTo = if ($isMac) { Join-Path $staging "Ironfront.app" } else { $staging }
robocopy $shipFrom $copyTo /E /NFL /NDL /NJH /NJS /NP /XD "*_DoNotShip" "*_ButDontShipItWithYourGame" | Out-Null
# robocopy: 0-7 are success codes (1 = files copied), 8+ is a failure.
if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE." }
$global:LASTEXITCODE = 0

$readmeTemplate = if ($isMac) { "release/README-macos.txt" } else { "release/README.txt" }
$readme = Get-Content -Raw (Join-Path $PSScriptRoot $readmeTemplate)
$readme = $readme.Replace("{{VERSION}}", $Version).Replace("{{COMMIT}}", $Commit)
# Windows: CRLF so Notepad on an old Windows shows lines, not one run-on paragraph. macOS: LF.
# UTF-8 WITH a BOM on both, so neither Notepad nor TextEdit guesses a legacy code page and
# mangles the Vietnamese.
$readme = if ($isMac) { $readme -replace "`r?`n", "`n" } else { $readme -replace "`r?`n", "`r`n" }
[System.IO.File]::WriteAllText((Join-Path $staging "README.txt"), $readme,
                               [System.Text.UTF8Encoding]::new($true))

# --- zip -------------------------------------------------------------------------------------
if ($isMac) {
    & python (Join-Path $PSScriptRoot "release/zip_macos_bundle.py") $staging $zipPath
    if ($LASTEXITCODE -ne 0) { throw "zip_macos_bundle.py failed ($LASTEXITCODE)." }
}
else {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $staging, $zipPath, [System.IO.Compression.CompressionLevel]::Optimal, $true)
}
Remove-Item -Recurse -Force $staging

$zip  = Get-Item $zipPath
$hash = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLowerInvariant()
$sizeMb = [math]::Round($zip.Length / 1MB, 1)
Write-Host "[release] $($zip.FullName)"
Write-Host "[release] $sizeMb MB, sha256 $hash"

if (-not $Publish) { return }

# --- publish ---------------------------------------------------------------------------------
if (-not $NotesFile) {
    $NotesFile = Join-Path $outDir "$name-$Platform-notes.md"
    if ($isMac) {
        @"
Ironfront: Reborn $Version for macOS 12+ (Intel and Apple silicon), built from ``$Commit``.

**Cài đặt:** tải ``$($zip.Name)``, mở file zip, kéo ``Ironfront.app`` vào Applications rồi mở. Game chưa được Apple công chứng nên lần đầu macOS sẽ chặn: vào System Settings > Privacy & Security, bấm "Open Anyway". Hướng dẫn đầy đủ nằm trong ``README.txt`` bên trong file zip.

**Install:** download ``$($zip.Name)``, open it, drag ``Ironfront.app`` into Applications and open it. The game is not notarized, so the first launch is blocked: System Settings > Privacy & Security > "Open Anyway". See ``README.txt`` inside the zip.

SHA-256: ``$hash``
"@ | Set-Content -Encoding utf8NoBOM $NotesFile
    }
    else {
        @"
Ironfront: Reborn $Version for Windows 64-bit, built from ``$Commit``.

**Cài đặt:** tải ``$($zip.Name)``, giải nén ra một thư mục mới, mở ``Ironfront.exe``. Không cần cấu hình gì: địa chỉ server đã có sẵn trong game. Lần đầu chơi hãy tạo tài khoản ở màn hình Create Account; trận bắt đầu khi có ít nhất 2 người Ready trong một phòng. Hướng dẫn đầy đủ nằm trong ``README.txt`` bên trong file zip.

**Install:** download ``$($zip.Name)``, extract it into a new folder, run ``Ironfront.exe``, create an account and join a room. See ``README.txt`` inside the zip.

SHA-256: ``$hash``
"@ | Set-Content -Encoding utf8NoBOM $NotesFile
    }
}

& gh release view $Version --json tagName *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Host "[release] $Version exists; adding $($zip.Name) to it (the notes are left to edit by hand)"
    & gh release upload $Version $zipPath
    if ($LASTEXITCODE -ne 0) { throw "gh release upload failed ($LASTEXITCODE)." }
}
else {
    & gh release create $Version $zipPath --target $Target --title "Ironfront: Reborn $Version" --notes-file $NotesFile
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed ($LASTEXITCODE)." }
}
& gh release view $Version --json url -q .url
