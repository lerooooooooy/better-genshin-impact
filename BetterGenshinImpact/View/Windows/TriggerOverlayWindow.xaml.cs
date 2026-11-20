using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DrawingPoint = System.Drawing.Point;
using DrawingRectangle = System.Drawing.Rectangle;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Trigger;
using BetterGenshinImpact.Helpers;
using Vanara.PInvoke;


namespace BetterGenshinImpact.View.Windows;

/// <summary>
/// 透明 + 鼠标穿透 + TopMost + Canvas。跟游戏窗口走、画命中框/标签/点击红点。
/// </summary>
public partial class TriggerOverlayWindow : System.Windows.Window
{
    private nint _targetHwnd;

    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private readonly List<(DrawingPoint Position, long CreatedMs)> _clickDots = new();
    private const long ClickDotLifetimeMs = 800;

    public TriggerOverlayWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyOwnerAndStyles();
    }

    public void AttachTo(nint targetHwnd)
    {
        _targetHwnd = targetHwnd;
        if (IsLoaded)
        {
            ApplyOwnerAndStyles();
        }
    }

    private void ApplyOwnerAndStyles()
    {
        var helper = new WindowInteropHelper(this);
        // Owner 窗口 Z 序变更会自动 bring overlay。
        helper.Owner = _targetHwnd;

        var hWnd = helper.Handle;
        int exStyle = User32.GetWindowLong(hWnd, User32.WindowLongFlags.GWL_EXSTYLE);
        var newStyle = exStyle | WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW;
        User32.SetWindowLong(hWnd, User32.WindowLongFlags.GWL_EXSTYLE, newStyle);

        RefreshPosition();
    }

    /// <summary>
    /// 100ms 定时执行(必须在 UI 线程)。
    /// </summary>
    public void RefreshPosition()
    {
        var rect = SystemControl.GetCaptureRect(_targetHwnd); // 屏幕物理像素
        var dpi = DpiHelper.ScaleY; // 主屏 DPI，默认 1.0
        if (dpi <= 0)
        {
            dpi = 1f;
        }

        Left = rect.Left / dpi;
        Top = rect.Top / dpi;
        Width = rect.Width / dpi;
        Height = rect.Height / dpi;

        RedrawClickDots();
    }

    /// <summary>
    /// 线程安全的画框入口:后台线程(trigger 的 OnCapture)调用,内部切到 UI 线程执行。
    /// 只快照 RECT? 和 float 两个值,不传 Mat。overlay 为 null 时直接跳过。
    /// </summary>
    public static void DrawMatchRect(TriggerOverlayWindow? overlay, DrawingRectangle rect, TriggerCaptureContent content, string label)
    {
        if (overlay == null)
        {
            return;
        }

        var capturedCaptureRect = content.CaptureRect;
        var capturedDpiScale = content.DpiScale;
        Application.Current?.Dispatcher.BeginInvoke(() =>
            overlay.SetMatchRect(rect, capturedCaptureRect, capturedDpiScale, label));
    }

    /// <summary>
    /// 线程安全的清框入口:后台线程(trigger 的 OnCapture)调用,内部切到 UI 线程执行。
    /// 目标消失后必须显式调用,否则上一次命中画下的框会一直留在屏幕上。
    /// </summary>
    public static void ClearMatchRect(TriggerOverlayWindow? overlay)
    {
        if (overlay == null)
        {
            return;
        }

        Application.Current?.Dispatcher.BeginInvoke(() => overlay.ClearMatchRectCore());
    }

    /// <summary>
    /// 命中回调时(UI 线程)。本方法不放任何 Mat/IO。
    /// </summary>
    public void SetMatchRect(DrawingRectangle rect, RECT? captureRect, float dpiScale, string label)
    {
        // 先清掉上一帧残留的绿框/标签。
        ClearMatchRectCore();

        if (dpiScale <= 0)
        {
            dpiScale = 1f;
        }

        // capture 像素坐标 + captureRect.Left/Top → 屏幕物理坐标。
        var screenX = rect.X;
        var screenY = rect.Y;
        if (captureRect.HasValue)
        {
            screenX += captureRect.Value.Left;
            screenY += captureRect.Value.Top;
        }

        // 屏幕物理 / dpiScale - overlay.Left/Top → overlay 内 DIP 偏移。
        var xDip = screenX / dpiScale - Left;
        var yDip = screenY / dpiScale - Top;
        var wDip = rect.Width / dpiScale;
        var hDip = rect.Height / dpiScale;

        var border = new Rectangle
        {
            Width = wDip,
            Height = hDip,
            Stroke = Brushes.Lime,
            StrokeThickness = 3,
            Fill = Brushes.Transparent,
        };
        Canvas.SetLeft(border, xDip);
        Canvas.SetTop(border, yDip);
        MatchCanvas.Children.Add(border);

        var labelText = new TextBlock
        {
            Text = label,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            Padding = new Thickness(4, 1, 4, 1),
            FontSize = 12,
        };
        // 默认在框上方 20 像素，贴近顶部时切到框内顶部 +2。
        var labelY = yDip - 20;
        if (labelY < 0)
        {
            labelY = yDip + 2;
        }

        Canvas.SetLeft(labelText, xDip);
        Canvas.SetTop(labelText, labelY);
        MatchCanvas.Children.Add(labelText);
    }

    /// <summary>
    /// dispatcher 每帧 Tick 末尾。只清 Rectangle 和 TextBlock(Ellipse 红点不删)。
    /// </summary>
    public void ClearMatchRectCore()
    {
        foreach (var child in MatchCanvas.Children.OfType<Rectangle>().ToList())
        {
            MatchCanvas.Children.Remove(child);
        }

        foreach (var child in MatchCanvas.Children.OfType<TextBlock>().ToList())
        {
            MatchCanvas.Children.Remove(child);
        }
    }

    /// <summary>
    /// 命中并点击后调用：标一个 7 DIP 半径的红点，800ms 后 RefreshPosition 清理。
    /// </summary>
    public void AddClickDot(DrawingPoint point, RECT? captureRect, float dpiScale)
    {
        var screenX = point.X + (captureRect?.Left ?? 0);
        var screenY = point.Y + (captureRect?.Top ?? 0);
        _clickDots.Add((new DrawingPoint(screenX, screenY), Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency));
        RedrawClickDots();
    }

    private void RedrawClickDots()
    {
        foreach (var ellipse in MatchCanvas.Children.OfType<Ellipse>().ToList())
        {
            MatchCanvas.Children.Remove(ellipse);
        }

        var nowMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
        _clickDots.RemoveAll(dot => nowMs - dot.CreatedMs > ClickDotLifetimeMs);
        var dpiScale = DpiHelper.ScaleY;
        if (dpiScale <= 0)
        {
            dpiScale = 1f;
        }

        const double radius = 7;
        foreach (var dot in _clickDots)
        {
            var ellipse = new Ellipse
            {
                Width = radius * 2,
                Height = radius * 2,
                Fill = new SolidColorBrush(Color.FromArgb(200, 255, 0, 0)),
            };
            Canvas.SetLeft(ellipse, dot.Position.X / dpiScale - Left - radius);
            Canvas.SetTop(ellipse, dot.Position.Y / dpiScale - Top - radius);
            MatchCanvas.Children.Add(ellipse);
        }
    }

}
