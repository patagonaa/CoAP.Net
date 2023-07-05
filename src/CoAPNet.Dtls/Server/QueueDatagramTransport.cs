﻿using System;
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
    internal class QueueDatagramTransport : DatagramTransport
    {
        private readonly int _receiveLimit;
        private readonly int _sendLimit;
        private readonly Action<byte[]> _sendCallback;
        private readonly Action<IPEndPoint> _receiveCallback;
        private readonly CancellationTokenSource _cts;
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
            _receiveQueue = new BlockingCollection<(byte[] Data, IPEndPoint EndPoint)>();
        }

        public bool IsClosed { get; private set; }
        public ReaderWriterLockSlim CloseLock { get; } = new ReaderWriterLockSlim();

        public void Close()
        {
            // Cancel this before locking so the lock gets released ASAP
            _cts.Cancel();

            CloseLock.EnterWriteLock();
            IsClosed = true;
            _receiveQueue.Dispose();
            CloseLock.ExitWriteLock();
        }

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
            try
            {
                CloseLock.EnterReadLock();
                if (IsClosed)
                    return;
                _receiveQueue.Add((datagram, endPoint));
            }
            finally
            {
                CloseLock.ExitReadLock();
            }
        }

        public int Receive(byte[] buf, int off, int len, int waitMillis) => Receive(buf.AsSpan(off, len), waitMillis);
        public int Receive(Span<byte> buffer, int waitMillis)
        {
            try
            {
                CloseLock.EnterReadLock();
                if (IsClosed)
                    throw new DtlsConnectionClosedException();

                var success = _receiveQueue.TryTake(out var item, waitMillis, _cts.Token);
                if (!success)
                    return -1; // DO NOT return 0. This will disable the wait timeout effectively for the caller and any abort logic will by bypassed!

                _receiveCallback(item.EndPoint);

                var data = item.Data;
                var readLen = Math.Min(buffer.Length, data.Length);
                data.AsSpan(0, readLen).CopyTo(buffer);
                return readLen;
            }
            catch (OperationCanceledException)
            {
                return -1; // DO NOT return 0. This will disable the wait timeout effectively for the caller and any abort logic will by bypassed!
            }
            finally
            {
                CloseLock.ExitReadLock();
            }
        }

        public void Send(byte[] buf, int off, int len) => Send(buf.AsSpan(off, len));
        public void Send(ReadOnlySpan<byte> buffer)
        {
            try
            {
                CloseLock.EnterReadLock();
                if (IsClosed)
                    throw new DtlsConnectionClosedException(); // throw is important here, so DtlsServer.Accept() throws when the connection is closed and the client doesn't answer
                _sendCallback(buffer.ToArray());
            }
            finally
            {
                CloseLock.ExitReadLock();
            }
        }
    }
}
