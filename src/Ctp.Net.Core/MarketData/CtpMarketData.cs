using System.Runtime.InteropServices;
using System.Text;
using Ctp.Net.Core.Native;
using Ctp.Net.Core.Trader;

namespace Ctp.Net.Core.MarketData;

/// <summary>CTP 行情接口封装。生命周期：Create → LoginAsync → SubscribeMarketDataAsync → (OnTick) → Dispose。</summary>
public sealed class CtpMarketData : IDisposable
{
    // 真控制台流（同 CtpTrader.RealOut 的理由：绕开 Lean 引擎的 Console.SetOut(algorithm.Debug) 重定向，
    // 防回调线程被算法消息管道堵死——2026-10-14 实测 4:20 周期性 4097 死循环根因）。
    private static readonly TextWriter RealOut =
        TextWriter.Synchronized(new StreamWriter(Console.OpenStandardOutput(), Console.OutputEncoding) { AutoFlush = true });

    /// <summary>
    /// 逐笔 tick 控制台日志开关（2026-10-26 日志降噪）：默认关——bot 直连远程前置时行情活跃 ~2900 笔/s，
    /// 逐笔打印日志失控。调试要看得设 <c>AETHER_CTP_TICK_TRACE=1</c>（bot 与 console 的 [tick] 行同此开关，
    /// 一个变量开两处）。订阅/登录/连接事件不受此开关（低频，运维定位用）。
    /// </summary>
    public static bool TickTrace { get; } =
        Environment.GetEnvironmentVariable("AETHER_CTP_TICK_TRACE") is "1" or "true";

    // 注册 GB18030 编码提供程序（解码 CTP 的 GBK 中文报文）。与 CtpTrader 共用，任一被使用即注册。
    static CtpMarketData()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private IntPtr _handle;
    private GCHandle _selfHandle;
    private int _nextRequestId;

    private readonly TaskCompletionSource<bool> _frontConnectedTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<RspCallback> _loginTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Dictionary<int, TaskCompletionSource<RspCallback>> _pending = new();
    private readonly object _pendingLock = new();

    private readonly CtpMdCallbacks.OnFrontConnectedDelegate _dFrontConnected;
    private readonly CtpMdCallbacks.OnFrontDisconnectedDelegate _dFrontDisconnected;
    private readonly CtpMdCallbacks.OnRspErrorDelegate _dRspError;
    private readonly CtpMdCallbacks.OnRspUserLoginDelegate _dRspUserLogin;
    private readonly CtpMdCallbacks.OnRspSubMarketDataDelegate _dRspSub;
    private readonly CtpMdCallbacks.OnRspUnSubMarketDataDelegate _dUnSub;
    private readonly CtpMdCallbacks.OnRtnDepthMarketDataDelegate _dRtnDepth;

    public event EventHandler? FrontConnected;
    public event EventHandler<int>? FrontDisconnected;
    public event EventHandler<TickData>? TickReceived;

    /// <summary>订阅/退订应答（M3 E2E 用——SDK 即裁判的订阅链路断言面）。</summary>
    public event EventHandler<(string InstrumentId, int ErrorId, string ErrorMsg, bool IsLast)>? SubMarketDataResult;
    public event EventHandler<RspErrorEventArgs>? RspError;

    public bool IsDisposed => _handle == IntPtr.Zero;

    public CtpMarketData(string flowPath)
    {
        _selfHandle = GCHandle.Alloc(this);
        _dFrontConnected = OnFrontConnectedCb;
        _dFrontDisconnected = OnFrontDisconnectedCb;
        _dRspError = OnRspErrorCb;
        _dRspUserLogin = OnRspUserLoginCb;
        _dRspSub = OnRspSubMarketDataCb;
        _dUnSub = OnRspUnSubMarketDataCb;
        _dRtnDepth = OnRtnDepthMarketDataCb;

        var cb = new CtpMdCallbacks
        {
            OnFrontConnected = Marshal.GetFunctionPointerForDelegate(_dFrontConnected),
            OnFrontDisconnected = Marshal.GetFunctionPointerForDelegate(_dFrontDisconnected),
            OnRspError = Marshal.GetFunctionPointerForDelegate(_dRspError),
            OnRspUserLogin = Marshal.GetFunctionPointerForDelegate(_dRspUserLogin),
            OnRspSubMarketData = Marshal.GetFunctionPointerForDelegate(_dRspSub),
            OnRspUnSubMarketData = Marshal.GetFunctionPointerForDelegate(_dUnSub),
            OnRtnDepthMarketData = Marshal.GetFunctionPointerForDelegate(_dRtnDepth),
        };
        _handle = MdNativeMethods.CtpMd_Create(flowPath ?? "", ref cb, GCHandle.ToIntPtr(_selfHandle));
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("CtpMd_Create 失败");
    }

