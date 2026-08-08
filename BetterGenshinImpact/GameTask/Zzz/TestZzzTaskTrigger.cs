using System;
using System.Diagnostics;
using System.Threading;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.View.Windows;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 测试 trigger：跑一个 N 段的序列。每一段 = "同步阻塞等模板 + 命中后执行该段专属动作"。
///
/// 帧内流程:OnCapture → 跑当前段(最多阻塞 3s 等模板) → 命中则执行段动作 + 推进到下一段(轮转回 0);
/// 超时则停留在当前段,下一帧重试。每段各自一个 RunSeq* 方法,各自指定一个命中动作 delegate
/// (PressF2Key / ClickMatchedCenter / ...) —— 点击 / 按键 / 其它动作互相不耦合,
/// 共用同一个 WaitForHit 模板等待 + 画框 + 异常兜底。
///
/// 同步阻塞 Tick 持 dispatcher _locker,所以一段执行期间其他 trigger(Daily / Empty)也走不动。
/// </summary>
public sealed class TestZzzTaskTrigger : IZzzTaskTrigger, IDisposable
{
    public string Name => "TestZzzTaskTrigger";
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// F2 按键策略开关:false = 后台 PostMessage + Extension(不抢焦点,可能不被 ZZZ 接收);
    ///                true  = 前台 SendInput + ActivateWindow(抢焦点,已实测 ZZZ 响应)。
    /// 默认 false,后续若 ZZZ 验证后台也能响应再调。
    /// </summary>

    private const double MatchThreshold = 0.96;

    /// <summary>模拟人类反应时间,所有动作前随机等待 300-400ms</summary>
    private static readonly Random _rng = new();
    private const int ActionDelayMinMs = 300;
    private const int ActionDelayMaxMsExclusive = 401; // Random.Next 上界 exclusive


    private const string F2Path = "Assets\\Template\\Daily\\daliyF2_1538_116_36x24_200.png";
    private const string DailySelPath = "Assets\\Template\\Daily\\daliySel_934_148_111x57.png";
    private const string DailyNoSelPath = "Assets\\Template\\Daily\\daliyNoSel_926_158_129x43.png";
    private const string DaliyUiPath = "Assets\\Template\\Daily\\daliyUi_1701_221_54x290.png";
    private const string DaliyGoPath = "Assets\\Template\\Daily\\daliyGo_871_843_181x47.png";

    private readonly ZzzOverlayWindow? _overlay;
    private readonly Func<ZzzCaptureContent?> _captureProvider;
    private readonly PostMessageSimulator? _postMessageSimulator;

    private int _sequence;
    private TemplateImage? _f2Template;
    private TemplateImage? _dailySelTemplate;
    private TemplateImage? _dailyNoSelTemplate;
    private TemplateImage? _daliyUiTemplate;
    private TemplateImage? _daliyGoTemplate;

    public TestZzzTaskTrigger(
        Func<ZzzCaptureContent?> captureProvider,
        ZzzOverlayWindow? overlay = null,
        PostMessageSimulator? postMessageSimulator = null)
    {
        _overlay = overlay;
        _captureProvider = captureProvider;
        _postMessageSimulator = postMessageSimulator;
    }

    public void OnCapture(ZzzCaptureContent content)
    {
        switch (_sequence)
        {
            case 0:
                RunSeqF2();
                break;
            case 1:
                RunSeqDailySel();
                break;
            case 2:
                RunSeqDailyGo();
                break;
        }
    }

    /// <summary> seq 0: 等 F2 模板命中 → 按 F2 键(按 UseForegroundF2 切换前后台) </summary>
    private void RunSeqF2()
    {
        Action<TemplateMatchResult, ZzzCaptureContent> handler = PressF2KeyForeground;
        var result = WaitForHit(F2Path, ref _f2Template, "seq0/F2", handler);
        if (result != null)
        {
            Advance("F2", nextSeq: 1);
        }
        else
        {
            LogMiss("F2");
        }
    }

