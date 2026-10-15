namespace BoykisserPet;

/// <summary>
/// Settings window. Mirrors the plugin's option groups (tap, petting, idle,
/// sleep, dragging) and adds the roaming and physics controls that only exist
/// on the desktop version.
/// </summary>
public sealed class SettingsForm : Form
{
    private readonly PetSettings _settings;
    private readonly Action<PetSettings> _onChanged;
    private readonly System.Windows.Forms.Timer _saveDebounce;

    private readonly List<Control> _petRows = new();
    private FlowLayoutPanel _petList = null!;

    public SettingsForm(PetSettings settings, Action<PetSettings> onChanged)
    {
        _settings = settings;
        _onChanged = onChanged;

        Text = "Boykisser Pet — Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(460, 640);
        Font = new Font("Segoe UI", 9.5f);

        // Persist a moment after the last edit rather than on every keystroke.
        _saveDebounce = new System.Windows.Forms.Timer { Interval = 600 };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            _settings.Save();
        };

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(BuildBehaviourTab());
        tabs.TabPages.Add(BuildPetsTab());
        tabs.TabPages.Add(BuildMotionTab());

        var hint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 48,
            Padding = new Padding(10, 6, 10, 6),
            ForeColor = SystemColors.GrayText,
            Text = "Глобальные хоткеи отключены по умолчанию. Их можно включить в настройках."
        };

        Controls.Add(tabs);
        Controls.Add(hint);
        DarkTheme.Apply(this);
    }

    private TabPage BuildBehaviourTab()
    {
        var page = new TabPage("Behaviour");
        var flow = MakeFlow();
        page.Controls.Add(flow);

        AddSectionHeader(flow, "General");

        AddCheck(flow, "Show pets", _settings.Enabled, v => _settings.Enabled = v);
        AddCheck(flow, "Sounds", _settings.SoundsEnabled, v => _settings.SoundsEnabled = v);
        AddCheck(flow, "Freaky Mod (курсор падает при касании; охота быстрее)", _settings.FreakyModEnabled, v => _settings.FreakyModEnabled = v);
        AddCheck(flow, "Локальный чат с питомцем", _settings.LocalAiEnabled, v => _settings.LocalAiEnabled = v);
        AddCheck(flow, "Включить глобальные хоткеи", _settings.GlobalHotkeysEnabled, v => _settings.GlobalHotkeysEnabled = v);
        AddCheck(flow, "Режим разработчика (ПКМ-меню)", _settings.DeveloperModeEnabled, v => _settings.DeveloperModeEnabled = v);
        AddSectionHeader(flow, "Версия");
        flow.Controls.Add(new Label { Text = "Версия приложения: 3.0.0", AutoSize = true, Padding = new Padding(4) });
        AddCheck(flow, "Pause while a full-screen app is in front",
            _settings.PauseOnFullscreen, v => _settings.PauseOnFullscreen = v);

        AddSectionHeader(flow, "Tap");
        AddCheck(flow, "React to a click (animation + sound + speech)",
            _settings.TapEnabled, v => _settings.TapEnabled = v);

        AddSectionHeader(flow, "Petting");
        AddCheck(flow, "Stroke the top half to pet", _settings.PettingEnabled, v => _settings.PettingEnabled = v);
        AddCheck(flow, "Speak while being petted",
            _settings.PetNotificationEnabled, v => _settings.PetNotificationEnabled = v);

        AddSectionHeader(flow, "Dragging");
        AddCheck(flow, "Allow dragging", _settings.DraggingEnabled, v => _settings.DraggingEnabled = v);
        AddCheck(flow, "Fling with inertia", _settings.ThrowEnabled, v => _settings.ThrowEnabled = v);
        AddCheck(flow, "Jelly wobble on impact", _settings.JellyBounceEnabled, v => _settings.JellyBounceEnabled = v);

        AddSectionHeader(flow, "Sometimes");
        AddCheck(flow, "Play a random animation now and then",
            _settings.IdleGifEnabled, v => _settings.IdleGifEnabled = v);
        AddCombo(flow, "Animation frequency", PetSettings.SpeedPresetLabels, _settings.IdleGifPreset,
            v => _settings.IdleGifPreset = v);

        AddCheck(flow, "Meow now and then", _settings.IdleMeowEnabled, v => _settings.IdleMeowEnabled = v);
        AddCombo(flow, "Meow frequency", PetSettings.SpeedPresetLabels, _settings.IdleMeowPreset,
            v => _settings.IdleMeowPreset = v);

        AddCheck(flow, "Say something now and then", _settings.ChatterEnabled, v => _settings.ChatterEnabled = v);
        AddCombo(flow, "Chatter frequency", PetSettings.SpeedPresetLabels, _settings.ChatterPreset,
            v => _settings.ChatterPreset = v);

        AddSectionHeader(flow, "Windows integrations");
        AddCheck(flow, "React to Telegram/AyuGram/browser notifications",
            _settings.NotificationReactionEnabled, v => _settings.NotificationReactionEnabled = v);
        AddCheck(flow, "Get startled by loud app audio (35% peak; 15–20 min cooldown)",
            _settings.LoudSoundReactionEnabled, v => _settings.LoudSoundReactionEnabled = v);
        AddCheck(flow, "Dance while music is playing (LRCLIB title match)",
            _settings.MusicDanceEnabled, v => _settings.MusicDanceEnabled = v);
        AddCheck(flow, "Always dance when music is detected (100% instead of 80%)",
            _settings.MusicDanceAlways, v => _settings.MusicDanceAlways = v);

        AddSectionHeader(flow, "Sleep");
        AddCheck(flow, "Fall asleep when idle", _settings.SleepEnabled, v => _settings.SleepEnabled = v);
        AddCombo(flow, "Sleep after", PetSettings.SpeedPresetLabels, _settings.SleepTimeoutPreset,
            v => _settings.SleepTimeoutPreset = v);

        return page;
    }

    private TabPage BuildPetsTab()
    {
        var page = new TabPage("Pets");
        _petList = MakeFlow();
        page.Controls.Add(_petList);

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Padding = new Padding(8) };
        var add = new Button { Text = "Add pet", Width = 100 };
        var remove = new Button { Text = "Remove last", Width = 110 };

        add.Click += (_, _) =>
        {
            if (_settings.Pets.Count >= PetConfig.MaxPets) return;
            _settings.Pets.Add(new PetConfig());
            Notify();
            RefreshPetRows();
        };
        remove.Click += (_, _) =>
        {
            if (_settings.Pets.Count <= 1) return;
            _settings.Pets.RemoveAt(_settings.Pets.Count - 1);
            Notify();
            RefreshPetRows();
        };

        buttons.Controls.Add(add);
        buttons.Controls.Add(remove);
        page.Controls.Add(buttons);

        RefreshPetRows();
        return page;
    }

    private void RefreshPetRows()
    {
        _petList.Controls.Clear();
        _petRows.Clear();

        for (int i = 0; i < _settings.Pets.Count; i++)
        {
            PetConfig config = _settings.Pets[i];
            int index = i;

            AddSectionHeader(_petList, $"Pet {i + 1}");

            var nameRow = new FlowLayoutPanel { Width = 410, Height = 34, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            nameRow.Controls.Add(new Label { Text = "Name", Width = 110, TextAlign = ContentAlignment.MiddleLeft });
            var nameBox = new TextBox { Text = config.Name, Width = 285, MaxLength = 40 };
            nameBox.TextChanged += (_, _) =>
            {
                _settings.Pets[index].Name = nameBox.Text.Trim();
                Notify();
            };
            nameRow.Controls.Add(nameBox);
            _petList.Controls.Add(nameRow);

            var skinRow = new FlowLayoutPanel { Width = 410, Height = 34, FlowDirection = FlowDirection.LeftToRight, WrapContents = false };
            skinRow.Controls.Add(new Label { Text = "Skin", Width = 110, TextAlign = ContentAlignment.MiddleLeft });
            var skinBox = new ComboBox { Width = 285, DropDownStyle = ComboBoxStyle.DropDownList };
            skinBox.Items.AddRange(new object[] { "Бойкиссер", "Нико", "Экспи" });
            skinBox.SelectedIndex = config.Skin switch { "niko" => 1, "expie" => 2, _ => 0 };
            skinBox.SelectedIndexChanged += (_, _) =>
            {
                _settings.Pets[index].Skin = skinBox.SelectedIndex switch { 1 => "niko", 2 => "expie", _ => "boykisser" };
                Notify();
            };
            skinRow.Controls.Add(skinBox);
            _petList.Controls.Add(skinRow);

            var sizeRow = new FlowLayoutPanel { Width = 410, Height = 34, FlowDirection = FlowDirection.LeftToRight };
            sizeRow.Controls.Add(new Label { Text = "Size (px)", Width = 110, TextAlign = ContentAlignment.MiddleLeft });
            var sizeValue = new Label { Text = config.Size.ToString(), Width = 42, TextAlign = ContentAlignment.MiddleRight };
            var size = new TrackBar
            {
                Minimum = PetConfig.MinSize,
                Maximum = PetConfig.MaxSize,
                TickFrequency = 40,
                Value = config.Size,
                Width = 240,
                Height = 30
            };
            size.ValueChanged += (_, _) =>
            {
                _settings.Pets[index].Size = size.Value;
                sizeValue.Text = size.Value.ToString();
                Notify();
            };
            sizeRow.Controls.Add(size);
            sizeRow.Controls.Add(sizeValue);
            _petList.Controls.Add(sizeRow);

            var alphaRow = new FlowLayoutPanel { Width = 410, Height = 34, FlowDirection = FlowDirection.LeftToRight };
            alphaRow.Controls.Add(new Label { Text = "Opacity (%)", Width = 110, TextAlign = ContentAlignment.MiddleLeft });
            var alphaValue = new Label { Text = config.AlphaPercent.ToString(), Width = 42, TextAlign = ContentAlignment.MiddleRight };
            var alpha = new TrackBar
            {
                Minimum = PetConfig.MinAlpha,
                Maximum = PetConfig.MaxAlpha,
                TickFrequency = 10,
                Value = config.AlphaPercent,
                Width = 240,
                Height = 30
            };
            alpha.ValueChanged += (_, _) =>
            {
                _settings.Pets[index].AlphaPercent = alpha.Value;
                alphaValue.Text = alpha.Value.ToString();
                Notify();
            };
            alphaRow.Controls.Add(alpha);
            alphaRow.Controls.Add(alphaValue);
            _petList.Controls.Add(alphaRow);

            _petRows.Add(nameRow);
            _petRows.Add(sizeRow);
            _petRows.Add(alphaRow);
        }
    }

    private TabPage BuildMotionTab()
    {
        var page = new TabPage("Motion");
        var flow = MakeFlow();
        page.Controls.Add(flow);

        AddSectionHeader(flow, "Roaming");
        AddCheck(flow, "Walk around the screen on its own",
            _settings.WanderEnabled, v => _settings.WanderEnabled = v);
        AddCombo(flow, "Wander frequency", PetSettings.SpeedPresetLabels, _settings.WanderPreset,
            v => _settings.WanderPreset = v);

        AddSectionHeader(flow, "Event frequency (normal mode only; Freaky Mode ignores these rates)");
        AddCheck(flow, "Cursor-hunt events enabled", _settings.CursorChaseEnabled, v => _settings.CursorChaseEnabled = v);
        AddCombo(flow, "Cursor-hunt frequency", PetSettings.SpeedPresetLabels, _settings.CursorChasePreset,
            v => _settings.CursorChasePreset = v);
        AddCheck(flow, "Bring-a-meme events enabled", _settings.MemeEventEnabled, v => _settings.MemeEventEnabled = v);
        AddCombo(flow, "Meme frequency", PetSettings.SpeedPresetLabels, _settings.MemeEventPreset,
            v => _settings.MemeEventPreset = v);

        AddSectionHeader(flow, "Cursor hit (normal mode only)");
        AddCheck(flow, "Nudge cursor down when a jump hits it",
            _settings.CursorHitBumpEnabled, v => _settings.CursorHitBumpEnabled = v);
        AddSlider(flow, "Downward nudge", 0f, 100f, _settings.CursorHitBumpStrength,
            v => _settings.CursorHitBumpStrength = (int)MathF.Round(v));

        AddSectionHeader(flow, "Physics");
        AddSlider(flow, "Gravity", 0.2f, 3.0f, _settings.GravityScale,
            v => _settings.GravityScale = v);
        AddSlider(flow, "Speed", 0.2f, 3.0f, _settings.SpeedScale,
            v => _settings.SpeedScale = v);

        AddSectionHeader(flow, "Tips");
        flow.Controls.Add(new Label
        {
            Width = 400,
            Height = 90,
            ForeColor = SystemColors.GrayText,
            Text = "• Click the pet to make it meow.\n"
                 + "• Stroke the TOP half left-right to pet it (purring).\n"
                 + "• Drag the BOTTOM half to move it; fling it to throw.\n"
                 + "• Right-click the pet, or use the tray icon, for more.\n"
                 + "• Scroll over the pet to resize it."
        });

        return page;
    }

    private void Notify()
    {
        _onChanged(_settings);
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    private static FlowLayoutPanel MakeFlow() => new()
    {
        Dock = DockStyle.Fill,
        FlowDirection = FlowDirection.TopDown,
        WrapContents = false,
        AutoScroll = true,
        Padding = new Padding(10)
    };

    private static void AddSectionHeader(FlowLayoutPanel flow, string text)
    {
        flow.Controls.Add(new Label
        {
            Text = text,
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            Width = 410,
            Height = 30,
            TextAlign = ContentAlignment.BottomLeft,
            Margin = new Padding(0, 10, 0, 4)
        });
    }

    private void AddCheck(FlowLayoutPanel flow, string text, bool value, Action<bool> setter)
    {
        var check = new CheckBox { Text = text, Checked = value, Width = 410, Height = 26 };
        check.CheckedChanged += (_, _) => { setter(check.Checked); Notify(); };
        flow.Controls.Add(check);
    }

    private void AddCombo(FlowLayoutPanel flow, string label, string[] items, int selected, Action<int> setter)
    {
        var row = new FlowLayoutPanel { Width = 410, Height = 32, FlowDirection = FlowDirection.LeftToRight };
        row.Controls.Add(new Label { Text = label, Width = 150, TextAlign = ContentAlignment.MiddleLeft });

        var combo = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList };
        combo.Items.AddRange(items);
        combo.SelectedIndex = Math.Clamp(selected, 0, items.Length - 1);
        combo.SelectedIndexChanged += (_, _) => { setter(combo.SelectedIndex); Notify(); };

        row.Controls.Add(combo);
        flow.Controls.Add(row);
    }

    private void AddSlider(FlowLayoutPanel flow, string label, float min, float max, float value, Action<float> setter)
    {
        var row = new FlowLayoutPanel { Width = 410, Height = 34, FlowDirection = FlowDirection.LeftToRight };
        row.Controls.Add(new Label { Text = label, Width = 90, TextAlign = ContentAlignment.MiddleLeft });

        var readout = new Label { Width = 48, TextAlign = ContentAlignment.MiddleRight, Text = value.ToString("0.0") };

        // TrackBar is integer-only, so the 0.2–3.0 float range is scaled by 10.
        var track = new TrackBar
        {
            Minimum = (int)(min * 10),
            Maximum = (int)(max * 10),
            TickFrequency = 5,
            Value = (int)(value * 10),
            Width = 250,
            Height = 30
        };
        track.ValueChanged += (_, _) =>
        {
            float v = track.Value / 10f;
            readout.Text = v.ToString("0.0");
            setter(v);
            Notify();
        };

        row.Controls.Add(track);
        row.Controls.Add(readout);
        flow.Controls.Add(row);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _saveDebounce.Stop();
            _saveDebounce.Dispose();
            _settings.Save();
        }
        base.Dispose(disposing);
    }
}
