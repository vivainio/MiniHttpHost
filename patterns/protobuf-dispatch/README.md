# Protobuf over HTTP with message dispatch

A runnable client and server using `POST /messages`, with two request types and
an explicit typed-handler registry. This is ordinary HTTP with an application
protocol; it does not use gRPC. The protobuf-net dependency is confined to this
sample project; MiniHttpHost keeps its dependency-free core.

## Run

Start the server:

```sh
dotnet run --project patterns/protobuf-dispatch -f net10.0 -- server
```

In another terminal, run the client:

```sh
dotnet run --project patterns/protobuf-dispatch -f net10.0 -- client
```

The client gets customer 1 (`Ada`), updates the name to `Grace`, and gets it again.
State lives in a concurrent dictionary and resets when the server restarts.
Press Enter in the server terminal to stop. Both commands accept a prefix after
`server` or `client`; the default is `http://localhost:8082/`.

For .NET Framework on Windows:

```sh
dotnet build patterns/protobuf-dispatch -c Release
patterns\protobuf-dispatch\bin\Release\net472\ProtobufDispatch.exe server
```

Run that executable with `client` in another terminal. The project also works with
Mono using the Framework build. Restore requires access to NuGet.

## Follow the dispatch

1. `MessageClient` serializes the typed request into an envelope payload, sets a
   stable discriminator and a new correlation ID, and posts `application/protobuf`.
2. `MessageEndpoint` reads the route-limited request asynchronously, decodes the
   envelope, and looks up the discriminator in an explicit registry.
3. The registered delegate decodes its specific request type, calls the ordinary
   typed handler, and encodes a typed reply. Unknown discriminators are rejected
   before payload decoding. No caller-selected CLR types or reflection dispatch.
4. The reply carries the original correlation ID. The client checks the media
   type, correlation ID, HTTP status, and expected reply discriminator before
   returning the decoded customer.

| Request discriminator | Request payload | Reply discriminator | Reply payload |
| --- | --- | --- | --- |
| `customer.get.v1` | `GetCustomer` | `customer.v1` | `Customer` |
| `customer.update.v1` | `UpdateCustomer` | `customer.v1` | `Customer` |
| Any application error | — | `error.v1` | `Error` |

The [wire schema](messages.proto) documents the field numbers used by
[Contracts.cs](Contracts.cs). C# uses protobuf-net attributes, so no code-generation
step is required. Keep field numbers stable; reserve retired fields instead of
reusing their numbers. Wire names are independent of CLR names. Both operations
require a positive ID; updates require a nonblank name of at most 200 characters
and an existing customer.

## Errors and bounds

| HTTP status | Error code | Meaning |
| --- | --- | --- |
| 400 | `malformed_envelope` | Outer protobuf could not be decoded |
| 400 | `invalid_envelope` | Missing type or invalid correlation ID |
| 400 | `unknown_message_type` | No registered handler |
| 400 | `malformed_payload` | Selected request payload could not be decoded |
| 400 | `invalid_customer` | Decoded request failed application validation |
| 404 | `customer_not_found` | Customer does not exist |
| 415 | `unsupported_media_type` | Expected `application/protobuf` |

These errors use a protobuf `error.v1` envelope. A valid supplied correlation ID
is echoed when the outer envelope was decoded; otherwise it is empty. IDs are
required and limited to 128 characters. Missing protobuf fields use their default
values, then undergo application validation.

The route caps the complete request at 64 KiB and has a ten-second timeout.
Transport errors such as HTTP 413, timeouts, routing failures, and unexpected
server errors are handled by MiniHttpHost and need not contain protobuf envelopes.
The client handles non-protobuf HTTP failures before decoding a reply.
Serialization buffers these small messages; this pattern is not a large-file
streaming example. The local demo client bypasses proxy discovery.

## Check

With port 8082 free, run:

```sh
dotnet run --project patterns/protobuf-dispatch -f net10.0 -- check
```

This starts a fresh server, exercises both typed operations, checks correlated
error replies, unknown dispatch before payload parsing, malformed envelope and
payload handling, field validation, missing customers, content type, and the
request-size limit, then shuts down. A failed check exits unsuccessfully.

Use one route per operation when HTTP routing already supplies the dispatch you
need. A shared endpoint is useful when an integration already exchanges typed
messages. The registry is configured once before startup; the sample's synchronous
handlers can be replaced by async delegates when application work needs async I/O.

Serializer reference: [protobuf-net](https://github.com/protobuf-net/protobuf-net).
