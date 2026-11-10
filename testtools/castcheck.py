#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""强制转换与控件实际类型的一致性检查（补充 cs_check.py 的 ① 项）。

cs_check.py 的 ① 规则只认 `FindName("字面量") as 类型` 这种写法。
本工具补上三种它看不到的形态：

  1. `FindName(变量) as 类型` —— 变量在运行时才决定控件，静态无法确认；
     但若转换目标是个具体控件类型（不是 FrameworkElement / Control / UIElement
     这类基类），一旦对不上就是恒为 null，必须人工确认，所以报出来；
  2. `(类型)FindName("名字")` —— 显式转换写法的同一问题；
  3. `控件名 as 类型` —— 直接对 x:Name 生成的字段做 as 转换。

判定口径：
  元素类型 == 转换类型                      -> 通过；
  转换类型是元素类型的基类（见 BASE_OF）     -> 通过（派生类转基类永远安全）；
  其余                                       -> 报出（恒为 null 或必然抛 InvalidCastException）。

用法:
    python castcheck.py <源码目录>
退出码: 0 = 没发现问题, 1 = 有可疑项, 2 = 目录不存在
"""
import re
import sys
from pathlib import Path

SRC_EXT = (".cs", ".xaml")

# WPF 里常用的「基类 -> 派生」关系。只列本工程会用到的几条，
# 宁可少列（多报几条人工确认），也不要错列成通过。
BASE_OF = {
    "FrameworkElement": {"Control", "Button", "TextBox", "TextBlock", "Grid", "StackPanel",
                          "Border", "Image", "ListView", "ListBox", "ComboBox", "CheckBox",
                          "RadioButton", "ToggleButton", "Slider", "TabItem", "MenuItem",
                          "Window", "ContentControl", "ItemsControl", "Panel", "Canvas",
                          "DockPanel", "WrapPanel", "ScrollViewer", "ProgressBar", "Separator",
                          "PasswordBox", "Expander", "GroupBox", "Label", "Rectangle"},
    "Control": {"Button", "TextBox", "TextBlock", "ListView", "ListBox", "ComboBox", "CheckBox",
                 "RadioButton", "ToggleButton", "Slider", "TabItem", "MenuItem", "Window",
                 "ContentControl", "ProgressBar", "PasswordBox", "Expander", "GroupBox",
                 "Label", "ScrollViewer"},
    "UIElement": {"FrameworkElement"},
    "DependencyObject": {"FrameworkElement", "UIElement"},
    "ButtonBase": {"Button", "ToggleButton", "RadioButton", "CheckBox", "RepeatButton"},
    "ItemsControl": {"ListView", "ListBox", "ComboBox", "Menu", "TreeView", "TabControl"},
    "Panel": {"Grid", "StackPanel", "Canvas", "DockPanel", "WrapPanel", "UniformGrid"},
    "ContentControl": {"Button", "Window", "GroupBox", "Expander", "Label", "TabItem",
                        "ScrollViewer", "ToolTip"},
    "Selector": {"ListView", "ListBox", "ComboBox", "TabControl"},
}

# 这些目标类型什么都能装，不算可疑
WIDE_TYPES = {"FrameworkElement", "Control", "UIElement", "DependencyObject", "object",
              "Object", "Visual", "ContentControl", "Panel", "ItemsControl"}


def read(p):
    raw = p.read_bytes()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace")
    return raw.decode("utf-8-sig", errors="replace")


def collect(root):
    out = []
    for p in sorted(root.rglob("*")):
        if not p.is_file() or p.suffix not in SRC_EXT:
            continue
        if any(x in p.parts for x in ("bin", "obj", "build")):
            continue
        out.append(p)
    return out


def compatible(element, cast_type):
    if element == cast_type:
        return True
    if cast_type in WIDE_TYPES:
        return True
    return element in BASE_OF.get(cast_type, set())


def main():
    if len(sys.argv) < 2:
        print("用法: python castcheck.py <源码目录>")
        return 2
    root = Path(sys.argv[1])
    if not root.is_dir():
        print("目录不存在: %s" % root)
        return 2

    sources = collect(root)

    # XAML: x:Name -> 元素类型
    element_of = {}
    for p in sources:
        if p.suffix != ".xaml":
            continue
        text = read(p)
        for m in re.finditer(r"<([\w:]+)([^>]*?x:Name=\"([^\"]+)\"[^>]*?)/?>", text, re.S):
            element_of[(p.stem, m.group(3))] = m.group(1).split(":")[-1]

    findings = []
    for p in sources:
        if p.suffix != ".cs":
            continue
        text = read(p)
        # XAML 侧键用的是 p.stem（MainWindow.xaml -> MainWindow），
        # 所以代码侧必须把 MainWindow.xaml.cs 还原成 MainWindow；
        # 原来写 p.name[:-3] 得到的是 "MainWindow.xaml"，②③ 两条规则因此永远匹配不上。
        if p.name.endswith(".xaml.cs"):
            stem = p.name[:-8]
        else:
            stem = p.stem
        lines = text.split("\n")

        # ③ 控件名 as 类型
        for m in re.finditer(r"\b(\w+)\s+(\w+)\s*=\s*(\w+)\s+as\s+([\w\.]+)\s*;", text):
            field, cast = m.group(3), m.group(4).split(".")[-1]
            element = element_of.get((stem, field))
            if element is None or compatible(element, cast):
                continue
            findings.append((p.name, text[:m.start()].count("\n") + 1,
                             "%s as %s，但 XAML 里它是 <%s> —— 恒为 null" % (field, cast, element)))

        # ② (类型)FindName("名字")
        for m in re.finditer(r'\(\s*([\w\.]+)\s*\)\s*FindName\(\s*"([^"]+)"\s*\)', text):
            cast, field = m.group(1).split(".")[-1], m.group(2)
            element = element_of.get((stem, field))
            if element is None or compatible(element, cast):
                continue
            findings.append((p.name, text[:m.start()].count("\n") + 1,
                             "(FindName(\"%s\")) 强转 %s，但 XAML 里它是 <%s> —— 运行时抛异常"
                             % (field, cast, element)))

        # ① FindName(非字面量) as 具体类型
        for m in re.finditer(r"FindName\(\s*([A-Za-z_]\w*)\s*\)\s*as\s+([\w\.]+)", text):
            arg, cast = m.group(1), m.group(2).split(".")[-1]
            if cast in WIDE_TYPES:
                continue
            lineno = text[:m.start()].count("\n") + 1
            findings.append((p.name, lineno,
                             "FindName(%s) as %s —— 控件名在运行时才确定，"
                             "若该控件不是 %s 则恒为 null，需人工确认" % (arg, cast, cast)))

    print("=" * 70)
    print("强制转换类型检查: %s" % root)
    print("扫描文件 %d 个，XAML 控件 %d 个" % (len(sources), len(element_of)))
    print("=" * 70)

    if findings:
        for name, lineno, detail in findings:
            print("  !! %s:%d  %s" % (name, lineno, detail))
        print()
    print("TOTAL PROBLEMS: %d" % len(findings))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
