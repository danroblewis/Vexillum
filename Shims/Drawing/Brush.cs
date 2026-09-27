using System;

namespace System.Drawing
{
    public abstract class Brush : IDisposable, ICloneable
    {
        public abstract object Clone();

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
        }
    }

    public sealed class SolidBrush : Brush
    {
        private Color color;

        public SolidBrush(Color color)
        {
            this.color = color;
        }

        public Color Color
        {
            get { return color; }
            set { color = value; }
        }

        public override object Clone()
        {
            return new SolidBrush(color);
        }
    }

    /// <summary>The GDI+ stock brushes the game may name; each is a shared SolidBrush.</summary>
    public static class Brushes
    {
        private static readonly Brush white = new SolidBrush(Color.White);
        private static readonly Brush black = new SolidBrush(Color.Black);
        private static readonly Brush transparent = new SolidBrush(Color.Transparent);
        private static readonly Brush red = new SolidBrush(Color.Red);
        private static readonly Brush green = new SolidBrush(Color.Green);
        private static readonly Brush blue = new SolidBrush(Color.Blue);
        private static readonly Brush yellow = new SolidBrush(Color.Yellow);
        private static readonly Brush gray = new SolidBrush(Color.Gray);
        private static readonly Brush lightGray = new SolidBrush(Color.LightGray);
        private static readonly Brush darkGray = new SolidBrush(Color.DarkGray);

        public static Brush White { get { return white; } }
        public static Brush Black { get { return black; } }
        public static Brush Transparent { get { return transparent; } }
        public static Brush Red { get { return red; } }
        public static Brush Green { get { return green; } }
        public static Brush Blue { get { return blue; } }
        public static Brush Yellow { get { return yellow; } }
        public static Brush Gray { get { return gray; } }
        public static Brush LightGray { get { return lightGray; } }
        public static Brush DarkGray { get { return darkGray; } }
    }
}
