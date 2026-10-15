namespace BoykisserPet;

/// <summary>
/// Cursor play. Now and then the pet notices the mouse pointer, runs along the
/// floor after it, crouches and pounces. If the pounce lands on the pointer the
/// real system cursor is knocked away with a short slide.
///
/// The pounce is a ballistic jump aimed at where the pointer was when the pet
/// took off, so a moving cursor can dodge it — a miss simply doesn't knock
/// anything.
/// </summary>
public sealed partial class Pet
{
    private enum CursorPhase { None, Chasing, Crouching, Leaping, Recovering }

    private const float ChaseSpeedMin = 150f;
    private const float ChaseSpeedMax = 440f;
    private const double CrouchMs = 420;
    private double CurrentCrouchMs => _settings.FreakyModEnabled ? 115.0 : CrouchMs;
    private const double RecoverMs = 1200;
    private const double KnockSlideMs = 260;

    private CursorPhase _cursorPhase = CursorPhase.None;
    private double _nextChaseMs;
    private double _chaseEndsMs;
    private double _nextPounceMs;
    private double _cursorPhaseStartMs;
    private float _leapElapsed;
    private float _leapDuration;
    private float _leapDirX = 1f, _leapDirY;

    /// <summary>True on frames where the pet actually ran somewhere; drives the bob and lean.</summary>
    private bool _running;
    private int _runDir = 1;

    // ---- knocked cursor: a short eased slide of the real system pointer ----
    private bool _slideActive;
    private PointF _slideFrom, _slideTo;
    private double _slideStartMs;

    /// <summary>Shared by all pets so two of them never fight over the pointer.</summary>
    private static double s_slideBusyUntilMs;

    private bool SpecialBehaviourActive => _cursorPhase != CursorPhase.None || _memePhase != MemePhase.None;

    private static float Dist(float dx, float dy) => MathF.Sqrt(dx * dx + dy * dy);

    /// <summary>Window-position limits that keep the body inside the work area.</summary>
    private void WindowLimits(out float minX, out float maxX, out float minY, out float maxY)
    {
        minX = _workArea.Left - (_pivotX - _bodyRx);
        maxX = _workArea.Right - (_pivotX + _bodyRx);
        minY = _workArea.Top - (_pivotY - _bodyRy);
        maxY = _workArea.Bottom - WindowH - FloorGap;
        if (maxY < minY) maxY = minY;
    }

    // ------------------------------------------------------------------ //
    // Scheduling and entry points
    // ------------------------------------------------------------------ //

    private void ScheduleNextChase(double nowMs)
    {
        var (min, max) = _settings.CursorChaseIntervalMs();
        // Freaky mode makes cursor-hunting a frequent behaviour, not a rare event.
        if (_settings.FreakyModEnabled)
        {
            // Freaky mode has its own fixed aggressive cadence; normal-mode settings don't affect it.
            min = 1400;
            max = 4200;
        }
        _nextChaseMs = nowMs + Random.Shared.Next(min, max + 1);
    }

    /// <summary>Starts a chase right now (context-menu action); ignores the "enabled" setting.</summary>
    public void StartCursorChase(double nowMs)
    {
        _nowMs = nowMs;
        if (_sleeping) WakeUp(nowMs);
        AbortMemeEvent();
        EndCursorPlay();
        StopTransientAnimations();
        EndWalk();
        BeginChase(nowMs);
    }

    private void BeginChase(double nowMs)
    {
        EndWalk();
        _cursorPhase = CursorPhase.Chasing;
        _cursorPhaseStartMs = nowMs;
        _chaseEndsMs = nowMs + Random.Shared.Next(12_000, 20_001);
        _nextPounceMs = nowMs + (_settings.FreakyModEnabled
            ? Random.Shared.Next(650, 1501)
            : Random.Shared.Next(3_500, 6_500));
        _lastInteractionMs = nowMs;

        // A pet left hovering mid-screen drops to the floor first, so the chase
        // is a run along the ground followed by a jump rather than a flight.
        WindowLimits(out _, out _, out _, out float floorY);
        if (!_airborne && Y < floorY - 4f)
        {
            _airborne = true;
            _vx = 0f;
            _vy = 0f;
        }

        Say(nowMs, Phrases.ChaseDeck.Next(_bubble.Text));
        _dirty = true;
    }

