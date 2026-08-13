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
percentages are estimates: sent loss is based on reliable retransmission timeouts, while received
loss is based on observed sequence gaps and can count reordering as loss.

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

The analyzer reports packet counts, invalid records, estimated sequence gaps, duplicate outgoing
sequences, and RTT samples correlated through the GSP ACK plus bitfield. Congestion mode is not
claimed from a capture because it is a local control state and is not encoded on the wire.

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
