using System;
using System.Collections.Generic;
using System.Threading;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.servergameplay
{
    public class WeaponsFixture : GameplayFixture
    {
    }

    /// <summary>Weapon activation, reload, selection, hitscans and friendly fire on the server.</summary>
    public class WeaponTests : IClassFixture<WeaponsFixture>
    {
        private readonly WeaponsFixture fx;

        public WeaponTests(WeaponsFixture fx)
        {
            this.fx = fx;
        }

        private const float Down = (float)Math.PI / 2;

        // Weapon activate: 14 to the others, 42 for the rocket, 11 to the shooter; action frames never lie in the past
        [Fact]
        public void Firing_the_rocket_launcher_broadcasts_fire_creates_the_projectile_and_reports_ammo()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            using (ScriptedClient b = fx.Join(GameplayFixture.Unique("bob")))
            {
                Walk.Park(fx, a, 2800, 628);
                List<string> stale = new List<string>();
                Action<ServerPacket> check = delegate(ServerPacket p)
                {
                    int now = a.Frame;
                    if (p is EntityCreatePacket && ((EntityCreatePacket)p).Frame < now) stale.Add(p + " < " + now);
                    if (p is EntityRemovePacket && ((EntityRemovePacket)p).Frame < now) stale.Add(p + " < " + now);
                    if (p is ProjectileCreatePacket && ((ProjectileCreatePacket)p).Frame < now) stale.Add(p + " < " + now);
                };
                a.PacketReceived += check;
                Assert.Equal(0, fx.Console.ServerPlayer(a.Name).WeaponIndex);
                int mA = a.PacketCount, mB = b.PacketCount;
                a.SendWeaponActivate(Protocol.Button.Left, true, Down);

                WeaponFirePacket fire = b.WaitFor<WeaponFirePacket>(p => p.EntityId == a.MyEntityId, TimeSpan.FromSeconds(5), mB);
                Assert.Equal(0, fire.Index);
                ProjectileCreatePacket rocketB = b.WaitFor<ProjectileCreatePacket>(p => p.OwnerId == a.MyEntityId, TimeSpan.FromSeconds(5), mB);
                ProjectileCreatePacket rocketA = a.WaitFor<ProjectileCreatePacket>(p => p.OwnerId == a.MyEntityId, TimeSpan.FromSeconds(5), mA);
                Assert.Equal("Rocket", rocketA.TypeName);
                Assert.Equal(rocketB.EntityId, rocketA.EntityId);
                Assert.Equal(rocketB.Angle, rocketA.Angle);
                Assert.Equal(Down, rocketA.Angle, 3);
                AmmoPacket ammo = a.WaitFor<AmmoPacket>(null, TimeSpan.FromSeconds(5), mA);
                Assert.Equal(0, ammo.WeaponIndex);
                Assert.Equal(20, ammo.TotalAmmo);
                Assert.Equal(3, ammo.ClipAmmo);
                Assert.True(a.NoneWithin<WeaponFirePacket>(p => p.EntityId == a.MyEntityId, TimeSpan.FromSeconds(0.5), mA), "the shooter gets no 14 of its own");
                a.SendWeaponActivate(Protocol.Button.Left, false, Down);

                // the rocket hits the platform below: explosion and removal
                ExplodePacket boom = a.WaitFor<ExplodePacket>(p => p.Radius == 26, TimeSpan.FromSeconds(10), mA);
                Assert.False(boom.Nonlethal);
                EntityRemovePacket gone = a.WaitFor<EntityRemovePacket>(p => p.EntityId == rocketA.EntityId, TimeSpan.FromSeconds(10), mA);
                Assert.True(gone.Frame >= rocketA.Frame, "removed after created");
                a.PacketReceived -= check;
                Assert.Empty(stale);
                fx.Leave(b);
                fx.Leave(a);
            }
        }

        [Fact]
        public void Reload_action_refills_the_clip_and_reports_ammo()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            {
                Walk.Park(fx, a, 2800, 628);
                int m = a.PacketCount;
                a.SendWeaponActivate(Protocol.Button.Left, true, Down);
                AmmoPacket afterShot = a.WaitFor<AmmoPacket>(null, TimeSpan.FromSeconds(5), m);
                a.SendWeaponActivate(Protocol.Button.Left, false, Down);
                Assert.Equal(3, afterShot.ClipAmmo);

                int m2 = a.PacketCount;
                a.SendWeaponAction(KeyAction.Reload);
                // KeyDownServer reports the ammo at once (still 3), then Step refills one round per reloadDelay and reports 4
                AmmoPacket immediate = a.WaitFor<AmmoPacket>(null, TimeSpan.FromSeconds(5), m2);
                Assert.Equal(3, immediate.ClipAmmo);
                AmmoPacket refilled = a.WaitFor<AmmoPacket>(p => p.ClipAmmo == 4, TimeSpan.FromSeconds(5), m2);
                Assert.Equal(0, refilled.WeaponIndex);
                Assert.Equal(20, refilled.TotalAmmo);
                Assert.Equal(4, fx.Console.EvalT<int>("Sync(() => ((ReloadableWeapon)" + GameplayFixture.P(a.Name) + ".Entity.Weapon).clipAmmo)"));
                fx.Leave(a);
            }
        }

        [Fact]
        public void Weapon_select_is_applied_on_the_server_and_echoed_to_the_others()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            using (ScriptedClient b = fx.Join(GameplayFixture.Unique("bob")))
            {
                int mA = a.PacketCount, mB = b.PacketCount;
                a.SendWeaponSelect(2);
                WeaponSelectPacket sel = b.WaitFor<WeaponSelectPacket>(p => p.EntityId == a.MyEntityId, TimeSpan.FromSeconds(5), mB);
                Assert.Equal(2, sel.Index);
                Assert.True(a.NoneWithin<WeaponSelectPacket>(null, TimeSpan.FromSeconds(0.5), mA), "no echo to the selecting player");
                GameplayFixture.WaitUntil(() => fx.Console.ServerPlayer(a.Name).WeaponIndex == 2, TimeSpan.FromSeconds(5), "server weapon index");
                Assert.Equal("Sword", fx.Console.Eval("Sync(() => " + GameplayFixture.P(a.Name) + ".Entity.Weapon.GetType().Name)").Trim());
                Assert.Equal(2, b.PlayerNamed(a.Name).WeaponIndex);
                fx.Leave(b);
                fx.Leave(a);
            }
        }

        // SRV-20
        [Fact]
        public void Sword_swing_is_a_nonlethal_explosion_plus_a_short_flat_damage_hitscan()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            using (ScriptedClient b = fx.Join(GameplayFixture.Unique("bob")))
            {
                Assert.NotEqual(a.Me.Class, b.Me.Class);
                Vec2 posA = Walk.Park(fx, a, 2800, 628);
                Vec2 posB = Walk.Park(fx, b, 2820, 628);
                a.SendWeaponSelect(2);
                GameplayFixture.WaitUntil(() => fx.Console.ServerPlayer(a.Name).WeaponIndex == 2, TimeSpan.FromSeconds(5), "sword selected");
                int mA = a.PacketCount, mB = b.PacketCount;

                // the swing: a non-lethal radius-22 explosion at the sword's end, no ammo packet (clipAmmo -1)
                a.SendWeaponActivate(Protocol.Button.Left, true, 0f);
                ExplodePacket swingB = b.WaitFor<ExplodePacket>(p => p.Radius == 22, TimeSpan.FromSeconds(5), mB);
                ExplodePacket swingA = a.WaitFor<ExplodePacket>(p => p.Radius == 22, TimeSpan.FromSeconds(5), mA);
                Assert.True(swingA.Nonlethal);
                Assert.Equal(swingB.Seed, swingA.Seed);
                Assert.InRange(swingA.Position.X, posA.X, posA.X + 40);
                b.WaitFor<WeaponFirePacket>(p => p.EntityId == a.MyEntityId && p.Index == 2, TimeSpan.FromSeconds(5), mB);
                a.SendWeaponActivate(Protocol.Button.Left, false, 0f);
                Assert.True(a.NoneWithin<AmmoPacket>(null, TimeSpan.FromSeconds(0.5), mA), "no ammo packet for the sword");
                Assert.True(b.NoneWithin<HealthPacket>(p => p.EntityId == b.MyEntityId, TimeSpan.FromSeconds(0.2), mB), "the swing itself does not hurt");

                // the hitscan: flat 25 damage and knockback 8 along the aim, nothing shown to the others
                int mA2 = a.PacketCount, mB2 = b.PacketCount;
                a.SendHitscan(0f, (int)posA.X, (int)posA.Y);
                HealthPacket hit = b.WaitFor<HealthPacket>(p => p.EntityId == b.MyEntityId, TimeSpan.FromSeconds(5), mB2);
                Assert.Equal(75f, hit.Health);
                // knockback: Velocity = unit * 8 to the right; by the time the 50 ms velocity broadcast goes
                // out friction (x 0.5 per frame) has already eaten part of it, so assert the direction and
                // the displacement it causes (8 + 4 + 2 + 1 px) rather than the exact vector
                VelocitiesPacket kb = b.WaitFor<VelocitiesPacket>(p => p.For(b.MyEntityId) != null && p.For(b.MyEntityId).Vector.X >= 1, TimeSpan.FromSeconds(5), mB2);
                Assert.True(kb.For(b.MyEntityId).Vector.X <= 8, "knockback starts at 8");
                GameplayFixture.WaitUntil(() => fx.Position(b.Name).X >= posB.X + 8, TimeSpan.FromSeconds(5), "B pushed to the right");
                Assert.True(b.NoneWithin<HitscanPacket>(null, TimeSpan.FromSeconds(0.5), mB2), "showHitscan is false for the sword");
                Assert.True(a.NoneWithin<AmmoPacket>(null, TimeSpan.FromSeconds(0.1), mA2));
                Assert.Equal(75f, fx.Health(b.Name));

                // out of reach: 40 px is beyond maxHitscanLengthSquared (32*32)
                posB = Walk.Park(fx, b, 2840, 628);
                Assert.True(posB.X - posA.X >= 38, "B parked 40 px away");
                int mB3 = b.PacketCount;
                a.SendHitscan(0f, (int)posA.X, (int)posA.Y);
                Assert.True(b.NoneWithin<HealthPacket>(p => p.EntityId == b.MyEntityId, TimeSpan.FromSeconds(1.5), mB3), "no hit at 40 px");
                Assert.Equal(75f, fx.Health(b.Name));
                fx.Leave(b);
                fx.Leave(a);
            }
        }

        // SRV-17
        [Fact]
        public void Friendly_fire_is_blocked_for_damage_but_knockback_still_applies()
        {
            using (Trio t = new Trio(fx))
            {
                Vec2 posT = Walk.Park(fx, t.Taker, 2800, 628);
                Vec2 posM = Walk.Park(fx, t.Mate, 2820, 628);
                Assert.Equal(fx.CurrentClass(t.Taker.Name), fx.CurrentClass(t.Mate.Name));
                t.Taker.SendWeaponSelect(2);
                GameplayFixture.WaitUntil(() => fx.Console.ServerPlayer(t.Taker.Name).WeaponIndex == 2, TimeSpan.FromSeconds(5), "sword selected");
                int mM = t.Mate.PacketCount;
                t.Taker.SendWeaponActivate(Protocol.Button.Left, true, 0f);
                t.Mate.WaitFor<ExplodePacket>(p => p.Radius == 22, TimeSpan.FromSeconds(5), mM);
                t.Taker.SendWeaponActivate(Protocol.Button.Left, false, 0f);
                t.Taker.SendHitscan(0f, (int)posT.X, (int)posT.Y);
                // the velocity assignment precedes the CanDamage check: the teammate is pushed to the right
                VelocitiesPacket kb = t.Mate.WaitFor<VelocitiesPacket>(p => p.For(t.Mate.MyEntityId) != null && p.For(t.Mate.MyEntityId).Vector.X >= 1, TimeSpan.FromSeconds(5), mM);
                Assert.True(kb.For(t.Mate.MyEntityId).Vector.X <= 8, "knockback starts at 8");
                GameplayFixture.WaitUntil(() => fx.Position(t.Mate.Name).X >= posM.X + 8, TimeSpan.FromSeconds(5), "teammate pushed to the right");
                Assert.True(t.Mate.NoneWithin<HealthPacket>(p => p.EntityId == t.Mate.MyEntityId, TimeSpan.FromSeconds(1.5), mM), "no damage to a teammate");
                Assert.Equal(100f, fx.Health(t.Mate.Name));
            }
        }

        // SRV-19 (b)(c)
        [Fact]
        public void SMG_hitscan_digs_destructible_terrain_but_not_solid_terrain()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            using (ScriptedClient b = fx.Join(GameplayFixture.Unique("bob")))
            {
                a.SendWeaponSelect(1);
                GameplayFixture.WaitUntil(() => fx.Console.ServerPlayer(a.Name).WeaponIndex == 1, TimeSpan.FromSeconds(5), "SMG selected");
                // (b) the flag platform is Solid (code 1): no explosion packet
                Vec2 posA = Walk.Park(fx, a, 2800, 628);
                Assert.Equal(1, fx.CollisionCode(2800, 608));
                int mA = a.PacketCount, mB = b.PacketCount;
                a.SendWeaponActivate(Protocol.Button.Left, true, Down);
                AmmoPacket ammo = a.WaitFor<AmmoPacket>(null, TimeSpan.FromSeconds(5), mA);
                Assert.Equal(1, ammo.WeaponIndex);
                Assert.Equal(49, ammo.ClipAmmo);
                b.WaitFor<WeaponFirePacket>(p => p.EntityId == a.MyEntityId && p.Index == 1, TimeSpan.FromSeconds(5), mB);
                a.SendWeaponActivate(Protocol.Button.Left, false, Down);
                a.SendHitscan(Down, (int)posA.X, (int)posA.Y);
                HitscanPacket shown = b.WaitFor<HitscanPacket>(p => p.EntityId == a.MyEntityId, TimeSpan.FromSeconds(5), mB);
                Assert.NotNull(shown);
                Assert.True(a.NoneWithin<HitscanPacket>(null, TimeSpan.FromSeconds(0.2), mA), "the shooter gets no 20 of its own");
                Assert.True(a.NoneWithin<ExplodePacket>(null, TimeSpan.FromSeconds(1.5), mA), "no digging in Solid terrain");
                Assert.True(fx.TerrainSolid(2800, 608));

                // (c) destructible ground (code >= 12): a radius-2 non-lethal explosion and the pixel is cleared
                posA = Walk.Park(fx, a, 1240, 470);
                int code = fx.CollisionCode(1240, 444);
                Assert.True(code >= 12, "collision code at the impact column is " + code);
                int mA2 = a.PacketCount, mB2 = b.PacketCount;
                a.SendHitscan(Down, (int)posA.X, (int)posA.Y);
                ExplodePacket dig = a.WaitFor<ExplodePacket>(null, TimeSpan.FromSeconds(5), mA2);
                Assert.Equal(2, dig.Radius);
                Assert.True(dig.Nonlethal);
                Assert.Equal(1240, dig.Position.X);
                Assert.InRange(dig.Position.Y, 438, 447);
                ExplodePacket digB = b.WaitFor<ExplodePacket>(null, TimeSpan.FromSeconds(5), mB2);
                Assert.Equal(dig.Seed, digB.Seed);
                Assert.Equal(dig.Position, digB.Position);
                GameplayFixture.WaitUntil(() => !fx.TerrainSolid((int)dig.Position.X, (int)dig.Position.Y), TimeSpan.FromSeconds(5), "impact pixel cleared");
                Assert.True(fx.CollisionCode((int)dig.Position.X, (int)dig.Position.Y) >= 12, "the code of the cleared pixel is unchanged");
                fx.Leave(b);
                fx.Leave(a);
            }
        }
    }
}
