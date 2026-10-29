# Ctp.Net.Core 设计说明

> 本文件写「怎么设计 / 为什么这么设计 / 还能往哪升级」。工作区边界与迁移决策以根 `MIGRATION.md` 为准。

CTP（综合交易平台，上海期货信息技术公司）官方 API 是 C++ 的（`CThostFtdcTraderApi` /
`CThostFtdcMdApi`，虚函数接口 + SPI 回调），且只有闭源二进制。`Ctp.Net.Core` 把它包装成
跨平台 .NET 10 库：

```
C++ SPI 回调 → C++ wrapper（flat C ABI） → C# P/Invoke → 面向对象 .NET API
```

---

## 1. 定位与边界

**本仓提供**：

- 交易接口：认证、登录、结算确认、资金/持仓/报单/合约查询、下单、撤单、报单与成交回报。
- 行情接口：登录、订阅/退订、深度行情（Tick）推送。
- 三平台（Windows / macOS / Linux，另含 iOS 构建分支）的 C++ wrapper 源码与构建脚本。
- CTP 6.7.2 官方闭源 SDK 二进制（`ctp-sdk/`，全平台入库）与预编译兜底产物（`build/native/`）。
- macOS/iOS 穿透式监管采集信息接入（`DataCollect` / `IOSDataCollect`）。

**本仓不提供**：

- 业务契约 DTO、`ICtpSession`、配置契约、交易日历 —— 在 `ctp-net-contracts`（`Ctp.Net.Contracts`）。
  Core 刻意不引用它，暴露的是 SDK 级类型；由 `ctp-net-client` 负责映射为契约 DTO
  （其 csproj 同时引用 contracts 与本仓，见 `ctp-net-client/src/Ctp.Net.Client/Ctp.Net.Client.csproj`）。
- 前置服务器、会话编排、重连策略、连接档案持久化 —— 在 `ctp-net-front`、`ctp-net-client`。
- 宿主打包、安装包、部署脚本 —— 由宿主仓承担。拆分前的 monorepo 根目录曾有
  `scripts/build-stockapp-*.{ps1,sh}`，**本仓没有 `scripts/` 目录**（`ls scripts` 不存在），
  旧文档中的打包流程只作为历史经验保留在 §4.5。
- NuGet 包 —— 当前未包化，消费者全部走 `ProjectReference`（见 §6 第 3 项）。

**依赖位置**：本仓是依赖图的叶子。`Directory.Packages.props` 为空占位，所有 csproj 中
**0 个 `PackageReference`、0 个 `ProjectReference`**。反过来，`MIGRATION.md` §5.1 记录的
消费者是 `ctp-net-client`、`ctp-net-capture`，以及 `ctp-net-front` 的 E2E 测试。

---

## 2. 项目组成

