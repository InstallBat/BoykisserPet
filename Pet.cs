using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BoykisserPet;

public enum PetActivity
{
    Standing,
    Walking,
    Sleeping
}

/// <summary>
/// One pet: its sprites, physics body, timers and drawing. Everything that
/// used to be Android view manipulation in the plugin (layout params, alpha,
/// scaleX/scaleY, ImageDrawable swapping) is expressed here as state that the
/// host window paints each frame.
/// </summary>
public sealed partial class Pet : IDisposable
{
    // ---- animation durations, ported from the plugin ----
    private const int AnimDurationMs = 1250;
    private const int IdleGifDurationMs = 7000;
    private const float DragStartThreshold = 13f;
    private const int BulletinDelayMs = 650;
    private const int MinMeowHalfMs = 200;

    // ---- physics, ported from the reference exe ----
    private const float Gravity = 2600f;
    private const float FloorGap = 6f;
    private const float ThrowMinVelocity = 300f;
    private const float ThrowMaxVelocity = 6000f;
    private const float ThrowRetentionPerSec = 0.5f;
    private const float ThrowBounceDamping = 0.6f;
    private const float ThrowStopVelocity = 40f;
    private const float VelocitySmoothing = 0.35f;

    private const float JellyMaxSquash = 0.28f;
    private const float JellyDurationMs = 320f;
    private const float JellyFrequencyHz = 7f;
    private const float JellyDampingRate = 9f;

    // ---- petting gesture detection ----
    private const int PetDirectionChangesThreshold = 3;
    private const float PetMinStep = 8f;
    private const float PetMaxVerticalDrift = 40f;

    private const float WalkSpeedMin = 55f;
    private const float WalkSpeedMax = 115f;
    private const float WalkBobAmplitude = 3.5f;

    public int Index { get; }
    public PetConfig Config { get; }
    private readonly PetSettings _settings;
    private readonly PetAudio _audio;

    // ---- sprites ----
    private AnimatedGif? _base;
    private AnimatedGif? _petHand;
    private AnimatedGif? _petMain;
    private AnimatedGif? _sleep;
    private readonly List<AnimatedGif?> _idleGifs = new();
    private readonly Dictionary<AnimatedGif, AnimatedGif> _flipped = new();

    // ---- layout (raw pixels) ----
    public int SpriteW { get; private set; }
    public int SpriteH { get; private set; }
    public int WindowW { get; private set; }
    public int WindowH { get; private set; }

    /// <summary>Body half-axes used for hit-testing and collision.</summary>
    private float _bodyRx, _bodyRy;
    private float _rollR;

    /// <summary>Sprite pivot (bottom centre) inside the buffer.</summary>
    private float _pivotX, _pivotY;

    /// <summary>Window top-left in virtual-screen pixels.</summary>
    public float X { get; set; }
    public float Y { get; set; }

    // ---- motion ----
    private float _vx, _vy;
    private bool _airborne;
    private float _spin, _spinVel;
    private float _tilt, _tiltVel;
    private float _walkPhase;

    private double _impactAtMs = -10000;
    private float _impactAmp;
    private float _impactSide;

    private double _jellyAtMs = -10000;
    private float _jellyAmp;
    private int _jellyAxis; // 0 = x, 1 = y, 2 = both

    private double _hopAtMs = -10000;

    // ---- visual state ----
    private double _baseClockMs;
    private bool _baseAnimating;
    private double _petMainClockMs;
    private bool _petMainActive;
    private double _idleClockMs;
    private AnimatedGif? _idleGif;
    private bool _idleActive;
    private bool _sleeping;
    private bool _animating;
    public bool IsPetting { get; private set; }
    public bool IsSleeping => _sleeping;
    public bool IsAirborne => _airborne;
    public bool IsBeingDragged { get; private set; }

    private PetActivity _activity = PetActivity.Standing;
    public PetActivity Activity => _activity;

    /// <summary>Remembers the last event that should keep the frame rate high.</summary>
    private bool _dirty;
    private bool _positionFrozen;
    private bool _silent;

    // ---- autonomous behaviour timers ----
    private double _nextIdleGifMs;
    private double _nextIdleMeowMs;
    private double _nextChatterMs;
    private double _nextWanderMs;
    private double _nextStumbleMs;
    private double _lastInteractionMs;

    private float _wanderTargetX, _wanderTargetY;
    private int _facing = 1; // 1 = right, -1 = left

    private readonly ParticleSystem _particles = new();
    private readonly SpeechBubble _bubble = new();

    private double _nowMs;
    private readonly Rectangle _workArea;

    public Pet(int index, PetConfig config, PetSettings settings, Rectangle workArea, double nowMs)
    {
        Index = index;
        Config = config;
        _settings = settings;
        _workArea = workArea;
        _nowMs = nowMs;

        _audio = new PetAudio(config.Skin.Equals("expie", StringComparison.OrdinalIgnoreCase) ? null : Assets.SoundPath(Assets.MeowMp3), Assets.SoundPath(Assets.PurrMp3, config.Skin), Assets.SoundPath(Assets.FeedMp3, config.Skin), Assets.SoundPath("rummage.mp3", config.Skin));

        LoadSprites();

        if (config.X is int px && config.Y is int py)
        {
            X = px;
            Y = py;
            ClampToWorkArea();
        }
        else
        {
            // First run: stack pets up the right-hand edge so they don't overlap.
            X = _workArea.Right - WindowW - 24;
            Y = _workArea.Bottom - WindowH - FloorGap - index * (WindowH + 12);
            ClampToWorkArea();
        }

        _lastInteractionMs = nowMs;
        ScheduleAll(nowMs);
        ScheduleNextChase(nowMs);
        ScheduleNextMeme(nowMs);
    }

    // ------------------------------------------------------------------ //
    // Assets and layout
    // ------------------------------------------------------------------ //

    private void LoadSprites()
    {
        Config.Clamp();
        SpriteW = Config.Size;
        SpriteH = Config.Size;

        // The buffer needs room for the balloon above the pet and a little
        // slack either side so impact squash isn't clipped.
        WindowW = Math.Max(SpriteW + 80, 320);
        WindowH = SpriteH + 220;

        _pivotX = WindowW / 2f;
        _pivotY = WindowH - 30f - ExhaustGap();

        _base = Assets.Gif(Config.Skin == "expie" ? "boykisser.gif" : Assets.BaseGif, SpriteW, SpriteH, Config.Skin);
        _petHand = Assets.Gif(Assets.PetGif, SpriteW, SpriteH, Config.Skin);
        _petMain = Assets.Gif(Assets.PetMainGif, SpriteW, SpriteH, Config.Skin);
        _sleep = Assets.Gif(Assets.SleepGif, SpriteW, SpriteH, Config.Skin);

        _idleGifs.Clear();
        foreach (string name in Assets.IdleGifs)
            _idleGifs.Add(Assets.Gif(name, SpriteW, SpriteH, Config.Skin));

        // Body ellipse inscribed in the sprite, matching the reference's
        // slightly-inset hit shape.
        _bodyRx = SpriteW * 0.47f;
        _bodyRy = SpriteH * 0.48f;
        _rollR = (_bodyRx + _bodyRy) / 2f;
    }

