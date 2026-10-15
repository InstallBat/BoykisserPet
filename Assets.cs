using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using System.Reflection;

namespace BoykisserPet;

/// <summary>
/// Resolves the sprite/sound assets that ship inside the executable. GIFs are
/// decoded once per (name, size) pair and cached, so five pets at the same
/// size share one set of decoded frames instead of five.
/// </summary>
public static class Assets
{
    public const string BaseGif = "boykisser.gif";
    public const string PetGif = "pet.gif";
    public const string PetMainGif = "pet_main_v2.gif";
    public const string SleepGif = "sleep.gif";
    public const string MeowMp3 = "meow.mp3";
    public const string PurrMp3 = "purr.mp3";
    public const string FeedMp3 = "nom-nom-nom.mp3";

    public static readonly string[] IdleGifs =
    {
        "idle_random_0.gif", "idle_random_1.gif", "idle_random_2.gif"
    };


    public static string[] MemeNamesForSkin(string skin) => skin.ToLowerInvariant() switch
    {
        "expie" => new[] { "mem1.webp", "mem2.webp", "mem3.webp", "mem4.webp", "mem5.webp" },
        "niko" => new[] { "mem1.webp", "mem2.webp", "mem3.webp", "mem4.webp" },
        _ => new[] { "mem1.webp", "mem2.webp", "mem3.webp", "mem4.webp", "mem5.webp", "mem6.webp", "mem7.webp" }
    };

    /// <summary>Loads an optional meme asset, returning null if it is missing or invalid.</summary>
    public static Bitmap? MemeBitmap(string name, int maxDimension, string skin = "boykisser")
    {
        byte[]? bytes = ReadBytes(name, skin);
        if (bytes is null) return null;
        try
        {
            using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(bytes);
            float scale = Math.Min(1f, maxDimension / (float)Math.Max(image.Width, image.Height));
            int width = Math.Max(1, (int)(image.Width * scale));
            int height = Math.Max(1, (int)(image.Height * scale));
            image.Mutate(x => x.Resize(width, height));
            using var output = new MemoryStream();
            image.SaveAsPng(output);
            output.Position = 0;
            using var bitmap = new Bitmap(output);
            return new Bitmap(bitmap);
        }
        catch { return null; }
    }

    private static readonly Assembly Asm = typeof(Assets).Assembly;
    private static readonly Dictionary<string, AnimatedGif> GifCache = new();
    private static readonly Dictionary<string, byte[]> BytesCache = new();
    private static readonly object Gate = new();

    public static byte[]? ReadBytes(string name, string skin = "boykisser")
    {
        string cacheKey = skin + ":" + name;
        lock (Gate)
        {
            if (BytesCache.TryGetValue(cacheKey, out byte[]? cached)) return cached;
        }

        string normalizedSkin = skin.ToLowerInvariant() switch { "niko" => "niko", "expie" => "expie", _ => "boykisser" };
        string[] names = Asm.GetManifestResourceNames();
        string? resourceName = names.FirstOrDefault(n =>
            n.Contains(".Assets." + normalizedSkin + ".", StringComparison.OrdinalIgnoreCase)
            && n.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
        // Missing optional animation? Use the selected character's base sprite, then Boykisser's matching asset.
        if (resourceName is null && name.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
            && !name.Equals(BaseGif, StringComparison.OrdinalIgnoreCase))
            resourceName = names.FirstOrDefault(n =>
                n.Contains(".Assets." + normalizedSkin + ".", StringComparison.OrdinalIgnoreCase)
                && n.EndsWith("." + BaseGif, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null)
            resourceName = names.FirstOrDefault(n => n.Contains(".Assets.boykisser.", StringComparison.OrdinalIgnoreCase)
                && n.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) return null;

        using Stream? stream = Asm.GetManifestResourceStream(resourceName);
        if (stream is null) return null;

        var buffer = new byte[stream.Length];
        int read = 0;
        while (read < buffer.Length)
        {
            int n = stream.Read(buffer, read, buffer.Length - read);
            if (n <= 0) break;
            read += n;
        }
        if (read != buffer.Length) Array.Resize(ref buffer, read);

        lock (Gate) BytesCache[cacheKey] = buffer;
        return buffer;
    }

    /// <summary>Decoded, pre-scaled animation — or null when the asset is missing.</summary>
    public static AnimatedGif? Gif(string name, int width, int height, string skin = "boykisser")
    {
        string key = $"{skin}:{name}@{width}x{height}";
        lock (Gate)
        {
            if (GifCache.TryGetValue(key, out AnimatedGif? cached)) return cached;
        }

        byte[]? data = ReadBytes(name, skin);
        if (data is null) return null;

        AnimatedGif gif;
        try
        {
            gif = AnimatedGif.LoadFromBytes(data, width, height);
        }
        catch
        {
            // A format we can't decode shouldn't be fatal — the caller simply
            // falls back to whatever sprite it already has.
            return null;
        }

        lock (Gate) GifCache[key] = gif;
        return gif;
    }

    /// <summary>
    /// Materialises an embedded sound to disk so the MCI API can open it by
    /// path. Extracted once per version, then reused.
    /// </summary>
    public static string? SoundPath(string name, string skin = "boykisser")
    {
        byte[]? data = ReadBytes(name, skin);
        if (data is null) return null;

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "BoykisserPet", "sounds");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, name);

            // Re-extract only when the on-disk copy is missing or truncated,
            // so a half-written file from an earlier crash self-heals.
            if (!File.Exists(path) || new FileInfo(path).Length != data.Length)
                File.WriteAllBytes(path, data);

            return path;
        }
        catch
        {
            return null;
        }
    }
}
