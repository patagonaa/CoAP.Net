using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CoAPNet.Dtls.Server.Statistics;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Tls;

namespace CoAPNet.Dtls.Server
{
    internal class CoapDtlsServerTransport : ICoapTransport
    {
        private const int NetworkMtu = 1500;

        private readonly TimeSpan _sessionTimeout;
        private readonly CoapDtlsServerEndPoint _endPoint;
        private readonly ICoapHandler _coapHandler;
        private readonly IDtlsServerFactory _tlsServerFactory;
        private readonly ILogger<CoapDtlsServerTransport> _logger;
        private readonly DtlsServerProtocol _serverProtocol;
        private readonly ConcurrentDictionary<IPEndPoint, CoapDtlsServerClientEndPoint> _sessions;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly BlockingCollection<UdpSendPacket> _sendQueue = new BlockingCollection<UdpSendPacket>();

        private UdpClient _socket;
        private int? connectionIdLength;

        private int _handshakeSuccessCount = 0;
        private int _handshakeTlsErrorCount = 0;
        private int _handshakeTimeoutErrorCount = 0;
        private int _handshakeErrorCount = 0;

        private int _packetsReceivedNewSession = 0;
        private int _packetsReceivedSessionByEp = 0;
        private int _packetsReceivedSessionByCid = 0;
        private int _packetsReceivedInvalid = 0;

        private int _packetsSent = 0;

        public CoapDtlsServerTransport(
            CoapDtlsServerEndPoint endPoint,
            ICoapHandler coapHandler,
            IDtlsServerFactory tlsServerFactory,
            ILogger<CoapDtlsServerTransport> logger,
            TimeSpan sessionTimeout)
        {
            _endPoint = endPoint ?? throw new ArgumentNullException(nameof(endPoint));
            _coapHandler = coapHandler ?? throw new ArgumentNullException(nameof(coapHandler));
            _tlsServerFactory = tlsServerFactory ?? throw new ArgumentNullException(nameof(tlsServerFactory));
            _sessionTimeout = sessionTimeout;
            _logger = logger;

            _serverProtocol = new DtlsServerProtocol();

            _sessions = new ConcurrentDictionary<IPEndPoint, CoapDtlsServerClientEndPoint>();
        }

        internal DtlsStatistics GetStatistics()
        {
            return new DtlsStatistics
            {
                Sessions = _sessions.Values.Select(x => new DtlsSessionStatistics
                {
                    EndPoint = x.EndPoint.ToString(),
                    ConnectionInfo = x.ConnectionInfo,
                    LastReceivedTime = x.LastReceivedTime,
                    SessionStartTime = x.SessionStartTime
                }).ToList(),
                HandshakesByResult = new Dictionary<string, uint>
                {
                    { "Success", (uint)_handshakeSuccessCount },
                    { "TlsError", (uint)_handshakeTlsErrorCount },
                    { "TimedOut", (uint)_handshakeTimeoutErrorCount },
                    { "Error", (uint)_handshakeErrorCount }
                },
                PacketsReceivedByType = new Dictionary<string, uint>
                {
                    { "ByEndpoint", (uint)_packetsReceivedSessionByEp },
                    { "ByConnectionId", (uint)_packetsReceivedSessionByCid },
                    { "NewSession", (uint)_packetsReceivedNewSession },
                    { "InvalidCid", (uint)_packetsReceivedInvalid }
                },
                PacketsSent = (uint)_packetsSent
            };
        }

