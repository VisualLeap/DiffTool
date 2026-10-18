using System;

namespace DiffTool
{
    internal static class WheelScroll
    {
        // Both panes apply input directly and retain fractional pixels. Adding a
        // second animation can lag behind trackpad/Remote Desktop gestures.
        public static double GetOffset(double offset, double maximum, int delta, int scrollLines, double lineHeight, double pageHeight)
        {
            double distance = scrollLines < 0 ? pageHeight : scrollLines * lineHeight;
            return Math.Max(0, Math.Min(maximum, offset - delta / 120.0 * distance));
        }
    }
}
