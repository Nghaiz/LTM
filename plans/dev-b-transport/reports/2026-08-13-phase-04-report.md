# Report — Phase 04: Report and defense

- **Status:** Draft complete for repository evidence; external experiment data and rehearsal pending

## Repository deliverables

- Draft chapter: [`docs/transport-layer-report.md`](../../../docs/transport-layer-report.md)
- Usage guide: [`Ironfront.Net.Transport/README.md`](../../../Ironfront.Net.Transport/README.md)
- Troubleshooting guide: [`docs/transport-troubleshooting.md`](../../../docs/transport-troubleshooting.md)
- Offline evidence tool: `Ironfront.Tools.PacketReplay`

## Experiment audit

| Experiment | Current evidence | Status |
|---|---|---|
| UDP vs TCP under 0/5/15/30% loss | Local UDP/simulator evidence; no approved TCP impairment run | Pending external run |
| ACK bitfield on/off | ACK implementation/tests; no comparative run | Pending experiment harness/data |
| Head-of-line blocking | Channel semantics/tests; no p99 comparison table | Pending experiment harness/data |
| Congestion on/off at 20% loss | Hysteresis tests and local benchmark; no chart | Pending experiment harness/data |
| BufferPool vs ArrayPool | Reproducible 1M-operation runner now covers `new byte[]`, `BufferPool`, `ArrayPool.Shared` and `ArrayPool.Create` | Local run complete; not a cross-machine claim |
| 1-to-64 scalability | Local 16/64 connection benchmark recorded | Partial; final chart pending |

## Acceptance audit

The chapter, API documentation and troubleshooting guide are committed. The six complete tables,
charts, live 3-minute demo, Unity F3 screenshot and eight-hour soak cannot be honestly marked
complete without the VPS, Unity and team integration runs. No numbers are invented to fill those
cells.

## Local benchmark evidence

Command:

```powershell
dotnet run --project Ironfront.Net.Transport.Bench -c Release --no-build -- --seconds 1 --connections 16
```

The run was performed on Windows with .NET 8 Release binaries. The pool comparison executes
1,000,000 rent/release operations against a 1200-byte target:

| Implementation | ns/op | Alloc/op | Gen0 collections |
|---|---:|---:|---:|
| `new byte[1200]` | 17.5 | 1224.00 B | 146 |
| Hand-written `BufferPool` | 10.2 | 0.00 B | 0 |
| `ArrayPool<byte>.Shared` | 18.8 | 0.00 B | 0 |
| `ArrayPool<byte>.Create(1200,256)` | 18.4 | 0.00 B | 0 |

These are local benchmark observations, not universal performance guarantees. The benchmark
also completed the local 16-connection load window; the VPS 10-minute scaling, TCP comparison
and Unity F3 measurements remain external acceptance work.

The same smoke benchmark completed 64 local connections in one run (`conns=64`, `messages=1984`,
`cpu=7.76% of one core`). This is a reproducibility check, not the required 10-minute VPS
capacity result or a completed scalability chart.

## Test evidence

The full Release solution run passed 605 tests: Protocol 198, Replication 284, Transport 81 and
MasterServer 42. The repository therefore exceeds the 60-test floor; the mandatory eight-hour
soak remains an external run.
