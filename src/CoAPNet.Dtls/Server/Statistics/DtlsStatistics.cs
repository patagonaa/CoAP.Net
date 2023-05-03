using System.Collections.Generic;

namespace CoAPNet.Dtls.Server.Statistics
{
    public class DtlsStatistics
    {
        public IReadOnlyList<DtlsSessionStatistics> Sessions { get; internal set; }
        public IDictionary<string, uint> HandshakesByResult { get; internal set; }
        public IDictionary<string, uint> PacketsReceivedByType { get; internal set; }
        public uint PacketsSent { get; internal set; }
    }
}
