using CoAPNet.Dtls.Server;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using System;
using System.Linq;
using System.Net;

namespace CoAPNet.Dtls.Tests
{
    [TestFixture]
    public class DtlsSessionStoreTests
    {
        [Test]
        public void TryFind_NoCid_NoSession_NotFound()
        {
            var sessionStore = GetSut();
            Assert.AreEqual(DtlsSessionFindResult.NotFound, sessionStore.TryFindSession(Ep1, null, out _));
        }

        [Test]
        public void TryFind_Cid_NoSession_NotFound()
        {
            var sessionStore = GetSut();
            Assert.AreEqual(DtlsSessionFindResult.NotFound, sessionStore.TryFindSession(Ep1, Cid1, out _));
        }

        [Test(Description = "packet with cid from session endpoint, establishing session")]
        public void TryFind_Cid_EstablishingSession_FoundByEp()
        {
            var sessionStore = GetSut();
            var session = new TestSession(Ep1);
            sessionStore.Add(session);
            Assert.AreEqual(DtlsSessionFindResult.FoundByEndPoint, sessionStore.TryFindSession(Ep1, Cid1, out _));
        }

        [Test(Description = "packet without cid from session endpoint, establishing session")]
        public void TryFind_NoCid_EstablishingSession_FoundByEp()
        {
            var sessionStore = GetSut();
            var session = new TestSession(Ep1);
            sessionStore.Add(session);
            Assert.AreEqual(DtlsSessionFindResult.FoundByEndPoint, sessionStore.TryFindSession(Ep1, null, out _));
        }

        [Test(Description = "packet without cid from session endpoint, session with cid")]
        public void TryFind_NoCid_SessionWithCid_NotFound()
        {
            var sessionStore = GetSut();
            var session = new TestSession(Ep1);
            sessionStore.Add(session);
            session.ConnectionId = Cid1;
            sessionStore.NotifySessionAccepted(session);
            Assert.AreEqual(DtlsSessionFindResult.NotFound, sessionStore.TryFindSession(Ep1, null, out _));
        }

        [Test(Description = "packet with cid from different endpoint, session with cid")]
        public void TryFind_Cid_SessionWithCid_FoundByCid()
        {
            var sessionStore = GetSut();
            var session = new TestSession(Ep1);
            sessionStore.Add(session);
            session.ConnectionId = Cid1;
            sessionStore.NotifySessionAccepted(session);

            Assert.AreEqual(DtlsSessionFindResult.FoundByConnectionId, sessionStore.TryFindSession(Ep2, Cid1, out _));
        }

        [Test(Description = "one session with cid (found by cid), one establishing session (found by ep)")]
        public void TryFind_Cid_SessionWithCid_EstablishingSession()
        {
            var sessionStore = GetSut();
            var session1 = new TestSession(Ep1);
            sessionStore.Add(session1);
            session1.ConnectionId = Cid1;
            sessionStore.NotifySessionAccepted(session1);

            var session2 = new TestSession(Ep1);
            sessionStore.Add(session2);

            Assert.AreEqual(DtlsSessionFindResult.FoundByConnectionId, sessionStore.TryFindSession(Ep1, Cid1, out _));
            Assert.AreEqual(DtlsSessionFindResult.FoundByEndPoint, sessionStore.TryFindSession(Ep1, null, out _));
        }

        [Test]
        public void Add_ConflictingEstablishingSession_Throws()
        {
            var sessionStore = GetSut();
            var session1 = new TestSession(Ep1);
            sessionStore.Add(session1);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());

