using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using MiniHttpHost;

internal static partial class Program
{
    private static async Task CheckRoutingAsync()
    {
        var prefix = Prefix();
        var routes = new HttpRouter()
            .MapGet("/", c => c.TextAsync("root"))
            .MapGet("/parts/{first}/{second}", c => c.TextAsync(c.RouteValues["first"] + "|" + c.RouteValues["second"]))
            .MapGet("/parts/fixed/{second}", c => c.TextAsync("fixed|" + c.RouteValues["second"]))
            .MapGet("/empty//tail/", c => c.TextAsync("empty"))
            .MapGet("/method", c => c.TextAsync("get"))
            .MapPost("/method", c => c.TextAsync("post"));
        using (var server = new MiniWebServer(prefix, routes.HandleAsync))
        using (var client = new HttpClient(new HttpClientHandler { UseProxy = false })
            { BaseAddress = new Uri(prefix), Timeout = TimeSpan.FromSeconds(5) })
        {
            await server.StartAsync();
            Check(await client.GetStringAsync("") == "root", "Root route preserves empty segments");
            Check(await client.GetStringAsync("parts/a/b%20c") == "a|b c", "Multiple route captures decode correctly");
            Check(await client.GetStringAsync("parts/fixed/value") == "fixed|value", "Literal prefix beats parameter prefix");
            Check(await client.GetStringAsync("empty//tail/") == "empty", "Literal empty and trailing segments match");
            foreach (string path in new[] { "parts/a", "parts/a/b/c", "parts//b", "parts/a/b/", "empty//tail", "Method" })
                using (var response = await client.GetAsync(path))
                    Check(response.StatusCode == HttpStatusCode.NotFound, "Exact segment matching: " + path);
            using (var request = new HttpRequestMessage(HttpMethod.Put, "method"))
            using (var response = await client.SendAsync(request))
            {
                Check(response.StatusCode == HttpStatusCode.MethodNotAllowed, "Multiple methods produce 405");
                Check(string.Join(", ", response.Content.Headers.Allow) == "GET, POST", "Allow methods stay sorted");
            }
            using (var content = new ByteArrayContent(new byte[0]))
            using (var response = await client.PostAsync("method", content))
                Check(await response.Content.ReadAsStringAsync() == "post", "Matching method wins after another method matches");
            routes.MapGet("/added", c => c.TextAsync("new"));
            Check(await client.GetStringAsync("added") == "new", "New route snapshot becomes visible after registration");
            Check(await client.GetStringAsync("") == "root", "Publishing routes preserves existing registrations");
            await server.StopAsync();
        }
    }
}
