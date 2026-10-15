using System.Runtime.InteropServices;
using System.Text;

namespace BoykisserPet;

/// <summary>
/// Sound playback through the Win32 MCI API. MCI is used rather than
/// System.Media.SoundPlayer because SoundPlayer cannot seek — and the plugin's
/// signature meow picks a random half of the clip every time it plays.
/// </summary>
internal static class Mci
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr hwnd);

    private static int _aliasCounter;

    public static string NextAlias(string prefix) => $"{prefix}{Interlocked.Increment(ref _aliasCounter)}";

    /// <summary>Runs an MCI command, returning the numeric result (0 = success).</summary>
    public static int Send(string command)
    {
        try { return mciSendString(command, null, 0, IntPtr.Zero); }
        catch { return -1; }
    }

    public static string Query(string command)
    {
        var sb = new StringBuilder(256);
        try
        {
            if (mciSendString(command, sb, sb.Capacity, IntPtr.Zero) != 0) return string.Empty;
            return sb.ToString().Trim();
        }
        catch { return string.Empty; }
    }

    /// <summary>Opens a clip for playback. Returns false when the file can't be opened.</summary>
    public static bool Open(string path, string alias)
    {
        // Paths are quoted so spaces in the asset folder don't break the command.
        if (Send($"open \"{path}\" type mpegvideo alias {alias}") != 0)
        {
            // Retry without an explicit type: lets MCI sniff the container.
            return Send($"open \"{path}\" alias {alias}") == 0;
        }
        return true;
    }

    public static void Close(string alias) => Send($"close {alias}");

    /// <summary>Clip length in milliseconds, or 0 when unknown.</summary>
    public static int LengthMs(string alias)
    {
        Send($"set {alias} time format milliseconds");
        string s = Query($"status {alias} length");
        return int.TryParse(s, out int ms) ? ms : 0;
    }
}

/// <summary>
/// Per-pet audio: one short meow (with the plugin's random-half behaviour) and
/// one looping purr for petting. Each pet owns its own aliases so one cat's
/// meow never cuts off another's.
/// </summary>
public sealed class PetAudio : IDisposable
{
    private readonly string? _meowPath;
    private readonly string? _purrPath;
    private readonly string? _feedPath;
    private readonly string? _rummagePath;
    private string _meowAlias = string.Empty;
    private string _purrAlias = string.Empty;
    private string _feedAlias = string.Empty;
    private bool _meowOpen;
    private bool _purrOpen;
    private bool _feedOpen;
    private string _rummageAlias = string.Empty;
    private bool _rummageOpen;
    private bool _purrPlaying;
    private int _meowDurationMs;

    /// <summary>Suppressed while a full-screen app is in front, so the pet
    /// doesn't yowl over someone's game.</summary>
    public bool Muted { get; set; }

    public PetAudio(string? meowPath, string? purrPath, string? feedPath = null, string? rummagePath = null)
    {
        _meowPath = meowPath;
        _purrPath = purrPath;
        _feedPath = feedPath;
        _rummagePath = rummagePath;
    }

    private void EnsureMeowOpen()
    {
        if (_meowOpen || _meowPath is null) return;
        _meowAlias = Mci.NextAlias("bkmeow");
        _meowOpen = Mci.Open(_meowPath, _meowAlias);
        if (_meowOpen) _meowDurationMs = Mci.LengthMs(_meowAlias);
    }

    private void EnsurePurrOpen()
    {
        if (_purrOpen || _purrPath is null) return;
        _purrAlias = Mci.NextAlias("bkpurr");
        _purrOpen = Mci.Open(_purrPath, _purrAlias);
    }

