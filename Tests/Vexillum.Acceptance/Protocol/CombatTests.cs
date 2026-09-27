using System;
using System.Collections.Generic;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.protocol
{
    /// <summary>
    /// Weapons over the wire: rocket launcher fire/explode, reload, the
    /// grappling hook and the SMG hitscan. Two clients, bases, default bots
    /// (their own projectiles and sounds are filtered out by entity id).
    /// </summary>
    public class CombatTests : IClassFixture<DuoBasesFixture>
    {
        private readonly DuoBasesFixture fx;

        public CombatTests(DuoBasesFixture fx)
        {
            this.fx = fx;
        }

        private const float Up = -(float)(Math.PI / 2);   // Rocket.Setup: velocity (cos, -sin), y up => -pi/2 flies upward

        // PROTO-15
        [Fact]
        public void Firing_the_rocket_launcher_produces_ammo_fire_projectile_explode_and_remove_packets()
        {
            string alice = Names.Unique("alice"), bob = Names.Unique("bob");
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            using (ScriptedClient b = new ScriptedClient(fx.Server))
            {
                a.JoinGame(alice);
                b.JoinGame(bob);
                ServerSide side = new ServerSide(fx.Server);
                short idA = a.MyEntityId;
                Vec2 spawnRest = side.WaitForRest(alice, TimeSpan.FromSeconds(10));
                Assert.Equal(0, side.WeaponIndex(alice));
                Assert.Equal("RocketLauncher", side.WeaponTypeName(alice));

                // stand somewhere with 160 px of free air above and no bot within 120 px, so the
                // rocket travels before it explodes (bots crowd the spawn platforms)
                TerrainSnapshot t = a.Terrain;
                float k = spawnRest.Y - Ground.Below(t, (int)spawnRest.X, (int)spawnRest.Y);
                int probeY = (int)spawnRest.Y + 60;
                int sx = Ground.FindClearPair(t, a, idA, 100, t.Width - 100, probeY, 0, 6, k, 120, 160);
                Assert.True(sx >= 0, "no clear standing spot with free air above");
                int sy = Ground.Below(t, sx, probeY) + (int)k;
                a.SendPositionAbsolute(sx, sy);
                System.Threading.Thread.Sleep(100);   // a duration: the repeat zeroes the derived velocity
                a.SendPositionAbsolute(sx, sy);
                Assert.True(Poll.Until(() => Math.Abs(side.Position(alice).X - sx) <= 3, TimeSpan.FromSeconds(3)));
                Vec2 rest = side.WaitForRest(alice, TimeSpan.FromSeconds(5));

                int ma = a.PacketCount, mb = b.PacketCount;
                a.SendWeaponActivate(0, true, Up);
                a.SendWeaponActivate(0, false, Up);   // release, or the held button auto-fires every 500 ms

                AmmoPacket ammo = a.WaitFor<AmmoPacket>(null, TimeSpan.FromSeconds(5), ma);
                Assert.Equal(0, ammo.WeaponIndex);
                Assert.Equal(20, ammo.TotalAmmo);
                Assert.Equal(3, ammo.ClipAmmo);
                WeaponFirePacket fire = b.WaitFor<WeaponFirePacket>(p => p.EntityId == idA, TimeSpan.FromSeconds(5), mb);
                Assert.Equal(0, fire.Index);
                ProjectileCreatePacket rocketA = a.WaitFor<ProjectileCreatePacket>(p => p.OwnerId == idA, TimeSpan.FromSeconds(5), ma);
                ProjectileCreatePacket rocketB = b.WaitFor<ProjectileCreatePacket>(p => p.OwnerId == idA, TimeSpan.FromSeconds(5), mb);
                Assert.Equal(5, rocketA.TypeIndex);
                Assert.Equal("Rocket", rocketA.TypeName);
                Assert.Equal(rocketA.EntityId, rocketB.EntityId);
                Assert.Equal(rocketA.Frame, rocketB.Frame);
                Assert.Equal(Up, rocketA.Angle);
                Assert.Equal(Up, rocketB.Angle);
                Assert.InRange(rocketA.Position.X, rest.X - 30, rest.X + 30);
                Assert.InRange(rocketA.Position.Y, rest.Y - 30, rest.Y + 30);
                Assert.True(rocketA.Frame > fx.Server.Console.ServerFrame() - 600, "projectile frame is a recent server frame");

                // the rocket leaves the map (out of bounds is solid) and explodes
                short rocketId = rocketA.EntityId;
                EntityRemovePacket removedA = a.WaitFor<EntityRemovePacket>(r => r.EntityId == rocketId, TimeSpan.FromSeconds(5), ma);
                EntityRemovePacket removedB = b.WaitFor<EntityRemovePacket>(r => r.EntityId == rocketId, TimeSpan.FromSeconds(5), mb);
                Assert.Equal(removedA.Frame, removedB.Frame);
                ExplodePacket boomA = a.WaitFor<ExplodePacket>(e => e.Radius == 26 && Math.Abs(e.Position.X - rocketA.Position.X) < 60, TimeSpan.FromSeconds(5), ma);
                ExplodePacket boomB = b.WaitFor<ExplodePacket>(e => e.Radius == 26 && e.Seed == boomA.Seed, TimeSpan.FromSeconds(5), mb);
                Assert.Equal(boomA.Frame, boomB.Frame);
                Assert.Equal(boomA.Position, boomB.Position);
                Assert.False(boomA.Nonlethal);
                Assert.True(boomA.Position.Y > rocketA.Position.Y + 60, "fired upward from " + rocketA.Position + ": explodes well above the launcher, at " + boomA.Position);
                Assert.InRange(boomA.Seed, 0, 999);       // DateTime.Now.Millisecond (known original bug: not a random seed)
                Assert.True(removedA.Frame >= rocketA.Frame);

                // a does not get its own 14, and nobody gets a server-side ROCKET sound (it is client-side only)
                Assert.True(a.NoneWithin<WeaponFirePacket>(p => p.EntityId == idA, TimeSpan.FromMilliseconds(300), ma));
                Assert.Empty(b.Packets<SoundPacket>(mb).FindAll(s => s.EntityId == idA));
                Assert.Equal(3, side.ClipAmmo(alice));
                Assert.Equal(20, side.TotalAmmo(alice));
            }
        }

        // PROTO-16
        [Fact]
        public void Reload_reports_the_clip_immediately_and_again_once_it_is_refilled()
        {
            string alice = Names.Unique("alice");
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            {
                a.JoinGame(alice);
                ServerSide side = new ServerSide(fx.Server);
                side.WaitForRest(alice, TimeSpan.FromSeconds(10));
                int ma = a.PacketCount;
                a.SendWeaponActivate(0, true, Up);
                a.SendWeaponActivate(0, false, Up);
                AmmoPacket afterFire = a.WaitFor<AmmoPacket>(null, TimeSpan.FromSeconds(5), ma);
                Assert.Equal(3, afterFire.ClipAmmo);

                int mark = a.PacketCount;
                a.SendWeaponAction(KeyAction.Reload);
                AmmoPacket immediate = a.WaitFor<AmmoPacket>(null, TimeSpan.FromSeconds(5), mark);
                Assert.Equal(0, immediate.WeaponIndex);
                Assert.Equal(20, immediate.TotalAmmo);
                Assert.Equal(3, immediate.ClipAmmo);                // sent unconditionally after KeyDownServer, before Step reloads
                AmmoPacket refilled = a.WaitFor<AmmoPacket>(p => p.ClipAmmo == 4, TimeSpan.FromSeconds(3), immediate.Sequence + 1);
                Assert.Equal(0, refilled.WeaponIndex);
                Assert.Equal(20, refilled.TotalAmmo);              // reload never consumes totalAmmo in this code
                Assert.True((refilled.ReceivedAt - immediate.ReceivedAt).TotalMilliseconds < 1500);
                Assert.Equal(4, side.ClipAmmo(alice));
                Assert.Equal(20, side.TotalAmmo(alice));
                Assert.True(a.NoneWithin<AmmoPacket>(null, TimeSpan.FromMilliseconds(600), refilled.Sequence + 1), "no further ammo packets once the clip is full");
            }
        }

        // PROTO-26
        [Fact]
        public void Grappling_hook_fires_is_rate_limited_and_releases()
        {
            string alice = Names.Unique("alice");
            using (ScriptedClient a = new ScriptedClient(fx.Server))
            {
                a.JoinGame(alice);
                ServerSide side = new ServerSide(fx.Server);
                short idA = a.MyEntityId;
                Vec2 rest = side.WaitForRest(alice, TimeSpan.FromSeconds(10));
                Vec2 pivot = side.WeaponPivot(alice);

                // an aim direction with terrain between 80 and 400 px away (UpdateCanGrapple needs it
                // within 420 px). The spawn is random, so sweep the full circle in 1 degree steps
                // instead of trying a few fixed angles: the player rests on ground, and a shallow
                // downward angle always meets that ground somewhere in the window. Take the
                // candidate nearest the middle of the window so a bot wandering into the ray or
                // a crater from its rockets cannot push the distance out of it.
                float angle = float.NaN;
                int distance = -1;
                TerrainSnapshot terrain = a.Terrain;
                for (int deg = 0; deg < 360; deg++)
                {
                    float cand = (float)(deg * Math.PI / 180.0);
                    int d = Ground.RayToTerrain(terrain, pivot.X, pivot.Y, cand, 420);
                    if (d >= 80 && d <= 400 && (distance < 0 || Math.Abs(d - 240) < Math.Abs(distance - 240)))
                    {
                        angle = cand;
                        distance = d;
                    }
                }
                Assert.False(float.IsNaN(angle), "no terrain within 80..400 px around " + pivot);

                int ma = a.PacketCount;
                a.SendWeaponActivate(255, true, angle);
                ProjectileCreatePacket hook = a.WaitFor<ProjectileCreatePacket>(p => p.OwnerId == idA && p.TypeIndex == 2, TimeSpan.FromSeconds(5), ma);
                Assert.Equal("GrapplingHook", hook.TypeName);
                Assert.Equal(angle, hook.Angle);
                HookPacket hp = a.WaitFor<HookPacket>(h => h.PlayerEntityId == idA, TimeSpan.FromSeconds(3), ma);   // no WriteData: rides on the next flush
                Assert.Equal(hook.EntityId, hp.HookEntityId);
                Assert.Equal(hook.Frame, hp.Frame);
                Assert.True(side.HasHook(alice));

                // a second fire within 250 ms clears the hook but does not fire a new one
                int m2 = a.PacketCount;
                a.SendWeaponActivate(255, true, angle);
                EntityRemovePacket gone = a.WaitFor<EntityRemovePacket>(r => r.EntityId == hook.EntityId, TimeSpan.FromSeconds(3), m2);
                Assert.True(gone.Frame >= hook.Frame);
                Assert.True(a.NoneWithin<ProjectileCreatePacket>(p => p.OwnerId == idA && p.TypeIndex == 2, TimeSpan.FromMilliseconds(500), m2));
                Assert.False(side.HasHook(alice));

                // after the guard time a new hook can be fired, and 254 releases it
                System.Threading.Thread.Sleep(300);   // a duration: the 250 ms grapplingHookTime guard
                int m3 = a.PacketCount;
                a.SendWeaponActivate(255, true, angle);
                ProjectileCreatePacket hook2 = a.WaitFor<ProjectileCreatePacket>(p => p.OwnerId == idA && p.TypeIndex == 2, TimeSpan.FromSeconds(5), m3);
                Assert.NotEqual(hook.EntityId, hook2.EntityId);
                int m4 = a.PacketCount;
                a.SendWeaponActivate(254, true, angle);
                a.WaitFor<EntityRemovePacket>(r => r.EntityId == hook2.EntityId, TimeSpan.FromSeconds(3), m4);
                Assert.True(Poll.Until(() => !side.HasHook(alice), TimeSpan.FromSeconds(2)));
                Assert.Empty(a.Packets<DisconnectPacket>());
            }
        }

        // PROTO-27
        [Fact]
        public void SMG_hitscan_damages_an_enemy_and_is_shown_to_the_others_with_a_sound()
        {
            // b must be on the other team (CanDamage). With the default bots the second human
            // always lands on Green (RemoveBot drops a green bot on a tie, then GetSpawnClass
            // picks the smaller team), so the first human has to be Blue: its class is random
            // on an empty server, so retry until it is.
            ServerSide side = new ServerSide(fx.Server);
            ScriptedClient a = null;
            string alice = null;
            for (int attempt = 0; attempt < 10 && a == null; attempt++)
            {
                alice = Names.Unique("alice");
                ScriptedClient cand = new ScriptedClient(fx.Server);
                cand.JoinGame(alice);
                if (cand.Me.Class == PlayerClass.Blue)
                    a = cand;
                else
                {
                    cand.Dispose();
                    Assert.NotNull(fx.Server.WaitFor(alice + " disconnected", 10));
                    Assert.True(Poll.Until(() => side.BotNames().Count == 0, TimeSpan.FromSeconds(10)), "bots cleared once the human left");
                }
            }
            Assert.NotNull(a);
            using (a)
            {
                short idA = a.MyEntityId;
                PlayerClass classA = a.Me.Class;
                string bob = Names.Unique("bob");
                using (ScriptedClient b = new ScriptedClient(fx.Server))
                {
                    b.JoinGame(bob);
                    Assert.NotEqual(classA, b.Me.Class);
                    short idB = b.MyEntityId;
                    a.SendWeaponSelect(1);
                    Assert.True(Poll.Until(() => side.WeaponIndex(alice) == 1, TimeSpan.FromSeconds(5)));
                    Vec2 rest = side.WaitForRest(alice, TimeSpan.FromSeconds(10));

                    // both on flat ground, b a fixed gap to the right of a, nothing solid between them
                    TerrainSnapshot t = a.Terrain;
                    int ground = Ground.Below(t, (int)rest.X, (int)rest.Y);
                    float k = rest.Y - ground;
                    int probeY = (int)rest.Y + 60;
                    // two standing spots 80 px apart anywhere on flat ground, as far from the bots as possible
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
                    // the bots are hostile too, so work from b's health right now and the exact
                    // SMG damage for the distance between the two centres (GetDamage)
                    Assert.NotEqual("Spectator", side.CurrentClass(bob));
                    float healthBefore = side.Health(bob);
                    Assert.True(healthBefore > 3f, "b must be alive with room for the hit: " + healthBefore);
                    float dist = (float)Math.Sqrt((pb.X - pa.X) * (pb.X - pa.X) + (pb.Y - pa.Y) * (pb.Y - pa.Y));
                    Assert.InRange(dist, gap - 10f, gap + 10f);
                    float expectedDamage = dist < 50 ? 3f : (dist > 450 ? 1f : 1f + 2f * (1f - dist / 450f));
                    float expectedHealth = healthBefore - expectedDamage;

                    int ma = a.PacketCount, mb = b.PacketCount;
                    a.SendWeaponActivate(0, true, 0f);
                    a.SendWeaponActivate(0, false, 0f);
                    AmmoPacket ammo = a.WaitFor<AmmoPacket>(null, TimeSpan.FromSeconds(5), ma);
                    Assert.Equal(1, ammo.WeaponIndex);
                    Assert.Equal(200, ammo.TotalAmmo);
                    Assert.Equal(49, ammo.ClipAmmo);
                    WeaponFirePacket fire = b.WaitFor<WeaponFirePacket>(p => p.EntityId == idA, TimeSpan.FromSeconds(5), mb);
                    Assert.Equal(1, fire.Index);
                    SoundPacket smg = b.WaitFor<SoundPacket>(s => s.EntityId == idA, TimeSpan.FromSeconds(5), mb);
                    Assert.Equal(7, smg.SoundId);
                    Assert.Equal(Sounds.SMG, smg.Sound);

                    // the hitscan itself is a separate packet from the client
                    a.SendHitscan(0f, (int)pivot.X, (int)pivot.Y);
                    HitscanPacket shown = b.WaitFor<HitscanPacket>(h => h.EntityId == idA, TimeSpan.FromSeconds(5), mb);
                    Assert.NotNull(shown);
                    HealthPacket hpB = b.WaitFor<HealthPacket>(h => h.EntityId == idB && Math.Abs(h.Health - expectedHealth) < 0.2f, TimeSpan.FromSeconds(5), mb);
                    HealthPacket hpA = a.WaitFor<HealthPacket>(h => h.EntityId == idB && h.Health == hpB.Health, TimeSpan.FromSeconds(5), ma);
                    Assert.Equal(hpA.Health, hpB.Health);
                    Assert.InRange(healthBefore - hpB.Health, 2.5f, 2.8f);   // SMG: 1 + 2 * (1 - 80/450) at 80 px
                    Assert.True(hpB.Health < healthBefore);
                    VelocitiesPacket knock = b.WaitFor<VelocitiesPacket>(v => v.For(idB) != null, TimeSpan.FromSeconds(5), mb);
                    Assert.Equal(new Vec2(0, 0), knock.For(idB).Vector);   // knockback 0 for the SMG, but velocityChanged is raised

                    // a sees neither its own hitscan nor its own sound
                    Assert.True(a.NoneWithin<HitscanPacket>(h => h.EntityId == idA, TimeSpan.FromMilliseconds(300), ma));
                    Assert.Empty(a.Packets<SoundPacket>(ma).FindAll(s => s.EntityId == idA));
                }
            }
        }
    }
}
