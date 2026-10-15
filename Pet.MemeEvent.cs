namespace BoykisserPet;

/// <summary>
/// Rare event in the spirit of Desktop Goose: the pet runs off the left or
/// right edge of the screen, is gone for a moment, then walks back in pulling a
/// random picture (mem1.webp … mem7.webp) and leaves it on the desktop.
///
/// The pet only models the event. The picture itself lives in its own
/// click-through overlay window owned by <see cref="PetAppContext"/>, which
/// follows <see cref="CarriedMemeBounds"/> while the picture is being pulled and
/// takes the bitmap over through <see cref="TakeDeliveredMeme"/> once it is
/// dropped.
/// </summary>
public sealed partial class Pet
{
    private enum MemePhase { None, LeavingScreen, Offscreen, Returning, Dropping }

    private const float MemeRunSpeed = 300f;
    private const double DropCelebrationMs = 1100;

    private MemePhase _memePhase = MemePhase.None;
    private double _nextMemeMs;
    private double _memePhaseStartMs;
    private double _memeOffscreenUntilMs;
    private double _memeRummageUntilMs;

    /// <summary>-1 = the pet leaves and re-enters through the left edge, +1 = the right edge.</summary>
    private int _memeEdge;
    private float _memeExitX;   // window X at which the body is completely off the edge
    private float _memeTargetX; // window X where the pet stops and lets go

    private Bitmap? _memeBitmap;
    private bool _memeCarried;

    private Bitmap? _deliveredBitmap;
    private Point _deliveredLocation;

    // ------------------------------------------------------------------ //
    // Public surface used by the host window code
    // ------------------------------------------------------------------ //

    /// <summary>The picture while the pet is pulling it; null at every other time.</summary>
    public Bitmap? CarriedMemeBitmap => _memeCarried ? _memeBitmap : null;

    /// <summary>Where the carried picture should be drawn, in screen pixels.</summary>
    public Rectangle CarriedMemeBounds
    {
        get
        {
            if (_memeBitmap is null) return Rectangle.Empty;
            int w = _memeBitmap.Width, h = _memeBitmap.Height;

            // The pet faces the picture and holds its near edge in its mouth,
            // backing away and pulling it along the ground.
            float bodyCx = X + _pivotX;
            float feetY = Y + _pivotY;
            float left = _facing >= 0
                ? bodyCx + _bodyRx * 0.55f
                : bodyCx - _bodyRx * 0.55f - w;
            float top = feetY + 4f - h;
            return new Rectangle((int)MathF.Round(left), (int)MathF.Round(top), w, h);
        }
    }

    /// <summary>
    /// Returns the picture once it has been dropped (exactly once) and hands over
    /// ownership of the bitmap. Null when nothing was delivered.
    /// </summary>
    public Bitmap? TakeDeliveredMeme(out Point location)
    {
        location = _deliveredLocation;
        Bitmap? bitmap = _deliveredBitmap;
        _deliveredBitmap = null;
        return bitmap;
    }

    // ------------------------------------------------------------------ //
    // Scheduling and entry points
    // ------------------------------------------------------------------ //

    private void ScheduleNextMeme(double nowMs)
    {
        var (min, max) = _settings.MemeEventIntervalMs();
        // Meme frequency tuning is a normal-mode option; Freaky mode keeps its own cadence.
        if (_settings.FreakyModEnabled)
        {
            (int presetMin, int presetMax) = PetSettings.IdleGifPresets[PetSettings.DefaultPresetIndex];
            min = presetMin * 1000;
            max = presetMax * 1000;
        }
        _nextMemeMs = nowMs + Random.Shared.Next(min, max + 1);
    }

    /// <summary>Starts the event right now (context-menu action); ignores the "enabled" setting.</summary>
    public void StartMemeEvent(double nowMs)
    {
        _nowMs = nowMs;
        if (_memePhase != MemePhase.None) return;

        EndCursorPlay();
        if (_sleeping) WakeUp(nowMs);
        StopTransientAnimations();

        if (!TryBeginMemeEvent(nowMs))
            Say(nowMs, "Я бы принёс картинку, но у меня их нет :c");
    }

