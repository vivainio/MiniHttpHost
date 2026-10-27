using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MiniHttpHost
{
    /// <summary>A one-shot, embeddable server using the shared managed thread pool.</summary>
    public sealed class MiniWebServer : IDisposable
    {
        private readonly object gate = new object();
        private readonly string prefix;
        private readonly HttpHandler handler;
        private readonly int concurrency;
        private readonly HashSet<HttpContext> active = new HashSet<HttpContext>();
        private readonly CancellationTokenSource abortRequests = new CancellationTokenSource();
        private HttpListener listener;
        private bool started;
        private bool disposed;
        private bool stopping;
        private Task completion = Task.CompletedTask;

        public MiniWebServer(string prefix, HttpHandler handler, int concurrency = 16)
        {
            if (string.IsNullOrWhiteSpace(prefix)) throw new ArgumentException("A URL prefix is required.", nameof(prefix));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (concurrency < 1) throw new ArgumentOutOfRangeException(nameof(concurrency));
            this.prefix = prefix;
            this.handler = handler;
            this.concurrency = concurrency;
        }

        /// <summary>Observe this task for listener failures after StartAsync completes.</summary>
        public Task Completion { get { lock (gate) return completion; } }

        /// <summary>Completes only after the URL is registered and listening.</summary>
        public Task StartAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException(nameof(MiniWebServer));
                if (started) throw new InvalidOperationException("Create a new server for each run.");
                cancellationToken.ThrowIfCancellationRequested();
                started = true;
                listener = new HttpListener();
                try
                {
                    listener.Prefixes.Add(prefix);
                    listener.Start();
                }
                catch { listener.Close(); throw; }
                // No dedicated accept thread; idle accepts don't occupy a pool thread.
                // A host may start us inside a WCF operation. Don't capture its
                // ambient principal/logical call context into long-lived workers.
                if (ExecutionContext.IsFlowSuppressed()) StartWorkers();
                else using (ExecutionContext.SuppressFlow()) StartWorkers();
            }
            return Task.CompletedTask;
        }

        private void StartWorkers()
        {
            var workers = Enumerable.Range(0, concurrency)
                .Select(_ => Task.Run(ServeAsync)).ToArray();
            completion = FinishAsync(workers);
            // Ensure failures are observed even if a host forgets to monitor Completion.
            completion.ContinueWith(t => Trace.TraceError("HTTP listener failed: {0}", t.Exception),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }

        /// <summary>
        /// Stops admitting application work and drains requests for up to gracePeriod
        /// (30 seconds by default). On expiry, aborts connections and signals request
        /// cancellation. Handlers ignoring cancellation may outlive this method;
        /// Completion remains available to observe their eventual completion.
        /// </summary>
        public async Task StopAsync(TimeSpan? gracePeriod = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            var grace = gracePeriod ?? TimeSpan.FromSeconds(30);
            if (grace < TimeSpan.Zero || grace.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(gracePeriod));
            bool idle;
            Task pending;
            lock (gate)
            {
                if (!started) return;
                stopping = true;
                idle = active.Count == 0;
                pending = completion;
            }
            // HttpListener cannot stop accepts independently of its active responses.
            // Keep it open while draining; reject newly accepted requests with 503.
            if (idle) CloseListener();
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                var delay = Task.Delay(grace, deadline.Token);
                if (await Task.WhenAny(pending, delay).ConfigureAwait(false) == pending)
                {
                    deadline.Cancel();
                    await pending.ConfigureAwait(false);
                }
                else
                {
                    Abort();
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }
        }

        /// <summary>Convenience lifetime: cancellation immediately aborts requests.</summary>
        public async Task RunAsync(CancellationToken cancellationToken)
        {
            await StartAsync(cancellationToken).ConfigureAwait(false);
            using (cancellationToken.Register(Abort))
                await Completion.ConfigureAwait(false);
        }

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    lock (gate) { if (stopping) return; }
                    HttpListenerContext native;
                    try { native = await listener.GetContextAsync().ConfigureAwait(false); }
                    catch (HttpListenerException) when (IsStopping()) { return; }
                    catch (ObjectDisposedException) when (IsStopping()) { return; }
                    catch (InvalidOperationException) when (IsStopping()) { return; }

                    HttpContext context;
                    lock (gate)
                    {
                        // Serialize admission/context construction with StopAsync.
                        // Otherwise an idle stop could dispose the native response
                        // while this worker was obtaining its body streams.
                        context = stopping ? null : new HttpContext(native, abortRequests.Token);
                        if (context != null) active.Add(context);
                    }
                    if (context == null)
                    {
                        try
                        {
                            native.Response.StatusCode = 503;
                            native.Response.ContentLength64 = 0;
                            native.Response.KeepAlive = false;
                            native.Response.Close();
                        }
                        catch (HttpListenerException) { }
                        catch (ObjectDisposedException) { }
                        catch (IOException) { }
                        return;
                    }
                    try
                    {
                        await handler(context).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        try { await HttpResponses.HandleErrorAsync(context, error).ConfigureAwait(false); }
                        catch (Exception writeError)
                        {
                            Trace.TraceError("Failed to finish HTTP error response: {0}", writeError);
                            context.Abort();
                        }
                    }
                    finally
                    {
                        try { native.Response.Close(); }
                        catch (HttpListenerException) { }
                        catch (ObjectDisposedException) { }
                        catch (IOException) { }
                        finally
                        {
                            bool drained;
                            lock (gate)
                            {
                                active.Remove(context);
                                drained = stopping && active.Count == 0;
                            }
                            if (drained) CloseListener();
                        }
                    }
                }
            }
            catch { Abort(); throw; }
        }

        private async Task FinishAsync(Task[] workers)
        {
            try { await Task.WhenAll(workers).ConfigureAwait(false); }
            finally { CloseListener(); }
        }

        private bool IsStopping() { lock (gate) return stopping; }

        private void CloseListener()
        {
            try { listener?.Abort(); }
            catch (ObjectDisposedException) { }
            catch (HttpListenerException) { }
        }

        private void Abort()
        {
            HttpContext[] requests;
            lock (gate)
            {
                stopping = true;
                requests = active.ToArray();
            }
            try { abortRequests.Cancel(); }
            catch (AggregateException error) { Trace.TraceError("Cancellation callback failed: {0}", error); }
            catch (ObjectDisposedException) { }
            foreach (var request in requests) request.Abort();
            CloseListener();
        }

        /// <summary>Immediately aborts connections. Use StopAsync for graceful shutdown.</summary>
        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
            }
            Abort();
            // Keep cancellation state alive while application handlers unwind.
            Completion.ContinueWith(_ => abortRequests.Dispose(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
