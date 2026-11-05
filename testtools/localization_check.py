#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""三语词典一致性检查：每种语言必须拥有完全相同的键集合。

为什么单独查：Localization.Get 在键缺失时回退到英文，界面上不会崩，
但中文环境下会冒出英文句子；如果英文也缺，就会显示裸键名（"ColAlgorithm" 这种）。
这类问题运行时测试很难覆盖，只能静态查。

另外查两类更容易被忽略的：
  * 同一个键在三种语言里的格式占位符数量不一致（Format 时会抛 FormatException）；
  * 代码里 Localization.Get/Format 用到的键，词典里根本不存在。

用法:
    python localization_check.py [源码目录]
退出码: 0 = 一致, 1 = 有问题
"""
import re
import sys
from pathlib import Path

SRC = Path(sys.argv[1] if len(sys.argv) > 1 else
           r"C:\Users\Mocha\Desktop\VSProject\HashChecker_WPF\HashChecker")
LOC = SRC / "Localization.cs"

LANG_HEADER = re.compile(r'"((?:zh-CN|zh-TW|en-US))"\s*,\s*new Dictionary<string, string>\s*\{')
ENTRY = re.compile(r'\{ "(\w+)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\}')


def blocks(text):
    """按语言切出各自的键值对。"""
    out = {}
    matches = list(LANG_HEADER.finditer(text))
    for index, m in enumerate(matches):
        start = m.end()
        depth = 1
        i = start
        while i < len(text) and depth > 0:
            if text[i] == '{':
                depth += 1
            elif text[i] == '}':
                depth -= 1
            i += 1
        segment = text[start:i - 1]
        out[m.group(1)] = dict(ENTRY.findall(segment))
    return out


def placeholders(value):
    """取出 {0} / {1} 这类占位符的编号集合。"""
    return set(re.findall(r'\{(\d+)\}', value))


def main():
    if not LOC.exists():
        print("找不到 %s" % LOC)
        return 2

    text = LOC.read_text(encoding='utf-8')
    languages = blocks(text)
    if not languages:
        print("没有解析到语言段")
        return 2

    problems = []
    names = list(languages.keys())
    base = names[0]
    base_keys = set(languages[base].keys())

    for name in names[1:]:
        keys = set(languages[name].keys())
        missing = base_keys - keys
        extra = keys - base_keys
        for key in sorted(missing):
            problems.append("%s 缺少键：%s" % (name, key))
        for key in sorted(extra):
            problems.append("%s 多出键：%s" % (name, key))

    # 占位符数量一致
    for key in sorted(base_keys):
        counts = {}
        for name in names:
            value = languages[name].get(key)
            if value is None:
                continue
            counts[name] = len(placeholders(value))
        if len(set(counts.values())) > 1:
            problems.append("键 %s 的占位符数量不一致：%s" % (key, counts))

    # 代码里引用的键必须存在
    used = set()
    for path in SRC.rglob("*.cs"):
        if path.name == "Localization.cs":
            continue
        content = path.read_text(encoding='utf-8', errors='replace')
        for m in re.finditer(r'Localization\.(?:Get|Format)\(\s*"(\w+)"', content):
            used.add(m.group(1))

    unknown = sorted(used - base_keys)
    for key in unknown:
        problems.append("代码引用了不存在的键：%s" % key)

    unused = sorted(base_keys - used)
    print("=" * 70)
    print("三语词典一致性检查: %s" % LOC)
    print("语言 %d 种，键 %d 个，代码引用 %d 个" % (len(names), len(base_keys), len(used)))
    print("=" * 70)

    if problems:
        for p in problems:
            print("  !! " + p)
        print()
        print("TOTAL PROBLEMS: %d" % len(problems))
        return 1

    print("(无问题)")
    if unused:
        print()
        print("提示：词典里有 %d 个键当前没被代码引用（可能是给未来功能留的）" % len(unused))
        for key in unused[:20]:
            print("  -- " + key)
    print()
    print("TOTAL PROBLEMS: 0")
    return 0


if __name__ == "__main__":
    sys.exit(main())
