using System.Collections.Generic;

namespace CoAPNet.Dtls.Server.Statistics
{
    public class DtlsStatistics
    {
        public IReadOnlyList<DtlsSessionStatistics> Sessions { get; }
        public IDictionary<string, uint> HandshakesByResult { get; }
        public IDictionary<string, uint> PacketsReceivedByType { get; }
        public uint PacketsSent { get; }

        public DtlsStatistics(
            IReadOnlyList<DtlsSessionStatistics> sessions,
            IDictionary<string, uint> handshakesByResult,
            IDictionary<string, uint> packetsReceivedByType,
            uint packetsSent)
        {
            Sessions = sessions;
            HandshakesByResult = handshakesByResult;
            PacketsReceivedByType = packetsReceivedByType;
            PacketsSent = packetsSent;
        }
    }
}
