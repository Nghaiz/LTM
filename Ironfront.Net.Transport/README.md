# Ironfront.Net.Transport

This assembly is the hand-written UDP transport used by the Unity client and game server. It
targets `netstandard2.1` for Unity and `net8.0` for the allocation-free socket path used by the
headless server and diagnostics tools.

## Runtime contract

Call `Poll()` once per frame from the owning thread. `UdpTransportClient`, `UdpTransportServer`,
`Connection`, `BufferPool` and the dictionaries behind the server are deliberately single-threaded.
Do not call transport methods concurrently from a networking thread.

```csharp
using var server = new UdpTransportServer { MapId = 1 };
server.OnValidateTicket += ticket => JoinTicket.Verify(ticket, sharedSecret) == TicketVerifyResult.Valid;
server.Start(27015, ProtocolConstants.MAX_PLAYERS);

while (running)
{
    server.Poll();
    // Application simulation and server.Send/Broadcast happen on this same thread.
}
```

`OnMessage` receives callback-scoped memory backed by the transport pool. Copy the payload if it
must survive the callback. The server-side ticket validator is fail-closed: no validator, or any
validator returning `false`, rejects the connection.

`TransportStats` exposes cumulative counters plus one-second byte-rate values. The two loss
percentages are five-second estimates: sent loss counts distinct reliable packets that needed a
retry, while received loss counts observed sequence gaps and can count reordering as loss.

The ACK bitfield is enabled by default. `UdpTransportClient.AckBitfieldEnabled` and
`UdpTransportServer.AckBitfieldEnabled` may be set before connecting/starting only for the Phase 4
comparison run; production configuration must leave them enabled.

## Capture and replay

Set `IRONFRONT_PCAP` before starting the client or server:

```powershell
$env:IRONFRONT_PCAP = 'artifacts/session.ifpcap'
dotnet run --project Ironfront.MasterServer -c Release
```

The file contains raw datagrams, direction, monotonic timestamp, IPv4 endpoint and length. Capture
files can contain join tickets and gameplay data; keep them private and never commit them.

Replay a capture offline:

```powershell
dotnet run --project Ironfront.Tools.PacketReplay -c Release -- artifacts/session.ifpcap
dotnet run --project Ironfront.Tools.PacketReplay -c Release -- artifacts/session.ifpcap --analyze
dotnet run --project Ironfront.Tools.PacketReplay -c Release -- artifacts/session.ifpcap --filter conn=3 --from 12000 --to 15000
```

The benchmark can run the ACK comparison with the history enabled or disabled:

```powershell
dotnet run --project Ironfront.Net.Transport.Bench -c Release -- --connections 16 --seconds 10 --ack-bitfield on
dotnet run --project Ironfront.Net.Transport.Bench -c Release -- --connections 16 --seconds 10 --ack-bitfield off
dotnet run --project Ironfront.Net.Transport.Bench -c Release -- --connections 16 --seconds 28800 --idle --report artifacts/transport-soak.csv
```

`--report` writes one CSV row per minute plus a final row. `--idle` stops application payloads
after the handshake while the transport continues polling keep-alives, which is the run mode for
the five-minute NAT-idle check. Keep the generated CSV under ignored `artifacts/` and attach it to
the integration report rather than committing it.

The analyzer reports packet counts, invalid records, estimated sequence gaps, duplicate outgoing
sequences, and RTT samples correlated through the GSP ACK plus bitfield. Congestion mode changes
are inferred from those RTT samples; they are diagnostic estimates, not authoritative wire data.

## Unity diagnostics overlay

`Ironfront_Reborn/Assets/Scripts/Net/Diagnostics/TransportDebugOverlay.cs` is the optional Unity
IMGUI surface for the same `TransportStats` fields used by headless tools. Add the component to a
debug object and bind the live client after that client is created:

```csharp
overlay.Bind(transportClient);
```

The default toggle is `Shift+F3`; bare `F3` remains available to the legacy vehicle-seat
binding. The component does not create sockets or synthesize values while unbound. A Unity Editor
compile and screenshot are still external integration evidence, not claimed by this source-level
commit.

The local Phase 4 behaviour report can be generated with:

```powershell
dotnet run --project Ironfront.Net.Transport.Bench -c Release -- --seconds 1 --connections 1 --idle `
  --phase4-report plans/dev-b-transport/reports/2026-08-14-phase-04-local-experiments.csv
```

That CSV covers ACK history, per-channel head-of-line behaviour and congestion hysteresis. It is
deterministic local evidence; it does not replace packet-loss, VPS/NAT or long-soak measurements.

## Operational defaults

- Safe datagram MTU: 1200 bytes; IP fragmentation is never delegated to the network.
- `UdpPeer.MaxPacketsPerPoll`: 1024 datagrams, preventing a receive flood from starving the game tick.
- Windows UDP ICMP reset is disabled with `SIO_UDP_CONNRESET`.
- Fragment reassembly is capped at eight groups per connection and expires after two seconds.
- Reliable retransmission exhaustion disconnects with `DisconnectReason.TransportError`; it never
  leaves a reliable-ordered channel silently stalled.

See [`docs/transport-troubleshooting.md`](../docs/transport-troubleshooting.md) for incident
triage and [`plans/dev-b-transport/phases/phase-03-operations.md`](../plans/dev-b-transport/phases/phase-03-operations.md)
for the VPS runbook.
