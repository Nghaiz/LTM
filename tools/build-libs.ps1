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
    [string]$Configuration = "Release",

    # Ship System.Threading.Tasks.Extensions and Microsoft.Bcl.AsyncInterfaces even though
    # Unity's netstandard2.1 profile provides the types they backfill. Off by default: see the
    # $unityProvided block below for why a duplicate is the more expensive failure.
    [switch]$IncludeBclFacades
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    # Ironfront.MasterClient and Ironfront.Net.MasterLink were added 2026-08-15 to close A11.
    # Until then ServerMasterReporter could only ever be a NullMatchReporter -- it documents that
    # wiring GameServerMatchReporter is "a two-line change and a plugin drop", and this is the
    # plugin drop. They come last because MasterLink references Replication and MasterClient.
    #
    # Ironfront.Net.Configuration comes first and has no dependencies of its own: it is the
    # IRONFRONT_* registry, the .env reader and the shared parsers, and the Unity bootstraps
    # read their port, slot count and master address through it. Before it existed those
    # settings lived only in scene assets, so a headless build could not be reconfigured
    # without opening the Editor.
    $libs = @("Ironfront.Net.Configuration",
              "Ironfront.Net.Protocol", "Ironfront.Net.Transport", "Ironfront.Net.Replication",
              "Ironfront.MasterClient", "Ironfront.Net.MasterLink")
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

    # MANDATORY: the managed dependency closure. Unity will not fetch any of it itself.
    #
    # TRAP: netstandard2.1 plus Span<byte> needs System.Memory.dll at runtime. Copy only the
    # main DLLs and Unity throws TypeLoadException -- an error that says nothing about a
    # missing assembly and costs hours to track down. This is a very common mistake.
    #
    # This used to be a hardcoded list of four names scavenged out of the NuGet cache by a
    # recursive Get-ChildItem. That worked while the only dependency was System.Memory, and
    # broke the moment Ironfront.MasterClient arrived carrying System.Text.Json: the closure
    # is now eight assemblies, and a hardcoded list is a list that silently goes stale every
    # time somebody adds a PackageReference.
    #
    # `dotnet publish` computes the closure the same way the runtime resolves it, so the set
    # below is measured rather than remembered. Adding a package to any library extends this
    # automatically.
    $publishRoot = Join-Path ([System.IO.Path]::GetTempPath()) "ironfront-libs-publish"
    if (Test-Path $publishRoot) { Remove-Item $publishRoot -Recurse -Force }

    foreach ($lib in $libs) {
        # -f is not optional. Ironfront.Net.Transport multi-targets netstandard2.1 and net8.0,
        # and `dotnet publish` on a cross-targeting project fails outright (NETSDK1129) rather
        # than picking one. Pinning it also stops a net8.0 asset from reaching Unity, which
        # cannot load one.
        dotnet publish (Join-Path $repoRoot "$lib/$lib.csproj") -c $Configuration -f netstandard2.1 -o $publishRoot --nologo | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $lib" }
    }

    # Assemblies Unity's own .NET Standard 2.1 profile ALREADY provides. Shipping these is not
    # a harmless duplicate -- Unity fails the whole compile with a duplicate-assembly error,
    # which blocks every script in the project rather than just the feature that needed them.
    #
    # A missing assembly is the cheaper failure of the two: it surfaces as a TypeLoadException
    # on the one code path that touches it, and the remedy is -IncludeBclFacades. A duplicate
    # stops Dev A's Editor dead. So the default excludes them.
    #
    # ValueTask lives in netstandard2.1's System.Runtime facade, and IAsyncDisposable /
    # IAsyncEnumerable came in with netstandard2.1 as well -- which is exactly what these two
    # packages exist to backfill for netstandard2.0 consumers. Unity does not need either.
    $unityProvided = @("System.Threading.Tasks.Extensions.dll", "Microsoft.Bcl.AsyncInterfaces.dll")

    $libDlls = $libs | ForEach-Object { "$_.dll" }
    $copiedDeps = @()
    $skipped = @()

    foreach ($f in Get-ChildItem -Path $publishRoot -Filter *.dll) {
        if ($libDlls -contains $f.Name) { continue }   # already copied above, from bin/

        if ($unityProvided -contains $f.Name -and -not $IncludeBclFacades) {
            $skipped += $f.Name
            continue
        }

        Copy-Item $f.FullName $plugin -Force
        $copiedDeps += $f.Name
        Write-Host "  -> $($f.Name)"
    }

    Remove-Item $publishRoot -Recurse -Force -ErrorAction SilentlyContinue

    Write-Host ""
    Write-Host "Copied $($libs.Count) library DLL(s) + $($copiedDeps.Count) dependency/dependencies into $plugin"

    foreach ($s in $skipped) {
        Write-Host "  skipped $s — Unity's netstandard2.1 profile provides it. Re-run with -IncludeBclFacades if Unity reports the types as missing rather than as duplicated."
    }
}
finally {
    Pop-Location
}