| 层/文件 | 职责 | 关键类型 | 出向依赖 |
|---|---|---|---|
| `src/Ctp.Net.Core/Trader/CtpTrader.cs` | 交易门面：生命周期、事件、async 登录/查询/下单/撤单 | `CtpTrader`、`QueryCollector`、`RspCallback` | `Native/NativeMethods.cs`、`Trader/Models.cs` |
| `src/Ctp.Net.Core/Trader/Models.cs` | 交易 DTO、byte 基枚举、异常 | `CtpConnectRequest`、`LoginResult`、`OrderRequest`、`Account`、`Position`、`Order`、`Trade`、`InstrumentInfo`、`CtpException`、`CtpBusinessException` | 仅 BCL |
| `src/Ctp.Net.Core/MarketData/CtpMarketData.cs` | 行情门面：登录、订阅、Tick 事件 | `CtpMarketData` | `Native/NativeMethods.cs`、`MarketData/Models.cs` |
| `src/Ctp.Net.Core/MarketData/Models.cs` | Tick 公共模型 | `TickData` | 仅 BCL |
| `src/Ctp.Net.Core/Native/NativeMethods.cs` | 25 个 `[DllImport]`、2 张回调函数指针表、委托签名 | `NativeMethods`、`MdNativeMethods`、`CtpTraderCallbacks`、`CtpMdCallbacks` | 动态库 `ctp_native` |
| `src/Ctp.Net.Core/Native/CtpStructs.cs` | CTP 6.7.2 结构体与 wrapper POD 的 C# 镜像 | `RspInfoField`、`RspUserLoginField`、`RspAuthenticateField`、`DepthMarketDataField`、`SpecificInstrumentField`、`CtpAccountInfo`、`CtpPositionInfo`、`CtpOrderInfo`、`CtpTradeInfo`、`CtpInstrumentInfo`、`CtpOrderInsertRequest` | 仅 `System.Runtime.InteropServices` |
| `Ctp.Native/include/ctp_api.h` | C ABI 契约：导出宏、25 个函数声明、稳定 POD、回调表 | `CTP_API`、`CtpTraderCallbacks`、`CtpMdCallbacks` | CTP 头文件（前向声明） |
| `Ctp.Native/TraderWrapper.cpp` | `CThostFtdcTraderSpi` 子类把回调转成 C 函数指针；从 CTP 大结构体提取字段填 POD；mac/iOS 登录采集 | `TraderSpi`、`struct CtpTrader` | `CThostFtdcTraderApi`、mac `DataCollect`、iOS `CollectionInfoIOS` |
| `Ctp.Native/MdWrapper.cpp` | 行情 SPI 桥接（Tick 直传 CTP 结构体指针） | `MdSpi`、`struct CtpMd` | `CThostFtdcMdApi` |
| `Ctp.Native/CollectionInfoIOS.mm` | iOS 采集信息：异步 block 转同步 | `CtpFetchCollectionInfoIOS` | UIKit / CoreLocation / CoreTelephony |
| `Ctp.Native/CMakeLists.txt` | 跨平台构建：按平台选 SDK、库类型、rpath、`/utf-8` | target `ctp_native` | `ctp-sdk/<平台>` |
| `ctp-sdk/{win-x64,win-x86,macos-universal,linux-x64,ios}` | CTP 官方 6.7.2 闭源库与头文件（入库） | `thost*.dll/.lib/.so/.framework` | 无 |
| `build/native/` | 预编译兜底产物 + CTP 运行时库 | `libctp_native.dylib`（x86_64）、`ctp_native.dll`（x64）、`ios/libctp_native.a`、`thost*.dll` | 由 wrapper 编译产出 |
| `src/Ctp.Net.Core/Ctp.Net.Core.csproj` | `net10.0`、nullable、Content glob 复制 native | — | `build/native/*.{dylib,so,dll}` |
| `Directory.Build.props` / `Directory.Packages.props` | 开 CPM、空包版本占位 | — | 无 |
| `.github/workflows/ci.yml` | Linux 上 `dotnet build -c Release` | — | .NET 10 SDK |

> 导出函数计数以 `ctp_api.h` 为准，2026-10-24 复核为 **25 个**（交易 17 + 行情 8），
> 与 `NativeMethods.cs` 的 25 个 `[DllImport]` 一一对应。旧文档写 24 个，与当前头文件不符；
> 差异点最可能是后来加入的 `CtpTrader_SetCollectionInfo`（旧文档正文已列出该函数，但计数未更新），
> 下次三平台构建时应以实际导出符号再次复核。

---

## 3. 关键设计决策

### 3.1 三层桥接，而不是直接 P/Invoke CTP

- **决策**：C++ wrapper 实现 `CThostFtdcTraderSpi`/`CThostFtdcMdSpi` 子类，接住回调后转成
  函数指针；对外只导出 `extern "C"` 的 flat C 函数。
- **理由**：CTP 没有 flat C 导出，只有 C++ 虚函数接口，P/Invoke 无法直接调用；C ABI 在三平台稳定。
- **代价**：多一层原生库和构建链；wrapper 要随 SDK 版本/平台维护；CTP 回调线程的约束
  （不可阻塞、结构体指针只在回调期间有效）会一直传导到 .NET 使用方。

### 3.2 跨 ABI 只传稳定小 POD

- **决策**：交易侧在 wrapper 里从 CTP 大结构体提取字段，填入自定义 POD
  （`CtpAccountInfo`/`CtpPositionInfo`/`CtpOrderInfo`/`CtpTradeInfo`/`CtpInstrumentInfo`/
  `CtpOrderInsertRequest`）；C# 只镜像这些小结构。行情 Tick 例外：直接传
  `CThostFtdcDepthMarketDataField*`，C# 完整镜像其布局。
- **理由**：CTP 大结构体字段多、跨版本可能变；小 POD 把 ABI 面收敛到实际使用的字段，
  C# 侧镜像成本低。
- **代价**：字段面要手工扩展；每加一个字段必须同时改 `ctp_api.h` 与 `CtpStructs.cs`，
  当前没有代码生成或自动化布局校验（§6 第 1 项）。行情侧仍承担 CTP 结构体布局风险。

### 3.3 导出宏、调用约定与字符编码

- **决策**：`CTP_API` 在 Windows 编译 wrapper 时展开为 `__declspec(dllexport)`、消费方为
  `dllimport`，macOS/Linux 为空；全部函数 `CallingConvention.Cdecl`；字符串 `CharSet.Ansi`；
  C 的 `bool` 在 C# 委托上标 `[MarshalAs(UnmanagedType.I1)]`；CTP 的 GBK 报文用 `GB18030` 解码。
