# MiniHttpHost

Minimal HttpListener library targeting .NET Framework 4.7.2, with no runtime packages.

**On Windows, MiniHttpHost uses HTTP.sys, the kernel-mode HTTP server stack also
used by IIS to host ASP.NET applications.** `HttpListener` exposes that Windows
infrastructure directly, so the library can live inside your existing process
without requiring an IIS deployment. Request handlers and middleware run in your
application on the shared managed thread pool.

See Microsoft's [HttpListener documentation](https://learn.microsoft.com/en-us/dotnet/api/system.net.httplistener?view=netframework-4.7.2)
and [IIS architecture](https://learn.microsoft.com/en-us/iis/get-started/introduction-to-iis/introduction-to-iis-architecture).
ASP.NET Core's default standalone server is Kestrel; the shared HTTP.sys foundation
described here applies to IIS hosting and Windows HttpListener. Mono on macOS uses
a different implementation.

Build using the .NET SDK (the reference-assemblies package supplies the Windows
compile-time API surface on macOS):

```sh
dotnet build -c Release
```

Reference `bin/Release/net472/MiniHttpHost.dll` in your application and copy it
alongside the application executable. No NuGet packages are needed at runtime.
Alternatively, add a project reference to `MiniHttpHost.csproj`.

The account needs permission to register
the URL prefix; configure a URL ACL for the actual service account if necessary.
Mono's macOS HTTP implementation differs from Windows HTTP.sys; verify Windows
hosting separately.
Local Mono 6.14.1 checks passed a 32 MiB fixed-length upload/download and an
incremental chunked response. A chunked request upload stalled after receiving the
body; verify chunked requests on Windows and use fixed-length uploads for local
Mono development. The library passes request streams through without decoding
HTTP framing itself.

Create a server with your own handler, then retain the running task for your
application's lifetime:

```csharp
using MiniHttpHost;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

// Fields on your application's hosting component:
private readonly CancellationTokenSource webStopping = new CancellationTokenSource();
private Task webTask;

public void StartWebServer()
{
    var routes = new HttpRouter()
        .MapGet("/hello", HandleRequestAsync)
        .MapPost("/echo", EchoAsync);
    var server = new MiniWebServer("http://localhost:8080/", routes.HandleAsync,
        concurrency: 16);
    webTask = server.RunAsync(webStopping.Token);
}

public async Task StopWebServerAsync()
{
    webStopping.Cancel();
    try
    {
        if (webTask != null) await webTask.ConfigureAwait(false);
    }
    finally
    {
        webStopping.Dispose();
    }
}

private async Task HandleRequestAsync(HttpListenerContext context,
    CancellationToken cancellationToken)
{
    byte[] body = Encoding.UTF8.GetBytes("Hello from the application!\n");
    context.Response.ContentType = "text/plain; charset=utf-8";
    context.Response.ContentLength64 = body.Length;
    await context.Response.OutputStream.WriteAsync(body, 0, body.Length,
        cancellationToken).ConfigureAwait(false);
}

private async Task EchoAsync(HttpListenerContext context, CancellationToken token)
{
    context.Response.ContentType = "application/octet-stream";
    context.Response.SendChunked = true; // Response length is not known upfront.
    await context.Request.InputStream.CopyToAsync(
        context.Response.OutputStream, 81920, token).ConfigureAwait(false);
}
```

This example assumes startup and shutdown each happen once, in order. Monitor
`webTask` for faults during the host's lifetime as well as awaiting it at shutdown.
All request handlers can run concurrently, so synchronize shared application state.
Handlers can be instance methods (as above) or lambdas, with dependencies passed
through your own constructor. Nothing requires static application state.

## Routes and streaming binary data

`HttpRouter.MapGet`, `MapPost`, and `Map(method, path, handler)` register handlers.
Paths match `Request.Url.AbsolutePath` exactly, including any listener base path;
they are case-sensitive, ignore the query string, and distinguish trailing slashes.
Methods are case-sensitive: use uppercase standard methods. Register HEAD explicitly
if needed. Unknown paths return 404; an unsupported method returns 405 with `Allow`.
Duplicate registrations throw. Path parameters and wildcard routes are not included.

Request bodies are exposed as `context.Request.InputStream` and responses as
`context.Response.OutputStream`. The library never buffers the whole body. Await
reads/writes or `CopyToAsync` to stream with bounded memory and backpressure.
Keep all stream work within the handler's task; the server closes the response
when that task completes. Set headers before writing the first byte. Use
`ContentLength64` when the response length is known, or `SendChunked = true` for
unknown lengths. For incremental messages, flush the output after each message.
Handlers can accept fixed-length or chunked request bodies without requiring a
known `ContentLength64`. There is no library-imposed payload-size limit; a host
can enforce its own limits/timeouts while reading.

The console example registers instance methods on `BinaryEndpoints`:

```csharp
var binary = new BinaryEndpoints(storageDirectory);
var routes = new HttpRouter()
    .MapPost("/binary", binary.UploadAsync)
    .MapGet("/binary", binary.DownloadAsync);
var server = new MiniWebServer("http://localhost:8080/", routes.HandleAsync);
Task running = server.RunAsync(hostStoppingToken);
```

`BinaryEndpoints` is sample application code in `examples/ConsoleHost`, not part
of the DLL. It streams uploads to a temporary file, publishes completed uploads,
and streams downloads from disk using 80 KiB copy buffers. It serializes access
to its single resource so downloads never read a partial upload. Your application
can register handlers for its own storage or data source instead.

## Middleware

Wrap the router with middleware, using instance methods or lambdas:

```csharp
var handler = new HttpPipeline()
    .Use(async (context, token, next) =>
    {
        // Set headers before the downstream handler starts streaming.
        context.Response.Headers["X-Application"] = "MyApp";
        await next().ConfigureAwait(false);
    })
    .Use(async (context, token, next) =>
    {
        if (!myAuthorizationService.IsAllowed(context.Request))
        {
            context.Response.StatusCode = 403;
            context.Response.ContentLength64 = 0;
            return; // Short-circuit: the route handler is not invoked.
        }
        await next().ConfigureAwait(false);
    })
    .Build(routes.HandleAsync);

var server = new MiniWebServer("http://localhost:8080/", handler);
```

With A then B registered, execution is A → B → route → B → A. `Build` takes a
snapshot; later `Use` calls do not affect the built handler. Middleware is shared
across requests, but its context and `next` callback belong to the current request.
Await `next()` at most once, or return without calling it. Do not retain it after
the middleware task completes. Middleware must not dispose the request/response
streams. Reading a request body consumes it; the library does not rewind it.
Post-processing can log timing or errors, but headers/status cannot be changed
once downstream code has started sending a streamed response.

Supply an asynchronous handler; the server closes
each response after the handler returns. Observe the returned task for startup or
listener failures. Use a new server instance for each run.

The default 16 async loops handle up to 16 requests concurrently using the shared
managed thread pool. Waiting for requests or asynchronous I/O does not reserve a
worker thread. No dedicated threads or custom thread pools are created. Keep
handlers asynchronous and avoid `.Wait()`, `.Result`, and `Thread.Sleep`.

Cancellation stops the listener and aborts active HTTP responses, then waits for
handlers to return. Handlers must honor the token. This is immediate shutdown,
not graceful draining. Handler failures are traced and their responses aborted.
Excess traffic waits in the underlying listener backlog; this is not a custom
overload/503 policy. Authentication, static files, and WCF are out of scope.

## Console example on macOS

The console host is a separate project and is not compiled into the library:

```sh
dotnet build examples/ConsoleHost/ConsoleHost.csproj
mono examples/ConsoleHost/bin/Debug/net472/ConsoleHost.exe
curl -i http://localhost:8080/
curl --data-binary @large.bin http://localhost:8080/binary
curl -o downloaded.bin http://localhost:8080/binary
curl --data-binary @large.bin http://localhost:8080/echo -o echoed.bin
```

Use Ctrl+C to stop it. On Windows, run `ConsoleHost.exe` directly.
