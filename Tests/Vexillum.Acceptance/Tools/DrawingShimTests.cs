using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace Vexillum.Acceptance.toolsconfig
{
    /// <summary>
    /// System.Drawing shim (Shims/Drawing) cases that Tests/Vexillum.Tests/
    /// DrawingShimTests does not cover: stream position handling, the
    /// PNG-only encoder, LockBits argument checks and sub-rectangles, lock
    /// release on Dispose, MakeTransparent keying, and the empty codec list
    /// MapCreator.GetJpgEncoder walks. No process state.
    /// </summary>
    public class DrawingShimTests
    {
        private static Bitmap Gradient(int w, int h)
        {
            Bitmap b = new Bitmap(w, h);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    b.SetPixel(x, y, Color.FromArgb(255, (x * 37) & 255, (y * 59) & 255, (x + y) & 255));
            return b;
        }

        private static byte[] Png(Bitmap b)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                b.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        // TOOLS-22
        [Fact]
        public void Stream_constructor_decodes_from_the_current_position_and_leaves_the_stream_at_its_end()
        {
            Bitmap src = Gradient(7, 5);
            byte[] png = Png(src);
            byte[] prefixed = new byte[png.Length + 4];
            BitConverter.GetBytes(0x004F876B).CopyTo(prefixed, 0);
            png.CopyTo(prefixed, 4);
            using (MemoryStream ms = new MemoryStream(prefixed))
            {
                ms.Position = 4;
                using (Bitmap decoded = new Bitmap(ms))
                {
                    Assert.Equal(7, decoded.Width);
                    Assert.Equal(5, decoded.Height);
                    Assert.Equal(ImageFormat.Png, decoded.RawFormat);
                    Assert.Equal(src.GetPixel(6, 4), decoded.GetPixel(6, 4));
                    Assert.Equal(src.GetPixel(0, 0), decoded.GetPixel(0, 0));
                }
                Assert.Equal(ms.Length, ms.Position);
            }
            // Position 0 on a MemoryStream takes the fast path and also ends at the end.
            using (MemoryStream ms = new MemoryStream(png))
            {
                using (Bitmap decoded = new Bitmap(ms))
                    Assert.Equal(7, decoded.Width);
                Assert.Equal(ms.Length, ms.Position);
            }
            Assert.Throws<ArgumentException>(() => new Bitmap((Stream)null));
            Assert.Throws<ArgumentException>(() => new Bitmap(new MemoryStream(new byte[] { 1, 2, 3 })));
        }

        // TOOLS-22
        [Fact]
        public void Only_PNG_can_be_encoded()
        {
            using (Bitmap b = Gradient(3, 3))
            using (MemoryStream ms = new MemoryStream())
            {
                NotSupportedException ex = Assert.Throws<NotSupportedException>(() => b.Save(ms, ImageFormat.Jpeg));
                Assert.Contains("only encodes PNG", ex.Message);
                Assert.Equal(0, ms.Length);
                Assert.Throws<NotSupportedException>(() => b.Save(ms, ImageFormat.Bmp));
                b.Save(ms, ImageFormat.Png);
                Assert.True(ms.Length > 8);
                Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, new ArraySegment<byte>(ms.ToArray(), 0, 4));
                Assert.Throws<ArgumentNullException>(() => b.Save((Stream)null, ImageFormat.Png));
                Assert.Throws<ArgumentNullException>(() => b.Save(ms, null));
            }
        }

        // TOOLS-22
        [Fact]
        public void Save_to_a_jpg_path_opens_the_file_first_and_then_refuses()
        {
            using (TempDir dir = new TempDir())
            using (Bitmap b = Gradient(3, 3))
            {
                string jpg = dir.File("x.jpg");
                File.WriteAllText(jpg, "previous content");
                Assert.Throws<NotSupportedException>(() => b.Save(jpg));
                Assert.True(File.Exists(jpg));
                Assert.Equal(0, new FileInfo(jpg).Length);   // FileMode.Create truncated it before the format check

                string png = dir.File("y.png");
                b.Save(png);
                using (Bitmap back = new Bitmap(png))
                {
                    Assert.Equal(3, back.Width);
                    Assert.Equal(b.GetPixel(2, 1), back.GetPixel(2, 1));
                }
                Assert.Throws<NotSupportedException>(() => b.Save(dir.File("z.bmp")));
            }
        }

        // TOOLS-22
        [Fact]
        public void LockBits_rejects_mode_zero_and_bad_rectangles()
        {
            using (Bitmap b = Gradient(4, 4))
            {
                ArgumentException ex = Assert.Throws<ArgumentException>(() => b.LockBits(new Rectangle(0, 0, 4, 4), (ImageLockMode)0, PixelFormat.Format32bppArgb));
                Assert.Equal("Parameter is not valid.", ex.Message);
                Assert.Throws<ArgumentException>(() => b.LockBits(new Rectangle(0, 0, 5, 4), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb));
                Assert.Throws<ArgumentException>(() => b.LockBits(new Rectangle(-1, 0, 2, 2), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb));
                Assert.Throws<ArgumentException>(() => b.LockBits(new Rectangle(0, 0, 0, 4), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb));
                Assert.Throws<NotSupportedException>(() => b.LockBits(new Rectangle(0, 0, 4, 4), ImageLockMode.ReadOnly, PixelFormat.Format16bppRgb565));
                // Nothing above left a lock behind.
                BitmapData d = b.LockBits(new Rectangle(0, 0, 4, 4), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                b.UnlockBits(d);
                Assert.Throws<ArgumentException>(() => b.UnlockBits(d));   // no lock any more
            }
        }

        // TOOLS-22: a sub-rectangle lock exposes the surface at that offset with the full stride (as GDI+ does for a matching format)
        [Fact]
        public void LockBits_sub_rectangle_points_into_the_surface_with_the_full_stride()
        {
            using (Bitmap b = Gradient(9, 6))
            {
                Color[,] expected = new Color[9, 6];
                for (int y = 0; y < 6; y++)
                    for (int x = 0; x < 9; x++)
                        expected[x, y] = b.GetPixel(x, y);

                Rectangle rect = new Rectangle(2, 3, 4, 2);
                BitmapData d = b.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                try
                {
                    Assert.Equal(4, d.Width);
                    Assert.Equal(2, d.Height);
                    Assert.Equal(9 * 4, d.Stride);
                    Assert.Equal(PixelFormat.Format32bppArgb, d.PixelFormat);
                    Assert.Throws<InvalidOperationException>(() => b.GetPixel(0, 0));
                    Assert.Throws<InvalidOperationException>(() => new Bitmap(b));
                    for (int y = 0; y < rect.Height; y++)
                    {
                        for (int x = 0; x < rect.Width; x++)
                        {
                            IntPtr p = d.Scan0 + y * d.Stride + x * 4;
                            Color c = expected[rect.X + x, rect.Y + y];
                            Assert.Equal(c.B, Marshal.ReadByte(p));
                            Assert.Equal(c.G, Marshal.ReadByte(p, 1));
                            Assert.Equal(c.R, Marshal.ReadByte(p, 2));
                            Assert.Equal(c.A, Marshal.ReadByte(p, 3));
                        }
                    }
                }
                finally
                {
                    b.UnlockBits(d);
                }
                Assert.Equal(expected[0, 0], b.GetPixel(0, 0));

                // 24bpp sub-rectangle: a conversion buffer with the 4-byte-aligned stride, pixels copied from the rectangle.
                BitmapData d24 = b.LockBits(new Rectangle(1, 1, 3, 2), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
                try
                {
                    Assert.Equal(12, d24.Stride);   // 3 * 3 = 9 rounded up to 12
                    Color c = expected[1 + 2, 1 + 1];
                    IntPtr p = d24.Scan0 + 1 * d24.Stride + 2 * 3;
                    Assert.Equal(c.B, Marshal.ReadByte(p));
                    Assert.Equal(c.G, Marshal.ReadByte(p, 1));
                    Assert.Equal(c.R, Marshal.ReadByte(p, 2));
                }
                finally
                {
                    b.UnlockBits(d24);
                }
            }
        }

        // TOOLS-22
        [Fact]
        public void Dispose_while_locked_releases_the_lock()
        {
            Bitmap b = Gradient(3, 3);
            BitmapData d = b.LockBits(new Rectangle(0, 0, 3, 3), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            Assert.Throws<InvalidOperationException>(() => new Bitmap(b));
            b.Dispose();
            using (Bitmap copy = new Bitmap(b))   // no "already locked" any more
            {
                Assert.Equal(3, copy.Width);
                Assert.Equal(3, copy.Height);
            }
            b.Dispose();                          // idempotent
        }

        // TOOLS-22: MakeTransparent keys on the bottom-left pixel only when that pixel is opaque, and only on exact matches
        [Fact]
        public void MakeTransparent_keys_the_bottom_left_colour_and_skips_non_opaque_corners()
        {
            using (Bitmap b = new Bitmap(3, 2))
            {
                Color key = Color.FromArgb(255, 200, 100, 50);
                b.SetPixel(0, 1, key);                                   // bottom-left: the key
                b.SetPixel(1, 1, key);
                b.SetPixel(2, 1, Color.FromArgb(255, 200, 100, 51));     // one channel off: kept
                b.SetPixel(0, 0, Color.FromArgb(254, 200, 100, 50));     // alpha differs: kept
                b.SetPixel(1, 0, key);
                b.SetPixel(2, 0, Color.FromArgb(255, 1, 2, 3));
                b.MakeTransparent();
                Assert.Equal(0, b.GetPixel(0, 1).A);
                Assert.Equal(0, b.GetPixel(1, 1).A);
                Assert.Equal(0, b.GetPixel(1, 0).A);
                Assert.Equal(255, b.GetPixel(2, 1).A);
                Assert.Equal(254, b.GetPixel(0, 0).A);
                Assert.Equal(Color.FromArgb(255, 1, 2, 3), b.GetPixel(2, 0));
                Assert.Equal(PixelFormat.Format32bppArgb, b.PixelFormat);
            }
            using (Bitmap b = new Bitmap(2, 2))
            {
                // Bottom-left already translucent: nothing happens, even to pixels equal to it.
                Color soft = Color.FromArgb(128, 9, 9, 9);
                b.SetPixel(0, 1, soft);
                b.SetPixel(1, 0, soft);
                b.SetPixel(0, 0, Color.FromArgb(255, 9, 9, 9));
                b.MakeTransparent();
                Assert.Equal(soft, b.GetPixel(0, 1));
                Assert.Equal(soft, b.GetPixel(1, 0));
                Assert.Equal(255, b.GetPixel(0, 0).A);
            }
            // A fresh bitmap is transparent black, so the corner is not opaque and nothing changes.
            using (Bitmap b = new Bitmap(2, 2))
            {
                b.SetPixel(1, 0, Color.FromArgb(0, 0, 0, 0));
                b.MakeTransparent();
                Assert.Equal(0, b.GetPixel(0, 1).A);
            }
        }

        // TOOLS-22: the only GetImageEncoders caller returns null
        [Fact]
        public void Codec_list_is_empty_so_MapCreator_GetJpgEncoder_returns_null()
        {
            Assert.Empty(ImageCodecInfo.GetImageEncoders());
            Assert.Empty(ImageCodecInfo.GetImageDecoders());
            Assert.Null(MapToolsDriver.GetJpgEncoder());
        }
    }
}
