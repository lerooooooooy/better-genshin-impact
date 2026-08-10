using System;
using System.Diagnostics;
using System.Threading;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.OCR;
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
    // 阈值 2 = "连续 > 2 次" = 第 3 次 retreat 时暂停 trigger,避免 seq=N ↔ seq=M 无限空转
    private const int RetreatStreakPauseThreshold = 2;


    private const string F2Path = "Assets\\Template\\Daily\\daliyF2_1538_116_36x24_200.png";
    private const string DailySelPath = "Assets\\Template\\Daily\\daliySel_934_148_111x57.png";
    private const string DailyNoSelPath = "Assets\\Template\\Daily\\daliyNoSel_926_158_129x43.png";
    private const string DaliyUiPath = "Assets\\Template\\Daily\\daliyUi_1701_221_54x290.png";
    private const string DaliyGoPath = "Assets\\Template\\Daily\\daliyGo_871_843_181x47.png";
    // 日常完成度判断(嵌入 RunSeqDailySel,不在独立 seq):
    // - daliyFin:capture (1541, 264),59x79 — 绿色"已完成"标记
    // - daliyReach:capture (832, 865),162x40 — 灰色"已达成待领取"标记
    private const string DaliyFinPath = "Assets\\Template\\Daily\\daliyFin_1541_264_59x79.png";
    private const string DaliyReachPath = "Assets\\Template\\Daily\\daliyReach_832_865_162x40.png";
    // daliyReach 命中后点击领取按钮:(1469, 257, 163x79)
    private static readonly CvRect DaliyClaimClickRect = new(1469, 257, 163, 79);
    private const string DialogOpt1Path = "Assets\\Template\\Daily\\dialogOpt1_1391_573_16x29_200.png";
    private const string Rwd1Path = "Assets\\Template\\Daily\\rwd1_1161_510_74x58_400.png";
    private const string GetBatteryPath = "Assets\\Template\\Daily\\getBattery_907_464_105x105.png";
    // 领取电池弹窗的"已领取/确认"按钮区域:固定位置,跟 getBattery 模板(907,464)无关。
    // 在矩形内随机点,避免重复同一坐标被检测。
    private static readonly CvRect GetBatteryClickRect = new(854, 709, 203, 37);
    // "rin" 红色 logo 模板 (rwd2):capture 位置 (1013, 632),尺寸 201x119,无缩放后缀。
    private const string RinPath = "Assets\\Template\\Daily\\rwd2_1013_632_201x119.png";
    // rwd3 模板:capture 位置 (1047, 662),尺寸 112x96,无缩放后缀。
    private const string Rwd3Path = "Assets\\Template\\Daily\\rwd3_1047_662_112x96.png";
    // rwd4 模板:capture 位置 (927, 406),尺寸 63x33,无缩放后缀。
    private const string Rwd4Path = "Assets\\Template\\Daily\\rwd4_927_406_63x33.png";
    // rwd5 模板:capture 位置 (934, 855),尺寸 56x26,无缩放后缀。"明日" 文字 + 旁边奖励信息。
    private const string Rwd5Path = "Assets\\Template\\Daily\\rwd5_934_855_56x26.png";
    // rwd6 模板:capture 位置 (1257, 955),尺寸 121x34,无缩放后缀。
    private const string Rwd6Path = "Assets\\Template\\Daily\\rwd6_1257_955_121x34.png";
    // rwd7 模板:capture 位置 (543, 233),尺寸 115x29,无缩放后缀。上方垂直条纹 — 结束/过渡遮罩。
    private const string Rwd7Path = "Assets\\Template\\Daily\\rwd7_543_233_115x29.png";
    // shop2 模板:capture 位置 (845, 575),尺寸 115x22,无缩放后缀。
    // 点击区 (829,642,145x228) 在模板正下方,跟 shop2 模板位置无关 — 固定 UI 按钮区。
    private const string Shop2Path = "Assets\\Template\\Daily\\shop2_845_575_115x22.png";
    private static readonly CvRect Shop2ClickRect = new(829, 642, 145, 228);
    // shop3 模板:capture 位置 (68, 1012),尺寸 118x29,无缩放后缀。
    // 点击区 (1605,1008,204x38) 在右下角,跟 shop3 模板(左下)无关 — 固定 UI 按钮区。
    private const string Shop3Path = "Assets\\Template\\Daily\\shop3_68_1012_118x29.png";
    private static readonly CvRect Shop3ClickRect = new(1605, 1008, 204, 38);
    // shop4 模板:capture 位置 (1056, 630),尺寸 90x228,无缩放后缀。
    // 命中后点击模板匹配框内随机一点(模板本身就是按钮,无需独立 ROI)。
    private const string Shop4Path = "Assets\\Template\\Daily\\shop4_1056_630_90x228.png";
    // shop5 模板:capture 位置 (1282, 1010),尺寸 187x34,无缩放后缀。
    // 命中后点击模板匹配框内随机一点(模板本身就是要点的按钮,无需独立 ROI)。
    private const string Shop5Path = "Assets\\Template\\Daily\\shop5_1282_1010_187x34.png";
    // shop6 模板:capture 位置 (1546, 955),尺寸 244x34,无缩放后缀。
    // 命中后点击模板匹配框内随机一点(模板本身就是要点的按钮,无需独立 ROI)。
    private const string Shop6Path = "Assets\\Template\\Daily\\shop6_1546_955_244x34.png";
    // "准备营业" 文字 OCR 检测:capture 区域 (912, 498),尺寸 123x52。
    // 命中后点击固定 UI 区域 (1029, 606, 188x36)(跟 OCR 区域无关 — 固定按钮位置)。
    private static readonly CvRect ReadyToOpenRoi = new(912, 498, 123, 52);
    private static readonly CvRect ReadyToOpenClickRect = new(1029, 606, 188, 36);
    private const string ReadyToOpenText = "准备营业";
    // "诚信经营" 文字 OCR 检测:capture 区域 (811, 507),尺寸 124x36。
    // 命中后点击固定 UI 区域 (846, 604, 218x42)(跟 OCR 区域无关 — 固定按钮位置)。
    private static readonly CvRect HonestBusinessRoi = new(811, 507, 124, 36);
    private static readonly CvRect HonestBusinessClickRect = new(846, 604, 218, 42);
    private const string HonestBusinessText = "诚信经营";

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
    // RunSeq* 失败时把 label 记到这里,OnCapture retreat 日志用 — 失败后 _sequence -= 1 再读 LogMiss 会拿到错 seq 数字
    private string? _lastSeqLabel;
    // 单层 retreat 状态:true = 当前段 miss 已 retreat 过 1 次,下一帧再 miss 直接全扫描(不再退)。
    // hit / 全扫描跳 seq 后清 false。新一轮 OnCapture 开始是 false。
    private bool _retreatUsed;
    // 连续 retreat 检测:同一源 seq 连续 retreat 次数超阈值时暂停 trigger。
    // 场景:seq=N 总 miss → retreat seq=M → seq=M 立即 hit → advance seq=N → seq=N 又 miss → ...
    // _lastRetreatFromSeq = 上次 retreat 的源 seq,-1 = 无历史;源 seq 变化时 streak 自然重置为 1
    private int _lastRetreatFromSeq = -1;
    private int _retreatStreakCount;
    private readonly TemplateWaitTarget _f2Target = new("F2", F2Path, MatchThreshold);
    private readonly TemplateWaitTarget _dailySelTarget = new("DailySel", DailySelPath, MatchThreshold);
    private readonly TemplateWaitTarget _dailyNoSelTarget = new("DailyNoSel", DailyNoSelPath, MatchThreshold);
    private readonly TemplateWaitTarget _daliyUiTarget = new("DaliyUi", DaliyUiPath, MatchThreshold);
    private readonly TemplateWaitTarget _daliyGoTarget = new("DaliyGo", DaliyGoPath, MatchThreshold);
    private readonly TemplateWaitTarget _daliyFinTarget = new("DaliyFin", DaliyFinPath, MatchThreshold);
    private readonly TemplateWaitTarget _daliyReachTarget = new("DaliyReach", DaliyReachPath, MatchThreshold);
    private readonly TemplateWaitTarget _dialogOpt1Target = new("DialogOpt1", DialogOpt1Path, MatchThreshold);
    private readonly TemplateWaitTarget _rwd1Target = new("Rwd1", Rwd1Path, MatchThreshold);
    private readonly TemplateWaitTarget _getBatteryTarget = new("GetBattery", GetBatteryPath, MatchThreshold);
    private readonly TemplateWaitTarget _rinTarget = new("Rin", RinPath, MatchThreshold);
    private readonly TemplateWaitTarget _rwd3Target = new("Rwd3", Rwd3Path, MatchThreshold);
    private readonly TemplateWaitTarget _rwd4Target = new("Rwd4", Rwd4Path, MatchThreshold);
    private readonly TemplateWaitTarget _rwd5Target = new("Rwd5", Rwd5Path, MatchThreshold);
    private readonly TemplateWaitTarget _rwd6Target = new("Rwd6", Rwd6Path, MatchThreshold);
    private readonly TemplateWaitTarget _rwd7Target = new("Rwd7", Rwd7Path, MatchThreshold);
    private readonly TemplateWaitTarget _shop2Target = new("Shop2", Shop2Path, MatchThreshold);
    private readonly TemplateWaitTarget _shop3Target = new("Shop3", Shop3Path, MatchThreshold);
    private readonly TemplateWaitTarget _shop4Target = new("Shop4", Shop4Path, MatchThreshold);
    private readonly TemplateWaitTarget _shop5Target = new("Shop5", Shop5Path, MatchThreshold);
    private readonly TemplateWaitTarget _shop6Target = new("Shop6", Shop6Path, MatchThreshold);
    private readonly OcrWaitTarget _readyToOpenTarget = new("ReadyToOpen", ReadyToOpenRoi, ReadyToOpenText);
    private readonly OcrWaitTarget _honestBusinessTarget = new("HonestBusiness", HonestBusinessRoi, HonestBusinessText);

    // 全模板扫描集合(单帧 scan):OnCapture 在 _sequence == 0 失败时遍历,任一命中即跳对应 seq。
    // 顺序影响优先级:F2 排第一 → 画面里同时有 F2 + 其他模板时先跳 seq 0(用户已确认)。
    // DailySel/DailyNoSel 不进 — 它们是 RunSeqDailySel 内的副模板,不做序列跳转。
    // ContinueArrow HSV 进 — 让 seq 4 可以从兜底扫描进入(不只依赖 seq 3 → 4 自然推进)。
    // 必须是 instance 字段:C# 不允许 field initializer 引用 instance 字段(CS0236)。
    private readonly (IZzzWaitTarget target, int seq)[] _scanTargets;

    public TestZzzTaskTrigger(
        Func<ZzzCaptureContent?> captureProvider,
        ZzzOverlayWindow? overlay = null,
        PostMessageSimulator? postMessageSimulator = null)
    {
        _overlay = overlay;
        _captureProvider = captureProvider;
        _postMessageSimulator = postMessageSimulator;

        _scanTargets = new (IZzzWaitTarget target, int seq)[]
        {
            (_f2Target, 0),
            (_daliyUiTarget, 1),
            (_daliyGoTarget, 2),
            (new HsvWaitTarget("ContinueArrow",
                c => ZzzImageUtils.DetectArrowBlob(
                    c.Image, ContinueArrowRoi,
                    ContinueArrowSMax, ContinueArrowVMin,
                    ContinueArrowMinArea, ContinueArrowMaxArea,
                    ContinueArrowMinHeight,
                    ContinueArrowMinAspect, ContinueArrowMinExtent,
                    ContinueArrowMinLargestFraction, ContinueArrowMaxConvexity,
                    ContinueArrowMaxLabels)), 4),
            (_dialogOpt1Target, 5),
            (_rwd6Target, 6),
            (_rwd1Target, 7),
            (_getBatteryTarget, 8),
            (_rinTarget, 9),
            (_rwd3Target, 10),
            (_rwd4Target, 11),
            (_rwd5Target, 12),
            (_rwd7Target, 13),
            (_shop2Target, 14),
            (_shop3Target, 15),
            (_shop4Target, 16),
            (_shop5Target, 17),
            (_shop6Target, 18),
            (_readyToOpenTarget, 19),
            (_honestBusinessTarget, 20),
        };
    }

    public void OnCapture(ZzzCaptureContent content)
    {
        try
        {
            bool hit = _sequence switch
            {
                0 => RunSeqF2(),
                1 => RunSeqDailySel(),
                2 => RunSeqDailyGo(),
                3 => RunSeqF(),
                4 => RunSeqContinueArrow(),
                5 => RunSeqDialogOpt1(),
                6 => RunSeqRwd6(),
                7 => RunSeqRwd1(),
                8 => RunSeqGetBattery(),
                9 => RunSeqRin(),
                10 => RunSeqRwd3(),
                11 => RunSeqRwd4(),
                12 => RunSeqRwd5(),
                13 => RunSeqRwd7(),
                14 => RunSeqShop2(),
                15 => RunSeqShop3(),
                16 => RunSeqShop4(),
                17 => RunSeqShop5(),
                18 => RunSeqShop6(),
                19 => RunSeqReadyToOpen(),
                20 => RunSeqHonestBusiness(),
                _ => false,  // _sequence 越界(idle)
            };

            if (hit)
            {
                _retreatUsed = false;
                return;
            }

            // 失败分支:
            // - _sequence > 0 且未 retreat 过: retreat 1,下一帧重试 _sequence - 1
            // - _sequence > 0 但已 retreat 过(_retreatUsed): 跳过 retreat,直接全扫描(避免一路退到 seq=0 浪费时间)
            // - _sequence == 0: 直接全扫描
            if (_sequence > 0)
            {
                if (_retreatUsed)
                {
                    _retreatUsed = false;
                    _lastSeqLabel = null;
                    Debug.WriteLine($"[ZZZ-Test] seq={_sequence} (after retreat) miss → scan recovery");
                    // fall through to scan
                }
                else
                {
                    int prevSeq = _sequence;
                    _sequence -= 1;
                    _retreatUsed = true;

                    // 累计 retreat streak:同一源 seq 连续 retreat → 可能卡死
                    if (prevSeq == _lastRetreatFromSeq)
                    {
                        _retreatStreakCount++;
                    }
                    else
                    {
                        _retreatStreakCount = 1;
                        _lastRetreatFromSeq = prevSeq;
                    }

                    // 同一源 retreat 超过阈值 → 暂停 trigger,避免空转(seq=N ↔ seq=M 无限循环)
                    if (_retreatStreakCount > RetreatStreakPauseThreshold)
                    {
                        IsEnabled = false;
                        Debug.WriteLine($"[ZZZ-Test] PAUSE: stuck retreating from seq={prevSeq}, streak={_retreatStreakCount}; IsEnabled=false (re-enable manually to resume)");
                        _retreatUsed = false;
                        _retreatStreakCount = 0;
                        _lastRetreatFromSeq = -1;
                        _lastSeqLabel = null;
                        return;
                    }

                    Debug.WriteLine($"[ZZZ-Test] seq={prevSeq} ({_lastSeqLabel ?? "?"}) miss → retreat to seq={_sequence}");
                    _lastSeqLabel = null;
                    return;
                }
            }

            // _sequence == 0 失败 → 全模板扫描
            var (matchedLabel, scanContent) = ScanAllMainTemplates();
            if (scanContent == null)
            {
                Debug.WriteLine("[ZZZ-Test] scan recovery: capture returned null (minimized?)");
                return;
            }
            if (matchedLabel == null)
            {
                Debug.WriteLine("[ZZZ-Test] scan recovery: nothing matched, idle");
                scanContent.Dispose();
                return;
            }

            int targetSeq = matchedLabel switch
            {
                "F2" => 0,
                "DaliyUi" => 1,
                "DaliyGo" => 2,
                "ContinueArrow" => 4,
                "DialogOpt1" => 5,
                "Rwd6" => 6,
                "Rwd1" => 7,
                "GetBattery" => 8,
                "Rin" => 9,
                "Rwd3" => 10,
                "Rwd4" => 11,
                "Rwd5" => 12,
                "Rwd7" => 13,
                "Shop2" => 14,
                "Shop3" => 15,
                "Shop4" => 16,
                "Shop5" => 17,
                "Shop6" => 18,
                "ReadyToOpen" => 19,
                "HonestBusiness" => 20,
                _ => _sequence,
            };
            Debug.WriteLine($"[ZZZ-Test] scan recovery: hit label={matchedLabel} → seq={targetSeq}");
            scanContent.Dispose();
            _retreatUsed = false;
            _sequence = targetSeq;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-Test] seq={_sequence} exception: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary> seq 0: 等 F2 模板命中 → 按 F2 键(按 UseForegroundF2 切换前后台) </summary>
    private bool RunSeqF2()
    {
        var (label, result, content) = WaitForHit(targets: _f2Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq0/F2";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq0/F2");
        PressKeyForeground(User32.VK.VK_F2, content);
        content.Dispose();
        Advance("seq0/F2");
        return true;
    }

    /// <summary>
    /// seq 1: 等 daliyUi 菜单面板出现(同一帧上 match DailySelPath / DailyNoSelPath)。
    /// - daliyUi 没出现 → return false(OnCapture 收到失败后 retreat _sequence -= 1)
    /// - 出现后在该帧上 match 两个 daily 状态模板:
    ///   · DailyNoSelPath 命中 → 区域内随机点点击
    ///   · DailySelPath 命中或两者都没命中 → 什么也不做
    /// - 最后推进到 seq 2
    /// </summary>
    private bool RunSeqDailySel()
    {
        var (uiLabel, uiResult, hitContent) = WaitForHit(targets: _daliyUiTarget);
        if (uiResult == null || hitContent == null)
        {
            _lastSeqLabel = uiLabel ?? "seq1/DaliyUi";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;

        DrawHitRect(uiResult, hitContent, "seq1/DaliyUi");

        var selHit = _dailySelTarget.TryMatch(hitContent, out var selMatch);
        var noSelHit = _dailyNoSelTarget.TryMatch(hitContent, out var noSelMatch);
        Debug.WriteLine($"[ZZZ-Test] seq=1 daily dual-match: noSelHit={noSelHit} score={noSelMatch?.Score:F3} selHit={selHit} score={selMatch?.Score:F3}");

        if (noSelHit && noSelMatch != null)
        {
            ClickMatchedArea(noSelMatch, hitContent);
        }
        hitContent.Dispose();

        // 日常完成度判断(嵌入,不独立 seq):
        // - daliyFin 命中 → 日常已完成(已领取),整个 trigger 退出
        // - daliyReach 命中 → 日常已完成但未领取,点击 (1469,257,163x79) 领取后继续 seq 2
        // - 都没命中 → 继续 seq 2
        ZzzCaptureContent? checkContent;
        try
        {
            checkContent = _captureProvider();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-Test] daily completion check: capture threw: {ex.GetType().Name}: {ex.Message}");
            checkContent = null;
        }

        if (checkContent != null)
        {
            bool finHit = _daliyFinTarget.TryMatch(checkContent, out var finMatch);
            if (finHit)
            {
                Debug.WriteLine("[ZZZ-Test] daily completion check: DaliyFin hit → daily complete, exiting trigger");
                if (finMatch != null)
                {
                    DrawHitRect(finMatch, checkContent, "DailyFin");
                }
                checkContent.Dispose();
                IsEnabled = false;
                return true;
            }

            bool reachHit = _daliyReachTarget.TryMatch(checkContent, out var reachMatch);
            if (reachHit)
            {
                Debug.WriteLine("[ZZZ-Test] daily completion check: DaliyReach hit → clicking claim area");
                if (reachMatch != null)
                {
                    DrawHitRect(reachMatch, checkContent, "DailyReach");
                }
                ClickRect(checkContent, DaliyClaimClickRect);
                checkContent.Dispose();
                Advance("DailyMenu");
                return true;
            }

            checkContent.Dispose();
        }

        Advance("DailyMenu");
        return true;
    }

    /// <summary> seq 2: 等 daliyGo 模板命中 → 区域内随机点点击 → 推进 seq 3 </summary>
    private bool RunSeqDailyGo()
    {
        var (label, result, content) = WaitForHit(targets: _daliyGoTarget);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq2/DaliyGo";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq2/DaliyGo");
        ClickMatchedArea(result, content);
        content.Dispose();
        Advance("DailyGo");
        return true;
    }

    /// <summary>
    /// seq 3: 点完 DailyGo 进入对话框后,F2 按钮重新出现表示可以按 F 选择第一项对话选项。
    /// 命中后前台 SendInput 按 F。
    /// </summary>
    private bool RunSeqF()
    {
        var (label, result, content) = WaitForHit(targets: _f2Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq3/F";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq3/F");
        PressKeyForeground(User32.VK.VK_F, content);
        content.Dispose();
        Advance("F");
        return true;
    }

    /// <summary>
    /// seq 4: 等右下角"继续对话"箭头 (HSV 颜色掩膜 + 连通域形状过滤) 命中 → 按空格键 → 推进 seq 5。
    /// 走 <see cref="WaitForHit"/> + <see cref="HsvWaitTarget"/>,与其他段共享 3s 轮询预算 + captureProvider,
    /// HSV 命中时 TemplateMatchResult 为 null,caller 走 HSV 专属画框 + 落盘逻辑。
    /// </summary>
    private bool RunSeqContinueArrow()
    {
        var (label, result, content) = WaitForHit(targets: new HsvWaitTarget("ContinueArrow",
            c => ZzzImageUtils.DetectArrowBlob(
                c.Image, ContinueArrowRoi,
                ContinueArrowSMax, ContinueArrowVMin,
                ContinueArrowMinArea, ContinueArrowMaxArea,
                ContinueArrowMinHeight,
                ContinueArrowMinAspect, ContinueArrowMinExtent,
                ContinueArrowMinLargestFraction, ContinueArrowMaxConvexity,
                ContinueArrowMaxLabels)));

        if (content == null)
        {
            _lastSeqLabel = label ?? "seq4/ContinueArrow";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;

        var safeRoi = ZzzImageUtils.ClampRoi(ContinueArrowRoi, content.Image.Width, content.Image.Height);
        ZzzTaskTriggerDispatcher.DrawMatchRect(
            _overlay,
            new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
            content,
            "seq4/ContinueArrow");

        // 把命中帧的 ROI 切片落盘,事后可肉眼比对 / 发给 Claude 验证是否真箭头
        try
        {
            var snapshotPath = ZzzImageUtils.SaveArrowRoiSnapshot(content.Image, ContinueArrowRoi, "test_seq4");
            Debug.WriteLine($"[ZZZ-Test] saved snapshot: {snapshotPath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-Test] snapshot save failed: {ex.GetType().Name}: {ex.Message}");
        }

        PressKeyForeground(User32.VK.VK_SPACE, content);
        content.Dispose();
        Advance("ContinueArrow");
        return true;
    }

    /// <summary>
    /// seq 5: ContinueArrow 按空格推进对话文本后,等对话框第一项的"①"指示图标出现 → 按数字键 1 选择第一项。
    /// 命中后前台 SendInput 按 VK_1。回到 seq 4 继续推进下一段文本。
    /// </summary>
    private bool RunSeqDialogOpt1()
    {
        var (label, result, content) = WaitForHit(targets: _dialogOpt1Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq5/DialogOpt1";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq5/DialogOpt1");
        PressKeyForeground(User32.VK.VK_1, content);
        content.Dispose();
        Advance("DialogOpt1");  // 回环到 ContinueArrow,不走默认 +1
        return true;
    }

    /// <summary>
    /// seq 6: rwd6 模板出现 → 命中后按 ESC 键(与其他 ESC 段同行为)。
    /// </summary>
    private bool RunSeqRwd6()
    {
        var (label, result, content) = WaitForHit(targets: _rwd6Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq6/Rwd6";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq6/Rwd6");
        PressKeyForeground(User32.VK.VK_ESCAPE, content);
        content.Dispose();
        Advance("seq6/Rwd6");
        return true;
    }

    /// <summary>
    /// seq 7: 奖励弹窗 (rwd1) 出现 → 命中后点击 rwd1 模板匹配框内随机一点(尝试关闭弹窗)。
    /// </summary>
    private bool RunSeqRwd1()
    {
        var (label, result, content) = WaitForHit(targets: _rwd1Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq7/Rwd1";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq7/Rwd1");
        ClickMatchedArea(result, content);
        content.Dispose();
        Advance("seq7/Rwd1");
        return true;
    }

    /// <summary>
    /// seq 8: 领取电池弹窗 (getBattery) 出现 → 命中后点击固定"已领取"按钮区 (854,709,203x37) 内随机一点。
    /// </summary>
    private bool RunSeqGetBattery()
    {
        var (label, result, content) = WaitForHit(targets: _getBatteryTarget);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq8/GetBattery";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq8/GetBattery");
        ClickRect(content, GetBatteryClickRect);
        content.Dispose();
        Advance("seq8/GetBattery");
        return true;
    }

    /// <summary>
    /// seq 9: "rin" 红色 logo 出现 → 命中后点击 rin 模板匹配框内随机一点。
    /// </summary>
    private bool RunSeqRin()
    {
        var (label, result, content) = WaitForHit(targets: _rinTarget);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq9/Rin";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq9/Rin");
        ClickMatchedArea(result, content);
        content.Dispose();
        Advance("seq9/Rin");
        return true;
    }

    /// <summary>
    /// seq 10: rwd3 模板出现 → 命中后点击匹配框内随机一点。
    /// </summary>
    private bool RunSeqRwd3()
    {
        var (label, result, content) = WaitForHit(targets: _rwd3Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq10/Rwd3";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq10/Rwd3");
        ClickMatchedArea(result, content);
        content.Dispose();
        Advance("seq10/Rwd3");
        return true;
    }

    /// <summary>
    /// seq 11: rwd4 模板出现 → 命中后按 ESC 键(尝试关闭弹窗/退出当前状态)。
    /// </summary>
    private bool RunSeqRwd4()
    {
        var (label, result, content) = WaitForHit(targets: _rwd4Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq11/Rwd4";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq11/Rwd4");
        PressKeyForeground(User32.VK.VK_ESCAPE, content);
        content.Dispose();
        Advance("seq11/Rwd4");
        return true;
    }

    /// <summary>
    /// seq 12: rwd5 模板出现 → 命中后按 ESC 键(与 seq 11 rwd4 同行为)。
    /// </summary>
    private bool RunSeqRwd5()
    {
        var (label, result, content) = WaitForHit(targets: _rwd5Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq12/Rwd5";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq12/Rwd5");
        PressKeyForeground(User32.VK.VK_ESCAPE, content);
        content.Dispose();
        Advance("seq12/Rwd5");
        return true;
    }

    /// <summary>
    /// seq 13: rwd7 模板出现 → 命中后按 ESC 键(结束/过渡遮罩,典型 ESC 关闭行为)。
    /// </summary>
    private bool RunSeqRwd7()
    {
        var (label, result, content) = WaitForHit(targets: _rwd7Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq13/Rwd7";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq13/Rwd7");
        PressKeyForeground(User32.VK.VK_ESCAPE, content);
        content.Dispose();
        Advance("seq13/Rwd7");
        return true;
    }

    /// <summary>
    /// seq 14: shop2 模板出现 → 命中后点击固定 UI 区域 (829, 642, 145x228)(模板正下方的按钮)。
    /// </summary>
    private bool RunSeqShop2()
    {
        var (label, result, content) = WaitForHit(targets: _shop2Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq14/Shop2";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq14/Shop2");
        ClickRect(content, Shop2ClickRect);
        content.Dispose();
        Advance("seq14/Shop2");
        return true;
    }

    /// <summary>
    /// seq 15: shop3 模板出现 → 命中后点击右下角固定 UI 区域 (1605, 1008, 204x38)(模板在左下,点击在右下)。
    /// </summary>
    private bool RunSeqShop3()
    {
        var (label, result, content) = WaitForHit(targets: _shop3Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq15/Shop3";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq15/Shop3");
        ClickRect(content, Shop3ClickRect);
        content.Dispose();
        Advance("seq15/Shop3");
        return true;
    }

    /// <summary>
    /// seq 16: shop4 模板出现 → 命中后点击模板匹配框内随机一点(模板本身就是要点的按钮)。
    /// </summary>
    private bool RunSeqShop4()
    {
        var (label, result, content) = WaitForHit(targets: _shop4Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq16/Shop4";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq16/Shop4");
        ClickMatchedArea(result, content);
        content.Dispose();
        Advance("seq16/Shop4");
        return true;
    }

    /// <summary>
    /// seq 17: shop5 模板出现 → 命中后点击模板匹配框内随机一点(模板本身就是要点的按钮)。
    /// </summary>
    private bool RunSeqShop5()
    {
        var (label, result, content) = WaitForHit(targets: _shop5Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq17/Shop5";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq17/Shop5");
        ClickMatchedArea(result, content);
        content.Dispose();
        Advance("seq17/Shop5");
        return true;
    }

    /// <summary>
    /// seq 18: shop6 模板出现 → 命中后点击模板匹配框内随机一点(模板本身就是要点的按钮)。
    /// </summary>
    private bool RunSeqShop6()
    {
        var (label, result, content) = WaitForHit(targets: _shop6Target);
        if (result == null || content == null)
        {
            _lastSeqLabel = label ?? "seq18/Shop6";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;
        DrawHitRect(result, content, "seq18/Shop6");
        ClickMatchedArea(result, content);
        content.Dispose();
        Advance("seq18/Shop6");
        return true;
    }

    /// <summary>
    /// seq 19: 在 (912, 498, 123x52) ROI 内 OCR 检测到"准备营业"文本 → 命中后点击 (1029, 606, 188x36) 固定区域。
    /// 走 <see cref="WaitForHit"/> + <see cref="OcrWaitTarget"/>,与 HSV 路径同形 — OCR 命中时 TemplateMatchResult 为 null,
    /// 用 ROI 矩形画框。
    /// </summary>
    private bool RunSeqReadyToOpen()
    {
        var (label, result, content) = WaitForHit(targets: _readyToOpenTarget);
        if (content == null)
        {
            _lastSeqLabel = label ?? "seq19/ReadyToOpen";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;

        var safeRoi = ZzzImageUtils.ClampRoi(ReadyToOpenRoi, content.Image.Width, content.Image.Height);
        ZzzTaskTriggerDispatcher.DrawMatchRect(
            _overlay,
            new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
            content,
            "seq19/ReadyToOpen");

        ClickRect(content, ReadyToOpenClickRect);
        content.Dispose();
        Advance("seq19/ReadyToOpen");
        return true;
    }

    /// <summary>
    /// seq 20: 在 (811, 507, 124x36) ROI 内 OCR 检测到"诚信经营"文本 → 命中后点击 (846, 604, 218x42) 固定区域。
    /// 与 seq 19 ReadyToOpen 完全同模式。
    /// </summary>
    private bool RunSeqHonestBusiness()
    {
        var (label, result, content) = WaitForHit(targets: _honestBusinessTarget);
        if (content == null)
        {
            _lastSeqLabel = label ?? "seq20/HonestBusiness";
            LogMiss(_lastSeqLabel);
            return false;
        }
        _lastSeqLabel = null;

        var safeRoi = ZzzImageUtils.ClampRoi(HonestBusinessRoi, content.Image.Width, content.Image.Height);
        ZzzTaskTriggerDispatcher.DrawMatchRect(
            _overlay,
            new System.Drawing.Rectangle(safeRoi.X, safeRoi.Y, safeRoi.Width, safeRoi.Height),
            content,
            "seq20/HonestBusiness");

        ClickRect(content, HonestBusinessClickRect);
        content.Dispose();
        Advance("seq20/HonestBusiness");
        return true;
    }

    /// <summary>
    /// 同步阻塞等任意 target 命中。在 <paramref name="totalTimeoutMs"/> 内最多尝试 <paramref name="maxAttempts"/> 次,
    /// 每次通过 <see cref="_captureProvider"/> 拉新一帧,遍历 <paramref name="targets"/> 顺序 TryMatch,
    /// 任一命中即返回(matchedLabel + TemplateMatchResult? + content)。
    ///
    /// 捕获失败(<see cref="_captureProvider"/> 返回 null)不计入尝试次数、不消耗 sleep。
    /// 命中分支:TemplateMatchResult? 仅在模板命中时非 null(给 DrawHitRect 用);HSV 命中为 null(caller 走 HSV 专属画框)。
    /// 全部未命中 / 任一 target 模板加载失败 → (null, null, null),内部已 Dispose 中间帧。
    ///
    /// 同步阻塞:单次调用最长占用 totalTimeoutMs。caller 负责在收到非 null content 后 Dispose。
    /// </summary>
    private (string? matchedLabel, TemplateMatchResult? result, ZzzCaptureContent? content) WaitForHit(
        int totalTimeoutMs = 3000,
        int maxAttempts = 3,
        params IZzzWaitTarget[] targets)
    {
        if (targets == null || targets.Length == 0)
        {
            return (null, null, null);
        }

        if (maxAttempts < 1 || totalTimeoutMs <= 0)
        {
            return (null, null, null);
        }

        var intervalMs = maxAttempts > 1 ? totalTimeoutMs / (maxAttempts - 1) : totalTimeoutMs;
        var startMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
        var deadlineMs = startMs + totalTimeoutMs;

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var nowMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency;
            if (nowMs > deadlineMs)
            {
                break;
            }

            ZzzCaptureContent? content;
            try
            {
                content = _captureProvider();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: capture provider threw: {ex.GetType().Name}: {ex.Message}");
                content = null;
            }

            if (content == null)
            {
                Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: capture returned null (minimized?)");
            }
            else
            {
                foreach (var target in targets)
                {
                    bool hit;
                    TemplateMatchResult? matchResult;
                    try
                    {
                        hit = target.TryMatch(content, out matchResult);
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: {target.Label} TryMatch threw: {ex.GetType().Name}: {ex.Message}");
                        continue;
                    }

                    if (hit)
                    {
                        Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: HIT type={(matchResult != null ? "template" : "hsv")} label={target.Label}{FormatMatchDetail(matchResult, target.Threshold)}");
                        return (target.Label, matchResult, content);
                    }

                    // per-target miss log:score + threshold 直观看离命中差多远
                    Debug.WriteLine($"[ZZZ-Wait] attempt {attempt}/{maxAttempts}: {target.Label} miss{FormatMatchDetail(matchResult, target.Threshold)}");
                }

                content.Dispose();
            }

            if (attempt < maxAttempts)
            {
                Thread.Sleep(intervalMs);
            }
        }

        var totalMs = Stopwatch.GetTimestamp() * 1000L / Stopwatch.Frequency - startMs;
        Debug.WriteLine($"[ZZZ-Wait] budget exhausted: attempts={maxAttempts} elapsed={totalMs}ms targets=[{string.Join(",", System.Linq.Enumerable.Select(targets, t => t.Label))}]");
        return (null, null, null);
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
    /// 格式化 score + threshold 给日志用。
    /// 模板命中/未命中 → " score=0.583 threshold=0.960";HSV → " hsv detector"。
    /// </summary>
    private static string FormatMatchDetail(TemplateMatchResult? result, double? threshold)
    {
        var s = result != null ? $" score={result.Score:F3}" : "";
        var t = threshold.HasValue ? $" threshold={threshold.Value:F3}" : " hsv detector";
        return $"{s}{t}";
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

    /// <summary>
    /// 鼠标动作:SendInput 点击给定 capture 坐标矩形内随机一点。
    /// 与 <see cref="ClickMatchedArea"/> 的区别:点击位置来自调用方指定的固定 UI 区域,
    /// 而非模板匹配的 absRect(用于匹配框 ≠ 实际按钮位置的场景,如 getBattery 弹窗)。
    /// </summary>
    private void ClickRect(ZzzCaptureContent hitContent, CvRect rect)
    {
        if (User32.GetForegroundWindow() != hitContent.Hwnd)
        {
            SystemControl.ActivateWindow(hitContent.Hwnd);
        }
        Thread.Sleep(_rng.Next(ActionDelayMinMs, ActionDelayMaxMsExclusive));
        var randX = rect.X + _rng.Next(0, Math.Max(1, rect.Width));
        var randY = rect.Y + _rng.Next(0, Math.Max(1, rect.Height));
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

    private void Advance(string fromLabel, int nextSeq = -1)
    {
        if (nextSeq < 0) nextSeq = _sequence + 1;
        Debug.WriteLine($"[ZZZ-Test] seq={_sequence} ({fromLabel}) hit → next seq={nextSeq}");
        _sequence = nextSeq;
    }

    private void LogMiss(string label)
    {
        Debug.WriteLine($"[ZZZ-Test] seq={_sequence} ({label}) miss → stay");
    }

    /// <summary>
    /// 全模板扫描兜底:在 _sequence == 0 失败时由 OnCapture 调用。
    /// 单帧拉一帧 capture,顺序遍历 _scanTargets,任一命中即返回(matchedLabel + content 给 caller Dispose);
    /// 全 miss 则 Dispose content + 返回 (null, null)。
    ///
    /// 同步阻塞:单次 capture + N 次 TryMatch,不轮询、不 sleep,通常 < 50ms 完成。
    /// </summary>
    private (string? matchedLabel, ZzzCaptureContent? content) ScanAllMainTemplates()
    {
        Debug.WriteLine($"[ZZZ-Test] scan recovery: starting single-frame scan, targets=[{string.Join(",", System.Linq.Enumerable.Select(_scanTargets, t => t.target.Label))}]");

        ZzzCaptureContent? content;
        try
        {
            content = _captureProvider();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ZZZ-Test] scan recovery: capture threw: {ex.GetType().Name}: {ex.Message}");
            return (null, null);
        }
        if (content == null)
        {
            Debug.WriteLine("[ZZZ-Test] scan recovery: capture returned null (minimized?)");
            return (null, null);
        }

        foreach (var (target, seq) in _scanTargets)
        {
            bool hit;
            TemplateMatchResult? result;
            try
            {
                hit = target.TryMatch(content, out result);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ZZZ-Test] scan recovery: {target.Label} TryMatch threw: {ex.GetType().Name}: {ex.Message}");
                continue;
            }
            if (hit)
            {
                Debug.WriteLine($"[ZZZ-Test] scan recovery: HIT type={(result != null ? "template" : "hsv")} label={target.Label}{FormatMatchDetail(result, target.Threshold)}");
                return (target.Label, content);
            }
            Debug.WriteLine($"[ZZZ-Test] scan recovery: check label={target.Label} miss{FormatMatchDetail(result, target.Threshold)}");
        }

        Debug.WriteLine($"[ZZZ-Test] scan recovery: nothing matched ({_scanTargets.Length} targets checked)");
        content.Dispose();
        return (null, null);
    }

    public void Dispose()
    {
        _f2Target.Dispose();
        _dailySelTarget.Dispose();
        _dailyNoSelTarget.Dispose();
        _daliyUiTarget.Dispose();
        _daliyGoTarget.Dispose();
        _daliyFinTarget.Dispose();
        _daliyReachTarget.Dispose();
        _dialogOpt1Target.Dispose();
        _rwd1Target.Dispose();
        _getBatteryTarget.Dispose();
        _rinTarget.Dispose();
        _rwd3Target.Dispose();
        _rwd4Target.Dispose();
        _rwd5Target.Dispose();
        _rwd6Target.Dispose();
        _rwd7Target.Dispose();
        _shop2Target.Dispose();
        _shop3Target.Dispose();
        _shop4Target.Dispose();
        _shop5Target.Dispose();
        _shop6Target.Dispose();
        _readyToOpenTarget.Dispose();
        _honestBusinessTarget.Dispose();
    }
}