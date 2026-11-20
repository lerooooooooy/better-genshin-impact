using BetterGenshinImpact.GameTask.Trigger;
using System;
using System.Diagnostics;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 暂停球活动 trigger:仅在 zzz 处于前台且模板匹配到暂停球图标时,
/// 每帧按 Fisher-Yates 随机顺序用前台 SendInput 发送 a/d/q/空格 共 4 个按键(每个 KeyDown+Sleep(50)+KeyUp)。
///
/// 设计:
/// - 前台守卫(<see cref="SystemControl.IsZzzActive"/>):不在前台直接 return,避免误触发。
/// - 无冷却:用户明确要求"每一帧都发四个按键"。每帧命中即发 4 次,不停。
/// - 用 <see cref="InputHub.Foreground.Keyboard"/> 而非 <see cref="ZzzTriggerActions.PressKeyForeground"/>:
///   后者每次都先 Thread.Sleep(1500) 等切前台,与"每帧发按键"的语义不兼容。
/// - 用 <see cref="Random.Shared"/> 而非新 Random:与 <see cref="ZzzTriggerActions"/> 共用同一随机源避免重复初始化。
/// </summary>
public sealed class BallPauseActivityTrigger : IZzzTaskTrigger, IDisposable
{
    public string Name => "BallPauseActivityTrigger";
    public bool IsEnabled { get; set; } = true;

    private const double MatchThreshold = 0.96;

    private const string BallPausePath = "Assets\\Template\\Zzz\\ballPause_72_30_91x49.png";

    private readonly TemplateWaitTarget _ballPauseTarget = new("BallPause", BallPausePath, MatchThreshold);

    /// <summary>每帧要发的 4 个前台按键。每次命中时 Fisher-Yates 打乱后按顺序发送。</summary>
    private static readonly User32.VK[] s_keys =
    {
        User32.VK.VK_A,
        User32.VK.VK_D,
        User32.VK.VK_Q,
        User32.VK.VK_SPACE,
    };

    /// <summary>Fisher-Yates 用的可写缓冲,每次 Copy + 原地打乱。</summary>
    private readonly User32.VK[] _shuffleBuf = new User32.VK[s_keys.Length];

    public void OnCapture(TriggerCaptureContent content)
    {
        if (!SystemControl.IsZzzActive(content.Hwnd))
        {
            return;
        }

        TemplateMatchResult? result;
        try
        {
            if (!_ballPauseTarget.TryMatch(content, out result))
            {
                return;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-BallPause] TryMatch threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        ShuffleInto(s_keys, _shuffleBuf);
        var keyboard = InputHub.Foreground.Keyboard;
        foreach (var vk in _shuffleBuf)
        {
            keyboard.KeyDown(vk).Sleep(50).KeyUp(vk);
        }

        Debug.WriteLine($"[ZZZ-BallPause] fired 4 keys frame={content.FrameIndex} score={(result != null ? result.Score.ToString("F3") : "n/a")}");
    }

    /// <summary>
    /// Fisher-Yates 把 <paramref name="src"/> 打乱到 <paramref name="dst"/>。
    /// dst 与 src 等长;src 不被修改。
    /// </summary>
    private static void ShuffleInto(User32.VK[] src, User32.VK[] dst)
    {
        Array.Copy(src, dst, src.Length);
        for (int i = dst.Length - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (dst[i], dst[j]) = (dst[j], dst[i]);
        }
    }

    public void Dispose()
    {
        _ballPauseTarget.Dispose();
    }
}
