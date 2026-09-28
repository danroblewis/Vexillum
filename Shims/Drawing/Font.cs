using System;

namespace System.Drawing
{
    [Flags]
    public enum FontStyle
    {
        Regular = 0,
        Bold = 1,
        Italic = 2,
        Underline = 4,
        Strikeout = 8
    }

    public enum GraphicsUnit
    {
        World = 0,
        Display = 1,
        Pixel = 2,
        Point = 3,
        Inch = 4,
        Document = 5,
        Millimeter = 6
    }

    /// <summary>
    /// Descriptive stub of a GDI+ font: it records the family name, size and
    /// style (ChatPanel creates one) but never rasterises anything; text in the
    /// game is drawn with XNA SpriteFonts.
    /// </summary>
    public sealed class Font : IDisposable, ICloneable
    {
        private readonly string name;
        private readonly float size;
        private readonly FontStyle style;
        private readonly GraphicsUnit unit;

        public Font(string familyName, float emSize)
            : this(familyName, emSize, FontStyle.Regular, GraphicsUnit.Point)
        {
        }

        public Font(string familyName, float emSize, FontStyle style)
            : this(familyName, emSize, style, GraphicsUnit.Point)
        {
        }

        public Font(string familyName, float emSize, FontStyle style, GraphicsUnit unit)
        {
            if (emSize <= 0 || float.IsNaN(emSize) || float.IsInfinity(emSize))
                throw new ArgumentException("Value of '" + emSize + "' is not valid for 'emSize'.");
            this.name = familyName ?? "Microsoft Sans Serif";
            this.size = emSize;
            this.style = style;
            this.unit = unit;
        }

        public string Name { get { return name; } }
        public float Size { get { return size; } }
        public float SizeInPoints { get { return unit == GraphicsUnit.Pixel ? size * 72f / 96f : size; } }
        public FontStyle Style { get { return style; } }
        public GraphicsUnit Unit { get { return unit; } }
        public bool Bold { get { return (style & FontStyle.Bold) != 0; } }
        public bool Italic { get { return (style & FontStyle.Italic) != 0; } }
        public bool Underline { get { return (style & FontStyle.Underline) != 0; } }
        public bool Strikeout { get { return (style & FontStyle.Strikeout) != 0; } }

        /// <summary>Line spacing in pixels at 96 dpi (GDI+ Font.Height is an int).</summary>
        public int Height
        {
            get { return (int)Math.Ceiling(GetHeight()); }
        }

        public float GetHeight()
        {
            float pixels = unit == GraphicsUnit.Pixel ? size : size * 96f / 72f;
            return pixels * 1.2f;
        }

        public object Clone()
        {
            return new Font(name, size, style, unit);
        }

        public void Dispose()
        {
        }

        public override string ToString()
        {
            return "[Font: Name=" + name + ", Size=" + size + ", Units=" + (int)unit + ", GdiCharSet=1, GdiVerticalFont=False]";
        }
    }
}
