using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MiniHttpHost;

internal static class Program
{
    private static int assertions;
    private static int portOffset;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++;
    }

    private static string Prefix()
    {
        if (int.TryParse(Environment.GetEnvironmentVariable("MINIHTTPHOST_TEST_PORT"), out int configuredPort))
            return "http://localhost:" + (configuredPort + portOffset++) + "/";
        var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        int port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return "http://localhost:" + port + "/";
    }

    private static async Task Main()
    {
        var file = Path.GetTempFileName();
        try
        {
            byte[] binary = Enumerable.Range(0, 4 * 1024 * 1024).Select(i => (byte)i).ToArray();
            File.WriteAllBytes(file, binary);
            await CheckHttpAsync(file, binary);
            await CheckForcedShutdownAsync();
            Console.WriteLine("PASS: {0} assertions", assertions);
        }
        finally { File.Delete(file); }
    }

    private static async Task CheckHttpAsync(string file, byte[] binary)
    {
        var prefix = Prefix();
        var slowEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var exits = new List<string>();
        var streamLeftOpen = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateErrorTracked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var routes = new HttpRouter()
            .MapGet("/files/{id}", c => c.TextAsync(c.RouteValues["id"]))
            .MapGet("/files/new", c => c.TextAsync("literal"))
            .MapGet("/file", c => c.FileAsync(file))
            .Map("HEAD", "/file", c => c.FileAsync(file))
            .MapGet("/json", c => c.JsonAsync(new Message { Value = "ok" }))
            .MapGet("/error", c => throw new InvalidOperationException("sensitive details"))
            .MapPost("/echo", c => c.StreamAsync(c.RequestBody))
            .MapPost("/limited", async c =>
            {
                await c.RequestBody.CopyToAsync(Stream.Null, 81920, c.RequestAborted);
                await c.TextAsync("accepted");
            }, new HttpRouteOptions { MaxRequestBodyBytes = 4 })
            .MapGet("/stream", async c =>
            {
                using (var input = new MemoryStream(new byte[] { 1, 2, 3 }))
                {
                    await c.StreamAsync(input);
                    streamLeftOpen.SetResult(input.CanRead);
                }
            })
            .MapGet("/late-error", async c =>
            {
                c.Response.SendChunked = true;
                byte[] body = { 1, 2, 3 };
                await c.ResponseBody.WriteAsync(body, 0, body.Length, c.RequestAborted);
                await c.ResponseBody.FlushAsync(c.RequestAborted);
                throw new Exception("after write");
            })
            .MapGet("/timeout", c => Task.Delay(10000, c.RequestAborted),
                new HttpRouteOptions { Timeout = TimeSpan.FromMilliseconds(100) })
            .MapGet("/slow", async c =>
            {
                slowEntered.TrySetResult(true);
                await slowRelease.Task;
                await c.TextAsync("drained");
            });
        routes.MapGroup("/api").Use(async (c, next) =>
        {
            if (c.Request.Headers["X-Key"] != "allowed") { await c.TextAsync("denied", 403); return; }
            c.Items["identity"] = "caller";
            await next();
        }).MapGroup("/v1").MapGet("/identity", c => c.TextAsync((string)c.Items["identity"]));

        bool duplicateRejected = false;
        try { routes.MapGet("/files/{name}", c => c.TextAsync("ambiguous")); }
        catch (ArgumentException) { duplicateRejected = true; }
        Check(duplicateRejected, "Equivalent parameter patterns must be rejected");

        var pipeline = new HttpPipeline().UseExceptionHandler((c, e) =>
        {
            if (e.Message == "after write") lateErrorTracked.TrySetResult(c.HasStarted);
        });
        pipeline.Use(async (c, next) =>
        {
            c.Items["order"] = "A";
            await next();
            lock (exits) exits.Add("A");
        });
        pipeline.Use(async (c, next) =>
        {
            c.Items["order"] += "B";
            c.Response.Headers["X-Order"] = (string)c.Items["order"];
            await next();
            lock (exits) exits.Add("B");
        });
        var handler = pipeline.Build(routes.HandleAsync);
        pipeline.Use((c, next) => throw new Exception("Must not affect built pipeline"));
        using (var server = new MiniWebServer(prefix, handler, concurrency: 4))
        using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(prefix), Timeout = TimeSpan.FromSeconds(5) })
        {
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel();
                bool cancelledStartup = false;
                try { await server.StartAsync(cancelled.Token); }
                catch (OperationCanceledException) { cancelledStartup = true; }
                Check(cancelledStartup, "Startup honors cancellation before opening listener");
            }
            await server.StartAsync();
            bool duplicateStart = false;
            try { await server.StartAsync(); }
            catch (InvalidOperationException) { duplicateStart = true; }
            Check(duplicateStart, "Server cannot start twice");
            using (var response = await client.GetAsync("files/new?ignored=yes"))
            {
                Check(await response.Content.ReadAsStringAsync() == "literal", "Literal must beat parameter");
                Check(response.Headers.GetValues("X-Order").Single() == "AB", "Middleware entry order");
            }
            Check(await client.GetStringAsync("files/hello%20world") == "hello world", "Parameter decoding");
            Check((await client.GetAsync("missing")).StatusCode == HttpStatusCode.NotFound, "404");
            using (var response = await client.PostAsync("files/123", new ByteArrayContent(new byte[0])))
            {
                Check(response.StatusCode == HttpStatusCode.MethodNotAllowed, "405");
                Check(response.Content.Headers.Allow.Contains("GET"), "Allow header");
            }
            Check((await client.GetAsync("api/v1/identity")).StatusCode == HttpStatusCode.Forbidden, "Group short-circuit");
            using (var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/identity"))
            {
                request.Headers.Add("X-Key", "allowed");
                using (var response = await client.SendAsync(request))
                    Check(await response.Content.ReadAsStringAsync() == "caller", "Nested groups and Items");
            }
            Check((await client.GetByteArrayAsync("file")).SequenceEqual(binary), "File streaming");
            using (var response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "file")))
            {
                Check(response.Content.Headers.ContentLength == binary.LongLength, "HEAD content length");
                Check((await response.Content.ReadAsByteArrayAsync()).Length == 0, "HEAD has no body");
            }
            // HTTP/1.1 clients may finish sending before reading a response.
            // Keep this convenience-client echo below socket buffer sizes; the
            // raw socket check below explicitly exercises incremental duplex I/O.
            byte[] echoPayload = binary.Take(65536).ToArray();
            using (var response = await client.PostAsync("echo", new ByteArrayContent(echoPayload)))
            {
                Check(response.Headers.TransferEncodingChunked == true, "Unknown length response is chunked");
                Check((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(echoPayload), "Binary streaming echo");
            }
            await CheckIncrementalStreamingAsync(prefix);
            Check((await client.GetStringAsync("json")).Contains("\"Value\":\"ok\""), "JSON helper");
            using (var response = await client.GetAsync("error"))
            {
                Check(response.StatusCode == HttpStatusCode.InternalServerError, "Central error response");
                Check(!(await response.Content.ReadAsStringAsync()).Contains("sensitive"), "Error details remain private");
            }
            using (var response = await client.PostAsync("limited", new ByteArrayContent(new byte[5])))
                Check((int)response.StatusCode == 413, "Known-length request limit");
            using (var response = await client.PostAsync("limited", new ByteArrayContent(new byte[4])))
                Check(await response.Content.ReadAsStringAsync() == "accepted", "Exact request limit");
            // Non-seekable StreamContent forces a chunked request. An over-limit
            // body must be rejected during reads, independently of Content-Length.
            using (var body = new StreamContent(new NonSeekableInput(new byte[5])))
            using (var response = await client.PostAsync("limited", body))
                Check((int)response.StatusCode == 413, "Unknown-length request limit");
            if (Type.GetType("Mono.Runtime") == null)
            {
                // Mono's chunked request EOF has a known local-development issue;
                // exercise successful chunked uploads against Windows HTTP.sys.
                using (var body = new StreamContent(new NonSeekableInput(new byte[4])))
                using (var response = await client.PostAsync("limited", body))
                    Check(await response.Content.ReadAsStringAsync() == "accepted", "Exact chunked request limit");
                using (var body = new StreamContent(new NonSeekableInput(echoPayload)))
                using (var response = await client.PostAsync("echo", body))
                    Check((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(echoPayload), "Chunked upload streaming echo");
            }
            Check((await client.GetByteArrayAsync("stream")).SequenceEqual(new byte[] { 1, 2, 3 }), "Stream helper");
            await WithDeadline(streamLeftOpen.Task);
            Check(streamLeftOpen.Task.Result, "Stream helper preserves caller ownership");
            try
            {
                using (var response = await client.GetAsync("late-error", HttpCompletionOption.ResponseHeadersRead))
                {
                    Check(response.StatusCode == HttpStatusCode.OK, "Started error response retains original status");
                    try { await response.Content.ReadAsByteArrayAsync(); } catch (HttpRequestException) { }
                }
            }
            catch (HttpRequestException) { } // Some transports observe the abort before parsing headers.
            await WithDeadline(lateErrorTracked.Task);
            Check(lateErrorTracked.Task.Result, "Central errors detect a started response");
            var watch = Stopwatch.StartNew();
            try { await client.GetAsync("timeout"); } catch (HttpRequestException) { }
            Check(watch.Elapsed < TimeSpan.FromSeconds(3), "Per-route timeout aborts connection");

            var pending = client.GetStringAsync("slow");
            await WithDeadline(slowEntered.Task);
            var stopping = server.StopAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
            Check(!stopping.IsCompleted, "Graceful stop waits for an active handler");
            Check((await client.GetAsync("files/new")).StatusCode == HttpStatusCode.ServiceUnavailable, "New requests rejected during drain");
            slowRelease.TrySetResult(true);
            Check(await pending == "drained", "Active response completes during drain");
            await WithDeadline(stopping);
            Check(server.Completion.IsCompleted, "Graceful stop finishes all workers");
        }
        lock (exits) Check(exits.Count >= 2 && exits[0] == "B" && exits[1] == "A", "Middleware unwind order");
    }

    private static async Task CheckForcedShutdownAsync()
    {
        var entered = new TaskCompletionSource<HttpContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefix = Prefix();
        using (var server = new MiniWebServer(prefix, async context =>
        {
            entered.SetResult(context);
            await release.Task; // Deliberately ignores cancellation to test the deadline.
        }))
        using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri(prefix) })
        {
            await server.StartAsync();
            var request = client.GetAsync("");
            await WithDeadline(entered.Task);
            await WithDeadline(server.StopAsync(TimeSpan.FromMilliseconds(50)));
            Check(entered.Task.Result.RequestAborted.IsCancellationRequested, "Deadline signals request cancellation");
            Check(!server.Completion.IsCompleted, "Uncooperative handler can outlive StopAsync");
            release.SetResult(true);
            await WithDeadline(server.Completion);
            try { using (await request) { } } catch (HttpRequestException) { }
        }
    }

    private static async Task CheckIncrementalStreamingAsync(string prefix)
    {
        var uri = new Uri(prefix);
        using (var socket = new TcpClient())
        {
            await socket.ConnectAsync(uri.Host, uri.Port);
            using (var stream = socket.GetStream())
            {
                byte[] header = Encoding.ASCII.GetBytes("POST /echo HTTP/1.1\r\nHost: " + uri.Authority +
                    "\r\nContent-Length: 131072\r\nConnection: close\r\n\r\n");
                byte[] block = new byte[65536];
                await stream.WriteAsync(header, 0, header.Length);
                await stream.WriteAsync(block, 0, block.Length);
                // A server that buffers the entire request deadlocks here: we
                // deliberately don't send the second half until body bytes arrive.
                var received = new MemoryStream();
                byte[] buffer = new byte[4096];
                while (true)
                {
                    var read = stream.ReadAsync(buffer, 0, buffer.Length);
                    await WithDeadline(read);
                    Check(read.Result > 0, "Incremental response connection stays open");
                    received.Write(buffer, 0, read.Result);
                    string partial = Encoding.ASCII.GetString(received.ToArray());
                    int boundary = partial.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (boundary >= 0 && received.Length > boundary + 8) break;
                }
                Check(Encoding.ASCII.GetString(received.ToArray()).StartsWith("HTTP/1.1 200", StringComparison.Ordinal),
                    "Response body begins before upload completes");
                await stream.WriteAsync(block, 0, block.Length);
                await WithDeadline(stream.CopyToAsync(Stream.Null));
            }
        }
    }

    private static async Task WithDeadline(Task task)
    {
        if (await Task.WhenAny(task, Task.Delay(5000)) != task) throw new Exception("Check timed out");
        await task;
    }

    [DataContract]
    private sealed class Message
    {
        [DataMember] public string Value { get; set; }
    }

    private sealed class NonSeekableInput : MemoryStream
    {
        internal NonSeekableInput(byte[] data) : base(data) { }
        public override bool CanSeek => false;
    }
}
