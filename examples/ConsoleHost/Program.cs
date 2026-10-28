using System;
using System.IO;
using System.Net;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHttpHost.Examples
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            try
            {
                await RunAsync(args).ConfigureAwait(false);
                return 0;
            }
            catch (HttpListenerException error)
            {
                Console.Error.WriteLine("HTTP listener failed. Check the URL reservation, account permissions, and port: {0}", error);
                return 1;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine("HTTP host failed: {0}", error);
                return 1;
            }
        }

        private static async Task RunAsync(string[] args)
        {
            var prefix = args.Length == 0 ? "http://localhost:8080/" : args[0];
            var binary = new BinaryEndpoints(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"));
            var routes = new HttpRouter()
                .MapGet("/", context => context.TextAsync("Hello from MiniHttpHost!\n"))
                .Map("HEAD", "/", context => context.TextAsync("Hello from MiniHttpHost!\n"))
                .MapGet("/hello/{name}", context => context.TextAsync("Hello, " + context.RouteValues["name"] + "!\n"))
                .MapGet("/report", context => context.TextAsync(new string('x', 100000)),
                    new HttpRouteOptions { EnableCompression = true })
                .MapGet("/example-error", context => throw new InvalidOperationException("Example application failure"))
                .MapPost("/binary", binary.UploadAsync,
                    new HttpRouteOptions { MaxRequestBodyBytes = 4L * 1024 * 1024 * 1024, Timeout = TimeSpan.FromMinutes(30) })
                .MapGet("/binary", binary.DownloadAsync)
                .MapPost("/echo", context => context.StreamAsync(context.RequestBody));
            routes.MapGroup("/api")
                .Use(async (context, next) =>
                {
                    context.Items["source"] = "application";
                    await next().ConfigureAwait(false);
                })
                .MapGet("/status", context => context.JsonAsync(new Status { State = "ready" }));

            var handler = new HttpPipeline()
                // Unhandled errors become a generic 500 before headers are sent.
                // Errors after streaming starts abort the response instead.
                .UseExceptionHandler((context, error) =>
                    Console.Error.WriteLine("{0} {1} failed: {2}", context.Request.HttpMethod,
                        context.Request.Url.AbsolutePath, error))
                .Use(async (context, next) =>
                {
                    try { await next().ConfigureAwait(false); }
                    catch (FileNotFoundException) when (!context.HasStarted)
                    {
                        // Map a known application error, while allowing the outer
                        // exception handler to abort an already-started download.
                        await context.TextAsync("Not found\n", 404).ConfigureAwait(false);
                    }
                })
                .Use(async (context, next) =>
                {
                    context.Response.Headers["X-MiniHttpHost"] = "example";
                    await next().ConfigureAwait(false);
                })
                .Build(routes.HandleAsync);
            using (var server = new MiniWebServer(prefix, handler))
            using (var stopRequested = new CancellationTokenSource())
            {
                ConsoleCancelEventHandler onCancel = (sender, e) => { e.Cancel = true; stopRequested.Cancel(); };
                Console.CancelKeyPress += onCancel;
                try
                {
                    await server.StartAsync(); // Startup failures reach Main's catch.
                    Console.WriteLine("Listening on {0} (Ctrl+C to stop)", prefix);
                    var requested = Task.Delay(Timeout.Infinite, stopRequested.Token);
                    await Task.WhenAny(server.Completion, requested);
                    // A failed Completion is rethrown by StopAsync and reaches Main.
                    await server.StopAsync(TimeSpan.FromSeconds(10));
                }
                finally { Console.CancelKeyPress -= onCancel; }
            }
        }

        // The built-in JSON helper uses DataContractJsonSerializer.
        [DataContract]
        public sealed class Status { [DataMember] public string State { get; set; } }
    }
}
