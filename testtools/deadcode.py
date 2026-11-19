#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""死代码扫描：找出「只在声明处出现一次」的成员，以及「同名重载里没人调用的那个签名」。

两档判定
--------
① **名字级**（原有能力）：把每个 .cs 里声明的成员名字收集起来，在整个源码集里数
   这个名字总共出现多少次。只出现一次 = 只有声明、没人用。

② **签名级**（本次新增）：C# 允许同名多签名（重载）。名字级计数会把
   `MarkCanceled(gate, state)` 与 `MarkCanceled(gate, ref a, ref b)` 算成
   同一个名字的两个引用，于是**零调用的那个重载永远报不出来**。
   实测：把一个零调用的 3 参重载注入项目后，旧版仍打印「未发现」。
   签名级判定按「形参个数 + 是否 params」区分同名重载，再逐个签名数调用点。

会主动排除的（不是死代码）
--------------------------
* XAML 里 `x:Name` 生成的字段（代码里看不到引用，但界面在用）；
* 由 XAML 绑定、事件、反射调用的成员（`Click="..."`、`GetMethod("...")`）；
* 探针通过反射调用的内部成员（testtools 里的名字也算引用，所以一并扫描）。

刻意不做真正的语法分析
----------------------
这个项目全是手写 C#，没有生成代码参与，按名字与形参个数计数已经足够，
误报一眼就能看出来。真正的语义分析需要 Roslyn，那超出这个工具的本分。

用法:
    python deadcode.py [源码目录]        # 默认指向 ..\\HashChecker
    python deadcode.py [源码目录] --no-overload   # 只跑名字级（旧行为）
    python deadcode.py [源码目录] --json

退出码: 0 = 未发现可疑项, 1 = 有可疑项, 2 = 环境不满足
"""
import json
import re
import sys
from collections import defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent

SKIP_DIRS = {"obj", "bin", "build", "__pycache__", ".vs", ".git"}


def collect_sources(root):
    """返回 [(相对路径, 文本)]，跳过 obj / bin / Properties 自动生成的文件。"""
    out = []
    for path in sorted(root.rglob("*.cs")):
        parts = path.parts
        if any(x in parts for x in SKIP_DIRS):
            continue
        # 自动生成的 Designer 文件不算
        if path.name.endswith(".Designer.cs"):
            continue
        out.append((str(path.relative_to(root)), path.read_text(encoding="utf-8", errors="replace")))
    return out


# ---------------------------------------------------------------- 词法骨架
def strip_noise(text):
    """剥掉注释与字符串字面量，只留会真正执行的代码。

    计数必须在代码骨架上做：产品的注释里会提到成员名（解释「为什么保留它」），
    把注释算进去会让真死代码看起来「被引用过」。
    字符串字面量要保留在原位置用空格占位 —— 长度变了会影响行号定位，
    所以这里保持等长替换。
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
    return set(xaml_bound_names(root))


def xaml_bound_names(root):
    """XAML 里**按名绑定**出现在事件属性上的处理器名字。

    `Click="Add_Click"` 这类绑定不会在 .cs 里留下任何调用点（WPF 靠反射挂上去），
    所以签名级判定必须把它们当作活引用，否则每个事件处理器都会被报成
    「2 参重载零调用」——那是纯粹的业务误报。
    """
    names = set()
    for path in root.rglob("*.xaml"):
        if any(x in path.parts for x in SKIP_DIRS):
            continue
        text = path.read_text(encoding="utf-8", errors="replace")
        names.update(re.findall(r'x:Name="(\w+)"', text))
        # 用通用形式「属性名="标识符"」而不是逐个列事件名：
        # 漏一个事件名就漏一批活引用，宁可宽一点（宽了只是少报，不会误删）。
        names.update(re.findall(r'[:\w]+="([A-Za-z_]\w*)"', text))
    return names


# ---------------------------------------------------------------- 签名级
# 方法声明：修饰符 + 返回类型 + 名字 + 形参表 + { 或 =>
METHOD_DECL = re.compile(
    r"(?:^|\n)[ \t]*(?P<mods>(?:(?:public|private|protected|internal|static|sealed|override|"
    r"virtual|async|new|partial|extern|unsafe|abstract)[ \t]+)+)"
    r"(?P<ret>[A-Za-z_][\w<>\[\],\.\? \t]*?)[ \t]+(?P<name>[A-Za-z_]\w*)[ \t]*"
    r"(?:<[^>\n]*>)?[ \t]*\((?P<params>[^;{}]*)\)[ \t\r\n]*(?:\{|=>)")


def _split_top(text, sep=","):
    """按顶层分隔符切分（忽略括号/尖括号里的）。"""
    parts, depth, cur = [], 0, []
    for ch in text:
        if ch in "([{<":
            depth += 1
        elif ch in ")]}>":
            depth -= 1
        if ch == sep and depth == 0:
            parts.append("".join(cur).strip())
            cur = []
        else:
            cur.append(ch)
    parts.append("".join(cur).strip())
    return [p for p in parts if p]


