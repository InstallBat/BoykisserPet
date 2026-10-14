using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.Media.Control;

namespace BoykisserPet;

/// <summary>Best-effort desktop integrations. Uses WinEvent hooks, Core Audio session meters,
/// window titles and LRCLIB metadata. Windows toast UI varies by Windows/app version.</summary>
internal sealed class SystemReactions : IDisposable
{
    private readonly ConcurrentQueue<Rectangle> _notifications = new();
    private readonly Dictionary<uint, SoundProcessState> _soundStates = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly ConcurrentDictionary<string, byte> _musicLookupStarted = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, bool> _musicLookupResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly WinEventDelegate _winEventDelegate;
    private IntPtr _hook;
    private DateTime _lastAudioPoll = DateTime.MinValue;
    private DateTime _lastWindowScan = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, DateTime> _recentNotifications = new(StringComparer.Ordinal);
    private DateTime _musicLastHeard = DateTime.MinValue;
    private string? _musicProcess;
    private string? _musicTitle;
    private bool _musicMatched;
    private GlobalSystemMediaTransportControlsSessionManager? _smtcManager;
    private DateTime _lastSmtcPoll = DateTime.MinValue;
    private int _smtcPollInFlight;
    private bool _smtcPlaying;
    private string? _smtcTrackKey;
    private string? _smtcTitle;
    private string? _smtcArtist;
    private bool? _smtcLyricsResult;
    private bool _smtcLookupPending;
    private bool _smtcStateChanged;
    private readonly Random _random = new();

    private sealed class SoundProcessState
    {
        public DateTime LastAudible = DateTime.MinValue;
        public DateTime CooldownUntil = DateTime.MinValue;
        public bool Armed = true;
        public DateTime? SilentSince;
        public float Peak;
        public string Name = "";
        public string WindowTitle = "";
    }

    public SystemReactions()
    {
        _winEventDelegate = OnWinEvent;
        _hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_SHOW, IntPtr.Zero,
            _winEventDelegate, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
    }

    public bool TryDequeueNotification(out Rectangle bounds) => _notifications.TryDequeue(out bounds);

    public SoundEvent? PollSoundAndMusic(bool soundsEnabled, bool musicEnabled, bool forceDance)
    {
        DateTime now = DateTime.UtcNow;
        if ((now - _lastAudioPoll).TotalMilliseconds < 450) return null;
        _lastAudioPoll = now;
        if ((now - _lastWindowScan).TotalMilliseconds >= 650)
        {
            _lastWindowScan = now;
            ScanNotificationWindows();
        }

        // SMTC is the authoritative source for track metadata and play/pause state.
        // Poll it asynchronously so Windows/WinRT calls never block the pet UI thread.
        if ((now - _lastSmtcPoll).TotalMilliseconds >= 400)
        {
            _lastSmtcPoll = now;
            _ = PollSmtcAsync();
        }

        List<AudioSessionSample> samples;
        try { samples = CoreAudio.ReadSessions(); }
        catch
        {
            return ConsumeSmtcState(musicEnabled, forceDance, now);
        }

        var seenProcesses = new HashSet<uint>();
        SoundEvent? soundEvent = null;
        foreach (AudioSessionSample sample in samples)
        {
            if (sample.ProcessId == 0 || sample.ProcessId == (uint)Environment.ProcessId) continue;
            seenProcesses.Add(sample.ProcessId);
            if (!_soundStates.TryGetValue(sample.ProcessId, out SoundProcessState? state))
                _soundStates[sample.ProcessId] = state = new SoundProcessState();
            state.Peak = sample.Peak;
            state.Name = sample.ProcessName;
            state.WindowTitle = sample.WindowTitle;

            // Audible means actual output, not merely an open audio session.
            if (sample.Peak >= 0.025f)
            {
                state.LastAudible = now;
                state.SilentSince = null;
                if (sample.Peak >= 0.35f && soundsEnabled && state.Armed && now >= state.CooldownUntil)
                {
                    state.Armed = false;
                    state.CooldownUntil = now.AddMinutes(_random.Next(15, 21));
                    WriteLog($"Loud audio: {sample.ProcessName} ({sample.Peak:P0}); next eligible after {state.CooldownUntil.ToLocalTime():T} and 10s silence.");
                    soundEvent ??= new SoundEvent(sample.ProcessId, sample.ProcessName, sample.WindowTitle, sample.Peak);
                }
            }
            else
            {
                state.SilentSince ??= now;
                if ((now - state.SilentSince.Value).TotalSeconds >= 10 && now >= state.CooldownUntil)
                {
                    // Cooldown expiry alone never triggers: a full quiet interval is required too.
                    state.Armed = true;
                }
            }
        }

        // A process whose audio session disappears is also considered silent.
        foreach (var pair in _soundStates)
        {
            if (seenProcesses.Contains(pair.Key)) continue;
            pair.Value.SilentSince ??= now;
            if ((now - pair.Value.SilentSince.Value).TotalSeconds >= 10 && now >= pair.Value.CooldownUntil)
                pair.Value.Armed = true;
        }
        // Remove dead/stale process states.
        foreach (uint pid in _soundStates.Where(kv => (now - kv.Value.LastAudible).TotalMinutes > 30).Select(kv => kv.Key).ToArray())
            _soundStates.Remove(pid);

        SoundEvent? smtcEvent = ConsumeSmtcState(musicEnabled, forceDance, now);
        return smtcEvent ?? soundEvent;
    }

