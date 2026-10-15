namespace BoykisserPet;

/// <summary>Small context-menu-triggered chat field positioned directly under a pet.</summary>
internal sealed class ChatBarForm : Form
{
    private readonly TextBox _input;
    public event Action<string>? MessageSubmitted;
    public event Action? Dismissed;

    public ChatBarForm()
    {
        FormBorderStyle = FormBorderStyle.FixedSingle;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(330, 62);
        MinimizeBox = false; MaximizeBox = false; ControlBox = false;
        BackColor = DarkTheme.Back;
        ForeColor = DarkTheme.Text;
        Padding = new Padding(3);
        _input = new TextBox { Left = 4, Top = 5, Width = 265, Height = 22, PlaceholderText = "Поговорить с питомцем…", BorderStyle = BorderStyle.FixedSingle, BackColor = DarkTheme.Input, ForeColor = DarkTheme.Text };
        var send = new Button { Text = "➤", Left = 274, Top = 3, Width = 48, Height = 26, TabStop = false, BackColor = DarkTheme.Panel, ForeColor = DarkTheme.Text, FlatStyle = FlatStyle.Flat };
        var close = new Button { Text = "Закрыть чат", Left = 4, Top = 34, Width = 318, Height = 24, TabStop = false, BackColor = DarkTheme.Panel, ForeColor = DarkTheme.Text, FlatStyle = FlatStyle.Flat };
        close.Click += (_, _) => { Hide(); Dismissed?.Invoke(); };
        send.Click += (_, _) => Submit();
        _input.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Submit(); } if (e.KeyCode == Keys.Escape) { Dismissed?.Invoke(); Hide(); } };
        Controls.Add(_input); Controls.Add(send); Controls.Add(close);
    }

    public void PositionBelow(Rectangle petBounds, Rectangle workArea)
    {
        int x = petBounds.Left + (petBounds.Width - Width) / 2;
        x = Math.Clamp(x, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
        int y = petBounds.Bottom - 2;
        if (y + Height > workArea.Bottom) y = petBounds.Top - Height + 2;
        y = Math.Clamp(y, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
        Location = new Point(x, y);
    }

    public void FocusInput()
    {
        if (IsDisposed) return;
        if (!Visible) Show();
        // Allow WinForms to create the handle before activating/focusing the edit.
        BeginInvoke((Action)(() =>
        {
            if (IsDisposed || !Visible) return;
            Activate();
            _input.Focus();
            _input.SelectionStart = _input.TextLength;
        }));
    }

    private void Submit()
    {
        string text = _input.Text.Trim();
        if (text.Length == 0) return;
        _input.Clear();
        Hide();
        MessageSubmitted?.Invoke(text);
    }

}
