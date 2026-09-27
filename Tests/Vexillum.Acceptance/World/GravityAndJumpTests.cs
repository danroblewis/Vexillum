using System;
using System.Collections.Generic;
using Vexillum;
using Vexillum.Acceptance;
using Vexillum.Entities;
using Vexillum.Game;
using Vexillum.util;
using Xunit;

namespace Vexillum.Acceptance.physicsterrain
{
    /// <summary>
    /// Vertical motion of a humanoid in Level.DoPhysicsForEntity: gravity,
    /// landing, jumping, and the entities that are not integrated at all.
    ///
    /// Integration note (Level.cs 466-548): the position loop runs
    /// ceil(|v|) sub-steps with k = (i+1)/|v|, so a frame moves the entity by
    /// ceil(|v|) whole pixels along v, never by the fractional |v| itself.
    /// A resting entity alternates between a frame with d = 0 (no collision
    /// test runs, gravity adds -0.3) and a frame that re-detects the ground
    /// (velocity.Y back to 0). Both are the author's behaviour.
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class GravityAndJumpTests
    {
        private static uint Floor(int x, int y)
        {
            return y <= 10 ? SyntheticLevel.Destructible(8) : SyntheticLevel.Empty;
        }

        // PHYS-10: gravity accumulates 0.3 per frame without a terminal velocity; 'jumping' once falling faster than 1 px/frame.
        [Fact]
        public void Free_fall_accumulates_0_3_per_frame_and_moves_ceil_of_the_speed_in_pixels()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(64, 256, SyntheticLevel.Empty);
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(32, 200));
            Assert.Equal(Vec2.Zero, e.Velocity);
            Assert.False(e.jumping);

