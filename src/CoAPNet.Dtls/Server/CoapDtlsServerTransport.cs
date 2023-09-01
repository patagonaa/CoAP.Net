using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using CoAPNet.Dtls.Server.Statistics;
using Microsoft.Extensions.Logging;
using Org.BouncyCastle.Tls;
using CoAPNet.Server;
using System.Net;

namespace CoAPNet.Dtls.Server
{
    internal class CoapDtlsServerTransport : ICoapTransport
    {
        private const int NetworkMtu = 1500;

        private readonly CoapDtlsServerEndPoint _endPoint;
        private readonly ICoapHandler _coapHandler;
        private readonly IDtlsServerFactory _tlsServerFactory;
        private readonly DtlsServerConfig _config;
        private readonly ILogger<CoapDtlsServerTransport> _logger;
        private readonly DtlsRecordParser _recordParser;
        private readonly DtlsServerProtocol _serverProtocol;
        private readonly DtlsSessionStore<CoapDtlsSession> _sessions;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();
        private readonly BlockingCollection<UdpSendPacket> _sendQueue = new BlockingCollection<UdpSendPacket>();

        private UdpClient? _socket;

        private int? _connectionIdLength;

        private Task? _incomingTask;
        private Task? _outgoingTask;
        private Task? _cleanupTask;

        #region Stats
        private int _handshakeSuccessCount = 0;
        private int _handshakeTlsErrorCount = 0;
        private int _handshakeTimeoutErrorCount = 0;
        private int _handshakeErrorCount = 0;

        private int _packetsReceivedNewSession = 0;
        private int _packetsReceivedSessionByEp = 0;
        private int _packetsReceivedSessionByCid = 0;
        private int _packetsReceivedUnknownCid = 0;
        private int _packetsReceivedInvalid = 0;

        private int _packetsSent = 0;
        #endregion

        public CoapDtlsServerTransport(
            CoapDtlsServerEndPoint endPoint,
            ICoapHandler coapHandler,
            IDtlsServerFactory tlsServerFactory,
            ILoggerFactory loggerFactory,
            DtlsServerConfig config)
        {
            _endPoint = endPoint ?? throw new ArgumentNullException(nameof(endPoint));
            _coapHandler = coapHandler ?? throw new ArgumentNullException(nameof(coapHandler));
            _tlsServerFactory = tlsServerFactory ?? throw new ArgumentNullException(nameof(tlsServerFactory));
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _logger = loggerFactory.CreateLogger<CoapDtlsServerTransport>();
            _recordParser = new DtlsRecordParser();

            _serverProtocol = new DtlsServerProtocol();

            _sessions = new DtlsSessionStore<CoapDtlsSession>(loggerFactory.CreateLogger<DtlsSessionStore<CoapDtlsSession>>());
        }

        internal DtlsStatistics GetStatistics()
        {
            var sessions = _sessions.GetSessions().Select(x => x.GetSessionStatistics()).ToList();
            var handshakesByResult = new Dictionary<string, uint>
                {
                    { "Success", (uint)_handshakeSuccessCount },
                    { "TlsError", (uint)_handshakeTlsErrorCount },
                    { "TimedOut", (uint)_handshakeTimeoutErrorCount },
                    { "Error", (uint)_handshakeErrorCount }
                };
            var packetsReceivedByType = new Dictionary<string, uint>
                {
                    { "ByEndpoint", (uint)_packetsReceivedSessionByEp },
                    { "ByConnectionId", (uint)_packetsReceivedSessionByCid },
                    { "NewSession", (uint)_packetsReceivedNewSession },
                    { "UnknownCid", (uint)_packetsReceivedUnknownCid },
                    { "Invalid", (uint)_packetsReceivedInvalid }
                };
            var packetsSent = (uint)_packetsSent;
            return new DtlsStatistics(sessions, handshakesByResult, packetsReceivedByType, packetsSent);
        }