    /// <summary>Stops any cursor play. Safe to call at any time.</summary>
    private void EndCursorPlay()
    {
        if (_cursorPhase == CursorPhase.None) return;
        _cursorPhase = CursorPhase.None;
        _running = false;
        ScheduleNextChase(_nowMs);
        _dirty = true;
    }

    /// <summary>Ends cursor play and any picture run (user grabbed the pet, pause, fullscreen…).</summary>
    private void EndSpecialBehaviours()
    {
        EndCursorPlay();
        AbortMemeEvent();
    }

    // ------------------------------------------------------------------ //
    // Per-frame logic
    // ------------------------------------------------------------------ //

    private void TickCursorPlay(double nowMs)
    {
        switch (_cursorPhase)
        {
            case CursorPhase.None:
                if (!_settings.CursorChaseEnabled || nowMs < _nextChaseMs) return;
                ScheduleNextChase(nowMs); // a busy moment must not retry on every frame
                if (Busy || IsBeingDragged) return;
                if (Native.IdleMilliseconds() > 30_000) return;            // nobody is there to play with
                if (!_workArea.Contains(Native.CursorPosition())) return;  // pointer is on another monitor
                BeginChase(nowMs);
                break;

            case CursorPhase.Chasing:
                if (nowMs >= _chaseEndsMs)
                {
                    EndCursorPlay();
                    break;
                }
                if (!_airborne && nowMs >= _nextPounceMs && CursorInPounceRange())
                    BeginCrouch(nowMs);
                break;

            case CursorPhase.Crouching:
                if (nowMs - _cursorPhaseStartMs >= CurrentCrouchMs)
                    LaunchPounce(nowMs);
                break;

            case CursorPhase.Leaping:
                // Normally the arrival is detected inside the physics step; this
                // covers a landing that finished early and acts as a safety net.
                if (!_airborne || nowMs - _cursorPhaseStartMs > 2500)
                    OnPounceArrived(nowMs);
                break;

            case CursorPhase.Recovering:
                if (_airborne || nowMs - _cursorPhaseStartMs < RecoverMs) break;
                if (nowMs < _chaseEndsMs - 3000)
                {
                    _cursorPhase = CursorPhase.Chasing;
                    _nextPounceMs = nowMs + (_settings.FreakyModEnabled
                        ? Random.Shared.Next(650, 1501)
                        : Random.Shared.Next(3_500, 6_500));
                }
                else
                {
                    EndCursorPlay();
                }
                break;
        }
    }

    /// <summary>Runs along the floor towards the pointer. Returns true when the pet moved.</summary>
    private bool StepChase(float dt)
    {
        Point cur = Native.CursorPosition();
        WindowLimits(out float minX, out float maxX, out _, out _);

        float tx = Math.Clamp(cur.X - _pivotX, minX, maxX);
        float dx = tx - X;
        float adx = MathF.Abs(dx);
        if (adx > 2f) _facing = dx >= 0 ? 1 : -1;

        // Stop about one body-length short and wait for the pounce.
        float stop = _bodyRx * 1.1f;
        if (adx <= stop) return false;

        float speed = Math.Clamp(adx * 1.8f, ChaseSpeedMin, ChaseSpeedMax);
        float step = MathF.Min(adx - stop, speed * dt);
        int dir = dx >= 0 ? 1 : -1;
        X += dir * step;

        _runDir = dir;
        _walkPhase += dt * Math.Clamp(speed * 0.045f, 4f, 9f);
        _running = true;
        return true;
    }

