using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Org.BouncyCastle.Tls;

namespace CoAPNet.Dtls.Client
{
    public class CoapDtlsClientEndPoint : ICoapClientEndpoint, ICoapEndpointInfo
    {
        private const int NetworkMtu = 1500;

        private readonly TlsClient _tlsClient;
        private readonly SemaphoreSlim _ensureConnectedSemaphore = new SemaphoreSlim(1, 1);

        private UdpDatagramTransport? _udpTransport;
        private DtlsTransport? _dtlsTransport;
        private bool _isConnected = false;
        private Task _connectTask;

        public CoapDtlsClientEndPoint(string server, int port, TlsClient tlsClient)
        {
            Server = server;
            Port = port;
            _tlsClient = tlsClient;
        }

        public bool IsMulticast => false;
        public string Server { get; }
        public int Port { get; }

        public async Task<CoapPacket> ReceiveAsync(CancellationToken token)
        {
            await EnsureConnected(token);

            if (_dtlsTransport == null)
                throw new InvalidOperationException("Session must be established before sending/receiving any data.");

            var bufLen = _dtlsTransport.GetReceiveLimit();
            var buffer = new byte[bufLen];

            // we use a long running task here so we don't block the calling thread till we're done waiting, but start a new one and yield instead
            return await Task.Factory.StartNew(() =>
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    // we can't cancel waiting for a packet (BouncyCastle doesn't support this), so there will be a bit of delay between cancelling and actually stopping trying to receive.
                    // there is a wait timeout of 5000ms to close the CoapEndPoint, this has to be less than that.
                    int received = _dtlsTransport.Receive(buffer, 0, bufLen, 1000);

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

        public async Task SendAsync(CoapPacket packet, CancellationToken token)
        {
            await EnsureConnected(token);
            if (packet.Endpoint != this)
                throw new InvalidOperationException("Endpoint can only send its own packets");
            if (_dtlsTransport == null)
                throw new InvalidOperationException("Session must be established before sending/receiving any data.");

            var bytes = packet.Payload;
            _dtlsTransport.Send(bytes, 0, bytes.Length);
        }

        private async Task EnsureConnected(CancellationToken token)
        {
            if (_isConnected)
                return;

            await _ensureConnectedSemaphore.WaitAsync(token);
            try
            {
                if (_connectTask == null)
                    _connectTask = Task.Factory.StartNew(() => Connect(), TaskCreationOptions.LongRunning);

                // this is basically doing "await _connectTask.WaitAsync(token);", but that isn't available in .NET Standard 2.0
                await Task.WhenAny(_connectTask, Task.Delay(Timeout.Infinite, token));
                token.ThrowIfCancellationRequested();
                await _connectTask;
            }
            finally
            {
                _ensureConnectedSemaphore.Release();
            }
        }

        private void Connect()
        {
            var udpClient = new UdpClient(Server, Port);

            var dtlsClientProtocol = new DtlsClientProtocol();
            _udpTransport = new UdpDatagramTransport(udpClient, NetworkMtu);
            _dtlsTransport = dtlsClientProtocol.Connect(_tlsClient, _udpTransport);

            _isConnected = true;
        }

        public override string ToString() => $"udp+dtls://{Server}:{Port}";

        public void Dispose()
        {
            if (_dtlsTransport != null)
            {
                _dtlsTransport.Close();
            }
            else
            {
                _udpTransport?.Close();
            }

            if (_connectTask != null)
            {
                if (!_connectTask.Wait(5000))
                    throw new TimeoutException($"Timeout while trying to dispose {nameof(CoapDtlsClientEndPoint)}");
            }
        }

        public Task<ICoapEndpointInfo> GetEndpointInfoFromMessage(CoapMessage message)
        {
            return Task.FromResult<ICoapEndpointInfo>(this);
        }
    }
}
