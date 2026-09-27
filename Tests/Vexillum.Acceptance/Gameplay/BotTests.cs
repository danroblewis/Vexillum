using System;
using System.Collections.Generic;
using System.Linq;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.servergameplay
{
    /// <summary>
    /// The shipped default (maxbots 6) with at most two humans, the
    /// configuration docs/TESTING.md documents as safe for Server.UpdateBots.
    /// Clients join directly (no maxBots management).
    /// </summary>
    public class BotsFixture : ServerFixture
    {
        public BotsFixture()
        {
            GameplayFixture.WaitForServerObject(Server);
        }

        protected override void Configure(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("maps", "bases");
        }
    }

    public class BotTests : IClassFixture<BotsFixture>
    {
        // Server.cs: the names the server draws bots from (shuffled at startup)
        private static readonly HashSet<string> BotNames = new HashSet<string>
        {
            "Bot #2", "Brains", "Bot #1", "Unknown", "Vexillum Player", "GLaDOS", "Gabe", "Keybored", "Bolo Santosi",
            "Babies in Africa", "aimbot.exe", "Cry Some Moar", "Rick Astley", "Nyan Cat", "Borg", "Notch",
            "Artificial Stupidity", "xXxCODNoScope420SniperxXx", "Human", "There's a spy around here!"
        };

        private readonly BotsFixture fx;

        public BotTests(BotsFixture fx)
        {
            this.fx = fx;
        }

        private sealed class BotInfo
        {
            public string Name;
            public bool Ready;
            public int EntityId;
        }

        // A bot in the middle of AIPlayer.Login has no entity yet, so the harness's ServerPlayers()
        // probe (which calls GetID()) would throw; query the players with a guarded script instead.
        private const string botsScript =
            "Sync(() => string.Join(\"\\n\", ((System.Collections.IEnumerable)Server.players).Cast<ServerPlayer>()" +
            ".Where(p => p.isBot).Select(p => p.name + \"|\" + p.ready + \"|\" + (p.Entity != null ? p.Entity.ID : -1))))";

        private List<BotInfo> Bots()
        {
            List<BotInfo> r = new List<BotInfo>();
            foreach (string line in fx.Server.Console.Eval(botsScript).Split('\n'))
            {
                if (line.Trim().Length == 0)
                    continue;
                string[] f = line.Split('|');
                r.Add(new BotInfo { Name = f[0], Ready = bool.Parse(f[1]), EntityId = int.Parse(f[2]) });
            }
            return r;
        }

        private bool HumanPresent(string name)
        {
            return fx.Server.Console.EvalT<bool>("Sync(() => ((System.Collections.IEnumerable)Server.players).Cast<ServerPlayer>().Any(p => p.name == \"" + name + "\"))");
        }

        private void WaitForBots(int n, string what)
        {
            GameplayFixture.WaitUntil(() => Bots().Count == n && Bots().All(b => b.Ready && b.EntityId > 0), TimeSpan.FromSeconds(15), what + " (" + Bots().Count + " bots)");
        }

        private void LeaveAndWait(ScriptedClient c)
        {
            c.Close();
            Assert.NotNull(fx.Server.WaitFor(System.Text.RegularExpressions.Regex.Escape(c.Name + " disconnected"), 10));
            GameplayFixture.WaitUntil(() => !HumanPresent(c.Name), TimeSpan.FromSeconds(10), c.Name + " removed");
        }

        // SRV-02
        [Fact]
        public void Bots_fill_up_to_maxbots_minus_humans_and_leave_as_humans_join()
        {
            WaitForBots(0, "no bots before anyone joins");
            // UpdateBots counts every accepted connection (players.Count) as a human, so the second
            // client must not even connect before the first fill is observed
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            {
                a.JoinGame(GameplayFixture.Unique("alice"));
                WaitForBots(5, "5 bots after the first human");
                List<BotInfo> bots = Bots();
                Assert.All(bots, bot => Assert.Contains(bot.Name, BotNames));
                Assert.Equal(5, bots.Select(x => x.Name).Distinct().Count());
                int green = fx.Server.Console.EvalT<int>("Sync(() => Server.gameMode.numGreen)");
                int blue = fx.Server.Console.EvalT<int>("Sync(() => Server.gameMode.numBlue)");
                Assert.Equal(6, green + blue);
                Assert.True(Math.Abs(green - blue) <= 1, "teams balanced: " + green + "/" + blue);
                // bots are real players: A got a PlayerSpawn (5) for each of them
                foreach (BotInfo bot in bots)
                {
                    PlayerSpawnPacket spawn = a.WaitFor<PlayerSpawnPacket>(p => p.Name == bot.Name, TimeSpan.FromSeconds(5));
                    Assert.Equal(bot.EntityId, spawn.EntityId);
                    Assert.Contains(spawn.Class, new PlayerClass[] { PlayerClass.Green, PlayerClass.Blue });
                }

                // a second human displaces one bot, taken from the team with more players (blue only when strictly more)
                PlayerClass expected = blue > green ? PlayerClass.Blue : PlayerClass.Green;
                HashSet<string> before = new HashSet<string>(bots.Select(x => x.Name));
                int mA = a.PacketCount;
                ScriptedClient b = new ScriptedClient(fx.Server);
                b.JoinGame(GameplayFixture.Unique("bob"));
                WaitForBots(4, "4 bots after the second human");
                HashSet<string> after = new HashSet<string>(Bots().Select(x => x.Name));
                Assert.True(before.IsSupersetOf(after), "no new bot names");
                string removed = Assert.Single(before.Except(after));
                PlayerSpawnPacket removedSpawn = a.Packets<PlayerSpawnPacket>().Find(p => p.Name == removed);
                Assert.Contains(removedSpawn.Class, new PlayerClass[] { expected, PlayerClass.Spectator });
                a.WaitFor<EntityRemovePacket>(p => p.EntityId == removedSpawn.EntityId, TimeSpan.FromSeconds(5), mA);
                Assert.Null(a.PlayerNamed(removed));

                // humans leaving: bots fill back up to maxbots - humans, and vanish with the last human
                LeaveAndWait(a);
                WaitForBots(5, "5 bots with one human left");
                LeaveAndWait(b);
                b.Dispose();
                WaitForBots(0, "no bots without humans");
                Assert.Equal(0, fx.Server.Console.EvalT<int>("Sync(() => ((System.Collections.IEnumerable)Server.players).Cast<Player>().Count())"));
            }
        }

        // SRV-10
        [Fact]
        public void Bots_never_take_or_capture_flags()
        {
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            {
                a.JoinGame(GameplayFixture.Unique("watcher"));
                WaitForBots(5, "bots joined");
                a.SendChat("/spec");
                a.WaitFor<ClassChangePacket>(p => p.EntityId == -1 && p.Class == PlayerClass.Spectator, 5);
                DateTime end = DateTime.UtcNow + TimeSpan.FromSeconds(60);
                int checks = 0;
                while (DateTime.UtcNow < end)
                {
                    string state = fx.Server.Console.Eval("Sync(() => (Server.gameMode.greenFlagCarrier == null) + \",\" + (Server.gameMode.blueFlagCarrier == null) + \",\" + Server.gameMode.greenCaptures + \",\" + Server.gameMode.blueCaptures)").Trim();
                    Assert.Equal("True,True,0,0", state);
                    checks++;
                    System.Threading.Thread.Sleep(2000);
                }
                Assert.True(checks >= 25);
                Assert.DoesNotContain(a.Packets<GameModeShortPacket>(), p => (p.Command == Protocol.GameMode.GreenFlagCarrier || p.Command == Protocol.GameMode.BlueFlagCarrier) && p.Value >= 0);
                Assert.DoesNotContain(a.Packets<GameModeBytePacket>(), p => (p.Command == Protocol.GameMode.GreenScore || p.Command == Protocol.GameMode.BlueScore) && p.Value > 0);
                // the flags are still at their bases
                Assert.Equal(Bases.GreenFlag.ToString(), fx.Server.Console.Eval("Sync(() => Server.level.greenFlag.Position.ToString())").Trim());
                Assert.Equal(Bases.BlueFlag.ToString(), fx.Server.Console.Eval("Sync(() => Server.level.blueFlag.Position.ToString())").Trim());
                LeaveAndWait(a);
                WaitForBots(0, "no bots without humans");
            }
        }

        // SRV-24
        [Fact]
        public void Bots_move_switch_weapons_and_shoot()
        {
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            {
                a.JoinGame(GameplayFixture.Unique("watcher"));
                WaitForBots(5, "bots joined");
                a.SendChat("/spec");
                a.WaitFor<ClassChangePacket>(p => p.EntityId == -1 && p.Class == PlayerClass.Spectator, 5);
                HashSet<short> botIds = new HashSet<short>(Bots().Select(b => (short)b.EntityId));
                Assert.Equal(5, botIds.Count);

                // movement: some bot's position changes over time (AIController.MoveTowards sets xVelocity)
                Assert.True(a.WaitUntil(delegate()
                {
                    Dictionary<short, HashSet<string>> seen = new Dictionary<short, HashSet<string>>();
                    foreach (PositionsPacket pp in a.Packets<PositionsPacket>())
                        foreach (EntityMotion m in pp.Entries)
                            if (botIds.Contains(m.Id))
                            {
                                if (!seen.ContainsKey(m.Id)) seen[m.Id] = new HashSet<string>();
                                seen[m.Id].Add(m.Vector.ToString());
                            }
                    return seen.Values.Any(s => s.Count >= 5);
                }, TimeSpan.FromSeconds(30)), "a bot moved through at least 5 distinct positions");

                // weapon switches carry a valid inventory index
                WeaponSelectPacket sel = a.WaitFor<WeaponSelectPacket>(p => botIds.Contains(p.EntityId), TimeSpan.FromSeconds(40));
                Assert.InRange(sel.Index, 0, 2);
                Assert.All(a.Packets<WeaponSelectPacket>().Where(p => botIds.Contains(p.EntityId)), p => Assert.InRange(p.Index, 0, 2));

                // firing at an enemy in range: a fire packet and either a rocket owned by the bot or its hitscan
                WeaponFirePacket fire = a.WaitFor<WeaponFirePacket>(p => botIds.Contains(p.EntityId), TimeSpan.FromSeconds(40));
                Assert.InRange(fire.Index, 0, 2);
                Assert.True(a.WaitUntil(() => a.Packets<ProjectileCreatePacket>().Any(p => botIds.Contains(p.OwnerId))
                                           || a.Packets<HitscanPacket>().Any(p => botIds.Contains(p.EntityId)), TimeSpan.FromSeconds(20)),
                    "a bot's shot became a rocket (42) or a hitscan (20)");
                LeaveAndWait(a);
                WaitForBots(0, "no bots without humans");
            }
        }
    }
}
