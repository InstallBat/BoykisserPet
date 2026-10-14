using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Reflection;
using Microsoft.Win32;

namespace BoykisserPet;

/// <summary>
/// Owns the pets, their overlay windows, the shared 60 Hz timer, the tray icon
/// and the global hotkeys. One window per pet keeps per-pixel alpha and
/// click-through isolated to that pet's bounds.
/// </summary>
public sealed class PetAppContext : ApplicationContext
{
    private readonly PetSettings _settings;
    private readonly List<PetRuntime> _pets = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly System.Windows.Forms.Timer _timer;
    private readonly NotifyIcon _tray;
    private HotkeyWindow? _hotkeys;
    private readonly SystemReactions _systemReactions;

    private double _lastTickMs;
    private bool _fullscreenActive;
    private double _lastFullscreenCheckMs = -10000;
    private int _nextPetIndex;
    private bool _shuttingDown;
    private double _lastSystemCheckMs = -15_000;
    private double _lastLowBatteryAlertMs = -1_000_000;
    private DateOnly? _lastNightAlertDate;

    private sealed class PetRuntime
    {
        public required Pet Pet { get; init; }
        public required OverlayWindow Window { get; init; }
        public bool PressActive;
        public Point PressOrigin;
        public bool PressZone;
    }

    public PetAppContext()
    {
        _settings = PetSettings.Load();
        _systemReactions = new SystemReactions();

        if (!_settings.FirstRunCompleted)
        {
            using var setup = new FirstRunForm(_settings.Pets.FirstOrDefault()?.Name ?? string.Empty, _settings.LaunchAtStartup);
            if (setup.ShowDialog() == DialogResult.OK)
            {
                if (_settings.Pets.Count == 0) _settings.Pets.Add(new PetConfig());
                _settings.Pets[0].Name = setup.PetName;
                _settings.LaunchAtStartup = setup.LaunchAtStartup;
                _settings.FirstRunCompleted = true;
                StartupRegistration.SetEnabled(_settings.LaunchAtStartup);
                _settings.Save();
            }
            else
            {
                // Closing the dialog still completes setup with a usable default name,
                // so the welcome screen will not trap the user on every launch.
                if (_settings.Pets.Count == 0) _settings.Pets.Add(new PetConfig());
                if (string.IsNullOrWhiteSpace(_settings.Pets[0].Name)) _settings.Pets[0].Name = "Бойкиссер";
                _settings.FirstRunCompleted = true;
                _settings.Save();
            }
        }

        // Keep Windows startup registration in sync with the persisted setting.
        StartupRegistration.SetEnabled(_settings.LaunchAtStartup);

        foreach (PetConfig config in _settings.Pets)
            SpawnPet(config);

        _tray = BuildTrayIcon();
        _hotkeys = new HotkeyWindow(TogglePause, TalkToAll, RollAll, AddPet, ShowSettings);

        _timer = new System.Windows.Forms.Timer { Interval = 16 };
        _timer.Tick += OnTick;
        _timer.Start();

        _lastTickMs = NowMs;
    }

    private double NowMs => _clock.Elapsed.TotalMilliseconds;

    // ------------------------------------------------------------------ //
    // Pet lifecycle
    // ------------------------------------------------------------------ //

    private void SpawnPet(PetConfig config)
    {
        Rectangle area = Native.WorkAreaOf(new Point(0, 0));
        var pet = new Pet(_nextPetIndex++, config, _settings, area, NowMs);
        pet.ApplySettings();

        var window = new OverlayWindow(pet.WindowW, pet.WindowH)
        {
            Bounds = new Rectangle((int)pet.X, (int)pet.Y, pet.WindowW, pet.WindowH)
        };

        var runtime = new PetRuntime { Pet = pet, Window = window };
        AttachInput(runtime);

        window.Show();
        window.SetClickThrough(true);
        _pets.Add(runtime);

        pet.Say(NowMs, Phrases.GreetDeck.Next());
    }

