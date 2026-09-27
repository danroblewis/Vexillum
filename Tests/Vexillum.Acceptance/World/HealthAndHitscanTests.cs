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
    /// HumanoidEntity.Health clamping and death notification, and
    /// Level.AddHitscan (stops at terrain or at an entity's box, TestPoint).
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class HealthAndHitscanTests
    {
        // PHYS-30: Health is clamped to [0, MaxHealth]; every assignment that clamps to 0 calls OnEntityDeath.
        [Fact]
        public void Health_is_clamped_and_reaching_zero_notifies_the_level_once_per_assignment()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(64, 64, SyntheticLevel.Empty);
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(32, 32));
            Assert.Equal(100f, e.MaxHealth);
            Assert.Equal(100f, e.Health);
            Assert.Empty(level.Deaths);

            e.Health = 150;
            Assert.Equal(100f, e.Health);
            Assert.Empty(level.Deaths);

            e.Health = 60;
            Assert.Equal(60f, e.Health);
            Assert.Empty(level.Deaths);

            e.Health = -5;
            Assert.Equal(0f, e.Health);
            Assert.Single(level.Deaths);
            Assert.Same(e, level.Deaths[0]);

            e.Health = 0;
            Assert.Equal(0f, e.Health);
            Assert.Equal(2, level.Deaths.Count);

            e.Health = 0.5f;
            Assert.Equal(0.5f, e.Health);
            Assert.Equal(2, level.Deaths.Count);
        }

        [Fact(Skip = "Known original bug: HumanoidEntity.Health setter calls Level.OnEntityDeath while Level can be null (only nonzero values survive before AddEntity), docs/PORTING.md")]
        public void Setting_health_to_zero_before_the_entity_is_added_does_not_throw()
        {
            SyntheticLevel.Prepare();
            HumanoidEntity e = HumanoidTypes.CreateHumanoid(PlayerClass.Green);
            Assert.Null(e.Level);
            e.Health = 50;              // fine: nonzero
            e.Health = 0;               // throws NullReferenceException today
            Assert.Equal(0f, e.Health);
        }

        // Scope: hitscan stops at the first solid terrain pixel along the ray (angle in radians, y flipped).
        [Fact]
        public void Hitscan_stops_at_terrain()
        {
            SyntheticLevel level = SyntheticLevel.Build(128, 64, delegate(int x, int y) { return x >= 100 ? SyntheticLevel.Destructible(8) : SyntheticLevel.Empty; });
            level.AddHitscan(new Vec2(20, 32), 0f, null);
            Assert.Single(level.HitscanHits);
            SyntheticLevel.HitscanRecord hit = level.HitscanHits[0];
            Assert.Equal(new Vec2(20, 32), hit.Start);
            Assert.Equal(new Vec2(1, 0), hit.Unit);
            Assert.Equal(100f, hit.Pos.X);
            Assert.Equal(32f, hit.Pos.Y);
            Assert.True(level.IsSolid((int)hit.Pos.X, (int)hit.Pos.Y));
            Assert.False(level.IsSolid((int)hit.Pos.X - 1, (int)hit.Pos.Y));

            // angle pi/2 points down (unit (cos, -sin)); the bottom border is solid
            level.AddHitscan(new Vec2(20, 32), (float)Math.PI / 2, null);
            Assert.Equal(2, level.HitscanHits.Count);
            Assert.Equal(-1f, level.HitscanHits[1].Pos.Y);
            Assert.Equal(20f, level.HitscanHits[1].Pos.X, 3);
        }

        // Scope: Entity.TestPoint uses the corners computed in the Size setter (around the position at that
        // time, i.e. the origin for a fresh humanoid); moving the entity does not move its box.
        [Fact]
        public void TestPoint_box_is_fixed_where_the_entity_was_when_its_size_was_set()
        {
            SyntheticLevel level = SyntheticLevel.Build(200, 64, delegate(int x, int y) { return x >= 180 ? SyntheticLevel.Destructible(8) : SyntheticLevel.Empty; });
            HumanoidEntity target = level.AddHumanoid(PlayerClass.Blue, new Vec2(100, 32));
            Assert.Equal(new Vec2(-4, -20), target.tCorner);
            Assert.Equal(new Vec2(4, 20), target.bCorner);
            Assert.True(target.TestPoint(0, 0));
            Assert.False(target.TestPoint(100, 32));
            // so a hitscan through the entity's real position reaches the wall behind it
            level.AddHitscan(new Vec2(20, 32), 0f, null);
            Assert.Single(level.HitscanHits);
            Assert.Equal(180f, level.HitscanHits[0].Pos.X);
            // while a ray into the origin box (from the right, angle pi) is stopped by the entity (strict interior: x < 4)
            level.AddHitscan(new Vec2(10, 0), (float)Math.PI, null);
            Assert.Equal(2, level.HitscanHits.Count);
            Assert.Equal(3f, level.HitscanHits[1].Pos.X);
            // setting Size again re-anchors the box at the current position
            target.Size = target.Size;
            Assert.True(target.TestPoint(100, 32));
            Assert.False(target.TestPoint(96, 32));
            Assert.False(target.TestPoint(104, 32));
        }

        [Fact(Skip = "Known original bug: Entity.tCorner/bCorner are only computed in the Size setter, so Entity.TestPoint (Level.AddHitscan) tests a box around the origin instead of the entity's position, docs/PORTING.md")]
        public void Hitscan_stops_at_an_entity_box_and_skips_the_ignored_entity()
        {
            SyntheticLevel level = SyntheticLevel.Build(200, 64, delegate(int x, int y) { return x >= 180 ? SyntheticLevel.Destructible(8) : SyntheticLevel.Empty; });
            HumanoidEntity shooter = level.AddHumanoid(PlayerClass.Green, new Vec2(20, 32));
            HumanoidEntity target = level.AddHumanoid(PlayerClass.Blue, new Vec2(100, 32));
            Assert.True(target.TestPoint(100, 32));

            level.AddHitscan(new Vec2(20, 32), 0f, shooter);
            Assert.Single(level.HitscanHits);
            Assert.Equal(97f, level.HitscanHits[0].Pos.X);            // first x strictly inside the target's box

            // ignoring the target lets the ray continue to the wall
            level.AddHitscan(new Vec2(20, 32), 0f, target);
            Assert.Equal(2, level.HitscanHits.Count);
            Assert.Equal(180f, level.HitscanHits[1].Pos.X);
        }
    }
}
