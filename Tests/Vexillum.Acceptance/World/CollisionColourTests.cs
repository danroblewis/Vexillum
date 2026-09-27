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
    /// The collision bitmap colour codes (Level.cs 90-123) and their bit
    /// layout in TerrainArray, on hand-made bitmaps and on the shipped maps.
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class CollisionColourTests
    {
        // PHYS-01: the eight colour codes, one per column of the bottom row (world y = 0 is bitmap row 7).
        [Fact]
        public void Colour_codes_set_terrain_collision_ladder_and_transparent_bits_with_the_Y_flip()
        {
            uint[] codes = { 0xFFFF00FF, 0x00000000, 0xFFFFFFFF, 0xFF0000FF, 0xFFFFFF00, 0xFFFFFF80, 0xFFFF0000, 0xFF808000 };
            SyntheticLevel.Prepare();
            System.Drawing.Bitmap main = SyntheticLevel.Filled(8, 8, SyntheticLevel.MainFill);
            System.Drawing.Bitmap collision = SyntheticLevel.CollisionBitmap(8, 8, delegate(int x, int y)
            {
                return y == 0 ? codes[x] : SyntheticLevel.Empty;
            });
            SyntheticLevel level = new SyntheticLevel("t", "t", main, SyntheticLevel.Filled(8, 8, SyntheticLevel.MainFill), collision, new List<Region>());

            bool[] terrain = { false, true, true, false, false, false, true, true };
            byte[] nibble = { 0, 1, 1, 0, 0, 0, 15, 8 };
            bool[] ladder = { false, false, false, false, true, true, false, false };
            for (int x = 0; x < 8; x++)
            {
                Assert.True(terrain[x] == level.IsSolid(x, 0), "GetTerrain(" + x + ",0) for code " + codes[x].ToString("X8"));
                Assert.True(nibble[x] == level.CollisionNibble(x, 0), "GetCollisionData(" + x + ",0) = " + level.CollisionNibble(x, 0) + " for code " + codes[x].ToString("X8"));
                Assert.True(ladder[x] == level.IsLadder(x, 0), "GetLadder(" + x + ",0) for code " + codes[x].ToString("X8"));
            }
            Assert.True(level.Terrain.GetTransparent(7, 0), "green == 128 marks transparent-when-destroyed");
            Assert.False(level.Terrain.GetTransparent(6, 0));

            // Blue (x3) and the yellow+clear code (x5) clear the main pixel of bitmap row 7; the others leave it.
            Assert.Equal(0, main.GetPixel(3, 7).A);
            Assert.Equal(0, main.GetPixel(5, 7).A);
            Assert.Equal(255, main.GetPixel(0, 7).A);
            Assert.Equal(255, main.GetPixel(4, 7).A);

            // Nothing on world rows 1..7 (all magenta).
            for (int y = 1; y < 8; y++)
                for (int x = 0; x < 8; x++)
                {
                    Assert.False(level.IsSolid(x, y), "row " + y + " column " + x + " should be empty");
                    Assert.Equal(0, level.CollisionNibble(x, y));
                    Assert.False(level.IsLadder(x, y));
                }
            Assert.Equal(new Vec2(8, 8), level.Size);
            Assert.Equal(8, level.Width);
            Assert.Equal(8, level.Height);
        }

        // PHYS-01 (size rule): Level.Size is taken from main; a larger collision bitmap is read only inside that area.
        [Fact]
        public void Level_size_comes_from_the_main_bitmap()
        {
            SyntheticLevel.Prepare();
            System.Drawing.Bitmap main = SyntheticLevel.Filled(8, 6, SyntheticLevel.MainFill);
            System.Drawing.Bitmap collision = SyntheticLevel.Filled(8, 6, SyntheticLevel.Solid);
            SyntheticLevel level = new SyntheticLevel("t", "t", main, SyntheticLevel.Filled(8, 6, SyntheticLevel.MainFill), collision, new List<Region>());
            Assert.Equal(new Vec2(8, 6), level.Size);
            Assert.Equal(6, level.GetTerrainState().Length);   // 48 bits
            Assert.True(level.IsSolid(7, 5));
        }

        // PHYS-02: the default branch clamps red/16 into 2..15, green == 128 marks transparent, partial alpha is not Solid.
        [Fact]
        public void Default_branch_clamps_red_to_nibble_and_partial_alpha_is_destructible()
        {
            uint[] codes = { 0xFF000001, 0xFF2F0000, 0xFF300000, 0xFFF00000, 0xFFFF0000, 0x80FFFFFF, 0xFF008000, 0xFFFFFFFF };
            SyntheticLevel.Prepare();
            SyntheticLevel level = SyntheticLevel.Build(8, 4, delegate(int x, int y) { return y == 0 ? codes[x] : SyntheticLevel.Empty; });

            byte[] expected = { 2, 2, 3, 15, 15, 15, 2, 1 };
            for (int x = 0; x < 7; x++)
            {
                Assert.True(level.IsSolid(x, 0), "terrain bit for code " + codes[x].ToString("X8"));
                Assert.True(expected[x] == level.CollisionNibble(x, 0), "nibble for code " + codes[x].ToString("X8") + " = " + level.CollisionNibble(x, 0));
            }
            for (int x = 0; x < 8; x++)
                Assert.True((x == 6) == level.Terrain.GetTransparent(x, 0), "transparent bit for column " + x);

            // Half-alpha white is destructible (nibble 15), opaque white is Solid and refuses Destroy.
            Assert.True(level.Destroy(5, 0));
            Assert.False(level.IsSolid(5, 0));
            Assert.False(level.Destroy(7, 0));
            Assert.True(level.IsSolid(7, 0));
        }

        // PHYS-03: the shipped maps reproduce the independent Python oracle (terrain_reference).
        [Theory]
        [InlineData("bases", "Bases", 3914, 1024, "059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3", 500992, 1694982, 688145, 1006837, 2312954, 71146, 48876)]
        [InlineData("complex", "Complex", 2736, 818, "d5d14f4c61b455f074f82222ad6da7a5a491bf4ddcd53bde8056c3f2974695b6", 279756, 354020, 185661, 168359, 1884028, 40154, 0)]
        public void Shipped_maps_reproduce_the_oracle_hash_and_per_class_pixel_counts(string map, string longName, int w, int h, string sha,
            int bytes, int solid, int nibble1, int nibble2plus, int nibble0, int ladders, int transparent)
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                LevelLoader.LevelData d = HeadlessLevel.LoadData(rt.Root, map);
                Assert.Equal(longName, d.longName);
                HeadlessLevel level = HeadlessLevel.Load(rt, map);
                Assert.Equal(w, level.Width);
                Assert.Equal(h, level.Height);
                byte[] state = level.GetTerrainState();
                Assert.Equal(bytes, state.Length);
                Assert.Equal(sha, new TerrainSnapshot(state, w, h).Sha256Hex());

                int cSolid = 0, c1 = 0, c2 = 0, c0 = 0, cLadder = 0, cTransparent = 0;
                for (int x = 0; x < w; x++)
                    for (int y = 0; y < h; y++)
                    {
                        if (level.IsSolid(x, y)) cSolid++;
                        byte n = level.CollisionNibble(x, y);
                        if (n == 0) c0++; else if (n == 1) c1++; else c2++;
                        if (level.IsLadder(x, y)) cLadder++;
                        if (level.IsTransparentWhenDestroyed(x, y)) cTransparent++;
                    }
                Assert.Equal(solid, cSolid);
                Assert.Equal(nibble1, c1);
                Assert.Equal(nibble2plus, c2);
                Assert.Equal(nibble0, c0);
                Assert.Equal(ladders, cLadder);
                Assert.Equal(transparent, cTransparent);
                Assert.Equal(w * h, c0 + c1 + c2);
                // Every solid pixel carries a nonzero nibble and vice versa (blue/magenta never produce t=1,c=0).
                Assert.Equal(solid, c1 + c2);
            }
        }

        // Scope: LevelLoader.LoadData yields the eight files (seven images + data.txt) and the four regions.
        [Theory]
        [InlineData("bases", "flag_Green", 951, 387, "spawn_Green", 459, 365, "spawn_Blue", 3312, 365)]
        [InlineData("complex", "flag_Green", 192, 170, "spawn_Green", 100, 655, "spawn_Blue", 2367, 655)]
        public void LoadData_yields_the_seven_images_and_the_regions(string map, string flag, int fx, int fy, string sg, int sgx, int sgy, string sb, int sbx, int sby)
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                LevelLoader.LevelData d = HeadlessLevel.LoadData(rt.Root, map);
                Assert.NotNull(d);
                Assert.Equal(map, d.shortName);
                Assert.Equal(new SortedSet<string> { "main", "background", "collision", "sky", "left", "right", "bottom" },
                             new SortedSet<string>(d.bitmaps.Keys));
                Assert.Equal(840, d.bitmaps["sky"].Width);
                Assert.Equal(630, d.bitmaps["sky"].Height);
                Assert.Equal(4, d.regions.Count);
                Region f = d.regions.Find(r => r.name == flag);
                Assert.NotNull(f);
                Assert.Equal(fx, f.x1);
                Assert.Equal(fy, f.y1);
                Region g = d.regions.Find(r => r.name == sg);
                Assert.Equal(sgx, g.x1);
                Assert.Equal(sgy, g.y1);
                Region b = d.regions.Find(r => r.name == sb);
                Assert.Equal(sbx, b.x1);
                Assert.Equal(sby, b.y1);
                Assert.Equal(new System.IO.FileInfo(rt.MapPath(map)).Length - 4, d.bytes.Length);
                Assert.Null(HeadlessLevel.LoadData(rt.Root, "no-such-map"));
            }
        }
    }
}
