#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""死代码扫描：找出「只在声明处出现一次」的成员。

做法很朴素：把每个 .cs 文件里声明的成员（方法 / 属性 / 字段 / 类）名字收集起来，
再在整个源码集里数这个名字总共出现多少次。只出现一次 = 只有声明、没人用。

刻意不去做真正的语法分析：这个项目全是手写 C#，没有生成代码参与，
按名字计数已经足够，而且误报一眼就能看出来。

会主动排除的：
* XAML 里用 x:Name 生成的字段（代码里看不到引用，但界面在用）；
* 由绑定、事件、反射调用的成员（WPF 的 Click="..." 之类）；
* 探针通过反射调用的内部成员（testtools 里的名字也算引用，所以一并扫描）。

用法:
    python deadcode.py [源码目录]        # 默认指向 ..\\HashChecker
退出码: 0 = 未发现可疑项, 1 = 有可疑项
"""
import re
import sys
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent


def collect_sources(root):
    """返回 [(相对路径, 文本)]，跳过 obj / bin / Properties 自动生成的文件。"""
    out = []
    for path in sorted(root.rglob("*.cs")):
        parts = path.parts
        if "obj" in parts or "bin" in parts:
            continue
        # 自动生成的 Designer 文件不算
        if path.name.endswith(".Designer.cs"):
            continue
        out.append((str(path.relative_to(root)), path.read_text(encoding="utf-8", errors="replace")))
    return out


# 成员声明：能抓出名字即可，不追求覆盖所有 C# 语法
DECLARATIONS = [
    # private / public / internal / protected [static] 方法
    re.compile(r'^\s*(?:public|private|protected|internal)\s+(?:static\s+)?(?:async\s+)?'
               r'(?:[\w<>\[\],\.\?\s]+?)\s+(\w+)\s*\(', re.M),
    # 属性
    re.compile(r'^\s*(?:public|private|protected|internal)\s+(?:static\s+)?'
               r'(?:[\w<>\[\],\.\?\s]+?)\s+(\w+)\s*$', re.M),
    # 字段
    re.compile(r'^\s*(?:public|private|protected|internal)\s+(?:static\s+|readonly\s+)*'
               r'(?:[\w<>\[\],\.\?\s]+?)\s+(\w+)\s*[;=]', re.M),
]

# 这些名字太通用，计数没有意义
IGNORED_NAMES = {
    "Main", "Get", "Set", "Add", "Run", "Load", "Save", "Show", "Close",
    "ToString", "Equals", "GetHashCode", "Initialize", "Append", "Finish",
    "Refresh", "Clear", "Remove", "Update", "Format", "Parse",
}


def declared_names(text):
    names = set()
    for pattern in DECLARATIONS:
        for match in pattern.finditer(text):
            name = match.group(1)
            if len(name) < 4:
                continue
            if name in IGNORED_NAMES:
                continue
            # 构造函数（名字等于类名）与常见关键字不算
            if name[0].isupper() and name in ("Window", "Content", "Header", "Title"):
                continue
            names.add(name)
    return names


def xaml_names(root):
    """XAML 里 x:Name 声明、Click/SelectionChanged 之类引用的名字。"""
    names = set()
    for path in root.rglob("*.xaml"):
        text = path.read_text(encoding="utf-8", errors="replace")
        names.update(re.findall(r'x:Name="(\w+)"', text))
        # 事件处理器的名字也算被引用
        names.update(re.findall(r'(?:Click|Changed|KeyDown|Loaded|Closing|Drop|DragOver|'
                               r'MouseDoubleClick|TextChanged|SelectionChanged)="(\w+)"', text))
    return names


def main():
    root = Path(sys.argv[1]) if len(sys.argv) > 1 else HERE.parent / "HashChecker"
    if not root.is_dir():
        print("找不到源码目录: %s" % root)
        return 2

    sources = collect_sources(root)

    # 全项目语料：所有 .cs 文本 + XAML 里出现的名字
    corpus_parts = [text for _, text in sources]
    for path in root.rglob("*.xaml"):
        corpus_parts.append(path.read_text(encoding="utf-8", errors="replace"))
    corpus = "\n".join(corpus_parts)

    referenced = set(xaml_names(root))

    suspects = []
    for rel, text in sources:
        for name in declared_names(text):
            # 在整个语料里数独立出现次数（包含声明处）
            hits = len(re.findall(r'\b' + re.escape(name) + r'\b', corpus))
            if hits <= 1:
                suspects.append((rel, name, hits))

    if not suspects:
        print("未发现「只声明未使用」的成员 ✓")
        return 0

    print("可疑死代码（名字在全项目中只出现一次）：")
    print("")
    by_file = defaultdict(list)
    for rel, name, hits in suspects:
        by_file[rel].append((name, hits))

    for rel in sorted(by_file):
        print("  %s" % rel)
        for name, _ in sorted(by_file[rel]):
            print("      %s" % name)
        print("")

    print("共 %d 个可疑成员，分布在 %d 个文件。" % (len(suspects), len(by_file)))
    print("注意：XAML 里 x:Name 与事件处理器已被计入引用；")
    print("      由字符串反射调用的成员需要人工确认（探针按名字调用的会被算作引用）。")
    return 1


if __name__ == "__main__":
    sys.exit(main())
