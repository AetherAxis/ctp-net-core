using Ctp.Net.Core;

namespace Ctp.Net.Core.Tests;

/// <summary>
/// <see cref="CtpRequestIdAllocator"/> 的行为契约（`DESIGN.md` §6 #1/#4）。
/// 这三条规则各自对应一次现场事故，所以逐条钉住：跳过标记字母表、0xFD 回卷、避让 in-flight。
/// </summary>
public class RequestIdAllocatorTests
{
    /// <summary>
    /// 一整轮完整序列：<c>1..0xDF</c> 然后 <c>0xF0..0xFD</c>，一个不差、一个不少。
    /// 同时验证「标记字母表 0xE0..0xEF / 0xFE 永不出现」和「到 0xFD 回卷」。
    /// </summary>
    [Fact]
    public void 一整轮序列_跳标记字母表_到0xFD回卷()
    {
        var expected = Enumerable.Range(1, CtpRequestIdAllocator.WrapAt)
            .Where(v => !CtpRequestIdAllocator.IsMarkerByte(v))
            .ToArray();

        var alloc = new CtpRequestIdAllocator();
        var actual = expected.Select(_ => alloc.Next()).ToArray();

        Assert.Equal(expected, actual);
        // 全轮无标记字节（上面的序列推导已蕴含，但显式断言一次，防将来改 WrapAt 时静默放过）
        Assert.DoesNotContain(actual, v => CtpRequestIdAllocator.IsMarkerByte(v));
        // 一轮的最后一个号就是回卷点
        Assert.Equal(CtpRequestIdAllocator.WrapAt, actual[^1]);
    }

    /// <summary>回卷是**回卷到 1**（不是 0、不是 0xFD 复用）。</summary>
    [Fact]
    public void 回卷后从1重新开始()
    {
        var alloc = new CtpRequestIdAllocator();
        var count = Enumerable.Range(1, CtpRequestIdAllocator.WrapAt)
            .Count(v => !CtpRequestIdAllocator.IsMarkerByte(v));

        for (var i = 0; i < count; i++) alloc.Next();

        Assert.Equal(1, alloc.Next());
        Assert.Equal(2, alloc.Next());
    }

    /// <summary>
    /// 0xDF 的下一个必须是 0xF0——这就是当年 4097 死循环的回卷边界（0xE0..0xEF 十六个号被跳过）。
    /// </summary>
    [Fact]
    public void 序号从0xDF直接跳到0xF0()
    {
        var alloc = new CtpRequestIdAllocator();
        int id;
        do id = alloc.Next(); while (id != 0xDF);

        Assert.Equal(0xF0, alloc.Next());
        Assert.Equal(0xF1, alloc.Next());
    }

    /// <summary>in-flight 的号必须被跳过，且跳过后仍然连续递增（不回退到已用号）。</summary>
    [Fact]
    public void 避让in_flight的号()
    {
        var alloc = new CtpRequestIdAllocator();
        var inUse = new HashSet<int> { 1, 2, 3, 0xF0 };

        var taken = new List<int>();
        for (var i = 0; i < 10; i++)
        {
            var id = alloc.Next(n => inUse.Contains(n));
            Assert.DoesNotContain(id, taken);
            taken.Add(id);
        }

        Assert.Equal(new[] { 4, 5, 6, 7, 8, 9, 10, 11, 12, 13 }, taken);
    }

    /// <summary>
    /// 回卷点恰好被 in-flight 占据时继续往后找（而不是撞上去就放弃/复用）。
    /// </summary>
    [Fact]
    public void 回卷点被占则回卷后继续找()
    {
        var alloc = new CtpRequestIdAllocator();
        int id;
        do id = alloc.Next(); while (id != 0xFC);

        // 0xFD 被占 ⇒ 回卷到 1
        Assert.Equal(1, alloc.Next(n => n == 0xFD));
        // 1、2 也被占 ⇒ 3
        Assert.Equal(3, alloc.Next(n => n is 1 or 2));
    }

    /// <summary>
    /// 耗尽必须**抛异常**而不是空转（2026-10-29 现场：写满后分配线程无 sleep 无限循环，
    /// 表现为整个客户端假死且零日志）。异常消息要带现场信息，方便一眼定位是哪种 pending 泄漏。
    /// </summary>
    [Fact]
    public void 全占用时抛异常并附现场信息()
    {
        var alloc = new CtpRequestIdAllocator();

        var ex = Assert.Throws<InvalidOperationException>(
            () => alloc.Next(_ => true, () => "_pending=237 / _queryPending=0 仍占用"));

        Assert.Contains("CTP 请求号耗尽", ex.Message);
        Assert.Contains("_pending=237", ex.Message);
    }

    /// <summary>耗尽判定发生在锁内、重试上限之内——不允许一个坏 inUse 把线程钉死。</summary>
    [Fact]
    public void 耗尽不会无限重试()
    {
        var alloc = new CtpRequestIdAllocator();
        var calls = 0;

        // Assert.Throws 本身就是「会返回」的证明；calls 只用于确认它没有绕开上限空转
        // （每次尝试里落在标记字母表的号会先被 continue 掉，不进 inUse，所以 calls <= MaxAttempts）。
        Assert.Throws<InvalidOperationException>(() => alloc.Next(_ => { calls++; return true; }));

        Assert.InRange(calls, 1, CtpRequestIdAllocator.MaxAttempts);
    }

    /// <summary>
    /// 并发取号不得重号（分配点是串行化的；Trader 侧还套了 <c>_pendingLock</c>，
    /// 但分配器自身必须独立成立）。
    /// </summary>
    [Fact]
    public void 并发取号不重号()
    {
        var alloc = new CtpRequestIdAllocator();
        const int n = 200;   // < 一轮 237，保证无回卷
        var ids = new int[n];

        Parallel.For(0, n, i => ids[i] = alloc.Next());

        Assert.Equal(n, ids.Distinct().Count());
        Assert.DoesNotContain(ids, v => CtpRequestIdAllocator.IsMarkerByte(v));
        Assert.All(ids, v => Assert.InRange(v, 1, CtpRequestIdAllocator.WrapAt));
    }
}