    /// <summary>
    /// seq 1: 等 daliyUi 菜单面板出现(同一帧上 match DailySelPath / DailyNoSelPath)。
    /// - daliyUi 没出现 → 退回 seq 0
    /// - 出现后在该帧上 match 两个 daily 状态模板:
    ///   · DailyNoSelPath 命中 → 区域内随机点点击
    ///   · DailySelPath 命中或两者都没命中 → 什么也不做
    /// - 最后推进到 seq 2
    /// </summary>
    private void RunSeqDailySel()
    {
        var uiResult = WaitForHit(DaliyUiPath, ref _daliyUiTemplate, "seq1/DaliyUi", (r, hitContent) =>
        {
            // 命中 daliyUi 的这一帧上再做两个 daily 状态匹配
            if (_dailyNoSelTemplate == null)
                _dailyNoSelTemplate = TemplateImage.FromFile(DailyNoSelPath, MatchThreshold);
            if (_dailySelTemplate == null)
                _dailySelTemplate = TemplateImage.FromFile(DailySelPath, MatchThreshold);

            var selResult = _dailySelTemplate.TryMatch(hitContent);
            var noSelResult = _dailyNoSelTemplate.TryMatch(hitContent);
            Debug.WriteLine($"[ZZZ-Test] seq=1 daily dual-match: noSel={noSelResult.Score:F3} sel={selResult.Score:F3}");

            if (noSelResult.Score >= MatchThreshold)
            {
                ClickMatchedArea(noSelResult, hitContent);
            }
        });

        if (uiResult == null)
        {
            Debug.WriteLine($"[ZZZ-Test] seq=1 (DaliyUi) miss → retreat to seq 0");
            _sequence = 0;
            return;
        }

        Advance("DailyMenu", nextSeq: 2);
    }

    /// <summary> seq 3: 等 daliyGo 模板命中 → 区域内随机点点击 → 推进 seq 4 </summary>
    private void RunSeqDailyGo()
    {
        Action<TemplateMatchResult, ZzzCaptureContent> handler = ClickMatchedArea;
        var result = WaitForHit(DaliyGoPath, ref _daliyGoTemplate, "seq3/DaliyGo", handler);
        if (result != null)
        {
            Advance("DailyGo", nextSeq: 4);
        }
        else
        {
            LogMiss("DailyGo");
        }
    }

