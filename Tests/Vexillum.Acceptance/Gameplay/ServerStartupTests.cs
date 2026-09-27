using System;
using System.Collections.Generic;
using System.IO;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.servergameplay
{
    /// <summary>SRV-01: a fresh headless server on the shipped defaults.</summary>
    public class FreshServerFixture : GameplayFixture
    {
    }

    public class ServerStartupTests : IClassFixture<FreshServerFixture>
    {
        private readonly FreshServerFixture fx;

        public ServerStartupTests(FreshServerFixture fx)
        {
            this.fx = fx;
        }

        // SRV-01
        [Fact]
        public void Fresh_server_is_headless_ready_and_has_no_players()
        {
            Assert.True(fx.Server.IsRunning);
            Assert.True(fx.Console.ServerReady());
            Assert.True(fx.Console.EvalT<bool>("Util.IsServer"));
            // AddBots is skipped on the first level (serverThread is null in setLevel) and
            // UpdateBots only runs on join/leave, so nobody is in the player set.
            Assert.Equal(0, fx.Console.EvalT<int>("Sync(() => ((System.Collections.IEnumerable)Server.players).Cast<Player>().Count())"));
            Assert.Empty(fx.Console.ServerPlayers());
        }

        // SRV-01
        [Fact]
        public void Fresh_server_places_both_flags_at_their_regions()
        {
            Vec2? green = fx.FlagPosition(PlayerClass.Green);
            Vec2? blue = fx.FlagPosition(PlayerClass.Blue);
            Assert.NotNull(green);
            Assert.NotNull(blue);
            Assert.Equal(Bases.GreenFlag, green.Value);
            Assert.Equal(Bases.BlueFlag, blue.Value);
            List<DebugConsole.EntityInfo> ents = fx.Console.ServerEntities();
            Assert.Single(ents, e => e.Type == "GreenFlagEntity");
            Assert.Single(ents, e => e.Type == "BlueFlagEntity");
            Assert.DoesNotContain(ents, e => e.Type == "HumanoidEntity");
        }

        // SRV-01: no graphics device on the server (the level's textures stay null, Util.IsServer guards)
        [Fact]
        public void Server_level_has_no_textures()
        {
            Assert.True(fx.Console.EvalT<bool>("Sync(() => Server.level.MainTexture == null)"));
            Assert.Equal("bases", fx.Console.ServerMap());
        }
    }

    /// <summary>SRV-22: Server/settings.txt keys are honoured by the running server.</summary>
    public class CustomSettingsFixture : GameplayFixture
    {
        public int ConfiguredPort;

        protected override void Settings(ScratchRuntime runtime, ServerProcess.Options options)
        {
            ConfiguredPort = FreePort.Tcp();
            // The launcher rewrites the port line from --port; give it the same value so
            // settings.txt and the listener agree and the test reads the file's key back.
            options.Port = ConfiguredPort;
            runtime.SetServerSetting("port", ConfiguredPort.ToString());
            runtime.SetServerSetting("maxplayers", "3");
            runtime.SetServerSetting("weapons", "SMG Sword");
            runtime.SetServerSetting("maxcaptures", "2");
            runtime.SetServerSetting("respawntime", "1500");
            runtime.SetServerSetting("maps", "bases");
            runtime.SetServerSetting("maxbots", "1");
        }
    }

    public class ServerSettingsTests : IClassFixture<CustomSettingsFixture>
    {
        private readonly CustomSettingsFixture fx;

        public ServerSettingsTests(CustomSettingsFixture fx)
        {
            this.fx = fx;
        }

        // SRV-22
        [Fact]
        public void Settings_port_weapons_maxcaptures_and_respawntime_are_applied()
        {
            Assert.Equal(fx.ConfiguredPort, fx.Console.EvalT<int>("Server.port"));
            Assert.Equal(fx.ConfiguredPort.ToString(), fx.Runtime.GetServerSetting("port"));
            Assert.Equal(3, fx.Console.EvalT<int>("Server.maxPlayers"));
            Assert.Equal(2, fx.Console.EvalT<int>("Server.gameMode.maxCaptures"));
            Assert.Equal(1500, fx.Console.EvalT<int>("Server.gameMode.respawnTime"));

            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            {
                // packet 5: inventory built from the weapons line, in order
                Assert.Equal(new string[] { "SMG", "Sword" }, a.Me.Weapons);
                PlayerSpawnPacket spawn = a.WaitFor<PlayerSpawnPacket>(p => p.SteamId == a.SteamId, 5);
                Assert.Equal(new byte[] { 9, 10 }, spawn.WeaponTypeIndexes);
                // packet 120 MAX_CAPTURES carries the configured value
                GameModeBytePacket mc = a.WaitFor<GameModeBytePacket>(p => p.Command == Protocol.GameMode.MaxCaptures, 10);
                Assert.Equal(2, mc.Value);
                // exactly zero bots while alone (maxbots 1 - 1 human)
                Assert.Equal(0, fx.BotCount());

                // respawn after a death takes respawntime (1.5 s), not the 5 s default
                int mark = a.PacketCount;
                fx.Kill(a.Name);
                HealthPacket dead = a.WaitFor<HealthPacket>(p => p.EntityId == a.MyEntityId && p.Health == 0, TimeSpan.FromSeconds(5), mark);
                TeleportPacket tp = a.WaitFor<TeleportPacket>(null, TimeSpan.FromSeconds(10), mark);
                double dt = (tp.ReceivedAt - dead.ReceivedAt).TotalSeconds;
                Assert.InRange(dt, 1.4, 2.5);
                fx.Leave(a);
            }
        }

        // SRV-22: a single-entry map list makes every /newgame pick the same map
        [Fact]
        public void Single_map_list_always_yields_that_map()
        {
            Assert.Equal("bases", fx.Console.ServerMap());
            for (int i = 0; i < 5; i++)
                Assert.Equal("bases", fx.Console.Eval("LevelLoader.GetRandomLevel()").Trim());
        }
    }

    public class MalformedSettingsTests
    {
        // SRV-22: a malformed line makes Program.Main log the settings error and exit
        [Fact]
        public void Malformed_settings_line_stops_the_server_at_startup()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                rt.SetServerSetting("maxplayers", "x");
                ServerProcess.Options o = new ServerProcess.Options();
                o.WaitForReady = false;
                o.DebugConsole = false;
                using (ServerProcess server = new ServerProcess(rt, o))
                {
                    Assert.NotNull(server.WaitFor("Error opening settings.txt", 20));
                    Assert.True(server.WaitForExit(TimeSpan.FromSeconds(20)), "process exits");
                    Assert.NotEqual(0, server.ExitCode);
                    Assert.DoesNotContain(ServerProcess.ReadyMarker, server.Output);
                }
            }
        }
    }
}
