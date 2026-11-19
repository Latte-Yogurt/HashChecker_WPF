#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""Auto7z 静态检查：命名/类型一致性、死分支、文档标签、路径边界。

这是为 §5b 变异测试补上的检查工具：
原有的 regress.py 只做运行时行为检查，
"控件类型与代码 cast 不一致"、"空 catch"、"文档标签没闭合"、
"递归删除缺边界检查" 这四类缺陷它一个都测不出来，
对应的测试项属于「无效测试」。

用法:
    python cs_check.py <源码目录>
    python cs_check.py <源码目录> --json

退出码: 0 = 没发现问题, 1 = 有问题
"""
import json
import re
import sys
from pathlib import Path

SRC_EXT = (".cs", ".xaml", ".cpp", ".h")


def collect_sources(root):
    out = []
    for p in sorted(root.rglob("*")):
        if not p.is_file():
            continue
        if p.suffix not in SRC_EXT:
            continue
        if any(x in p.parts for x in ("bin", "obj", "build")):
            continue
        out.append(p)
    return out


def read(p):
    raw = p.read_bytes()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        return raw.decode("utf-16", errors="replace")
    return raw.decode("utf-8", errors="replace")


# ---------------------------------------------------------------- ① 命名/类型
# XAML 里 x:Name 含类型词，但元素类型不是那个类型
TYPE_WORDS = {
    "Button": "Button", "TextBox": "TextBox", "CheckBox": "CheckBox",
    "RadioButton": "RadioButton", "ComboBox": "ComboBox", "Image": "Image",
    "TextBlock": "TextBlock", "Grid": "Grid", "StackPanel": "StackPanel",
    "Border": "Border", "TabItem": "TabItem", "PasswordBox": "PasswordBox",
    "ListBox": "ListBox", "Slider": "Slider", "Window": "Window",
    "ToggleButton": "ToggleButton", "RadioButton_": "RadioButton",
}


def check_naming(sources, findings):
    """① XAML x:Name 的类型词与实际元素类型不符；
    ② 代码里 FindName/X cast 的目标类型与 XAML 实际元素类型不符。"""
    # 收集每个 xaml 的 x:Name -> 元素类型
    name_to_elem = {}   # (xaml, xname) -> elem
    for p in sources:
        if p.suffix != ".xaml":
            continue
        text = read(p)
        for m in re.finditer(r"<(\w+)([^>]*?x:Name=\"([^\"]+)\"[^>]*?)/?>", text, re.S):
            elem, name = m.group(1), m.group(3)
            name_to_elem[(p.name, name)] = elem

            # ① 类型词不符
            for word, tname in TYPE_WORDS.items():
                if word in name and elem != tname:
                    # 已知且有意为之的例外：
                    #   Grid 做成按钮样子（标题栏的最小化/关闭）
                    #   StackPanel 里放 Image（图片面板）
                    #   Border 名字里带 Window/Border（窗口边框）
                    #   RadioButton_ 前缀是命名约定，RadioButton 本就是 Button 的子类
                    if word == "Button" and elem == "Grid":
                        continue
                    if word == "Image" and elem == "StackPanel":
                        continue
                    if word == "Window" and elem == "Border":
                        continue
                    if word == "Grid" and elem == "Border" and name.startswith("Border_"):
                        continue
                    if word == "Button" and elem == "RadioButton" and name.startswith("RadioButton_"):
                        continue
                    findings.append({
                        "class": "① 命名/类型不匹配",
                        "file": p.name,
                        "line": text[:m.start()].count("\n") + 1,
                        "detail": 'x:Name="%s" 含类型词 %s，实际元素是 <%s>' % (name, word, elem),
                    })

    # ② 代码里的 as 转换与 XAML 实际类型不符
    #    键是 (XAML 文件名, x:Name)，代码文件按 "同名.cs" 对应到同名 XAML。
    for p in sources:
        if p.suffix != ".cs":
            continue
        text = read(p)
        # PasswordWindow.xaml.cs -> PasswordWindow.xaml
        xaml_name = p.name[:-3] if p.name.endswith(".cs") else p.stem + ".xaml"
        # FindName("X") as Type  以及  FindName("X") as Some.Namespace.Type
        for m in re.finditer(r'FindName\(\s*"([^"]+)"\s*\)\s*as\s+([\w\.]+)', text):
            fname, ctype = m.group(1), m.group(2).split(".")[-1]
            elem = name_to_elem.get((xaml_name, fname))
            if elem is None:
                continue
            if ctype not in TYPE_WORDS:
                continue
            if elem == ctype:
                continue
            # 已知例外：FindName 拿到的可能是派生类型（基类 -> 派生类 是合法的）
            if ctype in ("FrameworkElement", "Control", "UIElement"):
                continue
            findings.append({
                "class": "① 命名/类型不匹配",
                "file": p.name,
                "line": text[:m.start()].count("\n") + 1,
                "detail": 'FindName("%s") as %s，但 XAML 里它是 <%s> —— 该转换恒为 null'
                          % (fname, ctype, elem),
            })


# ---------------------------------------------------------------- ② 死分支
CATCH_HEAD = re.compile(r"\bcatch\b\s*(?:\([^)]*\))?\s*\{")
COMMENT_RE = re.compile(r"/\*.*?\*/|//[^\n]*", re.S)


def _strip_code(inner):
    """去掉注释，返回 (剩余代码, 注释文本)。"""
    comments = " ".join(m.group(0) for m in COMMENT_RE.finditer(inner))
    code = COMMENT_RE.sub("", inner)
    return code, comments


def _empty_catch_body(text, brace_idx):
    """从 { 起做括号配对，返回 (是否只有注释没有语句, 注释文本)。

    必须做配对而不是只看一行：`catch` 与 `}` 分行、中间只有注释的写法
    单行正则完全漏掉（这是原先工具的一个盲区）。
    """
    depth = 0
    i = brace_idx
    buf = []
    while i < len(text):
        c = text[i]
        if c == "{":
            depth += 1
            i += 1
            continue
        if c == "}":
            depth -= 1
            if depth == 0:
                code, comments = _strip_code("".join(buf))
                return code.strip() == "", comments
            i += 1
            continue
        if depth >= 1:
            buf.append(c)
        i += 1
    return False, ""


def check_dead_branch(sources, findings):
    """空 catch / 恒假条件。

    空 catch 分两档：
      已说明 —— 吞掉异常但写了原因（`catch { /* 删不掉不影响结果 */ }`），登记但不判失败；
      未说明 —— 光秃秃地吞掉异常，判失败。
    规则：**异常可以吞，但必须写明为什么吞**。
    """
    for p in sources:
        if p.suffix != ".cs":
            continue
        text = read(p)
        lines = text.splitlines()

        for m in CATCH_HEAD.finditer(text):
            empty, comments = _empty_catch_body(text, m.end() - 1)
            if not empty:
                continue
            line = text[:m.start()].count("\n") + 1
            reason = comments.strip().strip("/*").strip()
            if len(reason) >= 4:
                findings.append({
                    "class": "② 空 catch（已说明）",
                    "file": p.name,
                    "line": line,
                    "detail": "空 catch，已写明原因：%s" % reason[:60],
                    "registered": True,
                })
            else:
                findings.append({
                    "class": "② 空 catch（未说明）",
                    "file": p.name,
                    "line": line,
                    "detail": "空 catch 吞掉异常且没有说明原因",
                })

        # if (false) / if (true) 之类恒定条件
        for i, l in enumerate(lines):
            if re.search(r"\bif\s*\(\s*(false|true|0|1)\s*\)", l.strip()):
                findings.append({
                    "class": "② 死分支/空 catch",
                    "file": p.name,
                    "line": i + 1,
                    "detail": "恒定条件的 if：分支永远不会走" + l.strip()[:60],
                })


# ---------------------------------------------------------------- ③ 文档标签
def check_doc_tags(sources, findings):
    """XML 文档标签不配对、重复。"""
    for p in sources:
        if p.suffix != ".cs":
            continue
        text = read(p)
        so = len(re.findall(r"<summary>", text))
        sc = len(re.findall(r"</summary>", text))
        if so != sc:
            findings.append({
                "class": "③ 未闭合文档标签",
                "file": p.name,
                "line": 0,
                "detail": "<summary> 开启 %d 次，</summary> 关闭 %d 次 —— 后续整段会被注释吞掉" % (so, sc),
            })
        po = len(re.findall(r"<param[ >]", text))
        pc = len(re.findall(r"</param>", text))
        if po != pc:
            findings.append({
                "class": "③ 未闭合文档标签",
                "file": p.name,
                "line": 0,
                "detail": "<param> %d 个，</param> %d 个" % (po, pc),
            })
        ro = len(re.findall(r"<returns>", text))
        rc = len(re.findall(r"</returns>", text))
        if ro != rc:
            findings.append({
                "class": "③ 未闭合文档标签",
                "file": p.name,
                "line": 0,
                "detail": "<returns> %d 个，</returns> %d 个" % (ro, rc),
            })
        # 连续重复的同一行 summary
        lines = text.splitlines()
        for i in range(len(lines) - 1):
            a, b = lines[i].strip(), lines[i + 1].strip()
            if a and a == b and a.startswith("///"):
                findings.append({
                    "class": "③ 未闭合文档标签",
                    "file": p.name,
                    "line": i + 1,
                    "detail": "重复的文档标签：%s" % a[:60],
                })


# ---------------------------------------------------------------- ⑤ 路径边界
DANGEROUS_CALLS = [
    # 只盯递归删除：单文件删除的输入要么是程序自己拼的临时文件，
    # 要么紧跟在 File.Exists 判断之后，误报太多没有意义。
    (r"Directory\.Delete\(\s*([^,)]+)\s*,\s*true\s*\)", "递归删除目录"),
    (r"DirectoryInfo\.Delete\(\s*true\s*\)", "递归删除目录"),
]


def check_path_guard(sources, findings):
    """递归删除缺边界校验：删除调用之前没有 null / 存在性 / 根路径检查。

    只查递归删除（recursive=true）。单文件删除的输入要么是程序自己拼的临时文件，
    要么紧跟在 File.Exists 判断之后，全查的话误报会淹没真问题。
    """
    for p in sources:
        if p.suffix not in (".cs",):
            continue
        lines = read(p).splitlines()
        for i, l in enumerate(lines):
            s = l.strip()
            for pat, what in DANGEROUS_CALLS:
                if not re.search(pat, s):
                    continue
                window = "\n".join(lines[max(0, i - 8):i])
                has_guard = bool(re.search(
                    r"(IsNullOrWhiteSpace|string\.IsNullOrEmpty|ArgumentException|"
                    r"Directory\.Exists|File\.Exists|StartsWith|IsPathRooted|"
                    r"GetFullPath|GetInvalidFileNameChars|EnumerateFileSystemEntries)", window))
                if not has_guard:
                    findings.append({
                        "class": "⑤ 路径缺边界检查",
                        "file": p.name,
                        "line": i + 1,
                        "detail": "%s 前没有路径边界校验：%s" % (what, s[:70]),
                    })


# ------------------------------------------------ ⑤-b 路径拼接越界（新增）
# 为什么需要这一条
# ----------------
# 原来的 ⑤ 只认「递归删除」。这个项目真正容易出事的地方不是删除，而是
# **把校验记录里写的文件名拼进目录**：
#
#     string combined = Path.GetFullPath(Path.Combine(root, recorded));
#     if (!combined.StartsWith(rootWithSeparator, ...)) return null;
#
# 删掉后面那行 StartsWith，一份写着 `../../secret.txt` 的记录就能让程序
# 去读记录目录之外的文件。旧规则对这种缺陷**一条都不报**——实测把那两行
# 删掉后，它仍打印 `TOTAL PROBLEMS: 0`。这是能力边界，不是执行错误。
#
# 判定口径（两条，任一命中即报）
# ------------------------------
# ① 「归一化 + 拼接」特征：出现 Path.GetFullPath(Path.Combine(...))。
#    这个写法本身说明作者**打算**做越界判定，所以方法体内必须真有判定；
#    没有就是判定被删了。
# ② 「参数直接拼接」特征：Path.Combine 的第二实参就是所在方法的一个形参
#    （外部输入直接进来），且方法体内没有越界判定。
#
# 为什么这两条能避开误报
# ----------------------
# 代码里另有三处 Path.Combine，第二实参都是**已经净化成单个名字段**的量：
#   MainWindow.xaml.cs:1310  stem + wanted      —— stem 由文件名 Substring 而来
#   MainWindow.xaml.cs:1417  folderName + ext   —— folderName 来自 Path.GetFileName
#   ChecksumResolver.cs:476  baseOnly           —— 来自 ChecksumFile.SafeFileName
# 它们既没有 GetFullPath 包裹，第二实参也不是裸形参，两条都不命中。
#
# 已知边界（如实声明）
# --------------------
# 若有人把 GetFullPath 与 StartsWith **两行一起**删掉，① 不再成立、② 也不成立，
# 本规则会漏报。那种改动由运行时 pathprobe 的 `..` 穿越用例兜底
# （本次变异实测：pathprobe 能捕获）。
#
# 一律先剥注释与字符串再判断：否则注释里那句「这里用 StartsWith 做边界校验」
# 会让规则误判成通过 —— heuristic_probe 的 path 段就栽在这上面。

COMBINE_RE = re.compile(r"\bPath\.Combine\s*\(")

# 越界判定的两种写法：前缀比较，或长度比较
CONTAIN_RE = re.compile(
    r"(?:\.StartsWith\s*\(|"
    r"\.Length\s*<=\s*\w+\.Length|"
    r"\w+\.Length\s*>\s*\w+\.Length)")

# 方法体定位：方法签名 + {，用于取形参表与体范围
METHOD_HEAD_RE = re.compile(
    r"(?:^|\n)[ \t]*(?:(?:public|private|protected|internal|static|sealed|override|virtual|"
    r"async|new|partial|extern|unsafe|abstract)[ \t]+)+"
    r"[A-Za-z_][\w<>\[\],\.\? \t]*?[ \t]+(?P<name>[A-Za-z_]\w*)[ \t]*\((?P<params>[^;{}]*)\)"
    r"[ \t\r\n]*(?:\{|=>)")


def _strip_noise(text):
    """剥掉注释与字符串字面量，只留会真正执行的代码骨架。

    这一步不能省：产品的注释里大量出现 `StartsWith`、`GetFullPath` 这些词
    （它们恰好在解释「为什么要这么做」），不剥离就会把注释当实现。
    """
    out = []
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                out.append(" ")
                i += 1
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            out.append("  ")
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                out.append("\n" if text[i] == "\n" else " ")
                i += 1
            if i + 1 < n:
                out.append("  ")
                i += 2
            continue
        if c == "@" and i + 1 < n and text[i + 1] == '"':
            out.append("  ")
            i += 2
            while i < n:
                if text[i] == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        out.append("  ")
                        i += 2
                        continue
                    out.append(" ")
                    i += 1
                    break
                out.append("\n" if text[i] == "\n" else " ")
                i += 1
            continue
        if c in '"\'':
            q = c
            out.append(" ")
            i += 1
            while i < n:
                if text[i] == "\\":
                    out.append("  ")
                    i += 2
                    continue
                if text[i] == q:
                    out.append(" ")
                    i += 1
                    break
                out.append("\n" if text[i] == "\n" else " ")
                i += 1
            continue
        out.append(c)
        i += 1
    return "".join(out)


def _brace_span(text, open_idx):
    """从 { 出发做配对，返回闭合 } 的下标；配不上返回 -1。"""
    depth = 0
    i = open_idx
    while i < len(text):
        c = text[i]
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def _split_args(text, open_paren):
    """从 ( 出发切出顶层实参列表；返回 (args, 右括号下标)。"""
    depth = 0
    i = open_paren
    args = []
    cur = []
    while i < len(text):
        c = text[i]
        if c in "([{":
            depth += 1
            if depth == 1:
                i += 1
                continue
        elif c in ")]}":
            depth -= 1
            if depth == 0:
                args.append("".join(cur).strip())
                return args, i
        if depth == 1 and c == ",":
            args.append("".join(cur).strip())
            cur = []
        elif depth >= 1:
            cur.append(c)
        i += 1
    return args, -1


def _method_of(code, pos):
    """返回 pos 所在方法的 (名字, 形参名集合, 方法体起, 方法体止)；找不到返回 None。"""
    head = None
    for hm in METHOD_HEAD_RE.finditer(code, 0, pos):
        head = hm
    if head is None:
        return None
    params = set()
    for raw in (head.group("params") or "").split(","):
        raw = raw.strip()
        if not raw:
            continue
        raw = re.sub(r"^(?:ref|out|in|params)\s+", "", raw)
        parts = raw.split("=")[0].split()
        if len(parts) >= 2:
            params.add(parts[-1])
    body_start = code.find("{", head.end() - 1)
    body_end = _brace_span(code, body_start) if body_start >= 0 else -1
    return head.group("name"), params, body_start, body_end


def check_combine_guard(sources, findings):
    """⑤-b 把外部输入拼进目录时，必须做越界判定。"""
    for p in sources:
        if p.suffix != ".cs":
            continue
        raw = read(p)
        code = _strip_noise(raw)

        for m in COMBINE_RE.finditer(code):
            args, close = _split_args(code, m.end() - 1)
            if len(args) < 2:
                continue
            second = args[1]

            info = _method_of(code, m.start())
            if info is None:
                continue
            mname, params, body_start, body_end = info
            if body_end < 0:
                continue

            body = code[body_start:body_end]
            has_contain = CONTAIN_RE.search(body) is not None

            # ① 归一化 + 拼接 特征
            norm_then_combine = re.match(r"\s*Path\.GetFullPath\s*$", code[:m.start()][-40:]) is not None
            # ② 第二实参是裸形参
            bare_param = second in params

            if not (norm_then_combine or bare_param):
                continue
            if has_contain:
                continue

            why = []
            if norm_then_combine:
                why.append("Path.GetFullPath(Path.Combine(…)) 之后")
            if bare_param:
                why.append("把形参 %s 直接拼进目录" % second)
            findings.append({
                "class": "⑤ 路径缺边界检查",
                "file": p.name,
                "line": code.count("\n", 0, m.start()) + 1,
                "detail": "%s，缺少「结果仍在目录内」的判定（StartsWith / 长度比较）：%s"
                          % ("；".join(why), code[m.start():close + 1][:70].replace("\n", " ")),
            })


def main():
    if len(sys.argv) < 2:
        print("用法: python cs_check.py <源码目录> [--json]")
        return 2
    root = Path(sys.argv[1])
    if not root.is_dir():
        print("目录不存在: %s" % root)
        return 2

    sources = collect_sources(root)
    findings = []
    check_naming(sources, findings)
    check_dead_branch(sources, findings)
    check_doc_tags(sources, findings)
    check_path_guard(sources, findings)
    check_combine_guard(sources, findings)

    problems = [f for f in findings if not f.get("registered")]
    registered = [f for f in findings if f.get("registered")]

    if "--json" in sys.argv:
        print(json.dumps(findings, ensure_ascii=False, indent=2))
        return 1 if problems else 0

    print("=" * 70)
    print("Auto7z 静态检查: %s" % root)
    print("扫描文件 %d 个" % len(sources))
    print("=" * 70)

    by_class = {}
    for f in problems:
        by_class.setdefault(f["class"], []).append(f)
    for cls in sorted(by_class):
        print()
        print("--- %s (%d) ---" % (cls, len(by_class[cls])))
        for f in by_class[cls]:
            print("  !! %s:%s  %s" % (f["file"], f["line"] or "-", f["detail"]))

    if registered:
        print()
        print("--- 已登记（不计失败）(%d) ---" % len(registered))
        for f in registered:
            print("  -- %s:%s  %s" % (f["file"], f["line"] or "-", f["detail"]))

    if not problems:
        print()
        print("(无问题)")

    print()
    print("TOTAL PROBLEMS: %d" % len(problems))
    print("REGISTERED: %d" % len(registered))
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
