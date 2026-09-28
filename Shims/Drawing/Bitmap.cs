using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using StbImageSharp;

namespace System.Drawing
{
    /// <summary>
    /// Managed stand-in for GDI+ System.Drawing.Bitmap, covering the members
    /// Vexillum uses (Util, Level, ClientLevel, LevelLoader, AssetManager).
    ///
    /// Every bitmap is stored as 32bppArgb in GDI+ memory order: four bytes per
    /// pixel B, G, R, A, top row first, stride = Width * 4, straight (non
    /// premultiplied) alpha. That is the layout Level's constructor reads back
    /// through LockBits (ptr = blue, ptr+1 = green, ptr+2 = red, ptr+3 = alpha).
    /// JPEG and PNG files are decoded by StbImageSharp; the only encoder is PNG
    /// (Save with ImageFormat.Png), which is all Util.loadTexture needs.
    /// </summary>
    public sealed class Bitmap : Image
    {
        private byte[] data;
        private int width;
        private int height;
        private ImageFormat rawFormat;
        private bool disposed;

        // LockBits state: at most one lock at a time, as in GDI+.
        private GCHandle lockHandle;
        private BitmapData lockData;
        private byte[] lockBuffer;      // conversion buffer when the requested format is not 32bpp straight
        private Rectangle lockRect;
        private ImageLockMode lockMode;

        public Bitmap(int width, int height)
            : this(width, height, PixelFormat.Format32bppArgb)
        {
        }

        public Bitmap(int width, int height, PixelFormat format)
        {
            if (width <= 0 || height <= 0)
                throw new ArgumentException("Parameter is not valid.");
            if (!IsSupportedFormat(format))
                throw new ArgumentException("Parameter is not valid.");
            this.width = width;
            this.height = height;
            this.data = new byte[checked(width * height * 4)];   // transparent black, like GDI+
            this.rawFormat = ImageFormat.MemoryBmp;
        }

        public Bitmap(string filename)
        {
            if (filename == null)
                throw new ArgumentNullException("filename");
            string path = Path.GetFullPath(filename);
            if (!File.Exists(path))
                throw new FileNotFoundException(path);
            Decode(File.ReadAllBytes(path));
        }

        /// <summary>
        /// Decodes the stream from its current position to its end. GDI+ leaves the
        /// stream positioned at the end of the image data; so does this.
        /// </summary>
        public Bitmap(Stream stream)
        {
            if (stream == null)
                throw new ArgumentException("Parameter is not valid.");
            byte[] bytes;
            MemoryStream ms = stream as MemoryStream;
            if (ms != null && ms.Position == 0)
            {
                bytes = ms.ToArray();
                ms.Seek(0, SeekOrigin.End);
            }
            else
            {
                MemoryStream copy = new MemoryStream();
                stream.CopyTo(copy);
                bytes = copy.ToArray();
            }
            Decode(bytes);
        }

        public Bitmap(Image original)
        {
            Bitmap src = original as Bitmap;
            if (src == null)
                throw new ArgumentException("Parameter is not valid.");
            src.CheckLocked();
            width = src.width;
            height = src.height;
            data = (byte[])src.data.Clone();
            rawFormat = src.rawFormat;
        }

        private static bool IsSupportedFormat(PixelFormat format)
        {
            return format == PixelFormat.Format32bppArgb
                || format == PixelFormat.Format32bppRgb
                || format == PixelFormat.Format32bppPArgb
                || format == PixelFormat.Format24bppRgb;
        }

        private void Decode(byte[] bytes)
        {
            ImageResult img;
            try
            {
                img = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlueAlpha);
            }
            catch (Exception e)
            {
                throw new ArgumentException("Parameter is not valid.", e);
            }
            width = img.Width;
            height = img.Height;
            byte[] rgba = img.Data;
            data = new byte[width * height * 4];
            for (int i = 0; i < data.Length; i += 4)
            {
                data[i] = rgba[i + 2];      // B
                data[i + 1] = rgba[i + 1];  // G
                data[i + 2] = rgba[i];      // R
                data[i + 3] = rgba[i + 3];  // A
            }
            rawFormat = DetectFormat(bytes);
        }

