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
#   RULE 5 (seam)   — Net/Input is its own assembly, and an assembly cannot reference
#                     Assembly-CSharp. So: (a) the asmdef exists, and (b) no source under
#                     Net/Input names a type DECLARED in a predefined-assembly source. Rules 1-4
#                     watch one namespace; this one watches every legacy type name there is,
#                     because the legacy half of this codebase has no namespace to watch.
#
# WHY RULE 5 IS SHAPED THAT WAY (phase C2, 2026-08-26)
#
# The C2 plan said Net/Input named "~8" legacy types, with Helicopter at 16 references and
# FpsActorController at 15, and told the implementer to enumerate rather than trust the number.
# Enumerating returned TWO: LoadoutUi and OptionsUi. All 16 Helicopter hits were Net/Input's own
# HelicopterAxes / HelicopterControls / HelicopterAxisMap and the "Helicopter Pitch" axis string;
# all 15 FpsActorController hits were XML doc comments, plus one inside a Debug.Log literal.
# The measurement had been a substring grep over comments and self-references.
#
# That is why this rule strips comment lines AND double-quoted string literals before matching.
# A name inside a doc comment or a log message is not a reference, and a gate that says it is
# gets muted by the first person who has to explain the seam in prose.
#
# The type set is derived from the tree rather than hardcoded, so it needs no maintenance as
# C3 and C4 move folders out of Assembly-CSharp: a folder that gains an asmdef leaves the set
# on its own, and until then its types are correctly off-limits to Net/Input.
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

