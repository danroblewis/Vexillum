using System;

namespace System.Drawing.Imaging
{
    /// <summary>
    /// Result of Bitmap.LockBits. Scan0 points at pinned managed memory that stays
    /// valid until Bitmap.UnlockBits is called with this same object.
    /// </summary>
    public sealed class BitmapData
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Stride { get; set; }
        public PixelFormat PixelFormat { get; set; }
        public IntPtr Scan0 { get; set; }
        public int Reserved { get; set; }
    }
}
