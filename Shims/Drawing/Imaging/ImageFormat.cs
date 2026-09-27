using System;

namespace System.Drawing.Imaging
{
    /// <summary>
    /// Image format identifiers. The Guids are the GDI+ ImageFormat values so
    /// comparisons against ImageCodecInfo.FormatID behave as on Windows.
    /// </summary>
    public sealed class ImageFormat
    {
        private static readonly ImageFormat bmp = new ImageFormat(new Guid("b96b3cab-0728-11d3-9d7b-0000f81ef32e"));
        private static readonly ImageFormat png = new ImageFormat(new Guid("b96b3caf-0728-11d3-9d7b-0000f81ef32e"));
        private static readonly ImageFormat jpeg = new ImageFormat(new Guid("b96b3cae-0728-11d3-9d7b-0000f81ef32e"));
        private static readonly ImageFormat gif = new ImageFormat(new Guid("b96b3cb0-0728-11d3-9d7b-0000f81ef32e"));

        private readonly Guid guid;

        public ImageFormat(Guid guid)
        {
            this.guid = guid;
        }

        public Guid Guid
        {
            get { return guid; }
        }

        public static ImageFormat Bmp { get { return bmp; } }
        public static ImageFormat Png { get { return png; } }
        public static ImageFormat Jpeg { get { return jpeg; } }
        public static ImageFormat Gif { get { return gif; } }

        public override bool Equals(object o)
        {
            ImageFormat other = o as ImageFormat;
            return other != null && other.guid == guid;
        }

        public override int GetHashCode()
        {
            return guid.GetHashCode();
        }

        public override string ToString()
        {
            if (this == png) return "Png";
            if (this == jpeg) return "Jpeg";
            if (this == bmp) return "Bmp";
            if (this == gif) return "Gif";
            return "[ImageFormat: " + guid + "]";
        }
    }
}
