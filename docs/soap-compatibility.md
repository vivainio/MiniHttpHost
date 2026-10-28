# A SOAP compatibility POST endpoint

A runnable [SOAP 1.1 pattern](https://github.com/vivainio/MiniHttpHost/tree/main/patterns/HttpPatterns)
shows explicit action/body dispatch, a typed greeting reply, and SOAP faults.

This is an application integration idea, not a SOAP implementation in MiniHttpHost.
An existing client already generates SOAP; it could change its destination to one
ordinary POST endpoint while keeping that serializer and message types.

```csharp
routes.MapPost("/legacy-message", soapAdapter.HandleAsync,
    new HttpRouteOptions {
        MaxRequestBodyBytes = 16L * 1024 * 1024,
        Timeout = TimeSpan.FromSeconds(30)
    });
```

`soapAdapter` is application-owned code. Its job would be to:

1. Read the envelope from `context.RequestBody` with an XML reader.
2. Select a registered handler from the SOAP action or body element's namespace/name.
3. Deserialize the expected, registered message type.
4. Invoke an ordinary instance method using the application's existing services.
5. Return the SOAP response or fault shape the client expects.

The transport stays an ordinary HTTP POST. This need not recreate ServiceHost,
WCF dispatch behaviors, or its instance/session model. One-way messages are simpler
because there is no typed reply to reproduce; the expected HTTP acknowledgment
still needs to match the client.

## Reuse framework types

On .NET Framework, `System.ServiceModel.Channels.Message.CreateMessage` can read
an envelope using a chosen `MessageVersion`. `DataContractSerializer` and
`XmlSerializer` can reuse the corresponding DTO contracts. These types can be used
by the application adapter without adding them to MiniHttpHost's core API.

The framework serializers are not interchangeable: match the serializer used by
the existing operation. Also match wrapper elements, parameter order, namespaces,
SOAP action, and response/fault conventions. A SOAP envelope's body is not always
just a serialized DTO at its root.

For `basicHttpBinding`, a narrow first target would be SOAP 1.1 with text encoding.
A client using a different binding, message contract, WS-Addressing destination,
or signed/encrypted messages may need more than changing the URL. SOAP 1.2,
WS-Security, reliable sessions, transactions, and MTOM are separate compatibility
work, not features implied by accepting the envelope.

## Start with a real message pair

Use a captured request and expected response from one existing operation as a
fixture. Verify the same generated client accepts the new endpoint's reply, then
add operations explicitly. Register known actions/types rather than allowing a
payload to select arbitrary CLR types. Apply XML reader quotas, disable DTD/external
entity resolution, and retain the route's overall body limit and cancellation.

Deserializing an object graph still allocates the graph; an XML reader alone does
not make large byte-array DTOs end-to-end streaming. Keep the raw streaming routes
for large binary transfers unless SOAP compatibility is required for them.

References: [BasicHttpBinding](https://learn.microsoft.com/en-us/dotnet/framework/configure-apps/file-schema/wcf/basichttpbinding),
[Message.CreateMessage](https://learn.microsoft.com/en-us/dotnet/api/system.servicemodel.channels.message.createmessage?view=netframework-4.7.2),
and [WCF serializer selection](https://learn.microsoft.com/en-us/dotnet/framework/wcf/feature-details/using-the-xmlserializer-class).
