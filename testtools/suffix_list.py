#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 HashAlgorithms.cs 导出全部算法的首选后缀，生成过滤器字符串。

为什么要脚本生成：保存对话框的 Filter 必须覆盖每一个算法的首选后缀，
否则 SaveFileDialog 的 AddExtension 会把不在列表里的后缀追加成首个后缀
（.sha3-512 → .sha3-512.sha256），已经踩过一次。

用法:
    python suffix_list.py            # 打印 ; 分隔的文件名模式
    python suffix_list.py --check    # 检查 Localization.cs 的过滤器是否覆盖全部后缀
"""
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
# testtools 就在工程根下，产品源码在「工程根\HashChecker_WPF\HashChecker」
# （解决方案目录外面还套了一层同名目录），原来少算了这一层，默认路径必然找不到文件。
PRODUCT = HERE.parent / "HashChecker_WPF" / "HashChecker"
SRC = PRODUCT / "Services" / "HashAlgorithms.cs"
LOC = PRODUCT / "Localization.cs"


def collect_extensions():
    """抓每个 Simple(...) 调用里 new[] { "第一项" } 的首个后缀。"""
    text = SRC.read_text(encoding="utf-8")
    pattern = re.compile(r'new\[\]\s*\{\s*"([^"]+)"', re.S)
    out = []
    for match in pattern.finditer(text):
        ext = match.group(1)
        if ext.startswith(".") and ext not in out:
            out.append(ext)
    return out


def main():
    exts = collect_extensions()
    if not exts:
        print("没有解析到后缀")
        return 2

    patterns = ";".join("*" + e for e in exts)

    if "--check" in sys.argv:
        loc = LOC.read_text(encoding="utf-8")
        problems = []
        for ext in exts:
            # 过滤器里必须出现这个后缀（任一语言段落即可代表）
            if ext not in loc:
                problems.append(ext)

        if problems:
            print("过滤器缺失后缀 %d 个: %s" % (len(problems), ", ".join(problems)))
            return 1

        print("过滤器覆盖全部 %d 个后缀 ✓" % len(exts))
        return 0

    print(patterns)
    return 0


if __name__ == "__main__":
    sys.exit(main())