- **理由**：Windows DLL 默认不导出 `extern "C"` 符号，不显式导出会报
  `Unable to find entry point named CtpTrader_Create`；C99 `_Bool` 是 1 字节，而 .NET
  `bool` 默认 4 字节；Windows 默认代码页是 GBK，macOS/Linux 默认不带 GB18030。
- **代价**：所有跨边界字符串走 ANSI/GBK 路径；错误消息解码分散在
  `CtpTrader.DecodeMsg`、`CtpMarketData.DecodeMsg`、`Order.DecodeGbk` 三处；
  `Encoding.RegisterProvider` 由两个类型的静态构造函数各自保证。

### 3.4 GCHandle 透传 ctx + 委托强引用

- **决策**：`CtpTrader_Create` 把 `GCHandle.ToIntPtr(this)` 作为 `ctx` 交给 C；回调时 C 原样
  回传，C# 用 `GCHandle.FromIntPtr(ctx).Target` 找回对象。所有回调委托保存为实例字段。
- **理由**：不需要全局句柄表，回调路径 O(1)；对象的生命周期由调用方 `Dispose` 控制。
- **代价**：委托一旦被 GC 回收，CTP 回调就会调用悬空函数指针并崩溃，因此必须用字段强引用；
  `Dispose` 必须 `Free()` 该 GCHandle；事件在 CTP 内部线程触发，订阅方阻塞会反压 SDK 派发。

### 3.5 平台差异收敛在 C++

- **决策**：macOS/iOS 登录用 4 参 `ReqUserLogin` + 穿透式采集信息，Windows/Linux 用 2 参；
  分支写在 `TraderWrapper.cpp`，C# 永远调用同一个 `CtpTrader_Login`。iOS 采集的异步 block
  由 `CollectionInfoIOS.mm` 同步等待。
- **理由**：上层 API 不因平台分叉；监管采集细节不泄漏到 .NET；C# 调用方无感。
- **代价**：wrapper 必须带平台宏编译；macOS 发布包必须随带 3 个 framework；采集库缺失时
  登录会失败（SimNow 仿真可不调用采集）。

### 3.6 SDK 入库 + 现场编译优先、预编译兜底

- **决策**：CTP 闭源 SDK 全平台入库（约 61MB）；有 CMake + 编译器就现场编 wrapper 覆盖
  `build/native/`，工具链不全则用入库的预编译产物兜底；产物由 csproj 的 `Content` glob
  复制到输出目录和 publish 目录。
- **理由**：闭源二进制没有更好的分发方式，clone 即用；现场编译保证产物与源码一致。
- **代价**：仓库体积大；预编译产物可能过期（修改 `Ctp.Native/*` 后需在三平台各重编一次并提交）；
  glob 不区分 RID，会把所有平台的二进制都复制进输出（§6 第 7 项）。

### 3.7 查询用 reqId 关联 + 静默期收尾

- **决策**：单响应请求（认证/登录/撤单/结算确认）走 `_pending` + `TaskCompletionSource`；
  多行查询走 `_queryPending` + `QueryCollector`，最后一次 `isLast=true` 后 150ms 无新行才完成。
  登录/撤单超时 15s，查询超时 30s。
- **理由**：本地前置的多行 tag 应答每帧都是 `isLast=true`，首个 `isLast` 立即完成会丢掉同一批
  后续行（2026-10-05 探针实证）；静默期同时兼容真实前置的 `false..true` 序列。
- **代价**：每个多行查询固定多 150ms 延迟；150ms 是经验值；`WaitAsync` 超时不会主动移除
  `_pending` 条目（§6 第 4 项）。

### 3.8 reqId 规避 SDK 转义字节与回卷冲突

- **决策**：交易侧 `NextRequestId` 跳过 `0xE0..0xEF` 与 `0xFE`，`>= 0xFD` 回卷到 0，
  并跳过仍在 `_pending`/`_queryPending` 里的 id；分配在锁内串行化。
- **理由**：reqId 落到 SDK 的转义字母表时会被序列化成 `[E0][值]`，而前置应答的 reqId 回显槽
  是定宽 1 字节，应答无法派发 → 查询挂死 → `rc=-2` → 4097 周期断连（2026-10 实测）。
- **代价**：可用 id 只有 234 个，做市商级高频会话会快速回卷；回卷时还要避开 in-flight，
  请求速率过高时余量变薄。行情侧未采用同一策略（§6 第 4 项）。

