using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Nexus.Controls;

/// <summary>
/// A stroked line icon drawn from vector paths on a 24×24 grid (Lucide-style). Used instead of
/// emoji so icons look identical on every OS — emoji fonts differ per platform and are missing
/// entirely on many Linux installs. Inherits <see cref="Foreground"/> like text does.
/// </summary>
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 16);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeThickness), 2);

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty, StrokeThicknessProperty);
        AffectsMeasure<Icon>(SizeProperty);
    }

    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        if (Kind is null || !Paths.TryGetValue(Kind, out var data) || Foreground is null) return;
        var geometry = Cache.TryGetValue(Kind, out var g) ? g : Cache[Kind] = Geometry.Parse(data);

        var scale = Size / 24.0;
        var pen = new Pen(Foreground, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        using (context.PushTransform(Matrix.CreateScale(scale, scale)))
            context.DrawGeometry(null, pen, geometry);
    }

    private static readonly Dictionary<string, Geometry> Cache = new();

    private static readonly Dictionary<string, string> Paths = new()
    {
        ["database"] = "M4 6a8 3 0 1 0 16 0a8 3 0 1 0 -16 0 M4 6v12a8 3 0 0 0 16 0V6 M4 12a8 3 0 0 0 16 0",
        ["folder"] = "M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z",
        ["table"] = "M5 4h14a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Z M3 10h18 M3 15h18 M10 10v10",
        ["view"] = "M2 12C5 6.5 19 6.5 22 12C19 17.5 5 17.5 2 12Z M9.5 12a2.5 2.5 0 1 0 5 0a2.5 2.5 0 1 0-5 0",
        ["collection"] = "M12 3L21 8L12 13L3 8Z M3 12.5l9 5l9-5 M3 17l9 5l9-5",
        ["column"] = "M7 8h10 M7 12h10 M7 16h6",
        ["key"] = "M3 15a4.5 4.5 0 1 0 9 0a4.5 4.5 0 1 0-9 0 M10.8 11.8L20 2.6 M16.5 6.1l3 3 M14 8.6l2 2",
        ["warning"] = "M10.3 3.9L2.4 17.5A2 2 0 0 0 4.1 20.5h15.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0Z M12 9v4 M12 17h.01",
        ["error"] = "M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0 M12 8v4.5 M12 16h.01",
        ["plus"] = "M12 5v14 M5 12h14",
        ["edit"] = "M4 20h4L19 9l-4-4L4 16Z M13.5 6.5l4 4",
        ["refresh"] = "M21 12a9 9 0 1 1-9-9c2.5 0 4.9 1 6.7 2.7L21 8 M21 3v5h-5",
        ["trash"] = "M4 7h16 M10 11v6 M14 11v6 M6 7l1 13h10l1-13 M9 7V4h6v3",
        ["swap"] = "M8 3L4 7l4 4 M4 7h16 M16 21l4-4l-4-4 M20 17H4",
        ["search"] = "M4 11a7 7 0 1 0 14 0a7 7 0 1 0-14 0 M20 20l-4.2-4.2",
        ["code"] = "M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9Z M14 3v6h6 M10 13l-2 2l2 2 M14 13l2 2l-2 2",
        ["import"] = "M12 3v12 M7 10l5 5l5-5 M5 21h14",
        ["export"] = "M12 15V3 M7 8l5-5l5 5 M5 21h14",
        ["server"] = "M4 4h16v6H4Z M4 14h16v6H4Z M8 7h.01 M8 17h.01",
        ["play"] = "M7 4.5v15L19 12Z",
        ["stop"] = "M6 6h12v12H6Z",
        ["zap"] = "M13 2L4 14h7l-1 8l9-12h-7Z",
        ["open"] = "M3 7a2 2 0 0 1 2-2h4l2 2h7a2 2 0 0 1 2 2v1 M3 7v10a2 2 0 0 0 2 2h12.5l3.5-8H7.5L4 19",
        ["save"] = "M5 3h11l5 5v11a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2Z M7 3v5h8 M7 21v-7h10v7",
        ["star"] = "M12 2.8l2.8 5.8l6.4.9l-4.6 4.5l1.1 6.4L12 17.4l-5.7 3l1.1-6.4L2.8 9.5l6.4-.9Z",
        ["close"] = "M6 6l12 12 M18 6L6 18",
        ["clock"] = "M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0 M12 7v5l3 2",
        ["history"] = "M3 12a9 9 0 1 0 2.6-6.4L3 8 M3 3v5h5 M12 7v5l3 2",
        ["undo"] = "M9 14L4 9l5-5 M4 9h10.5a5.5 5.5 0 0 1 0 11H11",
        ["ban"] = "M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0 M5.7 5.7l12.6 12.6",
        ["query"] = "M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9Z M14 3v6h6 M12 12v6 M9 15h6",
        ["lock"] = "M5 11h14v10H5Z M8 11V7a4 4 0 0 1 8 0v4",
        ["more"] = "M5 12h.01 M12 12h.01 M19 12h.01",
        ["file"] = "M14 3H6a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V9Z M14 3v6h6",
        ["download"] = "M12 4v11 M7 10l5 5l5-5 M5 20h14",
        ["link"] = "M10 14a4 4 0 0 0 5.7 0l3-3a4 4 0 0 0-5.7-5.7l-1 1 M14 10a4 4 0 0 0-5.7 0l-3 3a4 4 0 0 0 5.7 5.7l1-1",
        ["check"] = "M5 12l5 5L20 7",
        ["info"] = "M3 12a9 9 0 1 0 18 0a9 9 0 1 0-18 0 M12 11v5 M12 8h.01",
    };
}
