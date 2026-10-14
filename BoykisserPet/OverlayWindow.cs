using System.Drawing.Imaging;

namespace BoykisserPet;

/// <summary>
/// A borderless, transparent, always-on-top window drawn with
/// UpdateLayeredWindow. Per-pixel alpha means blank areas of the buffer are
/// genuinely see-through and let mouse input reach whatever is underneath,
/// which is what makes the pet feel like it sits on the desktop rather than in
/// a box.
/// </summary>
public sealed class OverlayWindow : Form
{
    private const int WS_EX_LAYERED = Native.WS_EX_LAYERED;
    private const int WS_EX_TOOLWINDOW = Native.WS_EX_TOOLWINDOW;
    private const int WS_EX_NOACTIVATE = Native.WS_EX_NOACTIVATE;

    private Bitmap _buffer;
    private readonly int _width;
    private readonly int _height;

    /// <summary>Set when the cursor is over an interactive part of this window.</summary>
    public bool Interactive { get; set; }

    public OverlayWindow(int width, int height)
    {
        _width = width;
        _height = height;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        MinimumSize = Size.Empty;
        BackColor = Color.Black;
        TransparencyKey = Color.Empty;
        // Never take focus from the app the user is actually working in.
        SetStyle(ControlStyles.Selectable, false);
        // Must remain enabled to receive mouse input; ShowWithoutActivation and
        // WS_EX_NOACTIVATE prevent it from stealing keyboard focus.
        Enabled = true;

        _buffer = new Bitmap(width, height, PixelFormat.Format32bppPArgb);

        // Create the handle up front so ex-styles can be applied before the
        // first frame is pushed.
        IntPtr _ = Handle;
        ApplyExtendedStyles();
    }

    private void ApplyExtendedStyles()
    {
        IntPtr ex = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
        long value = ex.ToInt64();
        value |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, new IntPtr(value));
    }

    /// <summary>
    /// Toggles click-through. When false the window receives mouse input; when
    /// true, clicks fall through to the desktop. Toggled per frame based on
    /// whether the cursor is over the pet.
    /// </summary>
    public void SetClickThrough(bool clickThrough)
    {
        IntPtr ex = Native.GetWindowLongPtr(Handle, Native.GWL_EXSTYLE);
        long value = ex.ToInt64();
        long updated = clickThrough ? value | Native.WS_EX_TRANSPARENT : value & ~(long)Native.WS_EX_TRANSPARENT;
        if (updated != value)
            Native.SetWindowLongPtr(Handle, Native.GWL_EXSTYLE, new IntPtr(updated));
    }

    /// <summary>Pushes a fully-composed frame to the screen at <paramref name="x"/>,<paramref name="y"/>.</summary>
    public void Present(Bitmap frame, int x, int y)
    {
        // Reassert topmost placement each frame in case another app changed z-order.
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, frame.Width, frame.Height,
            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            hBitmap = frame.GetHbitmap(Color.FromArgb(0)); // premultiplied alpha
            oldBitmap = Native.SelectObject(memDc, hBitmap);

            var size = new Native.SIZE(frame.Width, frame.Height);
            var srcPoint = new Native.POINT(0, 0);
            var dstPoint = new Native.POINT(x, y);

            var blend = new Native.BLENDFUNCTION
            {
                BlendOp = Native.AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = Native.AC_SRC_ALPHA
            };

            Native.UpdateLayeredWindow(
                Handle, screenDc, ref dstPoint, ref size, memDc, ref srcPoint, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            if (hBitmap != IntPtr.Zero)
            {
                Native.SelectObject(memDc, oldBitmap);
                Native.DeleteObject(hBitmap);
            }
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>The offscreen surface the pet renders into.</summary>
    public Bitmap Buffer => _buffer;

    /// <summary>
    /// Deliberately swallows activation. A topmost tool window that grabs focus
    /// would interrupt typing in whatever the user is working in.
    /// </summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            // WS_POPUP with no caption: a true borderless overlay.
            cp.Style &= ~0x00C00000; // WS_CAPTION
            cp.Style &= ~0x00040000; // WS_SIZEBOX
            return cp;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _buffer.Dispose();
        base.Dispose(disposing);
    }
}