### 3.9 回调诊断写真实 stdout

- **决策**：两个门面各自持有 `RealOut`（`TextWriter.Synchronized(new StreamWriter(
  Console.OpenStandardOutput()))`），回调内直接写，不使用 `Console.WriteLine`。
- **理由**：Lean 实盘引擎会 `Console.SetOut(algorithm.Debug)`，回调线程的日志若进入有界的算法
  消息管道会阻塞，进而停掉 SDK 派发（2026-10-14 实测周期性断连）。
- **代价**：没有日志级别和可插拔 sink；每 tick 同步写一行；stdout 若被接到管道且对端不读，
  仍可能阻塞（§6 第 6 项）。

### 3.10 零依赖边界

- **决策**：Core 不引用任何 NuGet 包或兄弟仓项目；CPM 相关文件只做占位。
- **理由**：SDK wrapper 只需要 BCL；避免把业务契约、DI、SQLite 等拖进最底层库；消费者
  （`ctp-net-client`）自己完成 Core 类型到契约 DTO 的映射。
- **代价**：模型在两层重复（Core 的 `Order`/`Direction` 与 contracts 的
  `CtpOrder`/`CtpEnums`）；未包化前消费者只能用 `ProjectReference`，无法按版本消费。

### 3.11 平台构建参数

- **决策**：macOS 设 `BUILD_RPATH=${CTP_ROOT}`、`INSTALL_RPATH=@loader_path`；Linux 用
  `$ORIGIN` 并在 POST_BUILD 复制 `thost*.so`；Windows 用 `NMake Makefiles` + `CTP_NATIVE_EXPORTS`
  + `/utf-8`，并在 POST_BUILD 复制 `thost*.dll`。
- **理由**：`cmake --build` 只写绝对 `BUILD_RPATH`，发布包不可移植；Linux/Windows 需要把
  CTP 运行时库放到 wrapper 旁边。
- **代价**：macOS 打包还需要额外的 `install_name_tool` 归一化与 framework 随包（原打包脚本
  不在本仓，见 §4.5）；`/utf-8` 不能省，否则 MSVC 按 GBK 解析 UTF-8 中文注释会导致行号错位和
  大量 `C2059`。

---

## 4. 运行与验证

### 4.1 编译托管层

```bash
dotnet build ctp-net-core.slnx -c Release   # net10.0，无需任何 NuGet 包
```

托管编译不要求本机有 C++ 工具链；`native` 文件只是被 csproj 复制，不参与编译。

### 4.2 编译原生 wrapper

macOS / Linux：

```bash
cd Ctp.Native
cmake -G "Unix Makefiles" -DCMAKE_BUILD_TYPE=Release .
cmake --build .
# 产物输出到 ../build/native（CMakeLists 设置 CMAKE_LIBRARY_OUTPUT_DIRECTORY）
```

Windows（在 `vcvars64.bat` 初始化的 shell 中）：

```bat
cd Ctp.Native
cmake -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release .
cmake --build .
```

前置工具链：CMake >= 3.20、C++17 编译器（macOS 为 clang/Xcode CLT，Linux 为 gcc/g++，
Windows 为 MSVC）。CMakeLists 按平台选择 `ctp-sdk/{macos-universal,linux-x64,win-x64|win-x86,ios}`。

### 4.3 运行时库解析

- `[DllImport("ctp_native")]` 按平台解析为 `libctp_native.dylib` / `libctp_native.so` /
  `ctp_native.dll`。
- csproj 把 `build/native/*.{dylib,so,dll}` 平铺复制到输出和 publish 目录。
- wrapper 依赖的 CTP 运行时库必须与它同目录：Linux/Windows 的 `thost*.so`/`thost*.dll` 由
  CMake POST_BUILD 复制；**macOS framework 不在 csproj 的 glob 里**，发布包必须由宿主把
  `thosttraderapi_se`、`thostmduserapi_se`、`MacDataCollect` 三个 framework 拷进可执行文件旁
  （`@loader_path` 解析）。
- 传入 `CtpTrader(flowPath)` / `CtpMarketData(flowPath)` 的路径是 CTP 写流文件/日志的目录。

### 4.4 验证状态