    /// <summary>Empty strip at the bottom of the buffer beneath the pet's feet.</summary>
    private static float ExhaustGap() => 6f;

    /// <summary>Rebuilds sprites after the size setting changed.</summary>
    public void Resize(double nowMs)
    {
        int oldW = WindowW, oldH = WindowH;
        float anchorX = X + oldW / 2f;

        LoadSprites();

        X = anchorX - WindowW / 2f;
        Y = Y + (oldH - WindowH);
        _nowMs = nowMs;
        ClampToWorkArea();
        _dirty = true;
    }

    public void ApplySettings()
    {
        _audio.Muted = !_settings.SoundsEnabled;
    }

    private void ClampToWorkArea()
    {
        X = Math.Clamp(X, _workArea.Left - (_pivotX - _bodyRx), _workArea.Right - (_pivotX + _bodyRx));
        float maxY = _workArea.Bottom - WindowH - FloorGap;
        float minY = _workArea.Top - (_pivotY - _bodyRy);
        if (maxY < minY) maxY = minY;
        Y = Math.Clamp(Y, minY, maxY);
    }

    // ------------------------------------------------------------------ //
    // Hit testing — uses the body ellipse, not the whole buffer
    // ------------------------------------------------------------------ //

    public bool HitTest(Point screenPoint)
    {
        float localX = screenPoint.X - X;
        float localY = screenPoint.Y - Y;
        float dx = (localX - _pivotX) / _bodyRx;
        float dy = (localY - (_pivotY - _bodyRy)) / _bodyRy;
        return dx * dx + dy * dy <= 1.0f;
    }

    /// <summary>Local Y within the sprite, 0 at the top — drives pet vs drag zones.</summary>
    public bool IsTopZone(Point screenPoint)
    {
        float localY = screenPoint.Y - Y;
        float spriteTop = _pivotY - SpriteH;
        return localY < spriteTop + SpriteH / 2f;
    }

    public bool IsOverBalloon(Point screenPoint)
    {
        if (!_bubble.Visible) return false;
        float localX = screenPoint.X - X;
        float localY = screenPoint.Y - Y;
        return localY >= 0 && localY < _pivotY - SpriteH - 8f
            && localX >= 0 && localX <= WindowW;
    }

    // ------------------------------------------------------------------ //
    // Input
    // ------------------------------------------------------------------ //

    private double _dragGrabDX, _dragGrabDY;
    private float _dragVelX, _dragVelY;
    private double _dragTrailTime;
    private float _dragTrailX, _dragTrailY;
    private bool _dragTrailValid;
    private double _pressStartMs;
    private float _pressX, _pressY;
    private float _petLastX;
    private int _petLastDirection;
    private int _petDirectionChanges;
    private bool _petTriggered;

    public void OnPointerDown(Point screenPoint, double nowMs)
    {
        _nowMs = nowMs;
        _dirty = true;
        // A user grab interrupts a meme run. Otherwise the autonomous walk
        // state can survive the drag and resume pushing the pet toward the edge.
        if (_memePhase != MemePhase.None)
            AbortMemeEvent();
        // Keep the pointer capture, but do not move the pet until the cursor
        // has crossed the drag threshold. A short click remains a click.
        IsBeingDragged = true;
        _pressStartMs = nowMs;
        _pressX = screenPoint.X;
        _pressY = screenPoint.Y;

        _dragGrabDX = screenPoint.X - X;
        _dragGrabDY = screenPoint.Y - Y;

        _dragTrailX = screenPoint.X;
        _dragTrailY = screenPoint.Y;
        _dragTrailTime = nowMs;
        _dragTrailValid = true;
        _dragVelX = _dragVelY = 0f;

        _petLastX = screenPoint.X;
        _petLastDirection = 0;
        _petDirectionChanges = 0;
        _petTriggered = false;

        // Grabbing mid-flight cancels the throw and the jelly wobble.
        StopThrow();
        RegisterInteraction(nowMs);
    }

    public void OnPointerMove(Point screenPoint, double nowMs)
    {
        _nowMs = nowMs;
        _dirty = true;

        float totalDx = screenPoint.X - _pressX;
        float totalDy = screenPoint.Y - _pressY;
        float totalDistance = MathF.Sqrt(totalDx * totalDx + totalDy * totalDy);

        // Petting lives in the upper half of the sprite. It can trigger with
        // back-and-forth strokes; otherwise only movement beyond the threshold
        // becomes a drag, so a short click never nudges the window.
        if (_pressIsTopZone && _settings.PettingEnabled)
        {
            HandlePettingGesture(screenPoint, nowMs);
            if (_petTriggered) return;
        }

        if (totalDistance >= DragStartThreshold && _settings.DraggingEnabled)
            _draggedFar = true;

        if (_draggedFar && _settings.DraggingEnabled)
        {
            X = (float)(screenPoint.X - _dragGrabDX);
            Y = (float)(screenPoint.Y - _dragGrabDY);
            ClampToWindowBoundsForDrag();
        }

        // Track pointer velocity for the fling. Raw screen coordinates are
        // required: the window follows the cursor, so window-local deltas
        // would always be ~0.
        if (_dragTrailValid)
        {
            double dt = (nowMs - _dragTrailTime) / 1000.0;
            if (dt > 0.001)
            {
                float instVX = (float)((screenPoint.X - _dragTrailX) / dt);
                float instVY = (float)((screenPoint.Y - _dragTrailY) / dt);
                _dragVelX = _dragVelX * (1 - VelocitySmoothing) + instVX * VelocitySmoothing;
                _dragVelY = _dragVelY * (1 - VelocitySmoothing) + instVY * VelocitySmoothing;
            }
            _dragTrailX = screenPoint.X;
            _dragTrailY = screenPoint.Y;
            _dragTrailTime = nowMs;
        }
    }

    private bool _pressIsTopZone;

    /// <summary>Called by the host once it knows which zone the press landed in.</summary>
    public void SetPressZone(bool topZone) => _pressIsTopZone = topZone;

