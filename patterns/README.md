# Application patterns

Small application-owned adapters built on MiniHttpHost routes and streams.
Serializer and protocol dependencies belong in the pattern application.

- [SOAP compatibility and streaming responses](HttpPatterns/README.md): runnable
  examples targeting .NET Framework 4.7.2 and .NET 10.
- [Protobuf message dispatch](protobuf-dispatch/README.md): runnable typed-message
  server, client, wire schema, and smoke checks.

Run the examples with:

```sh
dotnet run --project patterns/HttpPatterns -f net10.0
```

On Windows, build and run the `net472/HttpPatterns.exe` executable instead.
The default prefix is `http://localhost:8081/`; pass another prefix as an argument.

