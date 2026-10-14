using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoykisserPet;

/// <summary>
/// Persisted configuration. Mirrors the plugin's settings surface (per-pet
/// size/opacity/position plus shared behaviour toggles and cadence presets),
/// and adds the wandering options that have no Android counterpart.
/// </summary>
public sealed class PetSettings
{
    // ---- cadence presets, ported verbatim from the plugin ----
    public static readonly string[] SpeedPresetLabels =
    {
        "Very often", "Often", "Normal", "Rare", "Very rare"
    };

    public static readonly (int MinSec, int MaxSec)[] IdleGifPresets =
    {
        (10, 20), (20, 35), (30, 60), (90, 150), (300, 600)
    };

    public static readonly (int MinSec, int MaxSec)[] IdleMeowPresets =
    {
        (20, 35), (40, 60), (60, 90), (150, 240), (400, 600)
    };

    public static readonly int[] SleepTimeoutPresetsSec = { 30, 90, 300, 900, 1800 };

    public const int DefaultPresetIndex = 2; // "Normal"

    // ---- shared behaviour toggles ----
    public bool Enabled { get; set; } = true;

    public bool TapEnabled { get; set; } = true;
    public bool TapNotificationEnabled { get; set; } = true;

    public bool PettingEnabled { get; set; } = true;
    public bool PetNotificationEnabled { get; set; } = true;

    public bool DraggingEnabled { get; set; } = true;
    public bool ThrowEnabled { get; set; } = true;
    public bool JellyBounceEnabled { get; set; } = true;

    public bool IdleGifEnabled { get; set; } = true;
    public int IdleGifPreset { get; set; } = DefaultPresetIndex;

    public bool IdleMeowEnabled { get; set; } = true;
    public int IdleMeowPreset { get; set; } = DefaultPresetIndex;

    public bool SleepEnabled { get; set; } = true;
    public int SleepTimeoutPreset { get; set; } = DefaultPresetIndex;

    // ---- autonomous roaming (new; the reference exe already rolled around) ----
    public bool WanderEnabled { get; set; } = true;
    public int WanderPreset { get; set; } = DefaultPresetIndex;

    /// <summary>How often the pet speaks up unprompted.</summary>
    public bool ChatterEnabled { get; set; } = true;
    public int ChatterPreset { get; set; } = DefaultPresetIndex;

    /// <summary>Freeze all automatic behaviour while a full-screen app is in front.</summary>
    public bool PauseOnFullscreen { get; set; } = true;

    public bool SoundsEnabled { get; set; } = true;

    /// <summary>Whether first-run setup has been completed.</summary>
    public bool FirstRunCompleted { get; set; } = false;

    /// <summary>Launch automatically when the current Windows user signs in.</summary>
    public bool LaunchAtStartup { get; set; } = false;

    // Windows-native reactions (best-effort; toast markup differs by Windows build).
    public bool NotificationReactionEnabled { get; set; } = true;
    public bool LoudSoundReactionEnabled { get; set; } = true;
    public bool MusicDanceEnabled { get; set; } = true;
    public bool MusicDanceAlways { get; set; } = false; // false = 80% chance, true = 100%

    /// <summary>Physics knobs, exposed because they are the most fun to tune.</summary>
    public float GravityScale { get; set; } = 1.0f;
    public float SpeedScale { get; set; } = 1.0f;

    public List<PetConfig> Pets { get; set; } = new() { new PetConfig() };

    // ---------------------------------------------------------------- //

    [JsonIgnore]
    private static string SettingsPath
    {
        get
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BoykisserPet");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static PetSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                PetSettings? loaded = JsonSerializer.Deserialize<PetSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    if (loaded.Pets.Count == 0) loaded.Pets.Add(new PetConfig());
                    loaded.Clamp();
                    return loaded;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable config — fall back to defaults rather than
            // refusing to start, and let the next save overwrite it.
        }
        return new PetSettings();
    }

    public void Save()
    {
        try
        {
            Clamp();
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // A failed save must never take the pet down.
        }
    }

    public void Clamp()
    {
        IdleGifPreset = Math.Clamp(IdleGifPreset, 0, IdleGifPresets.Length - 1);
        IdleMeowPreset = Math.Clamp(IdleMeowPreset, 0, IdleMeowPresets.Length - 1);
        SleepTimeoutPreset = Math.Clamp(SleepTimeoutPreset, 0, SleepTimeoutPresetsSec.Length - 1);
        WanderPreset = Math.Clamp(WanderPreset, 0, IdleGifPresets.Length - 1);
        ChatterPreset = Math.Clamp(ChatterPreset, 0, IdleGifPresets.Length - 1);
        GravityScale = Math.Clamp(GravityScale, 0.2f, 3.0f);
        SpeedScale = Math.Clamp(SpeedScale, 0.2f, 3.0f);

        if (Pets.Count == 0) Pets.Add(new PetConfig());
        if (Pets.Count > PetConfig.MaxPets) Pets.RemoveRange(PetConfig.MaxPets, Pets.Count - PetConfig.MaxPets);
        foreach (PetConfig p in Pets) p.Clamp();
    }

    public (int MinMs, int MaxMs) IdleGifIntervalMs() => PresetToMs(IdleGifPresets, IdleGifPreset);
    public (int MinMs, int MaxMs) IdleMeowIntervalMs() => PresetToMs(IdleMeowPresets, IdleMeowPreset);
    public (int MinMs, int MaxMs) WanderIntervalMs() => PresetToMs(IdleGifPresets, WanderPreset);
    public (int MinMs, int MaxMs) ChatterIntervalMs() => PresetToMs(IdleMeowPresets, ChatterPreset);
    public int SleepTimeoutMs() => SleepTimeoutPresetsSec[Math.Clamp(SleepTimeoutPreset, 0, SleepTimeoutPresetsSec.Length - 1)] * 1000;

    private static (int, int) PresetToMs((int MinSec, int MaxSec)[] presets, int index)
    {
        (int min, int max) = presets[Math.Clamp(index, 0, presets.Length - 1)];
        return (min * 1000, max * 1000);
    }
}

/// <summary>Per-pet appearance and remembered position.</summary>
public sealed class PetConfig
{
    public const int MaxPets = 5;
    public const int MinSize = 40;
    public const int MaxSize = 400;
    public const int MinAlpha = 10;
    public const int MaxAlpha = 100;

    public string Name { get; set; } = string.Empty;
    public int Size { get; set; } = 140;
    public int AlphaPercent { get; set; } = 100;

    /// <summary>Left edge in virtual-screen pixels; null means "not placed yet".</summary>
    public int? X { get; set; }
    public int? Y { get; set; }

    public void Clamp()
    {
        Size = Math.Clamp(Size, MinSize, MaxSize);
        AlphaPercent = Math.Clamp(AlphaPercent, MinAlpha, MaxAlpha);
    }
}
