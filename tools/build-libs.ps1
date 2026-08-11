# tools/build-libs.ps1 — builds the pure-.NET libraries and drops the DLLs where Unity
# can load them. OWNER: Dev D (plans/00-shared/conventions.md section 7).
#
# This is the script A, B and C are blocked on: without it, nothing Dev B or Dev C writes
# reaches the Unity project at all. It has a week-2 deadline for that reason.
#
# Usage:  pwsh tools/build-libs.ps1 [-Configuration Release]

[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    $libs = @("Ironfront.Net.Protocol", "Ironfront.Net.Transport", "Ironfront.Net.Replication")
    $plugin = Join-Path $repoRoot "Ironfront_Reborn/Assets/Plugins"

    if (-not (Test-Path $plugin)) {
        New-Item -ItemType Directory -Path $plugin -Force | Out-Null
        Write-Host "Created $plugin"
    }

    foreach ($lib in $libs) {
        $project = Join-Path $repoRoot "$lib/$lib.csproj"
        if (-not (Test-Path $project)) {
            throw "Project not found: $project"
        }

        Write-Host "Building $lib ($Configuration)..."
        dotnet build $project -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $lib" }

        $dll = Join-Path $repoRoot "$lib/bin/$Configuration/netstandard2.1/$lib.dll"
        if (-not (Test-Path $dll)) { throw "Expected output missing: $dll" }

        Copy-Item $dll $plugin -Force
        Write-Host "  -> $lib.dll"
    }

    # MANDATORY: the System.Memory dependency chain. Unity will not fetch these itself.
    #
    # TRAP: netstandard2.1 plus Span<byte> needs System.Memory.dll at runtime. Copy only
    # the main DLLs and Unity throws TypeLoadException — an error that says nothing about
    # a missing assembly and costs hours to track down. This is a very common mistake.
    $deps = @("System.Memory.dll", "System.Buffers.dll",
              "System.Runtime.CompilerServices.Unsafe.dll", "System.Numerics.Vectors.dll")

    $nugetRoot = Join-Path $HOME ".nuget/packages"
    $missing = @()

    foreach ($d in $deps) {
        if (-not (Test-Path $nugetRoot)) { $missing += $d; continue }

        $src = Get-ChildItem -Recurse -Filter $d $nugetRoot -ErrorAction SilentlyContinue |
               Where-Object { $_.FullName -match "netstandard2\.[01]" } |
               Select-Object -First 1

        if ($src) {
            Copy-Item $src.FullName $plugin -Force
            Write-Host "  -> $d"
        }
        else {
            $missing += $d
        }
    }

    foreach ($d in $missing) {
        Write-Warning "Could not find $d in the NuGet cache — Unity may fail to load the DLLs with TypeLoadException."
    }

    Write-Host ""
    Write-Host "Copied $($libs.Count) DLL(s) + $($deps.Count - $missing.Count) dependency/dependencies into $plugin"
    if ($missing.Count -gt 0) {
        Write-Host "$($missing.Count) dependency/dependencies were not found; see the warnings above."
    }
}
finally {
    Pop-Location
}
