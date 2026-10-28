using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Description;
using System.ServiceModel.Web;
using System.Threading;
using System.Threading.Tasks;
using MiniHttpHost;

// Compares MiniHttpHost (HttpListener + HttpRouter) with a self-hosted WCF
// WebHttpBinding service on the same HTTP.sys stack. Both servers stream raw
// bodies. Run on Windows with: dotnet run -f net472 -- [--rounds 3] [--seconds 5] [--concurrency 32]
[ServiceContract]
public interface IBench
{
    [OperationContract]
    [WebInvoke(Method = "POST", UriTemplate = "echo")]
    Stream Echo(Stream body);

    [OperationContract]
    [WebGet(UriTemplate = "download/{size}")]
    Stream Download(string size);
}

[ServiceBehavior(InstanceContextMode = InstanceContextMode.PerCall, ConcurrencyMode = ConcurrencyMode.Multiple)]
public class BenchService : IBench
{
    public Stream Echo(Stream body) { return body; }

    public Stream Download(string size)
    {
        int count = int.Parse(size);
        return new MemoryStream(Program.Payload, 0, count, false);
    }
}

internal static class Program
{
    private const string MiniBase = "http://localhost:8081/";
    private const string WcfBase = "http://localhost:8082/";
    private const int CopyBuffer = 81920;
    private const int MaxConcurrentCalls = 64;

    // Shared so every download is a slice of the same buffer; the largest size is the only allocation.
    internal static readonly byte[] Payload = MakePayload(16 * 1024 * 1024);

    private static readonly int[] Sizes = { 1024, 1024 * 1024, 16 * 1024 * 1024 };
    private static readonly int[] LatencyIterations = { 500, 100, 10 };

    private enum Op { Echo, Download }

    private sealed class Cell
    {
        public string Server;
        public Op Operation;
        public int Size;
        public double P50Ms;
        public double P95Ms;
        public double RequestsPerSecond;
        public double MegabytesPerSecond;
    }

    private static async Task Main(string[] args)
    {
        int rounds = IntArg(args, "--rounds", 3);
        double seconds = IntArg(args, "--seconds", 5);
        int concurrency = IntArg(args, "--concurrency", 32);

        ServicePointManager.DefaultConnectionLimit = 256;
        ServicePointManager.Expect100Continue = false;
        ThreadPool.SetMinThreads(MaxConcurrentCalls, MaxConcurrentCalls);

        Console.WriteLine("Environment: {0}, {1} processors, CLR {2}", Environment.OSVersion, Environment.ProcessorCount, Environment.Version);
        Console.WriteLine("rounds={0} seconds={1} concurrency={2}", rounds, seconds, concurrency);

        using (var mini = StartMiniHttpHost())
        using (var wcf = StartWcf())
        using (var handler = new HttpClientHandler { MaxConnectionsPerServer = 256 })
        using (var client = new HttpClient(handler))
        {
            await VerifyAsync(client, MiniBase, "MiniHttpHost");
            await VerifyAsync(client, WcfBase, "WCF");

            var results = new List<Cell>();
            for (int round = 0; round < rounds; round++)
            {
                // Alternate order each round so neither server always runs first on a warm machine.
                var order = round % 2 == 0
                    ? new[] { new KeyValuePair<string, string>("MiniHttpHost", MiniBase), new KeyValuePair<string, string>("WCF", WcfBase) }
                    : new[] { new KeyValuePair<string, string>("WCF", WcfBase), new KeyValuePair<string, string>("MiniHttpHost", MiniBase) };
                foreach (var server in order)
                {
                    foreach (Op op in new[] { Op.Echo, Op.Download })
                    {
                        for (int i = 0; i < Sizes.Length; i++)
                        {
                            var cell = await MeasureAsync(client, server.Key, server.Value, op, Sizes[i], LatencyIterations[i], seconds, concurrency);
                            results.Add(cell);
                            Console.WriteLine("round {0} {1,-12} {2,-8} {3,9} B  p50={4,8:F2} ms  p95={5,8:F2} ms  {6,9:F1} req/s  {7,8:F1} MB/s",
                                round + 1, cell.Server, cell.Operation, cell.Size, cell.P50Ms, cell.P95Ms, cell.RequestsPerSecond, cell.MegabytesPerSecond);
                        }
                    }
                }
            }

            PrintMedians(results, rounds);
        }
    }

