# tools/build-libs.ps1 — builds the pure-.NET libraries and drops the DLLs where Unity
# can load them.md section 7).
#
# This is the script A, B and C are blocked on: without it, nothing the transport track or the replication track writes
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

    # Assemblies Unity's own profile ALREADY provides. Shipping one of these is not a harmless
    # duplicate -- Unity fails the whole compile with a duplicate-assembly error, which blocks
    # every script in the project rather than just the feature that needed them.
    #
    # WHICH ONES QUALIFY IS MEASURED, NOT ASSUMED, AND THE PLAYER IS THE PROFILE THAT DECIDES.
    # This list used to hold Microsoft.Bcl.AsyncInterfaces.dll as well, on the reasoning that
    # netstandard2.1 supplies IAsyncDisposable / IAsyncEnumerable so Unity would not need the
    # backfill. That is true of the EDITOR and false of a player. Searched in 6000.3.21f1:
    #
    #   System.Threading.Tasks.Extensions.dll  MonoBleedingEdge/lib/mono/unityjit-linux/Facades
    #                                          -- a UnityLinker --include-directory. Provided.
    #   Microsoft.Bcl.AsyncInterfaces.dll      Data/NetStandard/EditorExtensions ONLY. Absent
    #                                          from every Variations/* player profile and from
    #                                          the mono Facades. NOT provided to a player.
    #
    # Leaving it out did not produce the "cheap" TypeLoadException the note below predicts. It
    # killed the build outright: System.Text.Json.dll references it, so UnityLinker could not
    # close the reference graph and failed the Linux dedicated-server build with
    # `ILLink: error IL1010 ... Failed to resolve assembly: Microsoft.Bcl.AsyncInterfaces`,
    # which the Editor log then reported as the unrelated-sounding "UnityEditor.dll assembly
    # is referenced by user code, but this is not allowed." That is the whole reason no server
    # artifact had ever been produced (#80).
    #
    # A missing assembly is still the cheaper failure for anything the linker CAN resolve --
    # it surfaces as a TypeLoadException on the one code path that touches it, and the remedy
    # is -IncludeBclFacades. A duplicate stops the Editor dead. So the default still excludes
    # what the player genuinely provides, and now ships what it does not.
    #
    # System.Text.Json 10.0.11 added System.IO.Pipelines to this closure (8.0.5 had no such
    # dependency; 9.0 introduced it). Searched with the same method in 6000.3.21f1, it lands
    # in the SAME category as Microsoft.Bcl.AsyncInterfaces -- present only under
    # Tools/BuildPipeline/Compilation/{ApiUpdater,Unity.ILPP.Runner}, which are Editor build
    # tooling, and absent from every Variations/* player profile and from the mono Facades.
    # NOT provided to a player, so it ships, and is deliberately not listed below.
    #
    # The same bump DROPPED System.Threading.Tasks.Extensions from the publish output: at
    # netstandard2.1 that package (like System.Memory and System.Buffers) resolves to an
    # empty lib/netstandard2.1/_._ placeholder because the framework has the types inbox, so
    # publish emits no DLL for it. The entry below is therefore inert today. It stays because
    # it is a statement about what the PLAYER provides, which has not changed, and it becomes
    # load-bearing again the moment anything here targets netstandard2.0.
    $unityProvided = @("System.Threading.Tasks.Extensions.dll")

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
