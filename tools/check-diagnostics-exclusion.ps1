# tools/check-diagnostics-exclusion.ps1 — Net/Diagnostics stays out of a shipping player.
#
# WHY THIS GATE EXISTS
#
# plans/asmdef-seam/plan.md success criterion 5 reads "Net/Diagnostics is excluded from player
# builds, and SOMETHING FAILS IF IT IS RE-INCLUDED." The first half shipped on 2026-08-21: every
# .cs under Assets/Scripts/Net/Diagnostics/ is wrapped in `#if !IRONFRONT_NO_DIAGNOSTICS`, and
# EditorBuildWindowsHarness's -noDiagnostics flag proves it by building the same player both ways.
#
# The second half did not exist. Nothing failed if a guard was dropped, nothing failed if a new
# unguarded file landed in the folder, and nothing failed if code was appended after the closing
# #endif. A build-configuration guarantee that nothing checks is the shape that decays silently:
# it is correct on the day it is written, and no diff afterwards ever reads as breaking it.
#
# THE SENSE IS INVERTED, AND THAT IS LOAD-BEARING
#
# BuildPlayerOptions.extraScriptingDefines can only ADD a symbol, never subtract one. A positive
# IRONFRONT_DIAGNOSTICS would therefore have to be OFF in ProjectSettings and switched on for the
# Editor, the EditMode tests, the lane-B harness and the linked .NET test projects — i.e. for
# everything except the one build that does not exist yet. Defaulting ON and letting a shipping
# build ADD IRONFRONT_NO_DIAGNOSTICS is the only arrangement the mechanism supports, which is why
# RULE 1 matches the negated form EXACTLY rather than accepting any guard shaped roughly like it.
#
# There is a second reason, easy to miss. Ironfront.Net.Replication.Tests LINKS four of these
# files (ScriptedInputProgramme, ScriptedInputCursor, ScriptedAim, LaneBSpawnPin) and compiles
# them a second time under stricter settings. Flipping to a positive define would compile all four
# to nothing and break `dotnet build` — but only after someone had already shipped the flip.
#
# WHAT RULE 3 IS FOR, AND WHY IT IS NOT PARANOIA
#
# MovementShadowCompare.cs's header carries this sentence:
#
#     "Nothing outside Assets/Scripts/Net/Diagnostics/ names a type from this folder: the ten
#      mentions elsewhere are doc-comments, checked 2026-08-21."
#
# That is a stored negative result with nothing re-checking it, and it is what makes the strip
# safe: a stripped folder whose types are named from outside leaves a dangling reference and the
# shipping build fails to compile. By 2026-08-26 the ten mentions were fifteen — still all
# doc-comments, re-measured, but the count had moved and nobody had looked. RULE 3 is that
# sentence turned into something that fails.
#
# WHY THIS IS NOT AN ASMDEF
#
# The obvious mechanism is one asmdef with `defineConstraints: ["!IRONFRONT_NO_DIAGNOSTICS"]`,
# replacing thirteen #if blocks with one line. It is deferred to phase C4 on purpose, and the
# reason is a measurement: of the 13 distinct types Net/Diagnostics reaches for outside itself,
# EIGHT are declared in Net/Client — C4's folder, still inside Assembly-CSharp today. Sealing
# before C4 means writing eight interfaces whose whole purpose disappears the moment Net/Client
# becomes an assembly Diagnostics can simply reference. See plans/asmdef-seam/phases/
# phase-c3-net-diagnostics.md § 0.
#
# So this gate guards the #if arrangement, and it is written to be DELETED at C4 rather than
# carried forward: when the asmdef lands, RULE 1 and RULE 2 are replaced by the defineConstraints
# line and RULE 3 is replaced by the compiler. RULE 4 outlives both.
#
# WHY CI CANNOT OTHERWISE SEE IT
#
# No CI job builds a Unity player (tools/check-plugin-define-constraints.ps1's header records the
# same fact from the other direction). `dotnet build` compiles four of these files with the define
# absent, which exercises the INCLUDED configuration only. The first thing that would notice a
# broken exclusion is a human building a shipping client — and there is no shipping client target
# in this repo yet, so the answer is "nothing, for as long as it takes".
#
# WHAT IT DELIBERATELY DOES NOT CHECK
#
# A scene or prefab holding one of these MonoBehaviours by GUID. Stripping the folder would turn
# that into a missing script, and no rule below would see it — RULE 3 reads source only.
#
# Measured 2026-08-26: all 13 .cs.meta files carry a GUID, and ZERO of those GUIDs appear in any
# .unity or .prefab under Assets/. Every diagnostics component is added at runtime with
# AddComponent, so there is nothing to orphan today.
#
# It is not gated, for two reasons. Dropping TransportDebugOverlay onto a scene to look at
# something is a legitimate debugging move, and a gate that fails an investigation is one that
# gets deleted during the investigation. And the failure it would prevent is a missing-script
# WARNING in a shipping client build — which does not exist in this repo yet, and which is the
# intended outcome of the strip in any case. Recorded here so the next person knows the question
# was asked and answered rather than missed.
#
# EXIT CODES
#   0  clean
#   1  a violation
#   2  could not tell — the scan found no Diagnostics sources, or no type declarations to match
#      against. Deliberately NOT 0: an empty scan that exits 0 is a green nobody earned.
#
# Usage:  pwsh tools/check-diagnostics-exclusion.ps1