    private bool CursorInPounceRange()
    {
        Point cur = Native.CursorPosition();
        if (!_workArea.Contains(cur)) return false;

        float cx = X + _pivotX;
        float cy = Y + _pivotY - _bodyRy;
        float dx = MathF.Abs(cur.X - cx);
        float dy = MathF.Abs(cur.Y - cy);
        return dx <= MathF.Max(200f, SpriteW * 1.6f) && dy <= 800f;
    }

    private void BeginCrouch(double nowMs)
    {
        _cursorPhase = CursorPhase.Crouching;
        _cursorPhaseStartMs = nowMs;
        _running = false;

        Point cur = Native.CursorPosition();
        float dx = cur.X - (X + _pivotX);
        if (MathF.Abs(dx) > 2f) _facing = dx >= 0 ? 1 : -1;
        _dirty = true;
    }

    /// <summary>
    /// Jump so that the body centre reaches the pointer after T seconds. With
    /// constant gravity g the required launch velocity is closed-form:
    /// vx = dx / T, vy = (dy - g T² / 2) / T.
    /// </summary>
    private void LaunchPounce(double nowMs)
    {
        Point cur = Native.CursorPosition();
        WindowLimits(out float minX, out float maxX, out float minY, out float maxY);

        float tx = Math.Clamp(cur.X - _pivotX, minX, maxX);
        float ty = Math.Clamp(cur.Y - (_pivotY - _bodyRy), minY, maxY);
        float dx = tx - X;
        float dy = ty - Y;
        float distance = Dist(dx, dy);

        // Freaky mode commits to the leap quickly and uses a much shorter flight
        // time, which makes the trajectory faster and the cursor hit more precise.
        float flight = _settings.FreakyModEnabled
            ? Math.Clamp(0.16f + distance / 3200f, 0.18f, 0.34f)
            : Math.Clamp(0.30f + distance / 2200f, 0.34f, 0.62f);
        float g = Gravity * _settings.GravityScale;

        _vx = dx / flight;
        _vy = (dy - 0.5f * g * flight * flight) / flight;
        _airborne = true;
        _spin = 0f;
        _spinVel = 0f;

        _leapElapsed = 0f;
        _leapDuration = flight;
        if (distance > 1f)
        {
            _leapDirX = dx / distance;
            _leapDirY = dy / distance;
        }
        else
        {
            _leapDirX = _facing;
            _leapDirY = 0f;
        }
        if (MathF.Abs(dx) > 2f) _facing = dx >= 0 ? 1 : -1;

        _cursorPhase = CursorPhase.Leaping;
        _cursorPhaseStartMs = nowMs;
        _dirty = true;
    }

    private void OnPounceArrived(double nowMs)
    {
        if (_cursorPhase != CursorPhase.Leaping) return;

        _cursorPhase = CursorPhase.Recovering;
        _cursorPhaseStartMs = nowMs;
        _vx *= 0.3f; // drop down near the pointer instead of sailing on

        _impactAtMs = nowMs;
        _impactAmp = 0.14f;
        _impactSide = 0f;
        _particles.Dust(nowMs, _pivotX, _pivotY - _bodyRy, 0f, -1f, 10);
        if (_settings.SoundsEnabled) _audio.PlayMeow(0.55f);

        Point cur = Native.CursorPosition();
        float cx = X + _pivotX;
        float cy = Y + _pivotY - _bodyRy;
        bool hit = Dist(cur.X - cx, cur.Y - cy) <= MathF.Max(90f, SpriteW * 0.75f);

        if (hit)
        {
            Say(nowMs, Phrases.PounceDeck.Next(_bubble.Text));
            if (_settings.FreakyModEnabled)
                KnockCursor(nowMs, cur); // Freaky-mode knock remains independent of normal-mode strength.
            else
                BumpCursorDown(nowMs, cur);
        }
        else
        {
            Say(nowMs, Phrases.PounceMissDeck.Next(_bubble.Text));
        }
        _dirty = true;
    }

