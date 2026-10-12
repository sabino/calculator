// Licensed under the MIT License.
using System;
using System.Globalization;
using Microsoft.UI.Xaml.Automation;
using SkiaSharp;
using Uno.WinUI.Graphics2DSK;
using Windows.Foundation;

namespace CalculatorApp.Desktop;

public sealed class GraphCanvas : SKCanvasElement
{
    private GraphScene? _scene;
    private GraphCanvasTrace? _trace;

    public GraphCanvas()
    {
        MinWidth = 280;
        MinHeight = 300;
        AutomationProperties.SetName(this, "Graph. Drag to pan and scroll to zoom.");
    }

    public void SetScene(GraphScene scene)
    {
        _scene = scene;
        _trace = null;
        Invalidate();
    }

    public void SetTrace(GraphCanvasTrace? trace)
    {
        _trace = trace;
        Invalidate();
    }

    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        var scene = _scene;
        if (scene is null || scene.Width <= 0 || scene.Height <= 0)
        {
            canvas.Clear(SKColors.White);
            return;
        }

        canvas.Save();
        try
        {
            canvas.ClipRect(new SKRect(0, 0, (float)area.Width, (float)area.Height));
            canvas.Scale((float)(area.Width / scene.Width), (float)(area.Height / scene.Height));
            DrawScene(canvas, scene, _trace);
        }
        finally
        {
            canvas.Restore();
        }
    }

    public static byte[] EncodePng(GraphScene scene)
    {
        if (scene.Width is < 16 or > 4096 || scene.Height is < 16 or > 4096 || (long)scene.Width * scene.Height > 4194304)
            throw new ArgumentOutOfRangeException(nameof(scene), "Graph image dimensions exceed the supported size.");
        using var surface = SKSurface.Create(new SKImageInfo(scene.Width, scene.Height));
        if (surface is null) throw new InvalidOperationException("The graph image could not be created.");
        DrawScene(surface.Canvas, scene, null);
        using var image = surface.Snapshot();
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        if (encoded is null) throw new InvalidOperationException("The graph image could not be encoded.");
        return encoded.ToArray();
    }

    private static void DrawScene(SKCanvas canvas, GraphScene scene, GraphCanvasTrace? tracePoint)
    {
        canvas.Clear(ReadColor(scene.Background));
        using var paint = new SKPaint { IsAntialias = true };

        foreach (var fill in scene.Fills)
        {
            if (fill.Points.Count < 3)
                continue;
            using var path = new SKPath();
            path.MoveTo((float)fill.Points[0].X, (float)fill.Points[0].Y);
            for (var index = 1; index < fill.Points.Count; index++)
                path.LineTo((float)fill.Points[index].X, (float)fill.Points[index].Y);
            path.Close();
            paint.Color = ReadColor(fill.Color);
            paint.Style = SKPaintStyle.Fill;
            canvas.DrawPath(path, paint);
        }

        paint.Style = SKPaintStyle.Stroke;
        paint.StrokeCap = SKStrokeCap.Round;
        paint.StrokeJoin = SKStrokeJoin.Round;
        foreach (var stroke in scene.Strokes)
        {
            if (stroke.Points.Count < 2)
                continue;
            using var path = new SKPath();
            path.MoveTo((float)stroke.Points[0].X, (float)stroke.Points[0].Y);
            for (var index = 1; index < stroke.Points.Count; index++)
                path.LineTo((float)stroke.Points[index].X, (float)stroke.Points[index].Y);
            paint.Color = ReadColor(stroke.Color);
            paint.StrokeWidth = (float)stroke.Width;
            using var effect = CreateDashEffect(stroke.Style, paint.StrokeWidth);
            paint.PathEffect = effect;
            canvas.DrawPath(path, paint);
            paint.PathEffect = null;
        }

        paint.Style = SKPaintStyle.Fill;
        using var font = new SKFont(SKTypeface.Default, 12);
        foreach (var label in scene.Labels)
        {
            paint.Color = ReadColor(label.Color);
            canvas.DrawText(label.Text, (float)label.X, (float)label.Y, SKTextAlign.Left, font, paint);
        }

        if (tracePoint is { } trace)
        {
            paint.Color = ReadColor(trace.Color);
            paint.Style = SKPaintStyle.Stroke;
            paint.StrokeWidth = 1;
            canvas.DrawCircle((float)trace.X, (float)trace.Y, 5, paint);
            canvas.DrawLine((float)trace.X - 9, (float)trace.Y, (float)trace.X + 9, (float)trace.Y, paint);
            canvas.DrawLine((float)trace.X, (float)trace.Y - 9, (float)trace.X, (float)trace.Y + 9, paint);
        }
    }

    internal static SKColor ReadColor(string value)
    {
        if (value.Length is not (7 or 9) || value[0] != '#'
            || !uint.TryParse(value.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var color))
            return SKColors.Black;
        return new SKColor(value.Length == 7 ? color | 0xff000000u : color);
    }

    private static SKPathEffect? CreateDashEffect(int style, float width)
    {
        var unit = Math.Max(1, width);
        float[]? pattern = style switch
        {
            1 => [unit, 3 * unit],
            2 => [6 * unit, 3 * unit],
            3 => [6 * unit, 3 * unit, unit, 3 * unit],
            4 => [6 * unit, 3 * unit, unit, 3 * unit, unit, 3 * unit],
            _ => null
        };
        return pattern is null ? null : SKPathEffect.CreateDash(pattern, 0);
    }
}

public sealed record GraphCanvasTrace(double X, double Y, string Color);
