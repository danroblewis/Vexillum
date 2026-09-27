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
    /// Level.Explode / DrawCircle (Level.cs 153-285): determinism in the
    /// seed, indestructible pixels, the collision nibble as crater hardness,
    /// the plain disc for small radii and the entity knock-back.
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class ExplosionTests
    {
        private const string BasesSha = "059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3";

        // PHYS-20: the same seed gives the same crater, independent of level.random; different seeds differ.
        [Fact]
        public void Explosion_is_deterministic_in_the_seed_and_independent_of_the_level_random()
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                HeadlessLevel l1 = HeadlessLevel.Load(rt, "bases");
                HeadlessLevel l2 = HeadlessLevel.Load(rt, "bases");
                HeadlessLevel l3 = HeadlessLevel.Load(rt, "bases");
                Assert.Equal(15, l1.CollisionNibble(800, 300));
                int before = l1.Snapshot().SolidCount();
                for (int i = 0; i < 1000; i++)
                    l1.random.NextDouble();

                l1.Explode(800, 300, 26, 12345, false, null, null);
                l2.Explode(800, 300, 26, 12345, false, null, null);
                l3.Explode(800, 300, 26, 54321, false, null, null);

                string h1 = l1.Snapshot().Sha256Hex();
                Assert.Equal(h1, l2.Snapshot().Sha256Hex());
                Assert.NotEqual(BasesSha, h1);
                Assert.NotEqual(h1, l3.Snapshot().Sha256Hex());
                int destroyed = before - l1.Snapshot().SolidCount();
                Assert.True(destroyed > 0, "pixels destroyed");
                Assert.True(destroyed < Math.PI * 39 * 39, "within the damage disc: " + destroyed);
                Assert.Equal(destroyed, l1.Snapshot().CountDifferences(new TerrainSnapshot(HeadlessLevel.Load(rt, "bases").GetTerrainState(), l1.Width, l1.Height)));

                // Explode with a seed never touches level.random: l2.random continues like a fresh Random with the same state
                // (Random has no observable seed, so compare two levels that were built with identical random state).
                Random probe = new Random(77);
                l2.random = new Random(77);
                l2.Explode(800, 600, 26, 999, false, null, null);
                for (int i = 0; i < 20; i++)
                    Assert.Equal(probe.Next(), l2.random.Next());
            }
        }

        // PHYS-21: Solid (nibble 1) pixels survive and shield what lies behind them. The traces sample every
        // 2 px, so a wall must be at least 3 px thick to be met by every ray; the 8 px trace circle drawn at
        // the last sample in front of the wall still reaches up to 8 px behind it (Destroy refuses only the
        // Solid pixels themselves), and once a ray meets Solid it destroys nothing further.
        [Fact]
        public void Indestructible_pixels_survive_and_shield_the_terrain_behind_them()
        {
            SyntheticLevel level = SyntheticLevel.Build(128, 128, delegate(int x, int y)
            {
                return (x >= 64 && x <= 66) ? SyntheticLevel.Solid : SyntheticLevel.Destructible(15);
            });
            Assert.Equal(1, level.CollisionNibble(64, 64));

            level.Explode(58, 64, 26, 7, false, null, null);

            int destroyedLeft = 0, destroyedBehind = 0;
            for (int x = 0; x < 128; x++)
                for (int y = 0; y < 128; y++)
                {
                    if (x >= 64 && x <= 66)
                        Assert.True(level.IsSolid(x, y), "wall pixel destroyed at " + x + "," + y);
                    else if (x >= 72)
                        Assert.True(level.IsSolid(x, y), "pixel beyond the wall's shadow destroyed at " + x + "," + y);
                    else if (x > 66 && !level.IsSolid(x, y))
                        destroyedBehind++;
                    else if (x < 64 && !level.IsSolid(x, y))
                        destroyedLeft++;
                }
            Assert.True(destroyedLeft > 0, "some pixels left of the wall were destroyed");
            Assert.True(destroyedBehind < destroyedLeft / 4, "the strip behind the wall is only grazed: " + destroyedBehind + " vs " + destroyedLeft);
            for (int y = 0; y < 128; y++)
                Assert.Equal(1, level.CollisionNibble(64, y));
        }

        // PHYS-21 (thin wall): a 1 px Solid column can be jumped over by the 2 px trace step, so it does not shield.
        [Fact]
        public void One_pixel_wall_survives_but_does_not_shield()
        {
            SyntheticLevel level = SyntheticLevel.Build(128, 128, delegate(int x, int y)
            {
                return x == 64 ? SyntheticLevel.Solid : SyntheticLevel.Destructible(15);
            });
            level.Explode(58, 64, 26, 7, false, null, null);
            int destroyedFarBehind = 0;
            for (int x = 72; x < 128; x++)
                for (int y = 0; y < 128; y++)
                    if (!level.IsSolid(x, y))
                        destroyedFarBehind++;
            for (int y = 0; y < 128; y++)
                Assert.True(level.IsSolid(64, y), "the Solid column itself survives");
            Assert.True(destroyedFarBehind > 0, "rays that skipped the column kept cratering behind it");
        }

        // PHYS-22: the nibble is the hardness: 15 craters more than 2 for the same seed; nothing beyond the damage radius.
        [Fact]
        public void Higher_collision_nibble_yields_a_larger_crater_within_the_damage_radius()
        {
            SyntheticLevel a = SyntheticLevel.Uniform(128, 128, SyntheticLevel.Destructible(15));
            SyntheticLevel b = SyntheticLevel.Uniform(128, 128, SyntheticLevel.Destructible(2));
            a.Explode(64, 64, 26, 99, false, null, null);
            b.Explode(64, 64, 26, 99, false, null, null);
            int da = 128 * 128 - a.SolidCount();
            int db = 128 * 128 - b.SolidCount();
            Assert.True(da > db, "nibble 15 destroyed " + da + ", nibble 2 destroyed " + db);
            Assert.True(db > 0);
            foreach (SyntheticLevel l in new[] { a, b })
                for (int x = 0; x < 128; x++)
                    for (int y = 0; y < 128; y++)
                        if (!l.IsSolid(x, y))
                            Assert.True((x - 64) * (x - 64) + (y - 64) * (y - 64) < 39 * 39, "destroyed pixel outside the damage radius at " + x + "," + y);
            // craters are removed only up to t < radius (26) plus the 8 px trace circle: nothing farther than 34 from the centre
            for (int x = 0; x < 128; x++)
                for (int y = 0; y < 128; y++)
                    if (!a.IsSolid(x, y))
                        Assert.True((x - 64) * (x - 64) + (y - 64) * (y - 64) < 34 * 34, "destroyed pixel beyond t<radius+8 at " + x + "," + y);
        }

        // PHYS-23: radius below 4 cuts a plain disc without traces: radius 2 removes exactly 9 pixels.
        [Fact]
        public void Radius_below_4_cuts_a_plain_disc_of_9_pixels()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(32, 32, SyntheticLevel.Destructible(12));
            level.Explode(16, 16, 2, 0, true, null, null);
            List<string> destroyed = new List<string>();
            for (int x = 0; x < 32; x++)
                for (int y = 0; y < 32; y++)
                    if (!level.IsSolid(x, y))
                        destroyed.Add(x + "," + y);
            destroyed.Sort();
            List<string> expected = new List<string>();
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    expected.Add((16 + dx) + "," + (16 + dy));
            expected.Sort();
            Assert.Equal(expected, destroyed);
            Assert.True(level.IsSolid(14, 16));
            Assert.True(level.IsSolid(18, 16));
        }

        // PHYS-23 (nibble 1 disc): the disc respects Solid pixels too.
        [Fact]
        public void Small_disc_does_not_remove_Solid_pixels()
        {
            SyntheticLevel level = SyntheticLevel.Build(32, 32, delegate(int x, int y) { return x == 16 ? SyntheticLevel.Solid : SyntheticLevel.Destructible(12); });
            level.Explode(16, 16, 3, 0, true, null, null);
            for (int y = 0; y < 32; y++)
                Assert.True(level.IsSolid(16, y));
            Assert.False(level.IsSolid(15, 16));
            Assert.False(level.IsSolid(17, 16));
        }

        // PHYS-24: lethal explosions knock entities once, proportionally to distance; nonlethal ones only crater.
        [Fact]
        public void Lethal_explosion_knocks_an_entity_once_and_nonlethal_leaves_it_alone()
        {
            string lethalHash = null;
            foreach (bool nonlethal in new[] { false, true })
            {
                SyntheticLevel level = SyntheticLevel.Build(128, 128, delegate(int x, int y) { return y <= 10 ? SyntheticLevel.Destructible(12) : SyntheticLevel.Empty; });
                HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(74, 30));
                level.StepFrames(10);
                Assert.Equal(new Vec2(74, 30), e.Position);
                Assert.Equal(Vec2.Zero, e.velocity);

                level.Explode(64, 30, 26, 3, nonlethal, null, null);

                // the terrain result does not depend on the nonlethal flag
                if (lethalHash == null)
                    lethalHash = level.Snapshot().Sha256Hex();
                else
                    Assert.Equal(lethalHash, level.Snapshot().Sha256Hex());
                if (nonlethal)
                {
                    Assert.Equal(Vec2.Zero, e.velocity);
                    Assert.False(e.jumping);
                    // a nonlethal blast on the floor still craters it
                    int solidBefore = level.SolidCount();
                    level.Explode(64, 12, 26, 3, true, null, null);
                    Assert.True(level.SolidCount() < solidBefore, "the floor was cratered");
                    Assert.False(level.IsSolid(64, 10));
                    Assert.Equal(Vec2.Zero, e.velocity);
                }
                else
                {
                    // unit (1,0) * (1 - 10/39) * 16 = 11.897, applied exactly once although many circle pixels touch the outline
                    Assert.Equal(16f * (1f - 10f / 39f), e.velocity.X, 2);
                    Assert.Equal(0f, e.velocity.Y);
                    Assert.True(e.jumping);
                    level.StepFrames(1);
                    Assert.Equal(86f, e.Position.X);       // ceil(11.897) = 12 px in one frame
                    Assert.Equal(30f, e.Position.Y);
                }
            }
        }

        // PHYS-24 (direction): an entity above the blast is pushed upward and one to the left leftward.
        [Fact]
        public void Knock_back_points_away_from_the_blast_centre()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(128, 128, SyntheticLevel.Empty);
            HumanoidEntity above = level.AddHumanoid(PlayerClass.Green, new Vec2(64, 90));
            HumanoidEntity left = level.AddHumanoid(PlayerClass.Blue, new Vec2(44, 64));
            level.Explode(64, 64, 26, 11, false, null, null);
            Assert.True(above.velocity.Y > 0 && Math.Abs(above.velocity.X) < 1e-3f, "above: " + above.velocity);
            Assert.Equal(16f * (1f - 26f / 39f), above.velocity.Y, 2);
            Assert.True(left.velocity.X < 0 && Math.Abs(left.velocity.Y) < 1e-3f, "left: " + left.velocity);
            Assert.Equal(-16f * (1f - 20f / 39f), left.velocity.X, 2);
        }

        // Scope: an explosion exactly at an entity's centre normalises a zero vector (Vector2.Normalize -> NaN).
        [Fact]
        public void Explosion_at_the_entity_centre_leaves_a_NaN_velocity()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(128, 128, SyntheticLevel.Empty);
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(64, 64));
            level.Explode(64, 64, 26, 5, false, null, null);
            Assert.True(float.IsNaN(e.velocity.X) && float.IsNaN(e.velocity.Y), "velocity " + e.velocity);
            Assert.True(e.jumping);
            level.StepFrames(5);
            Assert.Equal(new Vec2(64, 64), e.Position);     // d is NaN: the position loop never runs
        }

        [Fact(Skip = "Known original bug: DrawCircle normalises the zero vector when an explosion is centred on an entity, leaving its velocity NaN, docs/PORTING.md")]
        public void Explosion_at_the_entity_centre_gives_a_finite_velocity()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(128, 128, SyntheticLevel.Empty);
            HumanoidEntity e = level.AddHumanoid(PlayerClass.Green, new Vec2(64, 64));
            level.Explode(64, 64, 26, 5, false, null, null);
            Assert.False(float.IsNaN(e.velocity.X) || float.IsNaN(e.velocity.Y));
        }
    }
}
