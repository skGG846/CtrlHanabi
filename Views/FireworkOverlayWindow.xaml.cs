using System.Windows;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using FormsScreen = System.Windows.Forms.Screen;
using CtrlHanabi.Models;
using CtrlHanabi.Services;
using CtrlHanabi.ViewModels;
using Microsoft.Win32;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace CtrlHanabi;

public partial class FireworkOverlayWindow : Window
{
    private const double ReferenceWidth = 1920;
    private const double ReferenceHeight = 1080;
    private const double FrameDeltaSeconds = 0.016;
    private const double PerspectiveDistance = 720;
    private const double MaxDepthOffset = 280;
    private const double FuseDelaySeconds = 0.11;
    private const double FuseDarkSeconds = 0.055;
    private const double RocketBaseGravity = 260;
    private const double RocketProgressGravity = 320;
    private const double RocketLaunchAverageGravity = 520;
    private const double RocketLaunchVelocityScale = 0.90;
    private const double RocketMaxHorizontalAccel = 16;
    private const double RocketBaseAirDrag = 0.992;
    private const double RocketVerticalAirDrag = 0.998;
    private const double RocketLateralAirDrag = 0.978;
    private const double RocketSwayFrequency = 5.4;
    private const double RocketSwayStrength = 36;
    private const double RocketWindShiftStrength = 18;
    private const double RocketLateRiseDragProgress = 0.38;
    private const int MinimumBurstPetalCount = 168;
    private const int LaunchBlastParticleCount = 34;
    private const int KobanaPetalCount = 12;
    private const int MaximumKobanaBurstCount = 3;
    private const int KobanaCapacityParticleCount = 36;
    private const int EstimatedRocketTrailCapacity = 80;
    private const int EstimatedBurstTrailFrames = 72;
    private const int MaxParticleTrailSegments = 4;
    private const int MaxRocketTrailSegments = 8;
    private static readonly double[] KobanaBurstProgressThresholds = [0.50, 0.68, 0.82];
    private const int StarmineMaxShots = 7;
    private const double StarmineIntervalJitterSeconds = 0.2;
    private const double LeafAscentEmitProgressStep = 0.064;
    private const int GroundLeafStarBurstCount = 8;
    private const double StarmineLaunchAngleJitter = 50;
    private const double SingleLaunchAngleJitter = 10;
    private const int MaxConcurrentRockets = 15;
    private const bool DisableGpuPhysicsDuringStarmine = false;

    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private static readonly nint HwndTopmost = -1;
    private const uint MonitorDefaultToNearest = 2;
    private const int MdtEffectiveDpi = 0;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private readonly DispatcherTimer _timer;
    private readonly List<Particle> _particles = [];
    private readonly List<TrailParticle> _trails = [];
    private readonly List<RenderTrail> _renderTrails = [];
    private readonly List<RenderParticle> _renderParticles = [];
    private readonly List<RenderRocket> _renderRockets = [];
    private readonly GpuParticlePhysics _gpuParticlePhysics = new();
    private readonly Random _random = new();
    private readonly FireworkOverlayViewModel _viewModel;
    private readonly ParticleSceneElement _scene = new();
    private readonly BurstPalette[] _burstPalettes =
    [
        new("strontium-red", WpfColor.FromRgb(246, 88, 76), WpfColor.FromRgb(255, 236, 232)),
        new("lithium-crimson", WpfColor.FromRgb(236, 74, 98), WpfColor.FromRgb(255, 233, 242)),
        new("sodium-yellow", WpfColor.FromRgb(255, 212, 94), WpfColor.FromRgb(255, 248, 216)),
        new("calcium-orange", WpfColor.FromRgb(255, 162, 82), WpfColor.FromRgb(255, 239, 214)),
        new("barium-green", WpfColor.FromRgb(126, 214, 108), WpfColor.FromRgb(233, 250, 224)),
        new("copper-blue", WpfColor.FromRgb(92, 170, 255), WpfColor.FromRgb(229, 242, 255)),
        new("potassium-violet", WpfColor.FromRgb(170, 136, 255), WpfColor.FromRgb(241, 236, 255))
    ];
    private readonly BurstPalette[] _kamuroPalettes =
    [
        new("kamuro-iron-bright", WpfColor.FromRgb(255, 236, 188), WpfColor.FromRgb(255, 250, 228)),
        new("kamuro-iron-ember", WpfColor.FromRgb(255, 205, 132), WpfColor.FromRgb(255, 238, 184)),
        new("kamuro-iron-deep", WpfColor.FromRgb(255, 172, 96), WpfColor.FromRgb(255, 222, 160))
    ];

    private DateTime _started;
    private readonly List<Rocket> _activeRockets = [];
    private readonly Queue<LaunchRequest> _launchQueue = new();
    private double _launchDelay;
    private bool _isStarmineActive;
    private bool _useConfiguredDisplayBounds;

    public FireworkOverlayWindow(HanabiSettings settings, ISettingsService settingsService)
    {
        _viewModel = new FireworkOverlayViewModel(settings, settingsService);
        InitializeComponent();
        RootHost.Children.Add(_scene);

        SourceInitialized += OnSourceInitialized;
        _ = new WindowInteropHelper(this).EnsureHandle();
        _useConfiguredDisplayBounds = false;
        ApplyOverlayBounds();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += (_, _) => UpdateFrame();
        Hide();
    }

    public void ShowFirework(WpfPoint screenPoint, bool forceStarmine = false)
    {
        _viewModel.ReloadSettings();
        _useConfiguredDisplayBounds = forceStarmine;
        Show();
        UpdateLayout();
        ApplyOverlayBounds();
        UpdateLayout();
        PrepareEffectStorage();
        ClearEffectStorage();
        QueueLaunchPattern(screenPoint, forceStarmine);
        _activeRockets.Clear();
        _launchDelay = 0;
        _started = DateTime.UtcNow;
        BringOverlayToFrontWithoutActivation();
        _timer.Start();
    }


    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
        {
            return;
        }

