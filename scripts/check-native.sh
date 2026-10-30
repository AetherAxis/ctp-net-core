#!/usr/bin/env bash
# 原生产物一致性核对（DESIGN.md §6 #8）。
#
# 三件事：
#   1) 导出符号：ctp_api.h 声明的每个 Ctp* 导出，必须在**每个**原生产物里真实存在；
#   2) C# 与 C 不脱节：NativeMethods.cs 里 P/Invoke 的每个入口，header 里必须声明过；
#   3) MANIFEST 不漂移：build/native/MANIFEST.md 里写的全量 sha256 必须与磁盘一致。
#
# 退出码：0 = 全绿；1 = 有偏差（CI 直接失败）。
# 依赖：nm（macOS/Linux 自带）；解析 PE 需要 llvm-objdump（Xcode 自带 / CI 安装 llvm）——
#       找不到就跳过 .dll 那一项并**明确打印跳过**，不静默通过。
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
HDR="$ROOT/Ctp.Native/include/ctp_api.h"
CSSRC="$ROOT/src/Ctp.Net.Core/Native/NativeMethods.cs"
NATIVE="$ROOT/build/native"
MANIFEST="$NATIVE/MANIFEST.md"

fail=0
note() { printf '%s\n' "$*"; }
bad()  { printf '✗ %s\n' "$*"; fail=1; }
ok()   { printf '✓ %s\n' "$*"; }

# —— 1. 声明集合 ——
[ -f "$HDR" ] || { bad "找不到 $HDR"; exit 1; }
declared="$(grep '^CTP_API' "$HDR" | grep -oE 'Ctp(Trader|Md)_[A-Za-z]+' | sort -u)"
n_declared="$(printf '%s\n' "$declared" | wc -l | tr -d ' ')"
ok "header 声明 $n_declared 个导出"

# —— 1b. 已登记的缺口（只告警，不失败） ——
KNOWN_FILE="$ROOT/scripts/native-known-missing.txt"
KNOWN=""
if [ -f "$KNOWN_FILE" ]; then
  KNOWN="$(grep -vE '^[[:space:]]*(#|$)' "$KNOWN_FILE" | sort -u)"
fi

# —— 2. C# 侧入口必须是 header 的子集 ——
if [ -f "$CSSRC" ]; then
  # 只取**真实声明行**（`static extern`），不扫注释——注释里提到未来要加的入口名不算数
  cs="$(grep 'static extern' "$CSSRC" | grep -oE 'Ctp(Trader|Md)_[A-Za-z]+' | sort -u)"
  extra="$(comm -23 <(printf '%s\n' "$cs") <(printf '%s\n' "$declared"))"
  if [ -n "$extra" ]; then
    bad "NativeMethods.cs 用了 header 未声明的入口："; printf '%s\n' "$extra" | sed 's/^/    /'
  else
    ok "NativeMethods.cs 的入口全部在 header 内（$(printf '%s\n' "$cs" | wc -l | tr -d ' ') 个）"
  fi
fi