    // ------------------------------------------------------------------ //
    // Knocking the real pointer away
    // ------------------------------------------------------------------ //

    private void KnockCursor(double nowMs, Point cur)
    {
        if (!_settings.CursorKnockEnabled) return;
        // Never fight the user while they hold a button (dragging, selecting…).
        if (nowMs < s_slideBusyUntilMs || Native.AnyMouseButtonDown()) return;

        // Push the pointer the way the pet was jumping, with some scatter and
        // a flatter angle so it mostly skids sideways.
        float angle = MathF.Atan2(_leapDirY * 0.45f, _leapDirX) + (Random.Shared.NextSingle() - 0.5f) * 0.9f;
        float length = Random.Shared.Next(150, 270) * Math.Clamp(SpriteW / 140f, 0.7f, 1.6f);

        Rectangle area = Native.WorkAreaOf(cur);
        float tx = Math.Clamp(cur.X + MathF.Cos(angle) * length, area.Left + 4, area.Right - 5);
        float ty = Math.Clamp(cur.Y + MathF.Sin(angle) * length, area.Top + 4, area.Bottom - 5);

        _slideFrom = cur;
        _slideTo = new PointF(tx, ty);
        _slideStartMs = nowMs;
        _slideActive = true;
        s_slideBusyUntilMs = nowMs + KnockSlideMs + 500;
    }


    /// <summary>Normal-mode hit reaction: nudge the pointer down by a user-configurable amount.</summary>
    private void BumpCursorDown(double nowMs, Point cur)
    {
        if (!_settings.CursorHitBumpEnabled || _settings.CursorHitBumpStrength <= 0) return;
        if (nowMs < s_slideBusyUntilMs || Native.AnyMouseButtonDown()) return;

        Rectangle area = Native.WorkAreaOf(cur);
        float maxDown = Math.Max(0, area.Bottom - 5 - cur.Y);
        float requested = 35f + 2.2f * _settings.CursorHitBumpStrength; // 35–255 px
        float down = Math.Min(maxDown, requested);
        if (down <= 1f) return;

        _slideFrom = cur;
        _slideTo = new PointF(cur.X, cur.Y + down);
        _slideStartMs = nowMs;
        _slideActive = true;
        s_slideBusyUntilMs = nowMs + KnockSlideMs + 500;
    }

    private void UpdateCursorSlide(double nowMs)
    {
        if (!_slideActive) return;
        if (Native.AnyMouseButtonDown())
        {
            _slideActive = false;
            return;
        }

        float t = (float)Math.Clamp((nowMs - _slideStartMs) / KnockSlideMs, 0.0, 1.0);
        float e = 1f - (1f - t) * (1f - t) * (1f - t); // ease-out cubic
        int x = (int)MathF.Round(_slideFrom.X + (_slideTo.X - _slideFrom.X) * e);
        int y = (int)MathF.Round(_slideFrom.Y + (_slideTo.Y - _slideFrom.Y) * e);
        Native.SetCursorPos(x, y);

        if (t >= 1f) _slideActive = false;
    }

    // ------------------------------------------------------------------ //
    // Pose
    // ------------------------------------------------------------------ //

    /// <summary>Crouch before the jump (squash + butt wiggle) and a stretched pose in flight.</summary>
    private void ApplyCursorPose(ref Pose p, double nowMs, float t)
    {
        switch (_cursorPhase)
        {
            case CursorPhase.Crouching:
            {
                float k = (float)Math.Clamp((nowMs - _cursorPhaseStartMs) / CurrentCrouchMs, 0.0, 1.0);
                p.ScaleY -= 0.18f * k;
                p.ScaleX += 0.10f * k;
                p.Angle += 0.05f * MathF.Sin(t * 55f) * k;
                break;
            }
            case CursorPhase.Leaping:
                p.ScaleY += 0.08f;
                p.ScaleX -= 0.05f;
                p.Angle += 0.12f * _facing;
                break;
        }
    }
}
