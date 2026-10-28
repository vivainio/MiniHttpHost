# SOAP compatibility and streaming responses

Run this project as described in the [patterns index](../README.md).

## SOAP 1.1 compatibility

`POST /soap` implements one explicit document/literal operation. It matches both
`SOAPAction` and the operation's XML name before dispatching to the greeting code.
The response uses the corresponding wrapper and result element; invalid XML,
unknown actions, and invalid fields produce a SOAP Client fault with HTTP 500.
Other media types receive HTTP 415. Request bodies are limited to 64 KiB.

```sh
curl -i http://localhost:8081/soap \
  -H 'Content-Type: text/xml; charset=utf-8' \
  -H 'SOAPAction: "urn:minihttp:hello/SayHello"' \
  --data-binary @- <<'XML'
<s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
  <s:Body>
    <SayHello xmlns="urn:minihttp:hello"><name>Ada</name></SayHello>
  </s:Body>
</s:Envelope>
XML
```

The reply contains `SayHelloResponse/SayHelloResult` in `urn:minihttp:hello`,
with the value `Hello, Ada!`. XML construction escapes text automatically.
Change the action to see the fault path.

This is a narrow compatibility example, not a general SOAP stack. It rejects
all nonempty SOAP headers and does not implement SOAP 1.2, WS-Addressing,
WS-Security, MTOM, WSDL publishing, or WCF sessions. When adapting a real service,
match captured requests and replies, then verify them with its generated client.
See the [SOAP compatibility chapter](../../docs/soap-compatibility.md).

## Streaming response records

```sh
curl --no-buffer -i http://localhost:8081/events
```

`GET /events` writes five newline-delimited JSON records, about one second apart.
It sets `SendChunked`, omits a content length, and flushes after each record.
The connection finishes after the fifth record. Cancellation is passed to body
writes, flushes, and delays; write failures propagate to the exception middleware,
which aborts an already-started response.

A write is not a guaranteed HTTP chunk or a client read boundary. Clients should
accumulate bytes and parse complete lines, including when one record spans reads
or several records arrive together. Proxies and clients can buffer despite a
flush. Compression is left disabled for this route to make incremental delivery
easier to observe. Once headers are sent, a failure cannot change the HTTP status;
a production feed can add explicit completion/error records if clients need to
distinguish successful completion from a truncated response.