| 项 | 方式 | 状态（截至 2026-10-24） |
|---|---|---|
| 托管编译 | `dotnet build -c Release` | CI 在 ubuntu-latest + .NET 10 上执行通过；本次未重复执行 |
| 原生符号 | `nm -gU` / `nm -D` / `dumpbin /exports` | 头文件与 C# 声明均为 25 个；2026-07 的历史验证记录为 24 个，差 1 个疑为后加的 `SetCollectionInfo`，需下次构建时以实际导出复核 |
| 动态加载 | `ctypes.CDLL` / 启动宿主 | macOS 2026-07 通过（dylib + 3 framework 的 dyld 解析）；Windows 2026-07 打包验证通过；Linux 未验证 |
| 端到端 | 下游 E2E、SimNow/本地前置 | 测试不在本仓；`MIGRATION.md` ⑤ 记录下游 E2E 只能本地跑，原因是 Linux `.so` 未进 `build/native` |
| 自动化测试 | `dotnet test` | **0 个测试项目**（slnx 仅 1 个项目） |

本次只做静态核对（读源码、`git status`、`git diff`、`file`、`grep`），**未执行 `dotnet build`/`dotnet test`**。

### 4.5 打包经验（历史，脚本不在本仓）

拆分前 monorepo 的 Stockapp 打包流程已经验证过 Windows/macOS（2026-07），Linux 待验证。
以下经验对宿主仓仍然成立：

- Windows：`vcvars64.bat` + `NMake Makefiles` 编出 `ctp_native.dll`；发布包需带
  `ctp_native.dll` + `thostmduserapi_se.dll` + `thosttraderapi_se.dll` 三件套。
- macOS：用 `install_name_tool` 删除源码树的绝对 rpath、改为 `@loader_path`；bundle 内随带
  3 个 framework；`xattr -cr` 去 Gatekeeper 隔离属性。framework 内的符号链接必须以真
  symlink 入库（`.gitattributes` 已把 `*.framework/**` 标为 binary，但 `git add` 时的
  `core.symlinks` 配置仍会影响链接形态）。
- Linux：`$ORIGIN` rpath，`thost*.so` 与 `libctp_native.so` 同目录。
- 「wrapper 编译失败只警告不中断」是宿主的打包策略，不是本库的行为：库本身在没有 native
  文件时会在 `CtpTrader_Create` 处 `DllNotFoundException`。

### 4.6 常见故障

- **`Unable to find entry point named CtpTrader_Create`**：Windows 上 wrapper 未导出符号。
  确认 `ctp_api.h` 的 `CTP_API` 宏生效、CMake 定义了 `CTP_NATIVE_EXPORTS`，且用的是新编的 dll。
- **MSVC 报大量 `C2059 语法错误:"if"` 且行号错位**：源码是 UTF-8，MSVC 按 GBK 解析。
  确认 MSVC 分支带 `/utf-8`。
- **找不到 CMake / MSVC**：安装 CMake、Xcode CLT 或 `build-essential`/`gcc-c++`；Windows
  可用 vswhere 或常见安装目录兜底。
- **macOS `unknown file type in '.../*.framework/<name>'`**：framework 内符号链接被 git 存成
  普通文本文件；在 mac/Linux 上重新提交为真 symlink。
- **macOS `dyld: Library not loaded: @rpath/thosttraderapi_se.framework/...`**：发布包没带
  framework；csproj 只复制 `*.dylib`，framework 必须由宿主随包。
- **CTP 官方 SDK 位置**：`ctp-sdk/<平台>`，已入库，clone 即有。

---

## 5. 升级记录（2026-10-24）

**结论：本仓本次没有代码或依赖变更。** 调查时（写入本文件之前）`git status --short` 与
`git diff` 输出均为空，HEAD 为 `eb05012`。原因是本仓是零 PackageReference、零
ProjectReference 的叶子库：没有可升级的包版本，也没有本轮触发升级的 API/CVE 变更。
本次唯一变动是补全本设计说明。

| 项 | 变更前 | 变更后 | 依据 / 验证 |
|---|---|---|---|
| 代码（`src/`、`Ctp.Native/`） | HEAD `eb05012` | 同左，未改 | `git status --short` 与 `git diff` 均为空；未执行 build/test |
| 依赖（NuGet / 兄弟仓） | 0 个 PackageReference、0 个 ProjectReference | 同左 | `Directory.Packages.props` 为 `<Project />` 空占位；csproj 无引用；`obj/project.assets.json` 无依赖项 |
| 原生产物（`build/native/`） | `libctp_native.dylib`(x86_64)、`ctp_native.dll`(x64)、`ios/libctp_native.a`、`thost*.dll`；无 `.so` | 同左，未重编 | `file` / `ls -lh` 核对；本次不改二进制 |
| 文档（`DESIGN.md`） | 缺少升级记录与待升级项；导出函数写作 24 个；§2.3 称 Windows dll 待入库 | 补齐六节结构；按头文件改为 25 个；注明 Windows dll 已入库、Linux so 仍缺 | `grep -c '^CTP_API' ctp_api.h` = 25；`build/native/ctp_native.dll` 存在 |