    private bool TryBeginMemeEvent(double nowMs)
    {
        // Decode a picture first, so the pet never runs off for nothing.
        Bitmap? bitmap = LoadRandomMeme();
        if (bitmap is null) return false;

        _memeBitmap = bitmap;
        _memeCarried = false;
        EndWalk();

        // Meme travel owns movement completely. If the pet was thrown just
        // before the event started, stale airborne physics would prevent the
        // walk state machine from ever running and leave it off-screen.
        _airborne = false;
        _vx = 0f;
        _vy = 0f;
        _spin = 0f;
        _spinVel = 0f;
        _activity = PetActivity.Standing;

        _memeEdge = Random.Shared.Next(2) == 0 ? -1 : 1;
        // Stop at the reachable edge of the work area. Asking the window to
        // travel fully off-screen can fail on Windows; then LeavingScreen never
        // completes and the pet keeps trying to walk in the same direction.
        _memeExitX = _memeEdge < 0
            ? _workArea.Left - (_pivotX - _bodyRx)
            : _workArea.Right - (_pivotX + _bodyRx);
        _memeExitX = Math.Clamp(
            _memeExitX,
            _workArea.Left - (_pivotX - _bodyRx),
            _workArea.Right - (_pivotX + _bodyRx));

        _memePhase = MemePhase.LeavingScreen;
        _memePhaseStartMs = nowMs;
        _lastInteractionMs = nowMs;

        Say(nowMs, Phrases.MemeLeaveDeck.Next(_bubble.Text));
        _dirty = true;
        return true;
    }

    private Bitmap? LoadRandomMeme()
    {
        // Keep the picture a sensible size relative to both the pet and the screen.
        float limit = Math.Min(_workArea.Width * 0.4f, _workArea.Height * 0.55f);
        int maxDim = (int)Math.Min(Math.Clamp(SpriteH * 2.6f, 240f, 520f), limit);
        maxDim = Math.Max(maxDim, 120);

        // Random order, so one missing or corrupt file doesn't cancel the event.
        string[] memeNames = Assets.MemeNamesForSkin(Config.Skin);
        int[] order = Enumerable.Range(0, memeNames.Length)
            .OrderBy(_ => Random.Shared.Next())
            .ToArray();

        foreach (int i in order)
        {
            Bitmap? bitmap = Assets.MemeBitmap(memeNames[i], maxDim, Config.Skin);
            if (bitmap is not null) return bitmap;
        }
        return null;
    }

    // ------------------------------------------------------------------ //
    // Per-frame logic
    // ------------------------------------------------------------------ //

    private void TickMemeEvent(double nowMs)
    {
        // Recovery guard: every active phase should be short. This also
        // protects against unusual multi-monitor work areas and interrupted ticks.
        if (_memePhase != MemePhase.None &&
            nowMs - _memePhaseStartMs > (_memePhase == MemePhase.Dropping ? 8_000 : 20_000))
        {
            AbortMemeEvent();
            return;
        }

        switch (_memePhase)
        {
            case MemePhase.None:
                if (!_settings.MemeEventEnabled || nowMs < _nextMemeMs) return;
                ScheduleNextMeme(nowMs); // a busy moment must not retry on every frame
                if (Busy || IsBeingDragged) return;
                TryBeginMemeEvent(nowMs); // quietly does nothing when no pictures are bundled
                break;

            case MemePhase.LeavingScreen:
                if (!_airborne && MathF.Abs(X - _memeExitX) <= 3f)
                {
                    ArriveAtMemeEdge(nowMs);
                }
                break;

            case MemePhase.Offscreen:
                if (nowMs >= _memeOffscreenUntilMs) BeginMemeReturn(nowMs);
                break;

            case MemePhase.Returning:
                if (MathF.Abs(X - _memeTargetX) <= Math.Max(3f, MemeRunSpeed * 0.05f))
                { X = _memeTargetX; BeginMemeDrop(nowMs); }
                break;

            case MemePhase.Dropping:
                if (nowMs - _memePhaseStartMs >= DropCelebrationMs)
                {
                    _memePhase = MemePhase.None;
                    ScheduleNextMeme(nowMs);
                }
                break;
        }
    }

    /// <summary>Walks towards the exit (outbound) or the drop spot (inbound). True when the pet moved.</summary>
    private bool StepMemeWalk(float dt)
    {
        bool leaving = _memePhase == MemePhase.LeavingScreen;
        float targetX = leaving ? _memeExitX : _memeTargetX;
        float dx = targetX - X;
        float adx = MathF.Abs(dx);
        if (adx <= Math.Max(2f, MemeRunSpeed * Math.Max(dt, 0.016f)))
        {
            X = targetX;
            if (leaving) ArriveAtMemeEdge(_nowMs);
            else if (_memePhase == MemePhase.Returning) BeginMemeDrop(_nowMs);
            return false;
        }

        // Trotting out is quicker than hauling a picture back in.
        float speed = leaving ? MemeRunSpeed * 1.15f : MemeRunSpeed;
        float step = MathF.Min(adx, speed * dt);
        int dir = dx >= 0 ? 1 : -1;
        X += dir * step;
        if (step >= adx - 0.5f)
        {
            X = targetX;
            if (leaving) ArriveAtMemeEdge(_nowMs);
            else if (_memePhase == MemePhase.Returning) BeginMemeDrop(_nowMs);
        }

        _runDir = dir;
        _facing = leaving ? dir : -dir; // inbound, the pet looks back at the picture it is pulling
        _walkPhase += dt * Math.Clamp(speed * 0.045f, 4f, 9f);
        _running = true;
        return true;
    }


