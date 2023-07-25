using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;

namespace CoAPNet.Dtls.Client
{
    public class CoapDtlsClientEndPoint : ICoapEndpoint
    {
        private const int NetworkMtu = 1500;

        private readonly TlsClient _tlsClient;
        private DtlsTransport? _datagramTransport;
        private bool _isConnected = false;

        public CoapDtlsClientEndPoint(string server, int port, TlsClient tlsClient)
        {
            BaseUri = new UriBuilder()
            {
                Scheme = "coaps://",
                Host = server,
                Port = port
            }.Uri;
            Server = server;
            Port = port;
            _tlsClient = tlsClient;
        }

        public bool IsMulticast => false;

        public Uri BaseUri { get; }
        public string Server { get; }
        public int Port { get; }

        public async Task<CoapPacket> ReceiveAsync(CancellationToken token)
        {
            EnsureConnected();

            if (_datagramTransport == null)
                throw new InvalidOperationException("Session must be established before sending/receiving any data.");

            var bufLen = _datagramTransport.GetReceiveLimit();
            var buffer = new byte[bufLen];

            // we use a long running task here so we don't block the calling thread till we're done waiting, but start a new one and yield instead
            return await Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    // we can't cancel waiting for a packet (BouncyCastle doesn't support this), so there will be a bit of delay between cancelling and actually stopping trying to receive.
                    // there is a wait timeout of 5000ms to close the CoapEndPoint, this has to be less than that.
                    int received = _datagramTransport.Receive(buffer, 0, bufLen, 1000);

                    if (received > 0)
                    {
                        return new CoapPacket
                        {
                            Payload = new ArraySegment<byte>(buffer, 0, received).ToArray(),
                            Endpoint = this
                        };
                    }
                }
            }, TaskCreationOptions.LongRunning);
        }

        public Task SendAsync(CoapPacket packet, CancellationToken token)
        {
            EnsureConnected();

            if (_datagramTransport == null)
                throw new InvalidOperationException("Session must be established before sending/receiving any data.");

            var bytes = packet.Payload;
            _datagramTransport.Send(bytes, 0, bytes.Length);
            return Task.CompletedTask;
        }

        private readonly object _ensureConnectedLock = new object();
        private void EnsureConnected()
        {
            lock (_ensureConnectedLock)
            {
                if (_isConnected)
                    return;

                var udpClient = new UdpClient(Server, Port);

                var dtlsClientProtocol = new DtlsClientProtocol();
                _datagramTransport = dtlsClientProtocol.Connect(_tlsClient, new UdpDatagramTransport(udpClient, NetworkMtu));
                _isConnected = true;
            }
        }

        public string ToString(CoapEndpointStringFormat format)
        {
            return BaseUri.ToString();
        }

        public void Dispose()
        {
            _datagramTransport?.Close();
        }
    }
}
