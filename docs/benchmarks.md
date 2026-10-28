# Benchmarks: MiniHttpHost, WCF, and ASP.NET Core

This page records a Windows comparison of MiniHttpHost against a self-hosted WCF
`WebHttpBinding` service and an ASP.NET Core minimal API. All three run on HTTP.sys, so the
comparison measures the layers above the listener: MiniHttpHost's router and streaming
helpers, WCF's dispatcher and streamed transfer mode, and ASP.NET Core's routing and
response pipeline.

The benchmark is in `tests/Benchmarks`. It builds for net472 only, because WCF
self-hosting is Framework-only in this repository. The ASP.NET Core server is in
`tests/BenchmarkServer`, a self-contained net10.0 project, and runs in its own process.

## Environment

| Item | Value |
| --- | --- |
| OS | Windows 11 Enterprise 10.0.26200. The unmanifested benchmark process reports `6.2.9200.0`. |
| CPU | Intel family 6, model 170, 22 logical processors |
| Runtime | .NET Framework, CLR `4.0.30319.42000`, net472 build (client, MiniHttpHost, WCF). ASP.NET Core: self-contained .NET `10.0.3`, win-x64, Server GC |
| Date | 2026-10-28 |
| Library commit | `f34bec3` |

## Method

- **Servers.** MiniHttpHost listens on `localhost:8081` with `MiniWebServer` at concurrency
  64. WCF listens on `localhost:8082` with a `WebServiceHost`, streamed transfer, and
  `ServiceThrottlingBehavior` at 64 calls, sessions, and instances. ASP.NET Core listens on
  `localhost:8083` with `UseHttpSys`, a 1 GiB request body limit, and console logging
  cleared, since the other servers don't log per request. Its routes mirror MiniHttpHost's.
- **Process layout.** MiniHttpHost and WCF run in the client's process. ASP.NET Core runs in
  a separate process, started by hand before the client. See Limitations.
- **Operations.** `Echo` posts a body and reads it back. `Download` requests a payload of
  the given size. Both use raw bodies, not SOAP.
- **Buffers.** MiniHttpHost echo uses a 4 KiB copy buffer up to 16 KiB and 80 KiB above that.
  Downloads use 4 KiB. ASP.NET Core uses the same echo and download buffer sizes.
  See the [copy buffer note](wcf-coexistence.md) for the trade-off.
- **Verification.** Every response is checked against the expected byte count before its
  time counts.
- **Latency.** Sequential requests after a warmup. p50 and p95 are reported.
- **Throughput.** 32 concurrent clients for 5 seconds per cell, reporting requests per
  second and payload MB/s.
- **Rounds.** Three rounds, with the starting server rotating each round, so each server
  runs first once. The table shows the median of each metric.

## Results

Median of three rounds, from `Benchmarks.exe --rounds 3 --seconds 5 --aspnet`.

The "vs" columns show how much higher MiniHttpHost's req/s is than the other server's, as a
percentage of that server's req/s. A negative value means MiniHttpHost is lower.

### Echo

Each body is posted and read back.

| Body | MiniHttpHost req/s | WCF req/s | ASP.NET Core req/s | vs WCF | vs ASP.NET Core |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 KiB | 15,236.5 | 10,939.7 | 18,175.9 | +39% | -16% |
| 1 MiB | 1,346.4 | 755.2 | 790.3 | +78% | +70% |
| 16 MiB | 94.1 | 60.2 | 56.2 | +56% | +67% |

| Body | MiniHttpHost p50 | WCF p50 | ASP.NET Core p50 |
| ---: | ---: | ---: | ---: |
| 1 KiB | 0.27 ms | 0.26 ms | 0.21 ms |
| 1 MiB | 15.77 ms | 76.58 ms | 4.79 ms |
| 16 MiB | 61.32 ms | 77.43 ms | 398.09 ms |

### Download

Each response is a payload of the given size.

