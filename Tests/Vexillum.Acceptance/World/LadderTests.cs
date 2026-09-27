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
    /// Ladders (yellow collision pixels, bit 3): hanging and climbing through
    /// FixedVelocity in Level.DoPhysicsForEntity (428-457, 471-483).
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class LadderTests
    {
        private static SyntheticLevel LadderLevel()
        {
            // ladder column x 30..34, rows 20..80, no floor anywhere
            return SyntheticLevel.Build(64, 128, delegate(int x, int y)
            {
                return (x >= 30 && x <= 34 && y >= 20 && y <= 80) ? SyntheticLevel.Ladder : SyntheticLevel.Empty;
            });
        }

        // PHYS-17: a falling humanoid stops on ladder pixels with no ground beneath it.
        [Fact]
        public void Falling_onto_a_ladder_hangs_the_humanoid_without_ground()
        {
            SyntheticLevel level = LadderLevel();
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(32, 100));
            HumanoidEntity control = level.AddHumanoid(PlayerClass.Green, new Vec2(52, 100));
            Assert.Equal(0, e.ladderDirection);
            Assert.True(level.IsLadder(32, 80));
            Assert.False(level.IsLadder(32, 81));

            int hangFrame = 0;
            for (int n = 1; n <= 60; n++)
            {
                level.StepFrames(1);
                if (hangFrame == 0 && e.ladder)
                {
                    hangFrame = n;
                    Assert.True(e.yCollision);
                    Assert.Equal(0f, e.velocity.Y);
                    Assert.False(e.jumping);
                }
                if (hangFrame > 0)
                {
                    Assert.True(e.ladder);
                    Assert.Equal(new Vec2(32, 95), e.Position);      // probe y = Position.Y - 15 = 80, the top ladder row
                    Assert.True(e.velocity.Y == 0f || Math.Abs(e.velocity.Y + 0.3f) < 1e-5f);
                    Assert.False(e.jumping);
                }
            }
            Assert.Equal(6, hangFrame);
            Assert.False(level.IsSolid(32, (int)(e.Position.Y - 20)), "nothing solid under the feet");
            Assert.True(level.IsLadder(32, (int)(e.Position.Y - 15)));
            Assert.False(control.ladder);
            Assert.True(control.Position.Y < 40, "the control entity kept falling, y = " + control.Position.Y);
        }

        // PHYS-18: ladderDirection +-1 moves 3 px per frame through FixedVelocity; leaving the ladder resets it.
        [Fact]
        public void Climbing_moves_3_px_per_frame_and_leaving_the_ladder_restores_gravity()
        {
            SyntheticLevel level = LadderLevel();
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(32, 100));
            level.StepFrames(6);
            Assert.Equal(new Vec2(32, 95), e.Position);
            Assert.True(e.ladder);

            e.ladderDirection = -1;
            float y = 95;
            for (int n = 1; n <= 10; n++)
            {
                float rawBefore = e.velocity.Y;
                level.StepFrames(1);
                Assert.Equal(-3f, e.FixedVelocity.Y);
                Assert.Equal(new Vec2(0, -3), e.Velocity);
                Assert.Equal(y - 3, e.Position.Y);
                y = e.Position.Y;
                Assert.True(e.ladder);
                Assert.Equal(-1, e.ladderDirection);
                // gravity keeps accumulating in the raw velocity while FixedVelocity drives the motion
                Assert.Equal(rawBefore - 0.3f, e.velocity.Y, 4);
            }
            Assert.Equal(65f, e.Position.Y);

            e.ladderDirection = 1;
            int leftFrame = 0;
            for (int n = 1; n <= 30 && leftFrame == 0; n++)
            {
                level.StepFrames(1);
                if (e.ladder)
                {
                    Assert.Equal(3f, e.FixedVelocity.Y);
                    Assert.Equal(y + 3, e.Position.Y);
                    y = e.Position.Y;
                }
                else
                {
                    leftFrame = n;
                    Assert.Equal(101f, e.Position.Y);        // probe y - 15 = 86 > 80: the probe left the ladder rows
                    Assert.Equal(1, e.ladderDirection);      // reset happens on the next DoPhysics
                    Assert.Equal(3f, e.FixedVelocity.Y);
                }
            }
            Assert.Equal(12, leftFrame);
            float raw = e.velocity.Y;
            Assert.True(raw < -6f, "accumulated gravity " + raw);

            level.StepFrames(1);
            Assert.Equal(0, e.ladderDirection);
            Assert.Equal(0f, e.FixedVelocity.Y);
            // the accumulated raw velocity is now the real velocity: the entity drops back onto the ladder
            Assert.True(e.Position.Y < 101f, "fell, y = " + e.Position.Y);
            Assert.Equal(95f, e.Position.Y);
            Assert.True(e.ladder);
            Assert.Equal(0f, e.velocity.Y);
        }

        // PHYS-18 (gravity resumes): stepping off the top of a ladder sideways lets gravity act again.
        [Fact]
        public void Off_the_ladder_gravity_resumes_from_the_accumulated_velocity()
        {
            SyntheticLevel level = LadderLevel();
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(32, 100));
            level.StepFrames(6);
            e.ladderDirection = -1;
            level.StepFrames(5);                     // y = 80, raw velocity keeps accumulating
            Assert.Equal(80f, e.Position.Y);
            float raw = e.velocity.Y;
            // teleport sideways off the ladder column (as a client position packet would)
            e.Position = new Vec2(50, e.Position.Y);
            // frame A: e.ladder is still set from last frame, so FixedVelocity drives one more 3 px step;
            // the probe finds no ladder pixel at x 47..53 and e.ladder ends the frame false
            level.StepFrames(1);
            Assert.Equal(77f, e.Position.Y);
            Assert.False(e.ladder);
            Assert.Equal(-1, e.ladderDirection);
            Assert.Equal(raw - 0.3f, e.velocity.Y, 4);
            // frame B: ladderDirection and FixedVelocity are reset and the accumulated raw velocity moves the entity
            float v1 = e.velocity.Y;
            level.StepFrames(1);
            Assert.Equal(0, e.ladderDirection);
            Assert.Equal(Vec2.Zero, e.FixedVelocity);
            Assert.Equal(Math.Ceiling(-v1 - 1e-5f), 77 - e.Position.Y, 3);
            Assert.Equal(v1 - 0.3f, e.velocity.Y, 4);
            float y2 = e.Position.Y;
            float v2 = e.velocity.Y;
            level.StepFrames(1);
            Assert.Equal(v2 - 0.3f, e.velocity.Y, 4);
            Assert.Equal(Math.Ceiling(-v2 - 1e-5f), y2 - e.Position.Y, 3);
        }
    }
}
