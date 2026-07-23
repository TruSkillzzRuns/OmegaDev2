using System;
using System.Diagnostics;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace OmegaDev2.Controls;

// Matrix-style digital rain, rendered as a passive decorative background (no
// interaction, no on-screen controls) — requested to visually match
// https://matrix.logic-wire.de/ minus its UI. Meant to sit behind the app's
// NavigationView content; visibility of the effect through page content
// depends on OmegaDev2.PageBackgroundBrush being partially transparent (see
// App.xaml) since panels themselves stay fully opaque for readability.
public sealed partial class MatrixRainBackground : UserControl
{
    private const float GlyphSize = 15f;
    private const float ColumnWidth = GlyphSize * 0.9f;
    private const string Glyphs =
        "アイウエオカキクケコサシスセソタチツテトナニヌネノハヒフヘホマミムメモヤユヨラリルレロワヲン0123456789";

    private static readonly Random Rng = new();
    private static readonly CanvasTextFormat TextFormat = new() { FontSize = GlyphSize, FontFamily = "Consolas" };

    private sealed class Column
    {
        public float Y;
        public float Speed;
        public char[] Chars = Array.Empty<char>();
    }

    private Column[] _columns = Array.Empty<Column>();
    private readonly Stopwatch _clock = new();
    private double _lastSeconds;

    public MatrixRainBackground()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            SetupColumns();
            _clock.Restart();
            _lastSeconds = 0;
            CompositionTarget.Rendering += OnRendering;
        };
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
        SizeChanged += (_, _) => SetupColumns();
    }

    private void SetupColumns()
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        int count = Math.Max(1, (int)(width / ColumnWidth));
        var columns = new Column[count];
        for (int i = 0; i < count; i++)
            columns[i] = NewColumn(height, seedAnywhere: true);
        _columns = columns;
    }

    private static Column NewColumn(double height, bool seedAnywhere)
    {
        int trailLength = Rng.Next(10, 28);
        var chars = new char[trailLength];
        for (int i = 0; i < trailLength; i++)
            chars[i] = Glyphs[Rng.Next(Glyphs.Length)];

        return new Column
        {
            // A freshly (re)armed column starts above the visible area so its
            // head has to travel down into view; an initial seed is instead
            // scattered across the whole height (including negative, i.e.
            // already-in-flight) so the effect doesn't visibly "start" empty.
            Y = seedAnywhere ? (float)(Rng.NextDouble() * height * 2 - height) : (float)(-Rng.NextDouble() * 200),
            Speed = (float)(70 + Rng.NextDouble() * 130),
            Chars = chars,
        };
    }

    private void OnRendering(object? sender, object e)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = _lastSeconds == 0 ? 0 : now - _lastSeconds;
        _lastSeconds = now;
        // Clamp absurd frame gaps (e.g. window was minimized) so a column
        // doesn't teleport across the whole screen on the next visible frame.
        if (dt <= 0 || dt > 0.25)
        {
            RainCanvas.Invalidate();
            return;
        }

        double height = ActualHeight;
        foreach (var col in _columns)
        {
            col.Y += (float)(col.Speed * dt);
            if (col.Y - col.Chars.Length * GlyphSize > height)
            {
                var fresh = NewColumn(height, seedAnywhere: false);
                col.Y = fresh.Y;
                col.Speed = fresh.Speed;
                col.Chars = fresh.Chars;
            }
            else if (Rng.NextDouble() < 0.03)
            {
                col.Chars[Rng.Next(col.Chars.Length)] = Glyphs[Rng.Next(Glyphs.Length)];
            }
        }

        RainCanvas.Invalidate();
    }

    private void RainCanvas_CreateResources(CanvasControl sender, CanvasCreateResourcesEventArgs args) { }

    private void RainCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        float height = (float)sender.ActualHeight;

        for (int i = 0; i < _columns.Length; i++)
        {
            var col = _columns[i];
            float x = i * ColumnWidth;

            for (int t = 0; t < col.Chars.Length; t++)
            {
                float y = col.Y - t * GlyphSize;
                if (y < -GlyphSize || y > height) continue;

                // Drawn BEHIND the NavigationView/pages (see MainWindow.xaml
                // z-order + App.xaml's semi-transparent PageBackgroundBrush),
                // so it only shows through page background gaps/margins —
                // panels stay fully opaque. Can afford fuller alpha than an
                // overlay would since it's not competing with foreground text.
                float fade = 1f - (float)t / col.Chars.Length;
                Color color = t == 0
                    ? Color.FromArgb(255, 200, 255, 220)              // bright near-white head
                    : Color.FromArgb((byte)(fade * fade * 200), 20, 200, 90); // fading green trail

                ds.DrawText(col.Chars[t].ToString(), x, y, color, TextFormat);
            }
        }
    }
}
