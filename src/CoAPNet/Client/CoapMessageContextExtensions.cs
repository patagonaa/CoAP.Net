using System;

namespace CoAPNet.Client
{
    public static class CoapMessageContextExtensions
    {
        public static CoapBlockWiseContext CreateBlockWiseContext(this CoapMessage message, CoapClient client, CoapMessage response = null)
        {
            if (!message.Code.IsRequest())
                throw new ArgumentException($"A block-Wise context requires a base request message. Message code {message.Code} is invalid.", nameof(message));

            if (response != null && response.Code.IsRequest())
                throw new ArgumentException($"A block-Wise context response can not be set from a message code {message.Code}.", nameof(response));

            return new CoapBlockWiseContext(client, message, response);
        }
    }
}