    private void ClampToWindowBoundsForDrag()
    {
        float minX = _workArea.Left - (_pivotX - _bodyRx);
        float maxX = _workArea.Right - (_pivotX + _bodyRx);
        float minY = _workArea.Top - (_pivotY - _bodyRy);
        float maxY = _workArea.Bottom - WindowH - FloorGap;
        if (maxY < minY) maxY = minY;

        X = Math.Clamp(X, minX, maxX);
        Y = Math.Clamp(Y, minY, maxY);
    }

    public void OnPointerUp(Point screenPoint, double nowMs)
    {
        _nowMs = nowMs;
        _dirty = true;
        IsBeingDragged = false;

        if (_pressIsTopZone && _settings.PettingEnabled && _petTriggered)
        {
            HidePetOverlay();
            _petTriggered = false;
            return;
        }

        if (_draggedFar)
        {
            SavePosition();

            float speed = MathF.Sqrt(_dragVelX * _dragVelX + _dragVelY * _dragVelY);
            if (_settings.ThrowEnabled && speed >= ThrowMinVelocity)
            {
                StartThrow(nowMs);
            }
        }
        else
        {
            OnTap(nowMs);
        }
    }

    private bool _draggedFar;

    public void NoteDragDistance(float distance)
    {
        if (distance >= DragStartThreshold) _draggedFar = true;
    }

    public void BeginPress(double nowMs)
    {
        _draggedFar = false;
    }

    // ------------------------------------------------------------------ //
    // Petting gesture: count left-right reversals of the pointer
    // ------------------------------------------------------------------ //

    private void HandlePettingGesture(Point screenPoint, double nowMs)
    {
        float verticalDrift = MathF.Abs(screenPoint.Y - _pressY);
        if (verticalDrift > PetMaxVerticalDrift) return; // wandering too much to be a stroke

        float step = screenPoint.X - _petLastX;
        if (MathF.Abs(step) < PetMinStep) return;

        int direction = step > 0 ? 1 : -1;
        if (_petLastDirection != 0 && direction != _petLastDirection)
            _petDirectionChanges++;

        _petLastDirection = direction;
        _petLastX = screenPoint.X;

        if (_petDirectionChanges >= PetDirectionChangesThreshold)
        {
            _petTriggered = true;
            // Reset the counter so continued stroking triggers again.
            _petDirectionChanges = 0;
            OnPet(nowMs);
        }
    }

    // ------------------------------------------------------------------ //
    // Behaviours
    // ------------------------------------------------------------------ //

    public void OnTap(double nowMs)
    {
        _nowMs = nowMs;
        _dirty = true;
        bool wasAnimating = _animating || _baseAnimating || _idleActive || _petMainActive || IsPetting;
        StopTransientAnimations();
        if (_sleeping) WakeUp(nowMs);
        if (wasAnimating) return; // short click cancels the current animation

        if (!_settings.TapEnabled)
        {
            // Still a reaction, just a silent/physical one.
            return;
        }

        if (_animating) return;
        _animating = true;

        string line = Phrases.TapDeck.Next(_bubble.Text);
        Say(nowMs, line);
        Hop(nowMs, reaction: true);

        int meowMs = _settings.SoundsEnabled ? _audio.PlayMeow() : 0;
        PlayBaseAnimation(nowMs);

        double busyMs = Math.Max(AnimDurationMs, meowMs);
        _busyUntilMs = nowMs + busyMs;
    }

    private double _busyUntilMs;

    /// <summary>Starts a seven-second dance/random sprite animation.</summary>
    public void StartDance(double nowMs)
    {
        // Niko has no dance animation asset; leave all other skins unchanged.
        if (Config.Skin.Equals("niko", StringComparison.OrdinalIgnoreCase)) return;
        _nowMs = nowMs;
        if (_sleeping) WakeUp(nowMs);
        StopTransientAnimations();
        AnimatedGif? gif = PickIdleGif();
        if (gif is null) return;
        _idleGif = gif;
        _idleActive = true;
        _idleClockMs = 0;
        _idleEndsMs = nowMs + 7000;
        _musicDanceMode = false;
        _dancePoseActive = true;
        _lastInteractionMs = nowMs;
        Say(nowMs, Phrases.IdleDeck.Next(_bubble.Text));
        _dirty = true;
    }

    /// <summary>Keep the dance animation active for as long as music is detected.</summary>
    public void SetMusicDance(bool active, double nowMs)
    {
        if (active)
        {
            if (!_musicDanceMode || !_idleActive) StartDance(nowMs);
            _musicDanceMode = true;
            _idleEndsMs = double.MaxValue;
            _dirty = true;
        }
        else if (_musicDanceMode)
        {
            _musicDanceMode = false;
            _dancePoseActive = false;
            _idleEndsMs = nowMs;
            _dirty = true;
        }
    }

    /// <summary>Return to the idle sprite and stop ongoing petting/dance/audio.</summary>
    private void StopTransientAnimations()
    {
        _musicDanceMode = false;
        _dancePoseActive = false;
        InterruptIdleGif();
        _animating = false;
        _busyUntilMs = 0;
        _baseAnimating = false;
        _baseClockMs = 0;
        _petMainActive = false;
        _petMainClockMs = 0;
        IsPetting = false;
        StopPurr();
        _dirty = true;
    }

    public void OnPet(double nowMs)
    {
        _nowMs = nowMs;
        _dirty = true;
        InterruptIdleGif();

        ShowPetOverlay();
        StartPurr();

        if (_settings.PetNotificationEnabled)
        {
            string line = Phrases.PurrDeck.Next(_bubble.Text);
            Say(nowMs, line);
        }

        Hop(nowMs, reaction: false);
    }

    /// <summary>Feed the pet: show a speech line and play the nom-nom sound.</summary>
    public void OnFeed(double nowMs)
    {
        _nowMs = nowMs;
        if (_sleeping) WakeUp(nowMs);
        StopTransientAnimations();
        Say(nowMs, "Ням-ням-ням! Спасибо за вкусняшку!");
        if (_settings.SoundsEnabled) _audio.PlayFeed();
        Hop(nowMs, reaction: true);
        _lastInteractionMs = nowMs;
        _dirty = true;
    }

    public void OnTalk(double nowMs)
    {
        _nowMs = nowMs;
        _dirty = true;
        string line = Phrases.IdleDeck.Next(_bubble.Text);
        Say(nowMs, line);
        Hop(nowMs, reaction: true);
        if (_settings.SoundsEnabled) _audio.PlayMeow(0.62f);
    }

