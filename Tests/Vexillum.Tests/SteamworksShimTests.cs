using Steamworks;
using Xunit;

namespace Vexillum.Tests
{
    // Offline Steamworks shim (docs/PORTING.md step 5).
    public class SteamworksShimTests
    {
        [Fact]
        public void SteamIdsFromDifferentSeedsDiffer()
        {
            CSteamID a = SteamUser.ComputeOfflineSteamID("alice", 1000);
            CSteamID b = SteamUser.ComputeOfflineSteamID("alice", 1001);
            CSteamID c = SteamUser.ComputeOfflineSteamID("bob", 1000);

            Assert.NotEqual(a.m_SteamID, b.m_SteamID);
            Assert.NotEqual(a.m_SteamID, c.m_SteamID);
            Assert.True(a != b);
            Assert.True(a.IsValid());
            // Deterministic for the same seed.
            Assert.Equal(a, SteamUser.ComputeOfflineSteamID("alice", 1000));
        }

        [Fact]
        public void GetSteamIDIsStableWithinProcess()
        {
            CSteamID first = SteamUser.GetSteamID();
            CSteamID second = SteamUser.GetSteamID();
            Assert.Equal(first, second);
            Assert.NotEqual(0UL, first.m_SteamID);
        }

        [Fact]
        public void InvalidTicketEqualsDefaultTicket()
        {
            HAuthTicket def = default(HAuthTicket);
            Assert.True(HAuthTicket.Invalid == def);
            Assert.False(HAuthTicket.Invalid != def);
            Assert.Equal(0u, HAuthTicket.Invalid.m_HAuthTicket);
        }

        [Fact]
        public void GetAuthSessionTicketReturnsEmptyTicketWithoutThrowing()
        {
            byte[] token = new byte[1024];
            uint length = 123;
            HAuthTicket ticket = SteamUser.GetAuthSessionTicket(token, token.Length, out length);

            Assert.Equal(0u, length);
            Assert.True(ticket == HAuthTicket.Invalid);
            // The client-side cleanup path (Client.cs) compares the ticket then cancels it.
            SteamUser.CancelAuthTicket(ticket);
        }

        [Fact]
        public void ServerAcceptsAnyTicketOffline()
        {
            EBeginAuthSessionResult r = SteamUser.BeginAuthSession(new byte[0], 0, new CSteamID(42));
            Assert.Equal(EBeginAuthSessionResult.k_EBeginAuthSessionResultOK, r);
            SteamUser.EndAuthSession(new CSteamID(42));
        }

        [Fact]
        public void ApiInitialisesOfflineAndCastsAppIdFromIntLiteral()
        {
            Assert.True(Packsize.Test());
            Assert.True(DllCheck.Test());
            Assert.False(SteamAPI.RestartAppIfNecessary((AppId_t)349380));
            Assert.Equal(349380u, ((AppId_t)349380).m_AppId);
            Assert.True(SteamAPI.Init());
            Assert.False(string.IsNullOrEmpty(SteamFriends.GetPersonaName()));
            SteamAPI.RunCallbacks();
            SteamAPI.Shutdown();
        }

        [Fact]
        public void PostedCallbackIsDeliveredOnRunCallbacks()
        {
            SteamAPI.Init();
            byte seen = 0;
            using (Callback<GameOverlayActivated_t> cb = Callback<GameOverlayActivated_t>.Create(p => seen = p.m_bActive))
            {
                CallbackDispatcher.Post(new GameOverlayActivated_t { m_bActive = 1 });
                Assert.Equal(0, seen);
                SteamAPI.RunCallbacks();
                Assert.Equal(1, seen);
            }
            SteamAPI.Shutdown();
        }
    }
}
