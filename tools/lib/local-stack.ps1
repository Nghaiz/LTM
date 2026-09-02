# tools/lib/local-stack.ps1 -- the three things every script that stands up a LOCAL master +
# game server has to do, in one place.
#
# WHY IT IS A FILE. tools/run-e2e.ps1 wrote these first and tools/playtest-local.ps1 needs the
# same three: read the master's metrics socket, wait for a TCP port, and refuse to start when
# somebody else already owns the port. Copied, they drift -- and one of them is a GATE, where a
# pattern that quietly stops matching turns green for ever. That is the exact failure
# green-that-proves-nothing.md names, so the regex in particular lives here and nowhere else.
#
# Dot-source it:
#   . (Join-Path $PSScriptRoot "lib/local-stack.ps1")

# The master's metrics document, matched for a non-zero healthy game-server count. ONE
# definition: run-e2e.ps1 gates on it and playtest-local.ps1 waits on it, and a copy that
# silently stopped matching would make the first pass for ever and the second hang for ever.
$script:IronfrontHealthyPattern = '"gameServers"\s*:\s*\{[^}]*"healthy"\s*:\s*([1-9][0-9]*)'

# Reads the master's metrics endpoint. It is a RAW TCP socket that writes one JSON document and
# closes -- not HTTP -- which is why this is a socket read and not Invoke-RestMethod. Same shape
# tools/alert.sh reads with /dev/tcp.
function Read-Metrics {
    param([int] $Port)

    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $client.Connect("127.0.0.1", $Port)
        $reader = New-Object System.IO.StreamReader($client.GetStream())
        return $reader.ReadToEnd()
    }
    catch { return $null }
    finally { $client.Dispose() }
}

function Wait-ForTcpPort {
    param([int] $Port, [int] $Seconds, [string] $What, [string] $Tag = "local")

    $deadline = (Get-Date).AddSeconds($Seconds)
    while ((Get-Date) -lt $deadline) {
        $client = New-Object System.Net.Sockets.TcpClient
        try { $client.Connect("127.0.0.1", $Port); return $true }
        catch { Start-Sleep -Milliseconds 300 }
        finally { $client.Dispose() }
    }

    Write-Host "[$Tag] $What never opened port $Port within ${Seconds}s"
    return $false
}

# True when nothing is listening on the loopback port.
function Test-TcpPortFree {
    param([int] $Port)

    $probe = New-Object System.Net.Sockets.TcpClient
    try { $probe.Connect("127.0.0.1", $Port); return $false }
    catch { return $true }
    finally { $probe.Dispose() }
}

# A master or game server leaked by an earlier run answers on these ports, and whatever comes
# next then talks to a process this script neither started nor configured. That is not
# hypothetical: tools/alert-drill.sh misgraded itself twice this way, and a soak run once
# produced three verdicts about a process that had never started.
function Assert-TcpPortsFree {
    param([hashtable[]] $Ports, [string] $Tag = "local")

    foreach ($busy in $Ports) {
        if (Test-TcpPortFree -Port $busy.Port) { continue }

        Write-Host "[$Tag] REFUSING TO RUN: $($busy.What) is already listening on 127.0.0.1:$($busy.Port)."
        Write-Host "      Whatever ran next would be talking to a process this script did not start."
        Write-Host "      Usually a leak: Get-Process Ironfront.MasterServer,Ironfront | Stop-Process -Force"
        return $false
    }

    return $true
}
