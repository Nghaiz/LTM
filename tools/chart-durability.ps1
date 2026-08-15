<#
.SYNOPSIS
    Turns the durability CSV into the phase-03 criterion-9 chart and a leak verdict.

.DESCRIPTION
    IRONFRONT_METRICS_CSV writes one row per minute. Over 72 hours that is ~4,300 rows,
    which is unreadable as a table and obvious as a picture.

    The verdict this produces is deliberately conservative. Working set rising over a few
    hours is NOT proof of a leak — the GC has no reason to return memory it may need again,
    so a flat-ish sawtooth is healthy. What indicates a leak is a line that rises
    monotonically while the connection count stays flat, which is what the correlation below
    measures: memory going up while load does not.

.PARAMETER CsvPath
    The file IRONFRONT_METRICS_CSV wrote.

.PARAMETER OutputPath
    Self-contained HTML chart. No CDN — it has to open on a machine with no network.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $CsvPath,
    [string] $OutputPath = "./durability-chart.html"
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $CsvPath)) { throw "No CSV at $CsvPath" }

$rows = Import-Csv $CsvPath
if ($rows.Count -lt 2) { throw "Only $($rows.Count) row(s) — nothing to chart yet." }

$samples = $rows | ForEach-Object {
    [pscustomobject]@{
        Time        = [datetime]::Parse($_.tsUtc, [cultureinfo]::InvariantCulture)
        Rss         = [int] $_.workingSetMB
        Connections = [int] $_.connCurrent
        Errors      = [double] $_.errorsPerMin
        Uptime      = [long] $_.uptimeSec
    }
}

$first = $samples[0]
$last  = $samples[-1]
$hours = [math]::Round(($last.Time - $first.Time).TotalHours, 1)

$rssValues = $samples.Rss
$rssMin = ($rssValues | Measure-Object -Minimum).Minimum
$rssMax = ($rssValues | Measure-Object -Maximum).Maximum
$growth = $last.Rss - $first.Rss

# A restart resets uptimeSec. Counting them matters because Restart=always hides crash
# loops: the unit reports "active" the whole time.
$restarts = 0
for ($i = 1; $i -lt $samples.Count; $i++) {
    if ($samples[$i].Uptime -lt $samples[$i - 1].Uptime) { $restarts++ }
}

# Fraction of consecutive samples where RSS rose. Near 1.0 with flat load is the leak
# signature; a healthy process hovers near 0.5 as the GC gives memory back.
$rises = 0
for ($i = 1; $i -lt $samples.Count; $i++) {
    if ($samples[$i].Rss -gt $samples[$i - 1].Rss) { $rises++ }
}
$riseFraction = [math]::Round($rises / [math]::Max(1, $samples.Count - 1), 3)

$connectionSpread = ($samples.Connections | Measure-Object -Maximum).Maximum -
                    ($samples.Connections | Measure-Object -Minimum).Minimum

$verdict = if ($riseFraction -gt 0.85 -and $connectionSpread -le 2) {
    "LEAK SUSPECTED - working set rose in $([math]::Round($riseFraction * 100))% of intervals while load stayed flat"
} elseif ($growth -gt ($first.Rss * 0.5)) {
    "INVESTIGATE - working set grew ${growth}MB (over 50%); check whether load also grew"
} else {
    "NO LEAK SIGNAL - working set is bounded across $hours hours"
}

Write-Host ""
Write-Host "Durability summary" -ForegroundColor Cyan
Write-Host "  window       $($first.Time.ToString('u')) .. $($last.Time.ToString('u'))  ($hours h, $($samples.Count) samples)"
Write-Host "  working set  ${rssMin}..${rssMax} MB, net ${growth} MB"
Write-Host "  rising       $riseFraction of intervals"
Write-Host "  connections  spread of $connectionSpread"
Write-Host "  restarts     $restarts"
Write-Host "  verdict      $verdict" -ForegroundColor $(if ($verdict.StartsWith('NO LEAK')) { 'Green' } else { 'Yellow' })
Write-Host ""

