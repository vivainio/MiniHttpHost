# Develop and check

```sh
dotnet build examples/ConsoleHost/ConsoleHost.csproj -c Release
mono examples/ConsoleHost/bin/Release/net472/ConsoleHost.exe
curl --data-binary @large.bin http://localhost:8080/binary
curl -o downloaded.bin http://localhost:8080/binary
curl --data-binary @large.bin http://localhost:8080/echo -o echoed.bin
curl http://localhost:8080/api/status
```

Use Ctrl+C to stop the example. On Windows, run the executable directly.

```sh
dotnet build tests/Checks/Checks.csproj -c Release
mono tests/Checks/bin/Release/net472/Checks.exe
```

The checks use local HTTP listeners and cover routing, group authorization,
middleware order, error handling, binary copying, response bytes arriving before
an upload completes, request limits, route timeouts, and graceful/forced shutdown.
The Windows GitHub Actions job runs the same executable on .NET Framework against
HTTP.sys. `MINIHTTPHOST_TEST_PORT` selects the first of four sequential test ports
when explicit Windows URL reservations are needed. The Windows-only WCF cohosting
check verifies both protocols can share a process on separate ports and shut down
independently.

Mono 6.14.1 on macOS supports local development, but is not an HTTP.sys substitute.
An unrestricted chunked request upload previously stalled after the body arrived;
use fixed-length uploads for local Mono development and verify chunked requests
on Windows. Incremental chunked responses and early rejection of oversized chunked
requests pass locally.

The current context-based handler API replaces the initial raw-context/two-argument
API: handlers now receive `HttpContext`, and use `context.RequestAborted` for the
token. Existing application services can stay instance-based.
