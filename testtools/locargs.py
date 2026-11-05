#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""本地化格式参数与占位符数量匹配检查（V03 维度）。

localization_check.py 只保证「三种语言的占位符数量一致」，它不保证
「代码里 Format("Key", a, b) 传的参数个数与词典里的 {n} 个数对得上」——
而编译期看不出这个，运行时就是 FormatException，界面直接崩。

用法:
    python locargs.py <源码目录> [--json]
退出码: 0 = 匹配, 1 = 有对不上的, 2 = 找不到词典
"""
import json
import re
import sys
from pathlib import Path

LANG_HEADER = re.compile(r'"((?:zh-CN|zh-TW|en-US))"\s*,\s*new Dictionary<string, string>\s*\{')
ENTRY = re.compile(r'\{ "(\w+)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\}')


def read(p):
    raw = p.read_bytes()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace")
    return raw.decode("utf-8-sig", errors="replace")


def blocks(text):
    out = {}
    for m in LANG_HEADER.finditer(text):
        start = m.end()
        depth = 1
        i = start
        while i < len(text) and depth > 0:
            if text[i] == "{":
                depth += 1
            elif text[i] == "}":
                depth -= 1
            i += 1
        out[m.group(1)] = dict(ENTRY.findall(text[start:i - 1]))
    return out


def placeholders(value):
    return set(re.findall(r"\{(\d+)\}", value))


def count_top_level_args(text, start):
    """从 '(' 后开始数顶层实参个数。返回 (个数, 结束位置)。"""
    depth = 1
    args = 1
    i = start
    in_str = False
    in_char = False
    saw_any = False
    while i < len(text):
        c = text[i]
        if in_str:
            if c == "\\":
                i += 2
                continue
            if c == '"':
                in_str = False
            i += 1
            continue
        if in_char:
            if c == "\\":
                i += 2
                continue
            if c == "'":
                in_char = False
            i += 1
            continue
        if c == '"':
            in_str = True
            saw_any = True
            i += 1
            continue
        if c == "'":
            in_char = True
            saw_any = True
            i += 1
            continue
        if c in "([{":
            depth += 1
            saw_any = True
            i += 1
            continue
        if c in ")]}":
            depth -= 1
            if depth == 0:
                return (args if saw_any else 0), i
            i += 1
            continue
        if c == "," and depth == 1:
            args += 1
            i += 1
            continue
        if not c.isspace():
            saw_any = True
        i += 1
    return args, i


def main():
    src = Path(sys.argv[1])
    loc = src / "Localization.cs"
    if not loc.exists():
        print("找不到 %s" % loc)
        return 2

    languages = blocks(read(loc))
    if not languages:
        print("没有解析到语言段")
        return 2
    base = list(languages.keys())[0]
    texts = languages[base]

    findings = []
    checked = 0
    for p in sorted(src.rglob("*.cs")):
        if p.name == "Localization.cs" or p.name.endswith(".Designer.cs"):
            continue
        text = read(p)
        for m in re.finditer(r'Localization\.Format\(\s*"(\w+)"', text):
            key = m.group(1)
            if key not in texts:
                continue          # 死引用由 localization_check.py 负责
            # 找到 Format( 的左括号
            paren = text.index("(", m.start() + len("Localization.Format"))
            args, _ = count_top_level_args(text, paren + 1)
            passed = args - 1     # 减掉 key 本身
            need = len(placeholders(texts[key]))
            checked += 1
            if passed != need:
                findings.append({
                    "file": str(p.relative_to(src)),
                    "line": text[:m.start()].count("\n") + 1,
                    "key": key,
                    "passed": passed,
                    "need": need,
                })

    print("=" * 70)
    print("本地化格式参数匹配检查: %s" % src)
    print("语言 %d 种，检查 Format 调用 %d 处" % (len(languages), checked))
    print("=" * 70)

    if findings:
        for f in findings:
            print("  !! %s:%d  Format(\"%s\") 传了 %d 个参数，词典里需要 %d 个占位符"
                  % (f["file"], f["line"], f["key"], f["passed"], f["need"]))
        print()
    print("TOTAL PROBLEMS: %d" % len(findings))

    if "--json" in sys.argv:
        print(json.dumps(findings, ensure_ascii=False, indent=2))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
