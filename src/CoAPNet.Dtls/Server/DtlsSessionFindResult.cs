namespace CoAPNet.Dtls.Server
{
    internal enum DtlsSessionFindResult
    {
        NewSession,
        UnknownCid,
        FoundByEndPoint,
        FoundByConnectionId,
        Invalid
    }
}