### 5.1 撤单请求补 `InstrumentID`（2026-10-24，真前置实测驱动）

**问题**：在 SimNow 真前置上撤一张挂着的单，前置稳定回 `ErrorID=25「找不到相应报单」`——无论用下单那条会话还是新会话。
真因不在会话、也不在 OrderRef：`CtpTrader_OrderAction` 的 `CThostFtdcInputOrderActionField` **从来没设 `InstrumentID`**。
大商所等交易所按「合约 + 报单引用」索引委托，缺合约 ⇒ 找不到。

**改动**：`ctp_api.h` / `TraderWrapper.cpp`（新增 `const char* instrumentId` 参数并写入 `f.InstrumentID`）、
`NativeMethods` P/Invoke、`CtpTrader.OrderActionAsync(..., string? instrumentId = null, ct)`。下游 `CtpOrderRef`
增可选 `InstrumentId`，`CtpSessionService` / `CtpBrokerage` 全链透传（撤单点用 `child.Request.InstrumentId`）。

| 项 | 变更前 | 变更后 | 依据 / 验证 |
|---|---|---|---|
| 撤单请求 | 只有 Broker/Investor/FrontID/SessionID/OrderRef/ExchangeID | 追加 `InstrumentID`（必填语义） | SimNow 实测：补上前置立刻接受，两张遗留挂单 m2701@3400、m2703@3300 撤掉、账本活单归零 |
| 原生产物 | `build/native/libctp_native.dylib`(x86_64) | 同架构重编（clang++ 直编；cmake 未安装），`-Wl,-rpath,@loader_path` + ad-hoc 签名 | `nm -gU` 有 `CtpTrader_OrderAction`；`otool -l` 有 LC_RPATH；下游 `dotnet build` 全绿、探针跑通 |
| 兼容性 | — | 新参数是可选尾参（`null` 默认）⇒ 现有调用点无需改动 | 6 个受影响项目 `dotnet build` 零错误；`Ctp.Net.Front.Tests` 28 例（撤单/适配器）3s 通过 |

### 5.2 导出查成交 `QueryTradeAsync`（2026-10-24）

标准客户端的「成交明细」来源。改动：`ctp_api.h`（`CtpTradeInfo` 尾部追加 `ExchangeID`/`TradeDate`、
`CtpTraderCallbacks` 尾部追加 `OnRspQryTrade`、新增 `CtpTrader_QryTrade`）、`TraderWrapper.cpp`（`OnRspQryTrade`
覆写 + 请求装配 + 成交推送同步补两个字段）、`CtpStructs.cs`/`NativeMethods.cs` 镜像、`CtpTrader.QueryTradeAsync`、
`Trade` 模型补 `ExchangeId`/`TradeDate`。

| 项 | 说明 |
|---|---|
| 验证 | 真前置实抓：请求 op `E104E28002`、应答 op `E104E28003`（空表 17B），客户端 0 行立即返回 |
| 同时修掉 | `OnQueryItem` 接受空记录（`nullptr`）→ 不入列但仍参与 `isLast` 收尾（此前空结果会被当成一条空行，或直接卡死） |
| 反向发现 | 空记录回调**不能对所有查询都转发**：`QryOrder`/`QryInstrument` 转发后 SDK 给出无效记录指针 → C# 段错误（6.6GB dump）。这两个回调维持「吞掉」，见 ⑤ DESIGN |

**已知残留**：真前置撤单成功**只推 `OnRtnOrder` '5'，不派发 `OnRspOrderAction`**，所以 `OrderActionAsync` 侧表现为
15s 超时异常（单其实撤掉了）。本地前置会回 ㉒ ⇒ 本地测不出这段差异。若要消掉这个误报，需要在真前置上抓一帧
撤单往返确认应答形态（或改为「收到 '5' 终态即视为成功」的容忍语义）。

---

### 5.4 移除「同值 tick 去重」（2026-10-29 复核后回退）

**问题**：2026-10-29 为压 tick 风暴在回调入口加过一层「连续同值丢弃」（`SuppressDuplicate`，比较 8 个
量价字段、**故意不看** `UpdateTime`）。复核发现这层判据是错的：

- 逐字节相同的帧**已由前置** `CtpFrontMdSession.OnTick` 挡掉，实测客户端 0 次收到同帧；
- 交易所对每次簿变更都发 tick，做市商在第 2 档以下撤改时，上报字段（最新价/量/持仓/买一卖一/秒级时间）
  里只有**时间在走**——这些是**两个不同的事件**，被本层当重复丢掉：实测 382 条原始回调误伤 11 条（2.9%）；
