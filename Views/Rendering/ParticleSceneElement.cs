using System.Windows;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfPen = System.Windows.Media.Pen;
using WpfPoint = System.Windows.Point;

namespace CtrlHanabi;

internal sealed class ParticleSceneElement : FrameworkElement
{
    private const double TubeBodyOffsetX = 7.5;
    private const double TubeBodyOffsetY = 11.5;
    private const double TubeBodyWidth = 15;
    private const double TubeBodyHeight = 23;
    private const double TubeBodyCornerRadius = 3.2;
    private const double TubeRimOffsetX = 8.2;
    private const double TubeRimOffsetY = 12.4;
    private const double TubeRimWidth = 16.4;
    private const double TubeRimHeight = 4.8;
    private const double TubeRimCornerRadius = 2.6;
    private const double TubeInnerOffsetY = 9.9;
    private const double TubeInnerRadiusX = 5.2;
    private const double TubeInnerRadiusY = 1.85;
    private const double FuseGlowRadius = 1.75;
    private const double FuseCoreRadius = 0.625;

    private static readonly WpfBrush RocketCoreBrush = CreateFrozenBrush(WpfColor.FromRgb(255, 215, 0));
    private static readonly WpfBrush RocketGlowBrush = new RadialGradientBrush(Colors.White, Colors.Transparent);
    private static readonly WpfBrush TubeBodyBrush = CreateFrozenBrush(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(WpfColor.FromRgb(78, 64, 52), 0.0),
            new(WpfColor.FromRgb(48, 37, 28), 0.35),
            new(WpfColor.FromRgb(98, 82, 66), 0.5),
            new(WpfColor.FromRgb(42, 31, 24), 0.82),
            new(WpfColor.FromRgb(70, 56, 44), 1.0)
        },
        new WpfPoint(0, 0),
        new WpfPoint(1, 0)));
    private static readonly WpfBrush TubeRimBrush = CreateFrozenBrush(new LinearGradientBrush(
        new GradientStopCollection
        {
            new(WpfColor.FromRgb(126, 112, 96), 0.0),
            new(WpfColor.FromRgb(74, 58, 44), 0.45),
            new(WpfColor.FromRgb(138, 120, 98), 1.0)
        },
        new WpfPoint(0, 0),
        new WpfPoint(1, 0)));
    private static readonly WpfBrush TubeInnerBrush = CreateFrozenBrush(new RadialGradientBrush(
        new GradientStopCollection
        {
            new(WpfColor.FromRgb(24, 20, 18), 0.0),
            new(WpfColor.FromRgb(10, 8, 7), 0.75),
            new(WpfColor.FromRgb(46, 37, 30), 1.0)
        }));
    private static readonly WpfPen TubeEdgePen = CreateFrozenPen(WpfColor.FromArgb(180, 24, 19, 15), 1.1);
    private static readonly WpfBrush RocketDimCoreBrush = CreateFrozenBrush(WpfColor.FromArgb(200, 255, 215, 0));
    private static readonly WpfBrush RocketDimGlowBrush = CreateFrozenBrush(WpfColor.FromArgb(110, 255, 245, 225));

    private List<RenderTrail> _trails = [];
    private List<RenderParticle> _particles = [];
    private List<RenderRocket> _rockets = [];
    private readonly D3DParticleRenderer _particleRenderer = new();
    private bool _renderedWithGpu;

    static ParticleSceneElement()
    {
        if (RocketGlowBrush is Freezable freezable && freezable.CanFreeze)
        {
            freezable.Freeze();
        }
    }

    public ParticleSceneElement()
    {
        Unloaded += (_, _) => _particleRenderer.Dispose();
    }

    public void UpdateScene(List<RenderTrail> trails, List<RenderParticle> particles, List<RenderRocket> rockets)
    {
        _trails = trails;
        _particles = particles;
        _rockets = rockets;
        _renderedWithGpu = _particleRenderer.Render(this, _trails, _particles);
        InvalidateVisual();
    }

    public void ClearScene()
    {
        _trails = [];
        _particles = [];
        _rockets = [];
        _renderedWithGpu = false;
        InvalidateVisual();
    }

    public void ResetRenderer()
    {
        _particleRenderer.Reset();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (_renderedWithGpu)
        {
            drawingContext.DrawImage(_particleRenderer.Image, new Rect(0, 0, ActualWidth, ActualHeight));
        }
        else
        {
            DrawFallbackParticles(drawingContext);
        }

        foreach (var rocket in _rockets)
        {
            var scale = rocket.Scale;
            drawingContext.DrawRoundedRectangle(
                TubeBodyBrush,
                TubeEdgePen,
                new Rect(
                    rocket.OriginX - (TubeBodyOffsetX * scale),
                    rocket.OriginY - (TubeBodyOffsetY * scale),
                    TubeBodyWidth * scale,
                    TubeBodyHeight * scale),
                TubeBodyCornerRadius * scale,
                TubeBodyCornerRadius * scale);
            drawingContext.DrawRoundedRectangle(
                TubeRimBrush,
                null,
                new Rect(
                    rocket.OriginX - (TubeRimOffsetX * scale),
                    rocket.OriginY - (TubeRimOffsetY * scale),
                    TubeRimWidth * scale,
                    TubeRimHeight * scale),
                TubeRimCornerRadius * scale,
                TubeRimCornerRadius * scale);
            drawingContext.DrawEllipse(
                TubeInnerBrush,
                null,
                new WpfPoint(rocket.OriginX, rocket.OriginY - (TubeInnerOffsetY * scale)),
                TubeInnerRadiusX * scale,
                TubeInnerRadiusY * scale);
            if (!rocket.FuseHidden)
            {
                DrawFilledCircle(drawingContext, rocket.X, rocket.Y, FuseGlowRadius * scale, RocketDimGlowBrush);
                DrawFilledCircle(drawingContext, rocket.X, rocket.Y, FuseCoreRadius * scale, RocketDimCoreBrush);
            }
        }

    }

    private void DrawFallbackParticles(DrawingContext drawingContext)
    {
        foreach (var trail in _trails)
        {
            var brush = CreateFallbackBrush(trail.Color, trail.Opacity);
            DrawFilledCircle(drawingContext, trail.X, trail.Y, trail.Size / 2, brush);
        }

        foreach (var particle in _particles)
        {
            if (particle.GlowOpacity > 0)
            {
                var glowBrush = CreateFallbackBrush(particle.GlowColor, particle.GlowOpacity);
                DrawFilledCircle(drawingContext, particle.X, particle.Y, particle.GlowSize / 2, glowBrush);
            }

            if (particle.CoreOpacity > 0)
            {
                var coreBrush = CreateFallbackBrush(particle.CoreColor, particle.CoreOpacity);
                DrawFilledCircle(drawingContext, particle.X, particle.Y, particle.CoreSize / 2, coreBrush);
            }
        }
    }

    private static WpfBrush CreateFallbackBrush(WpfColor color, double opacity)
    {
        var alpha = (byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1));
        var brush = new SolidColorBrush(WpfColor.FromArgb(alpha, color.R, color.G, color.B));
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        return brush;
    }

    private static WpfBrush CreateFrozenBrush(WpfColor color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static WpfBrush CreateFrozenBrush(WpfBrush brush)
    {
        if (brush is Freezable freezable && freezable.CanFreeze)
        {
            freezable.Freeze();
        }

        return brush;
    }

    private static WpfPen CreateFrozenPen(WpfColor color, double thickness)
    {
        var pen = new WpfPen(new SolidColorBrush(color), thickness);
        if (pen.Brush.CanFreeze)
        {
            pen.Brush.Freeze();
        }

        if (pen.CanFreeze)
        {
            pen.Freeze();
        }

        return pen;
    }

    private static void DrawFilledCircle(DrawingContext drawingContext, double x, double y, double radius, WpfBrush brush)
        => drawingContext.DrawEllipse(brush, null, new WpfPoint(x, y), radius, radius);

}

internal readonly record struct RenderTrail(double X, double Y, double Size, WpfColor Color, double Opacity);

internal readonly record struct RenderParticle(
    double X,
    double Y,
    double GlowSize,
    double CoreSize,
    WpfColor GlowColor,
    WpfColor CoreColor,
    double GlowOpacity,
    double CoreOpacity);

internal readonly record struct RenderRocket(double X, double Y, double OriginX, double OriginY, WpfColor TrailColor, bool FuseHidden, double Scale);
