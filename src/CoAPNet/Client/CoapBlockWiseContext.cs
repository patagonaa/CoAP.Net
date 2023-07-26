using CoAPNet.Options;
using System;
using System.Collections.Generic;
using System.Text;

namespace CoAPNet.Client
{

    public class CoapBlockWiseContext
    {
        public CoapClient Client { get; }

        public CoapMessage Request { get; internal set; }

        public CoapMessage Response { get; internal set; }

        public CoapMessageIdentifier MessageId { get; internal set; }

        public CoapBlockWiseContext(CoapClient client, CoapMessage request, CoapMessage response = null)
        {
            Client = client
                ?? throw new ArgumentNullException(nameof(client));

            Request = request?.Clone(true)
                ?? throw new ArgumentNullException(nameof(request));

            Response = response?.Clone(true);
        }
    }
}
