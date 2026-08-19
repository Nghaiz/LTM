# tools/check-harness-no-decoder.ps1 — the harness drives the SHIPPED decoder and owns none.
#
# WHY THIS GATE EXISTS
#
# plans/debt-closure/phases/phase-3-harness.md acceptance criterion 4:
#
#     "The harness drives the shipped Transport and DeltaDecoder — a grep proves no second
#      decoder exists in Ironfront.Net.LoadHarness/."
#
# and § 4 says why: "a harness with its own decoder would grade the harness". The failure is
# not that a second decoder would be wrong. It is that it would be written by the same person,
# from the same reading of the same spec, on the same afternoon — so it would agree with the
# shipped one precisely when both were wrong together, and the harness would report a clean
# run over a protocol bug. A second implementation does not double-check the first; it doubles
# the chance of a shared mistake being invisible.
#
# The criterion says "a grep", so this is a grep. It is a gate rather than a line in a report
# because a promise in a report is not re-checked when somebody adds a file next month.
#
# WHAT IT CHECKS
#
#   1. FORBIDDEN — no source under the harness declares a decoder of its own, and none reaches
#      for the byte-level snapshot codecs. Reading a payload is the shipped router's job.
#   2. REQUIRED — the harness actually names ClientMessageRouter and calls .Route(. A project
#      that parses nothing because it decodes nothing at all would pass check 1 trivially, and
#      that is a hollow pass, not a clean one.
#   3. REQUIRED — the csproj references Ironfront.Net.Replication and Ironfront.Net.Transport,
#      so "shipped" means the project rather than a vendored copy.
#
# EXIT CODES
#   0  clean
#   1  a violation
#   2  could not tell — the project is missing or no sources were scanned. Deliberately NOT 0,
#      the same reservation tools/ClientWiringGate makes: an empty scan that exits 0 is a green
#      nobody earned.
#
# Usage:  pwsh tools/check-harness-no-decoder.ps1 [-ProjectPath Ironfront.Net.LoadHarness]

[CmdletBinding()]
param(
    [string]$ProjectPath = "Ironfront.Net.LoadHarness"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$root     = Join-Path $repoRoot $ProjectPath

Write-Host "=== harness carries no decoder of its own ==="

if (-not (Test-Path $root)) {
    Write-Host "COULD NOT TELL: '$ProjectPath' does not exist under $repoRoot."
    exit 2
}

# bin/ and obj/ hold generated and copied sources that the harness did not write.
$sources = Get-ChildItem -Path $root -Filter *.cs -Recurse -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' }

if ($sources.Count -eq 0) {
    Write-Host "COULD NOT TELL: no .cs files under '$ProjectPath' outside bin/ and obj/."
    exit 2
}

# Each rule is a pattern plus the reason it is forbidden, so a failure explains itself rather
# than printing a regex at somebody.
$forbidden = @(
    @{ Pattern = '\b(class|struct|record)\s+\w*Decoder\b'
       Reason  = 'declares its own decoder type' }
    @{ Pattern = '\bSnapshotMessage\s*\.'
       Reason  = 'reaches for the actor snapshot byte codec directly' }
    @{ Pattern = '\bVehicleSnapshotMessage\s*\.'
       Reason  = 'reaches for the vehicle snapshot byte codec directly' }
    @{ Pattern = '\b(DeltaDecoder|VehicleDeltaDecoder)\s*\.\s*ApplyEntry\b'
       Reason  = 'applies snapshot entries itself instead of letting the router do it' }
    @{ Pattern = '\bnew\s+(DeltaEncoder|VehicleDeltaEncoder)\b'
       Reason  = 'builds a server-side encoder, which no client-side harness needs' }
)

$violations = @()

foreach ($file in $sources) {
    $lines = Get-Content -Path $file.FullName
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        # Comments discuss the rule on purpose — this file's own subject matter appears in the
        # harness's XML docs, and flagging that would make the gate unfixable except by
        # deleting the explanation of why it exists.
        if ($line -match '^\s*(//|///|\*|/\*)') { continue }

        foreach ($rule in $forbidden) {
            if ($line -match $rule.Pattern) {
                $relative = $file.FullName.Substring($repoRoot.Length + 1)
                $violations += "{0}:{1}: {2}`n    {3}" -f `
                    $relative, ($i + 1), $rule.Reason, $line.Trim()
            }
        }
    }
}

# The positive half. Without it, deleting every decode path would read as a pass.
$allText = ($sources | ForEach-Object { Get-Content -Path $_.FullName -Raw }) -join "`n"

if ($allText -notmatch '\bClientMessageRouter\b') {
    $violations += "the harness never names ClientMessageRouter — it decodes nothing, so " +
                   "'no second decoder' is vacuous rather than clean."
}

if ($allText -notmatch '\.Route\s*\(') {
    $violations += "the harness never calls .Route( — payloads are not reaching the shipped router."
}

$csproj = Get-ChildItem -Path $root -Filter *.csproj -File | Select-Object -First 1
if ($null -eq $csproj) {
    Write-Host "COULD NOT TELL: no .csproj under '$ProjectPath'."
    exit 2
}

$csprojText = Get-Content -Path $csproj.FullName -Raw
foreach ($required in @("Ironfront.Net.Replication", "Ironfront.Net.Transport")) {
    if ($csprojText -notmatch [regex]::Escape($required)) {
        $violations += "$($csproj.Name) does not reference $required — 'the shipped decoder' " +
                       "has to mean the shipped project."
    }
}

if ($violations.Count -gt 0) {
    Write-Host ""
    # Covers both halves. The forbidden rules and the vacuous-pass guards fail for opposite
    # reasons — decoding too much, and decoding nothing — and a headline naming only the first
    # sends a reader looking for decode logic that is not there.
    Write-Host "FAIL: the harness does not provably drive the shipped decoder, and only it."
    foreach ($violation in $violations) { Write-Host "  $violation" }
    Write-Host ""
    Write-Host "A harness that decodes for itself grades itself. Route payloads through"
    Write-Host "ClientMessageRouter and read the result off DeltaDecoder.Current."
    exit 1
}

Write-Host ("PASS: {0} source file(s) scanned; no decoder declared, no byte codec called," -f $sources.Count)
Write-Host "      and the shipped ClientMessageRouter is on the receive path."
Write-Host "      This says the harness does not decode for itself. It does NOT say the"
Write-Host "      shipped decoder is correct — nothing here could tell you that."
exit 0
