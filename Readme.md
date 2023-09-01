# CoAP.Net  [![NuGet](https://img.shields.io/nuget/v/patagona.CoAPNet.svg)](https://www.nuget.org/packages/patagona.CoAPNet/) [![license](https://img.shields.io/github/license/patagonaa/CoAP.Net.svg)](https://github.com/patagonaa/CoAP.Net/blob/master/LICENSE) 

## About

This is a fork of [NZSmartie/CoAP.Net](https://github.com/NZSmartie/CoAP.Net) which adds DTLS client/server support and other bugfixes/improvements/features.

This library is a transport agnostic implementation of the Constraint Application Protocol (CoAP - RFC 7252) for .NET Standard.

Since CoAP is designed for unreliable transport layers. (6LoWPAN, UDP, etc...) it made sense to not worry about the transport implementations and allow the application to provide their own.

If you're after a UDP transport example, see [CoAPNet.Udp](CoAPNet.Udp/)

## Changelog

All relevant changes are logged in [Changelog.md](Changelog.md)

## Status

- Full support for [RFC 7959 - Block-Wise Transfers in the Constrained Application Protocol (CoAP)](https://tools.ietf.org/html/rfc7959)
- Support for sending and receiving Confirmable (CON) and Non-Confirmable (NON) messagees.
  - Retransmit confirmable messages, throwing an exception on failure
  - Rejects malformed messages with appropriate error code.
  - Ignores repeated messages within a set `TimeSpan`
- Support for sending and receiving multicast messages. 
- `CoapServer` - Simple server for binding to local transports and processing requests 
  - `CoapHandler` - Template request handler to be used by CoapServer
    - `CoapResourceHandler` - Example handler that specifically serves `CoapResource`s
- Support for DTLS over UDP

### Todo

 - Create unit tests to cover as much of RFC7252 as possible.
 - Create more examples
 - Await message response for non-confirmable (NON) messages
 - Support for [RFC 7641 - Observing Resources in the Constrained Application Protocol (CoAP)](https://tools.ietf.org/html/rfc7641)
 - Support for [RFC 7390 - Group Communication for the Constrained Application Protocol (CoAP)](https://tools.ietf.org/html/rfc7390)

## Examples

### [SimpleServer](samples/SimpleServer/Program.cs)

Starts a CoAP server listening on all network interfaces and listens for multicast requests.

### [SimpleClient](samples/SimpleClient/Program.cs)

Sends a GET `/hello` request to localhost and prints the response resource.

> Note: Run SimpleServer and then run SimpleClient to see both in action

### [Multicast Discovery](samples/Multicast/Program.cs)

Will send a multicast GET `/.well-known/core` request every minute and prints responses received.