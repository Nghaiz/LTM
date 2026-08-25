<#
.SYNOPSIS
    Turns one lane-A run into phase 4's two tables: the bandwidth decomposition and the
    server tick histogram.

.DESCRIPTION
    Reads a harness report (client side, per-connection transport bytes and per-opcode
    attribution) and the server's per-tick JSONL, and prints:

      1. Per-client bandwidth at the datagram level, which is the level a budget is spent at.
      2. That bandwidth decomposed by message type, which is what says WHICH feature spent it.
      3. Server tick p50 / p99 / max, with the sample size beside every percentile.
      4. Two independent cross-checks between the server's accounting and the clients'.

    Every number is printed beside the configuration that produced it. A percentile without
    its sample size, or a byte rate without its network conditions, is a number rather than a
    measurement — see plans/debt-closure/phases/phase-4-measure.md sections 2 and 3.

.PARAMETER Report
    Path to the harness report JSON (schema ironfront.loadharness/2 or later for the
    decomposition; /1 reports still yield the datagram-level and tick tables).

.PARAMETER Ticks
    Path to the server's per-tick JSONL. Defaults to the report path with
    "-report.json" replaced by "-ticks.jsonl".

.PARAMETER BudgetKbPerSec
    The per-client downstream budget to grade against. Defaults to 8, which is
    plans/replication/plan.md:303 and docs/report-chapter-state-synchronization.md:47.
    Phase 4's own text quotes 5, inherited from phase-v4-vehicle-server-authority.md:364.
    They are different numbers and the report says which it used.

.EXAMPLE
    pwsh tools/analyse-lane-a.ps1 -Report artifacts/lane-a/run-02-clean-report.json
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Report,
    [string]$Ticks,
    [double]$BudgetKbPerSec = 8.0
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Report)) { throw "Report not found: $Report" }
if (-not $Ticks) { $Ticks = $Report -replace '-report\.json$', '-ticks.jsonl' }

$r = Get-Content $Report -Raw | ConvertFrom-Json

function Format-Rate([double]$bytesPerSec) {
    '{0,8:N0} B/s ({1,6:N2} KB/s)' -f $bytesPerSec, ($bytesPerSec / 1024.0)
}

Write-Host ''
Write-Host '=== Configuration ===' -ForegroundColor Cyan
Write-Host ("  label      : {0}" -f $r.Label)
Write-Host ("  schema     : {0}" -f $r.Schema)
Write-Host ("  started    : {0}" -f $r.StartedUtc)
Write-Host ("  duration   : {0:N1} s" -f $r.ActualDurationSec)
Write-Host ("  clients    : {0} requested / {1} connected / {2} held to end" -f `
    $r.ClientsRequested, $r.ClientsConnected, $r.ClientsHeldToEnd)
Write-Host ("  wire       : preset={0} enabled={1} latency={2}ms jitter={3}ms loss={4}% reorder={5}%" -f `
    $r.Network.Preset, $r.Network.SimulatorEnabled, $r.Network.LatencyMs, `
    $r.Network.JitterMs, $r.Network.PacketLossPercent, $r.Network.ReorderPercent)
Write-Host ("  sim seed   : {0}" -f $r.Network.Seed)
Write-Host ("  budget     : {0} KB/s per client (override with -BudgetKbPerSec)" -f $BudgetKbPerSec)

# --- 1. Bandwidth at the datagram level -------------------------------------------------
# The transport counts whole datagrams, so this INCLUDES the GSP header, the reliability
# framing and the ack/heartbeat datagrams that carry no payload. That is the correct level
# for a budget: a link carries datagrams, not payload regions.

Write-Host ''
Write-Host '=== 1. Per-client bandwidth (datagram level) ===' -ForegroundColor Cyan
Write-Host ('  {0,3}  {1,4}  {2,5}  {3,26}  {4,10}  {5,8}' -f `
    'idx', 'conn', 'held', 'received', 'acks', 'rtt ms')

