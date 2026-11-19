#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""静态检查：每个探针都必须在 Main 里**真的调用**配置改道函数。

为什么要有这个检查
------------------
`cs\` 下每个探针都带着一个 `RedirectSettingsToTemp()` 函数，把产品配置目录
改道到 `%TEMP%`。少了这一步，探针用 `Assembly.LoadFrom` 载入产品后，
产品会按「入口程序集名」决定配置目录名 —— 那个名字是**探针自己** ——
于是每跑一个探针就在 `%LOCALAPPDATA%` 下留一个目录。

**真实踩过的坑**：`perfprobe` 一直带着这个函数、注释也写得清清楚楚，
但 `Main` 里忘了写那一行调用。光看「文件里有没有这个函数」完全查不出来，
只有看「有没有被调用」才行 ——
所以本检查判的是**调用点**，不是函数定义。

它与 `settings-isolation` 的分工：
  * 本检查  —— 静态、每次必跑、抓的是「源头没改道」；
  * settings-isolation —— 运行期、直接看 `%LOCALAPPDATA%` 有没有多出目录，
    抓的是「改道了但仍然漏了」（例如漏改某个旁路）。

两个都留：静态抓源头，运行期兜底。

用法:
    python redirectcheck.py [cs 目录]
    python redirectcheck.py            # 默认 <testtools>\\cs

退出码: 0 = 全部通过, 1 = 有失败, 2 = 环境不满足
"""
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_CS_DIR = HERE / "cs"

# 改道函数的两种常见命名。产品的变量名不同（C# 探针 vs Python 套件），
# 这里只关心「有没有在 Main 里被调用」。
REDIRECT_NAMES = ("RedirectSettingsToTemp", "RedirectConfigToTemp")

# 判定「调用点」的正则：函数名后面紧跟 (，允许中间有空白。
CALL_RE = re.compile(r"\b(%s)\s*\(" % "|".join(REDIRECT_NAMES))


def find_probes(cs_dir):
    """找出所有含 <名字>.cs 的子目录（与 build_all.py 的口径一致）。"""
    if not cs_dir.is_dir():
        return []
    out = []
    for d in sorted(cs_dir.iterdir()):
        if not d.is_dir():
            continue
        src = d / (d.name + ".cs")
        if src.exists():
            out.append((d.name, src))
    return out


def check_probe(name, src):
    """检查一个探针。返回 (通过, 说明)。"""
    try:
        text = src.read_text(encoding="utf-8", errors="replace")
    except OSError as exc:
        return False, "读不到源码: %s" % exc

    if not any(n in text for n in REDIRECT_NAMES):
        # 没有改道函数：这个探针不碰产品配置，属于合法情况。
        return True, "不需要改道（不加载产品配置）"

    # 逐行去掉「函数定义」那一行，剩下出现的函数名就是调用点。
    # 定义形如 `static void RedirectSettingsToTemp()`；
    # 只看「函数名后面紧跟 (」还不够 —— 定义本身也长这样。
    calls = 0
    for line in text.splitlines():
        stripped = line.strip()
        if not any(n in stripped for n in REDIRECT_NAMES):
            continue
        # 定义行：函数名之前有返回类型 / static 等修饰，且以 { 或 ) 收尾
        if re.search(r"\b(void|bool|int|string)\s+(%s)\s*\(" % "|".join(REDIRECT_NAMES), stripped):
            continue
        # 调用行：函数名前面通常只有缩进，或以 `Redirect...();` 结束
        if CALL_RE.search(line):
            calls += 1

    if calls == 0:
        return False, ("定义了 %s 却没有在 Main 里调用 —— "
                       "每跑一次就在 %%LOCALAPPDATA%% 下留一个目录"
                       % "/".join(n for n in REDIRECT_NAMES if n in text))

    return True, "调用点 %d 处" % calls


def main():
    cs_dir = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else DEFAULT_CS_DIR

    print("=== 配置改道调用点检查 ===")
    print("探针目录: %s" % cs_dir)
    print("")

    if not cs_dir.is_dir():
        print("!! 目录不存在: %s" % cs_dir)
        return 2

    probes = find_probes(cs_dir)
    if not probes:
        print("没有找到探针")
        return 2

    failures = []
    for name, src in probes:
        ok, detail = check_probe(name, src)
        print("  %s %-16s %s" % ("OK  " if ok else "!!  ", name, detail))
        if not ok:
            failures.append(name)

    print("")
    print("检查 %d 个探针，失败 %d 个" % (len(probes), len(failures)))
    if failures:
        print("失败: %s" % ", ".join(failures))
        return 1
    print("全部符合预期 ✓")
    return 0


if __name__ == "__main__":
    sys.exit(main())