    private static int IntArg(string[] args, string name, int fallback)
    {
        int index = Array.IndexOf(args, name);
        int value;
        if (index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out value)) return value;
        return fallback;
    }

    private static byte[] MakePayload(int size)
    {
        var bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[i] = (byte)(i * 31 + 7);
        return bytes;
    }

    private static MiniWebServer StartMiniHttpHost()
    {
        var routes = new HttpRouter()
            .MapPost("/echo", c => c.StreamAsync(c.RequestBody), new HttpRouteOptions
            {
                MaxRequestBodyBytes = 1L << 30,
                Timeout = TimeSpan.FromMinutes(10)
            })
            .MapGet("/download/{size}", c =>
            {
                int count = int.Parse(c.RouteValues["size"]);
                return c.StreamAsync(new MemoryStream(Payload, 0, count, false), "application/octet-stream", count);
            });
        var handler = new HttpPipeline().UseExceptionHandler().Build(routes.HandleAsync);
        var server = new MiniWebServer(MiniBase, handler, concurrency: MaxConcurrentCalls);
        server.StartAsync().GetAwaiter().GetResult();
        return server;
    }

    private static ServiceHost StartWcf()
    {
        var host = new WebServiceHost(typeof(BenchService), new Uri(WcfBase));
        var binding = new WebHttpBinding
        {
            TransferMode = TransferMode.Streamed,
            MaxReceivedMessageSize = int.MaxValue,
            MaxBufferSize = CopyBuffer,
            // Register the exact localhost prefix. The default StrongWildcard asks HTTP.sys for
            // http://+:port/, which needs an elevated process or a URL reservation.
            HostNameComparisonMode = HostNameComparisonMode.Exact
        };
        var endpoint = host.AddServiceEndpoint(typeof(IBench), binding, string.Empty);
        endpoint.Behaviors.Add(new WebHttpBehavior());
        host.Description.Behaviors.Add(new ServiceThrottlingBehavior
        {
            MaxConcurrentCalls = MaxConcurrentCalls,
            MaxConcurrentSessions = MaxConcurrentCalls,
            MaxConcurrentInstances = MaxConcurrentCalls
        });
        host.Open();
        return host;
    }

    private static async Task VerifyAsync(HttpClient client, string baseUrl, string name)
    {
        byte[] upload = MakePayload(64 * 1024);
        long echoed = await ExecuteAsync(client, Op.Echo, baseUrl, upload, upload.Length);
        if (echoed != upload.Length) throw new InvalidOperationException(name + " echo returned " + echoed + " bytes");
        long downloaded = await ExecuteAsync(client, Op.Download, baseUrl, null, 64 * 1024);
        if (downloaded != 64 * 1024) throw new InvalidOperationException(name + " download returned " + downloaded + " bytes");
        Console.WriteLine("verified {0}", name);
    }

    private static async Task<Cell> MeasureAsync(HttpClient client, string server, string baseUrl, Op op, int size, int latencyIterations, double seconds, int concurrency)
    {
        byte[] upload = op == Op.Echo ? Payload.Take(size).ToArray() : null;
        int warmup = Math.Min(20, latencyIterations);
        for (int i = 0; i < warmup; i++) await ExecuteAsync(client, op, baseUrl, upload, size);

        var samples = new double[latencyIterations];
        for (int i = 0; i < latencyIterations; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await ExecuteAsync(client, op, baseUrl, upload, size);
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);

        // Each request moves `size` bytes in each direction for echo, and `size` bytes for download.
        long bytesPerRequest = op == Op.Echo ? 2L * size : size;
        long requests = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var workers = new Task[concurrency];
        for (int w = 0; w < concurrency; w++)
        {
            workers[w] = Task.Run(async () =>
            {
                while (clock.Elapsed.TotalSeconds < seconds)
                {
                    await ExecuteAsync(client, op, baseUrl, upload, size);
                    Interlocked.Increment(ref requests);
                }
            });
        }
        await Task.WhenAll(workers);
        double elapsed = clock.Elapsed.TotalSeconds;

        return new Cell
        {
            Server = server,
            Operation = op,
            Size = size,
            P50Ms = Percentile(samples, 0.50),
            P95Ms = Percentile(samples, 0.95),
            RequestsPerSecond = requests / elapsed,
            MegabytesPerSecond = requests * (double)bytesPerRequest / (1024 * 1024) / elapsed
        };
    }

    // Returns the number of response body bytes read, so the caller can check the size.
    private static async Task<long> ExecuteAsync(HttpClient client, Op op, string baseUrl, byte[] upload, int size)
    {
        if (op == Op.Echo)
        {
            using (var content = new ByteArrayContent(upload))
            using (var response = await client.PostAsync(baseUrl + "echo", content).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                {
                    long read = await CountAsync(body).ConfigureAwait(false);
                    if (read != upload.Length) throw new InvalidOperationException("echo returned " + read + " of " + upload.Length + " bytes");
                    return read;
                }
            }
        }
        using (var response = await client.GetAsync(baseUrl + "download/" + size).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            using (var body = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                long read = await CountAsync(body).ConfigureAwait(false);
                if (read != size) throw new InvalidOperationException("download returned " + read + " of " + size + " bytes");
                return read;
            }
        }
    }

    private static async Task<long> CountAsync(Stream body)
    {
        var buffer = new byte[CopyBuffer];
        long total = 0;
        int n;
        while ((n = await body.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0) total += n;
        return total;
    }

    private static double Percentile(double[] sorted, double fraction)
    {
        int index = (int)Math.Ceiling(fraction * sorted.Length) - 1;
        return sorted[Math.Max(0, Math.Min(sorted.Length - 1, index))];
    }

    private static void PrintMedians(List<Cell> results, int rounds)
    {
        Console.WriteLine();
        Console.WriteLine("Median across {0} round(s):", rounds);
        Console.WriteLine("| Server | Operation | Size | p50 ms | p95 ms | req/s | MB/s |");
        Console.WriteLine("|---|---|---:|---:|---:|---:|---:|");
        var groups = results.GroupBy(r => new { r.Server, r.Operation, r.Size }).OrderBy(g => g.Key.Operation).ThenBy(g => g.Key.Size).ThenBy(g => g.Key.Server);
        foreach (var g in groups)
        {
            Console.WriteLine("| {0} | {1} | {2:N0} | {3:F2} | {4:F2} | {5:F1} | {6:F1} |",
                g.Key.Server, g.Key.Operation, g.Key.Size,
                Median(g.Select(c => c.P50Ms)), Median(g.Select(c => c.P95Ms)),
                Median(g.Select(c => c.RequestsPerSecond)), Median(g.Select(c => c.MegabytesPerSecond)));
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        int mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
