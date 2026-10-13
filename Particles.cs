using System.Drawing.Drawing2D;

namespace BoykisserPet;

public struct Particle
{
    public float X, Y;
    public float VX, VY;
    public double BornMs;
    public double LifeMs;
    public float Size;
    public Color Color;
    public float Gravity;
}

/// <summary>
/// Short-lived sprite debris — crumb bursts on a jump, dust puffs on hard
/// landings and fast rolls. Ported from the reference exe, which used these to
/// make impacts read as physical events rather than a picture sliding around.
/// </summary>
public sealed class ParticleSystem
{
    private readonly List<Particle> _items = new();

    public bool IsEmpty => _items.Count == 0;

    public void Clear() => _items.Clear();

    public void Add(Particle p) => _items.Add(p);

    /// <summary>Crumb burst upward from a point, used on pokes and hops.</summary>
    public void Burst(double nowMs, float x, float y, int count, Color color)
    {
        for (int i = 0; i < count; i++)
        {
            // Fan the pieces upward but jitter the angle, so it reads as a
            // scatter rather than a fountain.
            float angle = -MathF.PI / 2f + (Random.Shared.NextSingle() - 0.5f) * 2.4f;
            float speed = 110f + Random.Shared.NextSingle() * 170f;
            _items.Add(new Particle
            {
                X = x + (Random.Shared.NextSingle() - 0.5f) * 100f,
                Y = y,
                VX = MathF.Cos(angle) * speed,
                VY = MathF.Sin(angle) * speed,
                BornMs = nowMs,
                LifeMs = 0.7 + Random.Shared.NextSingle() * 0.4,
                Size = 2.5f + Random.Shared.NextSingle() * 3.5f,
                Color = color,
                Gravity = 520f
            });
        }
    }

    /// <summary>Dust puff around an impact, pushed along <paramref name="dirX"/>/<paramref name="dirY"/>.</summary>
    public void Dust(double nowMs, float x, float y, float dirX, float dirY, int count)
    {
        for (int i = 0; i < count; i++)
        {
            // Spread sideways off the impact normal so puffs bloom along the surface.
            float sideX = -dirY * (Random.Shared.NextSingle() - 0.5f) * 2f;
            float sideY = dirX * (Random.Shared.NextSingle() - 0.5f) * 2f;
            float scale = 60f + Random.Shared.NextSingle() * 160f;
            int shade = 120 + (int)(Random.Shared.NextSingle() * 50f);

            _items.Add(new Particle
            {
                X = x + (Random.Shared.NextSingle() - 0.5f) * 30f,
                Y = y + (Random.Shared.NextSingle() - 0.5f) * 30f,
                VX = (dirX * (0.3f + Random.Shared.NextSingle()) + sideX) * scale,
                VY = (dirY * (0.3f + Random.Shared.NextSingle()) + sideY) * scale,
                BornMs = nowMs,
                LifeMs = 0.4 + Random.Shared.NextSingle() * 0.4,
                Size = 3f + Random.Shared.NextSingle() * 5f,
                Color = Color.FromArgb(255, shade + 40, shade + 20, shade),
                Gravity = -40f
            });
        }
    }

    public void Update(double nowMs, float dt)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (nowMs - _items[i].BornMs >= _items[i].LifeMs)
            {
                _items.RemoveAt(i);
                continue;
            }

            Particle p = _items[i];
            p.VY += p.Gravity * dt;
            p.X += p.VX * dt;
            p.Y += p.VY * dt;
            _items[i] = p;
        }
    }

    public void Draw(Graphics g, double nowMs)
    {
        SmoothingMode previous = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        foreach (Particle p in _items)
        {
            float t = (float)((nowMs - p.BornMs) / p.LifeMs);
            float k = 1f - Math.Clamp(t, 0f, 1f);
            int alpha = (int)(255f * k);
            if (alpha <= 0) continue;

            float radius = p.Size * (0.4f + 0.6f * k);
            using var brush = new SolidBrush(Color.FromArgb(alpha, p.Color));
            g.FillEllipse(brush, p.X - radius, p.Y - radius, radius * 2f, radius * 2f);
        }

        g.SmoothingMode = previous;
    }
}