[CmdletBinding()]
param(
    [string]$AssetsPath = "Ironfront_Reborn/Assets"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$assets   = Join-Path $repoRoot $AssetsPath
$diagRoot = Join-Path $assets ('Scripts' + [IO.Path]::DirectorySeparatorChar +
                               'Net'     + [IO.Path]::DirectorySeparatorChar + 'Diagnostics')

# Matched literally. A guard "shaped roughly like" this one is a finding, not a near miss —
# see the inverted-sense note above.
$GuardOpen  = '#if !IRONFRONT_NO_DIAGNOSTICS'
$Define     = 'IRONFRONT_NO_DIAGNOSTICS'

# The build that PROVES the strip. Without it the guard is never exercised in either direction
# and rules 1-3 grade a mechanism nobody runs.
$BuildFile = Join-Path $assets ('Editor' + [IO.Path]::DirectorySeparatorChar +
                                'EditorBuildWindowsHarness.cs')

Write-Host "=== Net/Diagnostics compiles out of a shipping player ==="

if (-not (Test-Path $diagRoot)) {
    Write-Host "COULD NOT TELL: '$AssetsPath/Scripts/Net/Diagnostics' does not exist."
    exit 2
}

function ConvertTo-RepoRelative([string]$fullPath) {
    return $fullPath.Substring($repoRoot.Length + 1).Replace('\', '/')
}

# A name in a comment or a string literal is not a reference. This is the property the C2
# measurement lacked: "Helicopter 16x" was 16 substring hits and 0 references, all of them the
# folder's own type names and one axis string. See tools/check-net-layering.ps1 RULE 5.
function Get-CodeLines([string]$fullPath) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($line in (Get-Content -Path $fullPath)) {
        if ($line -match '^\s*(//|///|\*|/\*)') { continue }
        $out.Add(($line -replace '"(?:\\.|[^"\\])*"', '""'))
    }
    return $out
}

$violations = @()

$diagSources = @(Get-ChildItem -Path $diagRoot -Filter *.cs -Recurse -File)
if ($diagSources.Count -eq 0) {
    Write-Host "COULD NOT TELL: no .cs files under '$AssetsPath/Scripts/Net/Diagnostics'."
    exit 2
}

# ---------------------------------------------------------------------------------------------
# RULE 1 (guarded) and RULE 2 (whole file).
#
# Two rules and not one, because "the guard is missing" and "the guard closes early" are
# different regressions with different fixes, and a file can only be reported for the second
# once it has passed the first. Reporting them together would let a missing guard read as a
# nesting problem.
# ---------------------------------------------------------------------------------------------
foreach ($file in $diagSources) {
    $relative = ConvertTo-RepoRelative $file.FullName
    $lines    = @(Get-Content -Path $file.FullName)

    # First line that is neither blank nor a comment. The guard sits ABOVE the usings, so this
    # is where it must be — a guard below them leaves the using directives in a stripped build.
    $firstCode = $null
    $firstIndex = -1
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $t = $lines[$i].Trim()
        if ($t -eq '') { continue }
        if ($t -match '^(//|///|\*|/\*)') { continue }
        $firstCode = $t
        $firstIndex = $i
        break
    }

    if ($firstCode -ne $GuardOpen) {
        $violations += "RULE 1 (unguarded): $relative does not open with '$GuardOpen'.`n" +
                       "    Found: $(if ($null -eq $firstCode) { '(no code lines at all)' } else { "'$firstCode'" })`n" +
                       "    Every file in this folder compiles out of a shipping player, and the`n" +
                       "    guard is how. The negated form is not a style choice: extraScripting-`n" +
                       "    Defines can only ADD a symbol, so a positive IRONFRONT_DIAGNOSTICS`n" +
                       "    would have to be switched on for the Editor, the EditMode tests, the`n" +
                       "    lane-B harness AND the four files Ironfront.Net.Replication.Tests`n" +
                       "    links — everything except the build that does not exist yet."
        continue
    }

    # RULE 2 — the guard must still be open on the last line of the file. Tracking depth rather
    # than matching the final #endif by text: an inner #if/#endif pair balances out, and a file
    # ending in #endif tells you nothing about WHICH directive it closes. Code appended after
    # the outer #endif is the regression this exists for, and it ends the file in #endif too.
    $depth = 0
    $closedAt = -1
    for ($i = $firstIndex; $i -lt $lines.Count; $i++) {
        $t = $lines[$i].Trim()
        if ($t -match '^#if\b')     { $depth++;  continue }
        if ($t -match '^#endif\b')  {
            $depth--
            if ($depth -eq 0 -and $closedAt -lt 0) { $closedAt = $i }
            continue
        }
    }

    if ($depth -ne 0) {
        $violations += "RULE 2 (unbalanced): $relative opens the guard and never closes it " +
                       "(depth $depth at EOF).`n" +
                       "    This does not compile, so it is caught by the next Unity refresh — " +
                       "reported here because a gate that stays silent on the loudest form of " +
                       "the same fault is one nobody trusts on the quiet forms."
        continue
    }

    # Anything but blank after the outer #endif is code outside the guard.
    $trailing = @()
    for ($i = $closedAt + 1; $i -lt $lines.Count; $i++) {
        if ($lines[$i].Trim() -ne '') { $trailing += "        line $($i + 1): $($lines[$i].Trim())" }
    }
    if ($trailing.Count -gt 0) {
        $violations += "RULE 2 (partial): $relative closes '$GuardOpen' at line " +
                       "$($closedAt + 1), but $($trailing.Count) line(s) follow it:`n" +
                       ($trailing -join "`n") + "`n" +
                       "    Those lines are in EVERY player build. A guard that covers most of a`n" +
                       "    file reads exactly like one that covers all of it, which is why this`n" +
                       "    is measured rather than eyeballed."
    }
}

