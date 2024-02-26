using System;

namespace CoAPNet.Dtls.Server
{
    /// <summary>
    /// The configuration for the DTLS server
    /// </summary>
    public class DtlsServerConfig
    {
        /// <summary>
        /// Session timeout (inactivity period) after which a session without a connection id is closed.
        /// </summary>
        public TimeSpan SessionTimeout { get; set; } = TimeSpan.FromHours(1);
        /// <summary>
        /// Session timeout (inactivity period) after which a session with a connection id is closed.
        /// </summary>
        public TimeSpan SessionTimeoutWithCid { get; set; } = TimeSpan.FromHours(1);
        /// <summary>
        /// Number of simultaneous handshakes. If exceeded, server will wait until other handshakes finish before starting new ones.
        /// </summary>
        public int MaxSimultaneousHandshakes { get; set; } = 1000;
    }
}
