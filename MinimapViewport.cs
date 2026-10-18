using System;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace DiffTool
{
    internal static class MinimapViewport
    {
        public static void Draw(Graphics graphics, RectangleF bounds)
        {
            // GDI+ outline antialiasing still quantizes these axis-aligned edges.
            // Blend the edge rows by their exact coverage so fractional movement
            // remains visible, without adding an animation or changing scrolling.
            GraphicsState state = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.None;
                graphics.PixelOffsetMode = PixelOffsetMode.None;
                int left = (int)Math.Round(bounds.Left);
                int width = Math.Max(1, (int)Math.Round(bounds.Width));
                Color fill = Color.FromArgb(35, Theme.Blue);
                Color border = Color.FromArgb(150, Theme.Blue);
                FillSpan(graphics, fill, left, width, bounds.Top, bounds.Bottom);
                FillSpan(graphics, border, left, 1, bounds.Top, bounds.Bottom);
                if (width > 1) FillSpan(graphics, border, left + width - 1, 1, bounds.Top, bounds.Bottom);
                // The horizontal edges exclude the side columns to avoid double blending.
                FillSpan(graphics, border, left + 1, width - 2, bounds.Top, Math.Min(bounds.Bottom, bounds.Top + 1));
                FillSpan(graphics, border, left + 1, width - 2, Math.Max(bounds.Top + 1, bounds.Bottom - 1), bounds.Bottom);
            }
            finally { graphics.Restore(state); }
        }

        private static void FillSpan(Graphics graphics, Color color, int x, int width, float top, float bottom)
        {
            if (width <= 0 || bottom <= top) return;
            int first = (int)Math.Floor(top), last = (int)Math.Ceiling(bottom) - 1;
            if (first == last) { FillEdge(graphics, color, x, first, width, bottom - top); return; }
            FillEdge(graphics, color, x, first, width, first + 1 - top);
            if (last > first + 1)
                using (var brush = new SolidBrush(color)) graphics.FillRectangle(brush, x, first + 1, width, last - first - 1);
            FillEdge(graphics, color, x, last, width, bottom - last);
        }

        private static void FillEdge(Graphics graphics, Color color, int x, int y, int width, float coverage)
        {
            using (var brush = new SolidBrush(Color.FromArgb((int)Math.Round(color.A * coverage), color)))
                graphics.FillRectangle(brush, x, y, width, 1);
        }
    }
}
