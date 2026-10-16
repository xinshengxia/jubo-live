using System;
using System.Drawing;

// Glyph bitmaps already have their final device-pixel size. Subpixel movement
// must not run them through another bicubic resampling pass on every frame.
internal static class TextBlit
{
    internal static void Draw(Graphics graphics, Bitmap bitmap, float x, float y)
    {
        graphics.DrawImageUnscaled(bitmap, (int)Math.Round(x), (int)Math.Round(y));
    }
}
