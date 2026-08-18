# tools/new-env.ps1 — creates the .env this checkout needs.
# (plans/00-shared/conventions.md section 7).
#
# .env is gitignored and always will be: it carries IRONFRONT_SHARED_SECRET, and a key that
# reached a git history is not a key any more. So every clone starts without one, and the
# master server refuses to start until it has one. This is the one-time step that closes
# that gap, and it exists because four people were each going to do it by hand.
#
# Usage:
#   pwsh tools/new-env.ps1                  # fresh secret, for a local master + game server
#   pwsh tools/new-env.ps1 -Secret '<key>'  # the key a teammate sent you, to share a server
#   pwsh tools/new-env.ps1 -Force           # overwrite an existing .env
#
# WHEN YOU NEED THE SAME SECRET AS SOMEONE ELSE: the master signs joinTickets with it and the
# game server verifies them with it, so the two PROCESSES must agree. Running both yourself
# means any secret will do — generate your own and never send it anywhere. Connecting to a
# master somebody else is running means you need theirs, and it has to travel out of band: a
# password manager or a direct message, never a commit, a PR, an issue or a screenshot.

[CmdletBinding()]
param(
    # Use a specific key instead of generating one. For joining somebody else's server.
    [string]$Secret = "",

    # Replace an existing .env. Off by default: silently overwriting the key that a running
    # pair of processes agreed on breaks both of them, and the error they report is
    # "CONNECT_DENIED reason 3", which does not sound like this file.
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$template = Join-Path $repoRoot ".env.example"
$target   = Join-Path $repoRoot ".env"

if (-not (Test-Path $template)) {
    throw ".env.example not found at $template — is this a full checkout?"
}

if ((Test-Path $target) -and -not $Force) {
    Write-Host ".env already exists at $target"
    Write-Host "Nothing was changed. Re-run with -Force to replace it (this invalidates the current key)."
    exit 0
}

if ([string]::IsNullOrWhiteSpace($Secret)) {
    # RandomNumberGenerator, not Get-Random. Get-Random is a general-purpose PRNG seeded from
    # the clock -- fine for picking a test case, not for an HMAC key, where predictable output
    # means forgeable joinTickets. 32 bytes is the SHA-256 block size and base64-encodes to the
    # 44 characters the >= 32 check wants.
    $bytes = [System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
    $Secret = [Convert]::ToBase64String($bytes)
    $origin = "generated"
}
else {
    $Secret = $Secret.Trim()
    if ($Secret.Length -lt 32) {
        throw "The supplied secret is $($Secret.Length) characters; the master server requires at least 32."
    }
    $origin = "supplied"
}

# Read and rewrite rather than copy-then-patch: a half-written .env that already has the
# variable names but not the key looks configured and is not.
$lines = Get-Content $template
$patched = $lines | ForEach-Object {
    if ($_ -match '^IRONFRONT_SHARED_SECRET=') { "IRONFRONT_SHARED_SECRET=$Secret" } else { $_ }
}

if (-not ($patched | Where-Object { $_ -match '^IRONFRONT_SHARED_SECRET=.+' })) {
    throw "IRONFRONT_SHARED_SECRET was not found in .env.example — the template is malformed."
}

# LF, matching what the generator writes and what .gitattributes normalises .env.example to.
$text = ($patched -join "`n") + "`n"
[System.IO.File]::WriteAllText($target, $text, (New-Object System.Text.UTF8Encoding($false)))

# The key itself is deliberately not printed. A terminal is scrollback, and scrollback ends up
# in screenshots and in pasted "here is my output" messages.
Write-Host "Wrote $target with a $origin shared secret."
Write-Host "Everything else is the template's defaults, so nothing else changed behaviour."
Write-Host ""
Write-Host "It is gitignored and must stay that way. To share it with a teammate who needs to"
Write-Host "reach YOUR master server, send the key out of band -- read it with:"
Write-Host "    (Select-String -Path .env -Pattern '^IRONFRONT_SHARED_SECRET=').Line"