        var extendedStyle = GetWindowLongPtr(hwnd, GwlExStyle);
        SetWindowLongPtr(hwnd, GwlExStyle, extendedStyle | (nint)(WsExTransparent | WsExNoActivate));
    }

    private void BringOverlayToFrontWithoutActivation()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == nint.Zero)
        {
            return;
        }

        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    private void UpdateFrame()
    {
        try
        {
            UpdateFrameCore();
        }
        catch (Exception ex)
        {
            LogOverlayFailure("UpdateFrame failed: " + ex);
            _timer.Stop();
            _gpuParticlePhysics.Reset();
            Hide();
        }
    }

    private void UpdateFrameCore()
    {
        if (_isStarmineActive && _activeRockets.Count == 0 && _launchQueue.Count == 0)
        {
            _isStarmineActive = false;
        }

        if (_launchQueue.Count > 0)
        {
            _launchDelay -= FrameDeltaSeconds;
            while (_launchDelay <= 0 && _activeRockets.Count < MaxConcurrentRockets && _launchQueue.Count > 0)
            {
                SpawnNextLaunch();
            }
        }
        UpdateRockets();
        UpdateParticles();
        if (_activeRockets.Count == 0 && _launchQueue.Count == 0 && _particles.Count == 0 && _trails.Count == 0)
        {
            StopEffect();
            return;
        }

        RenderFrame();
    }

    private void UpdateRockets()
    {
        if (_activeRockets.Count == 0)
        {
            return;
        }

        for (var i = _activeRockets.Count - 1; i >= 0; i--)
        {
            var rocket = _activeRockets[i];
            var totalRise = Math.Max(rocket.OriginY - rocket.TargetY, 1);
            var progress = Math.Clamp((rocket.OriginY - rocket.Y) / totalRise, 0, 1);
            var gravity = RocketBaseGravity + (progress * RocketProgressGravity);
            var sway = Math.Sin(rocket.SwayPhase + (rocket.SwayTime * RocketSwayFrequency));
            var windShift = Math.Sin(rocket.WindPhase + (rocket.SwayTime * (RocketSwayFrequency * 0.47)));
            var driftFactor = 0.35 + (progress * 0.85);
            var dragBlend = Math.Clamp((progress - RocketLateRiseDragProgress) / (1 - RocketLateRiseDragProgress), 0, 1);
            var lateralDrag = Lerp(RocketBaseAirDrag, RocketLateralAirDrag, dragBlend);
            var verticalDrag = Lerp(RocketBaseAirDrag, RocketVerticalAirDrag, dragBlend);

            rocket.Vx += sway * ScalePixels(RocketSwayStrength * driftFactor, rocket.EffectScale) * FrameDeltaSeconds;
            rocket.Vx += windShift * ScalePixels(RocketWindShiftStrength * driftFactor, rocket.EffectScale) * FrameDeltaSeconds;
            rocket.Vy += gravity * FrameDeltaSeconds;
            rocket.Vx += rocket.HorizontalAccel * FrameDeltaSeconds;
            rocket.Vx *= lateralDrag;
            rocket.Vy *= verticalDrag;
            rocket.X += rocket.Vx * FrameDeltaSeconds;
            rocket.Y += rocket.Vy * FrameDeltaSeconds;
            rocket.SwayTime += FrameDeltaSeconds;
            EmitAscentEffect(rocket, progress);

            var startedFalling = rocket.Vy >= 0;

            if (!rocket.FuseStarted && startedFalling)
            {
                rocket.FuseStarted = true;
                rocket.ApexX = rocket.X;
                rocket.ApexY = rocket.Y;
            }

            if (rocket.FuseStarted)
            {
                rocket.BurstDelay += FrameDeltaSeconds;
                rocket.FuseHidden = true;
            }
            else
            {
                rocket.BurstDelay = 0;
                rocket.FuseHidden = false;
            }

            var shouldBurst = rocket.BurstDelay >= FuseDelaySeconds;
            if (!shouldBurst)
            {
                _activeRockets[i] = rocket;
                continue;
            }

            SpawnBurst(rocket);
            _activeRockets.RemoveAt(i);
        }
    }

    private void QueueLaunchPattern(WpfPoint screenPoint, bool forceStarmine)
    {
        _launchQueue.Clear();
        var localPoint = ScreenToOverlayPoint(screenPoint);
        var stageScreenPoint = forceStarmine
            ? OverlayToScreenPoint(new WpfPoint(Width * 0.5, Height * 0.5))
            : screenPoint;
        var stageDipPerDevicePixel = GetDipPerDevicePixel(stageScreenPoint);
        var launchPlan = _viewModel.BuildLaunchPlan(localPoint, forceStarmine, Width, Height, stageDipPerDevicePixel);
        _isStarmineActive = launchPlan.IsStarmineActive;
        foreach (var request in launchPlan.Requests)
        {
            _launchQueue.Enqueue(request);
        }
    }

    private void SpawnNextLaunch()
    {
        if (_launchQueue.Count == 0)
        {
            return;
        }

        var launch = _launchQueue.Dequeue();
        var screenPoint = OverlayToScreenPoint(new WpfPoint(launch.TargetX, launch.TargetY));
        var effectScale = GetDipPerDevicePixel(screenPoint) * GetStageScale(screenPoint);
        var launchY = GetLaunchY(new WpfPoint(launch.TargetX, launch.TargetY));
        var startX = launch.IsStarmine ? launch.TargetX : launch.TargetX + ((_random.NextDouble() - 0.5) * ScalePixels(36, effectScale));
        var altitudeFactor = 1 - Math.Clamp(launch.TargetY / Math.Max(Height, 1), 0, 1);
        var jitterScale = 1.2 + (altitudeFactor * 2.0);
        var launchAngleJitter = ScalePixels(launch.IsStarmine ? StarmineLaunchAngleJitter : SingleLaunchAngleJitter, effectScale);
        var arcPull = ((launch.TargetX - startX) * 1.8) + ((_random.NextDouble() - 0.5) * launchAngleJitter * jitterScale);
        var travel = Math.Max(launchY - launch.TargetY, 120);
        var curveGuideType = PickCurveGuideType(launch.IsStarmine);
        var burstKind = PickBurstKind(allowKamuro: !launch.IsStarmine);
        var chrysanthemumCharcoalPalette = _kamuroPalettes[_random.Next(_kamuroPalettes.Length)];
        var silverDragonTone = PickSilverDragonTone();
        var burstPalette = burstKind == BurstKind.KamuroGiku
            ? PickKamuroPalette()
            : PickBurstPalette();

        _activeRockets.Add(new Rocket
        {
            X = startX,
            Y = launchY,
            OriginX = startX,
            OriginY = launchY,
            TargetX = launch.TargetX,
            TargetY = launch.TargetY,
            ApexX = launch.TargetX,
            ApexY = launch.TargetY,
            Vy = CalculateRocketLaunchVelocity(travel),
            Vx = arcPull,
            SwayPhase = _random.NextDouble() * Math.PI * 2,
            WindPhase = _random.NextDouble() * Math.PI * 2,
            SwayTime = _random.NextDouble() * 0.45,
            HorizontalAccel = (_random.NextDouble() - 0.5) * (ScalePixels(RocketMaxHorizontalAccel, effectScale) * 2),
            BurstDelay = 0,
            FuseHidden = false,
            FuseStarted = false,
            TrailColor = burstPalette.Outer,
            CurveGuide = curveGuideType,
            LastTrailEmitProgress = 0,
            KobanaBurstCount = 0,
            PrevX = startX,
            PrevY = launchY,
            BurstKind = burstKind,
            BurstPalette = burstPalette,
            ChrysanthemumCharcoalPalette = chrysanthemumCharcoalPalette,
            SilverDragonTone = silverDragonTone,
            IsStarmine = launch.IsStarmine,
            BurstScale = launch.BurstScale,
            EffectScale = effectScale
        });

        EmitLaunchBlast(startX, launchY, effectScale);
        if (launch.IsStarmine)
        {
            EmitGroundLeafStars(startX, launchY, effectScale);
        }
        if (launch.IsStarmine && _launchQueue.Count > 0)
        {
            var nextDelay = _launchQueue.Peek().DelaySeconds;
            _launchDelay = nextDelay;
        }
        else
        {
            _launchDelay = launch.DelaySeconds + (_random.NextDouble() * StarmineIntervalJitterSeconds);
        }
    }

    private void SpawnBurst(Rocket rocket)
    {
        var x = rocket.ApexX;
        var y = rocket.ApexY;
        var petalCount = Math.Max(_viewModel.Settings.ParticleCount * 2, MinimumBurstPetalCount);
        var outerRadius = ScalePixels(_viewModel.Settings.ExplosionRadius * 1.18, rocket.EffectScale) * rocket.BurstScale;
        var isChrysanthemum = rocket.BurstKind == BurstKind.Chrysanthemum;
        var isBotan = rocket.BurstKind == BurstKind.Botan;
        var isKamuro = rocket.BurstKind == BurstKind.KamuroGiku;
        var chrysanthemumTransitionColor = PickWeightedBurstPalette().Outer;

        for (var i = 0; i < petalCount; i++)
        {
            var t = (i + 0.5) / petalCount;
            var zDirection = 1 - (2 * t);
            var phi = Math.Acos(Math.Clamp(zDirection, -1, 1));
            var theta = Math.PI * (3 - Math.Sqrt(5)) * i;
            var sinPhi = Math.Sin(phi);
            var dirX = Math.Cos(theta) * sinPhi;
            var dirY = Math.Sin(theta) * sinPhi;
            var radialFactor = 1.0;
            var speed = outerRadius * (isKamuro
                ? (1.56 + _random.NextDouble() * 0.05) * radialFactor * 0.8
                : isBotan
                ? (1.82 + _random.NextDouble() * 0.025) * radialFactor
                : 1.7866666666666666 + _random.NextDouble() * 0.025);
            var petalJitter = 1.0;

            _particles.Add(new Particle
            {
                X = x,
                Y = y,
                PrevX = x,
                PrevY = y,
                BurstX = x,
                BurstY = y,
                Kind = rocket.BurstKind,
                Z = 0,
                PrevZ = 0,
                Vx = dirX * speed * petalJitter,
                Vy = dirY * speed * petalJitter,
                Vz = zDirection * speed * (isBotan ? 0.98 : isKamuro ? 0.8 : 0.92),
                Life = 1,
                InitialLife = 1,
                Decay = isKamuro
                    ? 0.24 + _random.NextDouble() * 0.08
                    : isBotan
                    ? 0.64 + _random.NextDouble() * 0.14
                    : 0.57 + _random.NextDouble() * 0.21,
                Size = isKamuro
                    ? ScalePixels(3.2 + _random.NextDouble() * 1.8, rocket.EffectScale)
                    : isBotan
                    ? ScalePixels(3.0 + _random.NextDouble() * 1.5, rocket.EffectScale)
                    : ScalePixels(2.4 + _random.NextDouble() * 1.5, rocket.EffectScale),
                StartColor = isChrysanthemum ? rocket.ChrysanthemumCharcoalPalette.Shell : rocket.BurstPalette.Shell,
                EndColor = isChrysanthemum ? rocket.ChrysanthemumCharcoalPalette.Outer : rocket.BurstPalette.Outer,
                TransitionColor = isChrysanthemum
                    ? chrysanthemumTransitionColor
                    : rocket.BurstPalette.Outer,
                TrailStrength = isKamuro ? 7.6 : isBotan ? 0.8 : 1.45,
                Drag = isKamuro
                    ? 0.974 + _random.NextDouble() * 0.008
                    : isBotan
                    ? 0.958 + _random.NextDouble() * 0.008
                    : 0.968 + _random.NextDouble() * 0.008,
                FlickerPhase = _random.NextDouble() * Math.PI * 2,
                Twinkle = isKamuro ? i % 7 == 0 : isBotan ? i % 14 == 0 : i % 9 == 0,
                EffectScale = rocket.EffectScale
            });
        }
    }

    private void UpdateParticles()
    {
        var useGpuPhysics = !_isStarmineActive || !DisableGpuPhysicsDuringStarmine;
        if (!useGpuPhysics)
        {
            _gpuParticlePhysics.Reset();
        }

        var gpuIntegratedParticleCount = useGpuPhysics
            ? _gpuParticlePhysics.TryApplyPending(_particles)
            : 0;
        var particleCountBeforeUpdate = _particles.Count;
        var particleWriteIndex = _particles.Count - 1;
        for (var i = _particles.Count - 1; i >= 0; i--)
        {
            var p = _particles[i];
            var age = 1 - (p.Life / p.InitialLife);
            if (i >= gpuIntegratedParticleCount)
            {
                p.PrevX = p.X;
                p.PrevY = p.Y;
                p.PrevZ = p.Z;
                p.Vy += ScalePixels(82 + age * 20, p.EffectScale) * FrameDeltaSeconds;
                p.Vx *= p.Drag;
                p.Vy *= p.Drag;
                p.Vz *= p.Drag;
                p.X += p.Vx * FrameDeltaSeconds;
                p.Y += p.Vy * FrameDeltaSeconds;
                p.Z = Math.Clamp(p.Z + p.Vz * FrameDeltaSeconds, -MaxDepthOffset, MaxDepthOffset);
                p.Life -= FrameDeltaSeconds * p.Decay;
            }

            if (p.Life > 0)
            {
                age = 1 - (p.Life / p.InitialLife);
                var color = GetParticleColor(p, age);
                p.Age = age;
                p.CurrentColor = color;
                var botanBloom = p.Kind == BurstKind.Botan ? GetBotanBloomFactor(age) : 1;
                var kamuroGlow = p.Kind == BurstKind.KamuroGiku ? GetKamuroGlowFactor(age) : 1;
                var centerFade = p.Kind == BurstKind.KamuroGiku ? GetKamuroCenterFadeFactor(p, age) : 1;
                var softFade = p.Kind == BurstKind.KamuroGiku ? GetKamuroSoftFade(age) : 1;
                var lifeFactor = p.Kind == BurstKind.KamuroGiku
                    ? Math.Clamp((p.Life * 0.7) + (centerFade * 0.3), 0, 1)
                    : p.Life;
                var fadeBase = lifeFactor * botanBloom * kamuroGlow * centerFade * softFade;
                p.CurrentCoreColor = LerpColor(color, p.TransitionColor, 0.3 + age * 0.2);
                p.RenderGlowSize = p.Size * (2.25 - age * 0.2) * kamuroGlow;
                p.RenderCoreSize = p.Size * (0.92 - age * 0.05) * (0.92 + kamuroGlow * 0.08);
                p.RenderGlowOpacityBase = fadeBase * 0.11;
                p.RenderCoreOpacityBase = fadeBase * 0.68;
                var trailLife = p.Kind == BurstKind.KamuroGiku
                    ? p.Life * (0.3 + p.TrailStrength * 1.05)
                    : p.Life * (0.3 + p.TrailStrength * 0.2);
                var trailSize = p.Kind == BurstKind.KamuroGiku
                    ? p.Size * (0.30 + p.TrailStrength * 0.032)
                    : p.Size * (0.92 + p.TrailStrength * 0.14);
                var dx = p.X - p.PrevX;
                var dy = p.Y - p.PrevY;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                var segmentSpacing = Math.Max(ScalePixels(1.35, p.EffectScale), trailSize * 0.55);
                var segments = Math.Clamp((int)Math.Ceiling(distance / segmentSpacing), 1, MaxParticleTrailSegments);
                var segmentStep = 1.0 / segments;
                var trailColor = WithAlpha(color, 168);
                for (var segment = 1; segment <= segments; segment++)
                {
                    var segmentT = segment * segmentStep;
                    _trails.Add(new TrailParticle(
                        p.PrevX + (dx * segmentT),
                        p.PrevY + (dy * segmentT),
                        p.PrevZ + ((p.Z - p.PrevZ) * segmentT),
                        p.BurstX,
                        p.BurstY,
                        trailLife * (0.9 + segmentT * 0.1),
                        trailSize,
                        trailColor,
                        p.EffectScale));
                }

                _particles[particleWriteIndex--] = p;
            }
        }

        if (particleWriteIndex >= 0)
        {
            _particles.RemoveRange(0, particleWriteIndex + 1);
        }

        if (useGpuPhysics)
        {
            var canReuseGpuParticleBuffer = gpuIntegratedParticleCount == particleCountBeforeUpdate
                && _particles.Count == particleCountBeforeUpdate;
            _gpuParticlePhysics.ScheduleUpdate(_particles, FrameDeltaSeconds, MaxDepthOffset, canReuseGpuParticleBuffer);
        }

        var trailWriteIndex = 0;
        for (var i = 0; i < _trails.Count; i++)
        {
            var t = _trails[i];
            t.Life -= FrameDeltaSeconds * 1.2;
            t.Size *= 0.992;
            if (t.Life > 0 && t.Size > ScalePixels(0.8, t.EffectScale))
            {
                _trails[trailWriteIndex++] = t;
            }
        }

        if (trailWriteIndex < _trails.Count)
        {
            _trails.RemoveRange(trailWriteIndex, _trails.Count - trailWriteIndex);
        }
    }

    private void RenderFrame()
    {
        _renderTrails.Clear();
        _renderTrails.EnsureCapacity(_trails.Count);
        for (var i = 0; i < _trails.Count; i++)
        {
            var t = _trails[i];
            var perspective = GetPerspectiveScale(t.Z);
            var projectedX = t.BurstX + ((t.X - t.BurstX) * perspective);
            var projectedY = t.BurstY + ((t.Y - t.BurstY) * perspective);
            _renderTrails.Add(new RenderTrail(projectedX, projectedY, t.Size, t.Color, Math.Clamp(t.Life, 0, 1)));
        }

        _renderParticles.Clear();
        _renderParticles.EnsureCapacity(_particles.Count);
        var shimmerTime = _started.Ticks / (double)TimeSpan.TicksPerSecond * 7;
        for (var i = 0; i < _particles.Count; i++)
        {
            var p = _particles[i];
            var age = p.Age;
            var shimmer = p.Twinkle ? 0.86 + 0.14 * Math.Sin(shimmerTime + p.FlickerPhase + age * 18) : 1;
            var perspective = GetPerspectiveScale(p.Z);
            var projectedX = p.BurstX + ((p.X - p.BurstX) * perspective);
            var projectedY = p.BurstY + ((p.Y - p.BurstY) * perspective);
            _renderParticles.Add(new RenderParticle(
                projectedX,
                projectedY,
                p.RenderGlowSize,
                p.RenderCoreSize,
                p.CurrentColor,
                p.CurrentCoreColor,
                p.RenderGlowOpacityBase * shimmer,
                Math.Clamp(p.RenderCoreOpacityBase * shimmer, 0, 1)));
        }

        _renderRockets.Clear();
        _renderRockets.EnsureCapacity(_activeRockets.Count);
        for (var i = 0; i < _activeRockets.Count; i++)
        {
            var rocket = _activeRockets[i];
            _renderRockets.Add(new RenderRocket(rocket.X, rocket.Y, rocket.OriginX, rocket.OriginY, rocket.TrailColor, rocket.FuseHidden, rocket.EffectScale));
        }

        _scene.UpdateScene(
            _renderTrails,
            _renderParticles,
            _renderRockets);
    }

    private void StopEffect()
    {
        _isStarmineActive = false;
        _timer.Stop();
        _gpuParticlePhysics.Reset();
        _scene.ClearScene();
        Hide();
    }

    private void ApplyOverlayBounds()
    {
        if (_useConfiguredDisplayBounds)
        {
            ApplyConfiguredDisplayBounds();
            return;
        }

        ApplyVirtualScreenBounds();
    }

    private void ApplyVirtualScreenBounds()
    {
        // WPF SystemParameters.VirtualScreen* values are already DPI-adjusted DIPs.
        // Converting them again shrinks the overlay at non-100% display scaling.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    private void ApplyConfiguredDisplayBounds()
    {
        var screen = ResolveConfiguredScreen();
        var bounds = DeviceRectToDip(new Rect(
            screen.Bounds.Left,
            screen.Bounds.Top,
            screen.Bounds.Width,
            screen.Bounds.Height));
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
    }

    private FormsScreen ResolveConfiguredScreen()
    {
        var screens = FormsScreen.AllScreens;
        if (screens.Length == 0)
        {
            return FormsScreen.PrimaryScreen ?? throw new InvalidOperationException("No display is available.");
        }

        var configuredDisplayIndex = _viewModel.Settings.StarmineDisplayIndex;
        var orderedScreens = screens
            .OrderByDescending(screen => screen.Primary)
            .ThenBy(screen => screen.Bounds.Left)
            .ThenBy(screen => screen.Bounds.Top)
            .ToArray();

        var zeroBasedIndex = configuredDisplayIndex - 1;
        if (zeroBasedIndex < 0 || zeroBasedIndex >= orderedScreens.Length)
        {
            zeroBasedIndex = 0;
        }

        return orderedScreens[zeroBasedIndex];
    }

    private double GetLaunchY(WpfPoint overlayPoint)
    {
        var screenPoint = OverlayToScreenPoint(overlayPoint);
        var screen = FormsScreen.FromPoint(new((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)));
        var launchScreenPoint = new WpfPoint(screenPoint.X, screen.WorkingArea.Bottom - 8);
        return ScreenToOverlayPoint(launchScreenPoint).Y;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        var previousBounds = new Rect(Left, Top, Width, Height);
        BurstApexRocketsBeforeDisplayChange();
        _viewModel.ReloadSettings();
        ApplyOverlayBounds();
        RemapActiveEffect(previousBounds);
        BringOverlayToFrontWithoutActivation();
    }

    private void RemapActiveEffect(Rect previousBounds)
    {
        if (previousBounds.IsEmpty || previousBounds.Width <= 0 || previousBounds.Height <= 0)
        {
            return;
        }

        if (_activeRockets.Count == 0 && _launchQueue.Count == 0 && _particles.Count == 0 && _trails.Count == 0)
        {
            return;
        }

        if (ShouldScaleActiveEffect(previousBounds))
        {
            ScaleActiveEffect(Width / previousBounds.Width, Height / previousBounds.Height);
        }
        else
        {
            var dx = previousBounds.Left - Left;
            var dy = previousBounds.Top - Top;
            TranslateActiveEffect(dx, dy);
        }

        var keepVisibleOffset = GetKeepVisibleOffset();
        if (keepVisibleOffset.X != 0 || keepVisibleOffset.Y != 0)
        {
            TranslateActiveEffect(keepVisibleOffset.X, keepVisibleOffset.Y);
        }

        _gpuParticlePhysics.Reset();
        _scene.ResetRenderer();
    }

    private void BurstApexRocketsBeforeDisplayChange()
    {
        for (var i = _activeRockets.Count - 1; i >= 0; i--)
        {
            var rocket = _activeRockets[i];
            var totalRise = Math.Max(rocket.OriginY - rocket.TargetY, 1);
            var progress = Math.Clamp((rocket.OriginY - rocket.Y) / totalRise, 0, 1);
            if (!rocket.FuseStarted && rocket.Vy < 0 && progress < 0.92)
            {
                continue;
            }

            if (!rocket.FuseStarted)
            {
                rocket.FuseStarted = true;
                rocket.ApexX = rocket.X;
                rocket.ApexY = rocket.Y;
            }

            SpawnBurst(rocket);
            _activeRockets.RemoveAt(i);
        }
    }

    private bool ShouldScaleActiveEffect(Rect previousBounds)
    {
        const double originTolerance = 2;
        const double sizeTolerance = 2;
        var sameOrigin =
            Math.Abs(previousBounds.Left - Left) <= originTolerance &&
            Math.Abs(previousBounds.Top - Top) <= originTolerance;
        var sizeChanged =
            Math.Abs(previousBounds.Width - Width) > sizeTolerance ||
            Math.Abs(previousBounds.Height - Height) > sizeTolerance;
        return sameOrigin && sizeChanged && Width > 0 && Height > 0;
    }

    private void ScaleActiveEffect(double scaleX, double scaleY)
    {
        if (scaleX <= 0 || scaleY <= 0 || (scaleX == 1 && scaleY == 1))
        {
            return;
        }

        for (var i = 0; i < _activeRockets.Count; i++)
        {
            var rocket = _activeRockets[i];
            rocket.X *= scaleX;
            rocket.Y *= scaleY;
            rocket.OriginX *= scaleX;
            rocket.OriginY *= scaleY;
            rocket.TargetX *= scaleX;
            rocket.TargetY *= scaleY;
            rocket.ApexX *= scaleX;
            rocket.ApexY *= scaleY;
            rocket.PrevX *= scaleX;
            rocket.PrevY *= scaleY;
            rocket.Vx *= scaleX;
            rocket.Vy *= scaleY;
            rocket.HorizontalAccel *= scaleX;
            _activeRockets[i] = rocket;
        }

        var sizeScale = Math.Sqrt(scaleX * scaleY);
        for (var i = 0; i < _particles.Count; i++)
        {
            var particle = _particles[i];
            particle.X *= scaleX;
            particle.Y *= scaleY;
            particle.PrevX *= scaleX;
            particle.PrevY *= scaleY;
            particle.BurstX *= scaleX;
            particle.BurstY *= scaleY;
            particle.Vx *= scaleX;
            particle.Vy *= scaleY;
            particle.Size *= sizeScale;
            particle.RenderGlowSize *= sizeScale;
            particle.RenderCoreSize *= sizeScale;
            _particles[i] = particle;
        }

        for (var i = 0; i < _trails.Count; i++)
        {
            var trail = _trails[i];
            trail.X *= scaleX;
            trail.Y *= scaleY;
            trail.BurstX *= scaleX;
            trail.BurstY *= scaleY;
            trail.Size *= sizeScale;
            _trails[i] = trail;
        }

        if (_launchQueue.Count > 0)
        {
            var launches = _launchQueue.ToArray();
            _launchQueue.Clear();
            foreach (var launch in launches)
            {
                _launchQueue.Enqueue(launch with
                {
                    TargetX = launch.TargetX * scaleX,
                    TargetY = launch.TargetY * scaleY
                });
            }
        }
    }

    private Vector GetKeepVisibleOffset()
    {
        var anchor = GetEffectAnchor();
        if (anchor is null || Width <= 0 || Height <= 0)
        {
            return default;
        }

        const double margin = 24;
        var x = Math.Clamp(anchor.Value.X, margin, Math.Max(margin, Width - margin));
        var y = Math.Clamp(anchor.Value.Y, margin, Math.Max(margin, Height - margin));
        return new Vector(x - anchor.Value.X, y - anchor.Value.Y);
    }

    private WpfPoint? GetEffectAnchor()
    {
        if (_activeRockets.Count > 0)
        {
            var rocket = _activeRockets[^1];
            return new WpfPoint(rocket.X, rocket.Y);
        }

        if (_particles.Count > 0)
        {
            var particle = _particles[^1];
            return new WpfPoint(particle.BurstX, particle.BurstY);
        }

        if (_trails.Count > 0)
        {
            var trail = _trails[^1];
            return new WpfPoint(trail.BurstX, trail.BurstY);
        }

        if (_launchQueue.Count > 0)
        {
            var launch = _launchQueue.Peek();
            return new WpfPoint(launch.TargetX, launch.TargetY);
        }

        return null;
    }

    private void TranslateActiveEffect(double dx, double dy)
    {
        if (dx == 0 && dy == 0)
        {
            return;
        }

        for (var i = 0; i < _activeRockets.Count; i++)
        {
            var rocket = _activeRockets[i];
            rocket.X += dx;
            rocket.Y += dy;
            rocket.OriginX += dx;
            rocket.OriginY += dy;
            rocket.TargetX += dx;
            rocket.TargetY += dy;
            rocket.ApexX += dx;
            rocket.ApexY += dy;
            rocket.PrevX += dx;
            rocket.PrevY += dy;
            _activeRockets[i] = rocket;
        }

        for (var i = 0; i < _particles.Count; i++)
        {
            var particle = _particles[i];
            particle.X += dx;
            particle.Y += dy;
            particle.PrevX += dx;
            particle.PrevY += dy;
            particle.BurstX += dx;
            particle.BurstY += dy;
            _particles[i] = particle;
        }

        for (var i = 0; i < _trails.Count; i++)
        {
            var trail = _trails[i];
            trail.X += dx;
            trail.Y += dy;
            trail.BurstX += dx;
            trail.BurstY += dy;
            _trails[i] = trail;
        }

        if (_launchQueue.Count > 0)
        {
            var launches = _launchQueue.ToArray();
            _launchQueue.Clear();
            foreach (var launch in launches)
            {
                _launchQueue.Enqueue(launch with
                {
                    TargetX = launch.TargetX + dx,
                    TargetY = launch.TargetY + dy
                });
            }
        }
    }

    private static void LogOverlayFailure(string message)
    {
        RuntimeLogging.AppendD3D11Log(message);
    }

    private WpfPoint ScreenToOverlayPoint(WpfPoint screenPoint)
    {
        try
        {
            return PointFromScreen(screenPoint);
        }
        catch
        {
            var dipPoint = DeviceToDip(screenPoint);
            return new WpfPoint(dipPoint.X - Left, dipPoint.Y - Top);
        }
    }

    private WpfPoint OverlayToScreenPoint(WpfPoint overlayPoint)
    {
        try
        {
            return PointToScreen(overlayPoint);
        }
        catch
        {
            return DipToDevice(new WpfPoint(overlayPoint.X + Left, overlayPoint.Y + Top));
        }
    }

    private Rect DeviceRectToDip(Rect deviceRect)
    {
        var topLeft = DeviceToDip(new WpfPoint(deviceRect.Left, deviceRect.Top));
        var bottomRight = DeviceToDip(new WpfPoint(deviceRect.Right, deviceRect.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private WpfPoint DeviceToDip(WpfPoint point)
    {
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return transform.Transform(point);
    }

    private WpfPoint DipToDevice(WpfPoint point)
    {
        var source = PresentationSource.FromVisual(this);
        var transform = source?.CompositionTarget?.TransformToDevice ?? Matrix.Identity;
        return transform.Transform(point);
    }

    protected override void OnClosed(EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _gpuParticlePhysics.Dispose();
        base.OnClosed(e);
    }

    private BurstPalette PickBurstPalette() => PickWeightedBurstPalette();
    private BurstPalette PickKamuroPalette() => _kamuroPalettes[_random.Next(_kamuroPalettes.Length)];

    private BurstPalette PickWeightedBurstPalette()
    {
        // green/blue/violet: 20% each, others: 10% each
        var roll = _random.NextDouble();
        if (roll < 0.10) return _burstPalettes[0]; // red
        if (roll < 0.20) return _burstPalettes[1]; // crimson
        if (roll < 0.30) return _burstPalettes[2]; // yellow
        if (roll < 0.40) return _burstPalettes[3]; // orange
        if (roll < 0.60) return _burstPalettes[4]; // green
        if (roll < 0.80) return _burstPalettes[5]; // blue
        return _burstPalettes[6]; // violet
    }

    private BurstKind PickBurstKind(bool allowKamuro = true)
    {
        var roll = _random.NextDouble();
        if (!allowKamuro)
        {
            return roll < 0.5 ? BurstKind.Chrysanthemum : BurstKind.Botan;
        }

        if (roll < 0.45)
        {
            return BurstKind.Chrysanthemum;
        }

        if (roll < 0.90)
        {
            return BurstKind.Botan;
        }

        return BurstKind.KamuroGiku;
    }

    private void EmitAscentEffect(Rocket rocket, double progress)
    {
        if (rocket.CurveGuide == CurveGuideType.LeafSplay)
        {
            EmitLeafSplayTrail(rocket, progress);
            return;
        }

        if (rocket.CurveGuide == CurveGuideType.SilverDragon)
        {
            EmitSilverDragonTrail(rocket, progress);
            return;
        }

        if (rocket.CurveGuide == CurveGuideType.Kobana)
        {
            EmitKobanaBursts(rocket, progress);
        }
    }

    private void EmitLaunchBlast(double x, double y, double effectScale)
    {
        for (var i = 0; i < LaunchBlastParticleCount; i++)
        {
            var heat = _random.NextDouble();
            var lift = _random.NextDouble();
            var spread = ScalePixels(4 + lift * 15, effectScale);
            var px = x + ((_random.NextDouble() - 0.5) * spread);
            var py = y - ScalePixels(7 + (lift * 30), effectScale) + ((_random.NextDouble() - 0.5) * ScalePixels(5, effectScale));
            var flame = LerpColor(
                WpfColor.FromRgb(255, 92, 28),
                WpfColor.FromRgb(255, 245, 174),
                heat);

            _trails.Add(new TrailParticle(
                px,
                py,
                0,
                px,
                py,
                0.22 + (_random.NextDouble() * 0.28),
                ScalePixels(4.0 + ((1 - lift) * 7.5) + (_random.NextDouble() * 2.0), effectScale),
                WithAlpha(flame, (byte)(170 + _random.Next(70))),
                effectScale));
        }
    }

    private void EmitSilverDragonTrail(Rocket rocket, double progress)
    {
        var heat = 1 - progress;
        var charcoal = rocket.SilverDragonTone == SilverDragonTone.Charcoal
            ? rocket.ChrysanthemumCharcoalPalette.Shell
            : WpfColor.FromRgb(196, 208, 220);
        var ember = rocket.SilverDragonTone == SilverDragonTone.Charcoal
            ? rocket.ChrysanthemumCharcoalPalette.Outer
            : WpfColor.FromRgb(244, 250, 255);
        var body = LerpColor(charcoal, ember, 0.42 + (heat * 0.44) + (_random.NextDouble() * 0.08));
        var dx = rocket.X - rocket.PrevX;
        var dy = rocket.Y - rocket.PrevY;
        var distance = Math.Sqrt((dx * dx) + (dy * dy));
        var steps = Math.Clamp((int)Math.Ceiling(distance / ScalePixels(2.8, rocket.EffectScale)), 1, MaxRocketTrailSegments);
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (double)steps;
            var px = rocket.PrevX + (dx * t);
            var py = rocket.PrevY + (dy * t);
            _trails.Add(new TrailParticle(
                px,
                py,
                0,
                px,
                py,
                1.05 + _random.NextDouble() * 0.5,
                ScalePixels(3.1 + _random.NextDouble() * 1.8, rocket.EffectScale),
                WithAlpha(body, 198),
                rocket.EffectScale));
        }

        rocket.PrevX = rocket.X;
        rocket.PrevY = rocket.Y;
        rocket.LastTrailEmitProgress = progress;
    }

    private void EmitLeafSplayTrail(Rocket rocket, double progress)
    {
        if (rocket.IsStarmine)
        {
            return;
        }

        if ((progress - rocket.LastTrailEmitProgress) < LeafAscentEmitProgressStep)
        {
            return;
        }

        EmitGroundLeafStars(rocket.OriginX, rocket.OriginY, rocket.EffectScale);
        rocket.LastTrailEmitProgress = progress;
    }

    private void EmitGroundLeafStars(double originX, double originY, double effectScale)
    {
        var transitionColor = PickWeightedBurstPalette().Outer;
        var botanPalette = PickWeightedBurstPalette();

        for (var i = 0; i < GroundLeafStarBurstCount; i++)
        {
            var baseX = originX + ((_random.NextDouble() - 0.5) * ScalePixels(16, effectScale));
            var baseY = originY - (_random.NextDouble() * ScalePixels(1.5, effectScale));
            var yaw = _random.NextDouble() * Math.PI * 2;
            var lateralSpeed = ScalePixels(132 + (_random.NextDouble() * 56), effectScale);
            var speedX = Math.Cos(yaw) * lateralSpeed;
            var speedZ = Math.Sin(yaw) * lateralSpeed;
            var speedY = -ScalePixels(152 * 5, effectScale);

            _particles.Add(new Particle
            {
                X = baseX,
                Y = baseY,
                PrevX = baseX,
                PrevY = baseY,
                BurstX = baseX,
                BurstY = baseY,
                Kind = BurstKind.Botan,
                Z = 0,
                PrevZ = 0,
                Vx = speedX,
                Vy = speedY,
                Vz = speedZ,
                Life = 1,
                InitialLife = 1,
                Decay = 0.64 + _random.NextDouble() * 0.14,
                Size = ScalePixels(3.0 + _random.NextDouble() * 1.5, effectScale),
                StartColor = botanPalette.Shell,
                EndColor = botanPalette.Outer,
                TransitionColor = botanPalette.Outer,
                TrailStrength = 0.8,
                Drag = 0.958 + _random.NextDouble() * 0.008,
                FlickerPhase = _random.NextDouble() * Math.PI * 2,
                Twinkle = true,
                EffectScale = effectScale
            });
        }
    }

    private void EmitKobanaBursts(Rocket rocket, double progress)
    {
        if (rocket.KobanaBurstCount >= MaximumKobanaBurstCount)
        {
            return;
        }

        if (progress < KobanaBurstProgressThresholds[rocket.KobanaBurstCount])
        {
            return;
        }

        SpawnKobanaBurst(rocket.X, rocket.Y, rocket.EffectScale);
        rocket.KobanaBurstCount++;
    }

    private void SpawnKobanaBurst(double x, double y, double effectScale)
    {
        var color = _burstPalettes[_random.Next(_burstPalettes.Length)];
        for (var i = 0; i < KobanaPetalCount; i++)
        {
            var angle = (Math.PI * 2 * i) / KobanaPetalCount;
            var speed = 46 + _random.NextDouble() * 26;
            _particles.Add(new Particle
            {
                X = x,
                Y = y,
                BurstX = x,
                BurstY = y,
                Kind = BurstKind.Botan,
                Z = 0,
                Vx = Math.Cos(angle) * ScalePixels(speed, effectScale),
                Vy = Math.Sin(angle) * ScalePixels(speed, effectScale),
                Vz = (_random.NextDouble() - 0.5) * ScalePixels(15, effectScale),
                Life = 1.0 + _random.NextDouble() * 0.25,
                InitialLife = 1.0,
                Decay = 0.9 + _random.NextDouble() * 0.25,
                Size = ScalePixels(1.5 + _random.NextDouble() * 0.9, effectScale),
                StartColor = color.Shell,
                EndColor = color.Outer,
                TransitionColor = color.Outer,
                TrailStrength = 0.42,
                Drag = 0.935 + _random.NextDouble() * 0.02,
                FlickerPhase = _random.NextDouble() * Math.PI * 2,
                Twinkle = false,
                EffectScale = effectScale
            });
        }
    }

    private CurveGuideType PickCurveGuideType(bool preferLeafSplay = false)
    {
        if (preferLeafSplay)
        {
            return CurveGuideType.LeafSplay;
        }

        var roll = _random.NextDouble();
        if (roll < 0.30)
        {
            return CurveGuideType.None;
        }

        if (roll < 0.90)
        {
            return CurveGuideType.SilverDragon;
        }

        return CurveGuideType.Kobana;
    }

    private SilverDragonTone PickSilverDragonTone() => _random.Next(2) == 0 ? SilverDragonTone.Charcoal : SilverDragonTone.Silver;

    private static WpfColor LerpColor(WpfColor from, WpfColor to, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return WpfColor.FromArgb(
            (byte)Math.Round(from.A + (to.A - from.A) * t),
            (byte)Math.Round(from.R + (to.R - from.R) * t),
            (byte)Math.Round(from.G + (to.G - from.G) * t),
            (byte)Math.Round(from.B + (to.B - from.B) * t));
    }

    private static double Lerp(double from, double to, double t)
        => from + ((to - from) * Math.Clamp(t, 0, 1));

    private static WpfColor WithAlpha(WpfColor color, byte alpha) => WpfColor.FromArgb(alpha, color.R, color.G, color.B);

    private static WpfColor GetParticleColor(Particle particle, double age)
    {
        if (particle.Kind != BurstKind.Chrysanthemum)
        {
            return LerpColor(particle.StartColor, particle.EndColor, Math.Clamp(age * 1.08, 0, 1));
        }

        // Chrysanthemum: start with Fe-like spark color, then shift into 7-tone flame palette.
        const double switchAge = 0.25;
        if (age <= switchAge)
        {
            var earlyT = Math.Clamp(age / switchAge, 0, 1);
            return LerpColor(particle.StartColor, particle.EndColor, earlyT);
        }

        var transitionWindow = Math.Max((1 - switchAge) * 0.10, 0.001);
        var lateT = Math.Clamp((age - switchAge) / transitionWindow, 0, 1);
        return LerpColor(particle.EndColor, particle.TransitionColor, lateT);
    }

    private static double GetPerspectiveScale(double z)
    {
        var clampedZ = Math.Clamp(z, -MaxDepthOffset, MaxDepthOffset);
        var denominator = PerspectiveDistance - clampedZ;
        return Math.Clamp(PerspectiveDistance / denominator, 0.72, 1.45);
    }

    private static double CalculateRocketLaunchVelocity(double travel)
    {
        var low = Math.Sqrt(2 * RocketLaunchAverageGravity * travel) * RocketLaunchVelocityScale;
        var high = low;

        while (SimulateRocketApexRise(high, travel) < travel)
        {
            high *= 1.12;
            if (high > 4000)
            {
                break;
            }
        }

        for (var i = 0; i < 20; i++)
        {
            var mid = (low + high) * 0.5;
            if (SimulateRocketApexRise(mid, travel) < travel)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return -high;
    }

    private static double SimulateRocketApexRise(double launchSpeed, double travel)
    {
        var y = 0.0;
        var vy = -launchSpeed;
        var totalRise = Math.Max(travel, 1);

        for (var i = 0; i < 240; i++)
        {
            var progress = Math.Clamp(-y / totalRise, 0, 1);
            var gravity = RocketBaseGravity + (progress * RocketProgressGravity);
            var dragBlend = Math.Clamp((progress - RocketLateRiseDragProgress) / (1 - RocketLateRiseDragProgress), 0, 1);
            var verticalDrag = Lerp(RocketBaseAirDrag, RocketVerticalAirDrag, dragBlend);

            vy += gravity * FrameDeltaSeconds;
            vy *= verticalDrag;
            y += vy * FrameDeltaSeconds;

            if (vy >= 0)
            {
                break;
            }
        }

        return -y;
    }

    private static double GetBotanBloomFactor(double age)
    {
        if (age <= 0.35)
        {
            return 0.24;
        }

        if (age >= 0.72)
        {
            return 1.18;
        }

        var t = (age - 0.35) / 0.37;
        return 0.24 + (0.94 * t * t * (3 - (2 * t)));
    }

    private static double GetKamuroGlowFactor(double age)
    {
        if (age <= 0.22)
        {
            return 0.84;
        }

        if (age >= 0.78)
        {
            return 1.12;
        }

        var t = (age - 0.22) / 0.56;
        return 0.84 + (0.28 * t * t * (3 - (2 * t)));
    }

    private static double GetKamuroCenterFadeFactor(Particle p, double age)
    {
        var dx = p.X - p.BurstX;
        var dy = p.Y - p.BurstY;
        var radial = Math.Sqrt((dx * dx) + (dy * dy) + (p.Z * p.Z));
        var radialNorm = Math.Clamp(radial / ScalePixels(220, p.EffectScale), 0, 1);

        // Center-out fade: center starts fading first, outer stars fade later.
        var startAge = 0.12 + (radialNorm * 0.20);
        var endAge = startAge + 0.18;
        var fadeT = Math.Clamp((age - startAge) / Math.Max(endAge - startAge, 0.001), 0, 1);
        var smooth = fadeT * fadeT * (3 - (2 * fadeT));
        var fade = 1 - smooth;
        var floor = radialNorm * 0.22;
        return Math.Clamp(Math.Max(fade, floor), 0, 1);
    }

    private static double GetKamuroSoftFade(double age)
    {
        if (age <= 0.62)
        {
            return 1;
        }

        var t = Math.Clamp((age - 0.62) / 0.38, 0, 1);
        var smooth = t * t * (3 - (2 * t));
        return 1 - (smooth * 0.78);
    }

    private void PrepareEffectStorage()
    {
        var starmineLaneCount = Math.Max(1, _viewModel.GetEnabledStarmineLaneCount());
        var starmineQueuedShots = 20 * starmineLaneCount;
        var capacityShots = Math.Max(StarmineMaxShots, starmineQueuedShots);
        var burstParticles = Math.Max(_viewModel.Settings.ParticleCount * 2, MinimumBurstPetalCount) * capacityShots;
        var burstTrailCount = burstParticles * EstimatedBurstTrailFrames;
        var totalParticleCapacity = burstParticles + (KobanaCapacityParticleCount * capacityShots);
        var totalTrailCapacity = (LaunchBlastParticleCount * capacityShots) + (EstimatedRocketTrailCapacity * capacityShots) + burstTrailCount;

        _particles.EnsureCapacity(totalParticleCapacity);
        _trails.EnsureCapacity(totalTrailCapacity);
        _renderParticles.EnsureCapacity(totalParticleCapacity);
        _renderTrails.EnsureCapacity(totalTrailCapacity);
        _renderRockets.EnsureCapacity(MaxConcurrentRockets);
    }


    private void ClearEffectStorage()
    {
        _gpuParticlePhysics.Reset();
        _particles.Clear();
        _trails.Clear();
        _renderParticles.Clear();
        _renderTrails.Clear();
        _renderRockets.Clear();
        _scene.ClearScene();
    }





    private sealed record BurstPalette(string Name, WpfColor Outer, WpfColor Shell);

    private enum BurstKind
    {
        Chrysanthemum,
        Botan,
        KamuroGiku
    }

    private enum CurveGuideType
    {
        None,
        LeafSplay,
        SilverDragon,
        Kobana
    }

    private enum SilverDragonTone
    {
        Charcoal,
        Silver
    }

    private sealed class Rocket
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double OriginX { get; set; }
        public double OriginY { get; set; }
        public double TargetX { get; set; }
        public double TargetY { get; set; }
        public double ApexX { get; set; }
        public double ApexY { get; set; }
        public double Vx { get; set; }
        public double Vy { get; set; }
        public double HorizontalAccel { get; set; }
        public double SwayPhase { get; set; }
        public double WindPhase { get; set; }
        public double SwayTime { get; set; }
        public double BurstDelay { get; set; }
        public bool FuseHidden { get; set; }
        public bool FuseStarted { get; set; }
        public WpfColor TrailColor { get; set; }
        public CurveGuideType CurveGuide { get; set; }
        public double LastTrailEmitProgress { get; set; }
        public int KobanaBurstCount { get; set; }
        public double PrevX { get; set; }
        public double PrevY { get; set; }
        public BurstKind BurstKind { get; set; }
        public BurstPalette BurstPalette { get; set; } = null!;
        public BurstPalette ChrysanthemumCharcoalPalette { get; set; } = null!;
        public SilverDragonTone SilverDragonTone { get; set; }
        public bool IsStarmine { get; set; }
        public double BurstScale { get; set; }
        public double EffectScale { get; set; } = 1;
    }

    private struct Particle
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double PrevX { get; set; }
        public double PrevY { get; set; }
        public double BurstX { get; set; }
        public double BurstY { get; set; }
        public BurstKind Kind { get; set; }
        public double Z { get; set; }
        public double PrevZ { get; set; }
        public double Vx { get; set; }
        public double Vy { get; set; }
        public double Vz { get; set; }
        public double Life { get; set; }
        public double InitialLife { get; set; }
        public double Decay { get; set; }
        public double Size { get; set; }
        public WpfColor StartColor { get; set; }
        public WpfColor EndColor { get; set; }
        public WpfColor TransitionColor { get; set; }
        public WpfColor CurrentColor { get; set; }
        public WpfColor CurrentCoreColor { get; set; }
        public double Age { get; set; }
        public double RenderGlowSize { get; set; }
        public double RenderCoreSize { get; set; }
        public double RenderGlowOpacityBase { get; set; }
        public double RenderCoreOpacityBase { get; set; }
        public double TrailStrength { get; set; }
        public double Drag { get; set; }
        public double FlickerPhase { get; set; }
        public bool Twinkle { get; set; }
        public double EffectScale { get; set; }
    }

    private struct TrailParticle(double x, double y, double z, double burstX, double burstY, double life, double size, WpfColor color, double effectScale)
    {
        public double X { get; set; } = x;
        public double Y { get; set; } = y;
        public double Z { get; set; } = z;
        public double BurstX { get; set; } = burstX;
        public double BurstY { get; set; } = burstY;
        public double Life { get; set; } = life;
        public double Size { get; set; } = size;
        public WpfColor Color { get; set; } = color;
        public double EffectScale { get; set; } = effectScale;
    }

    private static double ScalePixels(double value, double effectScale) => value * effectScale;

    private double GetStageScale(WpfPoint screenPoint)
    {
        var screen = FormsScreen.FromPoint(new((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)));
        var bounds = screen.Bounds;
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            return 1.0;
        }

        return Math.Min(bounds.Width / ReferenceWidth, bounds.Height / ReferenceHeight);
    }

    private double GetDipPerDevicePixel(WpfPoint screenPoint)
    {
        var monitor = MonitorFromPoint(new NativePoint((int)Math.Round(screenPoint.X), (int)Math.Round(screenPoint.Y)), MonitorDefaultToNearest);
        if (monitor != nint.Zero && GetDpiForMonitor(monitor, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0)
        {
            return 96.0 / dpiX;
        }

        var source = PresentationSource.FromVisual(this);
        var dpiScale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        return dpiScale <= 0 ? 1.0 : 1.0 / dpiScale;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativePoint(int x, int y)
    {
        public readonly int X = x;
        public readonly int Y = y;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint hmonitor, int dpiType, out uint dpiX, out uint dpiY);

}