    public void OnRoll(double nowMs)
    {
        _nowMs = nowMs;
        _dirty = true;
        CancelWander();

        string line = Phrases.RollDeck.Next(_bubble.Text);
        Say(nowMs, line);
        if (_settings.SoundsEnabled) _audio.PlayMeow(0.75f);

        float dir = X - _workArea.Left > _workArea.Right - X ? -1f : 1f;
        _vx = dir * 1150f;
        _vy = -650f;
        _airborne = true;
        _spinVel = _vx / _rollR;
        _dirty = true;
    }

    public void OnStumble(double nowMs)
    {
        _nowMs = nowMs;
        if (_sleeping) WakeUp(nowMs);
        StopTransientAnimations();
        Say(nowMs, new[] { "ОЙ!! ААА, ПАДАЮ!!", "Мяу!! Я оступился!!", "АААА— *кувырок*", "Кто убрал пол?! МЯУ!!" }[Random.Shared.Next(4)]);
        if (_settings.SoundsEnabled) _audio.PlayMeow(0.9f);
        CancelWander();
        float dir = Random.Shared.Next(2) == 0 ? -1f : 1f;
        _vx = dir * Random.Shared.Next(350, 700);
        _vy = -Random.Shared.Next(100, 280);
        _airborne = true;
        _spinVel = dir * Random.Shared.Next(5, 10);
        _lastInteractionMs = nowMs;
        _nextStumbleMs = nowMs + Random.Shared.Next(45_000, 100_001);
        _dirty = true;
    }

    public void OnScared(double nowMs, string sourceName)
    {
        _nowMs = nowMs;
        _dirty = true;
        if (_sleeping) WakeUp(nowMs);
        StopTransientAnimations();
        string[] scaredLines =
        {
            "ААА! ЧТО ЭТО БЫЛО?!",
            "АЙ! Я чуть не подпрыгнул до потолка!",
            "Тише-тише! Я тут вообще-то спал!",
            "Мя?! Это ты так громко?!",
            "Фух... показалось, что-то взорвалось :c"
        };
        Say(nowMs, scaredLines[Random.Shared.Next(scaredLines.Length)]);
        if (_settings.SoundsEnabled) _audio.PlayMeow(0.75f);
        CancelWander();
        float dir = X - _workArea.Left > _workArea.Right - X ? -1f : 1f;
        _vx = dir * 1150f;
        _vy = -650f;
        _airborne = true;
        _spinVel = _vx / _rollR;
        _lastInteractionMs = nowMs;
    }

    /// <summary>Startles the pet when it is picked up.</summary>
    public void OnGrabbed(double nowMs)
    {
        if (nowMs - _lastGrabPhraseMs < 3000) return;
        _lastGrabPhraseMs = nowMs;
        Say(nowMs, Phrases.GrabDeck.Next(_bubble.Text));
    }

    private double _lastGrabPhraseMs = -10000;

    public void TogglePositionFreeze() { _positionFrozen = !_positionFrozen; }
    public void ToggleSilent() { _silent = !_silent; if (_silent) _bubble.Hide(); }
    public void ShowRandomMeme(double nowMs) => StartMemeEvent(nowMs);

    public void Say(double nowMs, string text)
    {
        if (_silent) return;
        if (Config.Skin.Equals("expie", StringComparison.OrdinalIgnoreCase))
            text = Phrases.ExpieEquivalent(text, _bubble.Text);
        _bubble.Say(nowMs, text);
        _dirty = true;
    }

    public void HideBubble() => _bubble.Hide();

    /// <summary>Stops autonomous cursor/meme actions on a direct user request.</summary>
    public void StopCurrentAction(double nowMs)
    {
        _nowMs = nowMs;
        bool wasBusy = _cursorPhase != CursorPhase.None || _memePhase != MemePhase.None;
        EndCursorPlay();
        AbortMemeEvent();
        _running = false;
        _vx = 0f; _vy = 0f; _spin = 0f; _spinVel = 0f; _airborne = false;
        ClampToWorkArea();
        Say(nowMs, wasBusy ? "Остановился. Больше не гонюсь и мем пока не тащу." : "Я и так стою. Никакой погони сейчас нет.");
        _dirty = true;
    }

    private void Hop(double nowMs, bool reaction)
    {
        _hopAtMs = nowMs;
        Color crumb = Color.FromArgb(255, 232, 190, 120);
        _particles.Burst(nowMs, _pivotX, _pivotY - SpriteH * 0.7f, reaction ? 14 : 8, crumb);
        _dirty = true;
    }

    private void StartThrow(double nowMs)
    {
        float speed = MathF.Sqrt(_dragVelX * _dragVelX + _dragVelY * _dragVelY);
        if (speed > ThrowMaxVelocity && speed > 0)
        {
            float scale = ThrowMaxVelocity / speed;
            _dragVelX *= scale;
            _dragVelY *= scale;
        }

        CancelWander();
        _vx = _dragVelX;
        _vy = _dragVelY;
        _airborne = true;
        _spinVel += _vx / _rollR * 0.6f;

        if (nowMs > _bubble.UntilMs)
            Say(nowMs, Phrases.ThrowDeck.Next(_bubble.Text));

        _dirty = true;
    }

    private void StopThrow()
    {
        _airborne = false;
        _vx = 0f;
        _vy = 0f;
        _jellyAtMs = -10000;
        _jellyAmp = 0f;
    }

    private void RegisterInteraction(double nowMs)
    {
        _lastInteractionMs = nowMs;
        if (_sleeping) WakeUp(nowMs);
    }

    // ---- idle / sleep / wander scheduling ----

    private void ScheduleAll(double nowMs)
    {
        _nextIdleGifMs = nowMs + Random.Shared.Next(45_000, 90_001);

        var (meowMin, meowMax) = _settings.IdleMeowIntervalMs();
        _nextIdleMeowMs = nowMs + Random.Shared.Next(meowMin, meowMax + 1);

        var (chatMin, chatMax) = _settings.ChatterIntervalMs();
        _nextChatterMs = nowMs + Random.Shared.Next(chatMin, chatMax + 1);

        var (wanderMin, wanderMax) = _settings.WanderIntervalMs();
        _nextWanderMs = nowMs + Random.Shared.Next(wanderMin, wanderMax + 1);
    }

    private bool Busy => _animating || IsPetting || _petMainActive || _idleActive || _sleeping
        || _airborne || IsBeingDragged || !_particles.IsEmpty || SpecialBehaviourActive;

    private bool AutomaticBehaviourAllowed =>
        _settings.Enabled && !Busy && !_settings.PauseOnFullscreen;