# ---------------------------------------------------------------------------------------------
# RULE 3 (no dangling reference) — nothing outside the folder NAMES a type declared inside it.
#
# TOP-LEVEL declarations only. A nested type is reachable only as Outer.Inner, so an outside
# reference to one necessarily names its outer type too and is caught anyway — while including
# them would put `Outcome` (nested in LaneBSpawnPin) and `Solution` (nested in
# ScriptedTargetSolver) into a bare-identifier match. Those two names collide with ordinary
# locals across the tree, and check-net-layering.ps1's RULE 5b already recorded what a gate that
# fires on every lowercase local is worth: nobody keeps it.
# ---------------------------------------------------------------------------------------------
$topLevelDecl = '^ {0,4}(?:(?:public|internal|sealed|abstract|partial|static|unsafe)\s+)*(?:class|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)'

$diagTypes = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
foreach ($file in $diagSources) {
    foreach ($line in (Get-CodeLines $file.FullName)) {
        if ($line -match $topLevelDecl) { [void]$diagTypes.Add($Matches[1]) }
    }
}

if ($diagTypes.Count -eq 0) {
    Write-Host "COULD NOT TELL: no top-level type declarations found under Net/Diagnostics, so"
    Write-Host "                RULE 3 had nothing to match against. An empty set matches nothing"
    Write-Host "                and would have passed for the wrong reason."
    exit 2
}