$rates = @()
foreach ($c in $r.Clients) {
    $rate = $c.ReceivedBytesPerSecond
    $rates += $rate
    Write-Host ('  {0,3}  {1,4}  {2,5}  {3}  {4,10:N0}  {5,8:N1}' -f `
        $c.Index, $c.ConnectionId, $c.HeldToEnd, (Format-Rate $rate), $c.AcksSent, $c.SmoothedRttMs)
}

if ($rates.Count -gt 0) {
    $mean = ($rates | Measure-Object -Average).Average
    $max = ($rates | Measure-Object -Maximum).Maximum
    Write-Host ''
    # The mean is printed beside the max, never instead of it. One client in a crowd and one
    # alone are the two numbers worth having, and their mean describes neither.
    Write-Host ('  mean : {0}' -f (Format-Rate $mean))
    Write-Host ('  max  : {0}' -f (Format-Rate $max))

    # A run that lost clients cannot be graded against a bandwidth budget. A disconnected
    # client stops receiving, so its bytes-per-second is divided by the FULL run duration and
    # comes out low -- the worse the run went, the healthier the number looks. On the typical
    # preset with 0 of 8 held this printed "WITHIN the 8 KB/s budget" over 0.49 KB/s, which is
    # a green that could only ever be produced by the failure it was hiding.
    if ($r.ClientsHeldToEnd -lt $r.ClientsConnected) {
        Write-Host ('  NOT GRADED — {0} of {1} client(s) did not hold to the end.' -f `
            ($r.ClientsConnected - $r.ClientsHeldToEnd), $r.ClientsConnected) -ForegroundColor Red
        Write-Host '  A dropped client stops receiving while the divisor keeps running, so these'
        Write-Host '  rates are biased DOWN by exactly the failure. Fix the drop, then measure.'
    }
    else {
        $verdict = if (($max / 1024.0) -le $BudgetKbPerSec) { 'WITHIN' } else { 'OVER' }
        $colour = if ($verdict -eq 'WITHIN') { 'Green' } else { 'Red' }
        Write-Host ('  {0} the {1} KB/s budget, graded on the WORST client, not the mean' -f `
            $verdict, $BudgetKbPerSec) -ForegroundColor $colour
    }
}

# --- 2. Decomposition by message type ---------------------------------------------------
# Rows 1, 3 and 4 of the phase's table are these shares. They are shares of THIS world, not
# predictions of a world built without the feature: interest management's budget is shared
# between the actor and vehicle streams in one datagram, so removing vehicles would also
# change how actors shed. "Total minus vehicles" is an upper bound on a no-vehicle run.

$haveWire = $r.Clients.Count -gt 0 -and $null -ne $r.Clients[0].PSObject.Properties['Wire']

Write-Host ''
Write-Host '=== 2. What spent it, by message type ===' -ForegroundColor Cyan

if (-not $haveWire) {
    Write-Host '  NOT AVAILABLE — this report predates schema ironfront.loadharness/2.' -ForegroundColor Yellow
    Write-Host '  Re-run the harness to get the decomposition; the tables above and below stand.'
}
else {
    $bad = @($r.Clients | Where-Object { -not $_.Wire.Reconciles })
    if ($bad.Count -gt 0) {
        # Refuse to print shares of a total that does not add up. A percentage computed over a
        # broken decomposition is the shape green-that-proves-nothing.md warns about.
        Write-Host ("  REFUSING to print shares: {0} client(s) failed reconciliation." -f $bad.Count) `
            -ForegroundColor Red
        foreach ($c in $bad) {
            Write-Host ("    client {0}: payload={1} frame={2} msgHdr={3} unaccounted={4}" -f `
                $c.Index, $c.Wire.PayloadBytes, $c.Wire.FrameHeaderBytes, `
                $c.Wire.MessageHeaderBytes, $c.Wire.UnaccountedBytes) -ForegroundColor Red
        }
    }
    else {
        $agg = @{}
        $totalDatagram = 0L; $totalPayload = 0L; $totalOverhead = 0L
        $totalFrameHdr = 0L; $totalUnacc = 0L; $totalInvalid = 0L
        foreach ($c in $r.Clients) {
            $totalDatagram += $c.Wire.DatagramBytes
            $totalPayload  += $c.Wire.PayloadBytes
            $totalOverhead += $c.Wire.TransportOverheadBytes
            $totalFrameHdr += $c.Wire.FrameHeaderBytes
            $totalUnacc    += $c.Wire.UnaccountedBytes
            $totalInvalid  += $c.Wire.InvalidPayloads
            foreach ($t in $c.Wire.Types) {
                if (-not $agg.ContainsKey($t.Name)) {
                    $agg[$t.Name] = [pscustomobject]@{ Name = $t.Name; Messages = 0L; WireBytes = 0L }
                }
                $agg[$t.Name].Messages  += $t.Messages
                $agg[$t.Name].WireBytes += $t.WireBytes
            }
        }

        $n = [double]$r.Clients.Count
        $secs = [double]$r.ActualDurationSec

        Write-Host ('  {0,-18}  {1,10}  {2,14}  {3,26}  {4,7}' -f `
            'message type', 'messages', 'wire bytes', 'per client', 'share')
        foreach ($row in ($agg.Values | Sort-Object WireBytes -Descending)) {
            $perClientRate = $row.WireBytes / $n / $secs
            $share = 100.0 * $row.WireBytes / [double]$totalDatagram
            Write-Host ('  {0,-18}  {1,10:N0}  {2,14:N0}  {3}  {4,6:N2}%' -f `
                $row.Name, $row.Messages, $row.WireBytes, (Format-Rate $perClientRate), $share)
        }

        Write-Host ''
        Write-Host ('  {0,-18}  {1,10}  {2,14:N0}  {3}  {4,6:N2}%' -f `
            'frame headers', '', $totalFrameHdr, (Format-Rate ($totalFrameHdr / $n / $secs)), `
            (100.0 * $totalFrameHdr / [double]$totalDatagram))
        Write-Host ('  {0,-18}  {1,10}  {2,14:N0}  {3}  {4,6:N2}%' -f `
            'transport overhead', '', $totalOverhead, (Format-Rate ($totalOverhead / $n / $secs)), `
            (100.0 * $totalOverhead / [double]$totalDatagram))
        if ($totalUnacc -gt 0 -or $totalInvalid -gt 0) {
            Write-Host ('  {0,-18}  {1,10:N0}  {2,14:N0}' -f `
                'unaccounted', $totalInvalid, $totalUnacc) -ForegroundColor Yellow
        }
        Write-Host ('  {0,-18}  {1,10}  {2,14:N0}  {3}' -f `
            'TOTAL (datagrams)', '', $totalDatagram, (Format-Rate ($totalDatagram / $n / $secs)))

        # The two rows phase 4 section 2 names explicitly.
        $vehicleBytes = 0L
        foreach ($k in @('VehicleSnapshot', 'VehicleSpawn', 'VehicleDespawn', 'SeatChange')) {
            if ($agg.ContainsKey($k)) { $vehicleBytes += $agg[$k].WireBytes }
        }
        $projectileBytes = 0L
        foreach ($k in @('ProjectileSpawn', 'WeaponFire', 'Explosion', 'HitConfirm')) {
            if ($agg.ContainsKey($k)) { $projectileBytes += $agg[$k].WireBytes }
        }

        Write-Host ''
        Write-Host '  --- the phase table rows, as shares of this run ---'
        Write-Host ('  vehicles (snapshot+spawn+despawn+seat) : {0}  [{1:N2}% of datagrams]' -f `
            (Format-Rate ($vehicleBytes / $n / $secs)), (100.0 * $vehicleBytes / [double]$totalDatagram))
        Write-Host ('  projectiles (spawn+fire+explosion+hit) : {0}  [{1:N2}% of datagrams]' -f `
            (Format-Rate ($projectileBytes / $n / $secs)), (100.0 * $projectileBytes / [double]$totalDatagram))
        Write-Host ('  everything else                        : {0}' -f `
            (Format-Rate (($totalDatagram - $vehicleBytes - $projectileBytes) / $n / $secs)))
        if ($projectileBytes -eq 0) {
            Write-Host '  NOTE: the projectile row is a measured ZERO, not an absence of measurement —' -ForegroundColor Yellow
            Write-Host '        HarnessBehavior has Idle and Move only, so no synthetic client fires.' -ForegroundColor Yellow
        }
    }
}

# --- 3. Server tick histogram -----------------------------------------------------------

Write-Host ''
Write-Host '=== 3. Server tick cost ===' -ForegroundColor Cyan

if (-not (Test-Path $Ticks)) {
    Write-Host ("  NOT AVAILABLE — no tick JSONL at {0}" -f $Ticks) -ForegroundColor Yellow
}
else {
    $steps = New-Object System.Collections.Generic.List[double]
    $loaded = New-Object System.Collections.Generic.List[double]   # ticks with players connected
    $truncated = 0
    $connBytes = @{}

    foreach ($line in [System.IO.File]::ReadLines((Resolve-Path $Ticks))) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        try { $t = $line | ConvertFrom-Json }
        catch {
            # The server is killed at the end of a run, so the final line is routinely a
            # partial write. Counted and reported rather than swallowed.
            $truncated++
            continue
        }
        $steps.Add([double]$t.stepMicros)
        if ($t.conns -gt 0) { $loaded.Add([double]$t.stepMicros) }
        foreach ($pc in $t.perConn) {
            $id = [int]$pc[0]
            if (-not $connBytes.ContainsKey($id)) { $connBytes[$id] = 0L }
            $connBytes[$id] += [long]$pc[1]
        }
    }

    function Show-Percentiles([string]$label, $values) {
        if ($values.Count -eq 0) { Write-Host ("  {0}: no samples" -f $label); return }
        $s = $values | Sort-Object
        function P([double]$q) {
            # Nearest-rank. With n in the thousands the interpolation choice is far below the
            # measurement's own noise, and nearest-rank always names a tick that happened.
            $i = [Math]::Ceiling($q * $s.Count) - 1
            if ($i -lt 0) { $i = 0 }
            if ($i -ge $s.Count) { $i = $s.Count - 1 }
            $s[$i]
        }
        $mean = ($values | Measure-Object -Average).Average
        Write-Host ("  {0}" -f $label)
        Write-Host ("    n = {0,-8}  p50 = {1,8:N0} us   p95 = {2,8:N0} us   p99 = {3,8:N0} us   max = {4,9:N0} us   mean = {5,8:N0} us" -f `
            $s.Count, (P 0.50), (P 0.95), (P 0.99), (P 1.00), $mean)
    }

    Show-Percentiles 'all ticks' $steps
    Show-Percentiles 'ticks with at least one connection (the loaded sample)' $loaded

    # 30 Hz sim rate: the tick budget is 33,333 us. Stated so the percentiles above have
    # something to be large or small AGAINST.
    $budgetUs = 1000000.0 / 30.0
    if ($loaded.Count -gt 0) {
        $over = @($loaded | Where-Object { $_ -gt $budgetUs }).Count
        Write-Host ("    budget = {0:N0} us (30 Hz); loaded ticks over budget: {1} of {2} ({3:N2}%)" -f `
            $budgetUs, $over, $loaded.Count, (100.0 * $over / $loaded.Count))
    }
    if ($truncated -gt 0) {
        Write-Host ("    {0} unparseable line(s) skipped — expected 1 at end of run (killed mid-write)" -f $truncated) `
            -ForegroundColor Yellow
    }

    # --- 4. Cross-checks ----------------------------------------------------------------

    Write-Host ''
    Write-Host '=== 4. Cross-check: what the server sent vs what the clients received ===' -ForegroundColor Cyan
    Write-Host '  A disagreement above 5% is a finding, not a rounding note (phase 4 section 2).'
    Write-Host '  On an impaired wire the client SHOULD receive less: dropped datagrams are'
    Write-Host '  counted sent by the server and never counted received. Read the sign.'
    Write-Host ''
    Write-Host ('  {0,4}  {1,14}  {2,14}  {3,10}  {4,8}' -f 'conn', 'server sent', 'client recvd', 'delta', 'delta %')

    $worst = 0.0
    foreach ($c in $r.Clients) {
        $id = [int]$c.ConnectionId
        if (-not $connBytes.ContainsKey($id)) {
            Write-Host ('  {0,4}  {1,14}  {2,14:N0}  {3,10}  {4,8}' -f `
                $id, 'not in JSONL', $c.BytesReceived, '-', '-') -ForegroundColor Yellow
            continue
        }
        $sent = $connBytes[$id]
        $recv = [long]$c.BytesReceived
        $delta = $recv - $sent
        $pct = if ($sent -eq 0) { 0.0 } else { 100.0 * $delta / [double]$sent }
        if ([Math]::Abs($pct) -gt [Math]::Abs($worst)) { $worst = $pct }
        $colour = if ([Math]::Abs($pct) -gt 5.0) { 'Yellow' } else { 'Gray' }
        Write-Host ('  {0,4}  {1,14:N0}  {2,14:N0}  {3,10:N0}  {4,7:N2}%' -f `
            $id, $sent, $recv, $delta, $pct) -ForegroundColor $colour
    }

    Write-Host ''
    $verdict = if ([Math]::Abs($worst) -le 5.0) { 'AGREES within 5%' } else { 'DISAGREES — investigate' }
    $colour = if ([Math]::Abs($worst) -le 5.0) { 'Green' } else { 'Red' }
    Write-Host ("  worst per-connection delta: {0:N2}% — {1}" -f $worst, $verdict) -ForegroundColor $colour
    Write-Host ('  NOTE: the two counters are not the same quantity, so a small delta of EITHER')
    Write-Host ('        sign is expected and the sign alone proves nothing. Two effects pull')
    Write-Host ('        opposite ways: per-connection framing makes the client''s datagram count')
    Write-Host ('        the larger, while datagrams still in flight at teardown make it the')
    Write-Host ('        smaller. Measured on three clean 8-client runs the residue landed at')
    Write-Host ('        -0.19%, -0.27% and +0.26% — same magnitude, both signs. What would be a')
    Write-Host ('        finding is a delta of several percent, and on an impaired wire a large')
    Write-Host ('        NEGATIVE one is loss.')

    # --- 5. Mean entry size vs the pessimistic projection -------------------------------
    # Phase 4 section 2 asks for "entriesSent x mean entry size" as the cross-check. With the
    # decomposition present we can do better than assume the mean: divide the snapshot bodies
    # actually received by the entries actually sent, and compare the answer to the number
    # InterestManager budgets with. The gap between them is exactly how much head-room the
    # 20 -> 23 step consumed, which is what row 2 of the table is really asking about.

    if ($haveWire -and @($r.Clients | Where-Object { -not $_.Wire.Reconciles }).Count -eq 0) {
        $snapBodies = 0L
        $snapMessages = 0L
        $entriesSent = 0L
        $shortSnaps = 0L
        foreach ($c in $r.Clients) {
            # Both halves of the quotient come off the SAME messages. The server's
            # entriesSent is InterestManager.EntriesRefreshed — a refresh-or-hold decision
            # counter, not a count of entries written into a body — and dividing received
            # bytes by it returned 38.8 B against a 23 B ceiling, which is impossible and is
            # how the mismatch was caught. The entry count now comes off each snapshot's own
            # ActorCount byte.
            $entriesSent += [long]$c.Wire.SnapshotEntries
            $shortSnaps  += [long]$c.Wire.ShortSnapshots
            foreach ($t in $c.Wire.Types) {
                if ($t.Name -eq 'Snapshot') {
                    $snapBodies += $t.BodyBytes
                    $snapMessages += $t.Messages
                }
            }
        }

        # SnapshotMessage.Size, the fixed per-snapshot header: tick, baseline tick, entry
        # count and the rest. It is NOT entry bytes, and dividing without subtracting it
        # reports the header as though actors were paying for it — on a near-static world
        # where each snapshot carries about one changed entry, that roughly doubles the
        # answer and turns a comfortable margin into a false "over budget".
        $snapshotHeaderSize = 13
        $entryBytes = $snapBodies - ($snapMessages * $snapshotHeaderSize)

        Write-Host ''
        Write-Host '=== 5. Mean actor entry size vs the budgeted worst case ===' -ForegroundColor Cyan
        if ($shortSnaps -gt 0) {
            Write-Host ('  REFUSING to divide: {0} snapshot body(ies) too short to hold a header.' -f $shortSnaps) `
                -ForegroundColor Red
        }
        elseif ($entriesSent -le 0) {
            Write-Host '  no entries carried in this run — nothing to divide' -ForegroundColor Yellow
        }
        elseif ($entryBytes -lt 0) {
            Write-Host ('  REFUSING to divide: header bytes ({0:N0}) exceed snapshot bodies ({1:N0}).' -f `
                ($snapMessages * $snapshotHeaderSize), $snapBodies) -ForegroundColor Red
        }
        else {
            $meanEntry = $entryBytes / [double]$entriesSent
            $headerShare = 100.0 * ($snapMessages * $snapshotHeaderSize) / [double]$snapBodies
            Write-Host ('  entries carried (ActorCount sum) : {0:N0}' -f $entriesSent)
            Write-Host ('  snapshot messages received       : {0:N0}' -f $snapMessages)
            Write-Host ('  snapshot bodies received         : {0:N0} B' -f $snapBodies)
            Write-Host ('    of which fixed 13 B headers    : {0:N0} B  ({1:N1}% of snapshot bytes)' -f `
                ($snapMessages * $snapshotHeaderSize), $headerShare)
            Write-Host ('    leaving entry bytes            : {0:N0} B' -f $entryBytes)
            Write-Host ('  entries per snapshot             : {0:N2}' -f ($entriesSent / [double]$snapMessages))
            Write-Host ('  mean bytes per entry             : {0:N2} B' -f $meanEntry)
            Write-Host ('  InterestManager.MaxEntrySize     : 23 B (SnapshotField.Full, seat included)')
            if ($meanEntry -gt 23.0) {
                Write-Host ('  OVER the budgeted worst case by {0:N2} B — the projection is not pessimistic' -f `
                    ($meanEntry - 23.0)) -ForegroundColor Red
            }
            else {
                Write-Host ('  {0:N1}% under the budgeted worst case — the projection stays pessimistic' -f `
                    (100.0 * (23.0 - $meanEntry) / 23.0)) -ForegroundColor Green
            }
            Write-Host ''
            Write-Host '  Both halves are read off the snapshots the clients actually received, so'
            Write-Host '  loss cannot bias this: a dropped snapshot removes its bytes AND its'
            Write-Host '  entries together. It is the mean of what ARRIVED, which is the right'
            Write-Host '  quantity for a bandwidth question and the wrong one for asking what the'
            Write-Host '  server built.'
        }
    }
}

Write-Host ''