    /// <summary>Advances timers and physics. Returns true when the window must move.</summary>
    public bool Update(double nowMs, float dt, bool fullscreenActive)
    {
        _nowMs = nowMs;
        if (_positionFrozen) { _dirty = true; return false; }
        float scaledDt = dt * _settings.SpeedScale;
        bool moved = false;

        // --- animation clocks ---
        if (nowMs >= _busyUntilMs)
        {
            // Clear both the busy latch and the running animation together.
            // Leaving _animating set here would keep the pet "busy" forever and
            // permanently suppress every idle behaviour.
            if (_animating)
            {
                _animating = false;
                _dirty = true;
            }
            if (_baseAnimating)
            {
                // Reset to frame 0 so the pose doesn't freeze on whatever frame
                // it stopped at — the plugin had to rebuild the drawable for this.
                _baseAnimating = false;
                _baseClockMs = 0;
                _dirty = true;
            }
        }
        if (_baseAnimating || ((_activity == PetActivity.Walking || _running) && !_sleeping))
            _baseClockMs += dt * 1000f * _settings.SpeedScale;
        if (_petMainActive) _petMainClockMs += dt * 1000f * _settings.SpeedScale;
        if (_idleActive)
        {
            _idleClockMs += dt * 1000f * _settings.SpeedScale * 0.55f;
            if (nowMs >= _idleEndsMs)
            {
                _idleActive = false;
                _idleGif = null;
                _idleClockMs = 0;
                if (!_musicDanceMode) _dancePoseActive = false;
                _dirty = true;
            }
        }

        _audio.Muted = !_settings.SoundsEnabled;

        // --- lean damping (the pet lags behind a dragging hand) ---
        _tiltVel += (-130f * _tilt - 7.5f * _tiltVel) * dt;
        _tilt += _tiltVel * dt;
        _tilt = Math.Clamp(_tilt, -0.5f, 0.5f);
        if (MathF.Abs(_tilt) > 0.001f || MathF.Abs(_tiltVel) > 0.01f) _dirty = true;

        if (!IsBeingDragged)
        {
            moved = StepPhysics(nowMs, scaledDt);
        }

        _particles.Update(nowMs, dt == 0 ? 0.016f : dt);
        if (!_particles.IsEmpty) _dirty = true;

        if (_bubble.IsAnimating(nowMs)) _dirty = true;

        // --- autonomous behaviour ---
        bool pause = fullscreenActive && _settings.PauseOnFullscreen;

        if (_settings.Enabled && !pause)
        {
            // Occasionally stumble only when the pet is visibly high on the screen.
            // Y is the window's top coordinate, so compare against the upper half
            // of the usable work area rather than the sprite's bottom position.
            if (nowMs >= _nextStumbleMs)
            {
                _nextStumbleMs = nowMs + Random.Shared.Next(25_000, 70_001);
                float halfway = _workArea.Top + _workArea.Height * 0.5f;
                if (Y < halfway && !Busy && !_sleeping && !IsBeingDragged)
                    OnStumble(nowMs);
            }
            ShouldSleep(nowMs);
            TickIdleGif(nowMs);
            TickIdleMeow(nowMs);
            TickChatter(nowMs);
            TickCursorPlay(nowMs);
            TickMemeEvent(nowMs);
            if (_memePhase == MemePhase.None) TickWander(nowMs, dt);
        }

        if (_sleeping) _dirty = _dirty || false;
        if (_activity == PetActivity.Walking) _dirty = true;

        return moved;
    }

    // ---- physics ----

    private bool StepPhysics(double nowMs, float dt)
    {
        if (dt <= 0f) return false;
        bool moved = false;

        // These behaviours own horizontal movement while grounded. During a
        // pounce, normal ballistic physics still takes priority.
        if (_memePhase == MemePhase.LeavingScreen || _memePhase == MemePhase.Returning)
        {
            // Meme-event movement owns the pet regardless of stale airborne state.
            _airborne = false; _vx = 0f; _vy = 0f; _spin = 0f; _spinVel = 0f;
            moved = StepMemeWalk(dt);
        }
        else if (!_airborne && _cursorPhase == CursorPhase.Chasing)
        {
            moved = StepChase(dt);
        }
        else if (_airborne)
        {
            _vy += Gravity * _settings.GravityScale * dt;
            X += _vx * dt;
            Y += _vy * dt;
            _spin += _spinVel * dt;

            float minY = _workArea.Top - (_pivotY - _bodyRy);
            float maxY = _workArea.Bottom - WindowH - FloorGap;
            if (maxY < minY) maxY = minY;

            if (Y < minY)
            {
                Y = minY;
                _vy = MathF.Abs(_vy) * 0.5f;
            }

            if (Y >= maxY)
            {
                Y = maxY;
                float vy = _vy;
                if (vy > 260f)
                {
                    _vy = -vy * 0.42f;
                }
                else
                {
                    _vy = 0f;
                    _airborne = false;
                    _activity = PetActivity.Standing;
                }

                // Ground contact equalises rolling and forwards speed.
                float w = (_spinVel + _vx / _rollR) / 2f;
                _spinVel = w;
                _vx = w * _rollR;
                Impact(nowMs, vy / 2400f, 0);
            }

            float minX = _workArea.Left - (_pivotX - _bodyRx);
            float maxX = _workArea.Right - (_pivotX + _bodyRx);
            if (X < minX || X > maxX)
            {
                float side = X < minX ? -1f : 1f;
                X = Math.Clamp(X, minX, maxX);
                float vx = _vx;
                _vx = -vx * 0.7f;
                _spinVel = _airborne ? -_spinVel * 0.6f : _vx / _rollR;
                Impact(nowMs, MathF.Abs(vx) / 1600f, side > 0 ? 1 : 0);
                TriggerJelly(vx != 0 || _vy != 0 ? 2 : 0);
            }

            moved = true;
            _dirty = true;
        }
        else
        {
            // Grounded: settle any residual roll, otherwise walk or stand.
            if (MathF.Abs(_spin) > 0.0005f || MathF.Abs(_spinVel) > 0.02f)
            {
                _spin = WrapAngle(_spin);
                _spinVel += (-60f * _spin - 9f * _spinVel) * dt;
                _spin += _spinVel * dt;
                if (MathF.Abs(_spin) < 0.002f && MathF.Abs(_spinVel) < 0.02f)
                {
                    _spin = 0f;
                    _spinVel = 0f;
                }
                moved = true;
                _dirty = true;
            }

            if (_activity == PetActivity.Walking && !_sleeping && _settings.WanderEnabled)
                moved |= StepWalk(dt);
        }

        _spin = WrapAngle(_spin);
        return moved;
    }