# RULE 6's baseline: the legacy type names Net/Client still contains, one row per NAME.
#
# WHY THIS EXISTS
#
# Phase C4 seals Net/Client into an assembly, and an assembly cannot reference Assembly-CSharp.
# That is one move but roughly ten bindings, so it lands as a sequence of sub-phases -- one
# binding CLUSTER each, never one file each. Between the first and the last, the folder is
# partly bound, and nothing but this table can tell "a name C4b has not reached yet" apart from
# "a name somebody just added back".
#
# rules/pinned-baseline-test-companion.md governs the shape: a baseline pinning a known gap ships
# a companion that fails when the pin stops being accurate, and the companion asserts by
# IDENTITY, not by count. A count would be satisfied by any eight names -- including eight
# entirely different ones. So both directions are asserted, by name:
#
#   RULE 6a (grow)   -- a legacy name in Net/Client that is NOT here FAILS. That is the whole
#                       point: the cluster you just bound must not be quietly re-opened, and a
#                       new crossing must not slip in while the folder is legitimately dirty.
#   RULE 6b (stale)  -- a row whose name Net/Client no longer contains FAILS, telling you to
#                       DELETE it. Otherwise the list becomes a graveyard, and a cluster that
#                       got bound reads exactly like one nobody touched.
#
# ON A RULE 6b FAILURE: delete the row. Do NOT re-pin the table to whatever the run reported.
# When the last `debt` row goes, this rule is finished and the seal rule replaces it -- see the
# note at RULE 6 itself.
#
# Kind = 'debt'          -- a real crossing, retired by the named sub-phase.
# Kind = 'not-a-reference' -- the matcher cannot resolve it and never will: a member named like a
#                       legacy type, a namespace segment, an enum member, or System.Action. These
#                       rows do NOT go away when C4 finishes, and each one states what it really
#                       is so the next reader does not go hunting for a crossing that is not
#                       there. Every one of these was verified by reading the call site; the same
#                       four cost phase C2 a wrong plan when they were counted as real.
$ClientBaseline = @(
    # THE EIGHT 'debt' ROWS THAT WERE HERE ARE GONE, deleted by phase C4b on this rule's own
    # instruction — Vehicle, VehicleSpawner, Projectile, GrenadeProjectile, ProjectileCatalogBuilder,
    # DecalManager, DecalType and ScoreUi. Each was bound behind an interface Net/Client owns, RULE
    # 6b reported the row stale, and the row was DELETED rather than re-pinned.
    #
    # WHAT THAT MEANS FOR THIS RULE: no 'debt' rows remain, so Net/Client names no legacy type at
    # all and is ready for its asmdef. The four rows below are matcher artefacts — they outlive the
    # refactor and become the seam rule's allow-list in C4c. See the RULE 6 header.

    @{ Type = 'State'                   ; Kind = 'not-a-reference'; Retires = 'never'
       Reason = 'a PROPERTY named State on eight client types (GameFlowController.State, ' +
                'driver.State, _agent.State). The legacy declaration it collides with is a ' +
                'private nested enum inside ActiveRaggy, which no client file can even see' }
    @{ Type = 'Action'                  ; Kind = 'not-a-reference'; Retires = 'never'
       Reason = 'System.Action, the delegate -- plus result.Action, a field on a replication ' +
                'struct. Assembly-CSharp happens to declare a MonoBehaviour called Action too' }
    @{ Type = 'Configuration'           ; Kind = 'not-a-reference'; Retires = 'never'
       Reason = 'the last segment of `using Ironfront.Net.Configuration;`, matched as a bare ' +
                'identifier because the matcher tokenises rather than parses' }
    @{ Type = 'Helicopter'              ; Kind = 'not-a-reference'; Retires = 'never'
       Reason = 'VehicleKind.Helicopter, an enum MEMBER on a replication-library enum. The ' +
                'identically-named legacy MonoBehaviour is not referenced anywhere in Net/Client' }
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

# RULE 5 — the Net/Input seam. Two assertions, because "the assembly is gone" and "the assembly
# reaches backwards" are different regressions and must not be able to mask each other.
$inputRoot   = Join-Path $root ('Net' + [IO.Path]::DirectorySeparatorChar + 'Input')
$inputAsmdef = Join-Path $inputRoot 'Ironfront.Net.Unity.Input.asmdef'

if (-not (Test-Path $inputRoot)) {
    Write-Host "COULD NOT TELL: '$ScriptsPath/Net/Input' does not exist, so RULE 5 checked nothing."
    exit 2
}

# 5a — the seam itself. Without the asmdef, Net/Input is back inside Assembly-CSharp and 5b
# below becomes vacuous (its own types would join the legacy set). Assert it directly.
if (-not (Test-Path $inputAsmdef)) {
    $violations += "RULE 5a (seam gone): Net/Input has no Ironfront.Net.Unity.Input.asmdef.`n" +
                   "    Without it Net/Input compiles into Assembly-CSharp again and every " +
                   "legacy type is reachable from it. The seam is the asmdef; deleting it is " +
                   "the regression, not a formatting choice."
}

# 5b — no Net/Input source names a type declared in a predefined-assembly source.
# Declarations are read at line start after modifiers, which is how every declaration in this
# tree is written, and how the 207-name baseline set was measured.
$declPattern = '^\s*(?:(?:public|internal|private|protected|static|sealed|abstract|partial|new|unsafe)\s+)*(?:class|struct|interface|enum)\s+([A-Za-z_][A-Za-z0-9_]*)'

# A name in a comment or inside a string literal is not a reference. See the RULE 5 note above
# for the measurement error that made stripping both non-negotiable.
function Get-CodeLines([string]$fullPath) {
    $out = New-Object System.Collections.Generic.List[string]
    foreach ($line in (Get-Content -Path $fullPath)) {
        if ($line -match '^\s*(//|///|\*|/\*)') { continue }
        $out.Add(($line -replace '"(?:\\.|[^"\\])*"', '""'))
    }
    return $out
}

# ORDINAL, and this is not a detail. PowerShell hashtables and -match are case-INSENSITIVE by
# default; C# is not. The first run of this rule matched the local `options` against the type
# `Options` and the parameter `weaponSlot` against `WeaponSlot`, and reported both as legacy
# references. A gate that fires on every lowercase local is one nobody keeps.
# Excluded roots are the folder being judged: a folder's OWN type declarations are not legacy
# names to it, and letting them in buries the real finding under a pile of self-matches. For
# RULE 5b that is belt and braces (5a already failed if Net/Input has no asmdef); for RULE 6,
# where Net/Client genuinely has no asmdef yet, it is load-bearing -- without it every client
# type would report itself.
function Get-LegacyTypeSet([string[]]$excludedRoots) {
    $set = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)

    foreach ($file in $allSources) {
        if (-not (Test-OutsideAsmdef $file.FullName)) { continue }

        $skip = $false
        foreach ($excluded in $excludedRoots) {
            if ($file.FullName.StartsWith($excluded, [StringComparison]::OrdinalIgnoreCase)) {
                $skip = $true
                break
            }
        }
        if ($skip) { continue }

        foreach ($line in (Get-CodeLines $file.FullName)) {
            if ($line -match $declPattern) { [void]$set.Add($Matches[1]) }
        }
    }

    return $set
}

