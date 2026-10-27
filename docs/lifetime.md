# Lifetime and concurrency

`StartAsync` completes when the listener starts. Its token controls startup, not
the server's entire lifetime. `Completion` represents the worker loops' lifetime.

`StopAsync` stops admitting application work, drains active handlers for 30 seconds
by default, and aborts remaining connections/signals cancellation at the deadline.
While draining, newly accepted requests receive 503. Because HttpListener can't
stop accepts separately from active responses, requests still in the underlying
backlog may instead see a closed connection when draining finishes. A handler
that ignores cancellation can outlive `StopAsync`; observe `Completion` for its
eventual exit. `Dispose` aborts immediately. Use a new server instance to restart.

`RunAsync(lifetimeToken)` remains a convenience for running until cancellation;
that cancellation aborts immediately rather than gracefully draining.

The default 16 asynchronous loops bound concurrent handlers, including long
transfers. Excess traffic waits in the underlying listener backlog. Idle accepts
and asynchronous I/O don't reserve a pool thread. Keep handlers asynchronous;
avoid `.Wait()`, `.Result`, and `Thread.Sleep` so they don't starve other work,
including WCF, in the same process.