    private bool StepWalk(float dt)
    {
        float dx = _wanderTargetX - X;
        float dy = _wanderTargetY - Y;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        if (distance < 3f)
        {
            X = _wanderTargetX;
            Y = _wanderTargetY;
            ClampToWorkArea();
            EndWalk();
            return false;
        }

        float speed = Math.Clamp(distance * 1.15f, WalkSpeedMin, WalkSpeedMax) * _settings.SpeedScale;
        float step = Math.Min(distance, speed * dt);
        X += dx / distance * step;
        Y += dy / distance * step;
        ClampToWorkArea();
        if (MathF.Abs(dx) > 2f) _facing = dx >= 0 ? 1 : -1;
        _walkPhase += dt * speed * 0.08f;
        return true;
    }

    private void EndWalk()
    {
        _activity = PetActivity.Standing;
        _walkPhase = 0f;
    }

    private void CancelWander()
    {
        if (_activity == PetActivity.Walking) EndWalk();
    }

    private static float WrapAngle(float a)
    {
        const float tau = MathF.PI * 2f;
        return a - tau * MathF.Round(a / tau);
    }

    private void Impact(double nowMs, float strength, int side)
    {
        strength = Math.Clamp(strength, 0f, 1f);
        if (strength < 0.06f) return;

        _impactAtMs = nowMs;
        _impactAmp = 0.22f * strength;
        _impactSide = side;

        float atX = side == 0 ? _pivotX : _pivotX + (side > 0 ? _bodyRx : -_bodyRx);
        float atY = side == 0 ? _pivotY : _pivotY - _bodyRy;
        float dirX = side == 0 ? 0f : (side > 0 ? -1f : 1f);
        float dirY = side == 0 ? -1f : 0f;

        _particles.Dust(nowMs, atX, atY, dirX, dirY, (int)(strength * 18f));

        if (strength > 0.45f && nowMs > _bubble.UntilMs)
            Say(nowMs, Phrases.BumpDeck.Next(_bubble.Text));

        _dirty = true;
    }

    private void TriggerJelly(int axis)
    {
        if (!_settings.JellyBounceEnabled) return;
        _jellyAtMs = _nowMs;
        _jellyAmp = JellyMaxSquash;
        _jellyAxis = axis;
    }

    // ---- idle tickers ----

    private void ShouldSleep(double nowMs)
    {
        if (!_settings.SleepEnabled || _sleeping) return;
        if (Busy) return;
        if (nowMs - _lastInteractionMs < _settings.SleepTimeoutMs()) return;

        _sleeping = true;
        _activity = PetActivity.Standing;
        CancelWander();
        Say(nowMs, Phrases.SleepDeck.Next(_bubble.Text));
        _dirty = true;
    }

    public void WakeFromMenu(double nowMs)
    {
        _nowMs = nowMs;
        StopTransientAnimations();
        WakeUp(nowMs);
        _lastInteractionMs = nowMs;
        _dirty = true;
    }

    private void WakeUp(double nowMs)
    {
        if (!_sleeping) return;
        _sleeping = false;
        _lastInteractionMs = nowMs;
        Say(nowMs, Phrases.WakeDeck.Next(_bubble.Text));
        _dirty = true;
    }

    private void TickIdleGif(double nowMs)
    {
        if (nowMs < _nextIdleGifMs) return;

        // Random events are intentionally more frequent than the old 2.5–4 min cadence.
        int min = 45_000;
        int max = 90_000;
        _nextIdleGifMs = nowMs + Random.Shared.Next(min, max + 1);

        if (!_settings.IdleGifEnabled || Busy) return;

        AnimatedGif? gif = PickIdleGif();
        if (gif is null) return;

        _idleGif = gif;
        _idleActive = true;
        _idleClockMs = 0;
        _idleEndsMs = nowMs + IdleGifDurationMs;
        _dirty = true;
    }

    private double _idleEndsMs;
    private bool _musicDanceMode;
    private bool _dancePoseActive;

    private AnimatedGif? PickIdleGif()
    {
        var available = _idleGifs.Where(g => g is not null).ToList();
        if (available.Count == 0) return null;
        return available[Random.Shared.Next(available.Count)];
    }

    private void InterruptIdleGif()
    {
        if (!_idleActive) return;
        _idleActive = false;
        _idleGif = null;
        _dirty = true;
    }

    private void TickIdleMeow(double nowMs)
    {
        if (nowMs < _nextIdleMeowMs) return;

        var (min, max) = _settings.IdleMeowIntervalMs();
        _nextIdleMeowMs = nowMs + Random.Shared.Next(min, max + 1);

        if (!_settings.IdleMeowEnabled || Busy) return;

        _animating = true;
        int meowMs = _settings.SoundsEnabled ? _audio.PlayMeow() : 0;
        PlayBaseAnimation(nowMs);
        _busyUntilMs = nowMs + Math.Max(AnimDurationMs, meowMs);
        _dirty = true;
    }

    private void TickChatter(double nowMs)
    {
        if (nowMs < _nextChatterMs) return;

        var (min, max) = _settings.ChatterIntervalMs();
        _nextChatterMs = nowMs + Random.Shared.Next(min, max + 1);

        if (!_settings.ChatterEnabled || Busy) return;
        if (nowMs < _bubble.UntilMs) return;

        Say(nowMs, Phrases.IdleDeck.Next(_bubble.Text));
    }

    private void TickWander(double nowMs, float dt)
    {
        if (!_settings.WanderEnabled || SpecialBehaviourActive) return;

        if (_activity == PetActivity.Walking)
        {
            if (Busy) CancelWander();
            return;
        }

        if (nowMs < _nextWanderMs) return;

        var (min, max) = _settings.WanderIntervalMs();
        _nextWanderMs = nowMs + Random.Shared.Next(min, max + 1);

        if (Busy || _sleeping) return;

        // Pick a destination anywhere in the usable work area. Keep the
        // pet's body inside the monitor, but allow both vertical and diagonal
        // walks rather than only pacing along the bottom edge.
        float minX = _workArea.Left - (_pivotX - _bodyRx);
        float maxX = _workArea.Right - (_pivotX + _bodyRx);
        float minY = _workArea.Top - (_pivotY - _bodyRy);
        float maxY = _workArea.Bottom - WindowH - FloorGap;
        if (maxX <= minX || maxY <= minY) return;

        _wanderTargetX = minX + Random.Shared.NextSingle() * (maxX - minX);
        // Bias toward lower areas, but periodically let the pet climb higher.
        float verticalPick = Random.Shared.NextSingle();
        float verticalBias = verticalPick * verticalPick;
        _wanderTargetY = minY + verticalBias * (maxY - minY);
        if (MathF.Abs(_wanderTargetX - X) < 70f && MathF.Abs(_wanderTargetY - Y) < 55f)
            _wanderTargetY = minY + Random.Shared.NextSingle() * (maxY - minY);
        _activity = PetActivity.Walking;
        _walkPhase = 0f;
        _dirty = true;
    }

