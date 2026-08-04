using System;
using OpenCvSharp;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 单帧数据 + 屏幕坐标 / DPI 上下文。
/// CaptureRect 与 DpiScale 由 dispatcher 在调用 OnCapture 前实时计算并塞进来，
/// 因为 frame.CaptureRect 在 DwmSharedSurface 等模式可能缺失，dispatcher 兜底再算一次。
/// </summary>
public class ZzzCaptureContent : IDisposable
{
    public Mat Image { get; }
    public int FrameIndex { get; }
    public double IntervalMs { get; }
    public int FrameRate => IntervalMs > 0 ? (int)(1000 / IntervalMs) : 0;
    public nint Hwnd { get; }

    /// <summary>
    /// 屏幕物理坐标，capture 图像左上角在屏幕上的位置 + 宽高。
    /// 用于把 capture 像素坐标换算成屏幕坐标，再换算到 overlay DIP。
    /// </summary>
    public RECT? CaptureRect { get; }

    /// <summary>
    /// 游戏窗口所在监视器的 DPI 缩放(dpiY/96)。
    /// </summary>
    public float DpiScale { get; }

    public ZzzCaptureContent(Mat image, int frameIndex, double intervalMs,
        nint hwnd, RECT? captureRect = null, float dpiScale = 1f)
    {
        Image = image;
        FrameIndex = frameIndex;
        IntervalMs = intervalMs;
        Hwnd = hwnd;
        CaptureRect = captureRect;
        DpiScale = dpiScale;
    }

    public void Dispose()
    {
        Image.Dispose();
        GC.SuppressFinalize(this);
    }
}
