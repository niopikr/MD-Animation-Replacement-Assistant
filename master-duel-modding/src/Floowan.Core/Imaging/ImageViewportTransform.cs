namespace Floowan.Core.Imaging;

/// <summary>Fit, zoom and pan geometry shared by the image and its pixel-aligned overlays.</summary>
public sealed class ImageViewportTransform(int imageWidth, int imageHeight)
{
    public int ImageWidth { get; } = imageWidth > 0 ? imageWidth : throw new ArgumentOutOfRangeException(nameof(imageWidth));
    public int ImageHeight { get; } = imageHeight > 0 ? imageHeight : throw new ArgumentOutOfRangeException(nameof(imageHeight));
    public double Width { get; private set; }
    public double Height { get; private set; }
    public double Zoom { get; private set; } = 1;
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }
    public double FitScale => Math.Min(Width / ImageWidth, Height / ImageHeight);
    public double Scale => FitScale * Zoom;
    private double PaddingX => (Width - ImageWidth * FitScale) / 2;
    private double PaddingY => (Height - ImageHeight * FitScale) / 2;

    public void Resize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            return;
        if (width == Width && height == Height)
            return;

        var centerX = ImageWidth / 2.0;
        var centerY = ImageHeight / 2.0;
        if (Scale > 0)
        {
            centerX = ((Width / 2 - OffsetX) / Zoom - PaddingX) / FitScale;
            centerY = ((Height / 2 - OffsetY) / Zoom - PaddingY) / FitScale;
        }

        Width = width;
        Height = height;
        OffsetX = Width / 2 - (PaddingX + centerX * FitScale) * Zoom;
        OffsetY = Height / 2 - (PaddingY + centerY * FitScale) * Zoom;
        ClampPan();
    }

    public void Fit()
    {
        Zoom = 1;
        OffsetX = OffsetY = 0;
    }

    public void ActualSize()
    {
        if (FitScale > 0)
            ZoomAt(1 / FitScale, Width / 2, Height / 2);
    }

    /// <summary>Keep the image point under the pointer fixed, except when constrained at an edge.</summary>
    public void ZoomAt(double zoom, double anchorX, double anchorY)
    {
        if (FitScale <= 0 || !double.IsFinite(zoom) || !double.IsFinite(anchorX) || !double.IsFinite(anchorY))
            return;
        // Permit fit and 100% even for very small/large source images. Normal range is 10%–1600%.
        var minZoom = Math.Min(1, 0.1 / FitScale);
        var maxZoom = Math.Max(1, 16 / FitScale);
        var next = Math.Clamp(zoom, minZoom, maxZoom);
        var ratio = next / Zoom;
        OffsetX = anchorX - (anchorX - OffsetX) * ratio;
        OffsetY = anchorY - (anchorY - OffsetY) * ratio;
        Zoom = next;
        ClampPan();
    }

    public void PanBy(double dx, double dy)
    {
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || Scale <= 0)
            return;
        OffsetX += dx;
        OffsetY += dy;
        ClampPan();
    }

    private void ClampPan()
    {
        OffsetX = ClampAxis(OffsetX, Width, ImageWidth * Scale, PaddingX * Zoom);
        OffsetY = ClampAxis(OffsetY, Height, ImageHeight * Scale, PaddingY * Zoom);
    }

    private static double ClampAxis(double offset, double viewport, double image, double padding) =>
        image <= viewport
            ? (viewport - image) / 2 - padding
            : Math.Clamp(offset, viewport - image - padding, -padding);

    public bool TryMapToImage(double viewX, double viewY, out float imageX, out float imageY)
    {
        imageX = imageY = 0;
        if (Scale <= 0 || !double.IsFinite(viewX) || !double.IsFinite(viewY)
            || viewX < 0 || viewY < 0 || viewX >= Width || viewY >= Height)
            return false;
        var x = ((viewX - OffsetX) / Zoom - PaddingX) / FitScale;
        var y = ((viewY - OffsetY) / Zoom - PaddingY) / FitScale;
        if (x < 0 || y < 0 || x >= ImageWidth || y >= ImageHeight)
            return false;
        imageX = (float)x;
        imageY = (float)y;
        return true;
    }

    public (double X, double Y) MapToViewport(double imageX, double imageY) =>
        ((PaddingX + imageX * FitScale) * Zoom + OffsetX,
         (PaddingY + imageY * FitScale) * Zoom + OffsetY);
}
