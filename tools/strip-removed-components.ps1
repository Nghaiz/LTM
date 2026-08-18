# tools/strip-removed-components.ps1 — deletes component blocks whose Unity class no longer
# exists from serialized scenes and prefabs.
#
# WHY THIS EXISTS
#
# The Unity project was authored on Unity 5 (the YAML still carries m_PrefabParentObject) and
# is opened today on 6000.3.21f1. Classes Unity has since removed are still serialized in the
# scene files, and every open logs an error per instance:
#
#     Component GUI Layer in Camera for Scene Assets/Scenes/Menu.unity is no longer available.
#     It will be removed after you edit this GameObject and save the Scene.
#     Component at index 3 could not be loaded when loading game object 'Camera'. Removing it.
#
# Unity offers to fix this "after you edit this GameObject and save the Scene", which means
# opening all four scenes and re-saving each one — and a Unity 6 re-save of a Unity 5 scene
# rewrites the whole file, producing a multi-thousand-line diff in which the actual fix is
# invisible to a reviewer. Doing the deletion here keeps the diff to the handful of lines that
# are genuinely wrong, and it is the same edit Unity would have made.
#
# WHAT IT WILL NOT DO
#
# It refuses to touch a component that anything other than its owner's m_Component list points
# at. A removed class cannot be a live reference target, so a second reference means the file
# is not the shape this script assumes and a human should look at it.
#
# "Anything" is meant literally, and the first version of this script did not mean it literally
# enough. It counted only the `{fileID: N}` spelling in the file being edited, which misses the
# cross-file shapes: a prefab override writes `{fileID: N, guid: G, type: 2}` and an
# m_Modifications entry writes `target: {fileID: N, guid: G}`. Both would have been invisible,
# the count would have stayed at 1, and the block would have been deleted out from under a live
# override in another file.
#
# The cross-file scan must be guid-qualified, and the obvious version of it is wrong. fileIDs
# are file-LOCAL: Menu.unity contains a perfectly ordinary `{fileID: 140}` of its own, which has
# nothing to do with Splash.unity's component 140. Matching the bare id repo-wide reports every
# such coincidence as a live external reference and refuses to fix real files — it did exactly
# that on Splash.unity the first time this scan was written. A reference is only cross-file when
# it carries the target file's own guid, so that is what is matched.
#
# Usage:
#     pwsh tools/strip-removed-components.ps1 -DryRun     # report only, changes nothing
#     pwsh tools/strip-removed-components.ps1             # apply

