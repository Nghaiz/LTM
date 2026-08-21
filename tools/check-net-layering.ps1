# tools/check-net-layering.ps1 — nothing in Assembly-CSharp reaches into the server assembly
# except the files named here, and every file named here still reaches.
#
# WHY THIS GATE EXISTS
#
# Ledger row E-11 ("F — the asmdef split") is filed as a layering problem an asmdef would fix.
# It is not, in both directions:
#
#   1. An asmdef cannot fix it. `Assets/Scripts/NetBindings/` implements the seams
#      Ironfront.Net.Unity.Server declares, in terms of the original game's own types, and it
#      can only do that from Assembly-CSharp — predefined assemblies compile last and reference
#      every asmdef, while no asmdef references back. So Assembly-CSharp MUST reference the
#      server assembly, and once it does, all 333 legacy files may call into it. Setting
#      `autoReferenced: false` on the server asmdef closes that and kills NetBindings with it.
#
#   2. An asmdef is not needed to fix it. The reference is a `using` line, and a `using` line
#      can be gated.
#
# WHAT WAS ACTUALLY FOUND, 2026-08-21
#
# Fifteen files under Assets/Scripts/ referenced Ironfront.Net.Unity.Server outside a comment.
# Two are the bindings, which is by design. Eleven are legacy gameplay files. One is a
# diagnostics harness. And one — Net/Client/NetClientObjectivePresenter.cs — was a CLIENT
# presenter reading NetServerBindings.CapturePoints. E-11 reads as "nothing prevents client code
# calling server code"; the tree said it was not a risk but a present-tense fact with a name.
#
# That fifteenth was fixed rather than baselined, in the commit that added this gate:
# ICapturePointDirectory moved to Ironfront.Net.Unity.Shared and the registration with it, so
# the presenter reads NetSceneBindings and names nothing server-side. RULE 4 is what holds it.
#
# A sixteenth was found by the gate's own first run and was never real: ProjectileCatalogBuilder
# mentions the namespace only in a doc-comment at :38, which the comment skip below ignores.
# RULE 2 reported the baseline row as stale, and the row was deleted rather than re-pinned.
#
# THE BASELINE, AND ITS LEASH
#
# rules/pinned-baseline-test-companion.md: a baseline that pins broken state ships a companion
# that fails when the pin stops being accurate, and the companion asserts by IDENTITY, not by
# count. So:
#
#   RULE 1 (grow)   — a file outside $Baseline that references the server namespace FAILS. New
#                     debt cannot be added quietly.
#   RULE 2 (shrink) — a $Baseline entry that NO LONGER references it FAILS, telling you to
#                     delete the row. Otherwise the list becomes a graveyard nobody re-reads,
#                     and a fix looks identical to a file nobody touched.
#   RULE 3 (exist)  — a $Baseline entry naming a file that is gone FAILS, for the same reason
#                     one direction over: a renamed file must move its row, not orphan it.
#   RULE 4 (hard)   — Net/Client and Net/Input may not reference the server namespace at all,
#                     with no baseline available. Diagnostics is allowed while LaneBHarness
#                     needs it; a presenter is not.
#
# On a RULE 2 failure: DELETE the row. Do not re-pin the list to whatever the run reported —
# that renames a fix "expected" and is how a gate gets muted.
#
# EXIT CODES
#   0  clean
#   1  a violation
#   2  could not tell — the scan found no sources. Deliberately NOT 0: an empty scan that exits
#      0 is a green nobody earned (the reservation tools/ClientWiringGate makes).
#
# Usage:  pwsh tools/check-net-layering.ps1

