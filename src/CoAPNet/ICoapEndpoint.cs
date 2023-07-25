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
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;

namespace CoAPNet
{
    /// <summary/>
    [ExcludeFromCodeCoverage]
    public class CoapEndpointException : Exception
    {
        /// <summary/>
        public CoapEndpointException() : base() { }

        /// <summary/>
        public CoapEndpointException(string message) : base(message) { }

        /// <summary/>
        public CoapEndpointException(string message, Exception innerException) : base(message, innerException) { }
    }

    /// <summary>
    /// Contains the local <see cref="ICoapEndpoint"/> and the remote <see cref="ICoapEndpointInfo"/> that are part of the associate message request or reponse.
    /// </summary>
    public interface ICoapConnectionInformation
    {
        /// <summary>
        /// The Local <see cref="ICoapEndpoint"/> that represents the current connection.
        /// </summary>
        ICoapEndpoint LocalEndpoint { get; }

        /// <summary>
        /// The Remote <see cref="ICoapEndpoint"/> that represents the 3rd party.
        /// </summary>
        ICoapEndpointInfo RemoteEndpoint { get; }
    }

    /// <summary>
    /// Used with <see cref="ICoapEndpoint.ToString(CoapEndpointStringFormat)"/> to get a string representation of a <see cref="ICoapEndpoint"/>.
    /// </summary>
    public enum CoapEndpointStringFormat
    {
        /// <summary>
        /// Return a simple string format represeantion of <see cref="ICoapEndpoint"/> (usually in the form of &lt;address&gt;:&lt;port&gt;)
        /// </summary>
        Simple,
        /// <summary>
        /// Used to get string representation of a <see cref="ICoapEndpoint"/> for debugging purposes.
        /// </summary>
        Debuggable,
    }

    /// <summary>
    /// CoAP uses a <see cref="ICoapEndpoint"/> as a addressing mechanism for other CoAP clients and servers on a transport.
    /// </summary>
    public interface ICoapEndpoint : IDisposable
    {
        /// <summary>
        /// Gets the base URI (excluding path and query) for this endpoint.
        /// </summary>
        Uri BaseUri { get; }

        /// <summary>
        /// Called by CoapClient/CoapHandler to send a <see cref="CoapPacket.Payload"/> to the specified <see cref="CoapPacket.Endpoint"/> using this endpoint
        /// </summary>
        /// <param name="packet"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        Task SendAsync(CoapPacket packet, CancellationToken token);

        /// <summary>
        /// Returns a string representation of the <see cref="ICoapEndpoint"/>.
        /// </summary>
        /// <param name="format"></param>
        /// <returns></returns>
        string ToString(CoapEndpointStringFormat format);
    }

    public interface ICoapClientEndpoint : ICoapEndpoint
    {
        /// <summary>
        /// Gets if this endpoint used for Multicast.
        /// </summary>
        /// <remarks>
        /// Multicast endpoints do not acknolwedge received confirmables.
        /// </remarks>
        bool IsMulticast { get; }

        /// <summary>
        /// Used by <see cref="Client.CoapClient"/> to receive data from this endpoint
        /// </summary>
        /// <returns></returns>
        Task<CoapPacket> ReceiveAsync(CancellationToken tokens);

        /// <summary>
        /// Used by <see cref="Client.CoapClient"/> to determine where to send the <see cref="CoapMessage"/> when no endpoint is specified (usually by its URI)
        /// </summary>
        /// <param name="message"></param>
        /// <returns></returns>
        Task<ICoapEndpointInfo> GetEndpointInfoFromMessage(CoapMessage message);
    }
}