[CmdletBinding()]
param(
    # Searched recursively for *.unity and *.prefab.
    [string]$Path = "Ironfront_Reborn/Assets",

    # Unity class ids to strip. 92 is GUILayer, removed in 2019.3; it only ever serviced
    # GUIText and GUITexture, which were removed alongside it, so nothing can consume one.
    #
    # Before adding an id here, confirm the class is *removed* and not merely deprecated.
    # Deleting a class that still exists silently deletes working components.
    [int[]]$ClassIds = @(92),

    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $repoRoot

try {
    $root = Join-Path $repoRoot $Path
    if (-not (Test-Path $root)) { throw "Path not found: $root" }

    $files = Get-ChildItem -Path $root -Recurse -File -Include *.unity, *.prefab
    if ($files.Count -eq 0) {
        Write-Host "No scenes or prefabs under $Path." -ForegroundColor Yellow
        exit 0
    }

    Write-Host "Scanning $($files.Count) file(s) for class id(s) $($ClassIds -join ', ')"
    Write-Host ""

    # Every file is read once up front so the cross-file reference scan below does not re-read
    # the whole asset tree per candidate id. These files reach 9 MB, and the tank prefab alone
    # would otherwise be read eleven times.
    $contents = @{}
    foreach ($f in $files) { $contents[$f.FullName] = [System.IO.File]::ReadAllText($f.FullName) }

    $idPattern      = '^--- !u!(?<class>\d+) &(?<fileId>\d+)'
    $totalRemoved   = 0
    $filesChanged   = 0
    $filesSkipped   = 0

    foreach ($file in $files) {
        $raw = $contents[$file.FullName]

        # Preserve whatever the working copy uses. .gitattributes normalises *.unity to LF in
        # the repository, but rewriting a CRLF working copy to LF here would show every line
        # of a 9 MB scene as changed in `git diff` before normalisation.
        $eol   = if ($raw -match "`r`n") { "`r`n" } else { "`n" }
        $lines = $raw -split "`r?`n"

        # ---- pass 1: locate the blocks -------------------------------------------------
        $blocks = @()   # each: @{ Start; End; FileId; Class }
        $current = $null

        for ($i = 0; $i -lt $lines.Count; $i++) {
            $m = [regex]::Match($lines[$i], $idPattern)
            if (-not $m.Success) { continue }

            # A new document header closes the block that was open.
            if ($null -ne $current) {
                $current.End = $i - 1
                $blocks += $current
                $current = $null
            }

            $class = [int]$m.Groups['class'].Value
            if ($ClassIds -notcontains $class) { continue }

            $current = @{
                Start  = $i
                End    = $lines.Count - 1   # last block runs to EOF unless closed above
                FileId = $m.Groups['fileId'].Value
                Class  = $class
            }
        }
        if ($null -ne $current) { $blocks += $current }

        if ($blocks.Count -eq 0) { continue }

        # ---- pass 2: refuse anything that is referenced more than once -----------------
        #
        # Same-file: `{fileID: N}` or `{fileID: N, ...}`. The trailing [},] stops 598 matching
        # 5980. Cross-file: the id AND this file's guid, which is what makes it cross-file.
        $ownGuid = $null
        $metaPath = "$($file.FullName).meta"
        if (Test-Path $metaPath) {
            $mg = [regex]::Match([System.IO.File]::ReadAllText($metaPath), 'guid:\s*(?<g>[0-9a-f]{32})')
            if ($mg.Success) { $ownGuid = $mg.Groups['g'].Value }
        }

        $unsafe = @()
        foreach ($b in $blocks) {
            $own = [regex]::Matches($raw, "fileID:\s*$($b.FileId)\s*[},]").Count
            if ($own -ne 1) { $unsafe += "&$($b.FileId) (class $($b.Class)) has $own reference(s) in its own file, expected exactly 1" }

            if ($null -eq $ownGuid) {
                $unsafe += "&$($b.FileId) (class $($b.Class)): no .meta guid for this asset, so a cross-file reference cannot be ruled out"
                continue
            }

            $crossPattern = "fileID:\s*$($b.FileId)\s*,\s*guid:\s*$ownGuid"
            foreach ($other in $files) {
                if ($other.FullName -eq $file.FullName) { continue }
                $n = [regex]::Matches($contents[$other.FullName], $crossPattern).Count
                if ($n -gt 0) {
                    $rel = $other.FullName.Substring($repoRoot.Length + 1)
                    $unsafe += "&$($b.FileId) (class $($b.Class)) is referenced $n time(s) from $rel"
                }
            }
        }

        if ($unsafe.Count -gt 0) {
            $filesSkipped++
            Write-Host "  SKIP $($file.FullName.Substring($repoRoot.Length + 1))" -ForegroundColor Yellow
            foreach ($u in $unsafe) { Write-Host "       -> $u" -ForegroundColor Yellow }
            Write-Host "       -> not the expected shape; look at this one by hand" -ForegroundColor Yellow
            continue
        }

        # ---- pass 3: build the surviving line set --------------------------------------
        $drop = [System.Collections.Generic.HashSet[int]]::new()
        foreach ($b in $blocks) {
            for ($i = $b.Start; $i -le $b.End; $i++) { [void]$drop.Add($i) }
        }

        # The owner's list entry, in both the modern and the Unity 5 spelling:
        #     - component: {fileID: 598}
        #     - 92: {fileID: 598}
        $orphaned = @()
        foreach ($b in $blocks) {
            $hit = $false
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($drop.Contains($i)) { continue }
                if ($lines[$i] -match "^\s*-\s*(component|\d+):\s*\{fileID:\s*$($b.FileId)\}\s*$") {
                    [void]$drop.Add($i)
                    $hit = $true
                }
            }
            if (-not $hit) { $orphaned += "&$($b.FileId) (class $($b.Class))" }
        }

        # The pass-2 count proves there is exactly one reference; it does not prove that
        # reference is an m_Component entry. If it is an ordinary field instead, deleting the
        # block leaves a dangling pointer — and without this check the tool would print
        # "removed ... and its owner's list entry" and exit 0 while having done exactly that.
        if ($orphaned.Count -gt 0) {
            $filesSkipped++
            Write-Host "  SKIP $($file.FullName.Substring($repoRoot.Length + 1))" -ForegroundColor Yellow
            foreach ($o in $orphaned) { Write-Host "       -> $o : the one reference is not an m_Component entry" -ForegroundColor Yellow }
            Write-Host "       -> deleting the block would leave a dangling reference; nothing written" -ForegroundColor Yellow
            continue
        }

        $kept = for ($i = 0; $i -lt $lines.Count; $i++) { if (-not $drop.Contains($i)) { $lines[$i] } }

        $rel = $file.FullName.Substring($repoRoot.Length + 1)
        Write-Host "  $(if ($DryRun) { 'would fix' } else { 'fixed    ' }) $rel" -ForegroundColor Green
        foreach ($b in $blocks) {
            Write-Host "       -> removed class $($b.Class) &$($b.FileId) and its owner's list entry" -ForegroundColor DarkGray
        }

        $totalRemoved += $blocks.Count
        $filesChanged++

        if (-not $DryRun) {
            $text = $kept -join $eol

            # Splitting on the EOL turns a trailing newline into a trailing empty element, so
            # the join restores it — unless the deleted block ran to EOF, which consumes that
            # element. Unity writes a trailing newline; a file that loses one is a spurious
            # "\ No newline at end of file" in every future diff.
            if ($raw.EndsWith($eol) -and -not $text.EndsWith($eol)) { $text += $eol }

            [System.IO.File]::WriteAllText($file.FullName, $text)
        }
    }

    Write-Host ""
    if ($filesChanged -eq 0 -and $filesSkipped -eq 0) {
        Write-Host "Nothing to do — no removed-class components found." -ForegroundColor Green
        exit 0
    }

    $verb = if ($DryRun) { "would be removed from" } else { "removed from" }
    Write-Host "$totalRemoved component(s) $verb $filesChanged file(s)." -ForegroundColor Green
    if ($filesSkipped -gt 0) {
        Write-Host "$filesSkipped file(s) skipped — see the warnings above." -ForegroundColor Yellow
        exit 1
    }

    if ($DryRun) { Write-Host "Dry run: nothing was written. Re-run without -DryRun to apply." }
    exit 0
}
finally {
    Pop-Location
}
