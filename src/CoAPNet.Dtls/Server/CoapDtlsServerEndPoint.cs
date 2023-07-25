using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace CoAPNet.Dtls.Server
{
    public class CoapDtlsServerEndPoint : ICoapEndpoint
    {
        public Uri BaseUri { get; }
        public IPEndPoint IPEndPoint { get; }

        public CoapDtlsServerEndPoint(IPAddress? address = null, int port = Coap.PortDTLS)
        {
            address = address ?? IPAddress.IPv6Any;

            BaseUri = new UriBuilder()
            {
                Scheme = "coaps://",
                Host = address.ToString(),
                Port = port
            }.Uri;

            IPEndPoint = new IPEndPoint(address, port);
        }

        public void Dispose()
        {
        }

        public async Task SendAsync(CoapPacket packet, CancellationToken token)
        {
            //packet has the Session we have to respond to.
            if (packet.Endpoint is not CoapDtlsSession session)
                throw new ArgumentException("Can only send DTLS packets");

            await session.SendAsync(packet, token);
        }

        public override string ToString() => $"udp+dtls://{IPEndPoint}";
    }
}
