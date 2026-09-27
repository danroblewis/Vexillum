using System;
using Steamworks;
using Xunit;

namespace Vexillum.Acceptance.toolsconfig
{
    /// <summary>
    /// The offline Steamworks shim (Shims/Steamworks/SteamworksStub.cs) where
    /// Tests/Vexillum.Tests/SteamworksShimTests stops: the per-process
    /// identity compared across two real processes, and the Init/Shutdown
    /// gating of RunCallbacks. SteamAPI and CallbackDispatcher are static, so
    /// the class is serial.
    /// </summary>
    [Collection(ToolsConfigCollection.Name)]
    public class SteamShimTests
    {
        // TOOLS-21
        [Fact]
        public void Steam_ids_differ_between_this_process_and_a_spawned_server_and_follow_the_seed_rule()
        {
            CSteamID mine = SteamUser.GetSteamID();
            Assert.NotEqual(0UL, mine.m_SteamID);
            Assert.Equal(mine, SteamUser.GetSteamID());
            Assert.Equal(SteamUser.ComputeOfflineSteamID(Environment.UserName, Environment.ProcessId), mine);

            using (ScratchRuntime rt = new ScratchRuntime())
            using (ServerProcess s = new ServerProcess(rt))
            {
                ulong theirs = ulong.Parse(s.Console.Eval("Steamworks.SteamUser.GetSteamID().m_SteamID").Trim());
                Assert.NotEqual(0UL, theirs);
                Assert.NotEqual(mine.m_SteamID, theirs);
                Assert.Equal(theirs, ulong.Parse(s.Console.Eval("Steamworks.SteamUser.GetSteamID().m_SteamID").Trim()));
                // ComputeOfflineSteamID(persona name, pid): the server's id follows from its pid.
                Assert.Equal(SteamUser.ComputeOfflineSteamID(SteamFriends.GetPersonaName(), s.Pid).m_SteamID, theirs);
            }
        }

        // TOOLS-21
        [Fact]
        public void Callbacks_are_delivered_only_between_Init_and_Shutdown()
        {
            SteamAPI.Shutdown();
            int seen = 0;
            using (Callback<GameOverlayActivated_t> cb = Callback<GameOverlayActivated_t>.Create(p => seen += p.m_bActive))
            {
                CallbackDispatcher.Post(new GameOverlayActivated_t { m_bActive = 1 });
                SteamAPI.RunCallbacks();
                Assert.Equal(0, seen);                    // s_Initialized false: the queue is not drained

                Assert.True(SteamAPI.Init());
                SteamAPI.RunCallbacks();
                Assert.Equal(1, seen);                    // the queued payload arrives once
                SteamAPI.RunCallbacks();
                Assert.Equal(1, seen);

                SteamAPI.Shutdown();
                CallbackDispatcher.Post(new GameOverlayActivated_t { m_bActive = 1 });
                SteamAPI.RunCallbacks();
                Assert.Equal(1, seen);                    // after Shutdown nothing is delivered
            }
            // Drain the payload left in the queue so it cannot reach a later test.
            SteamAPI.Init();
            SteamAPI.RunCallbacks();
            SteamAPI.Shutdown();
            Assert.Equal(1, seen);                        // the disposed callback is unregistered
        }

        // TOOLS-21
        [Fact]
        public void Offline_api_surface_matches_what_Program_Main_and_the_menus_expect()
        {
            Assert.False(SteamAPI.IsSteamRunning());
            Assert.False(SteamAPI.RestartAppIfNecessary((AppId_t)349380));   // so Program.Main never exits early
            Assert.Equal(349380u, SteamUtils.GetAppID().m_AppId);
            Assert.False(SteamUtils.IsOverlayEnabled());
            string expected = string.IsNullOrWhiteSpace(Environment.UserName) ? "Player" : Environment.UserName;
            Assert.Equal(expected, SteamFriends.GetPersonaName());
            Assert.False(SteamUser.BLoggedOn());

            byte[] ticket = new byte[1024];
            uint length;
            Assert.Equal(HAuthTicket.Invalid, SteamUser.GetAuthSessionTicket(ticket, ticket.Length, out length));
            Assert.Equal(0u, length);
            Assert.Equal(EBeginAuthSessionResult.k_EBeginAuthSessionResultOK, SteamUser.BeginAuthSession(new byte[0], 0, SteamUser.GetSteamID()));
        }
    }
}