$legacyTypes = Get-LegacyTypeSet @($inputRoot)

if ($legacyTypes.Count -eq 0) {
    Write-Host "COULD NOT TELL: no type declarations found in predefined-assembly sources, so"
    Write-Host "                RULE 5b had nothing to match against. An empty set matches"
    Write-Host "                nothing and would have passed for the wrong reason."
    exit 2
}

$inputSources = @(Get-ChildItem -Path $inputRoot -Filter *.cs -Recurse -File)
if ($inputSources.Count -eq 0) {
    Write-Host "COULD NOT TELL: no .cs files under '$ScriptsPath/Net/Input'."
    exit 2
}

foreach ($file in $inputSources) {
    $relative = ConvertTo-RepoRelative $file.FullName
    # One row per (file, name). A type named on twelve lines is one finding with one fix, and
    # twelve identical paragraphs is how a reader stops reading the output.
    $named = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::Ordinal)
    # No line numbers: comment lines are dropped before matching, so any index counted here
    # would point at the wrong line in the file and send the reader somewhere innocent.
    foreach ($line in (Get-CodeLines $file.FullName)) {
        foreach ($name in ([regex]::Matches($line, '[A-Za-z_][A-Za-z0-9_]*') | ForEach-Object { $_.Value })) {
            if (-not $legacyTypes.Contains($name)) { continue }
            if (-not $named.Add($name)) { continue }
            $violations += "RULE 5b (reaches back): $relative names '$name', which is declared " +
                           "in a predefined-assembly source.`n" +
                           "    Ironfront.Net.Unity.Input cannot reference Assembly-CSharp, so " +
                           "this does not compile — and if it does, the asmdef is gone. Route " +
                           "it through an interface this assembly owns, as " +
                           "ILocalInputEnvironment does for LoadoutUi and OptionsUi."
        }
    }
}

# RULE 6 — the Net/Client seam, phase C4c.
#
# THIS RULE CHANGED JOB ON 2026-08-26, exactly as its previous header said it would. Through C4a
# and C4b it held a SHRINKING BASELINE: Net/Client was not an assembly yet, its legacy surface was
# supposed to shrink monotonically, and a baseline plus an identity-keyed companion is what holds
# a monotonic shrink. C4b removed the last `debt` row. Net/Client then took its asmdef, and this
# became a 5a/5b-shaped SEAM assertion — with the four surviving rows carried over as its
# allow-list, because those four names are matcher artefacts and outlive the refactor entirely.
#
# The three assertions below are separate on purpose. "The assembly is gone", "the assembly
# reaches backwards" and "an allow-list row went stale" are different regressions, and any one of
# them masking another is how a seam rots quietly.
$clientRoot   = Join-Path $root ('Net' + [IO.Path]::DirectorySeparatorChar + 'Client')
$clientAsmdef = Join-Path $clientRoot 'Ironfront.Net.Unity.Client.asmdef'

