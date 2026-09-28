using System;

namespace System.Drawing
{
    /// <summary>
    /// Stub of the GDI+ drawing surface. The only caller in the game is the
    /// System.Drawing overload of TextRenderer.DrawFormattedString, which nothing
    /// invokes; DrawString is therefore a no-op.
    /// </summary>
    public sealed class Graphics : IDisposable
    {
        private Graphics()
        {
        }

        public static Graphics FromImage(Image image)
        {
            if (image == null)
                throw new ArgumentNullException("image");
            return new Graphics();
        }

        public void Clear(Color color)
        {
        }

        public void DrawString(string s, Font font, Brush brush, PointF point)
        {
        }

        public void DrawString(string s, Font font, Brush brush, float x, float y)
        {
        }

        public SizeF MeasureString(string text, Font font)
        {
            if (text == null || font == null)
                return SizeF.Empty;
            return new SizeF(text.Length * font.Size * 0.6f, font.GetHeight());
        }

        public void Dispose()
        {
        }
    }
}
