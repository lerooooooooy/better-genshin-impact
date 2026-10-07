using System;
using System.Collections.Generic;
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
/// </summary>
public static class CombatKeySubstitution
{
    private static readonly object _lock = new();
    private static readonly Stack<IReadOnlyDictionary<string, string>> _stack = new();

    /// <summary>
    /// 安装替换映射到栈顶。<paramref name="map"/> 引用被 Lease 持有,Lease dispose 前必须保持 map 有效。
    /// </summary>
    public static IDisposable Install(IReadOnlyDictionary<string, string> map)
    {
        ArgumentNullException.ThrowIfNull(map);
        lock (_lock)
        {
            _stack.Push(map);
        }
        return new Lease(map);
    }

    /// <summary>
    /// 查替换:<paramref name="key"/> 命中栈顶映射返回目标键,否则返回 null(放行)。
    /// 严格相等匹配,"E" 与 "e" 区分。
    /// </summary>
    public static string? TrySubstitute(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return null;
        }
        IReadOnlyDictionary<string, string>? top;
        lock (_lock)
        {
            top = _stack.Count > 0 ? _stack.Peek() : null;
        }
        return top != null && top.TryGetValue(key, out var mapped) ? mapped : null;
    }

    /// <summary>测试用:清空栈。生产代码不应调用。</summary>
    public static void Reset()
    {
        lock (_lock)
        {
            _stack.Clear();
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