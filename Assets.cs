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

    public static readonly string[] IdleGifs =
    {
        "idle_random_0.gif", "idle_random_1.gif", "idle_random_2.gif"
    };

    private static readonly Assembly Asm = typeof(Assets).Assembly;
    private static readonly Dictionary<string, AnimatedGif> GifCache = new();
    private static readonly Dictionary<string, byte[]> BytesCache = new();
    private static readonly object Gate = new();

    public static byte[]? ReadBytes(string name)
    {
        lock (Gate)
        {
            if (BytesCache.TryGetValue(name, out byte[]? cached)) return cached;
        }

        string? resourceName = Asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + name, StringComparison.OrdinalIgnoreCase));
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

        lock (Gate) BytesCache[name] = buffer;
        return buffer;
    }

    /// <summary>Decoded, pre-scaled animation — or null when the asset is missing.</summary>
    public static AnimatedGif? Gif(string name, int width, int height)
    {
        string key = $"{name}@{width}x{height}";
        lock (Gate)
        {
            if (GifCache.TryGetValue(key, out AnimatedGif? cached)) return cached;
        }

        byte[]? data = ReadBytes(name);
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
    public static string? SoundPath(string name)
    {
        byte[]? data = ReadBytes(name);
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
