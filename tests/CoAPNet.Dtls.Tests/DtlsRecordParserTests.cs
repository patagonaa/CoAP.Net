using CoAPNet.Dtls.Server;
using NUnit.Framework;
using System;

namespace CoAPNet.Dtls.Tests
{
    [TestFixture]
    public class DtlsRecordParserTests
    {
        private DtlsRecordParser GetSut()
        {
            return new DtlsRecordParser();
        }

        [TestCase("xjwXKNNEEEq1Y/fAvGGPdA==", "Gf79AAEAAAAAAADGPBco00QQSrVj98C8YY90ACka09mvw1PO5z6+LRkm1OOcunaZ1wUM7MY5rux8cLs71W03XvOkheBKYw==")]
        [TestCase(null, "Gf79AAEAAAAAAADGPBcoDQ==", Description = "Truncated")]
        [TestCase(null, "Fv79AAAAAAAAAAIAMxAAACcAAQAAAAAAJwAEdXNlciA4iAxb6rGFbpD936XkpLVVl0sgdZh9uJH/uLroo3hNbg==", Description = "ClientKeyExchange")]
        public void GetConnectionId(string? expectedCidBase64, string packetBytesBase64)
        {
            var sut = GetSut();
            var expected = expectedCidBase64 == null ? null : Convert.FromBase64String(expectedCidBase64);
            CollectionAssert.AreEqual(expected, sut.GetConnectionId(Convert.FromBase64String(packetBytesBase64), 16));
        }

        [TestCase(true, "Fv7/AAAAAAAAAAAAqgEAAJ4AAAAAAAAAnv79fwHWeghJmGi7dnSkKYWcnqfCuArHQa5J0NmXRa9RYMsAAAAQzKzAN8A1zK0AqgCyAJAA/wEAAGQAFgAAABcAAAAFAAUBAAAAAAANADAALggHCAgEAwUDBgMIBAgFCAYICQgKCAsEAQUBBgEEAgUCBgIDAwMBAwICAwIBAgIACgAQAA4AHQAeABcAGAEAAQEBAgALAAIBAAA2AAEA", Description = "Handshake+ClientHello")]
        [TestCase(false, "Fv7/AAAAAAAAAAAAqg==", Description = "Handshake Truncated")]
        [TestCase(false, "Fv79AAAAAAAAAAAAXAIAAFAAAAAAAAAAUP79YTGjk7G6hG0Lwr0RaCsXWDpWGwLNAkIlkbKAkvu/bVAAzKwAACgABQAAABcAAAALAAIBAAA2ABEQxjwXKNNEEEq1Y/fAvGGPdP8BAAEA", Description = "Handshake+ServerHello")]
        [TestCase(false, "Fv79AAAAAAAAAAIAMxAAACcAAQAAAAAAJwAEdXNlciA4iAxb6rGFbpD936XkpLVVl0sgdZh9uJH/uLroo3hNbg==", Description = "Handshake+ClientKeyExchange")]
        [TestCase(false, "F/79AAEAAAAAAAIAIedv3DQDpEmfPiIHLRRhel7D7mxnai0r1xQF2ItZ5yVYDg==", Description = "ApplicationData")]
        public void MayBeClientHello(bool expected, string packetBytesBase64)
        {
            var sut = GetSut();
            Assert.AreEqual(expected, sut.MayBeClientHello(Convert.FromBase64String(packetBytesBase64)));
        }
    }
}