            float y = 200;
            for (int n = 1; n <= 12; n++)
            {
                float vBefore = e.velocity.Y;
                level.StepFrames(1);
                Assert.Equal(-0.3f * n, e.velocity.Y, 4);
                Assert.Equal(0f, e.velocity.X);
                Assert.Equal(32f, e.Position.X);
                float moved = y - e.Position.Y;
                Assert.Equal(Math.Ceiling(-vBefore - 1e-5f), moved, 3);   // ceil(|v|) pixels per frame
                y = e.Position.Y;
                Assert.False(e.yCollision);
                Assert.Equal(n >= 5, e.jumping);          // velocity < -1 first seen at the start of frame 5 (-1.2)
            }
            Assert.Equal(175f, e.Position.Y, 3);          // 0+1+1+1+2+2+2+3+3+3+3+4 = 25 px in 12 frames
            Assert.True(e.Position.Y < 200 - 12 * 0.3f * 11 / 2, "faster than exact integration would be");
        }

        // PHYS-11: landing on a flat destructible floor.
        [Fact]
        public void Landing_rests_the_feet_probe_on_the_top_solid_pixel_without_lateral_drift()
        {
            SyntheticLevel level = SyntheticLevel.Build(64, 64, Floor);
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(32, 40));
            int landedFrame = -1;
            int groundedFrames = 0;
            for (int n = 1; n <= 120; n++)
            {
                level.StepFrames(1);
                if (landedFrame < 0 && e.yCollision)
                    landedFrame = n;
                if (landedFrame > 0)
                {
                    Assert.Equal(30f, e.Position.Y);                       // feet probe at y 10 = top solid pixel
                    Assert.Equal(32f, e.Position.X);
                    Assert.Equal(0f, e.velocity.X);
                    Assert.False(e.jumping);
                    Assert.False(e.xCollision);
                    // resting alternates between the ground-detect frame (v.Y 0) and the d = 0 frame (v.Y -0.3)
                    Assert.True(e.velocity.Y == 0f || Math.Abs(e.velocity.Y + 0.3f) < 1e-5f, "v.Y = " + e.velocity.Y);
                    Assert.Equal(e.yCollision, e.velocity.Y == 0f);
                    if (e.yCollision) groundedFrames++;
                }
            }
            Assert.Equal(8, landedFrame);
            Assert.True(groundedFrames >= 56, "grounded on every other frame, got " + groundedFrames);
            Assert.True(level.IsSolid(32, (int)(e.Position.Y - 20)));
            Assert.False(level.IsSolid(32, (int)(e.Position.Y - 19)));
            Assert.Equal(new Vec2(32, 11), e.FeetPosition);
        }

        // PHYS-12: Jump sets velocity 6; apex after 20 frames; lands again with jumping false.
        [Fact]
        public void Jump_starts_at_6_reaches_the_apex_after_20_frames_and_lands_with_jumping_cleared()
        {
            SyntheticLevel level = SyntheticLevel.Build(64, 256, Floor);
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(32, 30));
            level.StepFrames(10);
            Assert.Equal(30f, e.Position.Y);
            Assert.False(e.jumping);

            e.Jump();
            Assert.Equal(6f, e.velocity.Y);
            Assert.True(e.jumping);

            float max = 30;
            int apexFrame = 0;
            int landedFrame = 0;
            float expectedRise = 0;
            for (int n = 1; n <= 80; n++)
            {
                float vBefore = e.velocity.Y;
                if (vBefore > 0)
                    expectedRise += (float)Math.Ceiling(vBefore - 1e-5f);
                level.StepFrames(1);
                if (e.Position.Y > max)
                {
                    max = e.Position.Y;
                    apexFrame = n;
                }
                if (n <= 19)
                    Assert.Equal(6f - 0.3f * n, e.velocity.Y, 4);
                if (landedFrame == 0 && n > 20 && e.yCollision && e.Position.Y == 30f)
                    landedFrame = n;
            }
            Assert.Equal(20, apexFrame);
            Assert.Equal(30 + expectedRise, max, 3);
            Assert.Equal(102f, max, 3);
            Assert.InRange(landedFrame, 40, 45);
            Assert.Equal(30f, e.Position.Y);
            Assert.False(e.jumping);
        }

        // PHYS-12 (EPSILON): at the apex the residual velocity below 0.1 is zeroed by the EPSILON rule.
        [Fact]
        public void Velocity_below_EPSILON_is_zeroed_at_the_apex()
        {
            SyntheticLevel level = SyntheticLevel.Build(64, 256, Floor);
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(32, 30));
            level.StepFrames(10);
            e.Jump();
            level.StepFrames(19);
            Assert.Equal(0.3f, e.velocity.Y, 4);
            level.StepFrames(1);                     // 0.3 - 0.3 = ~1e-7 -> |v| < 0.1 -> 0
            Assert.Equal(0f, e.velocity.Y);
            Assert.True(e.jumping);                  // still airborne: cleared only on a downward ground contact
            level.StepFrames(1);
            Assert.Equal(-0.3f, e.velocity.Y, 4);
        }

        // PHYS-19: spectators and anchored entities are not integrated.
        [Fact]
        public void Spectators_and_anchored_entities_do_not_move()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(64, 128, SyntheticLevel.Empty);
            HumanoidEntity s = level.AddHumanoid(PlayerClass.Spectator, new Vec2(32, 50));
            BasicEntity b = new BasicEntity();
            b.Size = new Vec2(4, 4);
            b.anchored = true;
            level.Add(b, new Vec2(40, 50));
            HumanoidEntity control = level.AddHumanoid(PlayerClass.Green, new Vec2(20, 50));

            Assert.False(s.enablePhysics);
            Assert.Null(s.stance);
            level.StepFrames(60);

            Assert.Equal(new Vec2(32, 50), s.Position);
            Assert.Equal(Vec2.Zero, s.velocity);
            Assert.Equal(new Vec2(40, 50), b.Position);
            // The anchored entity is processed: gravity is added to velocity.Y each frame, but the
            // Velocity getter reports FixedVelocity (zero) so the EPSILON rule zeroes the raw
            // velocity again at the end of the same frame.
            Assert.Equal(Vec2.Zero, b.velocity);
            Assert.Equal(Vec2.Zero, b.Velocity);
            // the control humanoid fell to the map's bottom border (y = -1 reads as solid) and rests with its feet probe on it
            Assert.Equal(19f, control.Position.Y);
            Assert.True(control.velocity.Y == 0f || Math.Abs(control.velocity.Y + 0.3f) < 1e-5f);
        }
    }
}
