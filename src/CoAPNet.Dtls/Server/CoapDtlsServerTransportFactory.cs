using System;
using CoAPNet.Dtls.Server.Statistics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CoAPNet.Dtls.Server
{
    public class CoapDtlsServerTransportFactory : ICoapTransportFactory
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly IDtlsServerFactory _tlsServerFactory;
        private readonly DtlsServerConfig _config;
        private CoapDtlsServerTransport _transport;

        /// <param name="loggerFactory">LoggerFactory to use for transport logging</param>
        /// <param name="tlsServerFactory">a <see cref="IDtlsServerFactory"/> that creates the DtlsServer to use.</param>
        /// <param name="config">The configuration for the DTLS server</param>
        public CoapDtlsServerTransportFactory(ILoggerFactory loggerFactory, IDtlsServerFactory tlsServerFactory, IOptions<DtlsServerConfig> config)
        {
            _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
            _tlsServerFactory = tlsServerFactory ?? throw new ArgumentNullException(nameof(tlsServerFactory));
            _config = config.Value;
        }

        public ICoapTransport Create(ICoapEndpoint endPoint, ICoapHandler handler)
        {
            var serverEndpoint = endPoint as CoapDtlsServerEndPoint;
            if (serverEndpoint == null)
                throw new ArgumentException($"Endpoint has to be {nameof(CoapDtlsServerEndPoint)}");
            if (_transport != null)
                throw new InvalidOperationException("CoAP transport may only be created once!");

            _transport = new CoapDtlsServerTransport(serverEndpoint, handler, _tlsServerFactory, _loggerFactory, _config);
            return _transport;
        }

        public DtlsStatistics GetTransportStatistics()
        {
            return _transport?.GetStatistics();
        }
    }
}
