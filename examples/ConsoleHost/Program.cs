using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHttpHost.Examples
{
    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            var prefix = args.Length == 0 ? "http://localhost:8080/" : args[0];
            using (var stopping = new CancellationTokenSource())
            {
                Console.CancelKeyPress += (sender, e) =>
                {
                    e.Cancel = true;
                    stopping.Cancel();
                };
                var binary = new BinaryEndpoints(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data"));
                var routes = new HttpRouter()
                    .MapGet("/", (context, token) => WriteGreetingAsync(context, token))
                    .Map("HEAD", "/", (context, token) => WriteGreetingAsync(context, token))
                    .MapPost("/binary", binary.UploadAsync)
                    .MapGet("/binary", binary.DownloadAsync)
                    .MapPost("/echo", async (context, token) =>
                    {
                        context.Response.ContentType = "application/octet-stream";
                        context.Response.SendChunked = true;
                        await context.Request.InputStream.CopyToAsync(
                            context.Response.OutputStream, 81920, token).ConfigureAwait(false);
                    });
                var handler = new HttpPipeline()
                    .Use(async (context, token, next) =>
                    {
                        context.Response.Headers["X-MiniHttpHost"] = "example";
                        await next().ConfigureAwait(false);
                    })
                    .Build(routes.HandleAsync);
                var server = new MiniWebServer(prefix, handler);
                Console.WriteLine("Listening on {0} (Ctrl+C to stop)", prefix);
                await server.RunAsync(stopping.Token);
            }
        }

        private static async Task WriteGreetingAsync(HttpListenerContext context, CancellationToken token)
        {
            var response = context.Response;
            response.ContentType = "text/plain; charset=utf-8";
            var body = Encoding.UTF8.GetBytes("Hello from MiniHttpHost!\n");
            response.ContentLength64 = body.Length;
            if (context.Request.HttpMethod != "HEAD")
                await response.OutputStream.WriteAsync(body, 0, body.Length, token).ConfigureAwait(false);
        }
    }
}
