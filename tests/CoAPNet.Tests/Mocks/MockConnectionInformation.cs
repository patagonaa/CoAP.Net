using Moq;

namespace CoAPNet.Tests.Mocks
{
    public class MockConnectionInformation : ICoapConnectionInformation
    {
        public MockConnectionInformation(ICoapEndpoint endPoint)
        {
            LocalEndpoint = endPoint;
        }

        public ICoapEndpoint LocalEndpoint { get; }

        public ICoapEndpointInfo RemoteEndpoint => new Mock<ICoapEndpointInfo>().Object;
    }
}