[CmdletBinding()]
param(
    [string]$ScriptsPath = "Ironfront_Reborn/Assets/Scripts"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$root     = Join-Path $repoRoot $ScriptsPath

# The namespace the wall is around. Matches a using line and a fully-qualified reference alike.
$serverNamespace = 'Ironfront\.Net\.Unity\.Server'

# Paths are repo-relative with forward slashes, so the list reads the same on every platform.
# Reason is printed on a RULE 2 failure, so whoever deletes the row knows what it was for.
$Baseline = @(
    @{ Path = 'Ironfront_Reborn/Assets/Scripts/NetBindings/IronfrontNetBindings.cs'
       Kind = 'structural'
       Reason = 'implements the seams the server assembly declares; can only live in Assembly-CSharp' }
    @{ Path = 'Ironfront_Reborn/Assets/Scripts/NetBindings/NetDriverInputSink.cs'
       Kind = 'structural'
       Reason = 'the Assembly-CSharp half of IDriverInputSink' }

    @{ Path = 'Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Actor.cs'                     ; Kind = 'debt'; Reason = 'legacy gameplay calls the server assembly directly' }
    @{ Path = 'Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ActorManager.cs'              ; Kind = 'debt'; Reason = 'legacy gameplay calls the server assembly directly' }
    @{ Path = 'Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/AiActorController.cs'         ; Kind = 'debt'; Reason = 'legacy gameplay calls the server assembly directly' }
    @{ Path = 'Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ExplodingProjectile.cs'       ; Kind = 'debt'; Reason = 'legacy gameplay calls the server assembly directly' }
    @{ Path = 'Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/GrenadeProjectile.cs'         ; Kind = 'debt'; Reason = 'legacy gameplay calls the server assembly directly' }
    @{ Path = 'Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Projectile.cs'                ; Kind = 'debt'; Reason = 'legacy gameplay calls the server assembly directly' }
    @{ Path = 'Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ProjectileCatalogInstaller.cs'; Kind = 'debt'; Reason = 'legacy gameplay calls the server assembly directly' }
    @{ Path = 'Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ProjectileNetAnnouncer.cs'    ; Kind = 'debt'; Reason = 'legacy gameplay calls the server assembly directly' }
    @{ Path = 'Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/ProjectileNetSync.cs'         ; Kind = 'debt'; Reason = 'legacy gameplay calls the server assembly directly' }
    @{ Path = 'Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/Vehicle.cs'                   ; Kind = 'debt'; Reason = 'legacy gameplay calls the server assembly directly' }
    @{ Path = 'Ironfront_Reborn/Assets/Scripts/Assembly-CSharp/VehicleSpawner.cs'            ; Kind = 'debt'; Reason = 'legacy gameplay calls the server assembly directly' }

    @{ Path = 'Ironfront_Reborn/Assets/Scripts/Net/Diagnostics/LaneBHarness.cs'
       Kind = 'debt'
       Reason = 'the lane-B harness strips the server half of a listen-server scene, so it names it' }
)

Write-Host "=== Assembly-CSharp does not reach into Ironfront.Net.Unity.Server ==="

if (-not (Test-Path $root)) {
    Write-Host "COULD NOT TELL: '$ScriptsPath' does not exist under $repoRoot."
    exit 2
}

# A file is "outside any asmdef" when no ancestor directory up to $root carries one. Those are
# the files Unity compiles into the predefined Assembly-CSharp.
function Test-OutsideAsmdef([string]$filePath) {
    $dir = Split-Path -Parent $filePath
    while ($dir -and $dir.Length -ge $root.Length) {
        if (Get-ChildItem -Path $dir -Filter *.asmdef -File -ErrorAction SilentlyContinue) {
            return $false
        }
        $dir = Split-Path -Parent $dir
    }
    return $true
}

function ConvertTo-RepoRelative([string]$fullPath) {
    return $fullPath.Substring($repoRoot.Length + 1).Replace('\', '/')
}

$allSources = Get-ChildItem -Path $root -Filter *.cs -Recurse -File
if ($allSources.Count -eq 0) {
    Write-Host "COULD NOT TELL: no .cs files under '$ScriptsPath'."
    exit 2
}

# Comment lines are skipped for the same reason the sibling gate skips them: this rule is
# discussed in XML docs on purpose, and flagging the explanation makes the gate unfixable
# except by deleting the explanation.
function Test-ReferencesServer([string]$fullPath) {
    foreach ($line in (Get-Content -Path $fullPath)) {
        if ($line -match '^\s*(//|///|\*|/\*)') { continue }
        if ($line -match $serverNamespace) { return $true }
    }
    return $false
}

$baselinePaths = @{}
foreach ($entry in $Baseline) { $baselinePaths[$entry.Path] = $entry }

$violations = @()
$scanned    = 0
$offenders  = @{}

foreach ($file in $allSources) {
    if (-not (Test-OutsideAsmdef $file.FullName)) { continue }
    $scanned++

    $relative = ConvertTo-RepoRelative $file.FullName
    if (-not (Test-ReferencesServer $file.FullName)) { continue }
    $offenders[$relative] = $true

    # RULE 1 — grow.
    if (-not $baselinePaths.ContainsKey($relative)) {
        $violations += "RULE 1 (new debt): $relative references Ironfront.Net.Unity.Server " +
                       "and is not in the baseline.`n" +
                       "    Assembly-CSharp code calling the server assembly is exactly what " +
                       "E-11 names. Route it through a seam, or state the debt in `$Baseline " +
                       "with a reason."
    }
}

# RULE 2 (shrink) and RULE 3 (exist).
foreach ($entry in $Baseline) {
    $full = Join-Path $repoRoot ($entry.Path -replace '/', [IO.Path]::DirectorySeparatorChar)

    if (-not (Test-Path $full)) {
        $violations += "RULE 3 (orphan): baseline names $($entry.Path), which does not exist.`n" +
                       "    A renamed file moves its row; it does not leave one behind."
        continue
    }

    if (-not $offenders.ContainsKey($entry.Path)) {
        $violations += "RULE 2 (stale): $($entry.Path) no longer references the server " +
                       "namespace — the debt is PAID.`n" +
                       "    Reason it was listed: $($entry.Reason)`n" +
                       "    DELETE the row. Do NOT re-pin the list to what this run reported."
    }
}

# RULE 4 — hard, no baseline. Client and input code may not name the server assembly at all.
foreach ($hard in @('Net/Client', 'Net/Input')) {
    $hardRoot = Join-Path $root ($hard -replace '/', [IO.Path]::DirectorySeparatorChar)
    if (-not (Test-Path $hardRoot)) { continue }

    foreach ($file in (Get-ChildItem -Path $hardRoot -Filter *.cs -Recurse -File)) {
        if (-not (Test-ReferencesServer $file.FullName)) { continue }
        $relative = ConvertTo-RepoRelative $file.FullName
        $violations += "RULE 4 (hard): $relative references Ironfront.Net.Unity.Server.`n" +
                       "    Client and input code has no baseline available. A presenter that " +
                       "reads a server binding is the failure E-11 describes, not an instance " +
                       "of it that can be grandfathered."
    }
}

if ($violations.Count -gt 0) {
    Write-Host ""
    Write-Host "FAIL: the layering between Assembly-CSharp and Ironfront.Net.Unity.Server moved."
    Write-Host ""
    foreach ($violation in $violations) { Write-Host "  $violation"; Write-Host "" }
    Write-Host "A rise is new debt: fix the call site. A fall is a fix: delete the baseline row."
    Write-Host "The two read identically in a diff, which is why this gate distinguishes them."
    exit 1
}

$debtCount = ($Baseline | Where-Object { $_.Kind -eq 'debt' }).Count
Write-Host ("PASS: {0} predefined-assembly source(s) scanned; {1} reference the server assembly," -f $scanned, $offenders.Count)
Write-Host ("      all named in the baseline; {0} of them are debt, and every row still applies." -f $debtCount)
Write-Host "      Net/Client and Net/Input name it nowhere."
Write-Host "      This says no NEW call site appeared and no listed one was silently fixed. It"
Write-Host "      does NOT say the listed call sites are acceptable — that is what the count is for."
exit 0
