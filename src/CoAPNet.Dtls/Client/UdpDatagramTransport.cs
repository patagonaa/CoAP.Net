﻿using System;
using System.Net;
using System.Net.Sockets;
using Org.BouncyCastle.Tls;

namespace CoAPNet.Dtls.Client
{
    /// <summary>
    /// ported over from https://github.com/bcgit/bc-java/blob/master/tls/src/main/java/org/bouncycastle/tls/UDPTransport.java
    /// </summary>
    public class UdpDatagramTransport : DatagramTransport
    {
        private const int MIN_IP_OVERHEAD = 20;
        private const int MAX_IP_OVERHEAD = MIN_IP_OVERHEAD + 64;
        private const int UDP_OVERHEAD = 8;

        private readonly UdpClient _socket;
        private readonly int _receiveLimit;
        private readonly int _sendLimit;

        public UdpDatagramTransport(UdpClient socket, int mtu)
        {
            this._socket = socket;

            this._receiveLimit = mtu - MIN_IP_OVERHEAD - UDP_OVERHEAD;
            this._sendLimit = mtu - MAX_IP_OVERHEAD - UDP_OVERHEAD;
        }

        public int GetReceiveLimit()
        {
            return _receiveLimit;
        }

        public int GetSendLimit()
        {
            return _sendLimit;
        }
        public int Receive(byte[] buf, int off, int len, int waitMillis) => Receive(buf.AsSpan(off, len), waitMillis);
        public int Receive(Span<byte> buffer, int waitMillis)
        {
            IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.IPv6Any, 0);
            if (_socket.Client != null)
                _socket.Client.ReceiveTimeout = waitMillis;
            var data = _socket.Receive(ref remoteEndPoint);

            var readLen = Math.Min(buffer.Length, data.Length);
            data.AsSpan(0, readLen).CopyTo(buffer);
            return readLen;
        }

        public void Send(byte[] buf, int off, int len) => Send(buf.AsSpan(off, len));
        public void Send(ReadOnlySpan<byte> buffer)
        {
#if NETSTANDARD2_0
            _socket.Send(buffer.ToArray(), buffer.Length);
#else
            _socket.Send(buffer);
#endif
        }

        public void Close()
        {
            _socket.Close();
        }
    }
}
