#!/usr/bin/env python3
"""列出 PE 文件的导出函数名（一行一个）。

为什么不用 llvm-objdump：CI runner 不一定装了 llvm，而 PE 导出表解析只需要 stdlib。
自检口径：`python3 scripts/_pe_exports.py build/native/ctp_native.dll | grep -c '^Ctp'`
应当与 `llvm-objdump -p` 的结果一致（`scripts/check-native.sh` 里两者互为后备）。

只支持 PE32/PE32+，够用；解析失败时打印原因并以 2 退出（调用方据此跳过而不是当成「零导出」）。
"""
import struct
import sys


def exports(path: str) -> list[str]:
    with open(path, "rb") as fh:
        d = fh.read()

    if d[:2] != b"MZ":
        raise ValueError("不是 PE（缺 MZ 头）")
    e_lfanew = struct.unpack_from("<I", d, 0x3C)[0]
    if d[e_lfanew:e_lfanew + 4] != b"PE\0\0":
        raise ValueError("不是 PE（缺 PE 签名）")

    coff = e_lfanew + 4
    nsec = struct.unpack_from("<H", d, coff + 2)[0]
    opt_size = struct.unpack_from("<H", d, coff + 16)[0]
    opt = coff + 20
    magic = struct.unpack_from("<H", d, opt)[0]
    # 数据目录起点：PE32 的 OptionalHeader 固定 96 字节，PE32+ 是 112
    dd = opt + (112 if magic == 0x20B else 96)
    exp_rva, _exp_size = struct.unpack_from("<II", d, dd)
    if exp_rva == 0:
        return []

    sections = []
    sec = opt + opt_size
    for i in range(nsec):
        s = sec + i * 40
        vsize, vaddr, rsize, raddr = struct.unpack_from("<IIII", d, s + 8)
        sections.append((vaddr, max(vsize, rsize), raddr))

    def rva2off(rva: int) -> int:
        for vaddr, size, raddr in sections:
            if vaddr <= rva < vaddr + size:
                return raddr + (rva - vaddr)
        raise ValueError(f"RVA {rva:#x} 不在任何节内")

    off = rva2off(exp_rva)
    n_names = struct.unpack_from("<I", d, off + 24)[0]
    names_rva = struct.unpack_from("<I", d, off + 32)[0]
    noff = rva2off(names_rva)

    out = []
    for i in range(n_names):
        (nrva,) = struct.unpack_from("<I", d, noff + i * 4)
        o = rva2off(nrva)
        end = d.index(b"\0", o)
        out.append(d[o:end].decode("ascii", "replace"))
    return out


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print("用法: _pe_exports.py <file.dll>", file=sys.stderr)
        sys.exit(2)
    try:
        for name in exports(sys.argv[1]):
            print(name)
    except Exception as exc:                      # noqa: BLE001 - 故意全捕，只为给出可读原因
        print(f"无法解析 PE 导出表: {exc}", file=sys.stderr)
        sys.exit(2)
