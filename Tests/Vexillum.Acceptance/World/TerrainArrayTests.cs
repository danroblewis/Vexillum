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
    /// TerrainArray on its own: the wire bitfield (ToBytes/SetBytes), the
    /// out-of-bounds rules and Destroy. Pure data structure, no process-wide
    /// state, but the bases round trip loads a map so the class stays in the
    /// GameState collection.
    /// </summary>
    [Collection(GameStateCollection.Name)]
    public class TerrainArrayTests
    {
        // PHYS-04: column-major, LSB first, bit 0 only, (w*h)/8 bytes.
        [Fact]
        public void ToBytes_is_column_major_LSB_first_and_serialises_only_the_solid_bit()
        {
            TerrainArray t = new TerrainArray(16, 8);
            t.SetTerrain(1, 0, true);                       // index 1*8+0 = 8 -> byte 1, bit 0
            t.SetTerrain(0, 3, true);                       // index 3 -> byte 0, bit 3
            t.SetCollisionData(2, 2, 7);                    // nibble only, no terrain bit
            t.SetTerrainAndCollisionData(5, 5, 0, 0, 1);    // ladder only
            byte[] b = t.ToBytes();
            Assert.Equal(16, b.Length);
            Assert.Equal(0x08, b[0]);
            Assert.Equal(0x01, b[1]);
            for (int i = 2; i < 16; i++)
                Assert.True(0 == b[i], "byte " + i + " should be 0 (collision nibble and ladder bit are not serialised)");
            Assert.Equal(7, t.GetCollisionData(2, 2));
            Assert.True(t.GetLadder(5, 5));
        }

        // PHYS-04 (tail): (w*h)/8 is integer division; a 3x3 array with an empty tail pixel still serialises to one byte.
        [Fact]
        public void ToBytes_of_a_non_multiple_of_eight_array_truncates_to_whole_bytes_when_the_tail_is_empty()
        {
            TerrainArray t = new TerrainArray(3, 3);
            t.SetTerrain(0, 0, true);
            t.SetTerrain(2, 1, true);   // index 7 -> bit 7 of byte 0
            byte[] b = t.ToBytes();
            Assert.Single(b);
            Assert.Equal(0x81, b[0]);
        }

        [Fact(Skip = "Known original bug: TerrainArray.ToBytes/SetBytes overrun the (w*h)/8 buffer when width*height is not a multiple of 8 (a solid tail pixel throws IndexOutOfRangeException, SetBytes always throws), docs/PORTING.md")]
        public void ToBytes_and_SetBytes_round_trip_a_3x3_array()
        {
            TerrainArray t = new TerrainArray(3, 3);
            t.SetTerrain(2, 2, true);
            byte[] b = t.ToBytes();
            TerrainArray u = new TerrainArray(3, 3);
            u.SetBytes(b);
            Assert.True(u.GetTerrain(2, 2));
        }

        // PHYS-05: SetBytes rewrites bit 0 only.
        [Fact]
        public void SetBytes_rewrites_only_the_solid_bit_and_keeps_nibble_ladder_and_transparent_bits()
        {
            TerrainArray t = new TerrainArray(16, 8);
            t.SetTerrainAndCollisionData(4, 4, 1, 5, 0);
            t.SetTransparent(4, 4, true);
            t.SetTerrainAndCollisionData(6, 6, 0, 0, 1);
            Assert.True(t.GetTerrain(4, 4));

            t.SetBytes(new byte[16]);

            Assert.False(t.GetTerrain(4, 4));
            Assert.Equal(5, t.GetCollisionData(4, 4));
            Assert.True(t.GetTransparent(4, 4));
            Assert.True(t.GetLadder(6, 6));
            Assert.False(t.GetTerrain(6, 6));

            byte[] all = new byte[16];
            for (int i = 0; i < 16; i++) all[i] = 0xFF;
            t.SetBytes(all);
            for (int x = 0; x < 16; x++)
                for (int y = 0; y < 8; y++)
                    Assert.True(t.GetTerrain(x, y));
            Assert.Equal(5, t.GetCollisionData(4, 4));
        }

        // PHYS-05 (map-sized): GetTerrainState -> fresh level SetTerrainState is stable and touches no other bits.
        [Theory]
        [InlineData("bases", "059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3", 800, 300)]
        [InlineData("complex", "d5d14f4c61b455f074f82222ad6da7a5a491bf4ddcd53bde8056c3f2974695b6", 800, 100)]
        public void Terrain_state_round_trip_on_a_shipped_map_keeps_the_hash_and_the_other_bits(string map, string sha, int ex, int ey)
        {
            using (ScratchRuntime rt = new ScratchRuntime())
            {
                HeadlessLevel a = HeadlessLevel.Load(rt, map);
                byte[] state = a.GetTerrainState();
                HeadlessLevel b = HeadlessLevel.Load(rt, map);
                // Punch a hole in b first so SetTerrainState has something to restore.
                b.Explode(ex, ey, 26, 4242, false, null, null);
                Assert.NotEqual(sha, b.Snapshot().Sha256Hex());
                int ladders = 0, nibbles = 0;
                for (int x = 0; x < b.Width; x++)
                    for (int y = 0; y < b.Height; y++)
                    {
                        if (b.IsLadder(x, y)) ladders++;
                        nibbles += b.CollisionNibble(x, y);
                    }

                b.SetTerrainState(state);

                Assert.Equal(sha, b.Snapshot().Sha256Hex());
                int ladders2 = 0, nibbles2 = 0;
                for (int x = 0; x < b.Width; x++)
                    for (int y = 0; y < b.Height; y++)
                    {
                        if (b.IsLadder(x, y)) ladders2++;
                        nibbles2 += b.CollisionNibble(x, y);
                    }
                Assert.Equal(ladders, ladders2);
                Assert.Equal(nibbles, nibbles2);
                Assert.True(ladders > 0);
            }
        }

        // PHYS-06: outside the map terrain is solid, ladders/entities/collision data are not.
        [Fact]
        public void Out_of_bounds_reads_as_solid_terrain_but_no_ladder_entity_or_collision_data()
        {
            SyntheticLevel level = SyntheticLevel.Uniform(4, 4, SyntheticLevel.Empty);
            TerrainArray t = level.Terrain;
            int[][] outside = { new[] { -1, 0 }, new[] { 4, 0 }, new[] { 0, -1 }, new[] { 0, 4 } };
            foreach (int[] p in outside)
            {
                string at = "(" + p[0] + "," + p[1] + ")";
                Assert.True(t.GetTerrain(p[0], p[1]), "GetTerrain " + at);
                Assert.True(t.GetParticle(p[0], p[1]), "GetParticle " + at);
                Assert.True(t.GetTerrainOrParticle(p[0], p[1]), "GetTerrainOrParticle " + at);
                Assert.False(t.GetLadder(p[0], p[1]), "GetLadder " + at);
                Assert.Equal(0, t.GetCollisionData(p[0], p[1]));
                Assert.Equal(0, t.GetEntity(null, p[0], p[1], level));
                Assert.False(t.Destroy(p[0], p[1]), "Destroy " + at);
                t.SetTerrain(p[0], p[1], false);    // no exception
                t.SetParticle(p[0], p[1], true);
            }
            Assert.False(t.GetTerrain(0, 0));
            Assert.False(t.GetParticle(0, 0));
            Assert.Equal(0, t.GetEntity(null, 0, 0, level));
        }

        // PHYS-07: Destroy flips only destructible solid pixels.
        [Fact]
        public void Destroy_flips_only_destructible_solid_pixels()
        {
            TerrainArray t = new TerrainArray(4, 4);
            t.SetTerrainAndCollisionData(0, 0, 1, 1, 0);   // Solid (indestructible)
            t.SetTerrainAndCollisionData(1, 0, 1, 9, 0);   // destructible
            t.SetTerrainAndCollisionData(2, 0, 0, 0, 0);   // empty
            t.SetTerrainAndCollisionData(3, 0, 1, 0, 0);   // terrain with an Empty nibble (SetBytes can produce it)

            Assert.False(t.Destroy(0, 0));
            Assert.True(t.GetTerrain(0, 0));
            Assert.Equal(1, t.GetCollisionData(0, 0));

            Assert.True(t.Destroy(1, 0));
            Assert.False(t.GetTerrain(1, 0));
            Assert.Equal(9, t.GetCollisionData(1, 0));
            Assert.False(t.Destroy(1, 0));

            Assert.False(t.Destroy(2, 0));
            Assert.False(t.GetTerrain(2, 0));

            Assert.True(t.Destroy(3, 0));
            Assert.False(t.GetTerrain(3, 0));
        }

        // Scope: ToBytes layout agrees with the harness TerrainSnapshot index formula for every pixel of a small random array.
        [Fact]
        public void ToBytes_layout_matches_the_documented_index_formula()
        {
            Random r = new Random(7);
            TerrainArray t = new TerrainArray(24, 16);
            bool[,] truth = new bool[24, 16];
            for (int x = 0; x < 24; x++)
                for (int y = 0; y < 16; y++)
                {
                    truth[x, y] = r.Next(2) == 1;
                    t.SetTerrain(x, y, truth[x, y]);
                }
            TerrainSnapshot s = new TerrainSnapshot(t.ToBytes(), 24, 16);
            int solid = 0;
            for (int x = 0; x < 24; x++)
                for (int y = 0; y < 16; y++)
                {
                    Assert.True(truth[x, y] == s.IsSolid(x, y), "pixel " + x + "," + y);
                    if (truth[x, y]) solid++;
                }
            Assert.Equal(solid, s.SolidCount());
            Assert.True(solid > 100 && solid < 300);
        }
    }
}
