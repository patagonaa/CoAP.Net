using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Tls;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;

namespace CoAPNet.Dtls.Server
{
    internal class DtlsSessionStore
    {
        private readonly ILogger<DtlsSessionStore> _logger;
        private readonly ConcurrentDictionary<IPEndPoint, CoapDtlsServerClientEndPoint> _acceptingSessionsByEp;
        private readonly ConcurrentDictionary<IPEndPoint, CoapDtlsServerClientEndPoint> _sessionsByEp;
        private readonly ConcurrentDictionary<byte[], CoapDtlsServerClientEndPoint> _sessionsByCid;

        private int? _connectionIdLength;

        public DtlsSessionStore(ILogger<DtlsSessionStore> logger)
        {
            _logger = logger;
            _acceptingSessionsByEp = new ConcurrentDictionary<IPEndPoint, CoapDtlsServerClientEndPoint>();
            _sessionsByEp = new ConcurrentDictionary<IPEndPoint, CoapDtlsServerClientEndPoint>();
            _sessionsByCid = new ConcurrentDictionary<byte[], CoapDtlsServerClientEndPoint>(new ConnectionIdComparer());
        }

        public IEnumerable<CoapDtlsServerClientEndPoint> GetSessions()
        {
            return _sessionsByEp.Values;
        }

        public DtlsSessionFindResult TryFindSession(UdpReceiveResult data, out CoapDtlsServerClientEndPoint session)
        {
            // this is required because there may be packets with a cid before we have been notified of the cid by the session.
            // once the session is accepted, we just search by cid / endpoint (depending on whether the packet or session use cid or not)
            if (_acceptingSessionsByEp.TryGetValue(data.RemoteEndPoint, out var cidSessionByEp))
            {
                session = cidSessionByEp;
                return DtlsSessionFindResult.FoundByEndPoint;
            }

            byte[] cid;
            try
            {
                cid = GetConnectionId(data.Buffer);
            }
            catch (Exception ex)
            {
                cid = null;
                _logger.LogError(ex, "Error while getting cid from {EndPoint}", data.RemoteEndPoint);
            }

            if (cid != null)
            {
                if (_sessionsByCid.TryGetValue(cid, out var sessionByCid))
                {
                    var endpointChanged = !sessionByCid.EndPoint.Equals(data.RemoteEndPoint);
                    if (endpointChanged)
                        _logger.LogDebug("Found session by connection id. {OldEndPoint} -> {NewEndPoint}", sessionByCid.EndPoint, data.RemoteEndPoint);

                    session = sessionByCid;
                    return DtlsSessionFindResult.FoundByConnectionId;
                }

                session = null;
                return DtlsSessionFindResult.UnknownCid;
            }

            if (_sessionsByEp.TryGetValue(data.RemoteEndPoint, out var sessionByEp))
            {
                if (sessionByEp.ConnectionId != null)
                {
                    // https://www.rfc-editor.org/rfc/rfc9146.html#section-3-11
                    // Packet without cid for session with cid.
                    // This could either be an issue with the client or this endpoint has been reused for another client (NAT issue).
                    // We drop the packet in this case so the client can try again with another endpoint.

                    _logger.LogInformation("Got packet without cid for session with cid from {EndPoint}. Discarding.", data.RemoteEndPoint);
                    session = null;
                    return DtlsSessionFindResult.Invalid;
                }

                session = sessionByEp;
                return DtlsSessionFindResult.FoundByEndPoint;
            }

            session = null;
            return DtlsSessionFindResult.NewSession;
        }

        private byte[] GetConnectionId(byte[] packet)
        {
            if (!_connectionIdLength.HasValue)
                return null;

            const int headerCidOffset = 11;
            var cidLength = _connectionIdLength.Value;

            if (packet.Length >= (headerCidOffset + cidLength) && packet[0] == ContentType.tls12_cid)
            {
                var cid = new byte[cidLength];
                Array.Copy(packet, headerCidOffset, cid, 0, cidLength);
                return cid;
            }

            return null;
        }

        public void Add(CoapDtlsServerClientEndPoint session)
        {
            _sessionsByEp.TryAdd(session.EndPoint, session);
            _acceptingSessionsByEp.TryAdd(session.EndPoint, session);
        }

        public void NotifySessionAccepted(CoapDtlsServerClientEndPoint session)
        {
            if (session.ConnectionId != null)
            {
                SetConnectionIdLength(session.ConnectionId.Length);
                _sessionsByCid.TryAdd(session.ConnectionId, session);
            }
            _acceptingSessionsByEp.TryRemove(session.EndPoint, out _);
        }

        private void SetConnectionIdLength(int sessionCidLength)
        {
            if (_connectionIdLength.HasValue)
            {
                if (sessionCidLength != _connectionIdLength.Value)
                    throw new InvalidOperationException("Connection IDs must have constant length!");
            }
            else
            {
                _connectionIdLength = sessionCidLength;
            }
        }

        public void Remove(CoapDtlsServerClientEndPoint session)
        {
            _acceptingSessionsByEp.TryRemove(session.EndPoint, out _);
            _sessionsByEp.TryRemove(session.EndPoint, out _);
            if (session.ConnectionId != null)
            {
                _sessionsByCid.TryRemove(session.ConnectionId, out _);
            }
        }

        public void ReplaceSessionEndpoint(IPEndPoint oldEndPoint, IPEndPoint newEndPoint)
        {
            _acceptingSessionsByEp.TryRemove(oldEndPoint, out _);
            if (_sessionsByEp.TryRemove(oldEndPoint, out var session))
            {
                if (_sessionsByEp.TryAdd(newEndPoint, session))
                {
                    _logger.LogInformation("Replacing endpoint {OldEndPoint} with {NewEndPoint}", oldEndPoint, newEndPoint);
                }
                else
                {
                    _logger.LogWarning("Couldn't add session because endpoint {NewEndPoint} is already in use!", newEndPoint);
                    session.Dispose();
                }
            }
        }

        public int GetCount()
        {
            return _sessionsByEp.Count;
        }

        private class ConnectionIdComparer : IEqualityComparer<byte[]>
        {
            public bool Equals(byte[] x, byte[] y)
            {
                return StructuralComparisons.StructuralEqualityComparer.Equals(x, y);
            }

            public int GetHashCode(byte[] obj)
            {
                return StructuralComparisons.StructuralEqualityComparer.GetHashCode(obj);
            }
        }
    }
}
