using System;
using System.Threading.Tasks;

namespace MiniHttpHost.Patterns
{
    internal static class Program
    {
        private static async Task Main(string[] args)
        {
            var routes = new HttpRouter()
                .MapPost("/soap", SoapEndpoint.HandleAsync,
                    new HttpRouteOptions { MaxRequestBodyBytes = 65536, Timeout = TimeSpan.FromSeconds(10) })
                .MapGet("/events", StreamingEndpoint.HandleAsync,
                    new HttpRouteOptions { Timeout = TimeSpan.FromSeconds(30) });
            var handler = new HttpPipeline().UseExceptionHandler((context, error) =>
                Console.Error.WriteLine(error)).Build(routes.HandleAsync);
            using (var server = new MiniWebServer(args.Length == 0 ? "http://localhost:8081/" : args[0], handler))
            {
                await server.StartAsync();
                Console.WriteLine("Patterns listening; press Enter to stop.");
                Console.ReadLine();
                await server.StopAsync(TimeSpan.FromSeconds(5));
            }
        }
    }
}