    private void PlayBaseAnimation(double nowMs)
    {
        _baseAnimating = true;
        _baseClockMs = 0;
        _dirty = true;
    }

    private void ShowPetOverlay()
    {
        if (IsPetting) return; // already looping — don't restart on each stroke
        IsPetting = true;
        _petMainActive = true;
        _petMainClockMs = 0;
        _dirty = true;
    }

    private void HidePetOverlay()
    {
        IsPetting = false;
        _petMainActive = false;
        _petMainClockMs = 0;
        StopPurr();
        _baseAnimating = false;
        _baseClockMs = 0;
        _dirty = true;
    }

    private void StartPurr()
    {
        if (_settings.SoundsEnabled) _audio.StartPurr();
    }

    private void StopPurr() => _audio.StopPurr();

    private void SavePosition()
    {
        Config.X = (int)MathF.Round(X);
        Config.Y = (int)MathF.Round(Y);
    }

    // ------------------------------------------------------------------ //
    // Rendering
    // ------------------------------------------------------------------ //

    /// <summary>True while the pet still needs repainting; drives the frame rate.</summary>
    public bool NeedsAnimationFrame =>
        _animating || _baseAnimating || _petMainActive || _idleActive
        || _airborne || IsBeingDragged || IsPetting
        || MathF.Abs(_tilt) > 0.002f || MathF.Abs(_tiltVel) > 0.02f
        || _spin != 0f || _spinVel != 0f
        || !_particles.IsEmpty
        || _activity == PetActivity.Walking
        || _bubble.IsAnimating(_nowMs)
        || _nowMs - _impactAtMs < 700
        || _nowMs - _jellyAtMs < JellyDurationMs;

    public bool IsDirty => _dirty;

    public void MarkClean() => _dirty = false;

    public void Draw(Graphics g, double nowMs)
    {
        _nowMs = nowMs;
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        Pose pose = ComputePose(nowMs);

        DrawShadow(g, pose);
        DrawSprite(g, pose);
        _particles.Draw(g, nowMs);

        // Balloon tail points just above the pet's head.
        float tipX = _pivotX;
        float tipY = _pivotY - SpriteH - 12f;
        _bubble.Draw(g, nowMs, tipX, tipY, 0f, WindowW);
    }

    private struct Pose
    {
        public float Jump;
        public float ScaleX, ScaleY;
        public float Angle;
        public float Spin;
        public float Lift;
        public float SquashX, SquashY;
        public float SquashAtX, SquashAtY;
        public float Air;
    }

    private Pose ComputePose(double nowMs)
    {
        float t = (float)(nowMs / 1000.0);
        var p = new Pose();

        // Breathing.
        float breath = MathF.Sin(t * 2.3f);
        p.ScaleX = 1f - 0.014f * breath;
        p.ScaleY = 1f + 0.022f * breath;

        // Walking bob — a small vertical bounce so movement reads as stepping.
        if ((_activity == PetActivity.Walking || _running) && !_sleeping)
        {
            // Keep the older stepping bounce and squash/stretch while walking
            // or during scripted runs (including fetching a meme).
            float bob = MathF.Abs(MathF.Sin(_walkPhase * MathF.PI));
            p.Jump = bob * WalkBobAmplitude;
            p.ScaleY += 0.03f * bob;
            p.ScaleX -= 0.02f * bob;
        }

        p.Angle = 0.025f * MathF.Sin(t * 1.15f) + _tilt;
        if ((_activity == PetActivity.Walking || _running) && !_sleeping)
        {
            // Gentle body sway synced to the footsteps, like the legacy animation.
            p.Angle += 0.055f * MathF.Sin(_walkPhase * MathF.PI * 0.5f);
        }
        if (_running) p.Angle += 0.07f * _runDir; // lean into the run

        // A small procedural dance layered over the available sprite animation.
        // This keeps dancing visible even though the source asset pack has no dedicated dance GIF.
        if (_dancePoseActive)
        {
            float danceBeat = t * 10.5f;
            float side = MathF.Sin(danceBeat);
            p.Jump += 8f + MathF.Abs(MathF.Sin(danceBeat * 0.5f)) * 12f;
            p.Angle += side * 0.18f;
            p.ScaleX += 0.035f * MathF.Sin(danceBeat + MathF.PI / 2f);
            p.ScaleY += 0.045f * MathF.Abs(MathF.Sin(danceBeat));
        }

        // Hop envelope after a poke.
        float e = (float)((nowMs - _hopAtMs) / 1000.0);
        if (e >= 0f && e < 2f)
        {
            float q = e / 0.55f;
            if (q < 1f)
            {
                p.Jump += 62f * 4f * q * (1f - q);
                float s = MathF.Sin(MathF.PI * q);
                p.ScaleY += 0.10f * s;
                p.ScaleX -= 0.06f * s;
            }
            else if (q < 1.45f)
            {
                float r = (q - 1f) / 0.45f;
                float s = MathF.Sin(MathF.PI * r);
                p.ScaleY -= 0.17f * s;
                p.ScaleX += 0.10f * s;
            }
            p.Angle += 0.16f * MathF.Exp(-3.2f * e) * MathF.Sin(24f * e);
        }

        // Lift so a rotated body still touches the floor.
        float ss = MathF.Sin(_spin), sc = MathF.Cos(_spin);
        p.Spin = _spin;
        p.Lift = MathF.Sqrt(_bodyRx * ss * _bodyRx * ss + _bodyRy * sc * _bodyRy * sc) - _bodyRy;

        // Impact squash with a springy settle.
        p.SquashX = p.SquashY = 1f;
        p.SquashAtX = _pivotX;
        p.SquashAtY = _pivotY;
        float ie = (float)((nowMs - _impactAtMs) / 1000.0);
        if (ie >= 0f && ie < 0.7f)
        {
            float s = _impactAmp * MathF.Exp(-7f * ie) * MathF.Cos(20f * ie);
            if (_impactSide == 0f)
            {
                p.SquashX = 1f + 0.7f * s;
                p.SquashY = 1f - s;
            }
            else
            {
                p.SquashX = 1f - s;
                p.SquashY = 1f + 0.7f * s;
                p.SquashAtX = _pivotX + _impactSide * _bodyRx;
            }
        }

        // Jelly wobble after a wall bounce.
        float je = (float)((nowMs - _jellyAtMs) / 1000.0);
        if (je >= 0f && je < JellyDurationMs / 1000f)
        {
            float decay = MathF.Exp(-JellyDampingRate * je);
            float wobble = MathF.Cos(MathF.Tau * JellyFrequencyHz * je);
            float squash = _jellyAmp * decay * wobble;
            switch (_jellyAxis)
            {
                case 0: p.ScaleX *= 1f - squash; p.ScaleY *= 1f + squash; break;
                case 1: p.ScaleX *= 1f + squash; p.ScaleY *= 1f - squash; break;
                default: p.ScaleX *= 1f - squash; p.ScaleY *= 1f - squash; break;
            }
        }

        p.Air = _airborne ? MathF.Max(0f, (_workArea.Bottom - WindowH - FloorGap) - Y) : 0f;
        return p;
    }

