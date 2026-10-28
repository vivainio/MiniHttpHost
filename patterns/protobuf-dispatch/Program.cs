using System;
using System.Threading.Tasks;

namespace MiniHttpHost.Patterns.ProtobufDispatch
{
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            var mode = args.Length == 0 ? "server" : args[0];
            var prefix = args.Length < 2 ? "http://localhost:8082/" : args[1];
            if (mode == "client")
            {
                using (var client = new MessageClient(prefix)) await DemoAsync(client);
                return 0;
            }
            if (mode != "server" && mode != "check")
            {
                Console.Error.WriteLine("Usage: ProtobufDispatch [server|client|check] [http://localhost:8082/]");
                return 1;
            }
            var endpoint = new MessageEndpoint();
            var routes = new HttpRouter().MapPost("/messages", endpoint.HandleAsync,
                new HttpRouteOptions { MaxRequestBodyBytes = 65536, Timeout = TimeSpan.FromSeconds(10) });
            var handler = new HttpPipeline().UseExceptionHandler((context, error) =>
                Console.Error.WriteLine(error)).Build(routes.HandleAsync);
            using (var server = new MiniWebServer(prefix, handler))
            {
                await server.StartAsync();
                try
                {
                    if (mode == "check")
                    {
                        using (var client = new MessageClient(prefix)) await Checks.RunAsync(client);
                    }
                    else
                    {
                        Console.WriteLine("Listening on " + prefix + " (press Enter to stop)");
                        Console.ReadLine();
                    }
                }
                finally { await server.StopAsync(TimeSpan.FromSeconds(5)); }
            }
            return 0;
        }

        private static async Task DemoAsync(MessageClient client)
        {
            var before = await client.GetAsync(1);
            Console.WriteLine("Before: " + before.Id + " " + before.Name);
            var updated = await client.UpdateAsync(1, "Grace");
            Console.WriteLine("Updated: " + updated.Id + " " + updated.Name);
            var after = await client.GetAsync(1);
            Console.WriteLine("After: " + after.Id + " " + after.Name);
        }
    }
}
