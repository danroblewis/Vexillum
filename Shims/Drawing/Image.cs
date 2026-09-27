using System;
using System.IO;
using System.Drawing.Imaging;

namespace System.Drawing
{
    /// <summary>
    /// Base of the GDI+ image hierarchy. The only concrete image in this shim is
    /// Bitmap; this class exists so casts such as (Bitmap)image.Clone() and the
    /// static Image.FromFile/FromStream keep their GDI+ shape. Color, Point,
    /// Rectangle, Size and their float variants are NOT defined here: .NET ships
    /// them in System.Drawing.Primitives.
    /// </summary>
    public abstract class Image : IDisposable, ICloneable
    {
        internal Image()
        {
        }

        ~Image()
        {
            Dispose(false);
        }

        public abstract int Width { get; }
        public abstract int Height { get; }
        public abstract PixelFormat PixelFormat { get; }
        public abstract ImageFormat RawFormat { get; }

        public Size Size
        {
            get { return new Size(Width, Height); }
        }

        public float HorizontalResolution
        {
            get { return 96f; }
        }

        public float VerticalResolution
        {
            get { return 96f; }
        }

        public abstract object Clone();

        public abstract void Save(Stream stream, ImageFormat format);

        public void Save(string filename)
        {
            string ext = Path.GetExtension(filename);
            ImageFormat format = ImageFormat.Png;
            if (string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase))
                format = ImageFormat.Jpeg;
            else if (string.Equals(ext, ".bmp", StringComparison.OrdinalIgnoreCase))
                format = ImageFormat.Bmp;
            Save(filename, format);
        }

        public void Save(string filename, ImageFormat format)
        {
            using (FileStream fs = new FileStream(filename, FileMode.Create, FileAccess.Write))
            {
                Save(fs, format);
            }
        }

        public static Image FromFile(string filename)
        {
            return new Bitmap(filename);
        }

        public static Image FromStream(Stream stream)
        {
            return new Bitmap(stream);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected abstract void Dispose(bool disposing);
    }
}