if (Test-Path $clientRoot) {
    $clientSources = @(Get-ChildItem -Path $clientRoot -Filter *.cs -Recurse -File)

    # 6a - the seam. Mirrors RULE 5a: without the asmdef, Net/Client compiles back into
    # Assembly-CSharp and every legacy type is reachable from it again. Asserted DIRECTLY rather
    # than inferred from 6b, because 6b would stay GREEN right through the deletion: it matches
    # names, and deleting an asmdef changes no name in any file. That is exactly the shape of
    # silent regression this rule exists to refuse.
    if (-not (Test-Path $clientAsmdef)) {
        $violations += "RULE 6a (seam gone): Net/Client has no Ironfront.Net.Unity.Client.asmdef.`n" +
                       "    Without it Net/Client is inside Assembly-CSharp again, every legacy " +
                       "type is reachable from it, and the EditMode suite phase C4 exists for " +
                       "cannot reference it at all. The seam IS the asmdef; deleting it is the " +
                       "regression, not a formatting choice."
    }

    if ($clientSources.Count -eq 0) {
        Write-Host "COULD NOT TELL: no .cs files under '$ScriptsPath/Net/Client'."
        exit 2
    }

    # Its own declarations are not legacy names to it. Without this exclusion every client type
    # reports itself and the rule is noise rather than a gate.
    $clientLegacyTypes = Get-LegacyTypeSet @($inputRoot, $clientRoot)

    if ($clientLegacyTypes.Count -eq 0) {
        Write-Host "COULD NOT TELL: no type declarations found outside Net/Client, so RULE 6 had"
        Write-Host "                nothing to match against. An empty set matches nothing and"
        Write-Host "                would have passed for the wrong reason."
        exit 2
    }

    $clientAllowed = @{}
    foreach ($entry in $ClientBaseline) { $clientAllowed[$entry.Type] = $entry }

    # name -> the files naming it, so a RULE 6b failure can say where the row USED to apply and a
    # 6a failure can point at the call site rather than at the folder.
    $clientNamed = @{}
    foreach ($file in $clientSources) {
        $relative = ConvertTo-RepoRelative $file.FullName
        foreach ($line in (Get-CodeLines $file.FullName)) {
            foreach ($name in ([regex]::Matches($line, '[A-Za-z_][A-Za-z0-9_]*') | ForEach-Object { $_.Value })) {
                if (-not $clientLegacyTypes.Contains($name)) { continue }
                if (-not $clientNamed.ContainsKey($name)) { $clientNamed[$name] = @{} }
                $clientNamed[$name][$relative] = $true
            }
        }
    }

    # 6b - reaches back. Mirrors RULE 5b.
    foreach ($name in ($clientNamed.Keys | Sort-Object)) {
        if ($clientAllowed.ContainsKey($name)) { continue }

        $where = ($clientNamed[$name].Keys | Sort-Object) -join ', '
        $violations += "RULE 6b (reaches back): Net/Client names '$name', which is declared in " +
                       "a predefined-assembly source and is not in `$ClientBaseline.`n" +
                       "    Named by: $where`n" +
                       "    Ironfront.Net.Unity.Client cannot reference Assembly-CSharp, so this " +
                       "does not compile — and if it does, the asmdef is gone and 6a should have " +
                       "fired first. Route it through an interface this assembly owns, as " +
                       "IGameplayActorPresence does for Actor. If it is a matcher artefact rather " +
                       "than a reference, add a 'not-a-reference' row SAYING WHAT IT REALLY IS."
    }

    # 6c - stale. The companion, asserting by identity.
    foreach ($entry in $ClientBaseline) {
        if ($clientNamed.ContainsKey($entry.Type)) { continue }

        $violations += "RULE 6c (stale): Net/Client no longer names '$($entry.Type)'.`n" +
                       "    Reason it was listed: $($entry.Reason)`n" +
                       "    DELETE the row. Do NOT re-pin the table to what this run reported: " +
                       "that renames a fix 'expected' and is how a gate gets muted. Every " +
                       "surviving row is a MATCHER ARTEFACT — a member, an enum value, a " +
                       "namespace segment — so a stale one means the matcher stopped colliding, " +
                       "and the allow-list shrinks with it rather than becoming a graveyard " +
                       "nobody re-reads."
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
Write-Host ("      RULE 5: Net/Input carries its asmdef and names none of the {0} type(s) declared" -f $legacyTypes.Count)
Write-Host ("              in predefined-assembly sources, across {0} of its own file(s)." -f $inputSources.Count)
if (Test-Path $clientRoot) {
    Write-Host ("      RULE 6: Net/Client carries its asmdef and names {0} of the {1} type(s) declared" -f
                $clientNamed.Count, $clientLegacyTypes.Count)
    Write-Host ("              in predefined-assembly sources, across {0} of its own file(s) — and every" -f $clientSources.Count)
    Write-Host "              one of those is an allow-listed matcher artefact, not a reference."
}
Write-Host "      This says no NEW call site appeared and no listed one was silently fixed. It"
Write-Host "      does NOT say the listed call sites are acceptable — that is what the count is for."
exit 0
