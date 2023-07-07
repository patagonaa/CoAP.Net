using Org.BouncyCastle.Tls;

namespace CoAPNet.Dtls.Server
{
    /// <summary>
    /// A TLS server that may negotiate a connection id
    /// </summary>
    public interface IDtlsServerWithConnectionId : TlsServer
    {
        /// <summary>
        /// Returns the connection id (or <see langword="null"/>) once the session has been established.
        /// Generating the cid should be done in <see cref="AbstractTlsServer.GetNewConnectionID"/> instead.
        /// </summary>
        /// <returns></returns>
        byte[]? GetConnectionId();
    }
}
