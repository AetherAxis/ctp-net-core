namespace Ctp.Net.Core;

/// <summary>
/// CTP 请求号（RequestID）分配器——Trader 与 Md 共用（`DESIGN.md` §6 #4：原本两边各写一份，
/// Md 那份只是 <c>Interlocked.Increment</c>，既不跳过转义字节也没有 in-flight 避让）。
/// <para>
/// 三条硬规则（全部来自现场）：
/// <list type="number">
/// <item>**跳过标记字母表字节** <c>0xE0..0xEF</c> / <c>0xFE</c>：reqId 会作为裸字节进入 wire 字段，
/// 落在字母表里会被读端当成字段边界（2026-10-13 断连风暴同一族问题）。</item>
/// <item>**<c>&gt;= 0xFD</c> 回卷**：SDK 的请求号字段只有 1 字节，超出即溢出。</item>
/// <item>**避让 in-flight**：调用方传入的判定里必须包含「已登记未回收」的号——
/// 复用会串台（应答被派发给另一笔请求）。</item>
/// </list>
/// </para>
/// <para>
/// 线程安全：分配器自己持 <c>_gate</c>；<paramref name="inUse"/> 在锁内被调用，
/// 所以它内部只能读**已经被外层锁保护**的结构，且**不得再取外层锁**（否则与调用方的加锁顺序互逆）。
/// </para>
/// </summary>
internal sealed class CtpRequestIdAllocator
{
    /// <summary>单次分配的尝试上限——正常一次就中；连续失败只可能是 in-flight 泄漏。</summary>
    internal const int MaxAttempts = 2048;

    /// <summary>标记字母表（字段边界），reqId 不能落在这里。</summary>
    internal static bool IsMarkerByte(int id) => (id & 0xFF) is >= 0xE0 and <= 0xEF or 0xFE;

    /// <summary>请求号上限：到这个值就回卷（1 字节字段）。</summary>
    internal const int WrapAt = 0xFD;

    private readonly object _gate = new();
    private int _next;

    /// <summary>
    /// 取下一个可用请求号。取不到时抛 <see cref="InvalidOperationException"/>——
    /// 把「静默自旋」变成可见故障（2026-10-29 现场：写满后分配线程无 sleep 空转，客户端整体假死）。
    /// </summary>
    /// <param name="inUse">可选：判断某号是否仍被占用（已登记未回收）。</param>
    /// <param name="detail">可选：耗尽时附在异常消息里的现场信息（惰性求值，正常路径零开销）。</param>
    public int Next(Func<int, bool>? inUse = null, Func<string>? detail = null)
    {
        lock (_gate)
        {
            for (var attempt = 0; attempt < MaxAttempts; attempt++)
            {
                var n = ++_next;
                if (IsMarkerByte(n)) continue;
                if (n >= WrapAt) _next = 0;               // 回卷：下一轮从 1 起
                if (inUse is not null && inUse(n)) continue;
                return n;
            }
        }

        var extra = detail?.Invoke();
        throw new InvalidOperationException(
            "CTP 请求号耗尽" + (string.IsNullOrEmpty(extra) ? "" : $"：{extra}") +
            "（正常应有超时回收——看到这条说明有请求既不回也不超时）。");
    }
}
