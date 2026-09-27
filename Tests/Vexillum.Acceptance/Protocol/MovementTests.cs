using System;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>
    /// Position packets 14-19: the server trusts the client for its own
    /// position, relays it to the other clients in packet 30 and relays
    /// movement bits and arm angle in 30/32. Two clients, bases, default bots.
    /// </summary>
    public class MovementTests : IClassFixture<DuoBasesFixture>
    {
        private readonly DuoBasesFixture fx;

        public MovementTests(DuoBasesFixture fx)
        {
            this.fx = fx;
        }

        /// <summary>
        /// A spot on flat ground near x where a resting humanoid stays put:
        /// the ground level under the column plus the offset the player's own
        /// spawn rest position shows (centre above the ground).
        /// </summary>
        private static Vec2 StandingSpot(ScriptedClient c, Vec2 rest, int xFrom, int xTo)
        {
            TerrainSnapshot t = c.Terrain;
            int ground = Ground.Below(t, (int)rest.X, (int)rest.Y);
            Assert.True(ground >= 0, "no ground under the spawn rest position " + rest);
            float k = rest.Y - ground;
            Assert.InRange(k, 1, 60);
            int probeY = (int)rest.Y + 60;
            // the same platform (same ground level) as the spawn, at least 40 px away from the
            // rest position and as far as possible from bots and dropped flags
            int x = Ground.FindClearSpot(t, c, c.MyEntityId, xFrom, xTo, probeY, 14, ground, (int)rest.X, 40, 0, 30);
            Assert.True(x >= 0, "no clear flat ground at level " + ground + " in [" + xFrom + ", " + xTo + "]");
            return new Vec2(x, ground + k);
        }

        // PROTO-12
        [Fact]
        public void Absolute_delta_and_unchanged_position_packets_set_the_server_position_as_decoded()
        {
            string alice = Names.Unique("alice"), bob = Names.Unique("bob");
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                a.JoinGame(alice);
                b.JoinGame(bob);
                ServerSide side = new ServerSide(fx.Server);
                short idA = a.MyEntityId;
                Vec2 rest = side.WaitForRest(alice, TimeSpan.FromSeconds(10));
                Vec2 spot = StandingSpot(a, rest, (int)rest.X - 150, (int)rest.X + 150);
                int x = (int)spot.X, y = (int)spot.Y;
                Assert.True(Math.Abs(x - rest.X) >= 40, "pick a spot away from the rest position");

                // 18: absolute. The server also derives a velocity from (new - lastPosition) / elapsed,
                // and lastPosition starts at (0, 0), so the first packet gives the entity a drift;
                // the real client repeats its position every 100 ms, which zeroes it (diff 0).
                int mb = b.PacketCount;
                a.SendPositionAbsolute(x, y);
                System.Threading.Thread.Sleep(100);   // a duration: the client's position interval
                a.SendPositionAbsolute(x, y);
                Assert.True(Poll.Until(delegate()
                {
                    Vec2 p = side.Position(alice);
                    return Math.Abs(p.X - x) <= 2 && Math.Abs(p.Y - y) <= 4;
                }, TimeSpan.FromSeconds(3)), "server position after 18 is " + side.Position(alice) + ", expected " + spot);
                PositionsPacket seen = b.WaitFor<PositionsPacket>(p => p.For(idA) != null && Math.Abs(p.For(idA).Vector.X - x) <= 6 && Math.Abs(p.For(idA).Vector.Y - y) <= 8, TimeSpan.FromSeconds(3), mb);
                Assert.NotNull(seen);
                Vec2 settled = side.WaitForRest(alice, TimeSpan.FromSeconds(5));
                Assert.InRange(settled.X, x - 3, x + 3);
                Assert.InRange(settled.Y, y - 4, y + 4);
                Assert.Equal(0f, side.Velocity(alice).X);        // the repeat zeroed the derived drift (Y carries one frame of gravity while standing)

                // a is never told its own position in 30 (e != p.Entity)
                Assert.DoesNotContain(a.Packets<PositionsPacket>(), p => p.For(idA) != null);
                Assert.Contains(b.Packets<PositionsPacket>(), p => p.For(idA) != null);

                // 16: delta (+5, +20) from lastPosition, not from the physics position.
                // Only the position right after the packet is asserted: where the airborne
                // entity lands is not deterministic on this server (the derived velocity
                // depends on the time since the last packet, and a hostile bot's hit sets
                // its velocity or kills it, which freezes it mid-air until the respawn).
                a.SendPositionDelta(5, 20);
                Assert.True(Poll.Until(delegate()
                {
                    Vec2 p = side.Position(alice);
                    return Math.Abs(p.X - (x + 5)) <= 3 && p.Y >= y + 8;
                }, TimeSpan.FromSeconds(3)), "server position after 16 is " + side.Position(alice) + ", expected about " + (x + 5) + "," + (y + 20));
                // let physics carry it well below lastPosition so that 14 is observable
                // (normally it falls; not asserted, see above)
                Poll.Until(() => side.Position(alice).Y < y + 8, TimeSpan.FromSeconds(3));

                // 14: unchanged means "back to lastPosition" (x + 5, y + 20) with a zero velocity
                // (diff 0 in SetVelocity), whatever physics did meanwhile
                a.SendPositionUnchanged();
                Assert.True(Poll.Until(delegate()
                {
                    Vec2 p = side.Position(alice);
                    return Math.Abs(p.X - (x + 5)) <= 1.5f && p.Y >= y + 10;
                }, TimeSpan.FromSeconds(3)), "server position after 14 is " + side.Position(alice) + ", expected lastPosition " + (x + 5) + "," + (y + 20));
                Assert.Equal(0f, side.Velocity(alice).X);        // no derived drift this time: diff 0 gave velocity 0
            }
        }

        // PROTO-13
        [Fact]
        public void Movement_bits_and_arm_angle_are_relayed_to_other_clients()
        {
            string alice = Names.Unique("alice"), bob = Names.Unique("bob");
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                a.JoinGame(alice);
                b.JoinGame(bob);
                ServerSide side = new ServerSide(fx.Server);
                short idA = a.MyEntityId;
                Vec2 rest = side.WaitForRest(alice, TimeSpan.FromSeconds(10));
                bool[] before = side.MovementFlags(alice);
                Assert.False(before[0], "a fresh player is not moving");

                int mb = b.PacketCount;
                float angle = (float)(Math.PI / 2);
                a.SendPositionAbsolute((int)rest.X, (int)rest.Y, angle, true, true, false);   // 19: moving, right, not jumping

                AnglesPacket ang = b.WaitFor<AnglesPacket>(p => p.For(idA) != null, TimeSpan.FromSeconds(3), mb);
                Assert.InRange(ang.For(idA).Raw, 62, 64);                       // (pi/2 / pi) * 127 = 63.5 truncated, re-encoded by the server
                Assert.InRange(ang.For(idA).Angle, angle - 0.03f, angle + 0.03f);
                PositionsPacket pos = b.WaitFor<PositionsPacket>(p => p.For(idA) != null && p.For(idA).MovementByte == 3, TimeSpan.FromSeconds(3), mb);
                Assert.True(pos.For(idA).Moving && pos.For(idA).Direction && !pos.For(idA).Jumping);
                Assert.True(Poll.Until(delegate()
                {
                    bool[] f = side.MovementFlags(alice);
                    return f[0] && f[1] && !f[2];
                }, TimeSpan.FromSeconds(3)), "server flags " + string.Join(",", side.MovementFlags(alice)));
                Assert.InRange(side.ArmAngle(alice), angle - 0.03f, angle + 0.03f);
                // the entity walks right as instructed
                Assert.True(Poll.Until(() => side.Position(alice).X > rest.X + 4, TimeSpan.FromSeconds(3)), "moving+right should carry the entity to the right");
                // a itself never gets its own angle or movement back
                Assert.DoesNotContain(a.Packets<AnglesPacket>(), p => p.For(idA) != null);
                Assert.DoesNotContain(a.Packets<PositionsPacket>(), p => p.For(idA) != null);
            }
        }
    }
}
