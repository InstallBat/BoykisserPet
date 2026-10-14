using System.Runtime.InteropServices;

namespace BoykisserPet;

/// <summary>
/// Invisible window that owns the global hotkeys. Global hotkeys need a real
/// window to receive WM_HOTKEY, and this one must never be visible or take
/// focus, so it is a zero-size message-only-ish popup.
/// </summary>
public sealed class HotkeyWindow : NativeWindow, IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_NOREPEAT = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly Action _onPause;
    private readonly Action _onTalk;
    private readonly Action _onRoll;
    private readonly Action _onAddPet;
    private readonly Action _onSettings;
    private readonly List<int> _registered = new();
    private bool _disposed;

    public HotkeyWindow(Action onPause, Action onTalk, Action onRoll, Action onAddPet, Action onSettings)
    {
        _onPause = onPause;
        _onTalk = onTalk;
        _onRoll = onRoll;
        _onAddPet = onAddPet;
        _onSettings = onSettings;

        var cp = new CreateParams
        {
            Caption = "BoykisserPetHotkeys",
            X = 0, Y = 0, Width = 0, Height = 0,
            Style = 0,
            ExStyle = 0,
            Parent = new IntPtr(-3) // HWND_MESSAGE: message-only, never shown
        };
        CreateHandle(cp);

        uint mods = MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT;
        TryRegister(1, mods, (uint)Keys.B, _onPause);
        TryRegister(2, mods, (uint)Keys.T, _onTalk);
        TryRegister(3, mods, (uint)Keys.R, _onRoll);
        TryRegister(4, mods, (uint)Keys.A, _onAddPet);
        TryRegister(5, mods, (uint)Keys.S, _onSettings);
    }

    /// <summary>
    /// Registration fails when another app already owns the combination. That
    /// is a normal condition, not an error — the tray menu still exposes
    /// everything, so we just skip the binding.
    /// </summary>
    private void TryRegister(int id, uint modifiers, uint key, Action handler)
    {
        if (RegisterHotKey(Handle, id, modifiers, key))
            _registered.Add(id);
        else
            GC.KeepAlive(handler);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY)
        {
            switch (m.WParam.ToInt32())
            {
                case 1: _onPause(); return;
                case 2: _onTalk(); return;
                case 3: _onRoll(); return;
                case 4: _onAddPet(); return;
                case 5: _onSettings(); return;
            }
        }
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (int id in _registered) UnregisterHotKey(Handle, id);
        _registered.Clear();
        DestroyHandle();
    }
}