    private void DrawShadow(Graphics g, Pose pose)
    {
        float k = 1f - Math.Clamp((pose.Jump + pose.Air) / 140f, 0f, 0.6f);
        float cx = _pivotX;
        float cy = _pivotY + 2f + pose.Air;

        using var brush = new SolidBrush(Color.FromArgb(20, 0, 0, 0));
        for (int i = 0; i < 5; i++)
        {
            float f = 1f - i * 0.16f;
            float rx = _bodyRx * 0.98f * k * f;
            float ry = 12f * k * f;
            g.FillEllipse(brush, cx - rx, cy - ry, rx * 2f, ry * 2f);
        }
    }

    private AnimatedGif? CurrentSprite(out bool loop)
    {
        loop = true;
        if (_petMainActive && _petMain is not null) return _petMain;
        if (_idleActive && _idleGif is not null) return _idleGif;
        if (_sleeping && _sleep is not null) return _sleep;
        if (_base is null) return null;
        loop = _baseAnimating || ((_activity == PetActivity.Walking || _running) && !_sleeping);
        return _base;
    }

    /// <summary>
    /// Returns the mirrored copy of a sprite when the pet faces left, building
    /// and caching it on first use. All the source GIFs face right.
    /// </summary>
    private AnimatedGif? Flipped(AnimatedGif? gif)
    {
        if (gif is null) return null;
        if (_facing <= 0) return gif;
        if (_flipped.TryGetValue(gif, out AnimatedGif? flip)) return flip;

        var frames = new GifFrame[gif.Frames.Length];
        for (int i = 0; i < gif.Frames.Length; i++)
        {
            var src = gif.Frames[i].Bitmap;
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppPArgb);
            using (var gg = Graphics.FromImage(dst))
            {
                gg.Clear(Color.Transparent);
                // Negative width mirrors horizontally about the vertical centre.
                gg.DrawImage(src, new Rectangle(src.Width, 0, -src.Width, src.Height));
            }
            frames[i] = new GifFrame(dst, gif.Frames[i].DelayMs);
        }

        var flipped = AnimatedGif.FromFrames(frames, gif.TotalMs, gif.Width, gif.Height);
        _flipped[gif] = flipped;
        return flipped;
    }

    private void DrawSprite(Graphics g, Pose pose)
    {
        bool loop;
        AnimatedGif? source = CurrentSprite(out loop);
        if (source is null) return;

        double clock = _petMainActive ? _petMainClockMs
            : _idleActive ? _idleClockMs
            : _baseClockMs;

        AnimatedGif gif = Flipped(source) ?? source;

        int frameIndex = gif.IsAnimated && loop ? gif.FrameAt(clock) : 0;
        // While grounded and calm the base sprite holds frame 0, matching the
        // plugin's paused GIF.
        if (ReferenceEquals(source, _base) && !_baseAnimating
            && !((_activity == PetActivity.Walking || _running) && !_sleeping)) frameIndex = 0;

        Bitmap frame = gif.Frames[frameIndex].Bitmap;

        float pivotX = _pivotX;
        float pivotY = _pivotY - pose.Jump;

        var state = g.Save();

        // Apply transforms in the same order as the reference renderer:
        // squash about the contact point, then lean about the pivot, then
        // roll about the body centre, then the lift correction.
        g.TranslateTransform(pose.SquashAtX, pose.SquashAtY);
        g.ScaleTransform(pose.SquashX, pose.SquashY);
        g.TranslateTransform(-pose.SquashAtX, -pose.SquashAtY);

        g.TranslateTransform(pivotX, pivotY);
        g.RotateTransform(pose.Angle * 180f / MathF.PI);
        g.TranslateTransform(0f, -pose.Lift);
        g.ScaleTransform(pose.ScaleX, pose.ScaleY);
        g.TranslateTransform(0f, -SpriteH / 2f);
        g.RotateTransform(pose.Spin * 180f / MathF.PI);

        byte alpha = (byte)(Config.AlphaPercent / 100f * 255f);

        var destRect = new RectangleF(-SpriteW / 2f, -SpriteH / 2f, SpriteW, SpriteH);
        if (alpha >= 255)
        {
            g.DrawImage(frame, destRect);
        }
        else
        {
            using var attrs = new ImageAttributes();
            var matrix = new ColorMatrix { Matrix33 = alpha / 255f };
            attrs.SetColorMatrix(matrix);
            g.DrawImage(frame, Rectangle.Round(destRect), 0, 0, frame.Width, frame.Height,
                GraphicsUnit.Pixel, attrs);
        }

        // The hand GIF draws on top while petting, offset upward like the
        // plugin's pet overlay view.
        if (IsPetting && _petHand is not null && _settings.PettingEnabled)
        {
            AnimatedGif hand = Flipped(_petHand) ?? _petHand;
            double handClock = _petMainClockMs;
            Bitmap handFrame = hand.Frames[hand.FrameAt(handClock)].Bitmap;
            float offset = SpriteH * 0.14f;
            var handRect = new RectangleF(-SpriteW / 2f, -SpriteH / 2f - offset, SpriteW, SpriteH);
            g.DrawImage(handFrame, handRect);
        }

        g.Restore(state);
    }

    /// <summary>Screen rect the window currently occupies.</summary>
    public Rectangle WindowBounds => new((int)MathF.Round(X), (int)MathF.Round(Y), WindowW, WindowH);

    public void Dispose()
    {
        _audio.Dispose();
        _bubble.Dispose();
        // Decoded frames are owned by the Assets cache and shared between pets,
        // so only the per-pet mirrored copies are ours to release.
        foreach (AnimatedGif f in _flipped.Values) f.Dispose();
        _flipped.Clear();
    }
}