- 它自己那行告警「非零说明上游在重复投递」因此恒为误报，还会把界面时间戳冻在最后一帧内容变化处，
  制造与「数据源挂了」相同的症状；重绘合并本来属于渲染层（桌面端 `MarketWatchViewModel` 每秒一拍）。

**改法**：删掉去重调用与 `SuppressDuplicate`/`SameContent`/`_lastDelivered`（55 行），回调逐条
`TickReceived?.Invoke`；`AETHER_CTP_TICK_TRACE` 逐笔开关保留。实测（桌面端连远程前置）110 条原始回调
0 重复、5 条「内容同时间不同」的帧全部投递；端到端计数：前置推 11 帧 / 客户端交付 12 次 = 11 + 订阅快照，
零丢失。风暴检测改用**速率**（每秒回调数），不再用内容相等。

细节与判据推导见 `doc/md-tick-dedup.md`；上游投递契约见 `../ctp-net-front/doc/md-tick-delivery.md`。

## 6. 待升级项

| # | 优先级 | 项目 | 现状证据 | 建议 | 代价 / 风险 |
|---|---|---|---|---|---|
| 1 | P1 | 无测试项目，SDK wrapper 不可测、ABI 无自动校验 | `ctp-net-core.slnx` 只有 1 个项目，仓内无 `tests/`；`MIGRATION.md` ② 记录「未见 tests 项目」；`NativeMethods`/`MdNativeMethods` 是 `internal static class` 的 `extern` 方法，被 `CtpTrader` 直接调用（`CtpTrader.cs:113/224`），没有注入点；POD 靠手写镜像，`CtpOrderInfo` 甚至没有显式 `[StructLayout]`（`CtpStructs.cs:178`）；CI 只有 build（`ci.yml:36-38`） | 先给纯逻辑建 `tests/Ctp.Net.Core.Tests`：reqId 分配器（字母表跳过、回卷、in-flight 避让）、静默期收集器、GBK 解码、`From()` 映射；再引入 internal 接缝（`ICtpNativeApi`）或 stub `ctp_native`（假实现 25 个导出）测门面；加 ABI 钉子（`Marshal.SizeOf`/`OffsetOf` 对照 C 侧常量）；CI 加 `dotnet test`，native 缺失时跳过 P/Invoke 组 | 1-2 天；接缝重构触及热点路径；stub 必须与 `ctp_api.h` 同步维护；Linux CI 需先编 native 或只跑纯托管组 |
| 2 | P1 | Linux 兜底产物缺失，三平台可用性不完整 | `build/native/` 无 `libctp_native.so`（`ls` 仅有 dylib/dll/a）；`ctp-sdk/linux-x64/` 已有 `thost*.so`；`MIGRATION.md` ⑤ 明确下游 E2E 留本地的原因就是「Linux .so 未进 build/native」；旧文档 §3.3 仍是「待验证」 | 在 Linux 上跑一次 `Ctp.Native` CMake 构建并提交 `libctp_native.so`；CI 增加 native job：编译 + `nm -D` 数出 25 个 `Ctp*` 符号 + `ctypes.CDLL` dlopen 冒烟；顺带验证 `$ORIGIN` 能解析 `thost*.so` | 二进制入库增加体积；glibc 版本决定可移植下限；需要长期维护一台 Linux 构建环境 |
| 3 | P1 | 未包化，原生资产交付责任仍在消费方 | 本仓 csproj 无 `PackageId`/`IsPackable`/`<None Pack>` 等元数据；消费者全部是 `ProjectReference`（如 `ctp-net-client.csproj` 直引本仓 `src/Ctp.Net.Core/Ctp.Net.Core.csproj`）；`nuget.config` 注释写明 GitHub 源是「跨仓 ProjectReference 切 PackageReference 后使用」；`MIGRATION.md` §9.2 要求 Core 用包/构建资产交付完整原生依赖，消费方无须知道 SDK 路径 | 按 RID 打包：`runtimes/<rid>/native/` 放 wrapper + `thost*`，macOS framework 用 `buildTransitive` target 或随包复制；按 §9.2 建独立临时消费项目，从产物验证启动与交易/行情能力，再切消费者 | 跨平台原生打包复杂（RID、framework symlink、macOS 签名）；版本组合与发布流程要建立；短期会与 ProjectReference 并存 |
| 4 | P2 | 会话生命周期与请求表清理不完整，Md 防护弱于 Trader | `CtpTrader.Dispose` 只调 `Release` + `Free`，不 `Join`（`CtpTrader.cs:520-527`），而 `TraderWrapper.cpp:221` 注释要求「先 Join 退出后再 Release」；`RegisterPending` 的条目只由 `TryCompletePending` 移除，`WaitAsync` 超时后仍留在 `_pending`（`CtpTrader.cs:320-332`）；`CtpMarketData.NextRequestId` 只是 `Interlocked.Increment`（`CtpMarketData.cs:77`），不跳过 SDK 转义字节，也没有查询表/超时清理 | 提供 `DisposeAsync`/`Join(timeout)` 并规定「不得在回调线程调用」；超时路径主动移除 pending；把 reqId 分配器抽成共用组件给 Md 复用；补一个生命周期状态机 | 行为变更，Join 可能阻塞；若在回调线程误用会死锁；需要下游宿主配合调整释放顺序 |
| 5 | P2 | 协议/交易语义常量硬编码，无法按柜台调整 | `TraderWrapper.cpp:337-349` 固定 `UserID = InvestorID`、`CombHedgeFlag='1'`、`VolumeCondition='1'`、`MinVolume=1`、`ContingentCondition='1'`、`ForceCloseReason='0'`；`CtpTrader.cs:65` 固定静默期 150ms，登录/撤单 15s、查询 30s（`CtpTrader.cs:164/172/226`）；采集缓冲固定 `char[273]` | 抽 `CtpOptions`（超时、静默期、默认投机标志、成交量条件、采集缓冲）并保留当前默认值；在 XML 注释标明对应 CTP 字段语义；用单测锁默认值 | 公共 API 面扩张；不同柜台（SimNow/生产/本地前置）行为差异需实测后才能改默认；抽象过度会让调用点变复杂 |
| 6 | P2 | 日志不可插拔，每 tick 同步写 stdout | `CtpTrader.cs:22` 与 `CtpMarketData.cs:13` 的静态 `RealOut`；`OnRtnDepthMarketDataCb` 每收到一帧就 `RealOut.WriteLine`（`CtpMarketData.cs:178`）；无 `ILogger`、无级别、无采样 | 注入可选日志回调（默认仍走 `RealOut`），Tick 日志默认关闭或按采样率输出；保留当前排查开关 | 改变现场诊断习惯；回调内异步日志要防队列积压；API 面增加 |
| 7 | P2 | macOS 仅 x86_64 产物，且 csproj 不按 RID 过滤 | `file build/native/libctp_native.dylib` = `Mach-O 64-bit ... x86_64`；`bin/Debug/net10.0/` 同时出现 `libctp_native.dylib`、`ctp_native.dll`、`thost*.dll`，说明所有平台二进制被平铺复制；`Ctp.Net.Core.csproj:15` 的 glob 不含 RID 条件 | 在 Apple Silicon 上产 arm64 或 universal 并验证；包化时改为 `runtimes/<rid>/native` + RID 条件复制；publish 后按 RID 校验文件集，避免把 PE 文件塞进 macOS 包 | 需要双架构构建矩阵；包体积变化；Rosetta 与原生 arm64 的回归都要跑 |
| 8 | P3 | 文档与仓库现状漂移 | 旧文档称 24 个导出，实际 25 个（`grep -c '^CTP_API'` = 25）；旧 §2.3 称 Windows `ctp_native.dll` 待入库，实际已在 `build/native/`；旧 §3 描述的 `scripts/build-stockapp-*` 在本仓不存在（无 `scripts/`）；`CtpStructs.cs:6` 注释「仅镜像 Phase 1 所需」已过时 | 以本文件为准；加一个核对脚本（导出符号数、`build/native` 清单），CI 中低于预期数量即失败；给 `build/native/` 增加 MANIFEST（平台/架构/构建日期/源码 commit），减少「产物与源码不一致」的排查成本 | 成本低；MANIFEST 需要人工更新，若忘记更新会再次漂移 |

### 5.3 逐笔 tick 日志默认关（2026-10-26 日志降噪）

**问题**：`CtpMarketData` 逐笔打印 `[ctp-md] Tick …`——调试期有用；真实部署后 bot 直连远程前置
（服务器旧构建无同值快照去重），行情活跃 ~2900 笔/s，两条逐笔日志（② 这里 + ⑧ console 的 `[tick]`）
把 bot 日志刷成每天数百 MB（实测单 bot 半天 300MB+）。

**改法**：逐笔行门控到 `CtpMarketData.TickTrace`——env `AETHER_CTP_TICK_TRACE=1`（或 "true"）才打印，
默认关。⑧ console 的 `[tick]` 行接同一开关（REPL `tick on` 运行期开不受影响）。订阅/退订响应、
登录/连接事件照常打印（低频、运维定位面）。行为零改动（纯日志）。
