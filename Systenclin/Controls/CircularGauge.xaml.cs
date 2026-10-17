using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Systenclin.Controls;

/// <summary>
/// Medidor circular simples: trilho + arco de 0–100% + rótulo central.
/// Desenhado com Path/StreamGeometry para não exigir dependências externas.
/// </summary>
public partial class CircularGauge : UserControl
{
    private const double Radius = 44.0;
    private static readonly Point Center = new(50.0, 50.0);

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(CircularGauge),
            new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(CircularGauge),
            new PropertyMetadata(string.Empty, OnCaptionChanged));

    public CircularGauge()
    {
        InitializeComponent();
        UpdateArc();
        UpdateCaption();
    }

    /// <summary>Percentual exibido (0–100).</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Rótulo opcional abaixo do percentual (oculto quando vazio).</summary>
    public string Caption
    {
        get => (string)GetValue(CaptionProperty);
        set => SetValue(CaptionProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CircularGauge)d).UpdateArc();

    private static void OnCaptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((CircularGauge)d).UpdateCaption();

    private void UpdateArc()
    {
        var value = double.IsNaN(Value) ? 0.0 : Math.Clamp(Value, 0.0, 100.0);
        ValueLabel.Text = $"{value:0}%";

        if (value <= 0.05)
        {
            ValueArc.Data = null;
            return;
        }

        // Um arco de 360° é degenerado (início = fim); usa 359.9° no cheio.
        var sweep = Math.Min(value / 100.0 * 360.0, 359.9);
        var radians = (sweep - 90.0) * Math.PI / 180.0;
        var end = new Point(
            Center.X + Radius * Math.Cos(radians),
            Center.Y + Radius * Math.Sin(radians));

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(Center.X, Center.Y - Radius), false, false);
            context.ArcTo(end, new Size(Radius, Radius), 0.0,
                sweep > 180.0, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        ValueArc.Data = geometry;
    }

    private void UpdateCaption()
    {
        CaptionLabel.Text = Caption ?? string.Empty;
        CaptionLabel.Visibility = string.IsNullOrWhiteSpace(Caption)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }
}