| Body | MiniHttpHost req/s | WCF req/s | ASP.NET Core req/s | vs WCF | vs ASP.NET Core |
| ---: | ---: | ---: | ---: | ---: | ---: |
| 1 KiB | 27,482.0 | 11,950.1 | 20,309.3 | +130% | +35% |
| 1 MiB | 2,357.9 | 781.5 | 2,370.4 | +202% | -1% |
| 16 MiB | 147.0 | 64.0 | 155.2 | +130% | -5% |

| Body | MiniHttpHost p50 | WCF p50 | ASP.NET Core p50 |
| ---: | ---: | ---: | ---: |
| 1 KiB | 0.12 ms | 0.20 ms | 0.12 ms |
| 1 MiB | 2.46 ms | 4.53 ms | 3.30 ms |
| 16 MiB | 44.62 ms | 83.58 ms | 50.99 ms |

p95 latency and MB/s are left out to keep the tables readable. MB/s is req/s times the
body size. p95 varies most between rounds. For example, MiniHttpHost's 1 MiB echo p50 ranged
from 3.82 ms to 42.93 ms across the three rounds, so the p50 comparisons at that size are
weak too. The p95 discussion under Limitations applies.

## Reading the results

- Against WCF, MiniHttpHost has the higher median throughput at every size and operation on
  this machine. The gap is widest for downloads at 1 MiB, where it is about 3x.
- Against ASP.NET Core the result is mixed. MiniHttpHost is higher for echo at 1 MiB and
  16 MiB (+70% and +67%) and for download at 1 KiB (+35%). ASP.NET Core is higher for echo at
  1 KiB (MiniHttpHost is 16% lower, inside the noise threshold). Downloads at 1 MiB and 16 MiB
  are within 6%, which is also within noise.
- ASP.NET Core's 16 MiB echo has a sequential p50 of about 400 ms, against 60 to 80 ms for the
  other two. Its p50 was 377 to 401 ms in all three rounds, so this is not round-to-round noise.
  Its concurrent throughput there is only 40% below MiniHttpHost's, though. The two
  measurements disagree for that cell. The cause was not investigated, so don't draw a
  conclusion from either number alone.
- At 1 KiB, an 80 KiB copy buffer measured half the throughput of a 4 KiB buffer,
  which is why small echoes use 4 KiB.
- p95 values are noisy. At 1 MiB echo, one WCF round was far above the others, so the
  p95 comparison is weaker than the throughput comparison.

## Limitations

- **One machine.** The client and all servers share a 22-core machine. CPU contention is
  shared, so these numbers show loopback behavior, not server-only capacity.
- **Process and runtime differ for ASP.NET Core.** MiniHttpHost and WCF share the client's
  process, GC, and thread pool. ASP.NET Core runs in its own .NET 10 process with Server GC,
  which is its default for web apps. Its numbers therefore include a different runtime and
  GC setup, not only the framework's own overhead. Treat the ASP.NET Core comparison as
  "what a default ASP.NET Core app does here", not as a same-conditions measurement.
- **No concurrency cap on ASP.NET Core.** MiniHttpHost and WCF cap concurrent calls at 64.
  The ASP.NET Core server sets no equivalent cap. The client sends 32 at a time, so the cap
  doesn't bind for any server here, but it would matter at higher concurrency.
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

# With ASP.NET Core: start the server in its own terminal first, then run the client with --aspnet.
dotnet run -c Release --project tests/BenchmarkServer
tests/Benchmarks/bin/Release/net472/Benchmarks.exe --rounds 3 --seconds 5 --aspnet
```

Options: `--rounds`, `--seconds`, `--concurrency`, `--aspnet`, and `--sizes 1024,1048576` to
choose payload sizes. Port 8081, 8082, and (with `--aspnet`) 8083 must be free. The
benchmark and ASP.NET Core server register the exact `localhost` prefix, so neither needs
elevation.
