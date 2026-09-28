#!/usr/bin/env pwsh
# Fails when a plugin project's sources are newer than the DLL committed for Unity.
#
# WHY IT BLOCKS NOW, HAVING WARNED SINCE #21. Six .NET projects reach Unity as PREBUILT DLLs
# committed under Ironfront_Reborn/Assets/Plugins. Unity never compiles their sources -- it loads
# those binaries. So a source fix that lands without `tools/build-libs.ps1` having run is shipped
# in name only: the suite passes, the reviewer reads the correct code, and the game keeps the old
# behaviour.
#
# This logic already existed, inline in the `style (advisory)` job of ci.yml, and on 2026-09-14 it
# did its job exactly as written:
#
#     ##[warning]Ironfront.Net.Replication source changed after this DLL was last committed.
#     Run tools/build-libs.ps1 and commit the result -- Unity is loading a stale build.
#
# PR #281 merged anyway. The warning sat inside a continue-on-error job whose check reported
# `pass`, so the PR was green in every place anyone looks. The source fix it carried -- the
# vehicle-health divisor that made every vehicle on every client play damage smoke from the first
# snapshot -- did not reach the game. Executing both builds afterwards:
#
#     committed DLL : full health (wire byte 100) -> 0.3922   still under the 0.5 particle rung
#     rebuilt  DLL  : full health (wire byte 100) -> 1.0000
#
# The original step argued for advisory on the grounds that "a stale DLL never breaks the .NET
# build or the tests, it breaks Unity at the client track's desk." That was true when Unity was
# somebody else's desk. It is now the shipping path, and the cost of one scrolled-past warning was
# a user-visible bug shipped as fixed while every gate said otherwise.
#
# WHAT IT COMPARES. Commit timestamps, both sides from git: the newest commit touching the project
# directory against the newest commit touching its DLL. Not bytes -- Roslyn embeds a fresh MVID in
# every compilation, so two builds of identical source are never byte-equal and a gate written
# that way would be red on every PR, which is the same as no gate.
#
# "THE PROJECT DIRECTORY" MEANS ITS WHOLE PROJECTREFERENCE CLOSURE. A C# `const` is copied into the
# CALLER's IL at compile time, so a dependency's constant change rewrites every dependent DLL even
# though no dependent source moved. #318 bumped ProtocolConstants.PROTOCOL_VERSION 10 -> 11 and
# rebuilt Protocol.dll and Replication.dll only; Transport.dll and MasterClient.dll kept an inlined
# 10. This gate passed, because their own directories had not changed -- and the shipped game
# server answered a v11 client's UDP handshake with ProtocolMismatch while every Unity client
# would have been refused a login by the v11 master. Measured 2026-09-27 with the E2E tool.
#
# Usage:  pwsh tools/check-plugin-dll-freshness.ps1

$ErrorActionPreference = "Stop"

$repoRoot = (& git rev-parse --show-toplevel).Trim()
Push-Location $repoRoot

try {
    # The project directories whose commits can change what $Project's DLL contains: its own, plus
    # every project it references, transitively. Read from the .csproj files rather than listed
    # here, so a new reference extends the check with no edit. Repo convention: a project lives in
    # a root directory named after it, which is also how ProjectReference paths are written.
    function Get-SourceClosure([string] $Project) {
        $seen  = [System.Collections.Generic.List[string]]::new()
        $queue = [System.Collections.Generic.Queue[string]]::new()
        $queue.Enqueue($Project)

        while ($queue.Count -gt 0) {
            $current = $queue.Dequeue()
            if ($seen.Contains($current)) { continue }
            $seen.Add($current)

            $csproj = Join-Path $current "$current.csproj"
            if (-not (Test-Path $csproj)) { continue }

            $references = [regex]::Matches(
                (Get-Content -Raw $csproj), '<ProjectReference\s+Include="([^"]+)"')
            foreach ($reference in $references) {
                $queue.Enqueue([System.IO.Path]::GetFileNameWithoutExtension($reference.Groups[1].Value))
            }
        }

        return $seen.ToArray()
    }

    # Discovered from the DLLs actually present rather than a hardcoded project list, so adding a
    # library to build-libs.ps1 extends this check with no edit here. Ironfront.*, not
    # Ironfront.Net.*: Ironfront.MasterClient.dll is equally load-bearing and was invisible to the
    # narrower glob. The source-directory test below filters third-party DLLs out on their own.
    $dlls = Get-ChildItem "Ironfront_Reborn/Assets/Plugins" -Filter "Ironfront.*.dll" -ErrorAction SilentlyContinue

    if (-not $dlls) {
        Write-Host "FAIL: no Ironfront.*.dll under Ironfront_Reborn/Assets/Plugins." -ForegroundColor Red
        Write-Host "      The closure Unity loads is missing entirely. Run tools/build-libs.ps1."
        exit 1
    }

    $stale    = @()
    $checked  = 0
    $skipped  = @()

    foreach ($dll in $dlls) {
        $lib = [System.IO.Path]::GetFileNameWithoutExtension($dll.Name)
        if (-not (Test-Path -PathType Container $lib)) { continue }

        $dllPath = "Ironfront_Reborn/Assets/Plugins/$($dll.Name)"

        # The whole project directory, not just *.cs: a .csproj or Directory.Packages.props bump
        # changes the emitted assembly just as a source edit does. bin/ and obj/ are gitignored,
        # so they contribute no noise. And every referenced project's directory too -- see the
        # header on inlined constants.
        # @(...) and a plain `$sources`, never `@sources`: a one-project closure comes back as a
        # bare string, and splatting a string hands git no path at all -- it then answers with the
        # newest commit in the repository and reports every leaf project stale.
        $sources = @(Get-SourceClosure $lib)
        $srcAt   = (& git log -1 --format=%ct -- $sources)
        $dllAt   = (& git log -1 --format=%ct -- $dllPath)
        $newest  = $sources |
            Sort-Object { [long](& git log -1 --format=%ct -- $_) } -Descending |
            Select-Object -First 1

        if (-not $srcAt -or -not $dllAt) {
            $skipped += "$lib (no history on one side)"
            continue
        }

        $checked++
        if ([long]$srcAt -gt [long]$dllAt) {
            $stale += [pscustomobject]@{
                Library = $lib
                Source  = $newest
                Behind  = [TimeSpan]::FromSeconds([long]$srcAt - [long]$dllAt)
            }
        }
    }

    Write-Host "=== Plugin DLL freshness ==="

    foreach ($s in $skipped) { Write-Host "      skipped: $s" -ForegroundColor Yellow }

    if ($stale.Count -eq 0) {
        Write-Host "PASS: $checked plugin DLL(s) are at least as new as their sources." -ForegroundColor Green
        exit 0
    }

    foreach ($s in $stale) {
        Write-Host ""
        Write-Host ("FAIL: {2} source changed after {0}.dll was last committed ({1:0} day(s) behind)." -f $s.Library, $s.Behind.TotalDays, $s.Source) -ForegroundColor Red
        if ($s.Source -ne $s.Library) {
            Write-Host ("      {0} references {1}; a constant there is compiled INTO {0}.dll." -f $s.Library, $s.Source)
        }
        Write-Host "      Unity loads the committed DLL and never compiles these sources, so this"
        Write-Host "      change does not reach the game."
    }

    Write-Host ""
    Write-Host "Fix: pwsh tools/build-libs.ps1, then commit Ironfront_Reborn/Assets/Plugins/." -ForegroundColor Yellow
    Write-Host "Do NOT silence this by reverting the source change -- the source is the part that is right."
    exit 1
}
finally {
    Pop-Location
}
