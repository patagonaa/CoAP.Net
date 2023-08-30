using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

        public DtlsSessionFindResult TryFindSession(IPEndPoint endPoint, byte[]? cid, out TSession? session)
        {
            if (cid != null)
            {
                if (_sessionsByCid.TryGetValue(cid, out var sessionByCid))
                {
                    session = sessionByCid;
                    return DtlsSessionFindResult.FoundByConnectionId;
                }
            }
            else
            {
                if (_sessionsByEp.TryGetValue(endPoint, out var sessionByEp))
                {
                    if (sessionByEp.ConnectionId != null)
                        throw new InvalidOperationException("Session has acquired a connection id after it was accepted. Discarding packet.");

                    session = sessionByEp;
                    return DtlsSessionFindResult.FoundByEndPoint;
                }
            }

            // regardless of whether the packet has a cid or not, we check the accepting sessions.
            // this is required because there may be packets with a cid before we have been notified of the cid by the session.
            // once the session is accepted, we just search by cid / endpoint (depending on whether the packet or session use cid or not)
            if (_acceptingSessionsByEp.TryGetValue(endPoint, out var cidSessionByEp))
            {
                session = cidSessionByEp;
                return DtlsSessionFindResult.FoundByEndPoint;
            }

            session = null;
            return DtlsSessionFindResult.NotFound;
        }

        public void Add(TSession session)
        {
            if (_sessionsByEp.ContainsKey(session.EndPoint) || !_acceptingSessionsByEp.TryAdd(session.EndPoint, session))
                throw new InvalidOperationException($"Session can't be added because the endpoint {session.EndPoint} is already in use");
        }

        public void NotifySessionAccepted(TSession session)
        {
            if (session.ConnectionId != null)
            {
                if (!_sessionsByCid.TryAdd(session.ConnectionId, session))
                {
                    throw new InvalidOperationException($"Session {session.EndPoint} could not be accepted due to duplicate connection id!");
                }
            }
            else
            {
                if (!_sessionsByEp.TryAdd(session.EndPoint, session))
                {
                    throw new InvalidOperationException($"Session {session.EndPoint} could not be accepted due to duplicate endpoint!");
                }
            }
            if (!_acceptingSessionsByEp.TryRemove(session.EndPoint, out _))
            {
                _logger.LogError("Session {Endpoint} could not be found in accepting sessions! Adding anyways!", session.EndPoint);
            }
        }

        public void Remove(TSession session)
        {
            if (_acceptingSessionsByEp.TryRemove(session.EndPoint, out var acceptingSession))
            {
                if (acceptingSession == session)
                {
                    // if the removed acceptingSession was the right session, we just return to avoid removing an established session with the same endpoint/cid
                    return;
                }
                else
                {
                    // if we removed acceptingSession was the wrong session, we tried to remove an established session that happens to have the same endpoint
                    // as an accepting session.
                    // in this case we re-add the session we removed and try to remove the session by endpoint/cid
                    _logger.LogWarning("Accepting session we removed wasn't the one we wanted to remove.");
                    _acceptingSessionsByEp.TryAdd(session.EndPoint, acceptingSession);
                }
            }

            if (session.ConnectionId != null)
            {
                _sessionsByCid.TryRemove(session.ConnectionId, out _);
            }
            else
            {
                _sessionsByEp.TryRemove(session.EndPoint, out _);
            }
        }

        public IEnumerable<TSession> GetSessions()
        {
            return _acceptingSessionsByEp.Values.Concat(_sessionsByEp.Values).Concat(_sessionsByCid.Values);
        }

        public int GetCount()
        {
            return _acceptingSessionsByEp.Count + _sessionsByEp.Count + _sessionsByCid.Count;
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
