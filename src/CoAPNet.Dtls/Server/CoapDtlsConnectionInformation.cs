using Org.BouncyCastle.Tls;

namespace CoAPNet.Dtls.Server
{
    public class CoapDtlsConnectionInformation : ICoapConnectionInformation
    {
        public CoapDtlsConnectionInformation(ICoapEndpoint localEndpoint, ICoapEndpointInfo remoteEndpoint, TlsServer tlsServer)
        {
            LocalEndpoint = localEndpoint;
            RemoteEndpoint = remoteEndpoint;
            TlsServer = tlsServer;
        }

        public ICoapEndpoint LocalEndpoint { get; }
        public ICoapEndpointInfo RemoteEndpoint { get; }
        public TlsServer TlsServer { get; }
    }
}
