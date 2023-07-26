#region License
// Copyright 2017 Roman Vaughan (NZSmartie)
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
#endregion

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace CoAPNet.Udp
{
    public class CoapUdpConnectionInformation : ICoapConnectionInformation
    {
        public ICoapEndpoint LocalEndpoint { get; set; }
        public ICoapEndpointInfo RemoteEndpoint { get; set; }
    }

    public class CoapUdpEndPoint : ICoapEndpoint, ICoapClientEndpoint
    {
        private readonly ILogger<CoapUdpEndPoint> _logger;
        private readonly IPEndPoint _endpoint;
        private static readonly IPAddress _multicastAddressIPv4 = IPAddress.Parse(Coap.MulticastIPv4);
        private static readonly IPAddress[] _multicastAddressIPv6 = Enumerable.Range(1, 13).Select(n => IPAddress.Parse(Coap.GetMulticastIPv6ForScope(n))).ToArray();

        public IPEndPoint Endpoint => (IPEndPoint)Client?.Client.LocalEndPoint ?? _endpoint;

        public UdpClient Client { get; private set; }

        internal bool Bindable { get; set; } = true;

        public bool CanReceive => Client?.Client.LocalEndPoint != null;

        public bool IsMulticast { get; set; }

        public bool JoinMulticast { get; set; }

        public CoapUdpEndPoint(UdpClient udpClient, ILogger<CoapUdpEndPoint> logger = null)
            : this((IPEndPoint)udpClient.Client.LocalEndPoint, logger)
        {
            Client = udpClient;
        }

        public CoapUdpEndPoint(int port = 0, ILogger<CoapUdpEndPoint> logger = null)
            : this(new IPEndPoint(IPAddress.IPv6Any, port), logger)
        { }

        public CoapUdpEndPoint(IPAddress address, int port = 0, ILogger<CoapUdpEndPoint> logger = null)
            : this(new IPEndPoint(address, port), logger)
        { }

        public CoapUdpEndPoint(string ipAddress, int port = 0, ILogger<CoapUdpEndPoint> logger = null)
            : this(new IPEndPoint(IPAddress.Parse(ipAddress), port), logger)
        { }

        public CoapUdpEndPoint(IPEndPoint endpoint, ILogger<CoapUdpEndPoint> logger = null)
        {
            _logger = logger;
            _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        }

        public Task BindAsync()
        {
            if (Client != null)
                throw new InvalidOperationException($"Can not bind {nameof(CoapUdpEndPoint)} as it is already bound");
            if (!Bindable)
                throw new InvalidOperationException("Can not bind to remote endpoint");

            Client = new UdpClient(_endpoint.AddressFamily) { EnableBroadcast = true };
            if (_endpoint.Address.Equals(IPAddress.IPv6Any))
                Client.Client.DualMode = true;
            Client.Client.Bind(_endpoint);

            if (JoinMulticast)
            {
                switch (Client.Client.AddressFamily)
                {
                    case AddressFamily.InterNetworkV6:
                        _logger?.LogInformation("TODO: Join multicast group with the correct IPv6 scope.");
                        break;
                    case AddressFamily.InterNetwork:
                        Client.JoinMulticastGroup(_multicastAddressIPv4);
                        break;
                    default:
                        _logger?.LogError($"Can not join multicast group for the address family {Client.Client.AddressFamily:G}.");
                        break;
                }
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
            Client?.Dispose();
        }

        private Queue<Task<UdpReceiveResult>> _receiveTasks = new Queue<Task<UdpReceiveResult>>();

        public async Task<CoapPacket> ReceiveAsync(CancellationToken token)
        {
            if (Client == null)
                await BindAsync();

            Task<UdpReceiveResult> receiveTask;
            lock (_receiveTasks)
                receiveTask = _receiveTasks.Count > 0
                    ? _receiveTasks.Dequeue()
                    : receiveTask = Client.ReceiveAsync();

            try
            {
                // The receiveTask can not be canceled, so use another task that can monitor when the CancelationToken is canceled.
                var tcs = new TaskCompletionSource<bool>();
                using (token.Register(() => tcs.SetResult(false)))
                {
                    await Task.WhenAny(receiveTask, tcs.Task);

                    // Return a result if we have it already, there are no more async operations left.
                    if (!receiveTask.IsCompleted)
                        token.ThrowIfCancellationRequested();

                    return new CoapPacket
                    {
                        Payload = receiveTask.Result.Buffer,
                        Endpoint = new CoapUdpEndpointInfo(receiveTask.Result.RemoteEndPoint),
                    };
                }
            }
            catch (OperationCanceledException)
            {
                // The task may still complete later, if we lose it, then the packet is lost
                lock (_receiveTasks)
                    _receiveTasks.Enqueue(receiveTask);

                throw;
            }
        }

        public async Task SendAsync(CoapPacket packet, CancellationToken token)
        {
            if (Client == null)
                await BindAsync();

            if (packet.Endpoint is not CoapUdpEndpointInfo udpDestination)
            {
                throw new ArgumentException("Endpoint must be CoapUdpEndpointInfo");
            }

            token.ThrowIfCancellationRequested();

            var tcs = new TaskCompletionSource<bool>();
            using (token.Register(() => tcs.SetResult(false)))
            {
                try
                {
                    await Task.WhenAny(Client.SendAsync(packet.Payload, packet.Payload.Length, udpDestination.EndPoint), tcs.Task);
                    if (token.IsCancellationRequested)
                        Client.Dispose(); // Since UdpClient doesn't provide a mechanism for cancelling an async task. the safest way is to dispose the whole object
                }
                catch (SocketException se)
                {
                    _logger?.LogInformation($"Failed to send data. {se.GetType().FullName} (0x{se.HResult:x}): {se.Message}", se);
                }
            }

            token.ThrowIfCancellationRequested();
        }

        /// <inheritdoc />
        public override string ToString() => $"udp{(JoinMulticast ? "+multicast" : string.Empty)}://{_endpoint}";

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            if (obj is CoapUdpEndPoint other)
            {
                if (!other._endpoint.Equals(_endpoint))
                    return false;
                if (!other.IsMulticast.Equals(IsMulticast))
                    return false;
                return true;
            }
            return base.Equals(obj);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            return (_endpoint.GetHashCode() ^ 963144320)
                 ^ (IsMulticast.GetHashCode() ^ 1491585648);
        }

        public async Task<ICoapEndpointInfo> GetEndpointInfoFromMessage(CoapMessage message)
        {
            var uri = new UriBuilder(message.GetUri()) { Path = "/", Fragment = "", Query = "" }.Uri;

            int port = uri.Port;
            if (port == -1)
                port = Coap.Port;

            IPAddress address;
            if (message.IsMulticast)
            {
                // TODO: Support sending to IPv6 multicast endpoints as well.
                address = _multicastAddressIPv4;
            }
            else if (uri.HostNameType == UriHostNameType.IPv4 || uri.HostNameType == UriHostNameType.IPv6)
            {
                address = IPAddress.Parse(uri.Host);
            }
            else if (uri.HostNameType == UriHostNameType.Dns)
            {
                // TODO: how do we select the best ip address after looking it up?
                // This is especially an issue with IPv4 vs. IPv6, especially if only one of both is available on the server
                address = (await Dns.GetHostAddressesAsync(uri.Host)).FirstOrDefault();
            }
            else
            {
                throw new CoapUdpEndpointException($"Unsupported Uri HostNameType ({uri.HostNameType:G}");
            }

            // Check is we still don't have an address
            if (address == null)
                throw new CoapUdpEndpointException($"Can not resolve host name for {uri.Host}");

            return new CoapUdpEndpointInfo(new IPEndPoint(address, port));
        }

        internal class CoapUdpEndpointInfo : ICoapIpEndpointInfo
        {
            public IPEndPoint EndPoint { get; }
            public bool IsMulticast { get; }

            public CoapUdpEndpointInfo(IPEndPoint ipEndPoint)
            {
                EndPoint = ipEndPoint ?? throw new ArgumentNullException(nameof(ipEndPoint));
                IsMulticast = ipEndPoint.Address.Equals(_multicastAddressIPv4) || _multicastAddressIPv6.Contains(ipEndPoint.Address);
            }

            public override bool Equals(object obj)
            {
                return obj is CoapUdpEndpointInfo info && EndPoint.Equals(info.EndPoint);
            }

            public override int GetHashCode()
            {
                return EndPoint.GetHashCode();
            }

            public override string ToString()
            {
                return $"udp://{EndPoint}";
            }
        }
    }
}
