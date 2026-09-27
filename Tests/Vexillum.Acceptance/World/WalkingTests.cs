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
    /// Horizontal motion on the ground: LivingEntity.SetMovement/Step and the
    /// x branch of Level.DoPhysicsForEntity (friction, stepping, walls).
    ///
    /// On flat ground a walking humanoid alternates between a frame whose
    /// raw velocity is (2, 0), which moves exactly 2 px, and a frame whose
    /// raw velocity is (2, -0.3) after the d = 0 rest frame added gravity:
    /// that frame runs ceil(2.022) = 3 sub-steps with k = 3/2.022, moving
    /// 2.9668 px before the ground is re-detected. Speed 2 is therefore the
    /// per-frame velocity, not the per-frame displacement.
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class WalkingTests
    {
        private const float LongStep = 2.9668f;   // 2 * 3 / sqrt(4 + 0.09)

        private static uint Floor(int x, int y)
        {
            return y <= 10 ? SyntheticLevel.Destructible(8) : SyntheticLevel.Empty;
        }

        private static HumanoidEntity Resting(SyntheticLevel level, Vec2 pos)
        {
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, pos);
            level.StepFrames(10);
            Assert.Equal(pos, e.Position);
            return e;
        }

        // PHYS-13: walking imposes xVelocity = +-Speed every frame; reversing retraces the path exactly.
        [Fact]
        public void Walking_imposes_Speed_each_frame_and_reversing_retraces_the_path()
        {
            SyntheticLevel level = SyntheticLevel.Build(128, 64, Floor);
            HumanoidEntity e = Resting(level, new Vec2(32, 30));
            Assert.Equal(2f, e.Speed);

            e.moving = true;
            e.direction = true;
            e.SetMovement();
            Assert.Equal(2f, e.xVelocity);

            float x = 32;
            for (int n = 1; n <= 10; n++)
            {
                level.StepFrames(1);
                float dx = e.Position.X - x;
                x = e.Position.X;
                Assert.True(Math.Abs(dx - 2f) < 1e-3f || Math.Abs(dx - LongStep) < 1e-3f, "frame " + n + " moved " + dx);
                Assert.Equal(30f, e.Position.Y);
                Assert.Equal(2f, e.xVelocity);
                // friction halves velocity.X to 1 on the grounded frames; Step re-imposes 2 before the move
                Assert.True(e.velocity.X == 2f || e.velocity.X == 1f, "velocity.X = " + e.velocity.X);
                Assert.Equal(e.velocity.Y == 0f, e.velocity.X == 1f);
            }
            Assert.Equal(56.834f, e.Position.X, 2);   // 5 x 2 + 5 x 2.9668

            e.direction = false;
            e.SetMovement();
            Assert.Equal(-2f, e.xVelocity);
            level.StepFrames(10);
            Assert.Equal(32f, e.Position.X, 3);
            Assert.Equal(30f, e.Position.Y);
        }

        // PHYS-14: releasing movement: friction halves velocity.X on grounded frames, sub-pixel speeds still move whole pixels.
        [Fact]
        public void Releasing_movement_halves_velocity_on_grounded_frames_until_EPSILON_stops_it()
        {
            SyntheticLevel level = SyntheticLevel.Build(128, 64, Floor);
            HumanoidEntity e = Resting(level, new Vec2(32, 30));
            e.moving = true;
            e.direction = true;
            e.SetMovement();
            level.StepFrames(10);
            Assert.Equal(56.834f, e.Position.X, 2);
            Assert.Equal(1f, e.velocity.X);
            Assert.Equal(0f, e.velocity.Y);

            e.moving = false;
            e.SetMovement();
            Assert.Equal(0f, e.xVelocity);

            float[] velocitiesAfter = new float[8];
            float[] positions = new float[8];
            for (int n = 0; n < 8; n++)
            {
                level.StepFrames(1);
                velocitiesAfter[n] = e.velocity.X;
                positions[n] = e.Position.X;
            }
            // frames 1,3,5,7 start with velocity.Y == 0: d = |v.X| < 1 moves one whole pixel, then gravity;
            // frames 2,4,6,8 re-detect the ground and halve velocity.X (friction only runs when velocity.Y == 0).
            Assert.Equal(new float[] { 1f, 0.5f, 0.5f, 0.25f, 0.25f, 0.125f, 0.125f, 0f }, velocitiesAfter);
            Assert.Equal(57.834f, positions[0], 2);
            Assert.Equal(60.750f, positions[2], 2);
            Assert.Equal(62.607f, positions[4], 2);
            Assert.Equal(64.247f, positions[6], 2);
            for (int n = 1; n < 8; n++)
                Assert.True(positions[n] > positions[n - 1], "still moving on frame " + (n + 1));
            float stopped = e.Position.X;
            level.StepFrames(4);
            Assert.Equal(stopped, e.Position.X);
            Assert.Equal(0f, e.velocity.X);
        }

        // PHYS-15: an obstacle rising 6 px above the ground is climbed, a 7 px rise blocks.
        [Theory]
        [InlineData(6, true)]
        [InlineData(7, false)]
        public void Step_up_climbs_rises_below_7_px_and_is_blocked_by_7(int rise, bool climbs)
        {
            SyntheticLevel level = SyntheticLevel.Build(96, 64, delegate(int x, int y)
            {
                if (y <= 10) return SyntheticLevel.Destructible(8);
                if (x >= 36 && y <= 10 + rise) return SyntheticLevel.Destructible(8);
                return SyntheticLevel.Empty;
            });
            HumanoidEntity e = Resting(level, new Vec2(32, 30));
            e.moving = true;
            e.direction = true;
            e.SetMovement();

            level.StepFrames(1);
            if (climbs)
            {
                // lifted on the frame the probe column (x = 36) meets the block: ny = yPoint + HalfSize.Y, x unchanged
                Assert.Equal(new Vec2(32, 10 + rise + 20), e.Position);
                Assert.False(e.xCollision);
                level.StepFrames(7);
                Assert.True(e.Position.X > 44, "keeps advancing on top of the block, x = " + e.Position.X);
                Assert.InRange(e.Position.Y, 36f, 37f);            // feet probe rests on row 16
                Assert.True(level.IsSolid((int)e.Position.X, (int)(e.Position.Y - 20)));
                Assert.False(e.xCollision);
            }
            else
            {
                Assert.Equal(32f, e.Position.X);
                Assert.True(e.xCollision);
                Assert.Equal(0f, e.velocity.X);
                level.StepFrames(7);
                Assert.Equal(new Vec2(32, 30), e.Position);
                Assert.True(e.xCollision);
                Assert.True(e.velocity.X == 0f || e.velocity.X == 1f, "reset to 0 on the blocked sub-step; " + e.velocity.X);
            }
        }

        // PHYS-16 A: a wall stops horizontal motion; the entity keeps resting on the floor.
        [Fact]
        public void Wall_stops_horizontal_motion_without_affecting_vertical_rest()
        {
            SyntheticLevel level = SyntheticLevel.Build(96, 64, delegate(int x, int y)
            {
                return (y <= 10 || x >= 40) ? SyntheticLevel.Destructible(8) : SyntheticLevel.Empty;
            });
            HumanoidEntity e = Resting(level, new Vec2(32, 30));
            e.moving = true;
            e.direction = true;
            e.SetMovement();
            float maxX = 32;
            int blockedFrames = 0;
            for (int n = 1; n <= 20; n++)
            {
                level.StepFrames(1);
                Assert.True(e.Position.X >= maxX, "never moves back");
                maxX = Math.Max(maxX, e.Position.X);
                Assert.Equal(30f, e.Position.Y);
                if (e.xCollision)
                {
                    blockedFrames++;
                    Assert.Equal(e.Position.X, maxX);
                }
            }
            // probe (int)(x + 4) == 40 is the wall face: x converges to just below 37
            Assert.InRange(maxX, 36f, 37f);
            Assert.Equal(40, (int)(e.Position.X + e.HalfSize.X));
            Assert.True(blockedFrames >= 17, "xCollision on every frame after reaching the wall, got " + blockedFrames);
            Assert.True(e.xCollision);
            level.StepFrames(1);
            Assert.True(e.xCollision);
            Assert.True(e.yCollision || e.velocity.Y == -0.3f, "still resting");
        }

        // PHYS-16 B: the map border is solid for the probe (out of bounds reads as terrain).
        [Fact]
        public void Map_border_stops_a_humanoid_walking_left()
        {
            SyntheticLevel level = SyntheticLevel.Build(96, 64, Floor);
            HumanoidEntity e = Resting(level, new Vec2(5, 30));
            e.moving = true;
            e.direction = false;
            e.SetMovement();
            level.StepFrames(20);
            Assert.Equal(4f, e.Position.X);          // probe (int)(x - 5) == -1 is out of bounds
            Assert.Equal(30f, e.Position.Y);
            Assert.True(e.xCollision);
        }
    }
}