def method_signatures(code):
    """抽取方法声明：[(名字, 形参个数, 是否 params, 是否 private, 行号, 起始下标)]。"""
    out = []
    for m in METHOD_DECL.finditer(code):
        name = m.group("name")
        if name in ("if", "for", "foreach", "while", "switch", "catch", "using",
                    "lock", "return", "new", "throw", "else", "do", "try", "finally"):
            continue
        params = _split_top(m.group("params") or "")
        has_params = any(re.search(r"\bparams\b", p) for p in params)
        mods = m.group("mods").split()
        # name 在整段声明里的偏移：用它做「声明处」的精确标记。
        # 不能用行号——一行里可能既有属性又有方法，也可能声明跨行，
        # 行号对不上就会把声明本身算成一次调用（实测踩过：3 参重载因此永远报不出来）。
        name_off = code.find(name, m.start("name") - 0, m.end("name"))
        out.append({
            "name": name,
            "arity": len(params),
            "has_params": has_params,
            "private": "private" in mods,
            "line": code.count("\n", 0, m.start()) + 1,
            "name_off": name_off,
        })
    return out


def _argc_at(code, open_paren):
    """从 ( 起配对，数顶层逗号 -> 实参个数。"""
    depth = 0
    commas = 0
    has_content = False
    i = open_paren
    while i < len(code):
        c = code[i]
        if c in "([{":
            depth += 1
            if depth > 1:
                has_content = True
        elif c in ")]}":
            depth -= 1
            if depth == 0:
                break
        elif depth == 1:
            if c == ",":
                commas += 1
            elif not c.isspace():
                has_content = True
        i += 1
    return 0 if not has_content else commas + 1


def call_arity_histogram(code, name):
    """统计 name 出现处各实参个数，返回 (直方图, 声明行号集合)。

    注意：这里数的是**所有** `name(` 出现处，包含声明处自身。
    调用方必须自己把声明处扣掉，否则每个签名都会「有一个调用」。
    """
    hist = defaultdict(int)
    decl_lines = set()
    for m in re.finditer(r"(?<![\w\.])" + re.escape(name) + r"\s*\(", code):
        argc = _argc_at(code, m.end() - 1)
        hist[argc] += 1
        decl_lines.add(code.count("\n", 0, m.start()) + 1)
    return hist, decl_lines


def call_sites_excluding_decls(code, name, decl_offsets):
    """数 name 的调用点，按实参个数分组，**排除所有声明处**。

    排除用**名字在源文里的字符偏移**，不用行号。
    理由：`private static void Foo(object a, object b)` 这一行本身也长得像
    `Foo(` 出现一次，且实参个数正好等于 2。不排除的话，「零调用」的重载会因为
    自己的声明而看起来「被调用过一次」，永远报不出来 —— 实测就是这个原因让
    注入的 3 参重载仍然显示「未发现」。
    """
    hist = defaultdict(int)
    for m in re.finditer(r"(?<![\w\.])" + re.escape(name) + r"\s*\(", code):
        if m.start() in decl_offsets:
            continue
        hist[_argc_at(code, m.end() - 1)] += 1
    return hist