# ORDINAL, for the reason check-net-layering.ps1 records: PowerShell is case-insensitive by
# default and C# is not.
$outsideScanned = 0
foreach ($file in (Get-ChildItem -Path $assets -Filter *.cs -Recurse -File)) {
    if ($file.FullName.StartsWith($diagRoot, [StringComparison]::OrdinalIgnoreCase)) { continue }
    $outsideScanned++

    $relative = ConvertTo-RepoRelative $file.FullName
    # One row per (file, name): a type named on twelve lines is one finding with one fix.
    $named = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    foreach ($line in (Get-CodeLines $file.FullName)) {
        foreach ($name in ([regex]::Matches($line, '[A-Za-z_][A-Za-z0-9_]*') | ForEach-Object { $_.Value })) {
            if (-not $diagTypes.Contains($name)) { continue }
            if (-not $named.Add($name)) { continue }
            $violations += "RULE 3 (dangling): $relative names '$name', declared under " +
                           "Net/Diagnostics.`n" +
                           "    That folder compiles out under $Define, so this reference does " +
                           "not exist in a shipping player and the build fails there and " +
                           "nowhere else.`n" +
                           "    Doc-comments and string literals are stripped before matching, " +
                           "so this is a real call site. Route it through a type outside the " +
                           "folder, as NetLogUnitySink does for the transport sink LaneBHarness " +
                           "used to own."
        }
    }
}

# ---------------------------------------------------------------------------------------------
# RULE 4 (unexercised) — the build that proves the strip still passes the define.
#
# Rules 1-3 all grade the guard. None of them notices if the ONE build configuration that ever
# sets the symbol stops setting it — at which point the guard is true in every build that exists,
# the three rules above stay green forever, and the exclusion has quietly become decoration.
# This is the "would this check go red if the thing it guards were broken" question applied to
# the gate itself.
# ---------------------------------------------------------------------------------------------
if (-not (Test-Path $BuildFile)) {
    $violations += "RULE 4 (unexercised): $(ConvertTo-RepoRelative $BuildFile) does not exist.`n" +
                   "    It is the only build that sets $Define. Without it nothing ever compiles" +
                   " the excluded configuration, and rules 1-3 grade a mechanism nobody runs."
} else {
    # Comment lines stripped, string literals NOT — unlike everywhere else in this file, and the
    # first version of this rule got it wrong and reported a false positive against a healthy
    # tree. A scripting define can only reach BuildPlayerOptions as a string; the whole point of
    # `const string NoDiagnosticsDefine = "IRONFRONT_NO_DIAGNOSTICS"` is that it IS a literal.
    # Reusing Get-CodeLines here erases the only evidence the rule exists to find.
    $buildCode = @(Get-Content -Path $BuildFile | Where-Object { $_ -notmatch '^\s*(//|///|\*|/\*)' })
    $declaresDefine = @($buildCode | Where-Object { $_ -match [regex]::Escape($Define) }).Count -gt 0
    $passesDefine   = @($buildCode | Where-Object { $_ -match 'extraScriptingDefines' }).Count -gt 0

    if (-not $declaresDefine) {
        $violations += "RULE 4 (unexercised): EditorBuildWindowsHarness.cs no longer names " +
                       "$Define outside a comment.`n" +
                       "    The -noDiagnostics build is what turns the guard from an assertion " +
                       "into a demonstration. Losing it leaves rules 1-3 green against a strip " +
                       "no build has ever performed."
    }
    if (-not $passesDefine) {
        $violations += "RULE 4 (unexercised): EditorBuildWindowsHarness.cs no longer passes the " +
                       "define through extraScriptingDefines.`n" +
                       "    A const naming $Define that no BuildPlayerOptions reads is a string, " +
                       "not a mechanism."
    }
}

if ($violations.Count -gt 0) {
    Write-Host ""
    Write-Host "FAIL: the Net/Diagnostics exclusion moved."
    Write-Host ""
    foreach ($violation in $violations) { Write-Host "  $violation"; Write-Host "" }
    Write-Host "plans/asmdef-seam/plan.md success criterion 5 is what this gate holds. Fix the"
    Write-Host "call site or restore the guard; do not relax the rule to match what the tree does."
    exit 1
}

Write-Host ("PASS: {0} file(s) under Net/Diagnostics, each wrapped whole in '{1}'." -f $diagSources.Count, $GuardOpen)
Write-Host ("      RULE 3: {0} top-level type(s) declared there, named by none of the {1} other" -f $diagTypes.Count, $outsideScanned)
Write-Host ("              .cs file(s) under {0} — comments and string literals excluded." -f $AssetsPath)
Write-Host "      SCOPE: this is a claim about Assets/. The linked .NET test projects DO name"
Write-Host "             these types and are meant to: they compile the folder a second time with"
Write-Host "             the define absent, and never ship in a player."
Write-Host "      RULE 4: EditorBuildWindowsHarness still builds the excluded configuration, so"
Write-Host "              the strip above is demonstrated rather than asserted."
exit 0