        public Task BindAsync()
        {
            if (_socket != null)
                throw new InvalidOperationException("transport has already been used");

            _socket = new UdpClient(AddressFamily.InterNetworkV6);
            _socket.Client.DualMode = true;
            _socket.Client.Bind(_endPoint.IPEndPoint);

            _logger.LogInformation("Bound to Endpoint {IPEndpoint}", _endPoint.IPEndPoint);

            Task.Factory.StartNew(HandleIncoming, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(HandleOutgoing, TaskCreationOptions.LongRunning);
            Task.Factory.StartNew(HandleCleanup, TaskCreationOptions.LongRunning);

            return Task.CompletedTask;
        }

        private async Task HandleIncoming()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    UdpReceiveResult data;
                    try
                    {
                        data = await _socket.ReceiveAsync();
                        _logger.LogDebug("Received DTLS Packet from {EndPoint}", data.RemoteEndPoint);
                    }
                    catch (ObjectDisposedException)
                    {
                        // Happens when the Connection is being closed
                        continue;
                    }
                    catch (SocketException sockEx)
                    {
                        // Some clients send an ICMP Port Unreachable when they receive data after they stopped listening.
                        // If we knew from which client it came we could get rid of the connection, but we can't, so we just ignore the exception.
                        if (sockEx.SocketErrorCode == SocketError.ConnectionReset)
                        {
                            _logger.LogInformation("Connection Closed by remote host");
                            continue;
                        }
                        _logger.LogError("SocketException with SocketErrorCode {SocketErrorCode}", sockEx.SocketErrorCode);
                        throw;
                    }

                    var packetSessionType = TryFindSession(data, out var session);

                    switch (packetSessionType)
                    {
                        case PacketSessionType.FoundByEndPoint:
                            Interlocked.Increment(ref _packetsReceivedSessionByEp);
                            break;
                        case PacketSessionType.FoundByConnectionId:
                            Interlocked.Increment(ref _packetsReceivedSessionByCid);
                            break;
                        case PacketSessionType.NewSession:
                            Interlocked.Increment(ref _packetsReceivedNewSession);
                            break;
                        case PacketSessionType.InvalidCid:
                        default:
                            Interlocked.Increment(ref _packetsReceivedInvalid);
                            break;
                    }

                    if (packetSessionType == PacketSessionType.NewSession)
                    {
                        // if there isn't an existing session for this remote endpoint, we start a new one and pass the first datagram to the session
                        session = new CoapDtlsServerClientEndPoint(
                            data.RemoteEndPoint,
                            NetworkMtu,
                            packet => _sendQueue.Add(packet),
                            (oldEp, newEp) => ReplaceSessionEndpoint(oldEp, newEp),
                            DateTime.UtcNow);
                        session.EnqueueDatagram(data.Buffer, data.RemoteEndPoint);

                        _sessions.TryAdd(data.RemoteEndPoint, session);
                        try
                        {
                            _logger.LogInformation("New connection from {EndPoint}; Active Sessions: {ActiveSessions}", data.RemoteEndPoint, _sessions.Count);

#pragma warning disable CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                            Task.Factory.StartNew(() => HandleSession(session), TaskCreationOptions.LongRunning);
#pragma warning restore CS4014 // Because this call is not awaited, execution of the current method continues before the call is completed
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Exception while starting session handler!");
                            _sessions.TryRemove(session.EndPoint, out _);
                        }
                    }
                    else if (packetSessionType == PacketSessionType.FoundByEndPoint || packetSessionType == PacketSessionType.FoundByConnectionId)
                    {
                        // if there is an existing session, we pass the datagram to the session.
                        session.EnqueueDatagram(data.Buffer, data.RemoteEndPoint);
                        continue;
                    }
                    else
                    {
                        _logger.LogDebug("Invalid connection id from {EndPoint}", data.RemoteEndPoint);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while handling incoming datagrams");
                }
            }
        }

        private PacketSessionType TryFindSession(UdpReceiveResult data, out CoapDtlsServerClientEndPoint session)
        {
            if (_sessions.TryGetValue(data.RemoteEndPoint, out session))
            {
                return PacketSessionType.FoundByEndPoint;
            }

            if (connectionIdLength.HasValue && data.Buffer.Length >= 1 && data.Buffer[0] == ContentType.tls12_cid)
            {
                try
                {
                    var cid = new byte[connectionIdLength.Value];
                    Array.Copy(data.Buffer, 11, cid, 0, connectionIdLength.Value);

                    var sessionByCid = _sessions.Values.FirstOrDefault(x => x.ConnectionId != null && cid.SequenceEqual(x.ConnectionId));
                    if (sessionByCid != null)
                    {
                        _logger.LogDebug("Found session by connection id. {OldEndPoint} -> {NewEndPoint}", sessionByCid.EndPoint, data.RemoteEndPoint);
                        session = sessionByCid;
                        return PacketSessionType.FoundByConnectionId;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while getting cid from {EndPoint}", data.RemoteEndPoint);
                }

                return PacketSessionType.InvalidCid;
            }

            return PacketSessionType.NewSession;
        }

        private void ReplaceSessionEndpoint(IPEndPoint oldEndPoint, IPEndPoint newEndPoint)
        {
            if (_sessions.TryRemove(oldEndPoint, out var session))
            {
                if (_sessions.TryAdd(newEndPoint, session))
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

        private async Task HandleOutgoing()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    _sendQueue.TryTake(out UdpSendPacket toSend, Timeout.Infinite, _cts.Token);
                    Interlocked.Increment(ref _packetsSent);

                    await _socket.SendAsync(toSend.Payload, toSend.Payload.Length, toSend.TargetEndPoint);
                    _logger.LogDebug("Sent DTLS Packet to {EndPoint}", toSend.TargetEndPoint);
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogDebug(ex, "Operation canceled");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while handling outgoing datagrams");
                }
            }
        }

