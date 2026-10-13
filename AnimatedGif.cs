using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BoykisserPet;

public sealed class GifFrame
{
    public Bitmap Bitmap { get; }
    public int DelayMs { get; }

    public GifFrame(Bitmap bitmap, int delayMs)
    {
        Bitmap = bitmap;
        DelayMs = delayMs;
    }
}

/// <summary>
/// A decoded, pre-scaled animation. All frames are decoded up front at the
/// target pixel size so the render loop never touches the decoder — drawing a
/// sprite then costs a single DrawImage with no allocation.
/// </summary>
public sealed class AnimatedGif : IDisposable
{
    public GifFrame[] Frames { get; }
    public int TotalMs { get; }
    public int Width { get; }
    public int Height { get; }

    /// <summary>A single-frame GIF (like the sleep sprite) is a static pose.</summary>
    public bool IsAnimated => Frames.Length > 1;

    /// <summary>Index of the frame visible at <paramref name="timeMs"/> into the loop.</summary>
    public int FrameAt(double timeMs)
    {
        if (!IsAnimated) return 0;
        double t = timeMs % TotalMs;
        if (t < 0) t += TotalMs;
        double acc = 0;
        for (int i = 0; i < Frames.Length; i++)
        {
            acc += Frames[i].DelayMs;
            if (t < acc) return i;
        }
        return Frames.Length - 1;
    }

    private AnimatedGif(GifFrame[] frames, int totalMs, int width, int height)
    {
        Frames = frames;
        TotalMs = Math.Max(1, totalMs);
        Width = width;
        Height = height;
    }

    /// <summary>
    /// Wraps already-decoded frames. Used for the horizontally mirrored copy of
    /// a sprite, which is produced by redrawing rather than re-decoding.
    /// </summary>
    public static AnimatedGif FromFrames(GifFrame[] frames, int totalMs, int width, int height)
        => new(frames, totalMs, width, height);

    public static AnimatedGif LoadFromBytes(byte[] data, int targetWidth, int targetHeight)
    {
        targetWidth = Math.Max(1, targetWidth);
        targetHeight = Math.Max(1, targetHeight);

        using var ms = new MemoryStream(data);
        using var source = Image.FromStream(ms, useEmbeddedColorManagement: false, validateImageData: false);

        var dimension = new FrameDimension(source.FrameDimensionsList[0]);
        int count = source.GetFrameCount(dimension);
        var frames = new List<GifFrame>(count);
        int total = 0;
        int[] delays = new int[count];

        for (int i = 0; i < count; i++)
        {
            source.SelectActiveFrame(dimension, i);

            int delay = 100;
            try
            {
                PropertyItem? item = source.GetPropertyItem(0x5100); // PropertyTagFrameDelay
                if (item?.Value is { Length: >= 4 } value && i * 4 + 4 <= value.Length)
                {
                    int raw = BitConverter.ToInt32(value, i * 4);
                    delay = raw > 0 ? raw * 10 : 100; // GIF stores it in 1/100 s
                }
            }
            catch
            {
                // Some encoders omit the delay chunk entirely — 100 ms is a sane default.
            }

            delays[i] = delay;
            total += delay;
            frames.Add(new GifFrame(ScaleFrame(source, targetWidth, targetHeight), delay));
        }

        return new AnimatedGif(frames.ToArray(), total, targetWidth, targetHeight);
    }

    /// <summary>
    /// Copies the currently-selected frame into a fresh ARGB bitmap at the
    /// requested size. Cloning is mandatory: the Image decoder reuses one
    /// backing store across SelectActiveFrame calls.
    /// </summary>
    private static Bitmap ScaleFrame(Image source, int width, int height)
    {
        var bmp = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.CompositingMode = CompositingMode.SourceOver;
        g.CompositingQuality = CompositingQuality.HighQuality;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        // Without this, bicubic sampling pulls in the transparent black border
        // and every sprite gets a dark halo.
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.Clear(Color.Transparent);
        g.DrawImage(source, new Rectangle(0, 0, width, height));
        return bmp;
    }

    public void Dispose()
    {
        foreach (GifFrame f in Frames) f.Bitmap.Dispose();
    }
}
