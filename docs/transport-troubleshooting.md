# Transport troubleshooting

Use a capture as evidence before changing gameplay or replication code. Enable
`IRONFRONT_PCAP` for the affected process, reproduce once, stop the process cleanly, then run
`Ironfront.Tools.PacketReplay --analyze` against the resulting file.

| Symptom | Likely cause | Verify | Safe action |
|---|---|---|---|
| Server stops receiving during a flood | Receive budget is exhausted or the OS socket buffer is clamped | Inspect `PollBudgetExhausted` and `ReceiveBufferWasClampedByTheOs` | Poll every frame; raise the OS buffer on the host; do not make the drain unbounded |
| Client drops after idle time | NAT mapping or unreachable peer | Check keep-alives in the capture and `TIMEOUT_MS` | Keep the one-second keep-alive; verify on the VPS for five minutes |
| Client drops after source-port change | NAT rebinding is not in the frozen wire contract | Compare endpoint address/port before and after the gap | Treat as a protocol-owner change; do not invent an unauthenticated rebind token |
| Reliable messages stop arriving | Packet was abandoned or a reliability slot collided | Look for `TransportError`, `HasAbandonedReliable`, retransmits and ACK gaps | Reconnect and preserve the capture; investigate the first loss burst |
| RTT is zero or unexpectedly high | No untouched ACK sample, or a retransmit was sampled incorrectly | Compare `SmoothedRttMs`, `PacketsResent` and replay RTT samples | Apply Karn's rule; do not use a retransmitted packet as an RTT sample |
| Bandwidth/RTT climbs together | Loss burst, retransmit storm or congestion mode BAD | Compare `PacketLossPercentSent`, `CongestionMode` and replay duplicate sequences | Reduce snapshot/detail rate through the congestion signal |
| Messages contain garbage | Callback retained pooled memory | Enable Debug `0xDD` fill and inspect the handler | Copy `ReadOnlyMemory<byte>` before returning from `OnMessage` |
| Rented buffer count rises | A path failed to return pooled storage | Watch `TransportStats.BufferPoolRented` over time | Capture the first monotonic increase and audit `finally` ownership paths |
| Handshake is denied | Ticket validator, expiry, player binding or protocol version | Inspect CONNECT_DENIED and the server counters | Verify the shared secret/ticket policy; never log ticket bytes |
| Reconnect from the same endpoint is denied | Old connection is still alive | Check `DisconnectReason.AlreadyConnected` | Wait for/close the old session; do not silently replace it |

## Security rules

Capture files are sensitive because they can contain join tickets and application payloads. Store
them under ignored `artifacts/`, restrict access, and delete them after the incident. The transport
does not provide payload encryption; a public deployment needs an authenticated encrypted outer
layer before treating captures or network traffic as confidential.

Do not respond to malformed or unknown datagrams. Do not remove the receive budget, rate limiter,
cookie validation, fragment cap or connection-id checks to “make testing easier”. Those controls
are part of the production behavior being diagnosed.