            var session2 = new TestSession(Ep1);
            Assert.Throws<InvalidOperationException>(() => sessionStore.Add(session2));
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());
        }

        [Test]
        public void Add_ConflictingEstablishedSession_Throws()
        {
            var sessionStore = GetSut();
            var session1 = new TestSession(Ep1);
            sessionStore.Add(session1);
            sessionStore.NotifySessionAccepted(session1);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());

            var session2 = new TestSession(Ep1);
            Assert.Throws<InvalidOperationException>(() => sessionStore.Add(session2));
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());
        }

        [Test]
        public void Add_ConflictingEstablishedSessionWithCid_Works()
        {
            var sessionStore = GetSut();
            var session1 = new TestSession(Ep1);
            sessionStore.Add(session1);
            session1.ConnectionId = Cid1;
            sessionStore.NotifySessionAccepted(session1);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());

            var session2 = new TestSession(Ep1);
            Assert.DoesNotThrow(() => sessionStore.Add(session2));
            Assert.AreEqual(2, sessionStore.GetSessions().Count());
            Assert.AreEqual(2, sessionStore.GetCount());
        }

        [TestCase(true, true, true, false, true)]
        [TestCase(true, true, true, false, false)]
        [TestCase(true, true, true, true, false)]
        [TestCase(true, true, false, false, true)]
        [TestCase(true, true, false, false, false)]
        public void Remove_SessionsEqualEp_RemovesCorrectSession(bool session1HasCid, bool session1Accepted, bool session2HasCid, bool session2HasSameCid, bool session2Accepted)
        {
            var sessionStore = GetSut();
            var session1 = new TestSession(Ep1);
            sessionStore.Add(session1);
            if (session1HasCid)
                session1.ConnectionId = Cid1;
            if (session1Accepted)
                sessionStore.NotifySessionAccepted(session1);

            var session2 = new TestSession(Ep1);
            sessionStore.Add(session2);
            if (session2HasCid)
                session2.ConnectionId = session2HasSameCid ? Cid1 : Cid2;
            if (session2Accepted)
                sessionStore.NotifySessionAccepted(session2);

            sessionStore.Remove(session2);
            Assert.AreEqual(session1, sessionStore.GetSessions().Single(), "Has the right session been removed?");
        }

        [TestCase(true, true, true, false, true)]
        [TestCase(true, true, true, false, false)]
        [TestCase(true, true, true, true, false)]
        [TestCase(true, true, false, false, true)]
        [TestCase(true, true, false, false, false)]
        public void Remove_SessionsEqualEp_RemovesCorrectSession_Reversed(bool session1HasCid, bool session1Accepted, bool session2HasCid, bool session2HasSameCid, bool session2Accepted)
        {
            var sessionStore = GetSut();
            var session1 = new TestSession(Ep1);
            sessionStore.Add(session1);
            if (session1HasCid)
                session1.ConnectionId = Cid1;
            if (session1Accepted)
                sessionStore.NotifySessionAccepted(session1);

            var session2 = new TestSession(Ep1);
            sessionStore.Add(session2);
            if (session2HasCid)
                session2.ConnectionId = session2HasSameCid ? Cid1 : Cid2;
            if (session2Accepted)
                sessionStore.NotifySessionAccepted(session2);

            sessionStore.Remove(session1);
            Assert.AreEqual(session2, sessionStore.GetSessions().Single(), "Has the right session been removed?");
        }

        [Test]
        public void AddRemove_GetCountGetSessions_NoSessions()
        {
            var sessionStore = GetSut();
            Assert.AreEqual(0, sessionStore.GetSessions().Count());
            Assert.AreEqual(0, sessionStore.GetCount());
        }

        [Test]
        public void AddRemove_GetCountGetSessions_EstablishingSession()
        {
            var sessionStore = GetSut();
            var session = new TestSession(Ep1);
            sessionStore.Add(session);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());

            sessionStore.Remove(session);
            Assert.AreEqual(0, sessionStore.GetSessions().Count());
            Assert.AreEqual(0, sessionStore.GetCount());
        }

        [Test]
        public void AddRemove_GetCountGetSessions_EstablishedSession_NoCid()
        {
            var sessionStore = GetSut();
            var session = new TestSession(Ep1);
            sessionStore.Add(session);
            sessionStore.NotifySessionAccepted(session);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());

            sessionStore.Remove(session);
            Assert.AreEqual(0, sessionStore.GetSessions().Count());
            Assert.AreEqual(0, sessionStore.GetCount());
        }

        [Test]
        public void AddRemove_GetCountGetSessions_EstablishedSession_Cid()
        {
            var sessionStore = GetSut();
            var session = new TestSession(Ep1);
            sessionStore.Add(session);
            session.ConnectionId = Cid1;
            sessionStore.NotifySessionAccepted(session);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());

            sessionStore.Remove(session);
            Assert.AreEqual(0, sessionStore.GetSessions().Count());
            Assert.AreEqual(0, sessionStore.GetCount());
        }

        [Test]
        public void AddRemove_GetCountGetSessions_EstablishedSession_Cid_ChangingEp()
        {
            var sessionStore = GetSut();
            var session = new TestSession(Ep1);
            sessionStore.Add(session);
            session.ConnectionId = Cid1;
            sessionStore.NotifySessionAccepted(session);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());

            session.EndPoint = Ep2; // endpoint may have changed in the meantime

            sessionStore.Remove(session);
            Assert.AreEqual(0, sessionStore.GetSessions().Count());
            Assert.AreEqual(0, sessionStore.GetCount());
        }

        [Test]
        public void AddRemove_GetCountGetSessions_OneEstablishingOneEstablishedSession()
        {
            var sessionStore = GetSut();
            var session1 = new TestSession(Ep1);
            sessionStore.Add(session1);
            sessionStore.NotifySessionAccepted(session1);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());

            var session2 = new TestSession(Ep2);
            sessionStore.Add(session2);

            Assert.AreEqual(2, sessionStore.GetSessions().Count());
            Assert.AreEqual(2, sessionStore.GetCount());

            sessionStore.Remove(session1);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());

            sessionStore.Remove(session2);
            Assert.AreEqual(0, sessionStore.GetSessions().Count());
            Assert.AreEqual(0, sessionStore.GetCount());
        }

        [Test]
        public void AddRemove_GetCountGetSessions_OneEstablishingOneEstablishedSession_Cid()
        {
            var sessionStore = GetSut();
            var session1 = new TestSession(Ep1);
            sessionStore.Add(session1);
            session1.ConnectionId = Cid1;
            sessionStore.NotifySessionAccepted(session1);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());

            var session2 = new TestSession(Ep2);
            sessionStore.Add(session2);

            Assert.AreEqual(2, sessionStore.GetSessions().Count());
            Assert.AreEqual(2, sessionStore.GetCount());

            sessionStore.Remove(session1);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());

            sessionStore.Remove(session2);
            Assert.AreEqual(0, sessionStore.GetSessions().Count());
            Assert.AreEqual(0, sessionStore.GetCount());
        }

        [Test]
        public void AddRemove_GetCountGetSessions_OneEstablishingOneEstablishedSession_Cid_SameEp()
        {
            var sessionStore = GetSut();
            var session1 = new TestSession(Ep1);
            sessionStore.Add(session1);
            session1.ConnectionId = Cid1;
            sessionStore.NotifySessionAccepted(session1);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());

            var session2 = new TestSession(Ep1);
            sessionStore.Add(session2);

            Assert.AreEqual(2, sessionStore.GetSessions().Count());
            Assert.AreEqual(2, sessionStore.GetCount());

            sessionStore.Remove(session1);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());
            Assert.AreEqual(session2, sessionStore.GetSessions().Single());

            sessionStore.Remove(session2);
            Assert.AreEqual(0, sessionStore.GetSessions().Count());
            Assert.AreEqual(0, sessionStore.GetCount());
        }

        [Test]
        public void AddRemove_GetCountGetSessions_OneEstablishingOneEstablishedSession_Cid_SameEp_Reversed()
        {
            var sessionStore = GetSut();
            var session1 = new TestSession(Ep1);
            sessionStore.Add(session1);
            session1.ConnectionId = Cid1;
            sessionStore.NotifySessionAccepted(session1);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());

            var session2 = new TestSession(Ep1);
            sessionStore.Add(session2);

            Assert.AreEqual(2, sessionStore.GetSessions().Count());
            Assert.AreEqual(2, sessionStore.GetCount());

            sessionStore.Remove(session2);
            Assert.AreEqual(1, sessionStore.GetSessions().Count());
            Assert.AreEqual(1, sessionStore.GetCount());
            Assert.AreEqual(session1, sessionStore.GetSessions().Single());

            sessionStore.Remove(session1);
            Assert.AreEqual(0, sessionStore.GetSessions().Count());
            Assert.AreEqual(0, sessionStore.GetCount());
        }

        [Test]
        public void NotifyAccepted_DuplicateCid_Throws()
        {
            var sessionStore = GetSut();
            var session1 = new TestSession(Ep1);
            sessionStore.Add(session1);
            session1.ConnectionId = Cid1;
            sessionStore.NotifySessionAccepted(session1);

            Assert.AreEqual(DtlsSessionFindResult.FoundByConnectionId, sessionStore.TryFindSession(Ep2, Cid1, out _));

            var session2 = new TestSession(Ep2);
            sessionStore.Add(session2);
            session2.ConnectionId = Cid1;

            Assert.Throws<InvalidOperationException>(() => sessionStore.NotifySessionAccepted(session2));
            sessionStore.Remove(session2); // CoapDtlsServerTransport.HandleSession does this when an exception is thrown

            Assert.AreEqual(DtlsSessionFindResult.FoundByConnectionId, sessionStore.TryFindSession(Ep2, Cid1, out var foundSession));
            Assert.AreEqual(session1, foundSession, "session 1 should still be in session store");
        }

        [Test]
        public void FullRun_SessionWithoutCid()
        {
            var sessionStore = GetSut();

            Assert.AreEqual(DtlsSessionFindResult.NotFound, sessionStore.TryFindSession(Ep1, null, out _), "new endpoint => new session");

            // new session is added
            var session = new TestSession(Ep1);
            sessionStore.Add(session);

            Assert.AreEqual(DtlsSessionFindResult.FoundByEndPoint, sessionStore.TryFindSession(Ep1, null, out _), "establishing session => found by endpoint");

            // session has been accepted (without cid)
            sessionStore.NotifySessionAccepted(session);

            Assert.AreEqual(DtlsSessionFindResult.FoundByEndPoint, sessionStore.TryFindSession(Ep1, null, out _), "established session => found by endpoint");

            // session has been removed (is finished)
            sessionStore.Remove(session);

            Assert.AreEqual(DtlsSessionFindResult.NotFound, sessionStore.TryFindSession(Ep1, null, out _), "new endpoint => new session");
        }

        [Test]
        public void FullRun_SessionWithCid()
        {
            var sessionStore = GetSut();

            Assert.AreEqual(DtlsSessionFindResult.NotFound, sessionStore.TryFindSession(Ep1, null, out _), "new endpoint => new session");

            // new session is added
            var session = new TestSession(Ep1);
            sessionStore.Add(session);

            Assert.AreEqual(DtlsSessionFindResult.FoundByEndPoint, sessionStore.TryFindSession(Ep1, null, out _), "establishing session => found by endpoint");

            Assert.AreEqual(DtlsSessionFindResult.FoundByEndPoint, sessionStore.TryFindSession(Ep1, Cid1, out _), "establishing session (with cid) => found by endpoint");

            // session has been accepted (with cid)
            session.ConnectionId = Cid1;
            sessionStore.NotifySessionAccepted(session);

            Assert.AreEqual(DtlsSessionFindResult.FoundByConnectionId, sessionStore.TryFindSession(Ep1, Cid1, out _), "established session => found by cid");

            // packet from new endpoint
            Assert.AreEqual(DtlsSessionFindResult.FoundByConnectionId, sessionStore.TryFindSession(Ep2, Cid1, out _), "established session (new ep) => found by cid");

            // session has been removed (is finished)
            sessionStore.Remove(session);

            Assert.AreEqual(DtlsSessionFindResult.NotFound, sessionStore.TryFindSession(Ep1, null, out _), "new endpoint => new session");
        }

        [Test]
        public void FullRun_EndpointReused_SessionWithCid()
        {
            var sessionStore = GetSut();

            // add and accept new session with cid
            Assert.AreEqual(DtlsSessionFindResult.NotFound, sessionStore.TryFindSession(Ep1, null, out _), "new endpoint => new session");
            var session1 = new TestSession(Ep1);
            sessionStore.Add(session1);
            session1.ConnectionId = Cid1;
            sessionStore.NotifySessionAccepted(session1);

            // add and accept new session with same endpoint
            Assert.AreEqual(DtlsSessionFindResult.NotFound, sessionStore.TryFindSession(Ep1, null, out _), "new endpoint => new session");
            var session2 = new TestSession(Ep1);
            sessionStore.Add(session2);
            session2.ConnectionId = Cid2;
            sessionStore.NotifySessionAccepted(session2);

            // both sessions can be found by their cid (independent of the used endpoint)
            Assert.AreEqual(DtlsSessionFindResult.FoundByConnectionId, sessionStore.TryFindSession(Ep2, Cid1, out var foundByCid1));
            Assert.AreEqual(session1, foundByCid1);

            Assert.AreEqual(DtlsSessionFindResult.FoundByConnectionId, sessionStore.TryFindSession(Ep2, Cid2, out var foundByCid2));
            Assert.AreEqual(session2, foundByCid2);
        }

        [Test(Description =
            "Usually improbable case, where a retransmitted/replayed ClientHello creates a second session after the first one has been established, " +
            "in which case packets with a connection id should still reach the first session as to not disrupt the session.")]
        public void FullRun_EndpointReused_SessionWithCid_BothSessionsReachableDuringAccepting()
        {
            var sessionStore = GetSut();

            // add and accept new session with cid
            Assert.AreEqual(DtlsSessionFindResult.NotFound, sessionStore.TryFindSession(Ep1, null, out _), "new endpoint => new session");
            var session1 = new TestSession(Ep1);
            sessionStore.Add(session1);
            session1.ConnectionId = Cid1;
            sessionStore.NotifySessionAccepted(session1);

            // add (but don't accept) new session with same endpoint
            Assert.AreEqual(DtlsSessionFindResult.NotFound, sessionStore.TryFindSession(Ep1, null, out _), "new endpoint => new session");
            var session2 = new TestSession(Ep1);
            sessionStore.Add(session2);

            // both sessions can be found (session 1 by cid, establishing session 2 by ep)
            Assert.AreEqual(DtlsSessionFindResult.FoundByConnectionId, sessionStore.TryFindSession(Ep1, Cid1, out var foundByCid));
            Assert.AreEqual(session1, foundByCid);

            Assert.AreEqual(DtlsSessionFindResult.FoundByEndPoint, sessionStore.TryFindSession(Ep1, null, out var foundByEp));
            Assert.AreEqual(session2, foundByEp);
        }

        // these are properties so we get a new instance each time to check for proper equality check (instead of == which doesn't work here)
        private IPEndPoint Ep1 => new IPEndPoint(IPAddress.Parse("172.0.0.11"), 1111);
        private IPEndPoint Ep2 => new IPEndPoint(IPAddress.Parse("172.0.0.22"), 2222);
        private byte[] Cid1 => new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        private byte[] Cid2 => new byte[] { 0xCA, 0xFE, 0xBA, 0xBE };

        private static DtlsSessionStore<TestSession> GetSut()
        {
            return new DtlsSessionStore<TestSession>(NullLogger<DtlsSessionStore<TestSession>>.Instance);
        }
    }

    internal class TestSession : IDtlsSession
    {
        public TestSession(IPEndPoint endPoint)
        {
            EndPoint = endPoint;
        }
        public byte[]? ConnectionId { get; set; }

        public IPEndPoint EndPoint { get; set; }
    }
}
