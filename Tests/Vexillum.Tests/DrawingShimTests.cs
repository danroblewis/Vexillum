using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SevenZip.Compression.LZMA;
using Xunit;

namespace Vexillum.Tests
{
    /// <summary>
    /// Proves the Shims/Drawing Bitmap behaves like GDI+ for the pixels the game
    /// reads: the two shipped maps must derive exactly the terrain bitfield the
    /// 2013 Windows build derived (oracle SHA-256 computed independently in
    /// Python from collision.png, see the vexillum-dev terrain_reference tool),
    /// and Save(PNG) must reproduce every pixel.
    ///
    /// The terrain derivation below is a verbatim copy of the Level constructor
    /// (Game/Game/Level.cs lines 66-126) run against a local copy of the bits of
    /// TerrainArray it touches, so the test compiles whether or not the Game
    /// project does yet.
    /// </summary>
    public class DrawingShimTests
    {
        private const int MapMagic = 0x004F876B;

        // --- verbatim helpers from Game/Game (kept local so the test has no Game dependency) ---

        private struct Vec2
        {
            public float X;
            public float Y;
            public Vec2(float x, float y) { X = x; Y = y; }
        }

        private static class TerrainCollisionType
        {
            public const byte Empty = 0;
            public const byte Solid = 1;
        }

        private static class GraphicsUtil
        {
            public static System.Drawing.Color transparent = System.Drawing.Color.Transparent;
        }

        /// <summary>The subset of Vexillum.util.TerrainArray used by the Level constructor and ToBytes, copied verbatim.</summary>
        private class TerrainArray
        {
            private int[,] terrain;
            private int width;
            private int height;
            public TerrainArray(int sx, int sy)
            {
                this.width = sx;
                this.height = sy;
                terrain = new int[sx, sy];
            }
            private bool CheckOutOfBounds(int x, int y)
            {
                return (x < 0 || y < 0 || x >= width || y >= height);
            }
            private void SetShort0(int x, int y, int data)
            {
                int num = terrain[x, y];
                int numMask = 0xFFFF;
                terrain[x, y] = (num & ~numMask) | data;
            }
            private void SetByte(int x, int y, int idx, bool value)
            {
                int mask = 1 << idx;
                if (value)
                    terrain[x, y] = terrain[x, y] | mask;
                else
                    terrain[x, y] = terrain[x, y] & ~mask;
            }
            private bool GetByte(int x, int y, int idx)
            {
                int mask = 1 << idx;
                return (terrain[x, y] & mask) != 0;
            }
            public bool GetTerrain(int x, int y)
            {
                if (CheckOutOfBounds(x, y))
                    return true;
                return GetByte(x, y, 0);
            }
            public void SetTransparent(int x, int y, bool v)
            {
                SetByte(x, y, 2, v);
            }
            public void SetTerrainAndCollisionData(int x, int y, byte t, byte c, byte l)
            {
                SetShort0(x, y, t | (c << 4) | (l << 3));
            }
            public byte[] ToBytes()
            {
                byte[] r = new byte[(width * height) / 8];
                int index = 0;
                int bitIndex = 0;
                byte[] mask = new byte[] { 1, 2, 4, 8, 16, 32, 64, 128 };
                for (int x = 0; x < width; x++)
                {
                    for (int y = 0; y < height; y++)
                    {
                        if (GetTerrain(x, y))
                            r[index] = (byte)(r[index] | mask[bitIndex]);
                        bitIndex++;
                        if (bitIndex == 8)
                        {
                            bitIndex = 0;
                            index++;
                        }
                    }
                }
                return r;
            }
        }

        // --- map container reading (docs/ARCHITECTURE.md "Map file format"), as LevelLoader.LoadData does it ---

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

