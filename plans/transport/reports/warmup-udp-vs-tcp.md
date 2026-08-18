# Warm-up — UDP, TCP and MTU

- **Author:** the transport track
- **Date:** 2026-08-12
- **Environment:** Windows development host, .NET 8.0.423, loopback

## UDP echo

The raw `UdpPeer` integration test sends 10,000 numbered GSP datagrams to localhost and polls
until the receiver has accepted all 10,000. The result was 10,000/10,000, with no loss or parser
failure. This is a loopback baseline only; it is not evidence about an Internet path.

## TCP echo and induced loss

A TCP echo comparison was not run because this host has no approved `clumsy`/`tc netem` impairment
tool configured. I am not recording invented TCP loss or latency numbers. The architectural
conclusion remains the protocol decision documented in `plans/00-shared/architecture.md`: TCP's
ordered byte stream creates head-of-line blocking when a segment is lost, while this transport
uses independent UDP channels. The induced-loss measurement must be repeated on a machine with
`clumsy` or Linux `tc netem` before the capstone report is finalized.

## MTU observation

The transport rejects datagrams larger than `ProtocolConstants.MTU_SAFE` (1200 bytes) and the
protocol limits the payload to 1184 bytes. Oversized logical messages use the 4-byte fragment
header and are reassembled with a bounded eight-group/2-second policy. The 20 KB test verifies
the split/reassembly path byte-for-byte. A Wireshark fragmentation capture was not made on this
host; the safe-MTU choice is therefore verified by code and tests, not by a packet capture.

## Follow-up required

Run the TCP comparison and Wireshark capture on the integration/VPS machine, then append measured
rows to `reports/measurements.csv`. This is an environment-dependent experiment, not a code gap.
