using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;

namespace CoAPNet.Dtls.Server
{
    internal class DtlsSessionStore<TSession>
        where TSession : class, IDtlsSession
    {
        private readonly ILogger<DtlsSessionStore<TSession>> _logger;
        private readonly ConcurrentDictionary<IPEndPoint, TSession> _acceptingSessionsByEp;
        private readonly ConcurrentDictionary<IPEndPoint, TSession> _sessionsByEp;
        private readonly ConcurrentDictionary<byte[], TSession> _sessionsByCid;

        public DtlsSessionStore(ILogger<DtlsSessionStore<TSession>> logger)
        {
            _logger = logger;
            _acceptingSessionsByEp = new ConcurrentDictionary<IPEndPoint, TSession>();
            _sessionsByEp = new ConcurrentDictionary<IPEndPoint, TSession>();
            _sessionsByCid = new ConcurrentDictionary<byte[], TSession>(new ConnectionIdComparer());
        }

        public IEnumerable<TSession> GetSessions()
        {
            return _sessionsByEp.Values;
        }

        public DtlsSessionFindResult TryFindSession(IPEndPoint endPoint, byte[]? cid, out TSession? session)
        {
            // this is required because there may be packets with a cid before we have been notified of the cid by the session.
            // once the session is accepted, we just search by cid / endpoint (depending on whether the packet or session use cid or not)
            if (_acceptingSessionsByEp.TryGetValue(endPoint, out var cidSessionByEp))
            {
                session = cidSessionByEp;
                return DtlsSessionFindResult.FoundByEndPoint;
            }

            if (cid != null)
            {
                if (_sessionsByCid.TryGetValue(cid, out var sessionByCid))
                {
                    session = sessionByCid;
                    return DtlsSessionFindResult.FoundByConnectionId;
                }

                session = null;
                return DtlsSessionFindResult.UnknownCid;
            }

            if (_sessionsByEp.TryGetValue(endPoint, out var sessionByEp))
            {
                if (sessionByEp.ConnectionId != null)
                {
                    // https://www.rfc-editor.org/rfc/rfc9146.html#section-3-11
                    // Packet without cid for session with cid.
                    // This could either be an issue with the client or this endpoint has been reused for another client (NAT issue).
                    // We drop the packet in this case so the client can try again with another endpoint.
                    // Another solution might be to remove sessions from _sessionsByEp after a connection id is negotiated (but this would require some other changes as well).

                    _logger.LogWarning("Got packet without cid for session with cid from {EndPoint}. Discarding.", endPoint);
                    session = null;
                    return DtlsSessionFindResult.Invalid;
                }

                session = sessionByEp;
                return DtlsSessionFindResult.FoundByEndPoint;
            }

            session = null;
            return DtlsSessionFindResult.NewSession;
        }

        public void Add(TSession session)
        {
            _sessionsByEp.TryAdd(session.EndPoint, session);
            _acceptingSessionsByEp.TryAdd(session.EndPoint, session);
        }

        public void NotifySessionAccepted(TSession session)
        {
            if (session.ConnectionId != null)
            {
                _sessionsByCid.TryAdd(session.ConnectionId, session);
            }
            _acceptingSessionsByEp.TryRemove(session.EndPoint, out _);
        }

        public void Remove(TSession session)
        {
            _acceptingSessionsByEp.TryRemove(session.EndPoint, out _);
            _sessionsByEp.TryRemove(session.EndPoint, out _);
            if (session.ConnectionId != null)
            {
                _sessionsByCid.TryRemove(session.ConnectionId, out _);
            }
        }

        public void ReplaceSessionEndpoint(IDtlsSession sessionToReplace, IPEndPoint oldEndPoint, IPEndPoint newEndPoint)
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
            public bool Equals(byte[]? x, byte[]? y)
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