        public Task BindAsync()
        {
            if (_socket != null)
                throw new InvalidOperationException("transport has already been used");

            _socket = new UdpClient(AddressFamily.InterNetworkV6);
            _socket.Client.DualMode = true;
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                // Disable "ConnectionReset" exception when receiving an ICMP "Port unreachable".
                // Ideally, we would use that to close that session, but .NET doesn't tell us which Endpoint it came from.
                // We _could_ use a separate ICMP socket and parse the UDP packet ourselves, but this usually only happens while closing a session anyways, so we don't care.
                // Also, I'm not sure if there's a way to disable this on other OSes, but that exception is ignored anyway, so it's not much of an issue either way.
                const int SIO_UDP_CONNRESET = -1744830452;
                byte[] inValue = new byte[] { 0 };
                byte[] outValue = new byte[] { 0 };
                _socket.Client.IOControl(SIO_UDP_CONNRESET, inValue, outValue);
            }
            _socket.Client.Bind(_endPoint.IPEndPoint);

            _logger.LogInformation("Bound to Endpoint {IPEndpoint}", _endPoint.IPEndPoint);

            _incomingTask = Task.Run(HandleIncoming);
            _outgoingTask = Task.Run(HandleOutgoing);
            _cleanupTask = Task.Run(HandleCleanup);

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
                        data = await _socket!.ReceiveAsync();
                        _logger.LogDebug("Received DTLS Packet from {EndPoint}", data.RemoteEndPoint);
                    }
                    catch (SocketException sockEx) when (sockEx.SocketErrorCode == SocketError.OperationAborted)
                    {
                        // Happens when the Connection is being closed
                        continue;
                    }
                    catch (SocketException sockEx) when (sockEx.SocketErrorCode == SocketError.ConnectionReset)
                    {
                        // Some clients send an ICMP Port Unreachable when they receive data after they stopped listening.
                        // See BindAsync for details.
                        _logger.LogDebug("Connection Closed by remote host");
                        continue;
                    }
                    catch (SocketException sockEx)
                    {
                        _logger.LogError("SocketException with SocketErrorCode {SocketErrorCode}", sockEx.SocketErrorCode);
                        continue;
                    }

                    var cid = GetConnectionId(data.Buffer);
                    var packetSessionType = _sessions.TryFindSession(data.RemoteEndPoint, cid, out var session);

                    switch (packetSessionType)
                    {
                        case DtlsSessionFindResult.FoundByEndPoint:
                            Interlocked.Increment(ref _packetsReceivedSessionByEp);
                            if (session == null)
                                throw new InvalidOperationException("Session may not be null when it was found!");
                            break;
                        case DtlsSessionFindResult.FoundByConnectionId:
                            Interlocked.Increment(ref _packetsReceivedSessionByCid);
                            if (session == null)
                                throw new InvalidOperationException("Session may not be null when it was found!");
                            break;
                        case DtlsSessionFindResult.NotFound when cid != null:
                            Interlocked.Increment(ref _packetsReceivedUnknownCid);
                            _logger.LogInformation("Discarding packet with unknown connection id from {EndPoint}", data.RemoteEndPoint);
                            break;
                        case DtlsSessionFindResult.NotFound when _recordParser.MayBeClientHello(data.Buffer):
                            Interlocked.Increment(ref _packetsReceivedNewSession);
                            session = StartNewSession(data.RemoteEndPoint);
                            break;
                        case DtlsSessionFindResult.NotFound:
                            Interlocked.Increment(ref _packetsReceivedInvalid);
                            _logger.LogInformation("Discarding invalid packet from {EndPoint}", data.RemoteEndPoint);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }

                    session?.EnqueueDatagram(data.Buffer, data.RemoteEndPoint);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while handling incoming datagrams");
                }
            }
        }

        private CoapDtlsSession StartNewSession(IPEndPoint endpoint)
        {
            var session = new CoapDtlsSession(
                endpoint,
                NetworkMtu,
                packet => _sendQueue.Add(packet),
                DateTime.UtcNow);
            try
            {
                _sessions.Add(session);
                _logger.LogInformation("New connection from {EndPoint}; Active Sessions: {ActiveSessions}", endpoint, _sessions.GetCount());

                _ = Task.Factory.StartNew(() => HandleSession(session), TaskCreationOptions.LongRunning);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception while starting session handler!");
                _sessions.Remove(session);
                session.Dispose();
                throw;
            }

            return session;
        }

        private byte[]? GetConnectionId(byte[] packet)
        {
            try
            {
                if (!_connectionIdLength.HasValue)
                    return null;

                return _recordParser.GetConnectionId(packet, _connectionIdLength.Value);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while getting cid from {Packet}", packet);
                return null;
            }
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

        private async Task HandleOutgoing()
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    if (!_sendQueue.TryTake(out UdpSendPacket? toSend, 1000, _cts.Token))
                    {
                        // until we can use something async here (like Channels, which aren't available on netstandard2.0), yield now and then to avoid deadlocking everything
                        await Task.Yield();
                        continue;
                    }

                    await _socket!.SendAsync(toSend.Payload, toSend.Payload.Length, toSend.TargetEndPoint);
                    Interlocked.Increment(ref _packetsSent);
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

        private async Task HandleSession(CoapDtlsSession session)
        {
            var state = new Dictionary<string, object>
                {
                    { "InitialEndPoint", session.EndPoint }
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
                    _sessions.NotifySessionAccepted(session);

                    using (session.ConnectionInfo != null ? _logger.BeginScope(session.ConnectionInfo) : null)
                    {
                        _logger.LogInformation("New TLS connection from {RemoteEndPoint}", session.EndPoint);

                        var connectionInfo = new CoapDtlsConnectionInformation(_endPoint, session, server);

                        var cancellationToken = _cts.Token;
                        while (true)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            var packet = await session.ReceiveAsync(cancellationToken);

                            using (_logger.BeginScope(new Dictionary<string, object> { { "RemoteEndPoint", session.EndPoint } }))
                            {
                                _logger.LogDebug("Handling CoAP Packet");
                                await _coapHandler.ProcessRequestAsync(connectionInfo, packet.Payload);
                                _logger.LogDebug("CoAP request handled!", session.EndPoint);
                            }
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
                    _logger.LogInformation("Connection from {EndPoint} closed after {ElapsedMilliseconds}ms", session.EndPoint, (int)(DateTime.UtcNow - session.SessionStartTime).TotalMilliseconds);
                    _sessions.Remove(session);
                    session.Dispose();
                }
            }
        }

        private bool IsCanceledException(Exception ex)
        {
            return ex is OperationCanceledException ||
                (ex is TlsFatalAlert tlsAlert && (tlsAlert.InnerException is OperationCanceledException || tlsAlert.AlertDescription == AlertDescription.user_canceled));
        }

        private async Task HandleCleanup()
        {
            var cancellationToken = _cts.Token;

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    int cleaned = 0;
                    foreach (var session in _sessions.GetSessions())
                    {
                        var hasConnectionId = session.ConnectionId != null;
                        var sessionTimeout = hasConnectionId ? _config.SessionTimeoutWithCid : _config.SessionTimeout;
                        if (session.LastReceivedTime < DateTime.UtcNow - sessionTimeout)
                        {
                            // do not notify the peer here if we have a connection ID.
                            // we don't want to send an alert-message to an endpoint that has possibly be reused.
                            try
                            {
                                session.Close(!hasConnectionId);
                            }
                            catch (ObjectDisposedException)
                            {
                                // race condition: session has already been closed and disposed
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error while cleaning up session");
                            }

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
                await Task.Delay(10000, cancellationToken);
            }
        }

        public async Task UnbindAsync()
        {
            foreach (var session in _sessions.GetSessions())
            {
                try
                {
                    session.Close(true);
                }
                catch
                {
                    // Ignore all errors since we can not do anything anyways.
                }
            }

            using var cancelTimeoutCts = new CancellationTokenSource(10000);
            var cancelTimeoutToken = cancelTimeoutCts.Token;
            try
            {
                // wait until close-notifications have been sent and sessions have exited (with timeout)
                // would be better if there was something to await, but currently we don't save the session tasks and we don't complete the send queue either.
                while (_sessions.GetCount() == 0 && _sendQueue.Count == 0)
                {
                    await Task.Delay(100, cancelTimeoutToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Timeout waiting for all sessions to close. Continuing.");
            }

            _cts.Cancel();
            _socket?.Close(); // close socket to send cancellation notification because we can't cancel ReceiveAsync and SendAsync in netstandard2.0

            // we could use Task.WhenAll here, but that makes it harder to handle exceptions (all but the first exception are swallowed)
            foreach (var task in new[] { _incomingTask, _outgoingTask, _cleanupTask })
            {
                if (task == null)
                    continue;
                try
                {
                    await task;
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error while stopping Tasks");
                }
            }

            _socket?.Dispose();
            _cts.Dispose();
            _sendQueue.Dispose();
        }

        public Task StopAsync()
        {
            // there is not really a reason to have seperate StopAsync and UnbindAsync, so we do all cleanup in UnbindAsync
            return Task.CompletedTask;
        }
    }
}