    /// <summary>
    /// 通用"等模板 + 命中动作"。动作由 caller 注入(PressF2Key / ClickMatchedCenter / 其它),
    /// 不在这里写死。负责画框 + try/catch 兜底,caller 的动作只管自己的事。
    /// </summary>
    private TemplateMatchResult? WaitForHit(
        string path,
        ref TemplateImage? template,
        string label,
        Action<TemplateMatchResult, ZzzCaptureContent>? onHit)
    {
        return TemplateOverlayRunner.WaitForTemplateAppear(
            captureProvider: _captureProvider,
            templateRelativePath: path,
            template: ref template,
            onHit: onHit == null ? null : (r, hitContent) =>
            {
                try
                {
                    var abs = r.AbsRect;
                    var rect = new System.Drawing.Rectangle(abs.X, abs.Y, abs.Width, abs.Height);
                    ZzzTaskTriggerDispatcher.DrawMatchRect(_overlay, rect, hitContent, $"{label} hit {r.Score:F3}");
                    onHit(r, hitContent);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ZZZ-Test] {label} onHit exception: {ex.GetType().Name}: {ex.Message}");
                }
            },
            threshold: MatchThreshold);
    }

    /// <summary>
    /// 键盘动作:PostMessage 后台投递 F2 键(KeyDown + 持续 + KeyUp)。
    /// 优点:不抢焦点。缺点:ZZZ 当前若不在前台,user32 队列消息被丢。
    /// 走 PostMessageSimulatorExtension(KeyId 重载):不走 GIActions→KeyBindingsConfig 链路。
    /// </summary>
    private void PressF2Key(TemplateMatchResult r, ZzzCaptureContent hitContent)
    {
        Thread.Sleep(_rng.Next(ActionDelayMinMs, ActionDelayMaxMsExclusive));
        _postMessageSimulator?.KeyDown(User32.VK.VK_M);
        Debug.WriteLine($"[ZZZ-Test] KeyDown VK_M");
        Thread.Sleep(150);
        _postMessageSimulator?.KeyUp(User32.VK.VK_M);
        Debug.WriteLine($"[ZZZ-Test] KeyUp VK_M");
    }

    /// <summary>
    /// 键盘动作:先把 ZZZ 切到前台,再 SendInput F2 模拟硬件事件走 RIT 投到前台线程。
    /// 对比后台 PostMessage:抢焦点但能跨 ZZZ DX 全屏优化触发 RawInput。
    /// </summary>
    private void PressF2KeyForeground(TemplateMatchResult r, ZzzCaptureContent hitContent)
    {
        SystemControl.ActivateWindow(hitContent.Hwnd);
        Thread.Sleep(_rng.Next(ActionDelayMinMs, ActionDelayMaxMsExclusive));
        Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_F2);
        Thread.Sleep(50);
        Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_F2);
    }

    /// <summary>
    /// 鼠标动作:SendInput 点击命中矩形内随机一点(不是固定中心,避免重复同一坐标被检测)。
    /// 屏幕坐标 = capture 坐标 + captureRect.Left/Top,然后用 65535 归一化喂给 SendInput。
    /// </summary>
    private void ClickMatchedArea(TemplateMatchResult r, ZzzCaptureContent hitContent)
    {
        Thread.Sleep(_rng.Next(ActionDelayMinMs, ActionDelayMaxMsExclusive));
        var abs = r.AbsRect;
        var randX = abs.X + _rng.Next(0, Math.Max(1, abs.Width));
        var randY = abs.Y + _rng.Next(0, Math.Max(1, abs.Height));
        ClickAt(hitContent, randX, randY);
    }

    private static void ClickAt(ZzzCaptureContent hitContent, int captureX, int captureY)
    {
        var captureRect = hitContent.CaptureRect;
        if (captureRect == null)
        {
            Debug.WriteLine("[ZZZ-Test] ClickAt: captureRect is null");
            return;
        }

        var screenX = captureX + captureRect.Value.Left;
        var screenY = captureY + captureRect.Value.Top;
        var screenWidth = User32.GetSystemMetrics(User32.SystemMetric.SM_CXSCREEN);
        var screenHeight = User32.GetSystemMetrics(User32.SystemMetric.SM_CYSCREEN);
        if (screenWidth <= 1 || screenHeight <= 1)
        {
            Debug.WriteLine("[ZZZ-Test] ClickAt: invalid screen metrics");
            return;
        }

        var absX = screenX * 65535.0 / (screenWidth - 1);
        var absY = screenY * 65535.0 / (screenHeight - 1);
        Simulation.SendInput.Mouse.MoveMouseTo(absX, absY);
        Simulation.SendInput.Mouse.LeftButtonClick();
    }

    private void Advance(string fromLabel, int nextSeq)
    {
        Debug.WriteLine($"[ZZZ-Test] seq={_sequence} ({fromLabel}) hit → next seq={nextSeq}");
        _sequence = nextSeq;
    }

    private void LogMiss(string label)
    {
        Debug.WriteLine($"[ZZZ-Test] seq={_sequence} ({label}) miss → stay");
    }

    public void Dispose()
    {
        _f2Template?.Template.Dispose();
        _f2Template = null;
        _dailySelTemplate?.Template.Dispose();
        _dailySelTemplate = null;
        _daliyGoTemplate?.Template.Dispose();
        _daliyGoTemplate = null;
    }
}