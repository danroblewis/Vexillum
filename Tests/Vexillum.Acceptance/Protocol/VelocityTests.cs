using System;
using System.Collections.Generic;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>
    /// Packet 31 (velocities): its encoding and when the server sends it.
    /// A human's velocity on the server comes from physics (gravity,
    /// friction), from the difference between its position packets and
    /// from a hitscan knockback; the movement bits never make it walk
    /// (LivingEntity.SetMovement is only called client-side and by the AI).
    /// Two clients, bases, default bots (a server without bots is not an
    /// option, see docs/TESTING.md "Bots and maxbots"), so everything is
    /// filtered by entity id.
    /// </summary>
    public class VelocityTests : IClassFixture<DuoBasesFixture>
    {
        private readonly DuoBasesFixture fx;

        public VelocityTests(DuoBasesFixture fx)
        {
            this.fx = fx;
        }

        /// <summary>True when Server.level.terrain.GetLadder is set anywhere in column x between ground and ground + height (every 4 px).</summary>
        private bool ColumnHasLadder(int x, int ground, int height)
        {
            return fx.Server.Console.EvalT<bool>("Sync(() => { for (int y = " + ground + "; y <= " + (ground + height) + "; y += 4) if (Server.level.terrain.GetLadder(" + x + ", y)) return true; return false; })");
        }

        private static List<EntityMotion> EntriesFor(ScriptedClient c, short id, int startIndex)
        {
            List<EntityMotion> r = new List<EntityMotion>();
            foreach (VelocitiesPacket p in c.Packets<VelocitiesPacket>(startIndex))
                if (p.For(id) != null)
                    r.Add(p.For(id));
            return r;
        }

        // PROTO-30
        [Fact]
        public void Velocities_packet_lists_the_entities_whose_velocity_changed_and_never_the_receiver_at_rest()
        {
            string alice = Names.Unique("alice"), bob = Names.Unique("bob");
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                a.JoinGame(alice);
                ServerSide side = new ServerSide(fx.Server);
                short idA = a.MyEntityId;
                Vec2 spawnRest = side.WaitForRest(alice, TimeSpan.FromSeconds(10));
                b.JoinGame(bob);
                short idB = b.MyEntityId;

                // the first 31 a client gets lists every non-projectile entity except its own
                // (the per-client velocity table starts empty), each as int16-truncated Vec2
                VelocitiesPacket first = b.WaitFor<VelocitiesPacket>(null, TimeSpan.FromSeconds(3));
                List<short> flags = new List<short>();
                foreach (EntityListEntry e in b.Packets<EntityListPacket>()[0].Entities)
                    if (e.TypeIndex == 0 || e.TypeIndex == 3)
                        flags.Add(e.Id);
                Assert.Equal(2, flags.Count);
                Assert.NotNull(first.For(idA));
                foreach (short f in flags)
                    Assert.NotNull(first.For(f));
                Assert.Null(first.For(idB));
                Assert.Equal(new Vec2(0, 0), first.For(idA).Vector);     // at rest
                Assert.False(first.For(idA).Moving);                     // (the direction bit is the facing, set even at rest)
                Assert.False(first.For(idA).Jumping);
                foreach (EntityMotion m in first.Entries)
                {
                    Assert.Equal((int)m.Vector.X, m.Vector.X);           // the wire carries int16 components
                    Assert.Equal((int)m.Vector.Y, m.Vector.Y);
                    lock (b.Entities)
                    {
                        ScriptedClient.EntityState known;
                        Assert.True(b.Entities.TryGetValue(m.Id, out known), "31 names an unknown entity #" + m.Id);
                        Assert.NotEqual("Rocket", known.TypeName);       // projectiles are never listed
                        Assert.NotEqual("GrapplingHook", known.TypeName);
                    }
                }

                // stand somewhere with 160 px of free air above, then get dropped from 120 px up:
                // gravity (0.3 per frame) makes the velocity change by about one unit per 50 ms tick
                TerrainSnapshot t = a.Terrain;
                float k = spawnRest.Y - Ground.Below(t, (int)spawnRest.X, (int)spawnRest.Y);
                int probeY = (int)spawnRest.Y + 60;
                // (a humanoid on a ladder column hangs instead of falling, and the terrain
                // snapshot carries only the solid bit, so the server is asked about ladders)
                int sx = -1;
                for (int w = 100; w < t.Width - 250 && sx < 0; w += 150)
                {
                    int cand = Ground.FindClearPair(t, a, idA, w, w + 150, probeY, 0, 6, k, 120, 160);
                    if (cand >= 0 && !ColumnHasLadder(cand, Ground.Below(t, cand, probeY), 160))
                        sx = cand;
                }
                Assert.True(sx >= 0, "no clear standing spot with free air above and no ladder");
                int sy = Ground.Below(t, sx, probeY) + (int)k;
                a.SendPositionAbsolute(sx, sy);
                System.Threading.Thread.Sleep(100);   // a duration: the repeat zeroes the derived velocity
                a.SendPositionAbsolute(sx, sy);
                Assert.True(Poll.Until(() => Math.Abs(side.Position(alice).X - sx) <= 3, TimeSpan.FromSeconds(3)));
                Vec2 rest = side.WaitForRest(alice, TimeSpan.FromSeconds(5));
                int mb = b.PacketCount;
                // the server derives a velocity from the jump in position ((new - last) / elapsed),
                // which would throw the entity further up; the repeat 30 ms later makes it 0
                a.SendPositionAbsolute(sx, (int)rest.Y + 120);
                System.Threading.Thread.Sleep(30);    // a duration: the repeat zeroes the derived velocity
                a.SendPositionAbsolute(sx, (int)rest.Y + 120);
                Assert.True(Poll.Until(() => side.Position(alice).Y > rest.Y + 60, TimeSpan.FromSeconds(3)), "not lifted: " + side.Position(alice));
                Vec2 landed = side.WaitForRest(alice, TimeSpan.FromSeconds(10));
                Assert.True(Math.Abs(landed.Y - rest.Y) <= 4, "landed at " + landed + " (rest " + rest + "); velocities seen: " + string.Join(" ", EntriesFor(b, idA, mb)));
                // the fall as b saw it: falling velocities (y up, so negative) that only grow,
                // no sideways component, the jumping bit once faster than 1 px/frame, and a
                // final zero when it lands
                Assert.True(b.WaitUntil(delegate()
                {
                    List<EntityMotion> f = EntriesFor(b, idA, mb);
                    return f.Count > 0 && f[f.Count - 1].Vector.Y == 0 && f.Exists(m => m.Vector.Y < 0);
                }, TimeSpan.FromSeconds(5)), "no landing velocity after the fall: " + string.Join(" ", EntriesFor(b, idA, mb)));
                List<EntityMotion> fall = EntriesFor(b, idA, mb);
                EntityMotion last = fall[fall.Count - 1];
                List<float> falling = new List<float>();
                foreach (EntityMotion m in fall)
                {
                    Assert.Equal(0f, m.Vector.X);
                    Assert.Equal((int)m.Vector.Y, m.Vector.Y);
                    if (m.Vector.Y < 0)
                        falling.Add(m.Vector.Y);
                    if (m.Vector.Y <= -2)
                        Assert.True(m.Jumping, "airborne below -1 px/frame is flagged jumping: " + m);
                }
                Assert.True(falling.Count >= 3, "expected several falling velocities, got " + string.Join(" ", fall));
                for (int i = 1; i < falling.Count; i++)
                    Assert.True(falling[i] <= falling[i - 1], "falling velocities only grow: " + string.Join(",", falling));
                Assert.True(falling[falling.Count - 1] <= -4, "a 120 px fall ends faster than 4 px/frame: " + string.Join(",", falling));
                Assert.Equal(new Vec2(0, 0), last.Vector);
                Assert.False(last.Jumping);

                // while a's velocity holds (int-compared) it is in no further 31, whatever the bots do
                int quiet = b.PacketCount;
                Assert.True(b.NoneWithin<VelocitiesPacket>(p => p.For(idA) != null, TimeSpan.FromMilliseconds(1500), quiet), "31 for a while it rests: " + string.Join(" ", EntriesFor(b, idA, quiet)));

                // the receiver's own entity is listed only when the server forced its velocity
                // (velocityChanged: a hitscan knockback, which reports as a 110 for that entity)
                if (!a.Packets<HealthPacket>().Exists(h => h.EntityId == idA))
                    Assert.DoesNotContain(a.Packets<VelocitiesPacket>(), p => p.For(idA) != null);
                if (!b.Packets<HealthPacket>().Exists(h => h.EntityId == idB))
                    Assert.DoesNotContain(b.Packets<VelocitiesPacket>(), p => p.For(idB) != null);
                Assert.Null(a.ReaderError);
                Assert.Null(b.ReaderError);
            }
        }

        // PROTO-30
        [Fact]
        public void Sword_knockback_is_a_forced_velocity_sent_to_the_victim_and_the_others()
        {
            ServerSide side = new ServerSide(fx.Server);
            string alice;
            using (ScriptedClient a = Teams.JoinAsBlue(fx.Server, side, out alice))
            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                string bob = Names.Unique("bob");
                b.JoinGame(bob);
                Assert.NotEqual(a.Me.Class, b.Me.Class);
                short idA = a.MyEntityId, idB = b.MyEntityId;
                a.SendWeaponSelect(2);
                Assert.True(Poll.Until(() => side.WeaponIndex(alice) == 2, TimeSpan.FromSeconds(5)));
                Assert.Equal("Sword", side.WeaponTypeName(alice));
                Vec2 spawnRest = side.WaitForRest(alice, TimeSpan.FromSeconds(10));
                side.WaitForRest(bob, TimeSpan.FromSeconds(10));

                // b 20 px to the right of a on the same flat ground (the sword reaches 32 px)
                TerrainSnapshot t = a.Terrain;
                int ground = Ground.Below(t, (int)spawnRest.X, (int)spawnRest.Y);
                float k = spawnRest.Y - ground;
                int probeY = (int)spawnRest.Y + 60;
                const int gap = 20;
                int xa = Ground.FindClearPair(t, a, idA, 100, t.Width - 100, probeY, gap, 8, k, 60);
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
                Assert.True(pb.X - pivot.X > 0 && pb.X - pivot.X < 30, "b's centre must be within the sword's reach of the pivot: " + pivot + " -> " + pb);
                Assert.NotEqual("Spectator", side.CurrentClass(bob));
                float healthBefore = side.Health(bob);
                Assert.True(healthBefore > 30f, "b must survive a 25-damage hit: " + healthBefore);

                int ma = a.PacketCount, mb = b.PacketCount;
                a.SendHitscan(0f, (int)pivot.X, (int)pivot.Y);

                // the victim is told its own new velocity (velocityChanged) with the knockback
                // 8 along the swing, decayed by friction (halved per frame on the ground) by
                // the time of the next 50 ms tick; the others see the same changes as ints
                VelocitiesPacket own = b.WaitFor<VelocitiesPacket>(p => p.For(idB) != null, TimeSpan.FromSeconds(3), mb);
                EntityMotion knock = own.For(idB);
                Assert.Contains(knock.Vector.X, new float[] { 8, 4, 2, 1, 0 });   // 0 only when the tick came four frames late
                Assert.Equal(0f, knock.Vector.Y);
                Assert.False(knock.Jumping);
                VelocitiesPacket seen = a.WaitFor<VelocitiesPacket>(p => p.For(idB) != null && p.For(idB).Vector.X > 0, TimeSpan.FromSeconds(3), ma);
                Assert.Contains(seen.For(idB).Vector.X, new float[] { 8, 4, 2, 1 });
                Assert.Equal(0f, seen.For(idB).Vector.Y);
                HealthPacket hp = b.WaitFor<HealthPacket>(h => h.EntityId == idB && h.Health <= healthBefore - 24.9f, TimeSpan.FromSeconds(3), mb);
                Assert.True(hp.Health <= healthBefore - 24.9f);     // Sword.GetDamage: 25
                // the sword shows no hitscan line to anyone
                Assert.True(b.NoneWithin<HitscanPacket>(h => h.EntityId == idA, TimeSpan.FromMilliseconds(300), mb));
                // the swing carried b to the right and it comes to rest again
                Vec2 after = side.WaitForRest(bob, TimeSpan.FromSeconds(5));
                Assert.True(after.X > pb.X + 4, "b knocked from " + pb + " to " + after);
                Assert.Null(a.ReaderError);
                Assert.Null(b.ReaderError);
            }
        }
    }
}
