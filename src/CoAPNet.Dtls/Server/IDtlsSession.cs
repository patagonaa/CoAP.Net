using System;
using System.Net;

namespace CoAPNet.Dtls.Server
{
    internal interface IDtlsSession : IDisposable
    {
        IPEndPoint EndPoint { get; }
        /// <summary>
        /// Is set only after the connection has been accepted and if a connection id has been negotiated
        /// </summary>
        byte[]? ConnectionId { get; }
    }
}
