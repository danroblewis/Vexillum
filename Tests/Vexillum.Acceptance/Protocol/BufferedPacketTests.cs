using System;
using System.Collections.Generic;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>
    /// ServerPlayer.SendPlayerHealth (110), like SendGameModeByte/Short/String
    /// (120-122), SendSound (98) and SendGrapplingHook (22), writes into the
    /// per-connection buffer without WriteData, so those packets only reach
    /// the socket together with the next packet that is flushed (docs/PORTING.md,
    /// "Known original bugs"). Observed with an SMG hitscan: the target's 20
    /// is flushed when the server handles the hitscan, while the shooter's
    /// 110 waits for the next 30/31/8 broadcast. Two clients, bases, default
    /// bots (see docs/TESTING.md "Bots and maxbots").
    /// </summary>
    public class BufferedPacketTests : IClassFixture<DuoBasesFixture>
    {
        private readonly DuoBasesFixture fx;

        public BufferedPacketTests(DuoBasesFixture fx)
        {
            this.fx = fx;
        }

        private const int Trials = 5;

        private sealed class Trial
        {
            public float HealthBefore;
            public HitscanPacket ShownToB;      // 20 to the target: flushed at once, marks when the server handled the hitscan
            public HealthPacket HealthAtA;      // the buffered 110 as the shooter receives it
            public ServerPacket Carrier;        // the first packet after it that the server flushes on its own
        }

        /// <summary>
        /// Puts a (SMG) and b on the same flat ground 80 px apart with clear air
        /// between them, both at rest, and returns a's weapon pivot.
        /// </summary>
        private static Vec2 Arrange(ScriptedClient a, ScriptedClient b, ServerSide side, string alice, string bob)
        {
            Assert.NotEqual(a.Me.Class, b.Me.Class);
            short idA = a.MyEntityId;
            a.SendWeaponSelect(1);
            Assert.True(Poll.Until(() => side.WeaponIndex(alice) == 1, TimeSpan.FromSeconds(5)));
            Assert.Equal("SMG", side.WeaponTypeName(alice));
            Vec2 rest = side.WaitForRest(alice, TimeSpan.FromSeconds(10));
            side.WaitForRest(bob, TimeSpan.FromSeconds(10));

            TerrainSnapshot t = a.Terrain;
            int ground = Ground.Below(t, (int)rest.X, (int)rest.Y);
            float k = rest.Y - ground;
            int probeY = (int)rest.Y + 60;
            const int gap = 80;
            int xa = Ground.FindClearPair(t, a, idA, 100, t.Width - 100, probeY, gap, 6, k, 60);
            Assert.True(xa >= 0, "no two clear standing spots " + gap + " px apart on the map");
            int xb = xa + gap;
            int y = Ground.Below(t, xa, probeY) + (int)k;
            a.SendPositionAbsolute(xa, y);
            b.SendPositionAbsolute(xb, y);
            System.Threading.Thread.Sleep(100);   // a duration: repeating the position zeroes the derived velocity
            a.SendPositionAbsolute(xa, y);
            b.SendPositionAbsolute(xb, y);
            Assert.True(Poll.Until(() => Math.Abs(side.Position(alice).X - xa) <= 3 && Math.Abs(side.Position(bob).X - xb) <= 3, TimeSpan.FromSeconds(3)));
            Vec2 pa = side.WaitForRest(alice, TimeSpan.FromSeconds(5));
            Vec2 pb = side.WaitForRest(bob, TimeSpan.FromSeconds(5));
            Assert.True(Math.Abs(pa.Y - pb.Y) <= 4, "a at " + pa + ", b at " + pb + " should stand at the same height");
            Vec2 pivot = side.WeaponPivot(alice);
            Assert.True(Ground.ClearBetween(t, (int)pivot.X, (int)pb.X, (int)pivot.Y), "terrain between a and b at y " + (int)pivot.Y);
            Assert.NotEqual("Spectator", side.CurrentClass(bob));
            return pivot;
        }

        /// <summary>The packets that ServerPlayer writes without WriteData (docs/PORTING.md).</summary>
        private static bool IsBufferedKind(ServerPacket p)
        {
            return p is HealthPacket || p is SoundPacket || p is HookPacket || p is GameModeBytePacket || p is GameModeShortPacket || p is GameModeStringPacket;
        }

        /// <summary>
        /// One hitscan from a on b (packet 20 alone damages: DoHitscan needs no
        /// preceding fire) and what the two connections then see.
        /// </summary>
        private static Trial Shoot(ScriptedClient a, ScriptedClient b, ServerSide side, string bob, Vec2 pivot)
        {
            short idA = a.MyEntityId, idB = b.MyEntityId;
            Trial t = new Trial();
            t.HealthBefore = side.Health(bob);
            Assert.True(t.HealthBefore > 3f, "b must be alive with room for the hit: " + t.HealthBefore);
            int ma = a.PacketCount, mb = b.PacketCount;
            a.SendHitscan(0f, (int)pivot.X, (int)pivot.Y);
            t.ShownToB = b.WaitFor<HitscanPacket>(h => h.EntityId == idA, TimeSpan.FromSeconds(5), mb);
            t.HealthAtA = a.WaitFor<HealthPacket>(h => h.EntityId == idB, TimeSpan.FromSeconds(5), ma);
            Assert.True(t.HealthAtA.Health < t.HealthBefore, "b's health " + t.HealthAtA.Health + " did not drop from " + t.HealthBefore);
            // the first packet after the 110 that the server flushes on its own (other buffered
            // packets may sit in the same write before it)
            int i = t.HealthAtA.Sequence + 1;
            Assert.True(a.WaitUntil(delegate()
            {
                List<ServerPacket> all = a.AllPackets();
                for (; i < all.Count; i++)
                    if (!IsBufferedKind(all[i]))
                    {
                        t.Carrier = all[i];
                        return true;
                    }
                return false;
            }, TimeSpan.FromSeconds(6)), "nothing flushed after the 110 within 6 s");
            return t;
        }

        // PROTO-29 (actual behaviour)
        [Fact]
        public void Health_packet_only_leaves_the_server_together_with_the_next_flushed_packet()
        {
            ServerSide side = new ServerSide(fx.Server);
            string alice;
            using (ScriptedClient a = Teams.JoinAsBlue(fx.Server, side, out alice))
            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                string bob = Names.Unique("bob");
                b.JoinGame(bob);
                Vec2 pivot = Arrange(a, b, side, alice, bob);

                List<string> report = new List<string>();
                for (int i = 0; i < Trials; i++)
                {
                    // a duration: spread the trials over the server's 50/100 ms broadcast period
                    System.Threading.Thread.Sleep(23 * i);
                    Trial t = Shoot(a, b, side, bob, pivot);
                    double delay = (t.HealthAtA.ReceivedAt - t.ShownToB.ReceivedAt).TotalMilliseconds;
                    double gap = (t.Carrier.ReceivedAt - t.HealthAtA.ReceivedAt).TotalMilliseconds;
                    report.Add("trial " + i + ": 110 " + delay.ToString("F1") + " ms after the hitscan, then " + t.Carrier + " " + gap.ToString("F1") + " ms later");
                    // the 110 sits in the buffer until a flushed packet carries it out: the two leave
                    // in one write, so the carrier is never more than a moment behind, whereas a
                    // 110 flushed on its own would be followed by the next broadcast up to 50-100 ms later
                    Assert.True(gap < 15, "the packet after the 110 came " + gap.ToString("F1") + " ms later, so the 110 was flushed on its own; " + string.Join("; ", report));
                }
                Assert.Null(a.ReaderError);
                Assert.Null(b.ReaderError);
            }
        }

        // PROTO-29 (correct behaviour)
        [Fact(Skip = "Known original bug: ServerPlayer.SendPlayerHealth writes 110 without WriteData, so it only leaves with the next flushed packet (docs/PORTING.md)")]
        public void Health_packet_reaches_the_other_clients_as_soon_as_the_server_writes_it()
        {
            ServerSide side = new ServerSide(fx.Server);
            string alice;
            using (ScriptedClient a = Teams.JoinAsBlue(fx.Server, side, out alice))
            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                string bob = Names.Unique("bob");
                b.JoinGame(bob);
                Vec2 pivot = Arrange(a, b, side, alice, bob);

                List<string> report = new List<string>();
                for (int i = 0; i < Trials; i++)
                {
                    System.Threading.Thread.Sleep(23 * i);   // a duration: spread the trials over the broadcast period
                    Trial t = Shoot(a, b, side, bob, pivot);
                    double delay = (t.HealthAtA.ReceivedAt - t.ShownToB.ReceivedAt).TotalMilliseconds;
                    report.Add("trial " + i + ": 110 " + delay.ToString("F1") + " ms after the hitscan");
                    // the target's 20 is flushed when the hitscan is handled; a flushed 110 follows within
                    // the task hop that damages the target, never a whole broadcast period later
                    Assert.True(delay < 40, string.Join("; ", report));
                }
            }
        }
    }
}