# —— 3. 每个产物的导出集合 ——
check_artifact() {
  local file="$1" exports
  [ -f "$file" ] || return 0
  case "$file" in
    *.dylib|*.so)
      exports="$(nm -gU "$file" 2>/dev/null | grep -oE '_Ctp(Trader|Md)_[A-Za-z]+' | sed 's/^_//' | sort -u)"
      ;;
    *.dll)
      # 首选 llvm-objdump（Xcode / 装了 llvm 的机器），否则退回 stdlib 的 PE 解析器
      # （scripts/_pe_exports.py，CI 上不需要装 llvm）。
      local od
      od="$(command -v llvm-objdump || true)"
      [ -n "$od" ] || od="/Applications/Xcode.app/Contents/Developer/Toolchains/XcodeDefault.xctoolchain/usr/bin/llvm-objdump"
      if [ -x "$od" ]; then
        exports="$("$od" -p "$file" 2>/dev/null | grep -oE 'Ctp(Trader|Md)_[A-Za-z]+$' | sort -u)"
      elif command -v python3 >/dev/null 2>&1; then
        exports="$(python3 "$ROOT/scripts/_pe_exports.py" "$file" 2>/dev/null \
                   | grep -oE '^Ctp(Trader|Md)_[A-Za-z]+$' | sort -u)"
      else
        note "… 跳过 $(basename "$file")：既没有 llvm-objdump 也没有 python3"
        return 0
      fi
      ;;
    *)
      return 0
      ;;
  esac

  local missing known="" unknown
  missing="$(comm -23 <(printf '%s\n' "$declared") <(printf '%s\n' "$exports"))"
  # 已登记的历史缺口（scripts/native-known-missing.txt）只告警，其余缺口一律失败——
  # 这样 CI 仍然能抓住**新的**漂移，而不会被本机修不了的旧账永久卡红。
  if [ -n "$KNOWN" ]; then
    known="$(comm -12 <(printf '%s\n' "$missing") <(printf '%s\n' "$KNOWN"))"
    unknown="$(comm -23 <(printf '%s\n' "$missing") <(printf '%s\n' "$KNOWN"))"
  else
    unknown="$missing"
  fi
  if [ -n "$unknown" ]; then
    bad "$(basename "$file") 缺 $(printf '%s\n' "$unknown" | wc -l | tr -d ' ') 个导出（共 $(printf '%s\n' "$exports" | wc -l | tr -d ' ')/$n_declared）："
    printf '%s\n' "$unknown" | sed 's/^/    /'
  fi
  if [ -n "$known" ]; then
    note "⚠ $(basename "$file") 缺 $(printf '%s\n' "$known" | wc -l | tr -d ' ') 个已登记缺口（见 scripts/native-known-missing.txt，需在对应平台重编）："
    printf '%s\n' "$known" | sed 's/^/    /'
  fi
  if [ -z "$missing" ]; then
    ok "$(basename "$file") 导出齐全（$n_declared/$n_declared）"
  fi
}

# 只核对**本仓的 wrapper**（ctp_native / libctp_native）；同目录的官方 SDK
# （thosttraderapi_se.dll / thostmduserapi_se.dll）是上游产物，导出的是 C++ 修饰名，版本另由
# MANIFEST 记（v6.7.2）。混进来核对只会制造假失败。
for f in "$NATIVE"/ctp_native.* "$NATIVE"/libctp_native.*; do
  [ -e "$f" ] || continue
  check_artifact "$f"
done

# —— 4. MANIFEST 的 sha256 与实际文件 ——
# 只核对 manifest 里出现、且文件存在的全量 hash；缺失/新增文件都会被抓到。
if [ -f "$MANIFEST" ]; then
  if ! command -v python3 >/dev/null 2>&1; then
    note "… 没有 python3，跳过 MANIFEST sha256 核对"
  else
  python3 - "$MANIFEST" "$NATIVE" <<'PY' || fail=1
import hashlib, re, sys, pathlib
manifest, native = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
text = manifest.read_text(encoding="utf-8")
# 表行形如：| `libctp_native.dylib` | ... | `全量hash` | ...
pat = re.compile(r"^\|\s*`([^`]+)`\s*\|.*?`([0-9a-f]{64})`", re.M)
bad = False
for name, want in pat.findall(text):
    f = native / name
    if not f.is_file():
        print(f"✗ MANIFEST 记了 {name}，但文件不存在"); bad = True; continue
    got = hashlib.sha256(f.read_bytes()).hexdigest()
    if got != want:
        print(f"✗ MANIFEST sha256 与磁盘不符：{name}（{want[:16]}… / {got[:16]}…）"); bad = True
    else:
        print(f"✓ {name} sha256 一致")
sys.exit(1 if bad else 0)
PY
  fi
else
  note "… 未找到 MANIFEST.md，跳过 sha256 核对"
fi

if [ "$fail" -ne 0 ]; then
  printf '\n有偏差。见 DESIGN.md §6 #8 与 build/native/MANIFEST.md。\n'
  exit 1
fi
printf '\n全部一致。\n'