    private void AttachInput(PetRuntime runtime)
    {
        OverlayWindow window = runtime.Window;
        Pet pet = runtime.Pet;

        window.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            Point screen = Cursor.Position;
            runtime.PressActive = true;
            window.Capture = true;
            runtime.PressOrigin = screen;
            runtime.PressZone = pet.IsTopZone(screen);

            pet.BeginPress(NowMs);
            pet.SetPressZone(runtime.PressZone);
            pet.OnPointerDown(screen, NowMs);
            if (!runtime.PressZone) pet.OnGrabbed(NowMs);
        };

        window.MouseMove += (_, _) =>
        {
            if (!runtime.PressActive) return;
            Point screen = Cursor.Position;
            float dx = screen.X - runtime.PressOrigin.X;
            float dy = screen.Y - runtime.PressOrigin.Y;
            pet.NoteDragDistance(MathF.Sqrt(dx * dx + dy * dy));
            pet.OnPointerMove(screen, NowMs);
        };

        window.MouseUp += (_, e) =>
        {
            if (e.Button != MouseButtons.Left || !runtime.PressActive) return;
            runtime.PressActive = false;
            window.Capture = false;
            pet.OnPointerUp(Cursor.Position, NowMs);
        };

        // Losing the mouse (e.g. a click-through toggle mid-drag) must not
        // leave the pet stuck to the cursor.
        window.MouseLeave += (_, _) =>
        {
            if (!runtime.PressActive || runtime.PressZone) return;
            if (!CursorOverPet(runtime, Cursor.Position)) return;
        };

        window.MouseWheel += (_, e) => CycleSize(pet, e.Delta);

