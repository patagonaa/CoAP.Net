using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CoAPNet.Dtls.Server.Statistics;
using Org.BouncyCastle.Tls;

namespace CoAPNet.Dtls.Server
{
    internal class CoapDtlsServerClientEndPoint : ICoapEndpoint
    {
        private readonly QueueDatagramTransport _udpTransport;
        private readonly Action<IPEndPoint, IPEndPoint> _replaceEndpointAction;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        /// <summary>
        /// This semaphore is released whenever a packet has arrived or has been processed.
        /// This way, we don't have to have a thread for each connection just waiting for a packet to arrive.
        /// Instead, we can asynchronously wait for the semaphore to be released by an arriving packet.
        /// </summary>
        private SemaphoreSlim? _packetsReceivedSemaphore;
        private DtlsTransport? _dtlsTransport;

        public CoapDtlsServerClientEndPoint(
            IPEndPoint endPoint,
            int networkMtu,
            Action<UdpSendPacket> sendAction,
            Action<IPEndPoint, IPEndPoint> replaceEndpointAction,
            DateTime sessionStartTime)
        {
            EndPoint = endPoint ?? throw new ArgumentNullException(nameof(endPoint));
            _replaceEndpointAction = replaceEndpointAction;
            BaseUri = new UriBuilder()
            {
                Scheme = "coaps://",
                Host = endPoint.Address.ToString(),
                Port = endPoint.Port
            }.Uri;

            _udpTransport = new QueueDatagramTransport(networkMtu, bytes => sendAction(new UdpSendPacket(bytes, EndPoint)), ep => ProposeNewEndPoint(ep));
            SessionStartTime = sessionStartTime;
        }

        private void ProposeNewEndPoint(IPEndPoint ep)
        {
            if (!ep.Equals(EndPoint))
            {
                PendingEndPoint = ep;
            }
        }

        private void RecordCallback(DtlsRecordFlags recordFlags)
        {
            if (PendingEndPoint != null && recordFlags.HasFlag(DtlsRecordFlags.IsNewest) && recordFlags.HasFlag(DtlsRecordFlags.UsesConnectionID))
            {
                var oldEndPoint = EndPoint;
                EndPoint = PendingEndPoint;
                _replaceEndpointAction(oldEndPoint, PendingEndPoint);
                PendingEndPoint = null;
            }
        }

        public IPEndPoint EndPoint { get; private set; }
        private IPEndPoint? PendingEndPoint { get; set; }

        public Uri BaseUri { get; }
        public IReadOnlyDictionary<string, object>? ConnectionInfo { get; private set; }

        public bool IsSecure => true;

        public bool IsMulticast => false;

        public DateTime SessionStartTime { get; }
        public DateTime LastReceivedTime { get; private set; }
        public bool IsClosed { get; private set; }
        public byte[]? ConnectionId { get; private set; }

        public void Dispose()
        {
            IsClosed = true;
            _dtlsTransport?.Close();
            _cts.Cancel();
            _cts.Dispose();
        }

        public async Task<CoapPacket> ReceiveAsync(CancellationToken outerToken)
        {
            if (_dtlsTransport == null || _packetsReceivedSemaphore == null)
                throw new InvalidOperationException("Session must be established before sending/receiving any data.");

            if (IsClosed || _udpTransport.IsClosed || _dtlsTransport == null)
                throw new DtlsConnectionClosedException();

            using var cancelSource = CancellationTokenSource.CreateLinkedTokenSource(outerToken, _cts.Token);
            var bufLen = _dtlsTransport.GetReceiveLimit();
            var buffer = new byte[bufLen];
            while (true)
            {
                if (IsClosed || _udpTransport.IsClosed || _dtlsTransport == null)
                    throw new DtlsConnectionClosedException();

                cancelSource.Token.ThrowIfCancellationRequested();

                // if all queued packets have been processed (semaphore count is 0), wait asynchronously until another packet arrives
                await _packetsReceivedSemaphore.WaitAsync(cancelSource.Token);

                var receiveTimeout = 1; // we don't want to block here, 0 means never timeout, so we just wait 1ms
                int received = _dtlsTransport.Receive(buffer, 0, bufLen, receiveTimeout, RecordCallback);
                if (received > 0)
                {
                    // One packet may contain multiple records.
                    // For that case, we want to run Receive() another time to make sure BouncyCastle's internal receive-queue is empty.
                    SignalPossibleReceivedPacket();

                    var payload = new byte[received];
                    Array.Copy(buffer, payload, received);
                    return new CoapPacket
                    {
                        Payload = payload,
                        Endpoint = this
                    };
                }
            }
        }

        public Task SendAsync(CoapPacket packet, CancellationToken token)
        {
            if (_dtlsTransport == null)
                throw new InvalidOperationException("Session must be established before sending/receiving any data.");

            if (!_udpTransport.IsClosed)
                _dtlsTransport.Send(packet.Payload, 0, packet.Payload.Length);
            return Task.CompletedTask;
        }

        public override string ToString()
        {
            return ToString(CoapEndpointStringFormat.Simple);
        }

        public string ToString(CoapEndpointStringFormat format)
        {
            return EndPoint.ToString();
        }

        public void Accept(DtlsServerProtocol serverProtocol, TlsServer server)
        {
            _dtlsTransport = serverProtocol.Accept(server, _udpTransport);

            // Allow Receive() to run at least once in case there is still a record in BouncyCastle's record queue
            // This might be necessary if there was an "application_data" record transmitted together with the handshake "finished" record
            var initialReceiveRecordsCount = Math.Min(_udpTransport.QueueCount, 1);
            _packetsReceivedSemaphore = new SemaphoreSlim(initialReceiveRecordsCount);

            if (server is IDtlsServerWithConnectionId serverWithCid)
            {
                ConnectionId = serverWithCid.GetConnectionId();
            }

            if (server is IDtlsServerWithConnectionInfo serverWithInfo)
            {
                var serverInfo = serverWithInfo.GetConnectionInfo();
                ConnectionInfo = serverInfo;
            }
        }

        public void EnqueueDatagram(byte[] datagram, IPEndPoint endPoint)
        {
            _udpTransport.EnqueueReceived(datagram, endPoint);
            SignalPossibleReceivedPacket();
            LastReceivedTime = DateTime.UtcNow;
        }

        private void SignalPossibleReceivedPacket()
        {
            _packetsReceivedSemaphore?.Release();
        }

        public DtlsSessionStatistics GetSessionStatistics()
        {
            return new DtlsSessionStatistics(EndPoint.ToString(), ConnectionInfo, SessionStartTime, LastReceivedTime, ConnectionId != null);
        }
    }
}
