using System;
using System.Collections.Generic;
using System.Reflection;
using Vexillum;
using Vexillum.Acceptance;
using Vexillum.Entities;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.physicsterrain
{
    /// <summary>
    /// Projectiles and entity/entity contact: Rocket, ClusterBomb/Bomblet,
    /// GrapplingHook and the ContactFilter rules, with the server-style
    /// Collision hook (OnCollide) supplied by SyntheticLevel. Rocket and
    /// ClusterBomb are internal classes of Game.dll; they are created by
    /// full name as StreamHelper does.
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class ProjectileTests
    {
        private static SyntheticLevel WallLevel()
        {
            // wall x >= 120 (nibble 12), floor y <= 10, otherwise air; 256x128
            return SyntheticLevel.Build(256, 128, delegate(int x, int y)
            {
                return (x >= 120 || y <= 10) ? SyntheticLevel.Destructible(12) : SyntheticLevel.Empty;
            });
        }

        private static List<Bomblet> Bomblets(SyntheticLevel level)
        {
            List<Bomblet> r = new List<Bomblet>();
            foreach (Entity e in level.EntityList)
                if (e is Bomblet)
                    r.Add((Bomblet)e);
            return r;
        }

        // PHYS-25: a rocket flies straight at 8 px/frame through FixedVelocity and ignores its owner's outline.
        [Fact]
        public void Rocket_flies_straight_at_8_px_per_frame_ignoring_gravity_and_its_owner()
        {
            SyntheticLevel level = WallLevel();
            level.UseServerCollision();
            HumanoidEntity owner = level.AddHumanoid(PlayerClass.Green, new Vec2(32, 64));
            Projectile r = SyntheticLevel.CreateEntity<Projectile>("Vexillum.Entities.Rocket");
            Assert.Equal(new Vec2(14, 5), r.Size);
            r.Position = new Vec2(40, 64);     // overlaps the owner's right outline column (x 35) with its own outline
            level.AddProjectile(r, 0f, owner);

            Assert.Equal(8f, r.FixedVelocity.X);
            Assert.Equal(0f, r.FixedVelocity.Y);
            Assert.Equal(0f, r.Rotation);
            Assert.Same(owner, r.GetOwner());
            Assert.True(level.EntityIndex.ContainsKey(r.ID));

            for (int n = 1; n <= 9; n++)
            {
                level.StepFrames(1);
                Assert.Equal(new Vec2(40 + 8 * n, 64), r.Position);
                Assert.True(level.EntityIndex.ContainsKey(r.ID), "still alive on frame " + n);
                Assert.Empty(level.Collisions);
            }
            // gravity is added to the raw velocity each frame, but the Velocity getter returns FixedVelocity
            // (Y = -0), so the EPSILON rule zeroes the raw velocity again before the frame ends
            Assert.Equal(0f, r.velocity.Y);
            Assert.Equal(r.FixedVelocity, r.Velocity);
            Assert.False(r.removed);
        }

        // PHYS-26: hitting terrain explodes (radius 26) three pixels past the nose and removes the rocket.
        [Fact]
        public void Rocket_hitting_terrain_explodes_and_is_removed()
        {
            SyntheticLevel level = WallLevel();
            level.UseServerCollision();
            HumanoidEntity owner = level.AddHumanoid(PlayerClass.Green, new Vec2(32, 64));
            Projectile r = SyntheticLevel.CreateEntity<Projectile>("Vexillum.Entities.Rocket");
            r.Position = new Vec2(40, 64);
            level.AddProjectile(r, 0f, owner);
            short id = r.ID;
            int solidBefore = level.SolidCount();

            int frames = 0;
            while (!r.removed && frames < 30)
            {
                level.StepFrames(1);
                frames++;
            }
            Assert.Equal(10, frames);                              // nose x = Position.X + 7 reaches the wall face at 120
            Assert.True(r.removed);
            Assert.False(level.EntityIndex.ContainsKey(id));
            Assert.DoesNotContain(r, level.EntityList);
            Assert.Single(level.Collisions);
            Assert.Same(r, level.Collisions[0].E1);
            Assert.Null(level.Collisions[0].E2);
            Assert.Equal(0, level.Collisions[0].Direction);
            Assert.Equal(10, level.Collisions[0].Frame);
            Assert.Equal(113f, r.Position.X);                      // stopped one sub-step before the probe hit x = 120
            Assert.False(level.IsSolid(120, 64), "the wall face was cratered");
            Assert.True(level.IsSolid(159, 64), "beyond the damage radius the wall is intact");
            Assert.True(level.SolidCount() < solidBefore);
            object ps = r.GetType().GetField("particleSystem", BindingFlags.Public | BindingFlags.Instance).GetValue(r);
            Assert.True((bool)ps.GetType().GetField("done", BindingFlags.Public | BindingFlags.Instance).GetValue(ps), "particleSystem.done");
            // no pixel keeps the removed rocket's id
            for (int x = 100; x < 128; x++)
                for (int y = 56; y < 72; y++)
                    Assert.NotEqual(id, level.EntityAt(x, y, null));
        }

        // PHYS-27 (a): two humanoid players walking into each other pass through (isPlayer && isPlayer).
        [Fact]
        public void Two_players_pass_through_each_other()
        {
            SyntheticLevel level = SyntheticLevel.Build(128, 64, delegate(int x, int y) { return y <= 10 ? SyntheticLevel.Destructible(8) : SyntheticLevel.Empty; });
            HumanoidEntity a = level.AddHumanoid(PlayerClass.Green, new Vec2(32, 30));
            HumanoidEntity b = level.AddHumanoid(PlayerClass.Blue, new Vec2(48, 30));
            level.StepFrames(10);
            a.moving = true; a.direction = true; a.SetMovement();
            b.moving = true; b.direction = false; b.SetMovement();
            level.StepFrames(12);
            // walking on the ground reports Collision(e, null, 1) on every frame with d > 1; no entity/entity contact
            Assert.NotEmpty(level.Collisions);
            Assert.DoesNotContain(level.Collisions, r => r.E2 != null);
            Assert.DoesNotContain(level.Collisions, r => r.Direction == 0);
            Assert.True(a.Position.X > b.Position.X, "crossed: a at " + a.Position.X + ", b at " + b.Position.X);
            Assert.False(a.xCollision);
            Assert.False(b.xCollision);
        }

        // PHYS-27 (b): two rockets head-on pass through.
        [Fact]
        public void Two_rockets_pass_through_each_other()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(256, 128, SyntheticLevel.Empty);
            level.UseServerCollision();
            HumanoidEntity owner1 = level.AddHumanoid(PlayerClass.Green, new Vec2(20, 20));
            HumanoidEntity owner2 = level.AddHumanoid(PlayerClass.Blue, new Vec2(236, 20));
            Projectile r1 = SyntheticLevel.CreateEntity<Projectile>("Vexillum.Entities.Rocket");
            Projectile r2 = SyntheticLevel.CreateEntity<Projectile>("Vexillum.Entities.Rocket");
            r1.Position = new Vec2(40, 64);
            r2.Position = new Vec2(120, 64);
            level.AddProjectile(r1, 0f, owner1);
            level.AddProjectile(r2, (float)Math.PI, owner2);
            Assert.Equal(-8f, r2.FixedVelocity.X, 4);
            level.StepFrames(8);
            Assert.Empty(level.Collisions);
            Assert.False(r1.removed);
            Assert.False(r2.removed);
            Assert.True(r1.Position.X > r2.Position.X, "crossed");
            Assert.Equal(104f, r1.Position.X);
            Assert.Equal(56f, r2.Position.X, 3);
        }

        // PHYS-27 (c): a humanoid and someone else's grappling hook pass through.
        [Fact]
        public void Humanoid_and_grappling_hook_pass_through()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(256, 128, SyntheticLevel.Empty);
            level.UseServerCollision();
            HumanoidEntity target = level.AddHumanoid(PlayerClass.Green, new Vec2(100, 64));
            HumanoidEntity shooter = level.AddHumanoid(PlayerClass.Blue, new Vec2(20, 20));
            GrapplingHook h = new GrapplingHook();
            h.Position = new Vec2(40, 64);
            level.AddProjectile(h, 0f, shooter);
            level.StepFrames(4);        // 24 px/frame: passes x 96..103 (the target's outline) on frame 3
            Assert.Empty(level.Collisions);
            Assert.False(h.anchored);
            Assert.Equal(136f, h.Position.X);
            Assert.Equal(100f, target.Position.X);
            Assert.Equal(61f, target.Position.Y);          // no floor: plain free fall (0+1+1+1) undisturbed by the hook
        }

        // PHYS-27 (d)/(e): other pairs collide with momentum exchange; a disabled target is passed through.
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Bomblet_and_basic_entity_collide_with_momentum_exchange_unless_disabled(bool disabled)
        {
            SyntheticLevel level = SyntheticLevel.Uniform(128, 128, SyntheticLevel.Empty);
            BasicEntity target = new BasicEntity();
            target.Size = new Vec2(16, 16);
            target.anchored = true;             // stays put; its Velocity getter reports zero
            target.disabled = disabled;
            level.Add(target, new Vec2(40, 64));
            Bomblet b = new Bomblet();
            b.Velocity = new Vec2(2, 0);
            level.Add(b, new Vec2(28, 64));
            Assert.Equal(5f, b.Mass);           // sqrt(25 * 1)
            Assert.Equal(16f, target.Mass);     // sqrt(256 * 1)

            level.StepFrames(2);

            if (disabled)
            {
                Assert.Empty(level.Collisions);
                Assert.Equal(0f, target.velocity.X);
                level.StepFrames(6);
                Assert.True(b.Position.X > 40, "passed through the target, x = " + b.Position.X);
            }
            else
            {
                Assert.Single(level.Collisions);
                Assert.Same(target, level.Collisions[0].E1);
                Assert.Same(b, level.Collisions[0].E2);
                Assert.Equal(0, level.Collisions[0].Direction);
                Assert.Equal(2, level.Collisions[0].Frame);
                // v2 = target.Velocity + (dv / (m1 + m2)) * 2 * m1 = 0 + (2 / 21) * 2 * 5
                Assert.Equal(2f / 21f * 2f * 5f, target.velocity.X, 3);
                // the bomblet's share v1 = 2 - (2/21)*2*16 = -1.048 is written, then the xCollision rule
                // (Level.cs 601-602) resets e.velocity.X to 0 in the same frame
                Assert.Equal(0f, b.velocity.X);
                Assert.True(b.Position.X <= 30f, "stopped at the target's left column, x = " + b.Position.X);
            }
        }

        // PHYS-28: a ClusterBomb bursts into four bomblets which crater the floor.
        [Fact]
        public void ClusterBomb_bursts_into_four_bomblets_that_crater_on_contact()
        {
            SyntheticLevel level = SyntheticLevel.Build(256, 160, delegate(int x, int y) { return y <= 10 ? SyntheticLevel.Destructible(12) : SyntheticLevel.Empty; });
            HumanoidEntity owner = level.AddHumanoid(PlayerClass.Green, new Vec2(200, 30));
            Projectile c = SyntheticLevel.CreateEntity<Projectile>("Vexillum.Entities.ClusterBomb");
            c.Position = new Vec2(64, 64);
            List<Vec2> burstVelocities = null;
            List<Vec2> burstPositions = null;
            List<Bomblet> burst = null;
            Vec2 burstPoint = Vec2.Zero;
            int solidAtBurst = -1;
            level.OnCollision = delegate(Entity e1, Entity e2, int direction)
            {
                e1.OnCollide(e2, direction);
                if (e2 != null)
                    e2.OnCollide(e1, direction);
                if (e1 == c)
                {
                    burst = Bomblets(level);
                    burstVelocities = new List<Vec2>();
                    burstPositions = new List<Vec2>();
                    foreach (Bomblet b in burst)
                    {
                        burstVelocities.Add(b.velocity);
                        burstPositions.Add(b.Position);
                    }
                    burstPoint = c.Position;
                    solidAtBurst = level.SolidCount();
                }
            };
            level.AddProjectile(c, (float)Math.PI / 2, owner);     // angle pi/2 = straight down (y up, v = (cos, -sin) * 8)
            Assert.Equal(-8f, c.FixedVelocity.Y, 4);
            int solidBefore = level.SolidCount();

            int frames = 0;
            while (!c.removed && frames < 40)
            {
                level.StepFrames(1);
                frames++;
            }
            Assert.True(c.removed, "cluster bomb hit the floor");
            Assert.Equal(7, frames);                                   // bottom probe y - 2.5 - ... reaches row 10
            Assert.NotNull(burst);
            Assert.Equal(4, burst.Count);
            Assert.Equal(new List<Vec2> { new Vec2(-2, 0), new Vec2(2, 0), new Vec2(0, 2), new Vec2(0, 2) }, burstVelocities);
            Assert.Equal(new Vec2(64, 13), burstPoint);                // bottom probe (int)(y - 2.5) == 10 stops the bomb at y 13
            foreach (Bomblet b in burst)
                Assert.Equal(new Vec2(5, 5), b.Size);
            foreach (Vec2 p in burstPositions)
                Assert.Equal(burstPoint, p);                            // all four start at the bomb's position (overlapping outlines)
            Assert.Equal(solidBefore, solidAtBurst);                    // radius 0, nonlethal: the bomb itself destroys nothing

            level.StepFrames(300);
            Assert.Empty(Bomblets(level));
            foreach (Bomblet b in burst)
            {
                Assert.True(b.removed);
                Assert.Contains(level.Collisions, r => r.E1 == b || r.E2 == b);
            }
            Assert.True(level.SolidCount() < solidBefore, "the floor was cratered by the bomblets");
            Assert.False(level.IsSolid(64, 10), "floor under the burst point destroyed");
        }

        // PHYS-29 (headless): the grappling hook flies at 24 px/frame, anchors, pulls the owner and releases.
        [Fact]
        public void Grappling_hook_anchors_pulls_the_owner_with_force_2_and_releases_within_one_body_height()
        {
            SyntheticLevel level = SyntheticLevel.Build(400, 128, delegate(int x, int y) { return (y <= 10 || x >= 300) ? SyntheticLevel.Destructible(8) : SyntheticLevel.Empty; });
            level.UseServerCollision();
            HumanoidEntity owner = level.AddHumanoid(PlayerClass.Green, new Vec2(60, 30));
            level.StepFrames(10);
            owner.moving = true;
            owner.xVelocity = 2;
            GrapplingHook h = new GrapplingHook();
            h.Position = new Vec2(70, 40);
            level.AddProjectile(h, 0f, owner);
            owner.hook = h;
            Assert.Equal(24f, h.FixedVelocity.Length(), 4);
            Assert.Equal(new Vec2(6, 6), h.Size);

            int anchorFrame = 0;
            for (int n = 1; n <= 20 && anchorFrame == 0; n++)
            {
                float x = h.Position.X;
                level.StepFrames(1);
                if (h.anchored)
                    anchorFrame = n;
                else
                    Assert.Equal(x + 24, h.Position.X);
            }
            Assert.Equal(10, anchorFrame);
            Assert.False(h.enablePhysics);
            Assert.Equal(Vec2.Zero, h.FixedVelocity);
            Assert.False(owner.moving);
            Assert.Equal(0f, owner.xVelocity);
            Assert.Equal(300, (int)(h.Position.X + h.HalfSize.X));  // nose on the wall face
            Vec2 anchor = h.Position;

            float dist = (anchor - owner.Position).Length();
            int releaseFrame = 0;
            Vec2 lastForce = Vec2.Zero;
            Vec2 lastVelocity = Vec2.Zero;
            for (int n = 1; n <= 60 && releaseFrame == 0; n++)
            {
                lastForce = owner.Force;
                lastVelocity = owner.velocity;
                level.StepFrames(1);
                if (h.removed)
                {
                    releaseFrame = n;
                    break;
                }
                Vec2 diff = anchor - owner.Position;
                float d = diff.Length();
                Assert.True(d < dist, "distance decreases: " + d + " after " + dist);
                dist = d;
                diff.Normalize();
                Assert.Equal(2f, owner.Force.Length(), 3);
                Assert.Equal(diff.X * 2, owner.Force.X, 2);
                Assert.Equal(diff.Y * 2, owner.Force.Y, 2);
                Assert.True(d * d >= owner.Size.Y * owner.Size.Y, "not yet within one body height");
            }
            Assert.InRange(releaseFrame, 15, 40);
            Assert.True((anchor - owner.Position).LengthSquared() < owner.Size.Y * owner.Size.Y, "released within 40 px");
            Assert.Null(owner.hook);
            Assert.True(owner.jumping);                     // SetGrapplingHook(null) sets it
            Assert.Equal(Vec2.Zero, owner.Force);
            Assert.False(level.EntityIndex.ContainsKey(h.ID));
            // velocity scaled by 0.25 from (last velocity + the force added by LivingEntity.Step on the release frame)
            Assert.Equal(0.25f * (lastVelocity.X + lastForce.X), owner.velocity.X, 1);
            Assert.True(owner.velocity.Y <= 1f, "no Jump() unless the scaled velocity.Y exceeds 1");
        }
    }
}
