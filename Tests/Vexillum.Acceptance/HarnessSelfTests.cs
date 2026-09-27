using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using Vexillum.Entities;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance
{
    /// <summary>
    /// Self-tests of the harness: every helper is exercised against the real
    /// server and the real game classes. Test writers can copy these as
    /// templates (docs/TESTING.md).
    /// </summary>
    public class ScratchRuntimeSelfTests
    {
        [Fact]
        public void CopiesRuntimeWithoutExecutablesAndLogs()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                Assert.True(Directory.Exists(rt.ContentDir), "Content/ copied");
                Assert.True(File.Exists(rt.MapPath("bases")), "Maps/bases.map copied");
                Assert.True(File.Exists(rt.MapPath("complex")), "Maps/complex.map copied");
                Assert.True(File.Exists(rt.ServerSettingsPath), "Server/settings.txt copied");
                Assert.True(File.Exists(Path.Combine(rt.Root, "settings.xml")));
                Assert.True(File.Exists(Path.Combine(rt.Root, "controls.xml")));
                Assert.Empty(Directory.GetFiles(rt.Root, "*.exe"));
                Assert.False(File.Exists(rt.ServerLogPath), "no stale server log");
                Assert.False(File.Exists(Path.Combine(rt.Root, "lock")), "no lock file");
                Assert.NotEqual(Repo.RuntimeDir, rt.Root);
                Assert.Equal(new List<string> { "bases", "complex" }, rt.MapNames());

                rt.RemoveMap("complex");
                Assert.Equal(new List<string> { "bases" }, rt.MapNames());
                Assert.True(File.Exists(Path.Combine(Repo.RuntimeDir, "Maps", "complex.map")), "Test/ untouched");
            }
        }

        [Fact]
        public void EditsServerSettingsKeysInPlace()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                Assert.Equal("6", rt.GetServerSetting("maxbots"));
                Assert.Equal("bases complex", rt.GetServerSetting("maps"));
                rt.SetServerSetting("maxbots", "0");
                rt.SetServerSetting("maps", "complex");
                rt.SetServerSetting("newkey", "x y");
                string text = File.ReadAllText(rt.ServerSettingsPath);
                Assert.Contains("#Maximum number of players\nmaxplayers 12", text);
                Assert.Equal("0", rt.GetServerSetting("maxbots"));
                Assert.Equal("complex", rt.GetServerSetting("maps"));
                Assert.Equal("x y", rt.GetServerSetting("newkey"));
                Assert.Equal(1, text.Split("maxbots").Length - 1);
                // The file must still parse with the author's config reader.
                Util.ServerConfig sc = Util.ParseServerConfig(text.Replace("\r", ""));
                Assert.Equal(0, sc.maxBots);
                Assert.Equal(new string[] { "complex" }, sc.maps);
            }
        }

        [Fact]
        public void DisposeDeletesTheFolder()
        {
            ScratchRuntime rt = new ScratchRuntime();
            string root = rt.Root;
            rt.Dispose();
            Assert.False(Directory.Exists(root));
        }
    }

    public class FreePortSelfTests
    {
        [Fact]
        public void PortsAreDistinctAndBindable()
        {
            int a = FreePort.Tcp();
            int b = FreePort.Tcp();
            int u = FreePort.Udp();
            Assert.NotEqual(a, b);
            Assert.InRange(a, 1024, 65535);
            Assert.True(FreePort.IsTcpFree(a));
            using (TcpListener l = new TcpListener(System.Net.IPAddress.Any, a))
            {
                l.Start();
                Assert.False(FreePort.IsTcpFree(a));
                l.Stop();
            }
            using (UdpClient c = new UdpClient(u))
                Assert.Equal(u, ((System.Net.IPEndPoint)c.Client.LocalEndPoint).Port);
        }
    }

    public class MapFileSelfTests
    {
        [Theory]
        [InlineData("bases", 3914, 1024)]
        [InlineData("complex", 2736, 818)]
        public void ParsesShippedMaps(string name, int w, int h)
        {
            MapFile m = MapFile.Read(Path.Combine(Repo.RuntimeDir, "Maps", name + ".map"));
            Assert.Equal(w, m.Width);
            Assert.Equal(h, m.Height);
            Assert.NotNull(m.Entry("data.txt"));
            Assert.NotNull(m.Entry("collision.png"));
            Assert.False(string.IsNullOrEmpty(m.LongName));
            Assert.Equal(new FileInfo(m.Path).Length - 4, m.CompressedPayload.Length);
        }

        [Fact]
        public void ProtocolTableMatchesStreamHelper()
        {
            FieldInfo f = typeof(global::Vexillum.net.StreamHelper).GetField("entityTypes", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.NotNull(f);
            List<string> real = (List<string>)f.GetValue(null);
            Assert.Equal(new List<string>(Protocol.EntityTypes), real);
            Assert.Equal(Protocol.ProtocolVersion, VexillumConstants.PROTOCOL_VERSION);
            Assert.Equal(Protocol.DefaultPort, VexillumConstants.DEFAULT_PORT);
            Assert.Equal(Protocol.MapMagic, LevelLoader.MagicNumber);
            Assert.Equal(new byte[] { 1, 2, 4 }, new byte[] { Protocol.EncodeMovement(true, false, false), Protocol.EncodeMovement(false, true, false), Protocol.EncodeMovement(false, false, true) });
        }
    }

    /// <summary>One server (default settings, map forced to bases) shared by the tests of this class.</summary>
    public class BasesServerFixture : ServerFixture
    {
        protected override void Configure(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("maps", "bases");
            // maxbots stays at the default 6: Server.UpdateBots hangs the server when
            // RemoveBot cannot find a bot on the team it picks (docs/PORTING.md, known bug),
            // which happens with small maxbots values; see docs/TESTING.md.
        }
    }

    public class ServerHarnessSelfTests : IClassFixture<BasesServerFixture>
    {
        private readonly BasesServerFixture fx;

        public ServerHarnessSelfTests(BasesServerFixture fx)
        {
            this.fx = fx;
        }

        [Fact]
        public void ServerIsReadyOnItsPortAndAnswersTheProbe()
        {
            Assert.True(fx.Server.IsRunning);
            Assert.Contains(ServerProcess.ReadyMarker, fx.Server.Output);
            Assert.Contains("port=" + fx.Server.Port, fx.Server.Output);
            Assert.Equal(fx.Server.Port.ToString(), fx.Runtime.GetServerSetting("port"));
            Assert.Equal(true, ScriptedClient.Probe("127.0.0.1", fx.Server.Port, TimeSpan.FromSeconds(5)));
        }

        [Fact]
        public void DebugConsoleEvaluatesInsideTheServer()
        {
            DebugConsole c = fx.Server.Console;
            Assert.Equal(2, c.EvalT<int>("1+1"));
            Assert.Equal("bases", c.ServerMap());
            Assert.True(c.ServerReady());
            int f1 = c.ServerFrame();
            Assert.True(SpinUntil(delegate() { return c.ServerFrame() > f1; }, TimeSpan.FromSeconds(3)), "frame advances at 60 Hz");
            DebugEvalException ex = Assert.Throws<DebugEvalException>(delegate() { c.Eval("this is not C#"); });
            Assert.Contains("error", ex.Message);
            List<DebugConsole.EntityInfo> ents = c.ServerEntities();
            Assert.Contains(ents, e => e.Type == "GreenFlagEntity");
            Assert.Contains(ents, e => e.Type == "BlueFlagEntity");
        }

        [Fact]
        public void ScriptedClientJoinsAndReceivesTheReferenceTerrain()
        {
            using (ScriptedClient c = new ScriptedClient(fx.Server))
            {
                c.JoinGame("harness-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                Assert.Equal("bases", c.MapName);
                Assert.True(c.MyEntityId > 0, "own entity id assigned");
                Assert.NotNull(c.Terrain);
                Assert.Equal(3914, c.Terrain.Width);
                Assert.Equal(1024, c.Terrain.Height);
                Assert.Equal("059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3", c.Terrain.Sha256Hex());
                Assert.NotNull(c.Me);
                Assert.Equal(100f, c.Me.Health);
                Assert.Contains(c.Me.Class, new PlayerClass[] { PlayerClass.Green, PlayerClass.Blue });
                Assert.Equal(new string[] { "RocketLauncher", "SMG", "Sword" }, c.Me.Weapons);
                Assert.NotNull(c.MyEntity);
                Assert.True(fx.Server.WaitFor("logged in as " + c.Name, 5) != null);
                // The server pushes the game-mode state and pings after the join.
                c.WaitFor<GameModeBytePacket>(p => p.Command == Protocol.GameMode.MaxCaptures && p.Value == 4, 10);
                Assert.NotNull(fx.Server.Console.ServerPlayer(c.Name));
            }
        }

        [Fact]
        public void SecondClientIsBroadcastAsPlayerSpawnToTheFirst()
        {
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                a.JoinGame("first-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                int before = a.PacketCount;
                b.JoinGame("second-" + Guid.NewGuid().ToString("N").Substring(0, 6));
                PlayerSpawnPacket spawn = a.WaitFor<PlayerSpawnPacket>(p => p.Name == b.Name, TimeSpan.FromSeconds(10), before);
                Assert.Equal(b.MyEntityId, spawn.EntityId);
                Assert.Equal(b.SteamId, spawn.SteamId);
                Assert.NotNull(a.PlayerNamed(b.Name));
                // and b learned about a through its own join (SendPlayers)
                Assert.NotNull(b.PlayerNamed(a.Name));
                Assert.True(b.Entities.ContainsKey(a.MyEntityId));
                // the frame counter keeps advancing (packet 8 when nothing moved, else packet 30)
                Assert.True(a.WaitUntil(() => a.Frame > 0, TimeSpan.FromSeconds(5)), "a frame was received");
                int f = a.Frame;
                Assert.True(a.WaitUntil(() => a.Frame > f, TimeSpan.FromSeconds(5)), "frame advances");
            }
        }

        private static bool SpinUntil(Func<bool> cond, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (cond())
                    return true;
                System.Threading.Thread.Sleep(50);
            }
            return cond();
        }
    }

    public class KnownServerBugs
    {
        [Fact(Skip = "Known original bug: Server.UpdateBots loops forever on the Server Main thread when RemoveBot finds no bot of the class it picks (e.g. maxbots 0 and one human joins), docs/PORTING.md")]
        public void ServerKeepsServingWithMaxbotsZero()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                rt.SetServerSetting("maxbots", "0");
                using (ServerProcess server = new ServerProcess(rt))
                using (ScriptedClient c = new ScriptedClient(server))
                {
                    c.JoinGame("solo");
                    Assert.Equal(true, ScriptedClient.Probe("127.0.0.1", server.Port, TimeSpan.FromSeconds(5)));
                }
            }
        }
    }

    [Collection(GameStateCollection.Name)]
    public class HeadlessLevelSelfTests
    {
        [Theory]
        [InlineData("bases", 3914, 1024, "059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3")]
        [InlineData("complex", 2736, 818, "d5d14f4c61b455f074f82222ad6da7a5a491bf4ddcd53bde8056c3f2974695b6")]
        public void LoadsShippedMapsAndStepsPhysics(string map, int w, int h, string sha)
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                HeadlessLevel level = HeadlessLevel.Load(rt, map);
                Assert.Equal(w, level.Width);
                Assert.Equal(h, level.Height);
                Assert.Equal(sha, level.Snapshot().Sha256Hex());
                Assert.True(level.Spawns.ContainsKey(PlayerClass.Green));
                Assert.True(level.Spawns.ContainsKey(PlayerClass.Blue));
                Assert.True(level.Flags.ContainsKey(PlayerClass.Green));

                HumanoidEntity e = level.AddHumanoidAtSpawn(PlayerClass.Green);
                Assert.True(e.ID >= 2, "ids start after ResetID");
                Vec2 start = e.Position;
                level.StepFrames(5);
                Assert.Equal(5, level.frame);
                // gravity acted: the entity is falling (or already landed below its spawn point)
                Assert.True(e.Position.Y < start.Y || e.Velocity.Y != 0 || !e.jumping, "physics ran");
                Assert.True(level.EntityList.Contains(e));
                Assert.Equal(e.ID, level.EntityAt((int)e.Position.X - (int)e.HalfSize.X, (int)e.Position.Y, null));
            }
        }
    }
}
