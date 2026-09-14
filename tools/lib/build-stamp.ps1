# tools/lib/build-stamp.ps1 — bakes the commit identity into a Unity build, and puts the source
# back afterwards.
#
# WHY THIS IS A SHARED FILE. Two scripts cut Unity builds: tools/build-player.ps1 (the Windows
# client) and tools/build-server.ps1 (the Linux dedicated server). Only the first one stamped, and
# the gap was invisible from either side -- a stamped client and an unstamped server both start
# without complaint, and the server simply reports:
#
#     [net] build dev (built from the Editor, not by tools/build-player.ps1) (server assembly)
#
# which is exactly what an Editor build reports. So a deployed game server could not be told apart
# from somebody's local experiment, and the § 11 handoff manifest's `sourceCommit` had nothing on
# the running process to check it against. Copying the logic into build-server.ps1 would have
# fixed that build and left the next one to drift; this is the one place it lives.
#
# WHY THE FIELDS ARE REWRITTEN IN SOURCE rather than written to a file beside the executable: a
# stamp beside the executable describes the FOLDER, and the folder is what gets mixed when somebody
# copies one DLL over another. Compiled into the assemblies, it describes the code.
#
# Usage:
#     . "$PSScriptRoot/lib/build-stamp.ps1"
#     $stamp = Write-BuildStamp -RepoRoot $repoRoot -Dirty $dirty
#     try   { ...run Unity... }
#     finally { Restore-BuildStamp -Originals $stamp.Originals }
#
# Restore-BuildStamp MUST run in a finally. These files are tracked, so a build that throws between
# the rewrite and the restore leaves a commit hash committed into the working tree.

Set-StrictMode -Version Latest

# The two files carrying the stamp. Both, not one: ServerBuildStamp exists so the server can
# compare its own assembly's stamp against the shared assembly's and shout MIXED BUILD FOLDER when
# they disagree, and that comparison is meaningless if only one of them is ever written.
function Get-BuildStampFiles {
    param([Parameter(Mandatory)][string]$RepoRoot)

    $files = @(
        (Join-Path $RepoRoot "Ironfront_Reborn/Assets/Scripts/Net/Shared/BuildStamp.cs"),
        (Join-Path $RepoRoot "Ironfront_Reborn/Assets/Scripts/Net/Server/ServerBuildStamp.cs")
    )

    foreach ($file in $files) {
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
            throw "the build stamp source $file is missing. It is tracked, not generated -- " +
                  "restore it rather than letting the build ship an unidentifiable binary."
        }
    }

    return $files
}

<#
.SYNOPSIS
Rewrites the stamp fields with the current commit, returning what to put back.

