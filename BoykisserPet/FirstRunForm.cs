namespace BoykisserPet;

/// <summary>Small one-time setup dialog shown before the pet appears.</summary>
internal sealed class FirstRunForm : Form
{
    private readonly TextBox _nameBox;
    private readonly CheckBox _startupBox;

    public string PetName => string.IsNullOrWhiteSpace(_nameBox.Text) ? "Бойкиссер" : _nameBox.Text.Trim();
    public bool LaunchAtStartup => _startupBox.Checked;

    public FirstRunForm(string currentName, bool launchAtStartup)
    {
        Text = "Добро пожаловать в Boykisser Pet!";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(420, 205);
        MinimumSize = new Size(436, 244);
        Font = new Font("Segoe UI", 10f);
        Padding = new Padding(16);

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            AutoSize = false,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        layout.Controls.Add(new Label
        {
            Text = "Как будем звать питомца?",
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        _nameBox = new TextBox
        {
            Dock = DockStyle.Fill,
            MaxLength = 40,
            Text = currentName,
            Margin = new Padding(0, 0, 0, 10),
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        layout.Controls.Add(_nameBox, 0, 1);
        _startupBox = new CheckBox
        {
            Text = "Добавить в автозагрузку?",
            AutoSize = true,
            Checked = launchAtStartup,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8)
        };
        layout.Controls.Add(_startupBox, 0, 2);

        var buttons = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            AutoSize = true,
            Margin = new Padding(0)
        };
        var ok = new Button { Text = "Готово", Width = 100, Height = 32, DialogResult = DialogResult.OK, Margin = new Padding(0) };
        ok.Click += (_, _) => { if (string.IsNullOrWhiteSpace(_nameBox.Text)) _nameBox.Text = "Бойкиссер"; };
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        AcceptButton = ok;
    }
}

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "BoykisserPet";

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                ?? Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return;
            if (enabled)
            {
                string? executable = Environment.ProcessPath;
                if (!string.IsNullOrWhiteSpace(executable))
                    key.SetValue(ValueName, $"\"{executable}\"");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Startup registration is optional and must not prevent the pet launching.
        }
    }
}
