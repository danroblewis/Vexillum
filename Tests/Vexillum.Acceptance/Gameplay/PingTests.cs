using System;
using System.Collections.Generic;
using System.Threading;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.servergameplay
{
    public class PingFixture : GameplayFixture
    {
    }

    /// <summary>SRV-25: ping round trips, the reported ping and the lag-compensation frame.</summary>
    public class PingTests : IClassFixture<PingFixture>
    {
        private readonly PingFixture fx;

        public PingTests(PingFixture fx)
        {
            this.fx = fx;
        }

        /// <summary>Answers server pings after <paramref name="delayMs"/> on a helper thread (the reader thread must not block).</summary>
        private static void DelayPingReplies(ScriptedClient c, int delayMs)
        {
            c.AutoPing = false;
            c.PacketReceived += delegate(ServerPacket p)
            {
                if (p is PingPacket)
                {
                    Thread t = new Thread(delegate() { Thread.Sleep(delayMs); try { c.SendPingReply(); } catch (Exception) { } });
                    t.IsBackground = true;
                    t.Start();
                }
            };
        }

        [Fact]
        public void Ping_round_trip_sets_the_reported_ping_and_the_lag_frames()
        {
            using (ScriptedClient c = fx.Join(GameplayFixture.Unique("laggy")))
            {
                DelayPingReplies(c, 400);
                // pings go out every 5 s; the first 130 after our delayed reply reports the measured round trip
                PingTimesPacket pt = c.WaitFor<PingTimesPacket>(p => p.Entries.Exists(e => e.Key == c.MyEntityId && e.Value >= 380), TimeSpan.FromSeconds(20));
                short ms = pt.Entries.Find(e => e.Key == c.MyEntityId).Value;
                Assert.InRange(ms, 380, 700);
                Assert.Equal(ms.ToString(), fx.Console.Eval("Sync(() => " + GameplayFixture.P(c.Name) + ".pingString)").Trim());
                string r = fx.Console.Eval("Sync(() => { var p = " + GameplayFixture.P(c.Name) + "; int lag = (int)Get(p, \"lagFrames\"); return lag + \",\" + (p.GetClientFrame() == Server.level.frame - lag) + \",\" + Get(p, \"pingTime\"); })").Trim();
                string[] parts = r.Split(',');
                int lagFrames = int.Parse(parts[0]);
                int pingTime = int.Parse(parts[2]);
                Assert.Equal(pingTime / VexillumConstants.TIME_PER_FRAME, lagFrames);
                Assert.InRange(lagFrames, 380 / 16, 700 / 16);
                Assert.Equal("True", parts[1]);
                fx.Leave(c);
            }
        }

        // Lag compensation: a hitscan is tested against the frame the lagging client saw
        [Fact]
        public void Hitscans_are_resolved_against_the_frame_the_lagging_client_saw()
        {
            using (ScriptedClient shooter = fx.Join(GameplayFixture.Unique("laggy")))
            using (ScriptedClient target = fx.Join(GameplayFixture.Unique("target")))
            using (ScriptedClient control = fx.Join(GameplayFixture.Unique("prompt")))
            {
                DelayPingReplies(shooter, 400);
                // shooter and target are on different teams; the control shooter must be the target's enemy too.
                // If it landed on the target's team (2 vs 1) the switch command is allowed and kills it once.
                if (control.Me.Class == target.Me.Class)
                {
                    int m = control.PacketCount;
                    control.SendChat(control.Me.Class == PlayerClass.Green ? "/blue" : "/green");
                    control.WaitFor<ChatPacket>(p => p.Text.EndsWith(" team."), TimeSpan.FromSeconds(5), m);
                    control.WaitFor<ClassChangePacket>(p => p.EntityId == -1 && p.Class != PlayerClass.Spectator, TimeSpan.FromSeconds(15), m);
                }
                Assert.NotEqual(fx.CurrentClass(target.Name), fx.CurrentClass(shooter.Name));
                Assert.NotEqual(fx.CurrentClass(target.Name), fx.CurrentClass(control.Name));

                // solid platform west of the blue flag: shooters at both ends, target in the middle
                Vec2 posS = Walk.Park(fx, shooter, 2790, 628);
                Vec2 posC = Walk.Park(fx, control, 2910, 628);
                Vec2 posT = Walk.Park(fx, target, 2850, 628);
                shooter.SendWeaponSelect(1);
                control.SendWeaponSelect(1);
                GameplayFixture.WaitUntil(() => fx.Console.ServerPlayer(shooter.Name).WeaponIndex == 1 && fx.Console.ServerPlayer(control.Name).WeaponIndex == 1, TimeSpan.FromSeconds(5), "SMGs selected");
                // wait for the lag to be measured (>= 24 frames)
                GameplayFixture.WaitUntil(() => fx.Console.EvalT<int>("Sync(() => (int)Get(" + GameplayFixture.P(shooter.Name) + ", \"lagFrames\"))") >= 24, TimeSpan.FromSeconds(20), "lag measured");
                Assert.Equal(0, fx.Console.EvalT<int>("Sync(() => (int)Get(" + GameplayFixture.P(control.Name) + ", \"lagFrames\"))"));

                // control: the prompt client fires 200 ms after the target moved up out of the row -> miss
                Thread.Sleep(1200);
                int mT = target.PacketCount;
                target.SendPositionAbsolute((int)posT.X, (int)posT.Y + 120);
                Thread.Sleep(200);
                control.SendHitscan((float)Math.PI, (int)posC.X, (int)posC.Y);
                Assert.True(target.NoneWithin<HealthPacket>(p => p.EntityId == target.MyEntityId, TimeSpan.FromSeconds(1.5), mT), "a prompt client's shot is tested against the current frame: miss");
                Assert.Equal(100f, fx.Health(target.Name));

                // back to the row, settle, then the same move; the lagging client's shot uses the frame 400 ms ago -> hit
                posT = Walk.Park(fx, target, 2850, 628);
                Thread.Sleep(1200);
                int mT2 = target.PacketCount;
                target.SendPositionAbsolute((int)posT.X, (int)posT.Y + 120);
                Thread.Sleep(200);
                shooter.SendHitscan(0f, (int)posS.X, (int)posS.Y);
                HealthPacket hit = target.WaitFor<HealthPacket>(p => p.EntityId == target.MyEntityId, TimeSpan.FromSeconds(5), mT2);
                Assert.True(hit.Health < 100 && hit.Health > 90, "SMG hit at ~60 px: " + hit.Health);
                foreach (ScriptedClient c in new ScriptedClient[] { target, control, shooter })
                    fx.Leave(c);
            }
        }
    }
}
