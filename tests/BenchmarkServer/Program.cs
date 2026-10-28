using System;
using System.IO;
using System.Threading;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.HttpSys;
using Microsoft.Extensions.Logging;

// ASP.NET Core minimal API on HTTP.sys with the same routes as tests/Benchmarks.
// The net472 client is a separate process, so this server runs out of process:
//   dotnet run -c Release --project tests/BenchmarkServer
// Then, in another terminal, run tests/Benchmarks with --aspnet.
// Copy-buffer choices mirror the MiniHttpHost server in tests/Benchmarks/Program.cs.
internal static class Program
{
    private const string Url = "http://localhost:8083/";
    private const int MaxConcurrentCalls = 64;
    private const int SmallCopyBuffer = 4096;
    private const int LargeCopyBuffer = 81920;
    private const long SmallEchoLimit = 16 * 1024;

    private static readonly byte[] Payload = MakePayload(16 * 1024 * 1024);

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void Main(string[] args)
    {
        ThreadPool.SetMinThreads(MaxConcurrentCalls, MaxConcurrentCalls);

        var builder = WebApplication.CreateBuilder(args);
        // Per-request console logging would cost far more than the routes do; the other servers don't log.
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(Url);
        builder.WebHost.UseHttpSys(options =>
        {
            // Matches MaxRequestBodyBytes on the MiniHttpHost echo route.
            options.MaxRequestBodySize = 1L << 30;
        });

        var app = builder.Build();

        // Unknown length (chunked) takes the large buffer, as in MiniHttpHost.
        app.MapPost("/echo", async (HttpContext context) =>
        {
            long length = context.Request.ContentLength ?? -1;
            int bufferSize = length >= 0 && length <= SmallEchoLimit ? SmallCopyBuffer : LargeCopyBuffer;
            context.Response.ContentType = "application/octet-stream";
            await context.Request.Body.CopyToAsync(context.Response.Body, bufferSize, context.RequestAborted);
        });

        app.MapGet("/download/{size:int}", async (int size, HttpContext context) =>
        {
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = size;
            using (var body = new MemoryStream(Payload, 0, size, false))
                await body.CopyToAsync(context.Response.Body, SmallCopyBuffer, context.RequestAborted);
        });

        app.Run();
    }

    private static byte[] MakePayload(int size)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[i] = (byte)(i * 31 + 7);
        return bytes;
    }
}