# Inline SVG rather than a charting library: no CDN, no npm, opens anywhere, and diffs
# meaningfully in git if it is ever committed.
$width = 1100; $height = 320; $padding = 50
$plotWidth = $width - (2 * $padding); $plotHeight = $height - (2 * $padding)
$rssRange = [math]::Max(1, $rssMax - $rssMin)
$connMax = [math]::Max(1, ($samples.Connections | Measure-Object -Maximum).Maximum)

$rssPoints = @()
$connPoints = @()
for ($i = 0; $i -lt $samples.Count; $i++) {
    $x = $padding + ($plotWidth * $i / [math]::Max(1, $samples.Count - 1))
    $rssPoints += "{0:F1},{1:F1}" -f $x, ($padding + $plotHeight - ($plotHeight * ($samples[$i].Rss - $rssMin) / $rssRange))
    $connPoints += "{0:F1},{1:F1}" -f $x, ($padding + $plotHeight - ($plotHeight * $samples[$i].Connections / $connMax))
}

$html = @"
<!doctype html>
<meta charset="utf-8">
<title>Ironfront durability - $hours h</title>
<style>
  body { font: 14px/1.5 system-ui, sans-serif; margin: 2rem; color: #1a1a1a; background: #fff; }
  h1 { font-size: 1.25rem; margin-bottom: 0.25rem; }
  .verdict { padding: .6rem .9rem; border-radius: 6px; display: inline-block; margin: .5rem 0 1rem; }
  .ok { background: #e7f6ec; color: #15633a; }
  .warn { background: #fdf3e0; color: #7a4b09; }
  table { border-collapse: collapse; margin-top: 1rem; }
  td, th { padding: .3rem .8rem .3rem 0; text-align: left; }
  .legend span { display: inline-block; margin-right: 1.2rem; }
  .swatch { display: inline-block; width: 12px; height: 3px; vertical-align: middle; margin-right: .35rem; }
</style>
<h1>Ironfront master server - durability</h1>
<div class="verdict $(if ($verdict.StartsWith('NO LEAK')) { 'ok' } else { 'warn' })">$verdict</div>
<div class="legend">
  <span><i class="swatch" style="background:#c0392b"></i>working set (MB)</span>
  <span><i class="swatch" style="background:#2b6cb0"></i>connections</span>
</div>
<svg width="$width" height="$height" role="img">
  <rect x="$padding" y="$padding" width="$plotWidth" height="$plotHeight" fill="#fafafa" stroke="#ddd"/>
  <polyline points="$($rssPoints -join ' ')" fill="none" stroke="#c0392b" stroke-width="1.5"/>
  <polyline points="$($connPoints -join ' ')" fill="none" stroke="#2b6cb0" stroke-width="1.5"/>
  <text x="$padding" y="$($padding - 12)" font-size="12" fill="#666">$rssMax MB / $connMax connections</text>
  <text x="$padding" y="$($height - 18)" font-size="12" fill="#666">$($first.Time.ToString('u'))</text>
  <text x="$($width - $padding - 150)" y="$($height - 18)" font-size="12" fill="#666">$($last.Time.ToString('u'))</text>
</svg>
<table>
  <tr><th>Window</th><td>$hours hours, $($samples.Count) samples</td></tr>
  <tr><th>Working set</th><td>$rssMin - $rssMax MB (net $growth MB)</td></tr>
  <tr><th>Rising intervals</th><td>$riseFraction</td></tr>
  <tr><th>Connection spread</th><td>$connectionSpread</td></tr>
  <tr><th>Restarts observed</th><td>$restarts</td></tr>
</table>
"@

Set-Content -Path $OutputPath -Value $html -Encoding UTF8
Write-Host "Chart written to $OutputPath" -ForegroundColor Green