    /// <summary>
    /// Plays a randomly chosen half of the meow clip — the same variation the
    /// Android plugin produced. Returns how long the sound will run, in ms, so
    /// the caller can hold its "busy" flag for the right duration.
    /// </summary>
    public int PlayMeow(float volume = 1.0f)
    {
        if (Muted) return 0;
        EnsureMeowOpen();
        if (!_meowOpen) return 0;

        StopMeow();

        int startMs, playMs;
        if (_meowDurationMs > 0)
        {
            int half = _meowDurationMs / 2;
            if (Random.Shared.Next(2) == 0)
            {
                startMs = 0;
                playMs = Math.Max(half, 200);
            }
            else
            {
                startMs = half;
                playMs = Math.Max(_meowDurationMs - half, 200);
            }
        }
        else
        {
            startMs = 0;
            playMs = 1300;
        }

        Mci.Send($"set {_meowAlias} time format milliseconds");
        // MCI volume is 0..1000. Apply per-play volume so Talk can be softer.
        Mci.Send($"setaudio {_meowAlias} volume to {Math.Clamp((int)(volume * 1000), 0, 1000)}");
        Mci.Send($"seek {_meowAlias} to start");
        if (Mci.Send($"play {_meowAlias} from {startMs} to {startMs + playMs}") != 0)
            Mci.Send($"play {_meowAlias}"); // seeking unsupported — just play it

        return playMs;
    }

    public void StopMeow()
    {
        if (!_meowOpen) return;
        Mci.Send($"stop {_meowAlias}");
        Mci.Send($"seek {_meowAlias} to start");
    }

    private void EnsureFeedOpen()
    {
        if (_feedOpen || _feedPath is null) return;
        _feedAlias = Mci.NextAlias("bkfeed");
        _feedOpen = Mci.Open(_feedPath, _feedAlias);
    }

    /// <summary>Plays the feeding sound from the beginning.</summary>
    public void PlayFeed()
    {
        if (Muted) return;
        EnsureFeedOpen();
        if (!_feedOpen) return;
        Mci.Send($"stop {_feedAlias}");
        Mci.Send($"seek {_feedAlias} to start");
        Mci.Send($"setaudio {_feedAlias} volume to 1000");
        Mci.Send($"play {_feedAlias}");
    }

    /// <summary>Plays the rummaging sound when the pet reaches the screen edge.</summary>
    public int PlayRummage()
    {
        if (Muted || _rummagePath is null) return 900;
        if (!_rummageOpen)
        {
            _rummageAlias = Mci.NextAlias("bkrummage");
            _rummageOpen = Mci.Open(_rummagePath, _rummageAlias);
        }
        if (!_rummageOpen) return 900;
        Mci.Send($"stop {_rummageAlias}");
        Mci.Send($"seek {_rummageAlias} to start");
        Mci.Send($"setaudio {_rummageAlias} volume to 1000");
        int duration = Mci.LengthMs(_rummageAlias);
        if (Mci.Send($"play {_rummageAlias}") != 0) return 900;
        return Math.Clamp(duration, 500, 5000);
    }

    /// <summary>Starts the purr loop. Idempotent while already purring, so a
    /// continuous petting stroke never restarts the sound.</summary>
    public void StartPurr()
    {
        if (Muted || _purrPlaying) return;
        EnsurePurrOpen();
        if (!_purrOpen) return;

        Mci.Send($"seek {_purrAlias} to start");
        if (Mci.Send($"play {_purrAlias} repeat") == 0) _purrPlaying = true;
    }

    public void StopPurr()
    {
        if (!_purrPlaying) return;
        _purrPlaying = false;
        if (_purrOpen) Mci.Send($"stop {_purrAlias}");
    }

    public void Dispose()
    {
        try
        {
            if (_meowOpen) { Mci.Send($"stop {_meowAlias}"); Mci.Close(_meowAlias); }
            if (_purrOpen) { Mci.Send($"stop {_purrAlias}"); Mci.Close(_purrAlias); }
            if (_feedOpen) { Mci.Send($"stop {_feedAlias}"); Mci.Close(_feedAlias); }
            if (_rummageOpen) { Mci.Send($"stop {_rummageAlias}"); Mci.Close(_rummageAlias); }
        }
        catch { /* shutting down; nothing useful to do */ }
        _meowOpen = _purrOpen = _feedOpen = _rummageOpen = _purrPlaying = false;
    }
}
