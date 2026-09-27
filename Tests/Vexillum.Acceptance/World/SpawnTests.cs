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
    /// Spawn regions: Region.RandomPosition (bitmap Y-down to world Y-up with
    /// the +16 offset) and Level.GetSpawnPosition with its fallbacks.
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class SpawnTests
    {
        // PHYS-34 (a): headless region sampling stays inside the region and both extremes are observed.
        [Fact]
        public void RandomPosition_is_uniform_inside_the_region_with_the_Y_flip_and_16_px_offset()
        {
            Region r = new Region("spawn_Green", 459, 365, 593, 414);
            Random random = new Random(1234);
            Vec2 size = new Vec2(8, 40);
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            for (int i = 0; i < 500; i++)
            {
                Vec2 p = r.RandomPosition(random, size, 1024);
                Assert.Equal(p.X, (float)(int)p.X);        // integer positions
                Assert.Equal(p.Y, (float)(int)p.Y);
                Assert.InRange(p.X, 463, 588);              // 459 + Next(126) + 4
                Assert.InRange(p.Y, 647, 655);              // 1024 - (365 + Next(9) + 20) + 16
                minX = Math.Min(minX, (int)p.X); maxX = Math.Max(maxX, (int)p.X);
                minY = Math.Min(minY, (int)p.Y); maxY = Math.Max(maxY, (int)p.Y);
            }
            Assert.Equal(463, minX);
            Assert.Equal(588, maxX);
            Assert.Equal(647, minY);
            Assert.Equal(655, maxY);
        }

        // PHYS-34 (b): the shipped maps' spawn regions through GetSpawnPosition (region names matched case-insensitively).
        [Theory]
        [InlineData("bases", PlayerClass.Green, 463, 588, 647, 655)]
        [InlineData("bases", PlayerClass.Blue, 3316, 3441, 647, 655)]
        [InlineData("complex", PlayerClass.Green, 104, 364, 100, 159)]
        [InlineData("complex", PlayerClass.Blue, 2371, 2630, 100, 159)]
        public void GetSpawnPosition_samples_the_class_region_of_the_shipped_map(string map, PlayerClass cl, int x1, int x2, int y1, int y2)
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                HeadlessLevel level = HeadlessLevel.Load(rt, map);
                Assert.Single(level.Spawns[cl]);
                Assert.Equal("spawn_" + cl, level.Spawns[cl][0].name);
                HashSet<int> xs = new HashSet<int>();
                HashSet<int> ys = new HashSet<int>();
                for (int i = 0; i < 500; i++)
                {
                    Vec2 p = level.GetSpawnPosition(cl, new Vec2(8, 40));
                    Assert.InRange(p.X, x1, x2);
                    Assert.InRange(p.Y, y1, y2);
                    xs.Add((int)p.X);
                    ys.Add((int)p.Y);
                }
                Assert.True(xs.Count > 50, "x varies");
                Assert.True(ys.Count >= 5, "y varies");
                // a humanoid spawned there stands in air or on ground, never inside solid terrain
                HumanoidEntity e = level.AddHumanoidAtSpawn(cl);
                Assert.False(level.IsSolid((int)e.Position.X, (int)e.Position.Y), "spawned inside terrain at " + e.Position);
                Assert.Equal(cl, e.type);
            }
        }

        // PHYS-35 (a): no region for the class gives the map centre.
        [Theory]
        [InlineData("bases", 1957, 512)]
        [InlineData("complex", 1368, 409)]
        public void Without_a_region_GetSpawnPosition_returns_the_map_centre(string map, int cx, int cy)
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                SyntheticLevel level = SyntheticLevel.FromShippedMap(rt.Root, map, false);   // base Level never files regions
                Assert.Empty(level.Spawns);
                Assert.Equal(new Vec2(cx, cy), level.GetSpawnPosition(PlayerClass.Green, new Vec2(8, 40)));
                Assert.Equal(new Vec2(cx, cy), level.GetSpawnPosition(PlayerClass.Spectator, Vec2.Zero));
                HeadlessLevel filed = HeadlessLevel.Load(rt, map);
                Assert.Equal(new Vec2(cx, cy), filed.GetSpawnPosition(PlayerClass.Spectator, Vec2.Zero));
                Assert.NotEqual(new Vec2(cx, cy), filed.GetSpawnPosition(PlayerClass.Green, new Vec2(8, 40)));
            }
        }

        // PHYS-35 (b): a region narrower than the entity throws from Random.Next(negative); the flag regions
        // ("flag_Green 951 387 0 0" has a negative width) are never sampled: ServerLevel.PlaceFlag uses x1/y1.
        [Fact]
        public void Region_narrower_than_the_entity_throws()
        {
            Region narrow = new Region("spawn_Green", 0, 0, 4, 50);
            Assert.Throws<ArgumentOutOfRangeException>(delegate() { narrow.RandomPosition(new Random(), new Vec2(8, 40), 100); });

            Region exact = new Region("spawn_Green", 0, 0, 9, 41);      // width 9 - 8 = 1, height 41 - 40 = 1: Next(1) is always 0
            Assert.Equal(new Vec2(4, 100 - 20 + 16), exact.RandomPosition(new Random(), new Vec2(8, 40), 100));

            Region flag = new Region("flag_Green", 951, 387, 0, 0);
            Assert.Throws<ArgumentOutOfRangeException>(delegate() { flag.RandomPosition(new Random(), Vec2.Zero, 1024); });
            Assert.Equal(951, flag.x1);
            Assert.Equal(387, flag.y1);
        }

        // Scope: the flag regions of the shipped maps are filed per class with their bitmap coordinates.
        [Theory]
        [InlineData("bases", 951, 387, 2933, 387)]
        [InlineData("complex", 192, 170, 2522, 170)]
        public void Flag_regions_are_filed_per_class(string map, int gx, int gy, int bx, int by)
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                HeadlessLevel level = HeadlessLevel.Load(rt, map);
                Assert.Equal(2, level.Flags.Count);
                Assert.Equal(gx, level.Flags[PlayerClass.Green].x1);
                Assert.Equal(gy, level.Flags[PlayerClass.Green].y1);
                Assert.Equal(bx, level.Flags[PlayerClass.Blue].x1);
                Assert.Equal(by, level.Flags[PlayerClass.Blue].y1);
                // the flag stands in air at the server's placement point (x1, height - y1 - 14)
                Assert.False(level.IsSolid(gx, level.Height - gy - 29 / 2));
                Assert.False(level.IsSolid(bx, level.Height - by - 29 / 2));
            }
        }
    }
}
