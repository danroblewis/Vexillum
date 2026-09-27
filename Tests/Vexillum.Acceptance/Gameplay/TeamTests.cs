using System;
using System.Collections.Generic;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.servergameplay
{
    /// <summary>Teams, spawn regions and the /green /blue /spec commands; short respawn so deaths are quick.</summary>
    public class TeamsFixture : GameplayFixture
    {
        protected override void Settings(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("respawntime", "300");
        }
    }

    public class TeamTests : IClassFixture<TeamsFixture>
    {
        private readonly TeamsFixture fx;

        public TeamTests(TeamsFixture fx)
        {
            this.fx = fx;
        }

        // SRV-03
        [Fact]
        public void Joining_players_are_assigned_to_the_smaller_team()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            using (ScriptedClient b = fx.Join(GameplayFixture.Unique("bob")))
            {
                Assert.Contains(a.Me.Class, new PlayerClass[] { PlayerClass.Green, PlayerClass.Blue });
                Assert.Contains(b.Me.Class, new PlayerClass[] { PlayerClass.Green, PlayerClass.Blue });
                Assert.NotEqual(a.Me.Class, b.Me.Class);
                Assert.Equal(1, fx.NumGreen());
                Assert.Equal(1, fx.NumBlue());
                using (ScriptedClient c = fx.Join(GameplayFixture.Unique("carol")))
                {
                    // 1/1 -> random side for C, then D must land on the smaller team
                    int g = fx.NumGreen(), bl = fx.NumBlue();
                    Assert.Equal(3, g + bl);
                    Assert.Equal(1, Math.Abs(g - bl));
                    PlayerClass smaller = g < bl ? PlayerClass.Green : PlayerClass.Blue;
                    using (ScriptedClient d = fx.Join(GameplayFixture.Unique("dave")))
                    {
                        Assert.Equal(smaller, d.Me.Class);
                        Assert.Equal(2, fx.NumGreen());
                        Assert.Equal(2, fx.NumBlue());
                        // packet 5 for D as seen by A carries the same class
                        PlayerSpawnPacket spawn = a.WaitFor<PlayerSpawnPacket>(p => p.Name == d.Name, 10);
                        Assert.Equal(smaller, spawn.Class);
                        fx.Leave(d);
                    }
                    fx.Leave(c);
                }
                fx.Leave(b);
                fx.Leave(a);
            }
        }

        // SRV-04
        [Fact]
        public void Players_spawn_and_respawn_inside_their_teams_spawn_region()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            {
                PlayerClass team = a.Me.Class;
                PlayerSpawnPacket spawn = a.WaitFor<PlayerSpawnPacket>(p => p.SteamId == a.SteamId, 5);
                EntityListEntry mine = a.WaitFor<EntityListPacket>(null, 5).Entities.Find(e => e.Id == a.MyEntityId);
                Assert.NotNull(mine);
                Assert.True(Bases.InSpawn(team, mine.Position), "initial position " + mine.Position + " inside " + team + " spawn");
                Assert.True(Bases.InSpawn(team, fx.Position(a.Name)), "server position inside spawn");

                HashSet<string> distinct = new HashSet<string>();
                for (int i = 0; i < 20; i++)
                {
                    int mark = a.PacketCount;
                    fx.Kill(a.Name);
                    TeleportPacket tp = a.WaitFor<TeleportPacket>(null, TimeSpan.FromSeconds(10), mark);
                    Assert.True(Bases.InSpawn(team, tp.Position), "respawn " + i + " at " + tp.Position + " inside " + team + " spawn");
                    distinct.Add(tp.Position.ToString());
                    // wait for the class change that completes the respawn before the next kill
                    a.WaitFor<ClassChangePacket>(p => p.EntityId == -1 && p.Class == team, TimeSpan.FromSeconds(10), mark);
                }
                Assert.True(distinct.Count >= 2, "random spawn positions: " + string.Join(" ", distinct));
                fx.Leave(a);
            }
        }

        // SRV-04: a class without a spawn region spawns at the level centre (Size/2)
        [Fact]
        public void A_class_without_spawn_region_spawns_at_the_level_centre()
        {
            string pos = fx.Console.Eval("Sync(() => { var v = Server.level.GetSpawnPosition(PlayerClass.Spectator, new Vec2(8, 40)); return v.X + \",\" + v.Y; })").Trim();
            Assert.Equal((Bases.Width / 2) + "," + (Bases.Height / 2), pos);
        }

        // SRV-13
        [Fact]
        public void Team_switch_kills_the_switcher_and_respawns_them_on_the_new_team()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            {
                // alone: the lone player may have landed on either team; go to the other one
                PlayerClass from = a.Me.Class;
                PlayerClass to = from == PlayerClass.Green ? PlayerClass.Blue : PlayerClass.Green;
                string cmd = to == PlayerClass.Blue ? "/blue" : "/green";
                string teamWord = to == PlayerClass.Blue ? "blue" : "green";
                int scoreBefore = fx.Score(a.Name);

                int mark = a.PacketCount;
                a.SendChat(cmd);
                ChatPacket joined = a.WaitFor<ChatPacket>(p => p.Text.EndsWith(" joined the " + teamWord + " team."), TimeSpan.FromSeconds(5), mark);
                // ResetPlayer has already made the player a spectator when the display name is built
                Assert.Equal(TextUtil.COLOR_GRAY + a.Name + TextUtil.COLOR_WHITE + " joined the " + teamWord + " team.", joined.Text);
                Assert.Equal(to, fx.Team(a.Name));
                // death sequence. The 110 packet is queued from the chat thread and runs after
                // SetClass(Spectator) -> SetType has reset Health to MaxHealth, so it reports 100
                // (original quirk, docs/PORTING.md); deaths on the Server Main thread report 0.
                HealthPacket hp = a.WaitFor<HealthPacket>(p => p.EntityId == a.MyEntityId, TimeSpan.FromSeconds(5), mark);
                Assert.Equal(100f, hp.Health);
                ClassChangePacket spec = a.WaitFor<ClassChangePacket>(p => p.EntityId == -1 && p.Class == PlayerClass.Spectator, TimeSpan.FromSeconds(5), mark);
                Assert.Empty(spec.Weapons);
                ScorePacket score = a.WaitFor<ScorePacket>(p => p.EntityId == a.MyEntityId, TimeSpan.FromSeconds(5), mark);
                Assert.Equal(scoreBefore + 1, score.Score);
                // respawn on the new team
                TeleportPacket tp = a.WaitFor<TeleportPacket>(null, TimeSpan.FromSeconds(10), mark);
                Assert.True(Bases.InSpawn(to, tp.Position), "respawn " + tp.Position + " inside " + to + " spawn");
                ClassChangePacket back = a.WaitFor<ClassChangePacket>(p => p.EntityId == -1 && p.Class == to, TimeSpan.FromSeconds(10), mark);
                Assert.Equal(3, back.Weapons.Length);
                Assert.Equal(to, fx.CurrentClass(a.Name));

                // the same command again is a silent no-op (early return)
                int mark2 = a.PacketCount;
                a.SendChat(cmd);
                Assert.True(a.NoneWithin<ChatPacket>(null, TimeSpan.FromSeconds(1.5), mark2), "no chat for a repeated switch");
                Assert.True(a.NoneWithin<HealthPacket>(null, TimeSpan.FromSeconds(0.2), mark2), "no death for a repeated switch");
                Assert.Equal(to, fx.Team(a.Name));

                // switching back is allowed because the other team is now empty (0 < 1)
                string backCmd = from == PlayerClass.Blue ? "/blue" : "/green";
                string backWord = from == PlayerClass.Blue ? "blue" : "green";
                int mark3 = a.PacketCount;
                a.SendChat(backCmd);
                a.WaitFor<ChatPacket>(p => p.Text.EndsWith(" joined the " + backWord + " team."), TimeSpan.FromSeconds(5), mark3);
                Assert.Equal(from, fx.Team(a.Name));
                a.WaitFor<ClassChangePacket>(p => p.EntityId == -1 && p.Class == from, TimeSpan.FromSeconds(10), mark3);
                fx.Leave(a);
            }
        }

        // SRV-13
        [Fact]
        public void Team_switch_is_refused_when_teams_are_balanced()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            using (ScriptedClient b = fx.Join(GameplayFixture.Unique("bob")))
            {
                Assert.Equal(1, fx.NumGreen());
                Assert.Equal(1, fx.NumBlue());
                string cmd = a.Me.Class == PlayerClass.Green ? "/blue" : "/green";
                int markA = a.PacketCount, markB = b.PacketCount;
                a.SendChat(cmd);
                ChatPacket full = a.WaitFor<ChatPacket>(null, TimeSpan.FromSeconds(5), markA);
                // the refusal text is the same for both directions (Server.cs /blue branch says "Green team is full.")
                Assert.Equal(TextUtil.COLOR_ORANGE + "Green team is full.", full.Text);
                Assert.True(b.NoneWithin<ChatPacket>(null, TimeSpan.FromSeconds(1), markB), "refusal goes to the issuer only");
                Assert.True(a.NoneWithin<HealthPacket>(null, TimeSpan.FromSeconds(0.2), markA), "no death");
                Assert.Equal(a.Me.Class, fx.Team(a.Name));
                fx.Leave(b);
                fx.Leave(a);
            }
        }

        // SRV-14
        [Fact]
        public void Spec_is_permanent_and_blocks_rejoining_the_same_team()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            {
                PlayerClass team = a.Me.Class;
                int mark = a.PacketCount;
                a.SendChat("/spec");
                ClassChangePacket spec = a.WaitFor<ClassChangePacket>(p => p.EntityId == -1, TimeSpan.FromSeconds(5), mark);
                Assert.Equal(PlayerClass.Spectator, spec.Class);
                Assert.Empty(spec.Weapons);
                // no respawn ever: SetSpectator does not start RespawnPlayer (3x respawntime = 900 ms)
                Assert.True(a.NoneWithin<TeleportPacket>(null, TimeSpan.FromSeconds(1.5), mark), "no teleport");
                Assert.True(a.NoneWithin<HealthPacket>(null, TimeSpan.FromSeconds(0.1), mark), "no health packet");
                Assert.True(a.NoneWithin<ClassChangePacket>(p => p.Class != PlayerClass.Spectator, TimeSpan.FromSeconds(0.1), mark), "no class change back");
                Assert.Equal(PlayerClass.Spectator, fx.CurrentClass(a.Name));
                Assert.Equal(team, fx.Team(a.Name));
                Assert.False(fx.Console.EvalT<bool>("Sync(() => " + GameplayFixture.P(a.Name) + ".IsAlive())"));
                Assert.Equal(0, fx.Console.EvalT<int>("Sync(() => " + GameplayFixture.P(a.Name) + ".Inventory.Length)"));

                // the command for the team we already belong to does nothing at all
                string same = team == PlayerClass.Green ? "/green" : "/blue";
                int mark2 = a.PacketCount;
                a.SendChat(same);
                Assert.True(a.NoneWithin<ChatPacket>(null, TimeSpan.FromSeconds(1.5), mark2), "no chat");
                Assert.True(a.NoneWithin<ClassChangePacket>(null, TimeSpan.FromSeconds(0.1), mark2), "no class change");
                Assert.Equal(PlayerClass.Spectator, fx.CurrentClass(a.Name));
                Assert.Equal(team, fx.Team(a.Name));

                // the other team is empty, so switching there is allowed, but as a spectator nothing respawns
                string other = team == PlayerClass.Green ? "/blue" : "/green";
                string otherWord = team == PlayerClass.Green ? "blue" : "green";
                PlayerClass otherClass = team == PlayerClass.Green ? PlayerClass.Blue : PlayerClass.Green;
                int mark3 = a.PacketCount;
                a.SendChat(other);
                a.WaitFor<ChatPacket>(p => p.Text.EndsWith(" joined the " + otherWord + " team."), TimeSpan.FromSeconds(5), mark3);
                Assert.Equal(otherClass, fx.Team(a.Name));
                Assert.Equal(PlayerClass.Spectator, fx.CurrentClass(a.Name));
                Assert.True(a.NoneWithin<TeleportPacket>(null, TimeSpan.FromSeconds(1.5), mark3), "still no respawn");
                fx.Leave(a);
            }
        }
    }
}
