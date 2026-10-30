using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace PartWorkflow;

// Compose only the saved cutout onto a new transparent canvas. Never sample the source background.
public static class CutoutRenderer
{
    public static Image<Rgba32> Render(Image<Rgba32> cutout, Placement p, int width, int height)
    {
        if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || !double.IsFinite(p.Width) ||
            !double.IsFinite(p.Height) || !double.IsFinite(p.Rotation) || p.Width <= 0 || p.Height <= 0)
            throw new InvalidDataException("裁片位置或尺寸无效");
        var result = new Image<Rgba32>(width, height);
        var angle = p.Rotation * Math.PI / 180;
        var cos = Math.Cos(angle); var sin = Math.Sin(angle);
        for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
        {
            var dx = x + .5 - p.X; var dy = y + .5 - p.Y;
            var u = (dx * cos + dy * sin) * (p.Flip ? -1 : 1) / p.Width * cutout.Width + cutout.Width / 2d - .5;
            var v = (-dx * sin + dy * cos) / p.Height * cutout.Height + cutout.Height / 2d - .5;
            if (u <= -1 || v <= -1 || u >= cutout.Width || v >= cutout.Height) continue;
            var ix = (int)Math.Floor(u); var iy = (int)Math.Floor(v);
            double alpha = 0, red = 0, green = 0, blue = 0;
            for (var j = 0; j < 2; j++) for (var i = 0; i < 2; i++)
            {
                var sx = ix + i; var sy = iy + j;
                if (sx < 0 || sy < 0 || sx >= cutout.Width || sy >= cutout.Height) continue;
                var c = cutout[sx, sy];
                var coverage = (i == 0 ? 1 - (u - ix) : u - ix) * (j == 0 ? 1 - (v - iy) : v - iy) * c.A;
                alpha += coverage; red += c.R * coverage; green += c.G * coverage; blue += c.B * coverage;
            }
            var a = (byte)Math.Clamp((int)Math.Round(alpha), 0, 255);
            if (a != 0) result[x,y] = new Rgba32((byte)Math.Clamp(Math.Round(red / alpha),0,255),
                (byte)Math.Clamp(Math.Round(green / alpha),0,255), (byte)Math.Clamp(Math.Round(blue / alpha),0,255), a);
        }
        return result;
    }
}
