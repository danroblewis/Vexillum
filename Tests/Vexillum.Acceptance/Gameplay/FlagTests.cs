using System;
using System.Collections.Generic;
using System.Threading;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.servergameplay
{
    /// <summary>Three players: the taker has a teammate, the third player is the enemy. Roles follow the real team assignment.</summary>
    public sealed class Trio : IDisposable
    {
        public ScriptedClient Taker, Mate, Enemy;
        public PlayerClass TakerTeam, EnemyTeam;
        private readonly GameplayFixture fx;
        private readonly List<ScriptedClient> all = new List<ScriptedClient>();

        public Trio(GameplayFixture fx)
        {
            this.fx = fx;
            fx.WaitForFlagsHome();
            ScriptedClient a = fx.Join(GameplayFixture.Unique("alice"));
            ScriptedClient b = fx.Join(GameplayFixture.Unique("bob"));
            ScriptedClient c = fx.Join(GameplayFixture.Unique("carol"));
            all.Add(a); all.Add(b); all.Add(c);
            // A and B are on different teams; C joined one of them at random
            if (c.Me.Class == a.Me.Class) { Taker = a; Mate = c; Enemy = b; }
            else { Taker = b; Mate = c; Enemy = a; }
            TakerTeam = Taker.Me.Class;
            EnemyTeam = Enemy.Me.Class;
        }

        public Vec2 EnemyFlagHome { get { return EnemyTeam == PlayerClass.Green ? Bases.GreenFlag : Bases.BlueFlag; } }
        public Vec2 OwnFlagHome { get { return TakerTeam == PlayerClass.Green ? Bases.GreenFlag : Bases.BlueFlag; } }
        public string EnemyFlagType { get { return EnemyTeam == PlayerClass.Green ? "GreenFlagEntity" : "BlueFlagEntity"; } }
        /// <summary>GameModeCommand for "a player of TakerTeam carries the enemy flag".</summary>
        public byte CarrierCommand { get { return TakerTeam == PlayerClass.Green ? Protocol.GameMode.GreenFlagCarrier : Protocol.GameMode.BlueFlagCarrier; } }
        public byte ScoreCommand { get { return TakerTeam == PlayerClass.Green ? Protocol.GameMode.GreenScore : Protocol.GameMode.BlueScore; } }

        public void Dispose()
        {
            foreach (ScriptedClient c in all)
            {
                if (!c.IsClosed)
                {
                    try { fx.Leave(c); } catch (Exception) { c.Dispose(); }
                }
            }
        }
    }

    public static class Walk
    {
        /// <summary>
        /// Moves the client's server-side entity to (x, y) with packet 18 and
        /// pins it there with a few "position unchanged" packets (14), which
        /// zero the velocity SetVelocity derived from the jump; then waits until
        /// the server-side position is stable (the entity has landed).
        /// </summary>
        public static Vec2 Park(GameplayFixture fx, ScriptedClient c, int x, int y)
        {
            c.SendPositionAbsolute(x, y);
            for (int i = 0; i < 8; i++)
            {
                Thread.Sleep(50);
                c.SendPositionUnchanged();
            }
            Vec2 last = fx.Position(c.Name);
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(150);
                Vec2 now = fx.Position(c.Name);
                if (now == last)
                    return now;
                last = now;
            }
            Assert.Fail("entity of " + c.Name + " did not settle at " + x + "," + y + " (last " + last + ")");
            return last;
        }

        /// <summary>
        /// Moves the client's server-side entity into a flag standing at
        /// <paramref name="flag"/>: teleports 60 px to its left at the flag's
        /// height, then walks right 2 px per 50 ms (packet 16) until
        /// <paramref name="arrived"/> holds. Entity-entity collisions are only
        /// detected by the server's physics step while the entity moves, so the
        /// velocity derived from the deltas is what makes it touch the flag.
        /// </summary>
        public static bool Into(ScriptedClient c, Vec2 flag, Func<bool> arrived)
        {
            int x = (int)flag.X - 60;
            int y = (int)flag.Y + 5;
            c.SendPositionAbsolute(x, y);
            for (int i = 0; i < 80; i++)
            {
                Thread.Sleep(50);
                if (arrived())
                    return true;
                x += 2;
                c.SendPositionDelta(2, 0);
            }
            DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
            while (DateTime.UtcNow < deadline)
            {
                if (arrived())
                    return true;
                Thread.Sleep(50);
            }
            return arrived();
        }
    }

    public class FlagsFixture : GameplayFixture
    {
        protected override void Settings(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("respawntime", "300");
            runtime.SetServerSetting("maxcaptures", "10");
        }
    }

    public class FlagTests : IClassFixture<FlagsFixture>
    {
        private readonly FlagsFixture fx;

        public FlagTests(FlagsFixture fx)
        {
            this.fx = fx;
        }

        // SRV-05
        [Fact]
        public void Touching_the_enemy_flag_makes_the_player_its_carrier()
        {
            using (Trio t = new Trio(fx))
            {
                int flagId = Assert.Single(fx.FlagEntityIds(t.EnemyFlagType));
                int mT = t.Taker.PacketCount, mE = t.Enemy.PacketCount, mM = t.Mate.PacketCount;
                bool took = Walk.Into(t.Taker, t.EnemyFlagHome,
                    () => t.Taker.Packets<GameModeShortPacket>(mT).Exists(p => p.Command == t.CarrierCommand && p.Value == t.Taker.MyEntityId));
                Assert.True(took, "walking into the flag at " + t.EnemyFlagHome + " never produced the carrier packet; taker at " + fx.Position(t.Taker.Name));

                // everyone learns the carrier and sees the flag entity removed
                foreach (ScriptedClient c in new ScriptedClient[] { t.Taker, t.Mate, t.Enemy })
                {
                    GameModeShortPacket carrier = c.WaitFor<GameModeShortPacket>(p => p.Command == t.CarrierCommand && p.Value == t.Taker.MyEntityId, 5);
                    Assert.NotNull(carrier);
                    c.WaitFor<EntityRemovePacket>(p => p.EntityId == flagId, 5);
                }
                // messages: taker gets the instructions, the enemy "your flag was taken", the teammate "X has taken the flag"
                MessagePacket noob = t.Taker.WaitFor<MessagePacket>(p => p.MessageId == Messages.NOOB_INSTRUCTIONS, TimeSpan.FromSeconds(5), mT);
                Assert.Empty(noob.Args);
                Assert.Equal(2000, noob.DurationMs);
                t.Enemy.WaitFor<MessagePacket>(p => p.MessageId == Messages.OURFLAG_TAKEN, TimeSpan.FromSeconds(5), mE);
                MessagePacket taken = t.Mate.WaitFor<MessagePacket>(p => p.MessageId == Messages.THEIRFLAG_TAKEN, TimeSpan.FromSeconds(5), mM);
                Assert.Equal(new string[] { (t.TakerTeam == PlayerClass.Green ? TextUtil.COLOR_GREEN : TextUtil.COLOR_BLUE) + t.Taker.Name }, taken.Args);
                Assert.True(t.Taker.NoneWithin<MessagePacket>(p => p.MessageId == Messages.OURFLAG_TAKEN || p.MessageId == Messages.THEIRFLAG_TAKEN, TimeSpan.FromSeconds(0.5), mT), "taker gets only the instructions");

                Assert.Equal(t.Taker.Name, fx.FlagCarrier(t.TakerTeam));
                Assert.False(fx.FlagInLevel(t.EnemyTeam), "the enemy flag entity was removed from the level");
                Assert.Empty(fx.FlagEntityIds(t.EnemyFlagType));
            }
        }

        // SRV-26 (known original bug, preserved): TakeFlag calls RemoveFlag and then level.TakeFlag -> RemoveFlag
        // again, so the flag entity's removal (41) is sent twice to every client
        [Fact]
        public void Taking_a_flag_sends_its_entity_removal_twice_to_every_client()
        {
            using (Trio t = new Trio(fx))
            {
                int flagId = Assert.Single(fx.FlagEntityIds(t.EnemyFlagType));
                ScriptedClient[] all = new ScriptedClient[] { t.Taker, t.Mate, t.Enemy };
                int[] marks = new int[] { t.Taker.PacketCount, t.Mate.PacketCount, t.Enemy.PacketCount };
                fx.CollideWithFlag(t.Taker.Name, t.EnemyTeam);
                Assert.Equal(t.Taker.Name, fx.FlagCarrier(t.TakerTeam));

                for (int i = 0; i < all.Length; i++)
                {
                    ScriptedClient c = all[i];
                    int mark = marks[i];
                    GameModeShortPacket carrier = c.WaitFor<GameModeShortPacket>(p => p.Command == t.CarrierCommand && p.Value == t.Taker.MyEntityId, TimeSpan.FromSeconds(5), mark);
                    Assert.NotNull(carrier);
                    GameplayFixture.WaitUntil(() => c.Packets<EntityRemovePacket>(mark).FindAll(p => p.EntityId == flagId).Count >= 2,
                        TimeSpan.FromSeconds(5), c.Name + " receives the second removal of flag #" + flagId);
                    // and no third one follows
                    Thread.Sleep(500);
                    List<EntityRemovePacket> removals = c.Packets<EntityRemovePacket>(mark).FindAll(p => p.EntityId == flagId);
                    Assert.Equal(2, removals.Count);
                    Assert.True(removals[0].Sequence < carrier.Sequence, "the first removal precedes the carrier command");
                    Assert.Equal(removals[0].Frame, removals[1].Frame);
                    Assert.Null(c.ReaderError);
                }
                // the double removal is harmless: the level still runs and the flag is simply gone
                Assert.True(fx.Console.ServerReady(), "server still ready");
                Assert.Empty(fx.FlagEntityIds(t.EnemyFlagType));
                Assert.False(fx.FlagInLevel(t.EnemyTeam));
                Assert.Equal(3, fx.HumanCount());
            }
        }

        // SRV-06 (scoring part; the win is in FlagWinTests)
        [Fact]
        public void Bringing_the_enemy_flag_home_scores_a_capture_and_three_points()
        {
            using (Trio t = new Trio(fx))
            {
                fx.CollideWithFlag(t.Taker.Name, t.EnemyTeam);
                Assert.Equal(t.Taker.Name, fx.FlagCarrier(t.TakerTeam));
                int capturesBefore = fx.Captures(t.TakerTeam);
                int scoreBefore = fx.Score(t.Taker.Name);
                int mT = t.Taker.PacketCount, mE = t.Enemy.PacketCount, mM = t.Mate.PacketCount;

                bool captured = Walk.Into(t.Taker, t.OwnFlagHome,
                    () => t.Taker.Packets<GameModeBytePacket>(mT).Exists(p => p.Command == t.ScoreCommand));
                Assert.True(captured, "walking onto the own flag never scored; taker at " + fx.Position(t.Taker.Name));

                GameModeShortPacket cleared = t.Taker.WaitFor<GameModeShortPacket>(p => p.Command == t.CarrierCommand && p.Value == -1, TimeSpan.FromSeconds(5), mT);
                EntityCreatePacket flagBack = t.Taker.WaitFor<EntityCreatePacket>(p => p.TypeName == t.EnemyFlagType, TimeSpan.FromSeconds(5), mT);
                Assert.Equal(t.EnemyFlagHome, flagBack.Position);
                Assert.False(flagBack.IsPlayer);
                GameModeBytePacket score = t.Taker.WaitFor<GameModeBytePacket>(p => p.Command == t.ScoreCommand, TimeSpan.FromSeconds(5), mT);
                Assert.Equal(capturesBefore + 1, score.Value);
                ScorePacket points = t.Taker.WaitFor<ScorePacket>(p => p.EntityId == t.Taker.MyEntityId, TimeSpan.FromSeconds(5), mT);
                Assert.Equal(scoreBefore + 3, points.Score);
                // PlaceFlag's 40 is flushed at once; 121 and 120 sit in the write buffer until SendScore's 131 flushes them.
                Assert.True(flagBack.Sequence < cleared.Sequence && cleared.Sequence < score.Sequence && score.Sequence < points.Sequence,
                    "order: flag re-created, carrier cleared, team score, player score; got " + flagBack.Sequence + " " + cleared.Sequence + " " + score.Sequence + " " + points.Sequence);

                MessagePacket own = t.Taker.WaitFor<MessagePacket>(p => p.MessageId == Messages.OURFLAG_CAPTURED_1, TimeSpan.FromSeconds(5), mT);
                Assert.Empty(own.Args);
                t.Enemy.WaitFor<MessagePacket>(p => p.MessageId == Messages.OURFLAG_CAPTURED, TimeSpan.FromSeconds(5), mE);
                MessagePacket mate = t.Mate.WaitFor<MessagePacket>(p => p.MessageId == Messages.THEIRFLAG_CAPTURED, TimeSpan.FromSeconds(5), mM);
                Assert.Single(mate.Args);
                Assert.EndsWith(t.Taker.Name, mate.Args[0]);
                // no win at maxcaptures 10
                Assert.True(t.Taker.NoneWithin<MessagePacket>(p => p.MessageId == Messages.GREEN_WIN || p.MessageId == Messages.BLUE_WIN, TimeSpan.FromSeconds(1), mT));

                Assert.Null(fx.FlagCarrier(t.TakerTeam));
                Assert.Equal(capturesBefore + 1, fx.Captures(t.TakerTeam));
                Assert.Equal(scoreBefore + 3, fx.Score(t.Taker.Name));
                Assert.Equal(t.EnemyFlagHome, fx.FlagPosition(t.EnemyTeam));
            }
        }

        // SRV-07
        [Fact]
        public void Carrier_death_drops_the_flag_where_they_died_and_it_returns_after_ten_seconds()
        {
            using (Trio t = new Trio(fx))
            {
                fx.CollideWithFlag(t.Taker.Name, t.EnemyTeam);
                Assert.Equal(t.Taker.Name, fx.FlagCarrier(t.TakerTeam));
                // die away from both spawn regions, or a teammate landing there would pick the flag up
                Vec2 deathPos = Walk.Park(fx, t.Taker, (int)t.EnemyFlagHome.X - 60, (int)t.EnemyFlagHome.Y + 5);
                int mT = t.Taker.PacketCount, mE = t.Enemy.PacketCount, mM = t.Mate.PacketCount;
                fx.Kill(t.Taker.Name);

                GameModeShortPacket cleared = t.Enemy.WaitFor<GameModeShortPacket>(p => p.Command == t.CarrierCommand && p.Value == -1, TimeSpan.FromSeconds(5), mE);
                EntityCreatePacket dropped = t.Enemy.WaitFor<EntityCreatePacket>(p => p.TypeName == t.EnemyFlagType, TimeSpan.FromSeconds(5), mE);
                Assert.Equal(new Vec2((int)deathPos.X, (int)deathPos.Y), dropped.Position);
                Assert.False(dropped.IsPlayer);
                Assert.NotEqual(t.EnemyFlagHome, dropped.Position);
                t.Enemy.WaitFor<MessagePacket>(p => p.MessageId == Messages.OURFLAG_DROPPED, TimeSpan.FromSeconds(5), mE);
                MessagePacket mate = t.Mate.WaitFor<MessagePacket>(p => p.MessageId == Messages.THEIRFLAG_DROPPED, TimeSpan.FromSeconds(5), mM);
                Assert.EndsWith(t.Taker.Name, Assert.Single(mate.Args));
                Assert.True(t.Taker.NoneWithin<MessagePacket>(p => p.MessageId == Messages.OURFLAG_DROPPED || p.MessageId == Messages.THEIRFLAG_DROPPED, TimeSpan.FromSeconds(0.5), mT), "the dead carrier gets no drop message");
                Assert.NotEqual(0, fx.FlagDropTime(t.EnemyTeam));
                Assert.Null(fx.FlagCarrier(t.TakerTeam));

                // return to base after 10 s of level time
                EntityRemovePacket gone = t.Enemy.WaitFor<EntityRemovePacket>(p => p.EntityId == dropped.EntityId, TimeSpan.FromSeconds(15), mE);
                EntityCreatePacket home = t.Enemy.WaitFor<EntityCreatePacket>(p => p.TypeName == t.EnemyFlagType && p.Sequence > dropped.Sequence, TimeSpan.FromSeconds(15), mE);
                Assert.Equal(t.EnemyFlagHome, home.Position);
                double dt = (gone.ReceivedAt - dropped.ReceivedAt).TotalSeconds;
                Assert.InRange(dt, 9.8, 11.0);
                t.Enemy.WaitFor<MessagePacket>(p => p.MessageId == Messages.OURFLAG_RETURNED, TimeSpan.FromSeconds(5), mE);
                t.Mate.WaitFor<MessagePacket>(p => p.MessageId == Messages.THEIRFLAG_RETURNED, TimeSpan.FromSeconds(5), mM);
                t.Taker.WaitFor<MessagePacket>(p => p.MessageId == Messages.THEIRFLAG_RETURNED, TimeSpan.FromSeconds(5), mT);
                Assert.Equal(0, fx.FlagDropTime(t.EnemyTeam));
                Assert.Equal(t.EnemyFlagHome, fx.FlagPosition(t.EnemyTeam));
            }
        }

        // SRV-08
        [Fact]
        public void Carrier_disconnect_drops_the_flag()
        {
            using (Trio t = new Trio(fx))
            {
                fx.CollideWithFlag(t.Taker.Name, t.EnemyTeam);
                Assert.Equal(t.Taker.Name, fx.FlagCarrier(t.TakerTeam));
                Vec2 lastPos = Walk.Park(fx, t.Taker, (int)t.EnemyFlagHome.X - 60, (int)t.EnemyFlagHome.Y + 5);
                short takerId = t.Taker.MyEntityId;
                int mE = t.Enemy.PacketCount, mM = t.Mate.PacketCount;
                fx.Leave(t.Taker);

                t.Enemy.WaitFor<GameModeShortPacket>(p => p.Command == t.CarrierCommand && p.Value == -1, TimeSpan.FromSeconds(5), mE);
                EntityCreatePacket dropped = t.Enemy.WaitFor<EntityCreatePacket>(p => p.TypeName == t.EnemyFlagType, TimeSpan.FromSeconds(5), mE);
                Assert.Equal(new Vec2((int)lastPos.X, (int)lastPos.Y), dropped.Position);
                t.Enemy.WaitFor<EntityRemovePacket>(p => p.EntityId == takerId, TimeSpan.FromSeconds(5), mE);
                t.Enemy.WaitFor<MessagePacket>(p => p.MessageId == Messages.OURFLAG_DROPPED, TimeSpan.FromSeconds(5), mE);
                t.Mate.WaitFor<MessagePacket>(p => p.MessageId == Messages.THEIRFLAG_DROPPED, TimeSpan.FromSeconds(5), mM);
                ChatPacket left = t.Enemy.WaitFor<ChatPacket>(p => p.Text.EndsWith(" left the game"), TimeSpan.FromSeconds(5), mE);
                Assert.Equal(TextUtil.COLOR_ORANGE + t.Taker.Name + " left the game", left.Text);
                Assert.Null(fx.FlagCarrier(t.TakerTeam));
                Assert.NotEqual(0, fx.FlagDropTime(t.EnemyTeam));

                EntityRemovePacket gone = t.Enemy.WaitFor<EntityRemovePacket>(p => p.EntityId == dropped.EntityId, TimeSpan.FromSeconds(15), mE);
                EntityCreatePacket home = t.Enemy.WaitFor<EntityCreatePacket>(p => p.TypeName == t.EnemyFlagType && p.Sequence > dropped.Sequence, TimeSpan.FromSeconds(15), mE);
                Assert.Equal(t.EnemyFlagHome, home.Position);
                Assert.InRange((gone.ReceivedAt - dropped.ReceivedAt).TotalSeconds, 9.8, 11.0);
            }
        }

        // SRV-09: the dropped-flag pickup rule differs between the teams (original quirk, preserved)
        [Fact]
        public void Dropped_flag_pickup_is_guarded_for_green_but_not_for_blue()
        {
            fx.WaitForFlagsHome();
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            using (ScriptedClient b = fx.Join(GameplayFixture.Unique("bob")))
            using (ScriptedClient c = fx.Join(GameplayFixture.Unique("carol")))
            using (ScriptedClient d = fx.Join(GameplayFixture.Unique("dave")))
            {
                List<ScriptedClient> greens = new List<ScriptedClient>(), blues = new List<ScriptedClient>();
                foreach (ScriptedClient x in new ScriptedClient[] { a, b, c, d })
                    (x.Me.Class == PlayerClass.Green ? greens : blues).Add(x);
                Assert.Equal(2, greens.Count);
                Assert.Equal(2, blues.Count);

                // (a) green: the dropped blue flag cannot be picked up while its drop timer runs
                fx.CollideWithFlag(greens[0].Name, PlayerClass.Blue);
                Assert.Equal(greens[0].Name, fx.FlagCarrier(PlayerClass.Green));
                Walk.Park(fx, greens[0], (int)Bases.BlueFlag.X - 60, (int)Bases.BlueFlag.Y + 5);
                fx.Kill(greens[0].Name);
                GameplayFixture.WaitUntil(() => fx.FlagDropTime(PlayerClass.Blue) != 0, TimeSpan.FromSeconds(5), "blue flag dropped");
                int mark = greens[1].PacketCount;
                fx.CollideWithFlag(greens[1].Name, PlayerClass.Blue);
                Assert.Null(fx.FlagCarrier(PlayerClass.Green));
                Assert.True(greens[1].NoneWithin<GameModeShortPacket>(p => p.Command == Protocol.GameMode.GreenFlagCarrier && p.Value == greens[1].MyEntityId, TimeSpan.FromSeconds(1), mark), "no carrier packet for the green pickup");
                Assert.NotEqual(0, fx.FlagDropTime(PlayerClass.Blue));

                // (b) blue: no such guard for the dropped green flag
                fx.CollideWithFlag(blues[0].Name, PlayerClass.Green);
                Assert.Equal(blues[0].Name, fx.FlagCarrier(PlayerClass.Blue));
                Walk.Park(fx, blues[0], (int)Bases.GreenFlag.X - 60, (int)Bases.GreenFlag.Y + 5);
                fx.Kill(blues[0].Name);
                GameplayFixture.WaitUntil(() => fx.FlagDropTime(PlayerClass.Green) != 0, TimeSpan.FromSeconds(5), "green flag dropped");
                int mark2 = blues[1].PacketCount;
                fx.CollideWithFlag(blues[1].Name, PlayerClass.Green);
                GameModeShortPacket carrier = blues[1].WaitFor<GameModeShortPacket>(p => p.Command == Protocol.GameMode.BlueFlagCarrier, TimeSpan.FromSeconds(5), mark2);
                Assert.Equal(blues[1].MyEntityId, carrier.Value);
                Assert.Equal(blues[1].Name, fx.FlagCarrier(PlayerClass.Blue));
                Assert.Equal(0, fx.FlagDropTime(PlayerClass.Green));

                // let the blue flag return so the next test starts from a clean field
                GameplayFixture.WaitUntil(() => fx.FlagDropTime(PlayerClass.Blue) == 0, TimeSpan.FromSeconds(15), "blue flag returned");
                foreach (ScriptedClient x in new ScriptedClient[] { a, b, c, d })
                    fx.Leave(x);
            }
        }
    }

    public class WinFixture : GameplayFixture
    {
        protected override void Settings(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("maxcaptures", "1");
            runtime.SetServerSetting("respawntime", "300");
        }
    }

    public class FlagWinTests : IClassFixture<WinFixture>
    {
        private readonly WinFixture fx;

        public FlagWinTests(WinFixture fx)
        {
            this.fx = fx;
        }

        // SRV-06
        [Fact]
        public void Reaching_maxcaptures_wins_the_game_and_rotates_the_level()
        {
            Assert.Equal(1, fx.Console.EvalT<int>("Server.gameMode.maxCaptures"));
            fx.WaitForFlagsHome();
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            using (ScriptedClient b = fx.Join(GameplayFixture.Unique("bob")))
            {
                ScriptedClient taker = a.Me.Class == PlayerClass.Green ? a : b;
                ScriptedClient enemy = taker == a ? b : a;
                PlayerClass takerTeam = taker.Me.Class, enemyTeam = enemy.Me.Class;
                byte carrierCmd = takerTeam == PlayerClass.Green ? Protocol.GameMode.GreenFlagCarrier : Protocol.GameMode.BlueFlagCarrier;
                byte scoreCmd = takerTeam == PlayerClass.Green ? Protocol.GameMode.GreenScore : Protocol.GameMode.BlueScore;
                byte winCmd = takerTeam == PlayerClass.Green ? Protocol.GameMode.GreenWin : Protocol.GameMode.BlueWin;
                int winMsg = takerTeam == PlayerClass.Green ? Messages.GREEN_WIN : Messages.BLUE_WIN;

                fx.CollideWithFlag(taker.Name, enemyTeam);
                Assert.Equal(taker.Name, fx.FlagCarrier(takerTeam));
                int mT = taker.PacketCount, mE = enemy.PacketCount;
                fx.CollideWithFlag(taker.Name, takerTeam);

                GameModeShortPacket cleared = taker.WaitFor<GameModeShortPacket>(p => p.Command == carrierCmd && p.Value == -1, TimeSpan.FromSeconds(5), mT);
                GameModeBytePacket score = taker.WaitFor<GameModeBytePacket>(p => p.Command == scoreCmd, TimeSpan.FromSeconds(5), mT);
                Assert.Equal(1, score.Value);
                ScorePacket points = taker.WaitFor<ScorePacket>(p => p.EntityId == taker.MyEntityId, TimeSpan.FromSeconds(5), mT);
                Assert.Equal(3, points.Score);
                MessagePacket winA = taker.WaitFor<MessagePacket>(p => p.MessageId == winMsg, TimeSpan.FromSeconds(5), mT);
                Assert.Empty(winA.Args);
                Assert.Equal(5000, winA.DurationMs);
                MessagePacket winB = enemy.WaitFor<MessagePacket>(p => p.MessageId == winMsg, TimeSpan.FromSeconds(5), mE);
                Assert.Equal(5000, winB.DurationMs);
                Assert.True(fx.Console.EvalT<bool>("!(bool)Get(Server.gameMode, \"inProgress\")"), "game over");
                // the level change 13 s from now drops every client; only one human may be connected then
                // (UpdateBots can hang on a two-human drop, see GameplayFixture.PrepareLevelChange)
                fx.Leave(enemy);
                fx.PrepareLevelChange(taker);

                // ~5 s later the win command, ~8 s after that the level change (253) and the socket is closed
                GameModeShortPacket win = taker.WaitFor<GameModeShortPacket>(p => p.Command == winCmd, TimeSpan.FromSeconds(15), mT);
                Assert.Equal(-1, win.Value);
                Assert.InRange((win.ReceivedAt - winA.ReceivedAt).TotalSeconds, 4.5, 7.0);
                LevelChangingPacket changing = taker.WaitFor<LevelChangingPacket>(null, TimeSpan.FromSeconds(20), mT);
                Assert.InRange((changing.ReceivedAt - win.ReceivedAt).TotalSeconds, 7.5, 10.0);
                Assert.True(taker.WaitForClose(TimeSpan.FromSeconds(10)), "server closes the socket after 253");

                GameplayFixture.WaitUntil(() => fx.Console.ServerReady(), TimeSpan.FromSeconds(30), "new level ready");
                Assert.Equal(0, fx.BotCount());
                Assert.Contains(fx.Console.ServerMap(), fx.Runtime.MapNames());
                Assert.Equal(0, fx.Console.EvalT<int>("Server.gameMode.greenCaptures"));
                Assert.Equal(0, fx.Console.EvalT<int>("Server.gameMode.blueCaptures"));
                Assert.True(fx.Console.EvalT<bool>("(bool)Get(Server.gameMode, \"inProgress\")"), "new game in progress");
            }
        }
    }
}