        private static Dictionary<string, byte[]> ReadMap(string mapName)
        {
            string path = Path.Combine(RepoRoot(), "Test", "Maps", mapName);
            byte[] file = File.ReadAllBytes(path);
            Assert.Equal(MapMagic, BitConverter.ToInt32(file, 0));
            byte[] compressedBytes = new byte[file.Length - 4];
            Array.Copy(file, 4, compressedBytes, 0, compressedBytes.Length);

            byte[] bytes = SevenZipHelper.Decompress(compressedBytes);
            MemoryStream input = new MemoryStream(bytes);
            BinaryReader reader = new BinaryReader(input);
            Dictionary<string, byte[]> files = new Dictionary<string, byte[]>();
            string longName = reader.ReadString();
            Assert.False(string.IsNullOrEmpty(longName));
            while (input.Position < input.Length)
            {
                string fileName = reader.ReadString();
                if (fileName.Trim() == "")
                    break;
                long length = reader.ReadInt64();
                files[fileName] = reader.ReadBytes((int)length);
            }
            return files;
        }

        /// <summary>LevelLoader.LoadData: new Bitmap(stream) then MakeTransparent(GraphicsUtil.transparent).</summary>
        private static Bitmap LoadLikeLevelLoader(byte[] fileBytes)
        {
            MemoryStream stream = new MemoryStream();
            stream.Write(fileBytes, 0, fileBytes.Length);
            stream.Seek(0, SeekOrigin.Begin);
            Bitmap bmp = new Bitmap(stream);
            bmp.MakeTransparent(GraphicsUtil.transparent);
            return bmp;
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant();
            }
        }

