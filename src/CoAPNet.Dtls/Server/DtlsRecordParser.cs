using Org.BouncyCastle.Tls;
using System;

namespace CoAPNet.Dtls.Server
{
    internal class DtlsRecordParser
    {
        public byte[]? GetConnectionId(byte[] packet, int cidLength)
        {
            const int headerCidOffset = 11;

            if (packet.Length >= (headerCidOffset + cidLength) && packet[0] == ContentType.tls12_cid)
            {
                var cid = new byte[cidLength];
                Array.Copy(packet, headerCidOffset, cid, 0, cidLength);
                return cid;
            }

            return null;
        }

        public bool MayBeClientHello(byte[] packet)
        {
            const int recordHeaderLength = 13;
            const int handshakeMessageHeaderLength = 12;

            if (packet.Length < recordHeaderLength + handshakeMessageHeaderLength)
                return false;

            var contentType = packet[0];
            if (contentType != ContentType.handshake)
                return false;
            if (packet[recordHeaderLength] == HandshakeType.client_hello)
                return true;

            return false;
        }
    }
}
