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

        if (scene.Range is not null && scene.Settings is not null) DrawGuides(canvas, scene, paint);
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
            if (stroke.Points.Count < 2 || scene.Range is not null && stroke.EquationId < 0)
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
            if (scene.Range is not null && (label.Text is "x" or "y" || double.TryParse(label.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out _))) continue;
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

    private static void DrawGuides(SKCanvas canvas, GraphScene scene, SKPaint paint)
    {
        var range = scene.Range!;
        var settings = scene.Settings!;
        double xSpan = range.Right - range.Left, ySpan = range.Top - range.Bottom;
        if (!double.IsFinite(xSpan) || !double.IsFinite(ySpan) || xSpan <= 0 || ySpan <= 0) return;
        float width = scene.Width - 1, height = scene.Height - 1;
        float X(double value) => (float)((value - range.Left) / xSpan * width);
        float Y(double value) => (float)((range.Top - value) / ySpan * height);
        double Step(double span)
        {
            double power = Math.Pow(10, Math.Floor(Math.Log10(span / 10)));
            double scaled = span / 10 / power;
            return (scaled <= 1 ? 1 : scaled <= 2 ? 2 : scaled <= 5 ? 5 : 10) * power;
        }
        double xStep = Step(xSpan), yStep = Step(ySpan);
        paint.Style = SKPaintStyle.Stroke; paint.StrokeWidth = 1; paint.IsAntialias = false;
        if (settings.ShowGrid)
        {
            paint.Color = ReadColor(settings.Grid);
            for (var index = 0; index < 64; index++)
            {
                double value = (Math.Ceiling(range.Left / xStep) + index) * xStep;
                if (value > range.Right) break;
                float x = (float)Math.Floor(X(value)) + 0.5f;
                canvas.DrawLine(x, 0, x, height, paint);
            }
            for (var index = 0; index < 64; index++)
            {
                double value = (Math.Ceiling(range.Bottom / yStep) + index) * yStep;
                if (value > range.Top) break;
                float y = (float)Math.Floor(Y(value)) + 0.5f;
                canvas.DrawLine(0, y, width, y, paint);
            }
        }
        if (settings.ShowAxes)
        {
            paint.Color = ReadColor(settings.Axis); paint.IsAntialias = true;
            float axisX = X(0), axisY = Y(0);
            bool showX = axisY >= 0 && axisY <= height, showY = axisX >= 0 && axisX <= width;
            if (showX)
            {
                canvas.DrawLine(0, axisY, width, axisY, paint);
                canvas.DrawLine(width - 6, axisY - 5, width, axisY, paint);
                canvas.DrawLine(width - 6, axisY + 5, width, axisY, paint);
            }
            if (showY)
            {
                canvas.DrawLine(axisX, 0, axisX, height, paint);
                canvas.DrawLine(axisX - 5, 6, axisX, 0, paint);
                canvas.DrawLine(axisX + 5, 6, axisX, 0, paint);
            }
            paint.Style = SKPaintStyle.Fill; paint.Color = ReadColor(settings.Text);
            using var typeface = SKTypeface.FromFamilyName("Segoe UI");
            using var font = new SKFont(typeface, 12) { SkewX = -0.2f };
            if (showX) canvas.DrawText("x", width - 12, Math.Clamp(axisY + 18, 12, height - 2), SKTextAlign.Left, font, paint);
            if (showY) canvas.DrawText("y", Math.Clamp(axisX - 16, 2, width - 16), 12, SKTextAlign.Left, font, paint);
            font.SkewX = 0;
            double xMajor = xStep * 5, yMajor = yStep * 5;
            for (var index = 0; index < 64; index++)
            {
                double value = (Math.Ceiling(range.Left / xMajor) + index) * xMajor;
                if (value > range.Right) break;
                if (showX) canvas.DrawText(value.ToString("G4", CultureInfo.CurrentCulture), Math.Clamp(X(value) - 6, 2, width - 30), Math.Clamp(axisY + 15, 12, height - 2), SKTextAlign.Left, font, paint);
            }
            for (var index = 0; index < 64; index++)
            {
                double value = (Math.Ceiling(range.Bottom / yMajor) + index) * yMajor;
                if (value > range.Top) break;
                if (showY && Math.Abs(value) > yStep / 100) canvas.DrawText(value.ToString("G4", CultureInfo.CurrentCulture), Math.Clamp(axisX + 6, 2, width - 30), Math.Clamp(Y(value) + 4, 12, height - 2), SKTextAlign.Left, font, paint);
            }
        }
        paint.IsAntialias = true; paint.Style = SKPaintStyle.Fill;
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