        [Theory]
        [InlineData("bases.map", 3914, 1024, "059ee417b1b64d17f7ebe439dd8bf5b05f01177866873db6dbcd4947716987e3")]
        [InlineData("complex.map", 2736, 818, "d5d14f4c61b455f074f82222ad6da7a5a491bf4ddcd53bde8056c3f2974695b6")]
        public void ShippedMapsDeriveTheOriginalTerrainBitfield(string mapName, int expectedWidth, int expectedHeight, string expectedSha256)
        {
            Dictionary<string, byte[]> files = ReadMap(mapName);
            Bitmap collision = LoadLikeLevelLoader(files["collision.png"]);
            Bitmap main = LoadLikeLevelLoader(files["main.jpg"]);

            Assert.Equal(expectedWidth, collision.Width);
            Assert.Equal(expectedHeight, collision.Height);
            Assert.Equal(expectedWidth, main.Width);
            Assert.Equal(expectedHeight, main.Height);
            Assert.Equal(PixelFormat.Format32bppArgb, collision.PixelFormat);

            Color corner = collision.GetPixel(0, 0);
            Color mainCorner = main.GetPixel(0, 0);
            Assert.Equal(255, mainCorner.A);    // JPEG decodes opaque

            // ---- Game/Game/Level.cs constructor, lines 61-126, verbatim ----
            int width = main.Width;
            int height = main.Height;
            Vec2 Size = new Vec2(main.Width, main.Height);

            TerrainArray terrain = new TerrainArray((int)Size.X, (int)Size.Y);

            BitmapData cData = collision.LockBits(new System.Drawing.Rectangle(0, 0, collision.Width, collision.Height),
                 System.Drawing.Imaging.ImageLockMode.ReadWrite, collision.PixelFormat);

            byte[] cBytes = new byte[cData.Stride * cData.Height];
            System.Runtime.InteropServices.Marshal.Copy(cData.Scan0, cBytes, 0
                                   , cBytes.Length);
            collision.UnlockBits(cData);

            uint cColor;
            int ptr = 0;
            int stride = cData.Stride;
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    ptr = (height - y - 1) * stride + x * 4;
                    cColor = 0;
                    byte red = cBytes[ptr + 2];
                    byte green = cBytes[ptr + 1];
                    cColor |= (uint)(cBytes[ptr + 3] << 24);
                    cColor |= (uint)(red << 16);
                    cColor |= (uint)(green << 8);
                    cColor |= (uint)(cBytes[ptr] << 0);
                    switch (cColor)
                    {
                        case 0xFFFF00FF:
                            terrain.SetTerrainAndCollisionData(x, y, 0, TerrainCollisionType.Empty, 0);
                            break;
                        case 0:
                            terrain.SetTerrainAndCollisionData(x, y, 1, TerrainCollisionType.Solid, 0);
                            break;
                        case 0xFFFFFFFF:
                            terrain.SetTerrainAndCollisionData(x, y, 1, TerrainCollisionType.Solid, 0);
                            break;
                        case 0xFF0000FF:
                            terrain.SetTerrainAndCollisionData(x, y, 0, TerrainCollisionType.Empty, 0);
                            main.SetPixel(x, (int)Size.Y - y - 1, GraphicsUtil.transparent);
                            break;
                        case 0xFFFFFF00:
                            terrain.SetTerrainAndCollisionData(x, y, 0, TerrainCollisionType.Empty, 1);

                            break;
                        case 0xFFFFFF80:
                            terrain.SetTerrainAndCollisionData(x, y, 0, TerrainCollisionType.Empty, 1);
                            main.SetPixel(x, (int)Size.Y - y - 1, GraphicsUtil.transparent);
                            break;
                        default:
                            int cl = red / 16;
                            if (cl > 15)
                                cl = 15;
                            else if (cl < 2)
                                cl = 2;
                            terrain.SetTerrainAndCollisionData(x, y, 1, (byte)cl, 0);
                            if (green == 128)
                                terrain.SetTransparent(x, y, true);
                            break;
                    }
                }
            }
            // ---- end of verbatim block ----

            Assert.Equal(expectedSha256, Sha256Hex(terrain.ToBytes()));

            // LockBits layout: stride is Width*4, bytes are B,G,R,A, top row first.
            Assert.Equal(collision.Width * 4, cData.Stride);
            Assert.Equal(collision.Width, cData.Width);
            Assert.Equal(collision.Height, cData.Height);
            Assert.Equal(corner.B, cBytes[0]);
            Assert.Equal(corner.G, cBytes[1]);
            Assert.Equal(corner.R, cBytes[2]);
            Assert.Equal(corner.A, cBytes[3]);

            // GetPixel(0,0) round-trips through LockBits/UnlockBits.
            Assert.Equal(corner, collision.GetPixel(0, 0));

            // The rest of the constructor: clone, key out, save as PNG for Texture2D.FromStream.
            Bitmap MainBitmap = (System.Drawing.Bitmap)main.Clone();
            MainBitmap.MakeTransparent(GraphicsUtil.transparent);
            Assert.Equal(main.GetPixel(0, 0), MainBitmap.GetPixel(0, 0));
            MemoryStream png = new MemoryStream(MainBitmap.Height * MainBitmap.Width * 4);
            MainBitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
            Assert.Equal(png.Length, png.Position);     // GDI+ leaves the stream at its end; MonoGame rewinds it
            png.Seek(0, SeekOrigin.Begin);
            Bitmap reloaded = new Bitmap(png);
            Assert.Equal(MainBitmap.Width, reloaded.Width);
            Assert.Equal(MainBitmap.Height, reloaded.Height);
            for (int y = 0; y < reloaded.Height; y += 61)
                for (int x = 0; x < reloaded.Width; x += 37)
                    Assert.Equal(MainBitmap.GetPixel(x, y), reloaded.GetPixel(x, y));
        }

        [Fact]
        public void PngRoundTripPreservesEveryPixel()
        {
            int w = 37, h = 23;
            Bitmap bmp = new Bitmap(w, h);
            Assert.Equal(PixelFormat.Format32bppArgb, bmp.PixelFormat);
            Assert.Equal(Color.FromArgb(0, 0, 0, 0), bmp.GetPixel(5, 5));    // GDI+ new bitmaps are transparent black

            uint state = 2463534242u;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                    int a = (x + y) % 4 == 0 ? 0 : ((x + y) % 4 == 1 ? 255 : (int)(state >> 24));
                    Color c = Color.FromArgb(a, (int)(state & 0xFF), (int)((state >> 8) & 0xFF), (int)((state >> 16) & 0xFF));
                    bmp.SetPixel(x, y, c);
                    Assert.Equal(c, bmp.GetPixel(x, y));
                }
            }

            MemoryStream stream = new MemoryStream();
            bmp.Save(stream, ImageFormat.Png);
            byte[] bytes = stream.ToArray();
            // PNG signature and an RGBA 8-bit IHDR.
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, new ArraySegment<byte>(bytes, 0, 8));
            Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(bytes, 12, 4));
            Assert.Equal(w, (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19]);
            Assert.Equal(h, (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23]);
            Assert.Equal(8, bytes[24]);
            Assert.Equal(6, bytes[25]);
            Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(bytes, bytes.Length - 8, 4));

            stream.Seek(0, SeekOrigin.Begin);
            Bitmap back = new Bitmap(stream);
            Assert.Equal(w, back.Width);
            Assert.Equal(h, back.Height);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    Assert.Equal(bmp.GetPixel(x, y), back.GetPixel(x, y));
        }

        [Fact]
        public void MakeTransparentComparesAllFourChannels()
        {
            Bitmap bmp = new Bitmap(3, 1);
            bmp.SetPixel(0, 0, Color.FromArgb(255, 255, 255, 255));    // opaque white: stays
            bmp.SetPixel(1, 0, Color.FromArgb(0, 255, 255, 255));      // == Color.Transparent: already alpha 0
            bmp.SetPixel(2, 0, Color.FromArgb(255, 10, 20, 30));
            bmp.MakeTransparent(Color.Transparent);
            Assert.Equal(Color.FromArgb(255, 255, 255, 255), bmp.GetPixel(0, 0));
            Assert.Equal(0, bmp.GetPixel(1, 0).A);
            Assert.Equal(Color.FromArgb(255, 10, 20, 30), bmp.GetPixel(2, 0));

            bmp.MakeTransparent(Color.FromArgb(255, 10, 20, 30));
            Assert.Equal(Color.FromArgb(0, 10, 20, 30), bmp.GetPixel(2, 0));

            // Parameterless overload keys on the bottom-left pixel when it is opaque.
            Bitmap corner = new Bitmap(2, 2);
            corner.SetPixel(0, 1, Color.Red);
            corner.SetPixel(1, 1, Color.Red);
            corner.SetPixel(0, 0, Color.Blue);
            corner.SetPixel(1, 0, Color.FromArgb(255, 255, 0, 0));
            corner.MakeTransparent();
            Assert.Equal(0, corner.GetPixel(0, 1).A);
            Assert.Equal(0, corner.GetPixel(1, 1).A);
            Assert.Equal(0, corner.GetPixel(1, 0).A);
            Assert.Equal(255, corner.GetPixel(0, 0).A);
        }

        [Fact]
        public void CloneIsDeepAndReturnsObject()
        {
            Bitmap bmp = new Bitmap(4, 4);
            bmp.SetPixel(1, 2, Color.Red);
            object o = bmp.Clone();
            Bitmap copy = (Bitmap)o;
            Assert.NotSame(bmp, copy);
            // GetPixel returns the ARGB form: like GDI+, it is never a named colour, so compare ToArgb().
            Assert.Equal(Color.Red.ToArgb(), copy.GetPixel(1, 2).ToArgb());
            copy.SetPixel(1, 2, Color.Blue);
            Assert.Equal(Color.Red.ToArgb(), bmp.GetPixel(1, 2).ToArgb());
            Assert.Equal(Color.Blue.ToArgb(), copy.GetPixel(1, 2).ToArgb());
        }

        [Fact]
        public void LockBitsExposesTheSurfaceInBgraOrder()
        {
            Bitmap bmp = new Bitmap(5, 3);
            bmp.SetPixel(2, 1, Color.FromArgb(0x80, 0x11, 0x22, 0x33));

            BitmapData bd = bmp.LockBits(new Rectangle(0, 0, 5, 3), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            Assert.Equal(20, bd.Stride);
            Assert.Equal(5, bd.Width);
            Assert.Equal(3, bd.Height);
            Assert.Equal(PixelFormat.Format32bppArgb, bd.PixelFormat);
            byte[] all = new byte[bd.Stride * bd.Height];
            Marshal.Copy(bd.Scan0, all, 0, all.Length);
            int i = 1 * bd.Stride + 2 * 4;
            Assert.Equal(0x33, all[i]);       // B
            Assert.Equal(0x22, all[i + 1]);   // G
            Assert.Equal(0x11, all[i + 2]);   // R
            Assert.Equal(0x80, all[i + 3]);   // A
            Assert.Throws<InvalidOperationException>(() => bmp.GetPixel(0, 0));
            Assert.Throws<InvalidOperationException>(() => bmp.LockBits(new Rectangle(0, 0, 1, 1), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb));
            // Writes through the pointer land in the bitmap.
            Marshal.WriteByte(bd.Scan0, 0, 0xAB);
            Marshal.WriteByte(bd.Scan0, 3, 0xFF);
            bmp.UnlockBits(bd);
            Assert.Equal(Color.FromArgb(0xFF, 0, 0, 0xAB), bmp.GetPixel(0, 0));

            // A sub-rectangle lock points into the same surface with the full stride.
            BitmapData sub = bmp.LockBits(new Rectangle(2, 1, 2, 2), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            Assert.Equal(20, sub.Stride);
            Assert.Equal(2, sub.Width);
            Assert.Equal(2, sub.Height);
            Assert.Equal(0x33, Marshal.ReadByte(sub.Scan0, 0));
            Assert.Equal(0x80, Marshal.ReadByte(sub.Scan0, 3));
            bmp.UnlockBits(sub);

            // 24bpp locks go through a conversion buffer and are copied back on unlock.
            BitmapData rgb = bmp.LockBits(new Rectangle(0, 0, 5, 3), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
            Assert.Equal(16, rgb.Stride);    // 15 rounded up to a multiple of 4
            Assert.Equal(0x33, Marshal.ReadByte(rgb.Scan0, 1 * 16 + 2 * 3));
            Marshal.WriteByte(rgb.Scan0, 2 * 16 + 4 * 3 + 2, 0x77);
            bmp.UnlockBits(rgb);
            Assert.Equal(Color.FromArgb(255, 0x77, 0, 0), bmp.GetPixel(4, 2));
            // A 24bpp write-back has no alpha channel: every pixel in the rectangle comes back opaque, as in GDI+.
            Assert.Equal(Color.FromArgb(255, 0x11, 0x22, 0x33), bmp.GetPixel(2, 1));

            Assert.Throws<ArgumentException>(() => bmp.LockBits(new Rectangle(0, 0, 6, 3), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb));
        }

        [Fact]
        public void LoadsContentFilesFromDiskLikeUtilLoadBitmap()
        {
            string content = Path.Combine(RepoRoot(), "Test", "Content");
            Bitmap png = new Bitmap(Path.Combine(content, "pixel_white.png"));
            Assert.True(png.Width >= 1 && png.Height >= 1);
            Assert.Equal(ImageFormat.Png.Guid, png.RawFormat.Guid);
            Bitmap jpg = new Bitmap(Path.Combine(content, "title.jpg"));
            Assert.True(jpg.Width > 1 && jpg.Height > 1);
            Assert.Equal(ImageFormat.Jpeg.Guid, jpg.RawFormat.Guid);
            Assert.Equal(255, jpg.GetPixel(jpg.Width / 2, jpg.Height / 2).A);
            Assert.Throws<FileNotFoundException>(() => new Bitmap(Path.Combine(content, "does_not_exist.png")));
        }

        [Fact]
        public void StubsUsedByTextRendererAndChatPanelWork()
        {
            Font font = new Font("Arial", 8, FontStyle.Bold);
            Assert.True(font.Bold);
            Assert.Equal(8f, font.Size);
            Brush[] brushes = new Brush[2];
            brushes[0] = new SolidBrush(Color.FromArgb(120, 120, 120));
            brushes[1] = Brushes.White;
            Assert.Equal(Color.White, ((SolidBrush)brushes[1]).Color);
            Graphics g = Graphics.FromImage(new Bitmap(1, 1));
            g.DrawString("x", font, brushes[0], new PointF(1, 2));
            Assert.Empty(ImageCodecInfo.GetImageEncoders());
            Assert.Equal(new Guid("b96b3cae-0728-11d3-9d7b-0000f81ef32e"), ImageFormat.Jpeg.Guid);
        }
    }
}
