using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace BetterGenshinImpact.GameTask.AutoDomain;

/// <summary>
/// 战斗按键替换的静态租约,供 <see cref="AutoDomainTask"/> 在战斗阶段用 using 安装,
/// <see cref="Avatar.KeyDown/KeyUp/KeyPress"/> 在每个 string 键路径上读 <see cref="TrySubstitute"/>。
///
/// 栈式 LIFO:嵌套 install 时新 inner 覆盖外层,inner dispose 后外层接管。仅 AutoDomain 战斗线程
/// (创建栈帧) + 战斗命令 task 线程(读栈顶)访问,故锁粒度极轻,TrySubstitute 命中一次堆分配都不需要。
///
/// 线程安全:AutoDomainTask.DoDomain 在调用线程创建 + AutoFight 战斗 task 在 testThreadPool 读,
/// 跨线程访问,故用 lock + Stack 字段。AutoDispose 在 Lease 上 Interlocked 防止双重 pop。
///
/// 日志:用 Debug.WriteLine 输出到 VS 调试输出窗口,不走 Serilog/App 通道,避免触发
/// 静态 host 初始化(单测或无 WPF host 场景会崩)。生产运行时把 VS 调试器挂上即可看到。
/// </summary>
public static class CombatKeySubstitution
{
    private static readonly object _lock = new();
    private static readonly Stack<IReadOnlyDictionary<string, string>> _stack = new();

    // 已打印过「首次命中」日志的源键集合:每个源键在一次租约生命周期内只打印一次,避免战斗时刷屏。
    // 与 _stack 同生命周期,由 Install 清空,Reset() 也清。
    private static readonly HashSet<string> _loggedHits = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 安装替换映射到栈顶。<paramref name="map"/> 引用被 Lease 持有,Lease dispose 前必须保持 map 有效。
    /// 用 OrdinalIgnoreCase 包装,大小写不敏感(脚本里 e press / E press 都生效)。
    /// </summary>
    public static IDisposable Install(IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        var wrapped = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
        lock (_lock)
        {
            _stack.Push(wrapped);
            _loggedHits.Clear();
        }
        Debug.WriteLine($"[CombatKeySubstitution] 已启用,映射({wrapped.Count} 项): " +
                        string.Join(", ", wrapped.Select(kv => $"{kv.Key}→{kv.Value}")));
        return new Lease(wrapped);
    }

    /// <summary>
    /// 查替换:<paramref name="key"/> 命中栈顶映射返回目标键,否则返回 null(放行)。
    /// 大小写不敏感。仍防模糊匹配(如 "escape" 不会匹配 "e")。
    /// </summary>
    public static string? TrySubstitute(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }
        IReadOnlyDictionary<string, string>? top;
        string? mapped = null;
        bool firstHit = false;

        lock (_lock)
        {
            top = _stack.Count > 0 ? _stack.Peek() : null;
            if (top != null && top.TryGetValue(key, out var m))
            {
                mapped = m;
                firstHit = _loggedHits.Add(key);
            }
        }

        if (mapped != null)
        {
            if (firstHit)
            {
                Debug.WriteLine($"[CombatKeySubstitution] 命中(首次): {key} → {mapped}");
            }
            else
            {
                Debug.WriteLine($"[CombatKeySubstitution] 命中: {key} → {mapped}");
            }
        }
        return mapped;
    }

    /// <summary>测试用:清空栈。生产代码不应调用。</summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _stack.Clear();
            _loggedHits.Clear();
        }
    }

    private sealed class Lease : IDisposable
    {
        private readonly IReadOnlyDictionary<string, string> _map;
        private int _disposed;

        public Lease(IReadOnlyDictionary<string, string> map) { _map = map; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }
            lock (_lock)
            {
                // 找到对应帧弹出(防御乱序 Dispose)
                var buffer = new Stack<IReadOnlyDictionary<string, string>>();
                while (_stack.Count > 0)
                {
                    var top = _stack.Pop();
                    if (ReferenceEquals(top, _map))
                    {
                        while (buffer.Count > 0)
                        {
                            _stack.Push(buffer.Pop());
                        }
                        return;
                    }
                    buffer.Push(top);
                }
                while (buffer.Count > 0)
                {
                    _stack.Push(buffer.Pop());
                }
            }
        }
    }
}