        window.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                ShowPetMenu(runtime, Cursor.Position);
        };
    }

    private static bool CursorOverPet(PetRuntime runtime, Point screen)
        => runtime.Pet.HitTest(screen) || runtime.Pet.IsOverBalloon(screen);

    // ------------------------------------------------------------------ //
    // Main loop
    // ------------------------------------------------------------------ //

    private void OnTick(object? sender, EventArgs e)
    {
        double now = NowMs;
        float dt = (float)Math.Min((now - _lastTickMs) / 1000.0, 0.05);
        _lastTickMs = now;
        if (dt <= 0f) return;

        // The fullscreen probe walks the window list, so it runs about twice a
        // second instead of every frame.
        if (now - _lastFullscreenCheckMs > 500)
        {
            _lastFullscreenCheckMs = now;
            _fullscreenActive = Native.FullscreenAppActive();
        }

        if (now - _lastSystemCheckMs >= 15_000)
        {
            _lastSystemCheckMs = now;
            CheckWindowsSystemState(now);
        }

        HandleSystemReactions(now);

        foreach (PetRuntime runtime in _pets)
        {
            Pet pet = runtime.Pet;
            bool moved = pet.Update(now, dt, _fullscreenActive);

            bool overPet = CursorOverPet(runtime, Cursor.Position);
            bool busy = overPet || runtime.PressActive || pet.IsBeingDragged || pet.NeedsAnimationFrame;

            // Update input routing every tick, even when no repaint is needed.
            // Otherwise the transparent overlay can remain click-through (or
            // intercept the desktop) after the cursor moves.
            runtime.Window.SetClickThrough(!(overPet || runtime.PressActive || pet.IsBeingDragged));
            bool needsPaint = moved || pet.IsDirty || busy;
            if (!needsPaint) continue;

            using (Graphics g = Graphics.FromImage(runtime.Window.Buffer))
                pet.Draw(g, now);

            runtime.Window.Present(runtime.Window.Buffer, (int)pet.X, (int)pet.Y);
            pet.MarkClean();
        }
    }

    private void HandleSystemReactions(double now)
    {
        // Toasts are detected by a global WinEvent hook and matched against UI Automation text.
        if (_settings.NotificationReactionEnabled)
        {
            int handled = 0;
            while (handled++ < 8 && _systemReactions.TryDequeueNotification(out Rectangle toastBounds))
            {
                foreach (PetRuntime runtime in _pets)
                {
                    var petBounds = new Rectangle(
                        (int)runtime.Pet.X + (runtime.Pet.WindowW - runtime.Pet.SpriteW) / 2,
                        (int)runtime.Pet.Y + runtime.Pet.WindowH - runtime.Pet.SpriteH - 45,
                        runtime.Pet.SpriteW, runtime.Pet.SpriteH + 30);
                    if (petBounds.IntersectsWith(toastBounds))
                        runtime.Pet.OnRoll(now);
                }
            }
        }
        else
        {
            while (_systemReactions.TryDequeueNotification(out _)) { }
        }

        SystemReactions.SoundEvent? evt = _systemReactions.PollSoundAndMusic(
            _settings.LoudSoundReactionEnabled, _settings.MusicDanceEnabled, _settings.MusicDanceAlways);
        if (evt is null) return;

        if (evt.Value.ProcessId == 0 && evt.Value.ProcessName == "music")
        {
            if (_pets.Count > 0) _pets[0].Pet.SetMusicDance(true, now);
            return;
        }
        if (evt.Value.ProcessId == 0 && evt.Value.ProcessName == "music-stopped")
        {
            foreach (PetRuntime runtime in _pets) runtime.Pet.SetMusicDance(false, now);
            return;
        }

        // Sudden loud sound startles a pet only if it was asleep; cooldown/re-arm is per process.
        foreach (PetRuntime runtime in _pets)
        {
            if (runtime.Pet.IsSleeping)
            {
                runtime.Pet.OnScared(now, evt.Value.ProcessName);
                break;
            }
        }
    }

    // ------------------------------------------------------------------ //
    // Windows system integration (Win32 power status + local time)
    // ------------------------------------------------------------------ //

    private void CheckWindowsSystemState(double now)
    {
        if (_pets.Count == 0) return;
        Pet pet = _pets[0].Pet;
        DateTime localNow = DateTime.Now;

        // Warn once per 20 minutes while unplugged and at/below 20%.
        if (Native.TryGetPowerStatus(out Native.SYSTEM_POWER_STATUS power)
            && power.ACLineStatus == 0
            && power.BatteryLifePercent <= 20
            && power.BatteryLifePercent != 255
            && now - _lastLowBatteryAlertMs >= 20 * 60_000)
        {
            _lastLowBatteryAlertMs = now;
            pet.Say(now, $"Батарея почти села — осталось {power.BatteryLifePercent}%. Подключи зарядку.");
        }

        // One gentle reminder per local date during the 03:00 hour.
        var today = DateOnly.FromDateTime(localNow);
        if (localNow.Hour == 3 && _lastNightAlertDate != today)
        {
            _lastNightAlertDate = today;
            pet.Say(now, "Уже три ночи. Может, пора спать? :3");
        }
    }

    // ------------------------------------------------------------------ //
    // Actions
    // ------------------------------------------------------------------ //

    private void CycleSize(Pet pet, int delta)
    {
        int step = delta > 0 ? 10 : -10;
        pet.Config.Size = Math.Clamp(pet.Config.Size + step, PetConfig.MinSize, PetConfig.MaxSize);
        pet.Resize(NowMs);
        ResizeWindowFor(pet);
        _settings.Save();
    }

    private void ResizeWindowFor(Pet pet)
    {
        foreach (PetRuntime runtime in _pets)
        {
            if (ReferenceEquals(runtime.Pet, pet))
            {
                runtime.Window.ClientSize = new Size(pet.WindowW, pet.WindowH);
                runtime.Window.Bounds = new Rectangle((int)pet.X, (int)pet.Y, pet.WindowW, pet.WindowH);
            }
        }
    }

    private void TalkToAll()
    {
        foreach (PetRuntime r in _pets) r.Pet.OnTalk(NowMs);
    }

    private void RollAll()
    {
        foreach (PetRuntime r in _pets) r.Pet.OnRoll(NowMs);
    }

    private void TogglePause()
    {
        _settings.Enabled = !_settings.Enabled;
        foreach (PetRuntime r in _pets) r.Pet.HideBubble();
        RebuildPets();
        _settings.Save();
        UpdateTrayText();
    }

    private void AddPet()
    {
        if (_settings.Pets.Count >= PetConfig.MaxPets)
        {
            _tray.ShowBalloonTip(2000, "Boykisser Pet",
                $"That's the limit — {PetConfig.MaxPets} pets.", ToolTipIcon.Info);
            return;
        }

        var config = new PetConfig();
        _settings.Pets.Add(config);
        SpawnPet(config);
        // Lift the newcomer slightly so it doesn't spawn directly on top of
        // the previous pet when both default to the same corner.
        PetRuntime last = _pets[^1];
        last.Pet.Y -= (_pets.Count - 1) * 24;
        _settings.Save();
        UpdateTrayText();
    }

    private void RemovePet()
    {
        if (_pets.Count <= 1) return;

        PetRuntime last = _pets[^1];
        _pets.RemoveAt(_pets.Count - 1);
        _settings.Pets.RemoveAt(_settings.Pets.Count - 1);
        last.Window.Hide();
        last.Window.Dispose();
        last.Pet.Dispose();
        _settings.Save();
        UpdateTrayText();
    }

    private void RebuildPets()
    {
        foreach (PetRuntime r in _pets)
        {
            r.Pet.ApplySettings();
            r.Window.Visible = _settings.Enabled;
        }
    }

    private void RestartAll()
    {
        foreach (PetRuntime r in _pets)
        {
            r.Window.Hide();
            r.Window.Dispose();
            r.Pet.Dispose();
        }
        _pets.Clear();
        _nextPetIndex = 0;

        foreach (PetConfig config in _settings.Pets)
            SpawnPet(config);

        _settings.Save();
        UpdateTrayText();
    }

    // ------------------------------------------------------------------ //
    // Menus
    // ------------------------------------------------------------------ //

    private void ShowPetMenu(PetRuntime runtime, Point screen)
    {
        var menu = new ContextMenuStrip();

        string petName = string.IsNullOrWhiteSpace(runtime.Pet.Config.Name)
            ? $"Бойкиссер {runtime.Pet.Index + 1}"
            : runtime.Pet.Config.Name;
        var header = new ToolStripMenuItem(petName) { Enabled = false };
        menu.Items.Add(header);
        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Поговорить", null, (_, _) => runtime.Pet.OnTalk(NowMs));
        menu.Items.Add("Погладить", null, (_, _) => runtime.Pet.OnPet(NowMs));
        menu.Items.Add("Покормить", null, (_, _) => runtime.Pet.OnFeed(NowMs));
        menu.Items.Add("Танцевать (7 секунд)", null, (_, _) => runtime.Pet.StartDance(NowMs));
        menu.Items.Add("Кувыркнуться", null, (_, _) => runtime.Pet.OnRoll(NowMs));
        menu.Items.Add("Разбудить", null, (_, _) => runtime.Pet.WakeFromMenu(NowMs));

        var sizeMenu = new ToolStripMenuItem("Размер");
        foreach (int size in new[] { 80, 110, 140, 180, 240, 320 })
        {
            int captured = size;
            var item = new ToolStripMenuItem($"{size} px", null, (_, _) =>
            {
                runtime.Pet.Config.Size = captured;
                runtime.Pet.Resize(NowMs);
                ResizeWindowFor(runtime.Pet);
                _settings.Save();
            })
            { Checked = runtime.Pet.Config.Size == size };
            sizeMenu.DropDownItems.Add(item);
        }
        menu.Items.Add(sizeMenu);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Настройки…", null, (_, _) => ShowSettings()));
        menu.Items.Add(new ToolStripMenuItem("Добавить питомца", null, (_, _) => AddPet()));
        if (_pets.Count > 1)
            menu.Items.Add(new ToolStripMenuItem("Убрать этого питомца", null, (_, _) => RemovePet()));

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem(_settings.Enabled ? "Пауза (скрыть питомцев)" : "Продолжить", null, (_, _) => TogglePause()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Выход", null, (_, _) => Shutdown()));

        menu.Show(screen);
    }

    private NotifyIcon BuildTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Поговорить со всеми", null, (_, _) => TalkToAll());
        menu.Items.Add("Кувыркнуться всем", null, (_, _) => RollAll());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Добавить питомца", null, (_, _) => AddPet());
        menu.Items.Add("Убрать питомца", null, (_, _) => RemovePet());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Настройки…", null, (_, _) => ShowSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Shutdown());

        var icon = new NotifyIcon
        {
            Icon = BuildTrayBitmap(),
            ContextMenuStrip = menu,
            Text = "Boykisser Pet",
            Visible = true
        };
        return icon;
    }

    private void UpdateTrayText()
    {
        _tray.Text = _pets.Count == 1 ? "Boykisser Pet" : $"Boykisser Pet ({_pets.Count})";
    }

    /// <summary>
    /// Draws the tray glyph at runtime: shipping a .ico file would mean another
    /// asset to keep in sync, and the pet's own sprite reads better than a
    /// generic icon anyway.
    /// </summary>
    private static Icon BuildTrayBitmap()
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var body = new SolidBrush(Color.FromArgb(255, 110, 118, 160));
            using var ear = new SolidBrush(Color.FromArgb(255, 110, 118, 160));
            using var innerEar = new SolidBrush(Color.FromArgb(255, 226, 160, 176));

            g.FillEllipse(ear, 5, 4, 9, 9);
            g.FillEllipse(ear, 18, 4, 9, 9);
            g.FillEllipse(innerEar, 7, 6, 5, 5);
            g.FillEllipse(innerEar, 20, 6, 5, 5);

            g.FillEllipse(body, 4, 10, 24, 19);

            using var eye = new SolidBrush(Color.FromArgb(255, 32, 36, 44));
            g.FillEllipse(eye, 10, 17, 3, 4);
            g.FillEllipse(eye, 19, 17, 3, 4);

            using var nose = new SolidBrush(Color.FromArgb(255, 226, 140, 158));
            g.FillEllipse(nose, 14, 23, 4, 3);
        }

        IntPtr handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            // The cloned Icon owns its own copy, so the GDI handle is safe to free.
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private SettingsForm? _settingsForm;

    private void ShowSettings()
    {
        if (_settingsForm is { Visible: true })
        {
            _settingsForm.Activate();
            return;
        }

        _settingsForm = new SettingsForm(_settings, pets =>
        {
            foreach (PetRuntime r in _pets) r.Pet.ApplySettings();
            StartupRegistration.SetEnabled(_settings.LaunchAtStartup);
            _settings.Save();
            _fullscreenCheck();
        });
        _settingsForm.FormClosed += (_, _) => _settingsForm = null;
        _settingsForm.Show();
        _settingsForm.Activate();
    }

    private void _fullscreenCheck() => _lastFullscreenCheckMs = -10000;

    // ------------------------------------------------------------------ //
    // Shutdown
    // ------------------------------------------------------------------ //

    private void Shutdown()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;

        _timer.Stop();
        _systemReactions.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _hotkeys?.Dispose();

        foreach (PetRuntime r in _pets)
        {
            r.Window.Hide();
            r.Window.Dispose();
            r.Pet.Dispose();
        }
        _pets.Clear();

        // Remember where each pet was left.
        _settings.Save();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray.Dispose();
            _hotkeys?.Dispose();
        }
        base.Dispose(disposing);
    }
}