        private async Task HandleSession(CoapDtlsServerClientEndPoint session)
        {
            var state = new Dictionary<string, object>
                {
                    { "RemoteEndPoint", session?.EndPoint }
                };
            using (_logger.BeginScope(state))
            {
                try
                {
                    _logger.LogDebug("Trying to accept TLS connection from {EndPoint}", session.EndPoint);

                    var server = _tlsServerFactory.Create();

                    try
                    {
                        session.Accept(_serverProtocol, server);
                        Interlocked.Increment(ref _handshakeSuccessCount);
                    }
                    catch (TlsTimeoutException)
                    {
                        Interlocked.Increment(ref _handshakeTimeoutErrorCount);
                        throw;
                    }
                    catch (TlsFatalAlert)
                    {
                        Interlocked.Increment(ref _handshakeTlsErrorCount);
                        throw;
                    }
                    catch (Exception)
                    {
                        Interlocked.Increment(ref _handshakeErrorCount);
                        throw;
                    }

                    if (session.ConnectionId != null)
                    {
                        SetConnectionIdLength(session.ConnectionId.Length);
                    }

                    using (session.ConnectionInfo != null ? _logger.BeginScope(session.ConnectionInfo) : null)
                    {
                        _logger.LogInformation("New TLS connection from {EndPoint}", session.EndPoint);

                        var connectionInfo = new CoapDtlsConnectionInformation
                        {
                            LocalEndpoint = _endPoint,
                            RemoteEndpoint = session,
                            TlsServer = server
                        };

                        while (!session.IsClosed && !_cts.IsCancellationRequested)
                        {
                            var packet = await session.ReceiveAsync(_cts.Token);
                            _logger.LogDebug("Handling CoAP Packet from {EndPoint}", session.EndPoint);
                            await _coapHandler.ProcessRequestAsync(connectionInfo, packet.Payload);
                            _logger.LogDebug("CoAP request from {EndPoint} handled!", session.EndPoint);
                        }
                    }
                }
                catch (Exception ex) when (IsCanceledException(ex))
                {
                    _logger.LogDebug(ex, "Session was canceled");
                }
                catch (TlsTimeoutException timeoutEx)
                {
                    _logger.LogInformation(timeoutEx, "Timeout while handling session");
                }
                catch (TlsFatalAlert tlsAlert)
                {
                    _logger.LogWarning(tlsAlert, "TLS Error");
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while handling session");
                }
                finally
                {
                    _logger.LogInformation("Connection from {EndPoint} closed after {ElapsedMilliseconds}ms", session.EndPoint, (DateTime.UtcNow - session.SessionStartTime).TotalMilliseconds);
                    _sessions.TryRemove(session.EndPoint, out _);
                }
            }
        }

        private void SetConnectionIdLength(int sessionCidLength)
        {
            if (connectionIdLength.HasValue)
            {
                if (sessionCidLength != connectionIdLength.Value)
                    throw new InvalidOperationException("Connection IDs must have constant length!");
            }
            else
            {
                connectionIdLength = sessionCidLength;
            }
        }

        private bool IsCanceledException(Exception ex)
        {
            return ex is OperationCanceledException ||
                ex is DtlsConnectionClosedException ||
                (ex is TlsFatalAlert tlsAlert && (tlsAlert.InnerException is DtlsConnectionClosedException || tlsAlert.AlertDescription == AlertDescription.user_canceled));
        }

        private async Task HandleCleanup()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    int cleaned = 0;
                    foreach (var session in _sessions.Values)
                    {
                        if (session.LastReceivedTime < DateTime.UtcNow - _sessionTimeout)
                        {
                            session.Dispose();
                            cleaned++;
                        }
                    }

                    if (cleaned > 0)
                    {
                        _logger.LogInformation("Closed {CleanedSessions} stale sessions", cleaned);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while cleaning up sessions");
                }
                await Task.Delay(10000);
            }
        }

        public Task UnbindAsync()
        {
            _socket?.Close();
            _socket?.Dispose();
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            foreach (var session in _sessions.Values)
            {
                try
                {
                    session.Dispose();
                }
                catch
                {
                    // Ignore all errors since we can not do anything anyways.
                }
            }

            _cts.Cancel();
            return Task.CompletedTask;
        }

        private enum PacketSessionType
        {
            NewSession,
            InvalidCid,
            FoundByEndPoint,
            FoundByConnectionId
        }
    }
}
