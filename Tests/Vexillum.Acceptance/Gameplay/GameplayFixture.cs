using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.servergameplay
{
    /// <summary>
    /// One server on the bases map for a test class, plus the helpers the
    /// gameplay tests share. The catalogue's "maxbots 0" scenarios cannot be
    /// configured through settings.txt (Server.UpdateBots hangs the Server
    /// Main thread when a human joins and there is no bot to remove, see
    /// docs/PORTING.md "Known original bugs"), so <see cref="Join"/> keeps the
    /// public field <c>Server.maxBots</c> equal to the number of open client
    /// connections through the debug console: UpdateBots then computes
    /// maxAllowedBots == 0 on every join and leave and neither adds nor removes
    /// a bot. Bot tests (BotTests) use a fixture of their own with the shipped
    /// default. Override <see cref="Settings"/> to edit Server/settings.txt.
    /// </summary>
    public class GameplayFixture : ServerFixture
    {
        private readonly List<ScriptedClient> live = new List<ScriptedClient>();
        private readonly object joinLock = new object();

        protected override void Configure(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("maps", "bases");
            Settings(runtime, options);
        }

        /// <summary>Edit Server/settings.txt (already forced to maps bases) before the server starts.</summary>
        protected virtual void Settings(ScratchRuntime runtime, ServerProcess.Options options)
        {
        }

        public GameplayFixture()
        {
            WaitForServerObject(Server);
        }

        public DebugConsole Console { get { return Server.Console; } }

        /// <summary>
        /// Waits until the debug console's <c>Server</c> global exists and the
        /// server reports ready. The harness ReadyMarker is logged by the
        /// acceptor thread started inside the Server constructor, before
        /// Program.Main assigns the static field the console resolves
        /// <c>Server</c> from (Server/Program.cs:56, Server/PortProgram.cs:95),
        /// so the first script of a fresh fixture would otherwise race that
        /// assignment ("Cannot perform runtime binding on a null reference").
        /// </summary>
        public static void WaitForServerObject(ServerProcess server)
        {
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            Exception last = null;
            while (true)
            {
                try
                {
                    if (server.Console.EvalT<bool>("Server != null && (bool)Server.ready"))
                        return;
                }
                catch (DebugEvalException e)
                {
                    last = e;
                }
                if (DateTime.UtcNow > deadline)
                    throw new TimeoutException("debug console never saw a ready Server object" + (last != null ? ": " + last.Message : ""));
                Thread.Sleep(50);
            }
        }

        /// <summary>A C# expression (for the debug console) evaluating to the ServerPlayer named <paramref name="name"/>.</summary>
        public static string P(string name)
        {
            return "((System.Collections.IEnumerable)Server.players).Cast<ServerPlayer>().First(p => p.name == \"" + name + "\")";
        }

        /// <summary>Runs statements on the Server Main thread.</summary>
        public void Sync(string statements)
        {
            Console.Sync(statements);
        }

        /// <summary>Evaluates an expression on the Server Main thread and parses the result.</summary>
        public T SyncT<T>(string expression)
        {
            return Console.EvalT<T>("Sync(() => " + expression + ")");
        }

        /// <summary>Server.players entries that are not bots.</summary>
        public int HumanCount()
        {
            return SyncT<int>("((System.Collections.IEnumerable)Server.players).Cast<ServerPlayer>().Count(p => !p.isBot)");
        }

        public int BotCount()
        {
            return SyncT<int>("((System.Collections.IEnumerable)Server.players).Cast<ServerPlayer>().Count(p => p.isBot)");
        }

        private int UnreadyBotCount()
        {
            return SyncT<int>("((System.Collections.IEnumerable)Server.players).Cast<ServerPlayer>().Count(p => p.isBot && !p.ready)");
        }

        /// <summary>
        /// Connects a new scripted client and runs the whole join handshake,
        /// with Server.maxBots set to the number of connections the server
        /// will have afterwards (so UpdateBots is a no-op). Waits first until
        /// the server's player set matches the clients this fixture knows to
        /// be open, so a client disposed by a previous test has been removed.
        /// </summary>
        public ScriptedClient Join(string name)
        {
            lock (joinLock)
            {
                live.RemoveAll(c => c.IsClosed);
                DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
                while (HumanCount() != live.Count || UnreadyBotCount() != 0)
                {
                    if (DateTime.UtcNow > deadline)
                        throw new TimeoutException("server player set did not settle: humans=" + HumanCount() + " expected " + live.Count + ", unready bots=" + UnreadyBotCount());
                    Thread.Sleep(50);
                }
                int connections = live.Count + 1;
                Sync("Server.maxBots = " + connections + ";");
                ScriptedClient c = new ScriptedClient(Server);
                live.Add(c);
                try
                {
                    c.JoinGame(name);
                }
                catch (Exception)
                {
                    live.Remove(c);
                    c.Dispose();
                    throw;
                }
                return c;
            }
        }

        /// <summary>
        /// Announces that <paramref name="c"/> is about to disconnect (by
        /// itself or by a server kick): lowers Server.maxBots first so the
        /// removal does not add a bot.
        /// </summary>
        public void ExpectDisconnect(ScriptedClient c)
        {
            lock (joinLock)
            {
                live.Remove(c);
                Sync("Server.maxBots = " + live.Count + ";");
            }
        }

        /// <summary>
        /// Prepares a level change (setLevel: /newgame or a win) that will
        /// disconnect every client. Only <paramref name="last"/> may still be
        /// connected: with Server.maxBots = 0 setLevel's AddBots adds nothing
        /// and the single removal computes maxAllowedBots 0. With two humans
        /// dropped by one level change UpdateBots can pick a team without a
        /// bot and hang (docs/PORTING.md), whatever maxBots is.
        /// </summary>
        public void PrepareLevelChange(ScriptedClient last)
        {
            lock (joinLock)
            {
                live.RemoveAll(c => c.IsClosed);
                Assert.True(live.Count == 1 && live[0] == last, "exactly one client may be connected during a level change; open: " + live.Count);
                live.Clear();
                Sync("Server.maxBots = 0;");
            }
        }

        /// <summary>Closes the client's socket and waits until the server has removed the player.</summary>
        public void Leave(ScriptedClient c)
        {
            ExpectDisconnect(c);
            c.Close();
            Assert.NotNull(Server.WaitFor(System.Text.RegularExpressions.Regex.Escape(c.Name + " disconnected"), 10));
            WaitUntil(() => !PlayerExists(c.Name), TimeSpan.FromSeconds(10), "player " + c.Name + " removed from Server.players");
        }

        public bool PlayerExists(string name)
        {
            return SyncT<bool>("((System.Collections.IEnumerable)Server.players).Cast<ServerPlayer>().Any(p => p.name == \"" + name + "\")");
        }

        /// <summary>The ServerPlayer's CurrentClass.</summary>
        public PlayerClass CurrentClass(string name)
        {
            return SyncT<PlayerClass>(P(name) + ".CurrentClass");
        }

        /// <summary>The ServerPlayer's team (PlayerClass), which /spec does not change.</summary>
        public PlayerClass Team(string name)
        {
            return SyncT<PlayerClass>(P(name) + ".PlayerClass");
        }

        public float Health(string name)
        {
            return SyncT<float>(P(name) + ".Entity.Health.ToString(System.Globalization.CultureInfo.InvariantCulture)");
        }

        public int Score(string name)
        {
            return SyncT<int>(P(name) + ".Score");
        }

        public bool IsOp(string name)
        {
            return SyncT<bool>(P(name) + ".isOp");
        }

        public Vec2 Position(string name)
        {
            string s = Console.Eval("Sync(() => " + P(name) + ".Position.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + \",\" + " + P(name) + ".Position.Y.ToString(System.Globalization.CultureInfo.InvariantCulture))").Trim();
            string[] xy = s.Split(',');
            return new Vec2(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture));
        }

        /// <summary>Name of the player carrying the flag of <paramref name="flagOf"/>'s enemy (greenFlagCarrier for Green), or null.</summary>
        public string FlagCarrier(PlayerClass carrierTeam)
        {
            string field = carrierTeam == PlayerClass.Green ? "greenFlagCarrier" : "blueFlagCarrier";
            string r = Console.Eval("Sync(() => Server.gameMode." + field + " == null ? \"<null>\" : (object)Server.gameMode." + field + ".name)").Trim();
            return r == "<null>" ? null : r;
        }

        public int Captures(PlayerClass team)
        {
            return SyncT<int>("Server.gameMode." + (team == PlayerClass.Green ? "greenCaptures" : "blueCaptures"));
        }

        public int NumGreen() { return SyncT<int>("Server.gameMode.numGreen"); }
        public int NumBlue() { return SyncT<int>("Server.gameMode.numBlue"); }

        /// <summary>Position of the level's flag entity of that colour (null when it is carried/removed).</summary>
        public Vec2? FlagPosition(PlayerClass colour)
        {
            string f = colour == PlayerClass.Green ? "greenFlag" : "blueFlag";
            string s = Console.Eval("Sync(() => Server.level." + f + " == null ? \"-\" : (object)(Server.level." + f + ".Position.X.ToString(System.Globalization.CultureInfo.InvariantCulture) + \",\" + Server.level." + f + ".Position.Y.ToString(System.Globalization.CultureInfo.InvariantCulture)))").Trim();
            if (s == "-")
                return null;
            string[] xy = s.Split(',');
            return new Vec2(float.Parse(xy[0], CultureInfo.InvariantCulture), float.Parse(xy[1], CultureInfo.InvariantCulture));
        }

        /// <summary>Whether the level's entity list still contains the flag entity object of that colour.</summary>
        public bool FlagInLevel(PlayerClass colour)
        {
            string f = colour == PlayerClass.Green ? "greenFlag" : "blueFlag";
            return SyncT<bool>("((List<Entity>)Server.level.getEntities()).Contains((Entity)Server.level." + f + ")");
        }

        /// <summary>Entity ids of the flag entities of a type currently in the level.</summary>
        public List<int> FlagEntityIds(string typeName)
        {
            List<int> r = new List<int>();
            foreach (DebugConsole.EntityInfo e in Console.ServerEntities())
                if (e.Type == typeName)
                    r.Add(e.Id);
            return r;
        }

        /// <summary>The game-mode's drop timer for the flag of that colour (0 = not dropped).</summary>
        public int FlagDropTime(PlayerClass colour)
        {
            return SyncT<int>("(int)Get(Server.gameMode, \"" + (colour == PlayerClass.Green ? "greenFlagDropTime" : "blueFlagDropTime") + "\")");
        }

        /// <summary>Waits until neither flag is carried or lying dropped (a previous test's carrier may have just left).</summary>
        public void WaitForFlagsHome()
        {
            WaitUntil(() => FlagCarrier(PlayerClass.Green) == null && FlagCarrier(PlayerClass.Blue) == null
                            && FlagDropTime(PlayerClass.Green) == 0 && FlagDropTime(PlayerClass.Blue) == 0
                            && FlagPosition(PlayerClass.Green) == Bases.GreenFlag && FlagPosition(PlayerClass.Blue) == Bases.BlueFlag,
                     TimeSpan.FromSeconds(20), "both flags back at their bases");
        }

        /// <summary>The catalogue's fallback driver: the server's own flag-collision handler for that player and flag.</summary>
        public void CollideWithFlag(string name, PlayerClass flagColour)
        {
            string f = flagColour == PlayerClass.Green ? "greenFlag" : "blueFlag";
            Sync("Server.gameMode.OnFlagCollide(" + P(name) + ", Server.level." + f + ");");
        }

        /// <summary>Kills the player the way ResetPlayer does (Health = 0, no weapon).</summary>
        public void Kill(string name)
        {
            Sync("{ var p = " + P(name) + "; p.Entity.Health = 0; Server.gameMode.PlayerHealthChanged(p, null); }");
        }

        /// <summary>Kills <paramref name="victim"/> crediting <paramref name="attacker"/>'s current weapon.</summary>
        public void KillBy(string victim, string attacker)
        {
            Sync("{ var v = " + P(victim) + "; var a = " + P(attacker) + "; v.Entity.Health = 0; Server.gameMode.PlayerHealthChanged(v, a.Entity.Weapon); }");
        }

        /// <summary>Sets the player's server-side position (as the player's own packet 18 would).</summary>
        public void Teleport(string name, int x, int y)
        {
            Sync("{ var p = " + P(name) + "; p.Position = new Vec2(" + x + ", " + y + "); Set(p, \"lastPosition\", new Vec2(" + x + ", " + y + ")); }");
        }

        /// <summary>SHA-256 hex of Server.level.GetTerrainState() (comparable to TerrainSnapshot.Sha256Hex).</summary>
        public string TerrainSha256()
        {
            return Console.Eval("Sync(() => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData((byte[])Server.level.GetTerrainState())).ToLowerInvariant())").Trim();
        }

        /// <summary>Collision nibble of a terrain pixel on the live server.</summary>
        public int CollisionCode(int x, int y)
        {
            return SyncT<int>("(int)Server.level.terrain.GetCollisionData(" + x + ", " + y + ")");
        }

        public bool TerrainSolid(int x, int y)
        {
            return SyncT<bool>("Server.level.terrain.GetTerrain(" + x + ", " + y + ")");
        }

        /// <summary>Polls until the condition holds; fails the test on timeout.</summary>
        public static void WaitUntil(Func<bool> condition, TimeSpan timeout, string what)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                if (DateTime.UtcNow > deadline)
                    Assert.Fail("timeout waiting for: " + what);
                Thread.Sleep(50);
            }
        }

        public static string Unique(string prefix)
        {
            return prefix + "-" + Guid.NewGuid().ToString("N").Substring(0, 5);
        }
    }

    /// <summary>The shipped runtime's bases map geometry (map_info Test/Maps/bases.map, docs/ARCHITECTURE.md).</summary>
    public static class Bases
    {
        public const int Width = 3914, Height = 1024;
        // flag_Green 951 387 / flag_Blue 2933 387 -> Position (x1, height - y1 - 14)
        public static readonly Vec2 GreenFlag = new Vec2(951, 623);
        public static readonly Vec2 BlueFlag = new Vec2(2933, 623);
        // spawn_Green (459,365)-(593,414), spawn_Blue (3312,365)-(3446,414)
        public const int GreenSpawnX1 = 459, GreenSpawnX2 = 593, BlueSpawnX1 = 3312, BlueSpawnX2 = 3446;
        // Region.RandomPosition: y = height - (y1 + rnd(h - size.Y) + size.Y/2) + 16, so 626 <= y <= 675 for any size
        public const int SpawnY1 = 626, SpawnY2 = 675;

        public static bool InSpawn(PlayerClass cl, Vec2 p)
        {
            if (p.Y < SpawnY1 || p.Y > SpawnY2)
                return false;
            if (cl == PlayerClass.Green)
                return p.X >= GreenSpawnX1 && p.X <= GreenSpawnX2;
            if (cl == PlayerClass.Blue)
                return p.X >= BlueSpawnX1 && p.X <= BlueSpawnX2;
            return false;
        }
    }
}
