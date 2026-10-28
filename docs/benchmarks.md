# Benchmarks: MiniHttpHost and WCF

This page records a Windows comparison of MiniHttpHost against a self-hosted WCF
`WebHttpBinding` service. Both run on HTTP.sys through `System.Net.HttpListener`, so the
comparison measures the layers above the listener: MiniHttpHost's router and streaming
helpers against WCF's dispatcher and streamed transfer mode.

The benchmark is in `tests/Benchmarks`. It builds for net472 only, because WCF
self-hosting is Framework-only in this repository.

## Environment

| Item | Value |
| --- | --- |
| OS | Windows 11 Enterprise 10.0.26200. The unmanifested benchmark process reports `6.2.9200.0`. |
| CPU | Intel family 6, model 170, 22 logical processors |
| Runtime | .NET Framework, CLR `4.0.30319.42000`, net472 build |
| Date | 2026-10-28 |
| Commit | `7572d0b` benchmark policy; library default `4096` from `4ec526d` |

## Method

- **Servers.** MiniHttpHost listens on `localhost:8081` with `MiniWebServer` at concurrency
  64. WCF listens on `localhost:8082` with a `WebServiceHost`, streamed transfer, and
  `ServiceThrottlingBehavior` at 64 calls, sessions, and instances.
- **Operations.** `Echo` posts a body and reads it back. `Download` requests a payload of
  the given size. Both use raw bodies, not SOAP.
- **Buffers.** MiniHttpHost echo uses a 4 KiB copy buffer up to 16 KiB and 80 KiB above that.
  Downloads use 4 KiB. See the [copy buffer note](wcf-coexistence.md) for the trade-off.
- **Verification.** Every response is checked against the expected byte count before its
  time counts.
- **Latency.** Sequential requests after a warmup. p50 and p95 are reported.
- **Throughput.** 32 concurrent clients for 5 seconds per cell, reporting requests per
  second and payload MB/s.
- **Rounds.** Three rounds, with server order alternating each round. The table shows the
  median of each metric.

## Results

Median of three rounds, from `Benchmarks.exe --rounds 3 --seconds 5`.

### Echo

Each body is posted and read back. Ratio is MiniHttpHost requests per second divided by WCF's.

| Body | MiniHttpHost req/s | WCF req/s | Ratio | MiniHttpHost p50 | WCF p50 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 KiB | 13,272 | 10,472 | 1.3× | 0.27 ms | 0.34 ms |
| 1 MiB | 1,221 | 762 | 1.6× | 19.2 ms | 65.1 ms |
| 16 MiB | 79.2 | 57.7 | 1.4× | 69.4 ms | 95.5 ms |

### Download

Each response is a payload of the given size.

| Body | MiniHttpHost req/s | WCF req/s | Ratio | MiniHttpHost p50 | WCF p50 |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 KiB | 26,305 | 11,254 | 2.3× | 0.19 ms | 0.31 ms |
| 1 MiB | 2,389 | 790 | 3.0× | 3.18 ms | 5.56 ms |
| 16 MiB | 139.9 | 63.8 | 2.2× | 55.1 ms | 76.7 ms |

p95 latency and MB/s are left out to keep the tables readable. MB/s is req/s times the
body size. p95 varies most between rounds, so the p95 discussion under Limitations
applies.

## Reading the results

- MiniHttpHost has the higher median throughput at every size on this machine. The gap is
  widest for downloads at 1 MiB, where it is about 3x.
- The 1 KiB echo gap was investigated earlier. An 80 KiB copy buffer halved throughput at that
  size, which is why small echoes use 4 KiB.
- p95 values are noisy. At 1 MiB echo, one WCF round was far above the others, so the
  p95 comparison is weaker than the throughput comparison.

## Limitations

- **One machine, one process.** The client, both servers, and the timing share a 22-core
  machine and one process. CPU contention is shared, so these numbers show loopback
  behavior, not server-only capacity.
- **Short cells.** Five seconds per cell over three rounds gives noisy p95 values. Treat
  differences under roughly 30% as not significant.
- **Policy, not default.** MiniHttpHost's echo numbers use the size-based policy in the
  benchmark. A caller using the plain `StreamAsync` default gets 4 KiB for every size.
- **Concurrency.** Only 32 concurrent clients were tested. Typical production load may sit
  elsewhere.

## Reproduce

```sh
dotnet build tests/Benchmarks/Benchmarks.csproj -c Release -f net472
tests/Benchmarks/bin/Release/net472/Benchmarks.exe --rounds 3 --seconds 5
```

Options: `--rounds`, `--seconds`, `--concurrency`, and `--sizes 1024,1048576` to choose
payload sizes. Port 8081 and 8082 must be free. The benchmark registers the exact
`localhost` prefix for WCF, so it doesn't need elevation.