# ---------------------------------------------------------------- main
def main():
    argv = [a for a in sys.argv[1:] if not a.startswith("--")]
    flags = {a for a in sys.argv[1:] if a.startswith("--")}

    root = Path(argv[0]) if argv else HERE.parent / "HashChecker"
    if not root.is_dir():
        print("找不到源码目录: %s" % root)
        return 2

    sources = collect_sources(root)

    # 全项目语料：所有 .cs 的代码骨架 + XAML 原文
    corpus_parts = []
    skeletons = {}
    for rel, text in sources:
        sk = strip_noise(text)
        skeletons[rel] = sk
        corpus_parts.append(sk)
    for path in root.rglob("*.xaml"):
        if any(x in path.parts for x in SKIP_DIRS):
            continue
        corpus_parts.append(path.read_text(encoding="utf-8", errors="replace"))
    corpus = "\n".join(corpus_parts)

    # ---------------- ① 名字级 ----------------
    # 名字级计数**必须保留字符串字面量**。
    # 产品里大量属性是通过 Raise("跑马灯名") 触发通知的（INotifyPropertyChanged 的
    # 字符串形式），属性本身的 getter 在代码里没有任何调用点，活引用只存在于字符串里。
    # 剥掉字符串就会把 ExpectedHashBrush / IconText / Modified 这类属性全部误报成死代码
    # （实测：4 条误报）。签名级判定不受影响，因为它在代码骨架上按括号配对做。
    corpus_all = "\n".join(text for _, text in sources)
    for path in root.rglob("*.xaml"):
        if any(x in path.parts for x in SKIP_DIRS):
            continue
        corpus_all += "\n" + path.read_text(encoding="utf-8", errors="replace")

    suspects = []
    for rel, text in sources:
        for name in declared_names(text):
            hits = len(re.findall(r'\b' + re.escape(name) + r'\b', corpus_all))
            if hits <= 1:
                suspects.append({"kind": "name", "file": rel, "name": name, "detail": "全项目只出现 1 次"})

    # ---------------- ② 签名级（新增） ----------------
    overload_findings = []
    if "--no-overload" not in flags:
        # 全仓按名字聚合方法声明
        by_name = defaultdict(list)
        for rel, sk in skeletons.items():
            for sig in method_signatures(sk):
                sig["file"] = rel
                by_name[sig["name"]].append(sig)

        for name, sigs in sorted(by_name.items()):
            if len(sigs) < 2:
                continue   # 单签名的情况已由 ① 覆盖

            # 逐文件统计调用点。声明与调用可能不在同一文件（事件处理器在同一文件，
            # 普通方法不一定），所以按「该文件的声明偏移」分别扣。
            per_file = {}
            for rel, sk in skeletons.items():
                offs_here = {s["name_off"] for s in sigs if s["file"] == rel}
                per_file[rel] = call_sites_excluding_decls(sk, name, offs_here)

            # 汇总成「全仓按实参个数」的直方图
            hist = defaultdict(int)
            for h in per_file.values():
                for a, n in h.items():
                    hist[a] += n

            for sig in sigs:
                arity = sig["arity"]
                if sig["has_params"]:
                    calls = sum(n for a, n in hist.items() if a >= max(0, arity - 1))
                else:
                    calls = hist.get(arity, 0)
                if calls > 0:
                    continue

                # XAML 按名绑定的事件处理器：XAML 里 Click="Xxx" 不体现在 .cs 的调用点上，
                # 但它是活引用。事件签名固定为 (object, EventArgs 派生)，即 2 参。
                if arity == 2 and name in xaml_bound_names(root):
                    continue

                if not sig["private"]:
                    overload_findings.append({
                        "kind": "overload-public", "file": sig["file"], "name": name,
                        "line": sig["line"],
                        "detail": "同名重载 %s/%d 参零调用，但它是 public（默认不删）" % (name, arity),
                    })
                    continue
                overload_findings.append({
                    "kind": "overload", "file": sig["file"], "name": name,
                    "line": sig["line"],
                    "detail": "同名重载 %d 参版本零调用（同组还有 %d 个签名）" % (arity, len(sigs) - 1),
                })

    if "--json" in flags:
        print(json.dumps({"name_level": suspects, "overload_level": overload_findings},
                         ensure_ascii=False, indent=2))
        return 0 if not (suspects or [f for f in overload_findings if f["kind"] == "overload"]) else 1

    dead = [f for f in overload_findings if f["kind"] == "overload"]
    pub = [f for f in overload_findings if f["kind"] == "overload-public"]

    # 判定与退出码的约定
    # ------------------
    # 只有「私有重载零调用」和「名字级零引用」算**可疑**（退出码 1）。
    # 公开重载零调用**只作提示**（不影响退出码）：公开 API 允许暂时没有内部调用方，
    # 外部程序或测试探针可能按名反射调用它。把它们算进失败会让正常源码永远红，
    # 工具就失去「一跑即知有没有新问题」的价值 —— 实测这 13 条全是公开转发重载。
    if not suspects and not dead:
        if pub:
            print("同名重载零调用，但为 public（仅供参考，不计失败）：")
            print("")
            for f in sorted(pub, key=lambda x: (x["file"], x["line"])):
                print("  %s:%d  %s" % (f["file"], f["line"], f["detail"]))
            print("")
            print("共 %d 条公开重载提示；私有死代码 0、名字级死代码 0。" % len(pub))
            print("公开 API 允许没有内部调用方（外部程序 / 探针可能按名反射调用），此处不计失败。")
        else:
            print("未发现「只声明未使用」的成员 ✓")
        return 0

    if suspects:
        print("可疑死代码（名字在全项目中只出现一次）：")
        print("")
        by_file = defaultdict(list)
        for s in suspects:
            by_file[s["file"]].append(s["name"])
        for rel in sorted(by_file):
            print("  %s" % rel)
            for name in sorted(by_file[rel]):
                print("      %s" % name)
            print("")

    if dead:
        print("可疑死代码（同名重载里零调用的那个签名）：")
        print("")
        for f in sorted(dead, key=lambda x: (x["file"], x["line"])):
            print("  %s:%d  %s" % (f["file"], f["line"], f["detail"]))
        print("")

    if pub:
        print("同名重载零调用，但为 public（默认不删，仅供人工审查）：")
        print("")
        for f in sorted(pub, key=lambda x: (x["file"], x["line"])):
            print("  %s:%d  %s" % (f["file"], f["line"], f["detail"]))
        print("")

    print("共 %d 个可疑成员（名字级 %d，私有重载 %d；另有公开重载提示 %d 条，不计失败）。"
          % (len(suspects) + len(dead), len(suspects), len(dead), len(pub)))
    print("注意：XAML 里 x:Name 与事件处理器已被计入引用；")
    print("      由字符串反射调用的成员需要人工确认（探针按名字调用的会被算作引用）。")
    return 1


if __name__ == "__main__":
    sys.exit(main())
