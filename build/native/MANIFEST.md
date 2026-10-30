# `build/native/` 产物清单（MANIFEST）

本目录是 `Ctp.Net.Core` 随构建复制到输出目录的原生资产（`Ctp.Net.Core.csproj` 的 `Content` glob）。
**每个文件都必须在本表有一行**——`scripts/check-native.sh` 会核对此表与磁盘、以及导出符号与
`Ctp.Native/include/ctp_api.h` 的一致性（`DESIGN.md` §6 #8）。

更新产物后请同时更新：本表行、`sha256`、`导出数`、`末次变更 commit`。

## 1. 产物

| 文件 | 平台 / 架构 | 字节 | sha256（前 16） | 全量 sha256 | 导出数 | 末次变更 |
|---|---|---|---|---|---|---|
| `libctp_native.dylib` | macOS `x86_64`（minos 26.0） | 40296 | `b80400cb9681a800` | `b80400cb9681a800ec534edeba128d4635a3f435d4059b8e93a979d44776da5a` | 29 / 29 | `4fc7781` 2026-10-24 |
| `ctp_native.dll` | Windows `x86-64`（PE32+） | 25600 | `73426143fc8ae6ee` | `73426143fc8ae6eeca7a054a09a9db9e16b170b3c10354289570af594bb9c37f` | 29 / 29 | 2026-10-30 部署机重编（见 §2） |
| `ios/libctp_native.a` | iOS 静态库（ar） | 63456 | `459e4fb99be1b910` | `459e4fb99be1b9103273a90a5cef90fc22db9b457ec885e3928d3e34827dca81` | 未核对 | `aa3ef85` 2026-10-20 |
| `thosttraderapi_se.dll` | Windows `x86-64`（官方 CTP v6.7.2，构建 20230913） | 3419648 | `5906db0c1e76dd2e` | `5906db0c1e76dd2e0484413dad8ebc613b46c136d8931dc3b1c9226bd561c646` | 官方库 | `aa3ef85` 2026-10-20 |
| `thostmduserapi_se.dll` | Windows `x86-64`（官方 CTP v6.7.2） | 3035136 | `baa372754aaf8714` | `baa372754aaf8714486b61b3324e958660bb022526ec20780ab54ab87373a4e7` | 官方库 | `aa3ef85` 2026-10-20 |

macOS 侧的官方库不在本目录：它们是 `ctp-sdk/macos-universal/` 下的三个 framework
（`thosttraderapi_se` / `thostmduserapi_se` / `MacDataCollect`），由消费方（如 `ctp-net-front` 测试工程）
构建后复制到输出目录，见各处 csproj 的 `CopyCtpMacFrameworks` target。

## 2. 已闭合：Windows `ctp_native.dll` 曾落后源码 5 个导出（2026-10-30 重编）

**当时的问题**：`ctp_api.h` 声明 29 个 `Ctp*` 导出，macOS dylib 29 个齐全，Windows DLL 只有 24 个，
缺 `QryTrade` / `QryExchange` / `QryInvestor` / `QrySettlementInfo` / `QryInstrument`。
DLL 的末次提交是 `aa3ef85`（2026-10-20 仓库拆分），而 wrapper 源码在 10-24 的
`4fc7781`/`44a2221`/`88c55e1` 里才陆续加了这几个查询——**产物与源码不同步，此前无任何自动核对**。
影响面：Windows 侧调用这些方法会抛 `EntryPointNotFoundException`（已知调用点
`Aether.CtpConsole.QueryInstrumentsAsync`），服务端部署的也是同一份旧 DLL。

**怎么闭的**：在部署机（Win Server 2022，有 MSVC 14.44.35207 / MSVC 19.44 + Windows SDK 10.0.26100
+ VS 自带 CMake 3.30）上重跑 `Ctp.Native` 的 CMake 构建，把 `ctp_native.dll` 拉回本仓：

```
cmake -S Ctp.Native -B out -G "Visual Studio 17 2022" -A x64   # 外加 ctp-sdk/win-x64 与 Ctp.Native/
cmake --build out --config Release                            # 产物在 out 的 build/native/Release/
```

编译期会出现大量 `warning C4828`（官方 CTP 头是 GBK 中文注释，wrapper 强制 `/utf-8`）——是既有行为，
不影响产物；错误数为 0。产物 29 个 `Ctp*` 导出与 `ctp_api.h` 逐一对上，`scripts/native-known-missing.txt`
已清空。

**留下的事**：`dotnet` 消费方若是从 NuGet 走的旧包，需要重新打包；服务端 `Aether.CtpConsole` /
直连 bot 的 `publish` 目录里那份 DLL 也要替换（替换需停进程——DLL 被加载时会锁文件），本次未动线上。

**这一节要防的事没变**：产物必须与源码同步。区别是从此由 `scripts/check-native.sh` 把关
（CI 每次跑，导出/sha256/清单三方核对），而不是靠人记得。

## 3. macOS dylib 的两个约束（不是缺陷，是事实）

- **仅 `x86_64`**：Apple Silicon 上走 Rosetta（`DESIGN.md` §6 #7 的升级项）。
- **`minOS 26.0`**：`LC_BUILD_VERSION` 写的是 macOS 26，在更老的系统上 dyld 会拒绝加载。
  这是 Xcode 工具链默认部署目标导致的，若要下探到更老的 macOS，需构建时显式设 `-mmacosx-version-min`。