    private async Task PollSmtcAsync()
    {
        if (Interlocked.Exchange(ref _smtcPollInFlight, 1) != 0) return;
        try
        {
            _smtcManager ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            var session = _smtcManager.GetCurrentSession();
            if (session is null)
            {
                SetSmtcState(false, null, null, null);
                return;
            }

            var playback = session.GetPlaybackInfo();
            bool playing = playback?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var props = await session.TryGetMediaPropertiesAsync();
            string title = props?.Title?.Trim() ?? "";
            string artist = props?.Artist?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(title))
            {
                SetSmtcState(playing, null, null, null);
                return;
            }
            string key = NormalizeTrackText(title) + "|" + NormalizeTrackText(artist);
            SetSmtcState(playing, key, title, artist);
        }
        catch (Exception ex)
        {
            WriteLog($"SMTC poll error: {ex.GetType().Name}: {ex.Message}");
        }
        finally { Interlocked.Exchange(ref _smtcPollInFlight, 0); }
    }

    private void SetSmtcState(bool playing, string? key, string? title, string? artist)
    {
        lock (_musicLookupResults)
        {
            bool changed = playing != _smtcPlaying || !string.Equals(key, _smtcTrackKey, StringComparison.Ordinal);
            _smtcPlaying = playing;
            _smtcTrackKey = key;
            _smtcTitle = title;
            _smtcArtist = artist;
            if (changed)
            {
                _smtcStateChanged = true;
                _smtcLyricsResult = null;
                _smtcLookupPending = false;
                WriteLog($"SMTC state: {(playing ? "Playing" : "Paused/stopped")}; track='{title ?? "(none)"}', artist='{artist ?? ""}'.");
                if (playing && key is not null)
                {
                    string query = string.IsNullOrWhiteSpace(artist) ? title! : title + " " + artist;
                    string cacheKey = "smtc:" + key;
                    if (_musicLookupResults.TryGetValue(cacheKey, out bool cached))
                        _smtcLyricsResult = cached;
                    else
                    {
                        _smtcLookupPending = true;
                        _ = VerifySmtcTrackAsync(query, cacheKey);
                    }
                }
            }
        }
    }

    private async Task VerifySmtcTrackAsync(string query, string cacheKey)
    {
        await VerifyMusicTitleAsync(query, cacheKey).ConfigureAwait(false);
        lock (_musicLookupResults)
        {
            if (_musicLookupResults.TryGetValue(cacheKey, out bool result)) _smtcLyricsResult = result;
            _smtcLookupPending = false;
        }
    }

    private SoundEvent? ConsumeSmtcState(bool musicEnabled, bool forceDance, DateTime now)
    {
        lock (_musicLookupResults)
        {
            if (_musicMatched && _smtcPlaying && _smtcTrackKey is not null
                && _musicTitle is not null && _musicTitle != _smtcTrackKey)
            {
                _musicMatched = false;
                WriteLog("Music dance stopped: SMTC track changed; checking new track.");
                return SoundEvent.MusicStopped;
            }
            if (!musicEnabled || !_smtcPlaying || _smtcTrackKey is null)
            {
                bool wasDancing = _musicMatched;
                _musicMatched = false;
                _musicTitle = null;
                if (wasDancing) WriteLog("Music dance stopped: SMTC reports paused/stopped or music dancing is disabled.");
                return wasDancing ? SoundEvent.MusicStopped : null;
            }

            if (_smtcLyricsResult == true && !_musicMatched)
            {
                // ForceDance means 100% probability; otherwise preserve the optional chance.
                if (forceDance || _random.NextDouble() < 0.80)
                {
                    _musicMatched = true;
                    _musicTitle = _smtcTrackKey;
                    WriteLog($"Music dance started: SMTC + LRCLIB lyrics found for '{_smtcTitle} — {_smtcArtist}'.");
                    return SoundEvent.MusicStarted;
                }
                _smtcLyricsResult = false;
                WriteLog($"LRCLIB lyrics found, but dance chance did not trigger for '{_smtcTitle}'.");
            }
            else if (_smtcTrackKey != _musicTitle && _musicMatched)
            {
                _musicMatched = false;
                return SoundEvent.MusicStopped;
            }
            return null;
        }
    }

    private async Task VerifyMusicTitleAsync(string windowTitle, string cacheKey)
    {
        if (!_musicLookupStarted.TryAdd(cacheKey, 0)) return;
        try
        {
            string query = windowTitle.Contains("|", StringComparison.Ordinal) ? CleanMediaTitle(windowTitle) : windowTitle.Trim();
            if (query.Length < 3)
            {
                _musicLookupResults[cacheKey] = false;
                return;
            }

            // LRCLIB search is requested once per title. Cache the result for quick resume/replay.
            string url = "https://lrclib.net/api/search?q=" + Uri.EscapeDataString(query);
            using var response = await _http.GetAsync(url).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _musicLookupResults[cacheKey] = false;
                WriteLog($"LRCLIB lookup failed ({(int)response.StatusCode}) for '{query}'.");
                return;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using JsonDocument doc = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
            bool matched = false;
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                string normalizedQuery = NormalizeTrackText(query);
                foreach (JsonElement item in doc.RootElement.EnumerateArray())
                {
                    string trackName = item.TryGetProperty("trackName", out JsonElement track) ? track.GetString() ?? "" : "";
                    string artistName = item.TryGetProperty("artistName", out JsonElement artist) ? artist.GetString() ?? "" : "";
                    string plainLyrics = item.TryGetProperty("plainLyrics", out JsonElement plain) && plain.ValueKind == JsonValueKind.String ? plain.GetString() ?? "" : "";
                    string syncedLyrics = item.TryGetProperty("syncedLyrics", out JsonElement synced) && synced.ValueKind == JsonValueKind.String ? synced.GetString() ?? "" : "";
                    if (string.IsNullOrWhiteSpace(plainLyrics) && string.IsNullOrWhiteSpace(syncedLyrics)) continue;
                    string normalizedTrack = NormalizeTrackText(trackName);
                    string normalizedArtist = NormalizeTrackText(artistName);
                    bool titleRelevant = normalizedQuery.Contains(normalizedTrack, StringComparison.OrdinalIgnoreCase)
                        || normalizedTrack.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                        || (!string.IsNullOrEmpty(normalizedArtist) && normalizedQuery.Contains(normalizedArtist, StringComparison.OrdinalIgnoreCase)
                            && (normalizedQuery.Contains(normalizedTrack, StringComparison.OrdinalIgnoreCase)
                                || normalizedTrack.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)));
                    if (trackName.Length > 1 && titleRelevant)
                    {
                        matched = true;
                        break;
                    }
                }
            }
            _musicLookupResults[cacheKey] = matched;
            WriteLog($"LRCLIB title/lyrics check: '{query}' => {(matched ? "lyrics found" : "no matching lyrics")}. ");
        }
        catch (Exception ex)
        {
            _musicLookupResults[cacheKey] = false;
            WriteLog($"LRCLIB lookup error for '{CleanMediaTitle(windowTitle)}': {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static string NormalizeTrackText(string value)
    {
        var chars = value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }

    private static bool IsLikelyMusicSession(string processName, string title)
    {
        string process = processName.ToLowerInvariant();
        bool knownMediaProcess = process.Contains("spotify") || process.Contains("music")
            || process.Contains("vlc") || process.Contains("foobar") || process.Contains("aimp")
            || process.Contains("chrome") || process.Contains("msedge") || process.Contains("firefox")
            || process.Contains("opera") || process.Contains("brave") || process.Contains("vivaldi")
            || process.Contains("yandex") || process.Contains("browser");
        return knownMediaProcess || (!string.IsNullOrWhiteSpace(title) && LooksLikeMediaTitle(title));
    }

    private static bool LooksLikeMediaTitle(string title)
    {
        string t = title.ToLowerInvariant();
        if (t.Length < 5 || t is "settings" or "file explorer" or "desktop") return false;
        return t.Contains("youtube") || t.Contains("spotify") || t.Contains("soundcloud") || t.Contains("music")
            || t.Contains(" - ") || t.Contains(" | ") || t.Contains("♫") || t.Contains("▶");
    }

    private static string CleanMediaTitle(string title)
    {
        string t = title.Trim();
        string[] suffixes = { " - YouTube Music", " — YouTube Music", " - YouTube", " — YouTube", " | Spotify", " - Spotify", " - Google Chrome", " - Chrome", " - Microsoft Edge", " - Mozilla Firefox", " - Opera", " - Brave", " - Vivaldi" };
        bool changed;
        do
        {
            changed = false;
            foreach (string suffix in suffixes)
            {
                if (!t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
                t = t[..^suffix.Length].Trim();
                changed = true;
                break;
            }
        } while (changed);
        return t;
    }

    private static bool IsOurProcess(string name) => name.Equals("BoykisserPet", StringComparison.OrdinalIgnoreCase);

    private void ScanNotificationWindows()
    {
        try
        {
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd)) return true;
                    var rect = new RECT();
                    if (!GetWindowRect(hwnd, out rect)) return true;
                    Rectangle bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
                    if (bounds.Width < 160 || bounds.Height < 45 || bounds.Width > 900 || bounds.Height > 700) return true;
                    var cls = new System.Text.StringBuilder(256);
                    GetClassName(hwnd, cls, cls.Capacity);
                    string className = cls.ToString();
                    string title = ReadWindowText(hwnd);
                    string preliminary = (title + " " + className).ToLowerInvariant();
                    bool toastClass = className.Contains("Toast", StringComparison.OrdinalIgnoreCase)
                        || className.Contains("Notification", StringComparison.OrdinalIgnoreCase)
                        || className.Contains("XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase)
                        || preliminary.Contains("notification") || preliminary.Contains("уведом");
                    // UI Automation is relatively expensive; only inspect plausible toast windows.
                    string content = preliminary;
                    if (!toastClass && title.Length == 0) return true;
                    if (!preliminary.Contains("telegram") && !preliminary.Contains("ayugram") && !preliminary.Contains("ayu gram"))
                        content += " " + ReadAutomationText(hwnd).ToLowerInvariant();
                    bool toastLike = toastClass || content.Contains("notification") || content.Contains("уведом")
                        || content.Contains("telegram") || content.Contains("ayugram") || content.Contains("ayu gram");
                    bool targetApp = content.Contains("telegram") || content.Contains("ayugram") || content.Contains("ayu gram");
                    if (toastLike && targetApp) QueueNotification(bounds, content);
                }
                catch { }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
    }

    private void QueueNotification(Rectangle bounds, string content)
    {
        string key = $"{bounds.Left}:{bounds.Top}:{bounds.Width}:{bounds.Height}:{content.Trim()}";
        DateTime now = DateTime.UtcNow;
        if (_recentNotifications.TryGetValue(key, out DateTime last) && (now - last).TotalSeconds < 4) return;
        _recentNotifications[key] = now;
        foreach (string stale in _recentNotifications.Where(p => (now - p.Value).TotalSeconds > 20).Select(p => p.Key).ToArray())
            _recentNotifications.TryRemove(stale, out _);
        _notifications.Enqueue(bounds);
        WriteLog($"Notification candidate: {content.Trim()} at {bounds}.");
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            if (hwnd == IntPtr.Zero || objectId != 0 || childId != 0 || !IsWindowVisible(hwnd)) return;
            var rect = new RECT();
            if (!GetWindowRect(hwnd, out rect)) return;
            Rectangle bounds = Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
            if (bounds.Width < 160 || bounds.Height < 45 || bounds.Width > 900 || bounds.Height > 700) return;
            var cls = new System.Text.StringBuilder(256);
            GetClassName(hwnd, cls, cls.Capacity);
            string className = cls.ToString();
            string title = ReadWindowText(hwnd);
            bool toastLike = className.Contains("Toast", StringComparison.OrdinalIgnoreCase)
                || className.Contains("Notification", StringComparison.OrdinalIgnoreCase)
                || className.Contains("Overflow", StringComparison.OrdinalIgnoreCase)
                || className.Contains("CoreWindow", StringComparison.OrdinalIgnoreCase)
                || className.Contains("XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase)
                || title.Contains("notification", StringComparison.OrdinalIgnoreCase)
                || title.Contains("уведом", StringComparison.OrdinalIgnoreCase);
            if (!toastLike) return;

            string content = title + " " + className + " " + ReadAutomationText(hwnd);
            string lower = content.ToLowerInvariant();
            bool targetApp = lower.Contains("telegram") || lower.Contains("ayugram") || lower.Contains("ayu gram")
                || lower.Contains("chrome") || lower.Contains("firefox") || lower.Contains("edge")
                || lower.Contains("opera") || lower.Contains("brave") || lower.Contains("vivaldi");
            if (targetApp) QueueNotification(bounds, content);
        }
        catch { }
    }

    private static string ReadAutomationText(IntPtr hwnd)
    {
        try
        {
            Type? type = Type.GetTypeFromProgID("UIAutomationClient.CUIAutomation");
            if (type is null) return "";
            dynamic automation = Activator.CreateInstance(type)!;
            dynamic element = automation.ElementFromHandle(hwnd);
            string text = "";
            try { text += " " + (string)element.CurrentName; } catch { }
            try
            {
                dynamic condition = automation.CreateTrueCondition();
                dynamic children = element.FindAll(4, condition); // TreeScope_Subtree
                int count = Math.Min((int)children.Length, 80);
                for (int i = 0; i < count; i++)
                {
                    try { text += " " + (string)children.GetElement(i).CurrentName; } catch { }
                }
            }
            catch { }
            Marshal.FinalReleaseComObject(automation);
            return text;
        }
        catch { return ""; }
    }

    private static string ReadWindowText(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static readonly object LogGate = new();
    private static void WriteLog(string message)
    {
        try
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BoykisserPet");
            Directory.CreateDirectory(dir);
            lock (LogGate) File.AppendAllText(Path.Combine(dir, "system-reactions.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) { UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        _http.Dispose();
    }

    public readonly record struct SoundEvent(uint ProcessId, string ProcessName, string WindowTitle, float Peak)
    {
        public static SoundEvent MusicStarted => new(0, "music", "", 0);
        public static SoundEvent MusicStopped => new(0, "music-stopped", "", 0);
    }

    private sealed record AudioSessionSample(uint ProcessId, string ProcessName, string WindowTitle, float Peak);

    private static class CoreAudio
    {
        public static List<AudioSessionSample> ReadSessions()
        {
            var result = new List<AudioSessionSample>();
            IMMDeviceEnumerator? enumerator = null;
            IMMDevice? device = null;
            IAudioSessionManager2? manager = null;
            IAudioSessionEnumerator? sessions = null;
            try
            {
                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
                Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device));
                Guid iid = typeof(IAudioSessionManager2).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX.ALL, IntPtr.Zero, out object managerObj));
                manager = (IAudioSessionManager2)managerObj;
                Marshal.ThrowExceptionForHR(manager.GetSessionEnumerator(out sessions));
                Marshal.ThrowExceptionForHR(sessions.GetCount(out int count));
                for (int i = 0; i < count; i++)
                {
                    IAudioSessionControl? control = null;
                    try
                    {
                        Marshal.ThrowExceptionForHR(sessions.GetSession(i, out control));
                        if (control is not IAudioSessionControl2 control2 || control is not IAudioMeterInformation meter) continue;
                        Marshal.ThrowExceptionForHR(control2.GetProcessId(out uint pid));
                        Marshal.ThrowExceptionForHR(meter.GetPeakValue(out float peak));
                        if (pid == 0) continue;
                        string name = ""; string title = "";
                        try { using Process p = Process.GetProcessById((int)pid); name = p.ProcessName; title = p.MainWindowTitle; } catch { }
                        result.Add(new AudioSessionSample(pid, name, title, peak));
                    }
                    catch { }
                    finally { if (control != null) Marshal.ReleaseComObject(control); }
                }
            }
            finally
            {
                if (sessions != null) Marshal.ReleaseComObject(sessions);
                if (manager != null) Marshal.ReleaseComObject(manager);
                if (device != null) Marshal.ReleaseComObject(device);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }
            return result;
        }

        private enum EDataFlow { Render, Capture, All }
        private enum ERole { Console, Multimedia, Communications }
        private static class CLSCTX { public const int ALL = 23; }

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] private class MMDeviceEnumerator { }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
            int RegisterEndpointNotificationCallback(IntPtr client);
            int UnregisterEndpointNotificationCallback(IntPtr client);
        }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        private interface IMMDevice
        {
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
            int OpenPropertyStore(int access, out IntPtr properties);
            int GetId(out IntPtr id);
            int GetState(out int state);
        }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
        private interface IAudioSessionManager2
        {
            int GetAudioSessionControl(IntPtr audioSessionGuid, int streamFlags, out IntPtr sessionControl);
            int GetSimpleAudioVolume(IntPtr audioSessionGuid, int streamFlags, out IntPtr audioVolume);
            int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
            int RegisterSessionNotification(IntPtr notification);
            int UnregisterSessionNotification(IntPtr notification);
            int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionId, IntPtr notification);
            int UnregisterDuckNotification(IntPtr notification);
        }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
        private interface IAudioSessionEnumerator
        {
            int GetCount(out int count);
            int GetSession(int index, out IAudioSessionControl session);
        }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD")]
        private interface IAudioSessionControl
        {
            int GetState(out int state);
            int GetDisplayName(out IntPtr name);
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetIconPath(out IntPtr path);
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetGroupingParam(out Guid groupingId);
            int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
            int RegisterAudioSessionNotification(IntPtr client);
            int UnregisterAudioSessionNotification(IntPtr client);
        }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl base vtable
            int GetState(out int state);
            int GetDisplayName(out IntPtr name);
            int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetIconPath(out IntPtr path);
            int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string value, ref Guid eventContext);
            int GetGroupingParam(out Guid groupingId);
            int SetGroupingParam(ref Guid groupingId, ref Guid eventContext);
            int RegisterAudioSessionNotification(IntPtr client);
            int UnregisterAudioSessionNotification(IntPtr client);
            // IAudioSessionControl2 additions
            int GetSessionIdentifier(out IntPtr id);
            int GetSessionInstanceIdentifier(out IntPtr id);
            int GetProcessId(out uint processId);
            int IsSystemSoundsSession();
            int SetDuckingPreference([MarshalAs(UnmanagedType.Bool)] bool optOut);
        }
        [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
        private interface IAudioMeterInformation
        {
            int GetPeakValue(out float peak);
            int GetMeteringChannelCount(out int channelCount);
            int GetChannelsPeakValues(int channelCount, IntPtr peakValues);
            int QueryHardwareSupport(out int hardwareSupportMask);
        }
    }

    private const uint EVENT_OBJECT_SHOW = 0x8002;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);
    private struct RECT { public int Left, Top, Right, Bottom; }
    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc, WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hWinEventHook);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder lpString, int nMaxCount);
}
