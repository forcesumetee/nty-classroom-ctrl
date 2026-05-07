using ClassroomCtrl.Shared.Protocol;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;

namespace ClassroomCtrl.Teacher;

/// <summary>
/// Phase 9.2: Fullscreen transparent overlay for teacher to annotate over a screen
/// share. Captures stylus/mouse strokes via InkCanvas and broadcasts each one to
/// students who render them on top of their ScreenViewWindow.
/// </summary>
public partial class ScreenPenWindow : Window
{
    private System.Windows.Media.Color _color = Colors.Red;
    private double _thickness = 4.0;
    private bool _isEraser;

    public ScreenPenWindow()
    {
        InitializeComponent();
        ApplyBrush();
    }

    private void ApplyBrush()
    {
        InkSurface.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _color,
            Width = _thickness,
            Height = _thickness,
            FitToCurve = true,
        };
        InkSurface.EditingMode = _isEraser ? InkCanvasEditingMode.EraseByStroke : InkCanvasEditingMode.Ink;
    }

    private void Color_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string hex)
        {
            try
            {
                var c = (System.Windows.Media.Color)ColorConverter.ConvertFromString(hex);
                _color = c;
                _isEraser = false;
                ApplyBrush();
            }
            catch { }
        }
    }

    private void Thickness_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string s && double.TryParse(s, out var t))
        {
            _thickness = t;
            _isEraser = false;
            ApplyBrush();
        }
    }

    private void Eraser_Click(object sender, RoutedEventArgs e)
    {
        _isEraser = !_isEraser;
        ApplyBrush();
    }

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        if (InkSurface.Strokes.Count > 0)
        {
            InkSurface.Strokes.RemoveAt(InkSurface.Strokes.Count - 1);
        }
        if (App.Server != null)
            try { await App.Server.BroadcastDrawingUndoAsync(CancellationToken.None); } catch { }
    }

    private async void Clear_Click(object sender, RoutedEventArgs e)
    {
        InkSurface.Strokes.Clear();
        if (App.Server != null)
            try { await App.Server.BroadcastDrawingClearAsync(CancellationToken.None); } catch { }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
        else if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) != 0) Clear_Click(this, e);
    }

    private async void InkSurface_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (App.Server == null) return;

        var w = InkSurface.ActualWidth;
        var h = InkSurface.ActualHeight;
        if (w <= 0 || h <= 0) return;

        var pts = new List<DrawingPointDto>(e.Stroke.StylusPoints.Count);
        foreach (var p in e.Stroke.StylusPoints)
        {
            pts.Add(new DrawingPointDto { X = p.X / w, Y = p.Y / h });
        }

        var c = e.Stroke.DrawingAttributes.Color;
        var hex = $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        var msg = new DrawingStrokeMessage
        {
            Points = pts,
            ColorHex = hex,
            Thickness = e.Stroke.DrawingAttributes.Width,
            Tool = "pen",
        };

        try { await App.Server.BroadcastDrawingStrokeAsync(msg, CancellationToken.None); } catch { }
    }
}
