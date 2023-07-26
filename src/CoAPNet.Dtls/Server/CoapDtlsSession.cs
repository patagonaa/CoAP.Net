using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using CoAPNet.Dtls.Server.Statistics;
using Org.BouncyCastle.Tls;

namespace CoAPNet.Dtls.Server
{
    internal class CoapDtlsSession : ICoapIpEndpointInfo, IDtlsSession
    {
        private readonly QueueDatagramTransport _udpTransport;
        private readonly CancellationToken _cancelToken;
        /// <summary>
        /// This semaphore is released whenever a packet has arrived or has been processed.
        /// This way, we don't have to have a thread for each connection just waiting for a packet to arrive.
        /// Instead, we can asynchronously wait for the semaphore to be released by an arriving packet.
        /// </summary>
        private SemaphoreSlim? _packetsReceivedSemaphore;
        private DtlsTransport? _dtlsTransport;

        public CoapDtlsSession(
            IPEndPoint endPoint,
            int networkMtu,
            Action<UdpSendPacket> sendAction,
            DateTime sessionStartTime)
        {
            EndPoint = endPoint ?? throw new ArgumentNullException(nameof(endPoint));

            _udpTransport = new QueueDatagramTransport(networkMtu, bytes => sendAction(new UdpSendPacket(bytes, EndPoint)), ep => ProposeNewEndPoint(ep));
            SessionStartTime = sessionStartTime;

            // we just use the DatagramTransport's token source here because BouncyCastle may call
            // DatagramTransport.Close() internally and we need to close the session when that happens
            _cancelToken = _udpTransport.ClosedToken;
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
                EndPoint = PendingEndPoint;
                PendingEndPoint = null;
            }
        }

        public IPEndPoint EndPoint { get; private set; }
        private IPEndPoint? PendingEndPoint { get; set; }
        public IReadOnlyDictionary<string, object>? ConnectionInfo { get; private set; }

        public bool IsMulticast => false;

        public DateTime SessionStartTime { get; }
        public DateTime LastReceivedTime { get; private set; }
        public byte[]? ConnectionId { get; private set; }

        /// <summary>
        /// Close the session, optionally notifying the peer about the session close.
        /// </summary>
        /// <param name="notifyPeer">Should the peer be notified of the closing session?</param>
        /// <exception cref="ObjectDisposedException">The transport has already been disposed</exception>
        public void Close(bool notifyPeer)
        {
            // if we don't want to notify the peer, we close the DatagramTransport before BouncyCastle can send anything
            if (_dtlsTransport == null || !notifyPeer)
            {
                _udpTransport.Close();
            }

            // closes DatagramTransport internally
            _dtlsTransport?.Close();
        }

        public void Dispose()
        {
            try
            {
                Close(false);
            }
            catch (ObjectDisposedException)
            {
            }
            _udpTransport.Dispose();
            _packetsReceivedSemaphore?.Dispose();
        }

        public async Task<CoapPacket> ReceiveAsync(CancellationToken outerToken)
        {
            if (_dtlsTransport == null || _packetsReceivedSemaphore == null)
                throw new InvalidOperationException("Session must be established before sending/receiving any data.");

            using var cancelSource = CancellationTokenSource.CreateLinkedTokenSource(outerToken, _cancelToken);
            var bufLen = _dtlsTransport.GetReceiveLimit();
            var buffer = new byte[bufLen];
            while (true)
            {
                cancelSource.Token.ThrowIfCancellationRequested();

                // First handle packets left in BouncyCastle's internal receive-queue
                int receivedPending = _dtlsTransport.ReceivePending(buffer, 0, bufLen, RecordCallback);
                if (receivedPending > 0)
                {
                    var payload = new byte[receivedPending];
                    Array.Copy(buffer, payload, receivedPending);
                    return new CoapPacket
                    {
                        Payload = payload,
                        Endpoint = this
                    };
                }
                else
                {
                    // if all packets queued in QueueDatagramTransport have been processed (semaphore count is 0), wait asynchronously until another packet arrives
                    await _packetsReceivedSemaphore.WaitAsync(cancelSource.Token);

                    var receiveTimeout = 1; // we don't want to block here, 0 means never timeout, so we just wait 1ms
                    int received = _dtlsTransport.Receive(buffer, 0, bufLen, receiveTimeout, RecordCallback);
                    if (received > 0)
                    {
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
        }

        public Task SendAsync(CoapPacket packet, CancellationToken token)
        {
            if (_dtlsTransport == null)
                throw new InvalidOperationException("Session must be established before sending/receiving any data.");
            _cancelToken.ThrowIfCancellationRequested();
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

            // run Receive() as many times as we have packets in queue
            // first assign Semaphore and then read QueueCount to
            // avoid possible race condition where a packet arrives after QueueCount was read but before Semaphore is assigned.
            _packetsReceivedSemaphore = new SemaphoreSlim(0);
            var queueCount = _udpTransport.QueueCount;
            if (queueCount > 0)
                _packetsReceivedSemaphore.Release(queueCount);

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