    private void ArriveAtMemeEdge(double nowMs)
    {
        if (_memePhase != MemePhase.LeavingScreen) return;
        X = _memeExitX;
        _memePhase = MemePhase.Offscreen;
        _memePhaseStartMs = nowMs;
        _memeRummageUntilMs = nowMs + Math.Max(500, _audio.PlayRummage());
        _memeOffscreenUntilMs = _memeRummageUntilMs;
        _bubble.Hide();
        _running = false;
        _vx = _vy = _spinVel = _spin = 0f;
        _airborne = false;
        _dirty = true;
    }

    private void BeginMemeReturn(double nowMs)
    {
        if (_memeBitmap is null)
        {
            // Should not happen; leave the pet somewhere visible rather than offscreen.
            AbortMemeEvent();
            return;
        }

        _memePhase = MemePhase.Returning;
        _memePhaseStartMs = nowMs;
        _memeCarried = true;

        _spin = 0f;
        _spinVel = 0f;
        _vx = 0f;
        _vy = 0f;
        _airborne = false;

        WindowLimits(out _, out _, out _, out float floorY);
        X = _memeExitX;
        Y = floorY;
        _facing = _memeEdge; // faces the picture, which trails towards the edge it came from

        // Pick a spot far enough in that the whole picture ends up on screen.
        float memeW = _memeBitmap.Width;
        float bodyHalf = _bodyRx;
        const float margin = 24f;
        float areaL = _workArea.Left;
        float areaR = _workArea.Right;
        float areaW = _workArea.Width;

        float lo, hi;
        if (_memeEdge < 0) // enters from the left, picture trails on the left of the pet
        {
            lo = areaL + margin + memeW + bodyHalf * 0.55f;
            hi = areaL + areaW * 0.72f;
        }
        else // enters from the right, picture trails on the right of the pet
        {
            lo = areaL + areaW * 0.28f;
            hi = areaR - margin - memeW - bodyHalf * 0.55f;
        }

        // The request is explicit: return to the middle of the usable screen,
        // not a random point. Keep the carried image inside the work area where possible.
        float centreX = Math.Clamp(
            areaL + areaW * 0.5f,
            areaL + bodyHalf,
            areaR - bodyHalf);
        _memeTargetX = centreX - _pivotX;

        _dirty = true;
    }

    private void BeginMemeDrop(double nowMs)
    {
        DeliverMeme();
        _memePhase = MemePhase.Dropping;
        _memePhaseStartMs = nowMs;
        _running = false;
        _lastInteractionMs = nowMs;

        Say(nowMs, Phrases.MemeDropDeck.Next(_bubble.Text));
        if (_settings.SoundsEnabled) _audio.PlayMeow(0.8f);
        Hop(nowMs, reaction: true);
    }

    // ------------------------------------------------------------------ //
    // Picture hand-over and clean-up
    // ------------------------------------------------------------------ //

    /// <summary>Lets go of the picture where it is, clamped so it is fully on screen.</summary>
    private void DeliverMeme()
    {
        if (_memeBitmap is null) return;

        Rectangle bounds = CarriedMemeBounds;
        int w = _memeBitmap.Width, h = _memeBitmap.Height;
        int x = Math.Clamp(bounds.X, _workArea.Left, Math.Max(_workArea.Left, _workArea.Right - w));
        int y = Math.Clamp(bounds.Y, _workArea.Top, Math.Max(_workArea.Top, _workArea.Bottom - h));

        _deliveredBitmap?.Dispose(); // only if the host never collected the previous one
        _deliveredBitmap = _memeBitmap;
        _deliveredLocation = new Point(x, y);
        _memeBitmap = null;
        _memeCarried = false;
    }

    private void DiscardMeme()
    {
        _memeBitmap?.Dispose();
        _memeBitmap = null;
        _memeCarried = false;
    }

    /// <summary>
    /// Ends the event early (grabbed, rolled over by a toast, paused…). A picture
    /// that is already being pulled is dropped where it is; otherwise it is thrown
    /// away. A pet caught off the edge is put back on screen.
    /// </summary>
    private void AbortMemeEvent()
    {
        if (_memePhase == MemePhase.None) return;

        MemePhase phase = _memePhase;
        _memePhase = MemePhase.None;
        _running = false;

        if (phase == MemePhase.Returning && _memeCarried) DeliverMeme();
        else DiscardMeme();

        ClampToWorkArea();
        ScheduleNextMeme(_nowMs);
        _dirty = true;
    }

    private void DisposeMemeResources()
    {
        _memeBitmap?.Dispose();
        _memeBitmap = null;
        _deliveredBitmap?.Dispose();
        _deliveredBitmap = null;
    }
}
