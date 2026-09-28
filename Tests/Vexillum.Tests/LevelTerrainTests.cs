using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Vexillum.Entities;
using Xunit;

namespace Vexillum.Tests
{
    /// <summary>
    /// Terrain fidelity through the REAL game classes: LevelLoader.LoadData reads
    /// the shipped .map (LZMA container, data.txt, PNG/JPG decode through the
    /// System.Drawing shim) and the historical Level constructor turns the
    /// collision bitmap into the TerrainArray exactly as the 2013 build did.
    /// The expected hashes are SHA-256 of TerrainArray.ToBytes(), computed
    /// independently in Python from collision.png. If this test fails the
    /// Drawing shim (LockBits layout / decode / MakeTransparent) is wrong;
    /// never adjust the oracle or Level.cs.
    /// </summary>
    public class LevelTerrainTests
    {
        // Level is abstract only through Collision(); the server subclass needs a
        // Server instance, so the test supplies the smallest concrete subclass.
        private sealed class TestLevel : global::Vexillum.Level
        {
            public TestLevel(string shortName, string longName, System.Drawing.Bitmap main,
                             System.Drawing.Bitmap background, System.Drawing.Bitmap collision,
                             List<global::Vexillum.util.Region> regions)
                : base(shortName, longName, main, background, collision, regions)
            {
            }

            protected override void Collision(Entity e1, Entity e2, int direction)
            {
            }

            public int Width { get { return width; } }
            public int Height { get { return height; } }
        }

        private static readonly object cwdLock = new object();

        private static string RepoRoot()
        {
            string dir = AppContext.BaseDirectory;
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir, "Test", "Maps", "bases.map")))
                    return dir;
                dir = Path.GetDirectoryName(dir);
            }
            throw new DirectoryNotFoundException("Test/Maps/bases.map not found above " + AppContext.BaseDirectory);
        }

        private static string Sha256Hex(byte[] bytes)
        {
            byte[] hash = SHA256.HashData(bytes);
            StringBuilder sb = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash)
                sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        [Theory]
        [InlineData("bases", 3914, 1024, "059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3")]
        [InlineData("complex", 2736, 818, "d5d14f4c61b455f074f82222ad6da7a5a491bf4ddcd53bde8056c3f2974695b6")]
        public void RealLevelConstructorReproducesTheOriginalTerrain(string mapName, int expectedWidth, int expectedHeight, string expectedSha256)
        {
            lock (cwdLock)
            {
                // LevelLoader reads Maps/<name>.map relative to the working
                // directory, exactly as the shipped exe does from Test/.
                Directory.SetCurrentDirectory(Path.Combine(RepoRoot(), "Test"));
                // Server mode: no textures are created from the bitmaps.
                global::Vexillum.Util.IsServer = true;

                global::Vexillum.LevelLoader.LevelData d = global::Vexillum.LevelLoader.LoadData(mapName);
                Assert.NotNull(d);
                Assert.Equal(mapName, d.shortName);
                Assert.Equal(expectedWidth, d.bitmaps["main"].Width);
                Assert.Equal(expectedHeight, d.bitmaps["main"].Height);
                Assert.Equal(expectedWidth, d.bitmaps["collision"].Width);
                Assert.Equal(expectedHeight, d.bitmaps["collision"].Height);

                TestLevel level = new TestLevel(d.shortName, d.longName, d.bitmaps["main"],
                                                d.bitmaps["background"], d.bitmaps["collision"], d.regions);

                Assert.Equal(expectedWidth, level.Width);
                Assert.Equal(expectedHeight, level.Height);

                byte[] state = level.GetTerrainState();
                Assert.Equal(expectedSha256, Sha256Hex(state));

                // Round trip through the wire representation must be stable.
                TestLevel again = new TestLevel(d.shortName, d.longName, d.bitmaps["main"],
                                                d.bitmaps["background"], d.bitmaps["collision"], d.regions);
                again.SetTerrainState(state);
                Assert.Equal(expectedSha256, Sha256Hex(again.GetTerrainState()));
            }
        }
    }
}
