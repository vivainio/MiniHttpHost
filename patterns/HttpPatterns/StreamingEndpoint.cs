using System.Text;
using System.Threading.Tasks;

namespace MiniHttpHost.Patterns
{
    internal static class StreamingEndpoint
    {
        public static async Task HandleAsync(HttpContext context)
        {
            context.Response.ContentType = "application/x-ndjson; charset=utf-8";
            context.Response.SendChunked = true;
            context.Response.Headers["Cache-Control"] = "no-cache";
            for (var sequence = 1; sequence <= 5; sequence++)
            {
                context.RequestAborted.ThrowIfCancellationRequested();
                var bytes = Encoding.UTF8.GetBytes("{\"sequence\":" + sequence + "}\n");
                await context.ResponseBody.WriteAsync(bytes, 0, bytes.Length, context.RequestAborted);
                await context.ResponseBody.FlushAsync(context.RequestAborted);
                if (sequence < 5) await Task.Delay(1000, context.RequestAborted);
            }
        }
    }
}
