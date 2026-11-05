#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""用算法目录里的全部首选后缀重建保存/打开对话框的文件过滤器。

背景：SaveFileDialog 的 AddExtension 在「文件名后缀不在 Filter 列表里」时会
自动追加首个过滤器后缀。原来过滤器只列了 .sha256/.md5/.sha1/.sha512/.sha3-256，
于是选 SHA3-512 存出来的名字变成 x.bin.sha3-512.sha256——多了一层，
重新传回软件时又被按 .sha256 解析，校验就错了。

修法是让过滤器覆盖每一个算法的首选后缀（由 suffix_list.py 从源码导出）。

用法:
    python fix_filters.py            # 重写三语的 MsgSaveListFilter / MsgFileFilter
    python fix_filters.py --dry      # 只打印，不写文件
"""
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
from suffix_list import collect_extensions   # noqa: E402

LOC = HERE.parent / "HashChecker" / "Localization.cs"

# 每种语言的「校验记录」说法
LABEL = {"zh-CN": "校验记录", "zh-TW": "校驗記錄", "en-US": "Checksum record"}
ALL_LABEL = {"zh-CN": "所有文件", "zh-TW": "所有檔案", "en-US": "All files"}
TEXT_LABEL = {"zh-CN": "文本文件", "zh-TW": "文字檔", "en-US": "Text file"}


def build_filter(language):
    """拼出「校验记录 (后缀列表)|后缀列表|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*」。"""
    exts = collect_extensions()
    patterns = ";".join("*" + e for e in exts) + ";*.txt"

    readable = ";".join("*" + e for e in exts) + ";*.txt"
    return "%s (%s)|%s|%s (*.txt)|*.txt|%s (*.*)|*.*" % (
        LABEL[language], readable, patterns, TEXT_LABEL[language], ALL_LABEL[language])


def blocks(text):
    """按语言切段，返回 [(语言, 起, 止)]。"""
    out = []
    for m in re.finditer(r'"((?:zh-CN|zh-TW|en-US))"\s*,\s*new Dictionary<string, string>\s*\{', text):
        start = m.end()
        depth = 1
        i = start
        while i < len(text) and depth > 0:
            if text[i] == '{':
                depth += 1
            elif text[i] == '}':
                depth -= 1
            i += 1
        out.append((m.group(1), start, i - 1))
    return out


def main():
    dry = "--dry" in sys.argv
    text = LOC.read_text(encoding="utf-8")

    # 从后往前替换，避免下标失效
    for language, start, end in reversed(blocks(text)):
        segment = text[start:end]
        value = build_filter(language)

        escaped = value.replace("\\", "\\\\").replace('"', '\\"')

        for key in ("MsgSaveListFilter", "MsgFileFilter"):
            pattern = re.compile(r'(\{ "' + key + r'"\s*,\s*)"(?:[^"\\]|\\.)*"')
            segment, count = pattern.subn(lambda m, k=key: m.group(1) + '"' + escaped + '"', segment)
            if count == 0:
                print("  [警告] %s 缺少键 %s" % (language, key))

        text = text[:start] + segment + text[end:]

    if dry:
        print(build_filter("zh-CN"))
        return 0

    LOC.write_text(text, encoding="utf-8")
    print("已更新 %s" % LOC)
    return 0


if __name__ == "__main__":
    sys.exit(main())
