using System;
using System.Threading;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.servergameplay
{
    /// <summary>Deaths and respawns with a 1 s respawn time (SRV-11, SRV-12).</summary>
    public class DeathFixture : GameplayFixture
    {
        protected override void Settings(ScratchRuntime runtime, ServerProcess.Options options)
        {
            runtime.SetServerSetting("respawntime", "1000");
        }
    }

    public class DeathTests : IClassFixture<DeathFixture>
    {
        private readonly DeathFixture fx;

        public DeathTests(DeathFixture fx)
        {
            this.fx = fx;
        }

        /// <summary>Angle for a hitscan from <paramref name="from"/> to <paramref name="to"/> (DoHitscan: unit = (cos a, -sin a)).</summary>
        public static float AimAngle(Vec2 from, Vec2 to)
        {
            return (float)Math.Atan2(-(to.Y - from.Y), to.X - from.X);
        }

        // SRV-11
        [Fact]
        public void Hitscan_kill_makes_the_victim_a_weaponless_spectator_and_respawns_after_respawntime()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            using (ScriptedClient b = fx.Join(GameplayFixture.Unique("bob")))
            {
                PlayerClass teamA = a.Me.Class, teamB = b.Me.Class;
                Assert.NotEqual(teamA, teamB);
                // both on the solid platform west of the blue flag (x 2780..2920 at y 608), 100 px apart
                Vec2 posA = Walk.Park(fx, a, 2800, 628);
                Vec2 posB = Walk.Park(fx, b, 2900, 628);
                b.SendWeaponSelect(1);   // SMG
                GameplayFixture.WaitUntil(() => fx.Console.ServerPlayer(b.Name).WeaponIndex == 1, TimeSpan.FromSeconds(5), "B selected the SMG");
                int scoreB = fx.Score(b.Name);
                int mA = a.PacketCount, mB = b.PacketCount;

                // SMG damage at 100 px is 1 + 2 * (1 - 100/450) ~ 2.56 per hitscan; the server trusts every packet 20
                float angle = AimAngle(posB, posA);
                int shots = 0;
                while (shots < 80 && !a.Packets<HealthPacket>(mA).Exists(p => p.EntityId == a.MyEntityId && p.Health == 0))
                {
                    b.SendHitscan(angle, (int)posB.X, (int)posB.Y);
                    shots++;
                    Thread.Sleep(30);
                }
                HealthPacket dead = a.WaitFor<HealthPacket>(p => p.EntityId == a.MyEntityId && p.Health == 0, TimeSpan.FromSeconds(5), mA);
                Assert.True(shots >= 30, "the kill took " + shots + " hitscans (about 40 expected at 100 px)");
                Assert.Contains(a.Packets<HealthPacket>(mA), p => p.EntityId == a.MyEntityId && p.Health > 0 && p.Health < 100);

                ClassChangePacket specA = a.WaitFor<ClassChangePacket>(p => p.EntityId == -1 && p.Class == PlayerClass.Spectator, TimeSpan.FromSeconds(5), mA);
                Assert.Empty(specA.Weapons);
                MessagePacket killedBy = a.WaitFor<MessagePacket>(p => p.MessageId == Messages.KILLED_BY, TimeSpan.FromSeconds(5), mA);
                Assert.Equal(new string[] { (teamB == PlayerClass.Green ? TextUtil.COLOR_GREEN : TextUtil.COLOR_BLUE) + b.Name }, killedBy.Args);
                Assert.Equal(2000, killedBy.DurationMs);

                ClassChangePacket specB = b.WaitFor<ClassChangePacket>(p => p.EntityId == a.MyEntityId && p.Class == PlayerClass.Spectator, TimeSpan.FromSeconds(5), mB);
                Assert.Empty(specB.Weapons);
                ScorePacket score = b.WaitFor<ScorePacket>(p => p.EntityId == b.MyEntityId, TimeSpan.FromSeconds(5), mB);
                Assert.Equal(scoreB + 1, score.Score);
                MessagePacket youKilled = b.WaitFor<MessagePacket>(p => p.MessageId == Messages.YOU_KILLED, TimeSpan.FromSeconds(5), mB);
                // the victim's display name is built after SetClass(Spectator): gray
                Assert.Equal(new string[] { TextUtil.COLOR_GRAY + a.Name }, youKilled.Args);
                Assert.False(fx.Console.EvalT<bool>("Sync(() => " + GameplayFixture.P(a.Name) + ".IsAlive())"));

                // respawn after respawntime (1000 ms): teleport into the own spawn, full health, class back with 3 weapons
                TeleportPacket tp = a.WaitFor<TeleportPacket>(null, TimeSpan.FromSeconds(10), specA.Sequence);
                Assert.True(Bases.InSpawn(teamA, tp.Position), "respawn " + tp.Position + " inside " + teamA + " spawn");
                double dt = (tp.ReceivedAt - specA.ReceivedAt).TotalSeconds;
                Assert.InRange(dt, 0.9, 1.6);
                HealthPacket full = a.WaitFor<HealthPacket>(p => p.EntityId == a.MyEntityId && p.Health == 100 && p.Sequence > tp.Sequence, TimeSpan.FromSeconds(5));
                ClassChangePacket backA = a.WaitFor<ClassChangePacket>(p => p.EntityId == -1 && p.Class == teamA, TimeSpan.FromSeconds(5), tp.Sequence);
                Assert.Equal(new string[] { "RocketLauncher", "SMG", "Sword" }, backA.Weapons);
                Assert.True(full.Sequence < backA.Sequence, "health before class change");
                ClassChangePacket backB = b.WaitFor<ClassChangePacket>(p => p.EntityId == a.MyEntityId && p.Class == teamA, TimeSpan.FromSeconds(5), specB.Sequence);
                Assert.Equal(3, backB.Weapons.Length);
                Assert.True(fx.Console.EvalT<bool>("Sync(() => " + GameplayFixture.P(a.Name) + ".IsAlive())"));

                // and the respawned player can fire again: a rocket appears (42) and the clip drops (11)
                int mA2 = a.PacketCount;
                a.SendWeaponActivate(Protocol.Button.Left, true, (float)Math.PI / 2);
                ProjectileCreatePacket rocket = a.WaitFor<ProjectileCreatePacket>(p => p.OwnerId == a.MyEntityId, TimeSpan.FromSeconds(5), mA2);
                Assert.Equal("Rocket", rocket.TypeName);
                AmmoPacket ammo = a.WaitFor<AmmoPacket>(null, TimeSpan.FromSeconds(5), mA2);
                Assert.Equal(0, ammo.WeaponIndex);
                Assert.Equal(3, ammo.ClipAmmo);
                a.SendWeaponActivate(Protocol.Button.Left, false, (float)Math.PI / 2);
                fx.Leave(b);
                fx.Leave(a);
            }
        }

        // SRV-12
        [Fact]
        public void Self_inflicted_damage_hurts_and_a_self_kill_credits_the_victim()
        {
            using (ScriptedClient a = fx.Join(GameplayFixture.Unique("alice")))
            {
                // stand on destructible ground and fire a rocket straight down (angle +pi/2: unit (0,-1))
                Walk.Park(fx, a, 1240, 470);
                int m = a.PacketCount;
                a.SendWeaponActivate(Protocol.Button.Left, true, (float)Math.PI / 2);
                a.WaitFor<ProjectileCreatePacket>(p => p.OwnerId == a.MyEntityId, TimeSpan.FromSeconds(5), m);
                a.SendWeaponActivate(Protocol.Button.Left, false, (float)Math.PI / 2);
                ExplodePacket boom = a.WaitFor<ExplodePacket>(p => p.Radius == 26 && !p.Nonlethal, TimeSpan.FromSeconds(10), m);
                Assert.InRange(boom.Position.X, 1200, 1280);
                // CanDamage(p1 == p2) is true: the own rocket hurts
                HealthPacket hurt = a.WaitFor<HealthPacket>(p => p.EntityId == a.MyEntityId && p.Health < 100, TimeSpan.FromSeconds(5), m);
                Assert.True(hurt.Health > 0, "one rocket does at most 50 damage");
                Assert.True(a.NoneWithin<MessagePacket>(p => p.MessageId == Messages.KILLED_BY || p.MessageId == Messages.YOU_KILLED, TimeSpan.FromSeconds(0.5), m));

                // a death without an attacker (ResetPlayer path) credits the victim: Kill(attacker == defender)
                int scoreBefore = fx.Score(a.Name);
                int m2 = a.PacketCount;
                fx.Kill(a.Name);
                ScorePacket score = a.WaitFor<ScorePacket>(p => p.EntityId == a.MyEntityId, TimeSpan.FromSeconds(5), m2);
                Assert.Equal(scoreBefore + 1, score.Score);
                Assert.True(a.NoneWithin<MessagePacket>(p => p.MessageId == Messages.KILLED_BY || p.MessageId == Messages.YOU_KILLED, TimeSpan.FromSeconds(1), m2), "no kill messages when attacker == defender");
                Assert.Equal(scoreBefore + 1, fx.Score(a.Name));
                a.WaitFor<ClassChangePacket>(p => p.EntityId == -1 && p.Class != PlayerClass.Spectator, TimeSpan.FromSeconds(10), m2);
                fx.Leave(a);
            }
        }
    }
}