.DESCRIPTION
Returns an object with `Commit`, `BuiltAtUtc`, `Dirty`, `Line` and `Originals`. `Originals` is the
map Restore-BuildStamp consumes; it is empty when no commit could be read, which makes the restore
a no-op and is the correct behaviour for a source drop with no .git.
#>
function Write-BuildStamp {
    param(
        [Parameter(Mandatory)][string]$RepoRoot,

        # Whether Ironfront_Reborn/ had uncommitted changes when the caller looked. The caller
        # owns this question because it has to be asked BEFORE the caller rebuilds the tracked
        # plugin DLLs -- recompiled assemblies get new PE identities from unchanged source, so
        # asking afterwards turns every build into a false -dirty.
        [bool]$Dirty = $false
    )

    $stampFiles = Get-BuildStampFiles -RepoRoot $RepoRoot

    # Degrade rather than block when there is no git to ask. A source drop with no .git is a
    # legitimate way to build, and refusing it would trade a real capability for a diagnostic. It
    # is WARNED about rather than passed over silently: a binary that reports "dev" is one nobody
    # can identify later.
    $commit = $null
    try { $commit = (& git -C $RepoRoot rev-parse --short HEAD 2>$null) } catch { $commit = $null }

    if ($LASTEXITCODE -ne 0 -or -not $commit) {
        Write-Warning ("[build] no git commit could be read, so this build will report itself as " +
                       "'dev' and will be indistinguishable from an Editor build. See " +
                       "docs/handing-over-a-build.md.")
        return [pscustomobject]@{
            Commit = $null; BuiltAtUtc = $null; Dirty = $Dirty
            Line = "dev"; Originals = @{}
        }
    }

    $commit = $commit.Trim()
    $builtAtUtc = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")

    if ($Dirty) {
        Write-Warning ("[build] Ironfront_Reborn has uncommitted changes, so $commit names a " +
                       "commit this binary does NOT match. The stamp will say so with a -dirty suffix.")
    }

    $originals = @{}

    foreach ($stampFile in $stampFiles) {
        $originals[$stampFile] = Get-Content -LiteralPath $stampFile -Raw
        $stamped = $originals[$stampFile]

        # EVERY field is checked on its own, not the file as a whole. Comparing whole texts would
        # only notice all three substitutions failing together: rename ONE field and the other two
        # still change, the file differs, the check passes, and the build ships a stamp that is
        # half real -- a binary claiming a build time for a commit it does not name. A mechanism
        # whose entire purpose is not lying cannot afford a guard that only catches total failure.
        #
        # The patterns require the word `readonly`, which makes them refuse a field turned back
        # into a `const` -- worth keeping deliberately rather than by luck. A const is inlined into
        # every other assembly that reads it, which would leave ServerBuildStamp comparing its own
        # baked copy of the Shared value against its own stamp: equal by construction, mismatch
        # undetectable, in exactly the case the comparison exists for.
        # Mutation-tested 2026-09-06: renaming any one of the three fields, or restoring the const,
        # each makes this refuse; the unmutated source passes.
        $substitutions = @(
            @{ Pattern = '(?m)(readonly string Commit\s*=\s*)"[^"]*";'
               Replace = "`$1`"$commit`";"
               Expect  = "readonly string Commit = `"$commit`";" },
            @{ Pattern = '(?m)(readonly string BuiltAtUtc\s*=\s*)"[^"]*";'
               Replace = "`$1`"$builtAtUtc`";"
               Expect  = "readonly string BuiltAtUtc = `"$builtAtUtc`";" },
            @{ Pattern = '(?m)(readonly bool Dirty\s*=\s*)(true|false);'
               Replace = "`$1$($Dirty.ToString().ToLowerInvariant());"
               Expect  = "readonly bool Dirty = $($Dirty.ToString().ToLowerInvariant());" }
        )

        foreach ($s in $substitutions) {
            $stamped = $stamped -replace $s.Pattern, $s.Replace

            if ($stamped -notmatch [regex]::Escape($s.Expect)) {
                throw "the build stamp in $stampFile was not written: expected to find " +
                      "'$($s.Expect)' after substitution and did not. That field's declaration no " +
                      "longer matches the pattern this script rewrites, so the build would have " +
                      "shipped a partial or absent stamp while reporting success. Fix the pattern " +
                      "here or the field there."
            }
        }

        Set-Content -LiteralPath $stampFile -Value $stamped -NoNewline
    }

    $line = "$commit$(if ($Dirty) { '-dirty' }) $builtAtUtc"
    Write-Host "[build] stamp : $line"

    return [pscustomobject]@{
        Commit = $commit; BuiltAtUtc = $builtAtUtc; Dirty = $Dirty
        Line = $line; Originals = $originals
    }
}

<#
.SYNOPSIS
Puts the stamp sources back exactly as they were.

.DESCRIPTION
Restores by CONTENT, not by `git checkout`. The files are tracked, but a checkout would also
discard a legitimate uncommitted edit somebody had in them before the build started.
#>
function Restore-BuildStamp {
    param([hashtable]$Originals)

    if (-not $Originals -or $Originals.Count -eq 0) { return }

    foreach ($file in $Originals.Keys) {
        Set-Content -LiteralPath $file -Value $Originals[$file] -NoNewline
    }
}
