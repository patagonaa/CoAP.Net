using System;
using System.Collections.Concurrent;
using System.Net;
using System.Threading;
using Org.BouncyCastle.Tls;

namespace CoAPNet.Dtls.Server
{
    /// <summary>
    /// We need this because the server can't bind to a single endpoint on the server port, so we have to receive packets from all
    /// endpoints and pass them to the corresponsing "connection" / transport by the remote endpoint.
    /// </summary>
    internal class QueueDatagramTransport : DatagramTransport, IDisposable
    {
        private readonly int _receiveLimit;
        private readonly int _sendLimit;
        private readonly Action<byte[]> _sendCallback;
        private readonly Action<IPEndPoint> _receiveCallback;
        private readonly CancellationTokenSource _cts;
        private readonly CancellationToken _cancelToken;
        private readonly BlockingCollection<(byte[] Data, IPEndPoint EndPoint)> _receiveQueue;

        private const int MIN_IP_OVERHEAD = 20;
        private const int MAX_IP_OVERHEAD = MIN_IP_OVERHEAD + 64;
        private const int UDP_OVERHEAD = 8;

        public QueueDatagramTransport(int mtu, Action<byte[]> sendCallback, Action<IPEndPoint> receiveCallback)
        {
            _receiveLimit = mtu - MIN_IP_OVERHEAD - UDP_OVERHEAD;
            _sendLimit = mtu - MAX_IP_OVERHEAD - UDP_OVERHEAD;
            _sendCallback = sendCallback ?? throw new ArgumentNullException(nameof(sendCallback));
            _receiveCallback = receiveCallback ?? throw new ArgumentNullException(nameof(receiveCallback));
            _cts = new CancellationTokenSource();
            _cancelToken = _cts.Token;
            _receiveQueue = new BlockingCollection<(byte[] Data, IPEndPoint EndPoint)>();
        }

        public CancellationToken ClosedToken => _cancelToken;
        public int QueueCount => _receiveQueue.Count;

        public int GetReceiveLimit()
        {
            return _receiveLimit;
        }

        public int GetSendLimit()
        {
            return _sendLimit;
        }

        public void EnqueueReceived(byte[] datagram, IPEndPoint endPoint)
        {
            if (_cancelToken.IsCancellationRequested)
                return;
            _receiveQueue.Add((datagram, endPoint));
        }

        public int Receive(byte[] buf, int off, int len, int waitMillis) => Receive(buf.AsSpan(off, len), waitMillis);
        public int Receive(Span<byte> buffer, int waitMillis)
        {
            _cancelToken.ThrowIfCancellationRequested();
            var success = _receiveQueue.TryTake(out var item, waitMillis, _cancelToken);
            if (!success)
                return -1; // DO NOT return 0. This will disable the wait timeout effectively for the caller and any abort logic will by bypassed!

            _receiveCallback(item.EndPoint);

            var data = item.Data;
            var readLen = Math.Min(buffer.Length, data.Length);
            data.AsSpan(0, readLen).CopyTo(buffer);
            return readLen;
        }

        public void Send(byte[] buf, int off, int len) => Send(buf.AsSpan(off, len));
        public void Send(ReadOnlySpan<byte> buffer)
        {
            _cancelToken.ThrowIfCancellationRequested();
            _sendCallback(buffer.ToArray());
        }

        public void Close()
        {
            _cts.Cancel();
        }

        public void Dispose()
        {
            Close();
            _cts.Dispose();
            _receiveQueue.Dispose();
        }
    }
}
