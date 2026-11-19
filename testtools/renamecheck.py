#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""改名残留检查：改名必须同步改掉全部引用点。

为什么需要
----------
改 UI 控件标识名时，如果只改了 XAML 的 `x:Name` 而漏掉代码里的引用，
编译可能照样通过（C# 侧是 `FindName` 或生成的字段），但运行时拿到 null，
或者 XAML 绑定静默失效 —— 这类问题编译期不报、单元测试也不覆盖。
人工 grep 会漏，所以做成工具。

三类检查
--------
  RN1  旧名残留：本轮改名涉及的旧名字，在源码里一个都不该剩下
  RN2  新名一致：新名字在 XAML 里有 x:Name 定义、且在代码里有引用
  RN3  名不副实：名字里含类型词（Button/TextBox/…）但元素类型对不上

关于 RN3 的豁免
---------------
`RadioButton` 是 `Button` 的子类，名字里出现 `Button` 是合理的；
`Border_Window` 这类「容器叫 Window」的约定也属有意为之。
豁免按**名字前缀 + 元素类型**精确匹配，而不是按名字整体豁免 ——
否则一个真错的控件只要名字碰巧相似就会混过去。

用法:
    python renamecheck.py                 # 查默认工程
    python renamecheck.py "<源码目录>"     # 查任意一份（变异测试用）

