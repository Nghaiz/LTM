# tools/capture-lane-b-video.ps1 -- tile the three lane-B client windows and record the desktop.
#
# WHY THIS EXISTS, AND WHY IT IS NOT A HARNESS CHANGE.
# `plans/phases/phase-p4-lane-b-regrade.md` section 3.3: checks 8 and 9 are answered by a person
# watching, and "stills cannot show input lag at all -- capture video or a frame sequence dense
# enough to read convergence". The lane-B recorder captures ONE PNG per checkpoint, 20-30 s
# apart, which is a sampling rate that cannot exhibit a temporal property no matter how correct
# the frames are. That was half of why check 8 read UNGRADEABLE on 2026-08-28 (ledger B-8); the
# other half was X-48, now closed.
#
# The fix belongs OUTSIDE the game. Raising the recorder's capture rate would put a
# ScreenCapture.CaptureScreenshot PNG write into the frame loop every few frames -- and the
# stutter that introduces is indistinguishable from the input lag the check is trying to see.
# A screen recorder observes the frames the player actually presented and perturbs nothing in
# the build under test, so the artifact says something about the GAME rather than about the
# instrument. Nothing here is compiled into the player; the lane-B runner is untouched.
#
# WHY IT TILES THE WINDOWS FIRST. The runner launches three 960x540 windows and Unity centres
# every one of them, so all three land on top of each other and a desktop grab records only
# whichever is frontmost. Check 7 is "two clients see the same vehicle in the same PLACE" and
# check 8 is about convergence -- both are comparisons BETWEEN clients, so a video showing one
# client answers neither. Tiling is what makes the recording a comparison.
#
# Usage (run it in a second shell WHILE tools/run-lane-b.ps1 is running):
#   pwsh tools/capture-lane-b-video.ps1 -Seconds 120 -Output artifacts/lane-b/p4-vehicle-01/tiled.mp4

[CmdletBinding()]
param(
    # How long to record once all three windows are up. Size it to the programme's own length --
    # the programme total plus the join window, not less, or the recording stops mid-check.
    [int] $Seconds = 120,

    [string] $Output = "artifacts/lane-b/tiled.mp4",

    # How long to wait for three client windows to appear before giving up. The three clients
    # only open a window after the server reports ready, which the runner allows 120 s for.
    [int] $WaitSeconds = 180,

    # Frames per second in the recording. 15 is enough to read convergence and snapping without
    # writing a file too large to scrub; below ~10 a snap and a smooth blend start to look alike.
    [int] $Fps = 15,

    [string] $FfmpegPath = "ffmpeg"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$outPath  = if ([System.IO.Path]::IsPathRooted($Output)) { $Output } else { Join-Path $repoRoot $Output }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $outPath) | Out-Null

Add-Type -AssemblyName System.Windows.Forms

# SetWindowPos is the only way to move another process's window. MoveWindow would do as well;
# SetWindowPos is used because SWP_NOZORDER keeps the tiling from re-ordering the windows and
# hiding the one that was already frontmost.
if (-not ("Win32WindowPlacer" -as [type])) {
    Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32WindowPlacer {
    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    public const uint NOZORDER = 0x0004;
    public const uint NOACTIVATE = 0x0010;
    public const int RESTORE = 9;
}
"@
}

Write-Host "[capture] waiting for three Ironfront client windows (up to $WaitSeconds s)"

$deadline = (Get-Date).AddSeconds($WaitSeconds)
$windows = @()
while ((Get-Date) -lt $deadline) {
    # The headless server process is -nographics and owns no window, so filtering on a non-zero
    # MainWindowHandle separates the three clients from it without needing to know their order.
    $windows = @(Get-Process -Name "Ironfront" -ErrorAction SilentlyContinue |
                 Where-Object { $_.MainWindowHandle -ne 0 } |
                 Sort-Object StartTime)
    if ($windows.Count -ge 3) { break }
    Start-Sleep -Milliseconds 500
}

if ($windows.Count -lt 3) {
    throw "only $($windows.Count) client window(s) appeared within $WaitSeconds s. " +
          "Start tools/run-lane-b.ps1 first, then this script."
}

# The runner launches driver, observer-a, observer-b in that order, so StartTime orders the
# windows the same way. The label is recorded in the title bar overlay below rather than assumed
# by a reader of the video.
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$cellW  = [int]($screen.Width / 2)
$cellH  = [int]($screen.Height / 2)
$slots  = @(
    @{ Label = "driver";     X = 0;      Y = 0 }
    @{ Label = "observer-a"; X = $cellW; Y = 0 }
    @{ Label = "observer-b"; X = 0;      Y = $cellH }
)

for ($i = 0; $i -lt 3; $i++) {
    $h = $windows[$i].MainWindowHandle
    [void][Win32WindowPlacer]::ShowWindow($h, [Win32WindowPlacer]::RESTORE)
    [void][Win32WindowPlacer]::SetWindowPos(
        $h, [IntPtr]::Zero, $slots[$i].X, $slots[$i].Y, $cellW, $cellH,
        [Win32WindowPlacer]::NOZORDER -bor [Win32WindowPlacer]::NOACTIVATE)
    Write-Host ("[capture] {0,-11} pid {1} -> ({2},{3}) {4}x{5}" -f `
        $slots[$i].Label, $windows[$i].Id, $slots[$i].X, $slots[$i].Y, $cellW, $cellH)
}

Write-Host "[capture] recording $Seconds s at $Fps fps -> $outPath"

# gdigrab over the whole desktop rather than per-window: a Unity window renders through D3D, and
# a BitBlt against an occluded or resized D3D swap chain returns black often enough that a
# per-window grab is not something a verdict should rest on. The desktop grab records exactly
# what a person sitting here would see, which is what checks 8 and 9 ask for.
& $FfmpegPath -y -hide_banner -loglevel warning `
    -f gdigrab -framerate $Fps -draw_mouse 0 -t $Seconds -i desktop `
    -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p $outPath

if ($LASTEXITCODE -ne 0) { throw "ffmpeg exited $LASTEXITCODE" }

$size = [math]::Round((Get-Item $outPath).Length / 1MB, 1)
Write-Host "[capture] wrote $outPath ($size MB). Quadrants: top-left driver, top-right observer-a, bottom-left observer-b."
