using System;
using System.Diagnostics;
using System.Threading;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.View.Windows;
using OpenCvSharp;
using Vanara.PInvoke;
using CvRect = OpenCvSharp.Rect;

namespace BetterGenshinImpact.GameTask.Zzz;

/// <summary>
/// 测试 trigger：跑一个 N 段的序列。每一段 = "同步阻塞等模板 + 命中后执行该段专属动作"。
///
/// 帧内流程:OnCapture → 整层 try/catch 包 switch → 跑当前段(最多阻塞 3s 等模板) → 命中则执行段动作 + 推进到下一段(轮转回 0);
/// 超时则停留在当前段,下一帧重试。每段各自一个 RunSeq* 方法,各自调用
/// PressKeyForeground / ClickMatchedArea 等动作 —— 不在 WaitForHit 里塞 onHit 回调,
/// 因为 WaitForTemplateAppear 是同步阻塞的,caller 在调用返回后直接做事 + Dispose content 即可。
/// 异常由 OnCapture 顶层 try/catch 兜住,Debug message 包含 _sequence 定位段。
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
    private const string DialogOpt1Path = "Assets\\Template\\Daily\\dialogOpt1_1376_570_44x44.png";

    // 继续对话箭头 »» (屏幕最右下角):半透明白灰(无彩色 + 高亮度)。
    // 动画过程中箭头会在 ~30~150 px 区间缩放,持续从右下角向中央略有位移;
    // ROI 扩到 42×45 覆盖动画位移 + 帧间位置抖动。
    // 常量值与 DailyTaskZzzTrigger.ContinueArrow* 完全一致,便于阅读。
    private static readonly CvRect ContinueArrowRoi = new(1462, 966, 42, 45);
    private const int ContinueArrowSMax = 40;
    private const int ContinueArrowVMin = 130;
    // 连通域形状过滤:面积下限 80 是关键 — 箭头本体稳定在 168~171,UI 装饰类亮斑(任务标记/按钮描边) ≤ 90,
    // 把 MinArea 从 30 提到 80 后,ScreenShot_2026-08-09 误识别场景下 largest blob#16 (area=90) 因 aspect=1.20 不达标被拒,
    // 次大的 blob#1 (area=76) 因 area<80 被拒;箭头(淡帧 171 / 亮帧 168)稳定通过。
    private const int ContinueArrowMinArea = 80;
    private const int ContinueArrowMaxArea = 400;
    private const double ContinueArrowMinAspect = 1.3;
    private const double ContinueArrowMinExtent = 0.4;
    // 亮像素集中度 0.5:箭头 »» 是单连通域(~100%);UI 散点群最大 blob 通常只占 10~20%。
    private const double ContinueArrowMinLargestFraction = 0.5;
    // 凸性上界 0.85:»» 是凹形(chevron 围 V 形),convexity ≈ 0.6~0.8;UI 按钮是凸形,convexity > 0.9
    private const double ContinueArrowMaxConvexity = 0.85;
    // 高度下限 16:»» bbox ≈ 14×20(高 20);底部进度条 / 任务追踪条 bbox ≈ 30×12(高 12 < 16 被拒)
    private const int ContinueArrowMinHeight = 16;
    // 连通域上限 2:»» 通常 1 个 blob(动画极端相位 ≤ 2);UI 多 blob 装饰结构 3~8
    private const int ContinueArrowMaxLabels = 2;

    private readonly ZzzOverlayWindow? _overlay;
    private readonly Func<ZzzCaptureContent?> _captureProvider;
    private readonly PostMessageSimulator? _postMessageSimulator;

    private int _sequence;
    private TemplateImage? _f2Template;
    private TemplateImage? _dailySelTemplate;
    private TemplateImage? _dailyNoSelTemplate;
    private TemplateImage? _daliyUiTemplate;
    private TemplateImage? _daliyGoTemplate;
    private TemplateImage? _dialogOpt1Template;

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
        try
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
                case 3:
                    RunSeqF();
                    break;
                case 4:
                    RunSeqContinueArrow();
                    break;
                case 5:
                    RunSeqDialogOpt1();
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-Test] seq={_sequence} exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary> seq 0: 等 F2 模板命中 → 按 F2 键(按 UseForegroundF2 切换前后台) </summary>
    private void RunSeqF2()
    {
        var (result, content) = WaitForHit(F2Path, ref _f2Template);
        if (result == null || content == null)
        {
            LogMiss("seq0/F2");
            return;
        }
        DrawHitRect(result, content, "seq0/F2");
        PressKeyForeground(User32.VK.VK_F2, content);
        content.Dispose();
        Advance("seq0/F2", nextSeq: _sequence+1);
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
        var (uiResult, hitContent) = WaitForHit(DaliyUiPath, ref _daliyUiTemplate);
        if (uiResult == null || hitContent == null)
        {
            Debug.WriteLine($"[ZZZ-Test] seq=1 (DaliyUi) miss → retreat to seq 0");
            _sequence = 0;
            return;
        }

        DrawHitRect(uiResult, hitContent, "seq1/DaliyUi");
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
        hitContent.Dispose();
        Advance("DailyMenu", nextSeq: 2);
    }

    /// <summary> seq 2: 等 daliyGo 模板命中 → 区域内随机点点击 → 推进 seq 3 </summary>
    private void RunSeqDailyGo()
    {
        var (result, content) = WaitForHit(DaliyGoPath, ref _daliyGoTemplate);
        if (result == null || content == null)
        {
            LogMiss("DailyGo");
            return;
        }
        DrawHitRect(result, content, "seq2/DaliyGo");
        ClickMatchedArea(result, content);
        content.Dispose();
        Advance("DailyGo", nextSeq: 3);
    }

    /// <summary>
    /// seq 3: 点完 DailyGo 进入对话框后,F2 按钮重新出现表示可以按 F 选择第一项对话选项。
    /// 命中后前台 SendInput 按 F。
    /// </summary>
    private void RunSeqF()
    {
        var (result, content) = WaitForHit(F2Path, ref _f2Template);
        if (result == null || content == null)
        {
            LogMiss("F");
            return;
        }
        DrawHitRect(result, content, "seq3/F");
        PressKeyForeground(User32.VK.VK_F, content);
        content.Dispose();
        Advance("F", nextSeq: 4);
    }

    /// <summary>
    /// seq 4: 等右下角"继续对话"箭头 (HSV 颜色掩膜 + 连通域形状过滤) 命中 → 按空格键 → 回环 seq 0。
    /// 走 <see cref="HsvOverlayRunner.WaitForHsvAppear"/> 同步阻塞 3s 内多帧截图轮询,
    /// 与 seq 0/1/2/3 的 <see cref="WaitForHit"/> 体感一致。
    /// </summary>
    private void RunSeqContinueArrow()
    {
        var hitContent = HsvOverlayRunner.WaitForHsvAppear(
            captureProvider: _captureProvider,
            detector: c => ZzzImageUtils.DetectArrowBlob(
                c.Image, ContinueArrowRoi,
                ContinueArrowSMax, ContinueArrowVMin,
                ContinueArrowMinArea, ContinueArrowMaxArea,
                ContinueArrowMinHeight,
                ContinueArrowMinAspect, ContinueArrowMinExtent,
                ContinueArrowMinLargestFraction, ContinueArrowMaxConvexity,
                ContinueArrowMaxLabels),
            onHit: c =>
            {
                var safeRoi = ZzzImageUtils.ClampRoi(ContinueArrowRoi, c.Image.Width, c.Image.Height);
                ZzzTaskTriggerDispatcher.DrawMatchRect(
                    _overlay,
                    new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
                    c,
                    "seq4/ContinueArrow");

                // 把命中帧的 ROI 切片落盘,事后可肉眼比对 / 发给 Claude 验证是否真箭头
                try
                {
                    var snapshotPath = ZzzImageUtils.SaveArrowRoiSnapshot(c.Image, ContinueArrowRoi, "test_seq4");
                    Debug.WriteLine($"[ZZZ-Test] saved snapshot: {snapshotPath}");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ZZZ-Test] snapshot save failed: {ex.GetType().Name}: {ex.Message}");
                }
            },
            totalTimeoutMs: 3000);

        if (hitContent == null)
        {
            LogMiss("ContinueArrow");
            return;
        }

        PressKeyForeground(User32.VK.VK_SPACE, hitContent);
        Advance("ContinueArrow", nextSeq: 5);
    }

    /// <summary>
    /// seq 5: ContinueArrow 按空格推进对话文本后,等对话框第一项的"①"指示图标出现 → 按数字键 1 选择第一项。
    /// 命中后前台 SendInput 按 VK_1。回到 seq 4 继续推进下一段文本。
    /// </summary>
    private void RunSeqDialogOpt1()
    {
        var (result, content) = WaitForHit(DialogOpt1Path, ref _dialogOpt1Template);
        if (result == null || content == null)
        {
            LogMiss("DialogOpt1");
            return;
        }
        DrawHitRect(result, content, "seq5/DialogOpt1");
        // PressKeyForeground(User32.VK.VK_1, content);
        content.Dispose();
        Advance("DialogOpt1", nextSeq: 4);
    }

    /// <summary>
    /// 同步阻塞等模板出现,直接转发 <see cref="TemplateOverlayRunner.WaitForTemplateAppear"/>。
    /// WaitForTemplateAppear 是同步的(最多 3s 轮询),不需要 callback 模式 — caller 拿到 tuple
    /// 后自己画框 / 做动作 / Dispose content。
    /// 返回 (null, null) = 超时 / 模板加载失败。命中 / 未命中日志由 caller 决定。
    /// </summary>
    private (TemplateMatchResult? result, ZzzCaptureContent? content) WaitForHit(
        string path,
        ref TemplateImage? template)
    {
        return TemplateOverlayRunner.WaitForTemplateAppear(
            captureProvider: _captureProvider,
            templateRelativePath: path,
            template: ref template,
            threshold: MatchThreshold);
    }

    /// <summary>
    /// 在命中帧上画命中框。仅画框,不负责 Dispose content。
    /// </summary>
    private void DrawHitRect(TemplateMatchResult result, ZzzCaptureContent content, string label)
    {
        var abs = result.AbsRect;
        var rect = new System.Drawing.Rectangle(abs.X, abs.Y, abs.Width, abs.Height);
        ZzzTaskTriggerDispatcher.DrawMatchRect(_overlay, rect, content, $"{label} hit {result.Score:F3}");
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
    /// 键盘动作:SendInput 按下指定虚拟键(KeyDown + 持续 50ms + KeyUp)模拟硬件事件走 RIT 投到前台线程。
    /// 对比后台 PostMessage:抢焦点但能跨 ZZZ DX 全屏优化触发 RawInput。
    ///
    /// 焦点切换只在前台被抢占或窗口被最小化时执行 — 避免反复调 ShowWindow(SW_RESTORE)
    /// 触发 ZZZ DX 全屏 swap chain 重初始化(实测会让 ZZZ + BetterGI 互相阻塞)。
    /// 直接方法,不返回 delegate — caller 自己决定是 lambda 套一层(给 WaitForHit 的 onHit)还是直接调。
    /// </summary>
    private void PressKeyForeground(User32.VK keyCode, ZzzCaptureContent hitContent)
    {
        if (User32.GetForegroundWindow() != hitContent.Hwnd)
        {
            SystemControl.ActivateWindow(hitContent.Hwnd);
        }
        Thread.Sleep(_rng.Next(ActionDelayMinMs, ActionDelayMaxMsExclusive));
        Simulation.SendInput.Keyboard.KeyDown(keyCode);
        Thread.Sleep(50);
        Simulation.SendInput.Keyboard.KeyUp(keyCode);
    }

    /// <summary>
    /// 鼠标动作:SendInput 点击命中矩形内随机一点(不是固定中心,避免重复同一坐标被检测)。
    /// 屏幕坐标 = capture 坐标 + captureRect.Left/Top,然后用 65535 归一化喂给 SendInput。
    /// </summary>
    private void ClickMatchedArea(TemplateMatchResult r, ZzzCaptureContent hitContent)
    {
        if (User32.GetForegroundWindow() != hitContent.Hwnd)
        {
            SystemControl.ActivateWindow(hitContent.Hwnd);
        }
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
        _dialogOpt1Template?.Template.Dispose();
        _dialogOpt1Template = null;
    }
}