        private static ImageFormat DetectFormat(byte[] b)
        {
            if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47)
                return ImageFormat.Png;
            if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8)
                return ImageFormat.Jpeg;
            if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D)
                return ImageFormat.Bmp;
            if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46)
                return ImageFormat.Gif;
            return ImageFormat.MemoryBmp;
        }

        public override int Width
        {
            get { return width; }
        }

        public override int Height
        {
            get { return height; }
        }

        /// <summary>Always 32bppArgb: MakeTransparent converts to it in GDI+ and every
        /// bitmap the game reads through LockBits went through MakeTransparent.</summary>
        public override PixelFormat PixelFormat
        {
            get { return PixelFormat.Format32bppArgb; }
        }

        public override ImageFormat RawFormat
        {
            get { return rawFormat; }
        }

        private void CheckLocked()
        {
            if (lockData != null)
                throw new InvalidOperationException("Bitmap region is already locked.");
        }

        public Color GetPixel(int x, int y)
        {
            if (x < 0 || x >= width)
                throw new ArgumentOutOfRangeException("x", "Parameter must be positive and < Width.");
            if (y < 0 || y >= height)
                throw new ArgumentOutOfRangeException("y", "Parameter must be positive and < Height.");
            CheckLocked();
            int i = (y * width + x) * 4;
            return Color.FromArgb(data[i + 3], data[i + 2], data[i + 1], data[i]);
        }

        public void SetPixel(int x, int y, Color color)
        {
            if (x < 0 || x >= width)
                throw new ArgumentOutOfRangeException("x", "Parameter must be positive and < Width.");
            if (y < 0 || y >= height)
                throw new ArgumentOutOfRangeException("y", "Parameter must be positive and < Height.");
            CheckLocked();
            int i = (y * width + x) * 4;
            data[i] = color.B;
            data[i + 1] = color.G;
            data[i + 2] = color.R;
            data[i + 3] = color.A;
        }

        /// <summary>
        /// GDI+: the transparent colour is the pixel in the bottom-left corner,
        /// unless that pixel is already not opaque, in which case nothing is done.
        /// </summary>
        public void MakeTransparent()
        {
            Color key = Color.LightGray;
            if (width > 0 && height > 0)
                key = GetPixel(0, height - 1);
            if (key.A < 255)
                return;
            MakeTransparent(key);
        }

        /// <summary>
        /// GDI+ redraws the image onto a cleared 32bppArgb surface with a colour
        /// key equal to transparentColor; a pixel is keyed out only when all four
        /// channels match, so with an opaque source and Color.Transparent
        /// (A=0,R=G=B=255) as the key nothing changes but the pixel format.
        /// </summary>
        public void MakeTransparent(Color transparentColor)
        {
            CheckLocked();
            byte b = transparentColor.B, g = transparentColor.G, r = transparentColor.R, a = transparentColor.A;
            for (int i = 0; i < data.Length; i += 4)
            {
                if (data[i] == b && data[i + 1] == g && data[i + 2] == r && data[i + 3] == a)
                    data[i + 3] = 0;
            }
        }

        /// <summary>Deep copy; returns object like GDI+, so (Bitmap)bmp.Clone() works.</summary>
        public override object Clone()
        {
            return new Bitmap(this);
        }

        public override void Save(Stream stream, ImageFormat format)
        {
            if (stream == null)
                throw new ArgumentNullException("stream");
            if (format == null)
                throw new ArgumentNullException("format");
            CheckLocked();
            if (format.Guid == ImageFormat.Png.Guid || format.Guid == ImageFormat.MemoryBmp.Guid)
            {
                PngEncoder.Write(stream, data, width, height);
                return;
            }
            throw new NotSupportedException("The System.Drawing shim only encodes PNG (requested " + format + ").");
        }

        public BitmapData LockBits(Rectangle rect, ImageLockMode flags, PixelFormat format)
        {
            CheckLocked();
            if (rect.X < 0 || rect.Y < 0 || rect.Width <= 0 || rect.Height <= 0
                || rect.X + rect.Width > width || rect.Y + rect.Height > height)
                throw new ArgumentException("Parameter is not valid.");
            if ((flags & ImageLockMode.ReadWrite) == 0)
                throw new ArgumentException("Parameter is not valid.");

            BitmapData bd = new BitmapData();
            bd.Width = rect.Width;
            bd.Height = rect.Height;
            bd.PixelFormat = format;
            lockRect = rect;
            lockMode = flags;

            if (format == PixelFormat.Format32bppArgb || format == PixelFormat.Format32bppRgb)
            {
                // Same layout as the surface: hand out the surface itself, as GDI+
                // does for a matching format. Stride is the full bitmap stride.
                lockHandle = GCHandle.Alloc(data, GCHandleType.Pinned);
                lockBuffer = null;
                bd.Stride = width * 4;
                bd.Scan0 = lockHandle.AddrOfPinnedObject() + (rect.Y * width + rect.X) * 4;
            }
            else if (format == PixelFormat.Format32bppPArgb)
            {
                int stride = rect.Width * 4;
                lockBuffer = new byte[stride * rect.Height];
                if ((flags & ImageLockMode.ReadOnly) != 0)
                {
                    for (int y = 0; y < rect.Height; y++)
                    {
                        int src = ((rect.Y + y) * width + rect.X) * 4;
                        int dst = y * stride;
                        for (int x = 0; x < rect.Width; x++, src += 4, dst += 4)
                        {
                            int alpha = data[src + 3];
                            lockBuffer[dst] = (byte)((data[src] * alpha + 127) / 255);
                            lockBuffer[dst + 1] = (byte)((data[src + 1] * alpha + 127) / 255);
                            lockBuffer[dst + 2] = (byte)((data[src + 2] * alpha + 127) / 255);
                            lockBuffer[dst + 3] = (byte)alpha;
                        }
                    }
                }
                lockHandle = GCHandle.Alloc(lockBuffer, GCHandleType.Pinned);
                bd.Stride = stride;
                bd.Scan0 = lockHandle.AddrOfPinnedObject();
            }
            else if (format == PixelFormat.Format24bppRgb)
            {
                int stride = (rect.Width * 3 + 3) & ~3;
                lockBuffer = new byte[stride * rect.Height];
                if ((flags & ImageLockMode.ReadOnly) != 0)
                {
                    for (int y = 0; y < rect.Height; y++)
                    {
                        int src = ((rect.Y + y) * width + rect.X) * 4;
                        int dst = y * stride;
                        for (int x = 0; x < rect.Width; x++, src += 4, dst += 3)
                        {
                            lockBuffer[dst] = data[src];
                            lockBuffer[dst + 1] = data[src + 1];
                            lockBuffer[dst + 2] = data[src + 2];
                        }
                    }
                }
                lockHandle = GCHandle.Alloc(lockBuffer, GCHandleType.Pinned);
                bd.Stride = stride;
                bd.Scan0 = lockHandle.AddrOfPinnedObject();
            }
            else
            {
                throw new NotSupportedException("The System.Drawing shim cannot lock bits as " + format + ".");
            }
            lockData = bd;
            return bd;
        }

        public void UnlockBits(BitmapData bitmapdata)
        {
            if (bitmapdata == null)
                throw new ArgumentNullException("bitmapdata");
            if (lockData == null || !ReferenceEquals(bitmapdata, lockData))
                throw new ArgumentException("Parameter is not valid.");

            if (lockBuffer != null && (lockMode & ImageLockMode.WriteOnly) != 0)
            {
                Rectangle rect = lockRect;
                int stride = bitmapdata.Stride;
                if (bitmapdata.PixelFormat == PixelFormat.Format32bppPArgb)
                {
                    for (int y = 0; y < rect.Height; y++)
                    {
                        int dst = ((rect.Y + y) * width + rect.X) * 4;
                        int src = y * stride;
                        for (int x = 0; x < rect.Width; x++, src += 4, dst += 4)
                        {
                            int alpha = lockBuffer[src + 3];
                            if (alpha == 0)
                            {
                                data[dst] = data[dst + 1] = data[dst + 2] = 0;
                            }
                            else
                            {
                                data[dst] = (byte)Math.Min(255, (lockBuffer[src] * 255 + alpha / 2) / alpha);
                                data[dst + 1] = (byte)Math.Min(255, (lockBuffer[src + 1] * 255 + alpha / 2) / alpha);
                                data[dst + 2] = (byte)Math.Min(255, (lockBuffer[src + 2] * 255 + alpha / 2) / alpha);
                            }
                            data[dst + 3] = (byte)alpha;
                        }
                    }
                }
                else
                {
                    for (int y = 0; y < rect.Height; y++)
                    {
                        int dst = ((rect.Y + y) * width + rect.X) * 4;
                        int src = y * stride;
                        for (int x = 0; x < rect.Width; x++, src += 3, dst += 4)
                        {
                            data[dst] = lockBuffer[src];
                            data[dst + 1] = lockBuffer[src + 1];
                            data[dst + 2] = lockBuffer[src + 2];
                            data[dst + 3] = 255;
                        }
                    }
                }
            }
            ReleaseLock();
        }

        private void ReleaseLock()
        {
            if (lockHandle.IsAllocated)
                lockHandle.Free();
            lockBuffer = null;
            lockData = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposed)
                return;
            disposed = true;
            ReleaseLock();
        }
    }
}
