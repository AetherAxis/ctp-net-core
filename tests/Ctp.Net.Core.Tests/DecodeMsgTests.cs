using Ctp.Net.Core.Trader;

namespace Ctp.Net.Core.Tests;

/// <summary>
/// <see cref="CtpTrader.DecodeMsg"/> 的契约：CTP 的 <c>ErrorMsg</c> 是**定宽 GBK 字节数组**，
/// 尾部用 <c>\0</c> 补满。OpenCTP 在 Linux/Mac 上不做转码，拿到的就是原始 GB18030 字节。
/// <para>
/// 为什么要单测：这段解码在**每一个**回调的日志与异常消息里跑；一旦 GB18030 provider 未注册，
/// 抛的是 <see cref="ArgumentException"/>（"No data is available for encoding 936"），
/// 而它发生在 SDK 的回调线程上 ⇒ 异常穿出 native 边界 = 进程崩。所以「中文能解、尾部能清」必须钉死。
/// </para>
/// </summary>
public class DecodeMsgTests
{
    [Fact]
    public void 中文GBK字节正确解码()
    {
        // 中=D6D0 文=CEC4 错=B4ED 误=CEF3（GB2312/GBK 码位，非本机编码推导）
        byte[] bytes = [0xD6, 0xD0, 0xCE, 0xC4, 0xB4, 0xED, 0xCE, 0xF3];

        Assert.Equal("中文错误", CtpTrader.DecodeMsg(bytes));
    }

    [Fact]
    public void 尾部NUL被清掉()
    {
        // 定宽槽 81 字节，短消息后面全是 0
        byte[] bytes = [0xD6, 0xD0, 0xCE, 0xC4, 0x00, 0x00, 0x00, 0x00];

        Assert.Equal("中文", CtpTrader.DecodeMsg(bytes));
        Assert.DoesNotContain('\0', CtpTrader.DecodeMsg(bytes));
    }

    [Fact]
    public void 尾部空白也被清掉()
    {
        // ASCII 短消息常是 "  " 补齐；先清 \0 再清空白，两个都要做
        Assert.Equal("CTP:No Error", CtpTrader.DecodeMsg("CTP:No Error \0\0"u8.ToArray()));
        Assert.Equal("abc", CtpTrader.DecodeMsg("abc   "u8.ToArray()));
    }

    [Fact]
    public void 纯NUL解码为空串()
    {
        Assert.Equal(string.Empty, CtpTrader.DecodeMsg(new byte[81]));
    }

    [Fact]
    public void null与空数组给空串()
    {
        Assert.Equal(string.Empty, CtpTrader.DecodeMsg(null));
        Assert.Equal(string.Empty, CtpTrader.DecodeMsg([]));
    }

    [Fact]
    public void ASCII错误消息原样返回()
    {
        // 现场最常见的一类：本地前置直出的英文短消息
        Assert.Equal("Ctp:FrontDisconnected", CtpTrader.DecodeMsg("Ctp:FrontDisconnected\0"u8.ToArray()));
    }

    /// <summary>
    /// 截断在多字节字符中间（定宽槽被写满时可能发生）不能让解码抛异常——
    /// 这条运行在 SDK 回调线程上，抛出去就是进程级故障。
    /// </summary>
    [Fact]
    public void 截断的多字节字符不抛异常()
    {
        byte[] bytes = [0xD6, 0xD0, 0xCE];   // "中" + "文"的前半字节

        var ex = Record.Exception(() => CtpTrader.DecodeMsg(bytes));

        Assert.Null(ex);
    }
}
