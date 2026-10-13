using System.Drawing.Drawing2D;

namespace BoykisserPet;

/// <summary>
/// The pet's speech balloon: pops in with a back-out overshoot, types the text
/// out character by character, then fades. Ported from the reference exe,
/// which anchored everything to the balloon's tail tip so the animation
/// scales from the point touching the character.
/// </summary>
public sealed class SpeechBubble
{
    public string Text { get; private set; } = string.Empty;
    public double BornMs { get; private set; }
    public double UntilMs { get; private set; }
    public bool Visible { get; private set; }

    private const double AppearSeconds = 0.28;
    private const double FadeSeconds = 0.35;
    private const double TypeStartSeconds = 0.15;
    private const float CharsPerSecond = 38f;
    private const float WrapWidth = 250f;
    private const float TailHalfWidth = 9f;

    private readonly Font _font = new("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point);

    public void Say(double nowMs, string text, int holdExtraMs = 0)
    {
        Text = text;
        BornMs = nowMs;
        // Reading time scales with length, exactly like the reference: short
        // barks vanish quickly, long thoughts linger.
        double readingSeconds = 3.5 + text.Length * 0.06;
        UntilMs = nowMs + (readingSeconds + 1.0) * 1000.0 + holdExtraMs;
        Visible = true;
    }

    public void Hide() => Visible = false;

    /// <summary>Seconds for which the balloon still needs repainting.</summary>
    public bool IsAnimating(double nowMs)
    {
        if (!Visible) return false;
        if (nowMs < BornMs || nowMs > UntilMs) return false;
        double ageMs = nowMs - BornMs;
        double leftMs = UntilMs - nowMs;
        double typingDoneMs = (TypeStartSeconds + Text.Length / CharsPerSecond) * 1000.0;
        return ageMs < Math.Max(typingDoneMs, AppearSeconds * 1000.0) || leftMs < FadeSeconds * 1000.0;
    }

    /// <summary>
    /// Draws the balloon with its tail tip at (<paramref name="tipX"/>,
    /// <paramref name="tipY"/>) in buffer coordinates.
    /// </summary>
    public void Draw(Graphics g, double nowMs, float tipX, float tipY, float bandLeft, float bandWidth)
    {
        if (!Visible) return;
        if (nowMs < BornMs || nowMs > UntilMs) return;

        float age = (float)((nowMs - BornMs) / 1000.0);
        float left = (float)((UntilMs - nowMs) / 1000.0);

        float appear = Math.Clamp(age / (float)AppearSeconds, 0f, 1f);
        // Back-out easing: overshoots past 1 then settles, giving the pop.
        float t1 = appear - 1f;
        float pop = 1f + 1.70158f * t1 * t1 * t1 + 0.70158f * t1 * t1;
        float scale = Math.Max(0f, pop);

        float alpha = Math.Clamp(left / (float)FadeSeconds, 0f, 1f);
        int alphaByte = (int)(alpha * 255f);
        if (alphaByte <= 0) return;

        var wrap = new SizeF(WrapWidth, 0f);
        SizeF fullSize = g.MeasureString(Text, _font, wrap);
        float padX = 14f, padY = 10f;
        float boxW = fullSize.Width + padX * 2f;
        float boxH = fullSize.Height + padY * 2f;

        float bottom = tipY - 12f;
        float boxLeft = bandLeft + (bandWidth - boxW) / 2f;
        float boxTop = bottom - boxH;

        // Everything scales about the tail tip so the balloon grows out of the pet.
        PointF Tf(PointF p) => new(tipX + (p.X - tipX) * scale, tipY + (p.Y - tipY) * scale);

        var tl = Tf(new PointF(boxLeft, boxTop));
        var tr = Tf(new PointF(boxLeft + boxW, boxTop));
        var bl = Tf(new PointF(boxLeft, boxTop + boxH));
        var br = Tf(new PointF(boxLeft + boxW, boxTop + boxH));

        float radius = 12f * scale;

        Color fill = Color.FromArgb(alphaByte, 255, 255, 255);
        Color edge = Color.FromArgb(alphaByte, 205, 208, 215);
        Color shadow = Color.FromArgb((int)(alphaByte * 0.16f), 0, 0, 0);
        Color textColor = Color.FromArgb(alphaByte, 32, 36, 44);

        using var path = RoundedRect(new RectangleF(tl.X, tl.Y, Math.Max(1f, br.X - tl.X), Math.Max(1f, br.Y - tl.Y)), radius);

        float shadowOffset = 3f * scale;
        using (var shadowPath = RoundedRect(
            new RectangleF(tl.X - 1f, tl.Y + shadowOffset - 1f, Math.Max(1f, br.X - tl.X) + 2f, Math.Max(1f, br.Y - tl.Y) + 2f),
            radius))
        using (var shadowBrush = new SolidBrush(shadow))
        {
            g.FillPath(shadowBrush, shadowPath);
        }

        // Tail triangle, drawn before the body border so the seam is hidden.
        var tail = new[]
        {
            Tf(new PointF(tipX - TailHalfWidth, bottom - 1f)),
            Tf(new PointF(tipX + TailHalfWidth, bottom - 1f)),
            Tf(new PointF(tipX, tipY)),
        };

        using (var fillBrush = new SolidBrush(fill))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPolygon(fillBrush, tail);
            g.FillPath(fillBrush, path);
        }

        using (var edgePen = new Pen(edge, Math.Max(1f, scale)))
        {
            g.DrawPath(edgePen, path);
            g.DrawLines(edgePen, tail);
        }

        // Only draw the glyphs once the balloon is essentially full size;
        // before that they'd be squashed into an unreadable smear.
        if (scale > 0.9f)
        {
            int shown = (int)(Math.Max(0f, age - (float)TypeStartSeconds) * CharsPerSecond);
            string partial = shown >= Text.Length ? Text : Text[..Math.Max(0, shown)];
            if (partial.Length > 0)
            {
                using var textBrush = new SolidBrush(textColor);
                var textRect = new RectangleF(tl.X + padX * scale, tl.Y + padY * scale, boxW - padX * 2f, boxH - padY * 2f);
                using var format = new StringFormat { Trimming = StringTrimming.Word };
                g.DrawString(partial, _font, textBrush, textRect, format);
            }
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Max(0.1f, Math.Min(radius * 2f, Math.Min(r.Width, r.Height)));
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose() => _font.Dispose();
}
