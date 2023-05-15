using System;
using System.Collections.Generic;

namespace CoAPNet.Dtls.Server.Statistics
{
    public class DtlsSessionStatistics
    {
        public string EndPoint { get; }
        public IReadOnlyDictionary<string, object>? ConnectionInfo { get; }
        public DateTime SessionStartTime { get; }
        public DateTime LastReceivedTime { get; }

        public DtlsSessionStatistics(string endPoint, IReadOnlyDictionary<string, object>? connectionInfo, DateTime sessionStartTime, DateTime lastReceivedTime)
        {
            EndPoint = endPoint;
            ConnectionInfo = connectionInfo;
            SessionStartTime = sessionStartTime;
            LastReceivedTime = lastReceivedTime;
        }
    }
}
