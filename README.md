# Ctp.Net.Core

CTP（综合交易平台）6.7.2 跨平台 .NET 绑定 —— 三层架构：**C# 业务层 → C# P/Invoke 互操作层 → C++ wrapper（flat C ABI）→ CTP 官方闭源 SDK**。

## 快速开始

```csharp
// 交易接口
using Ctp.Net.Core.Trader;

using var trader = new CtpTrader("./runtime");
var login = await trader.LoginAsync(new CtpConnectRequest
{
    FrontAddress = "tcp://180.168.146.187:10130",
    BrokerId = "9999", UserId = "username", Password = "password",
    AppId = "stockapp", AuthCode = "xxx",
});
var account = await trader.QueryTradingAccountAsync("9999", "username");

// 行情接口
using Ctp.Net.Core.MarketData;

using var md = new CtpMarketData("./runtime");
md.TickReceived += (_, tick) => Console.WriteLine(tick);
await md.LoginAsync("tcp://180.168.146.187:10131", "9999", "username", "password");
await md.SubscribeMarketDataAsync(["au2506", "cu2506"]);
```

## 构建原生 wrapper（可选）

现场编译优先，无 CMake/clang/g++ 时用 `build/native/` 预编译产物兜底：

```bash
cd Ctp.Native
cmake -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=Release .
cmake --build .
cp libctp_native.dylib ../build/native/
```

## 依赖

- **C# 层**：零 PackageReference，纯 .NET 10 + `System.Runtime.InteropServices`
- **原生层**：`ctp_native`（本仓 C++ wrapper）+ CTP 官方 6.7.2 闭源 SDK（全平台已入库于 `ctp-sdk/`）

## 来历

本仓从 Aether monorepo 的 `externals/Ctp.Net.Core/` 拆分而来（2026-10-19，AetherAxis 独立仓迁移第 ② 号）。monorepo 副本冻结为最后同步点，后续修复/迭代仅发生在本仓。
