using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHttpHost
{
    // One instance per run. Cancelling stops accepts and aborts active responses.
    public sealed class MiniWebServer
    {
        private readonly string prefix;
        private readonly Func<HttpListenerContext, CancellationToken, Task> handler;
        private readonly int concurrency;
        private int started;

        public MiniWebServer(string prefix,
            Func<HttpListenerContext, CancellationToken, Task> handler,
            int concurrency = 16)
        {
            if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("A URL prefix is required.", nameof(prefix));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (concurrency < 1) throw new ArgumentOutOfRangeException(nameof(concurrency));
            this.prefix = prefix;
            this.handler = handler;
            this.concurrency = concurrency;
        }

        // Completes after shutdown and all handlers have returned. Handlers must
        // honor cancellation so shutdown is not held up by application work.
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref started, 1) != 0)
                throw new InvalidOperationException("Create a new server for each run.");

            using (var listener = new HttpListener())
            using (var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                listener.Prefixes.Add(prefix);
                listener.Start();
                using (stopping.Token.Register(listener.Abort))
                {
                    // Task.Run selects the default shared thread pool even if the
                    // caller has a UI or other custom synchronization context.
                    var workers = Enumerable.Range(0, concurrency)
                        .Select(_ => Task.Run(() => ServeAsync(listener, stopping)))
                        .ToArray();
                    await Task.WhenAll(workers).ConfigureAwait(false);
                }
            }
        }

        private async Task ServeAsync(HttpListener listener, CancellationTokenSource stopping)
        {
            try
            {
                while (!stopping.IsCancellationRequested)
                {
                    HttpListenerContext context;
                    try
                    {
                        context = await listener.GetContextAsync().ConfigureAwait(false);
                    }
                    catch (HttpListenerException) when (stopping.IsCancellationRequested) { return; }
                    catch (ObjectDisposedException) when (stopping.IsCancellationRequested) { return; }

                    try
                    {
                        await handler(context, stopping.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
                    catch (Exception error)
                    {
                        Trace.TraceError("HTTP handler failed: {0}", error);
                        // Headers might already be sent, so don't try to replace
                        // a partially written response with an error page.
                        context.Response.Abort();
                    }
                    finally
                    {
                        try { context.Response.Close(); }
                        catch (HttpListenerException) { }
                        catch (ObjectDisposedException) { }
                    }
                }
            }
            catch
            {
                // An accept-loop failure must also release the other accepts.
                stopping.Cancel();
                throw;
            }
        }
    }
}