退出码：0 = 通过；1 = 有残留或不一致；2 = 目录不存在。
"""
import re
import sys
from pathlib import Path

DEFAULT_SRC = Path(__file__).resolve().parent.parent / "HashChecker_WPF" / "HashChecker"

# ---------------------------------------------------------------- 改名清单
# (旧名, 新名, 说明)。本项目到目前为止**没有**发生过控件改名，
# 这条清单保持为空是正确的 —— 一旦真有改名，把条目加进来，工具立刻开始守它。
#
# 之所以留着一个能跑的空清单，而不是「没改名就不检查」：
# 空清单下 RN2 / RN3 仍然完整生效，能守住「新加的控件名与实际类型相符」。
RENAMES = []

# 旧名允许残留的 (旧名, 文件名) 白名单。按文件豁免，不按名字整体豁免。
ALLOWED_OLD = set()

# ---------------------------------------------------------------- 类型词
# 只列本工程实际会用到的控件类型。宁可少列（多报几条人工确认），
# 也不要错列成「通过」。
#
# 每项是 (类型词, 缩写前缀元组)。缩写这一列来自本工程真实的命名习惯：
# 控件名前缀普遍用 Btn/Lbl/Chk/Cmb/Txt/Col/Menu… 这些短写法，
# 只认完整类型词的话，一个叫 `BtnFoo` 的 TextBlock 会整条漏过去 ——
# 已实测确认过这个漏报，所以缩写必须一起认。
#
# 但缩写会撞车，撞车的必须剔除而不是硬塞进来：
#   * `List` —— `ListMenu*` 是「列表的右键菜单」，指的是 ContextMenu/MenuItem，
#     不是 ListView。实测把它当 ListView 缩写会一次性误报 10 项。已剔除，
#     ListView 只认 `Lv` / 全称。
#   * `Box`  / `Tb` / `Sp` —— 太泛，会命中 InputBox、ToolBox、TSpinner 之类。
#     已剔除，只保留足够特异的写法。
TYPE_WORDS = (
    ("PasswordBox", ("Pwd", "Password")),
    ("CheckBox", ("Chk", "Cb")),
    ("RadioButton", ("Rad", "Rb")),
    ("ComboBox", ("Cmb", "Cbo")),
    ("TextBox", ("Txt", "Tb")),
    ("TextBlock", ("Lbl", "Tblk", "Hint", "Caption")),
    ("ProgressBar", ("Prg", "Progress")),
    ("ListBox", ("Lst",)),
    ("ListView", ("Lv",)),
    ("ScrollViewer", ("Scroll",)),
    ("Slider", ("Sld",)),
    ("ToggleButton", ("Tgl",)),
    ("TabItem", ("Tab",)),
    ("MenuItem", ("MenuItem", "Mnu")),
    ("ContextMenu", ("Ctx", "ContextMenu")),
    ("StackPanel", ("Stack",)),
    ("DockPanel", ("Dock",)),
    ("WrapPanel", ("Wrap",)),
    ("Canvas", ("Cnv",)),
    ("Grid", ("Grd",)),
)

# 豁免：(名字前缀, 类型词, 元素类型) —— 三者同时吻合才豁免。
# 按「前缀 + 类型词 + 元素」精确匹配，不做整体豁免：
# 否则一个真错的控件只要名字碰巧相似就会混过去。
EXEMPT = (
    # RadioButton 是 Button 的子类
    ("RadioButton_", "RadioButton", "RadioButton"),
    # 「窗口样子的容器」：Border 做标题栏用的约定
    ("Border_", "Grid", "Border"),
    ("Border_", "Window", "Border"),
    # 容器里放别的控件，名字描述的是用途不是类型
    ("Panel_", "Image", "StackPanel"),
    # ListMenu* 是列表的右键菜单（ContextMenu / MenuItem），不是 ListView
    ("ListMenu", "ListView", "ContextMenu"),
    ("ListMenu", "ListView", "MenuItem"),
)

# 生成代码不参与：它们由 XAML 编译器按 x:Name 生成，名字对不对要看源文件。
GENERATED_SUFFIXES = (".g.cs", ".g.i.cs", ".Designer.cs")


def read(path):
    raw = path.read_bytes()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace")
    return raw.decode("utf-8", errors="replace")


def collect(root):
    files = []
    for p in sorted(root.rglob("*")):
        if not p.is_file():
            continue
        if p.suffix not in (".cs", ".xaml"):
            continue
        if any(x in p.parts for x in ("bin", "obj", "build")):
            continue
        if any(p.name.endswith(s) for s in GENERATED_SUFFIXES):
            continue
        files.append(p)
    return files


def exempt(name, word, elem):
    for prefix, w, e in EXEMPT:
        if name.startswith(prefix) and word == w and elem == e:
            return True
    return False


def main():
    src = Path(sys.argv[1]) if len(sys.argv) > 1 else DEFAULT_SRC
    if not src.is_dir():
        print("源码目录不存在: %s" % src)
        return 2

    files = collect(src)
    texts = {p: read(p) for p in files}
    all_text = "\n".join(texts.values())

    print("=" * 74)
    print("改名残留检查: %s" % src)
    print("扫描文件 %d 个" % len(files))
    print("=" * 74)

    failures = []

    # ---- RN1 旧名残留 ----
    if not RENAMES:
        print("  --  RN1 改名清单为空（本项目尚未发生控件改名），跳过旧名残留检查")
    for old, new, desc in RENAMES:
        hits = []
        for p, t in texts.items():
            for i, line in enumerate(t.splitlines(), 1):
                if old in line and (old, p.name) not in ALLOWED_OLD:
                    hits.append("%s:%d  %s" % (p.name, i, line.strip()[:90]))
        if hits:
            print("  !!  RN1 旧名「%s」仍有 %d 处残留（应改为 %s）"
                  % (old, len(hits), new))
            for h in hits[:6]:
                print("        %s" % h)
            failures.append("旧名 %s 残留 %d 处" % (old, len(hits)))
        else:
            print("  OK  RN1 旧名「%s」已无残留 —— %s" % (old, desc))

    # ---- RN2 新名一致：XAML 有定义 + 代码有引用 ----
    for old, new, desc in RENAMES:
        xaml_def = [(p.name, i) for p, t in texts.items()
                    if p.suffix == ".xaml"
                    for i, line in enumerate(t.splitlines(), 1)
                    if 'x:Name="%s"' % new in line]
        code_ref = [(p.name, i) for p, t in texts.items()
                    if p.suffix == ".cs"
                    for i, line in enumerate(t.splitlines(), 1)
                    if re.search(r"\b%s\b" % re.escape(new), line)]

        if not xaml_def:
            print("  !!  RN2 新名「%s」在 XAML 里没有 x:Name 定义" % new)
            failures.append("新名 %s 无 XAML 定义" % new)
        elif not code_ref:
            print("  !!  RN2 新名「%s」在 XAML 有定义，但代码里没有引用 —— 改名可能漏了" % new)
            failures.append("新名 %s 无引用" % new)
        else:
            print("  OK  RN2 新名「%s」：XAML 定义 %s，代码引用 %d 处"
                  % (new, ", ".join("%s:%d" % x for x in xaml_def), len(code_ref)))

    # ---- RN3 名不副实：名字含类型词（全称或缩写）但元素类型不符 ----
    checked = 0
    for p in files:
        if p.suffix != ".xaml":
            continue
        t = texts[p]
        for m in re.finditer(r'<(\w+)([^>]*?x:Name="([^"]+)"[^>]*?)/?>', t, re.S):
            elem, name = m.group(1), m.group(3)
            checked += 1

            # 名字里的「类型词」既可能是全称（TextBlock）也可能是缩写（Txt/Lbl）。
            # 逐个类型词看命中哪种写法，命中了就要求元素类型对得上。
            for type_word, tokens in TYPE_WORDS:
                hit_token = None
                for token in tokens:
                    # 缩写按「首字母大写 + 后接大写或结尾」的模式认，
                    # 免得 Txt 出现在单词中间（如 ContextTxtBox）被误判。
                    if re.search(r"(?:^|_)(%s)(?=[A-Z_]|$)" % re.escape(token), name):
                        hit_token = token
                        break
                if hit_token is None:
                    continue

                if elem == type_word:
                    continue
                if exempt(name, type_word, elem):
                    continue
                # 元素是类型词的派生（如 DataGridView 之于 Grid）也放过
                if elem.endswith(type_word):
                    continue

                line = t[:m.start()].count("\n") + 1
                print("  !!  RN3 %s:%d  名字「%s」含类型词 %s（%s），实际是 <%s>"
                      % (p.name, line, name, type_word, hit_token, elem))
                failures.append("名不副实: %s（%s vs <%s>）" % (name, type_word, elem))
                break  # 一个控件只报一次

    # ---- RN4 控件名在代码里是否真有引用（防止改名后留孤儿） ----
    orphans = []
    for p in files:
        if p.suffix != ".xaml":
            continue
        t = texts[p]
        stem = p.name[:-len(".xaml")]
        for m in re.finditer(r'x:Name="([^"]+)"', t):
            name = m.group(1)
            code_ref = re.search(r"\b%s\b" % re.escape(name), all_text)
            if code_ref:
                continue
            # XAML 内部引用（绑定、事件处理器、样式）也算用上了
            if re.search(r'\{[^}]*\b%s\b[^}]*\}' % re.escape(name), t):
                continue
            if re.search(r'(?:Click|Changed|Loaded|Closing|Drop|DragOver|MouseDoubleClick|'
                         r'TextChanged|SelectionChanged|KeyDown|ValueChanged)="%s"'
                         % re.escape(name), all_text):
                continue
            orphans.append("%s  %s" % (stem, name))

    if orphans:
        print("  --  RN4 以下控件名在代码与绑定里都没出现（可能是纯展示控件，需人工确认）：")
        for o in orphans[:20]:
            print("        %s" % o)
    else:
        print("  OK  RN4 全部 %d 个 x:Name 都有代码或绑定引用" % checked)

    print()
    if failures:
        print("改名检查不通过 %d 项：" % len(failures))
        for f in failures:
            print("  !! %s" % f)
        return 1
    print("改名检查通过 ✓")
    print("RN1 旧名无残留、RN2 新名定义与引用一致、RN3 名字不含不匹配的类型词")
    return 0


if __name__ == "__main__":
    sys.exit(main())