    private int NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    /// <summary>连接前置并登录行情。Md 登录不需认证/采集。</summary>
    public async Task LoginAsync(string front, string brokerId, string userId, string password,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        MdNativeMethods.CtpMd_RegisterFront(_handle, front);
        MdNativeMethods.CtpMd_Init(_handle);

        await _frontConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);

        var reqId = NextRequestId();
        int rc = MdNativeMethods.CtpMd_Login(_handle, brokerId, userId, password, reqId);
        if (rc != 0) throw new CtpException($"CtpMd_Login 发送失败 rc={rc}");
        var rsp = await _loginTcs.Task.WaitAsync(TimeSpan.FromSeconds(15), ct);
        if (rsp.Info.ErrorID != 0)
            throw new CtpBusinessException(rsp.Info.ErrorID, DecodeMsg(rsp.Info.ErrorMsg));
    }

    /// <summary>订阅行情。完成后开始接收 OnTick。</summary>
    public async Task SubscribeMarketDataAsync(IReadOnlyList<string> symbols, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        var arr = symbols.ToArray();
        int rc = MdNativeMethods.CtpMd_SubscribeMarketData(_handle, arr, arr.Length);
        if (rc != 0) throw new CtpException($"SubscribeMarketData 发送失败 rc={rc}");
        // 不阻塞等待 OnRspSubMarketData（订阅结果按合约逐个回调；Tick 流即开始）
        await Task.CompletedTask;
    }

    private TaskCompletionSource<RspCallback> RegisterPending(int reqId)
    {
        var tcs = new TaskCompletionSource<RspCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pendingLock) _pending[reqId] = tcs;
        return tcs;
    }

    private void TryCompletePending(int reqId, RspCallback cb)
    {
        TaskCompletionSource<RspCallback>? tcs;
        lock (_pendingLock) _pending.Remove(reqId, out tcs);
        tcs?.TrySetResult(cb);
    }

    // —— 回调（CTP 内部线程）——

    private static CtpMarketData Self(IntPtr ctx) => (CtpMarketData)GCHandle.FromIntPtr(ctx).Target!;

    private void OnFrontConnectedCb(IntPtr ctx)
    {
        var self = Self(ctx);
        RealOut.WriteLine("[ctp-md] 前置已连接");
        self._frontConnectedTcs.TrySetResult(true);
        self.FrontConnected?.Invoke(self, EventArgs.Empty);
    }

    private void OnFrontDisconnectedCb(IntPtr ctx, int reason)
    {
        var self = Self(ctx);
        RealOut.WriteLine($"[ctp-md] 前置断开 reason={reason}");
        self.FrontDisconnected?.Invoke(self, reason);
    }

    private void OnRspErrorCb(IntPtr ctx, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        RealOut.WriteLine($"[ctp-md] RspError ErrorID={i.ErrorID} {DecodeMsg(i.ErrorMsg)} reqId={requestId} isLast={isLast}");
        self.RspError?.Invoke(self, new RspErrorEventArgs(i.ErrorID, DecodeMsg(i.ErrorMsg), requestId));
        self.TryCompletePending(requestId, new RspCallback(i, null));
    }

    private void OnRspUserLoginCb(IntPtr ctx, IntPtr field, IntPtr info, int requestId, bool isLast)
    {
        var self = Self(ctx);
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        var f = field != IntPtr.Zero ? Marshal.PtrToStructure<RspUserLoginField>(field) : default;
        RealOut.WriteLine($"[ctp-md] 登录响应 ErrorID={i.ErrorID} {DecodeMsg(i.ErrorMsg)} TradingDay={f.TradingDay}");
        self._loginTcs.TrySetResult(new RspCallback(i, null));
    }

    private void OnRspSubMarketDataCb(IntPtr ctx, IntPtr field, IntPtr info, int requestId, bool isLast)
    {
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        var f = field != IntPtr.Zero ? Marshal.PtrToStructure<SpecificInstrumentField>(field) : default;
        RealOut.WriteLine($"[ctp-md] 订阅响应 InstrumentID={f.InstrumentID} ErrorID={i.ErrorID} {DecodeMsg(i.ErrorMsg)} isLast={isLast}");
        Self(ctx).SubMarketDataResult?.Invoke(Self(ctx), (f.InstrumentID, i.ErrorID, DecodeMsg(i.ErrorMsg), isLast));
    }

    private void OnRspUnSubMarketDataCb(IntPtr ctx, IntPtr field, IntPtr info, int requestId, bool isLast)
    {
        var i = info != IntPtr.Zero ? Marshal.PtrToStructure<RspInfoField>(info) : default;
        RealOut.WriteLine($"[ctp-md] 退订响应 ErrorID={i.ErrorID} isLast={isLast}");
    }

    private void OnRtnDepthMarketDataCb(IntPtr ctx, IntPtr field)
    {
        var self = Self(ctx);
        if (field == IntPtr.Zero) return;
        var f = Marshal.PtrToStructure<DepthMarketDataField>(field);
        // 逐笔 tick 日志默认关（2026-10-26 日志降噪）：真实部署下 bot 直连远程前置（无同值去重的旧构建）
        // 行情活跃时 ~2900 笔/s，逐笔打印会把 bot 日志刷成每天数百 MB——调试要看得设 AETHER_CTP_TICK_TRACE=1。
        // 订阅/退订响应与登录/连接事件照常打印（低频、运维定位用）。
        if (TickTrace)
            RealOut.WriteLine($"[ctp-md] Tick {f.InstrumentID} last={f.LastPrice} bid={f.BidPrice1}x{f.BidVolume1} ask={f.AskPrice1}x{f.AskVolume1} vol={f.Volume} {f.UpdateTime}.{f.UpdateMillisec}");

        var tick = TickData.From(f);
        if (self.SuppressDuplicate(tick)) return;
        self.TickReceived?.Invoke(self, tick);
    }

    // —— 同值 tick 去重（2026-10-29 定案）——

    /// <summary>每合约最后一条**已投递**的 tick（去重比较基准）。</summary>
    private readonly Dictionary<string, TickData> _lastDelivered = new(StringComparer.Ordinal);
    private readonly object _dedupeLock = new();
    private int _dupesSuppressed;
    private int _dupesLastReported;
    private DateTime _dupesLastReportAt = DateTime.MinValue;

    /// <summary>
    /// 连续同值 tick 丢弃，返回 true 表示本条被丢弃。
    /// <para>
    /// 为什么在**回调入口**去重：2026-10-29 服务器双 bot 事故实测，客户端 10 秒内被投递 282 万次 tick 回调，
    /// 而这些回调的 (last,bid,ask,量) 只有一种组合——同一条行情被反复重投；同期前置日志只涨 13 B/s，
    /// 说明前置并没有往外推（前置按帧字节去重），重复发生在原生 SDK 封装层与回调之间。
    /// 这层重复会把下游全部放大：bot 日志每 200 笔打一行 ⇒ 1400 行/秒 ⇒ 单 bot 8.6 GB/天（撑爆磁盘），
    /// 桌面端每 tick 一次 <c>Dispatcher.UIThread.Post</c> ⇒ 主线程被淹 ⇒ 界面假死。
    /// </para>
    /// <para>
    /// <b>比较时不看 <c>UpdateTime</c>/<c>UpdateMillisec</c></b>：重投的特征就是"时间在变、行情内容没变"，
    /// 带上时间字段等于完全不起作用。只看对策略/展示有意义的字段（价、量、持仓、成交额）。
    /// 只挡**连续**同值（A→B→A 时 A 会正常通过），不会吞掉任何真实变化。
    /// </para>
    /// </summary>
    private bool SuppressDuplicate(TickData tick)
    {
        lock (_dedupeLock)
        {
            if (_lastDelivered.TryGetValue(tick.InstrumentId, out var prev) && SameContent(prev, tick))
            {
                _dupesSuppressed++;
                // 限频上报（最多 10s 一条，量小到可以忽略）：没有这行就无从判断风暴是否复发。
                var now = DateTime.UtcNow;
                if ((now - _dupesLastReportAt).TotalSeconds >= 10 && _dupesSuppressed > _dupesLastReported)
                {
                    RealOut.WriteLine($"[ctp-md] 同值 tick 去重 {_dupesSuppressed - _dupesLastReported} 条/10s（累计 {_dupesSuppressed}）"
                        + "——非零说明上游在重复投递，排查客户端会话重建/原生层重放");
                    _dupesLastReported = _dupesSuppressed;
                    _dupesLastReportAt = now;
                }
                return true;
            }
            _lastDelivered[tick.InstrumentId] = tick;
            return false;
        }
    }

    private static bool SameContent(in TickData a, in TickData b) =>
        a.LastPrice == b.LastPrice
        && a.Volume == b.Volume
        && a.OpenInterest == b.OpenInterest
        && a.Turnover == b.Turnover
        && a.BidPrice == b.BidPrice && a.BidVolume == b.BidVolume
        && a.AskPrice == b.AskPrice && a.AskVolume == b.AskVolume;

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        MdNativeMethods.CtpMd_Release(_handle);
        _handle = IntPtr.Zero;
        _selfHandle.Free();
    }

    private static string DecodeMsg(byte[]? b) =>
        b is null ? string.Empty :
        System.Text.Encoding.GetEncoding("GB18030").GetString(b).TrimEnd('\0').TrimEnd();

    private readonly struct RspCallback(RspInfoField info, object? field)
    {
        public readonly RspInfoField Info = info;
        public readonly object? Field = field;
    }
}
