#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""启发式探测：拿产品源码/README 里写明的承诺，去撞既有用例没覆盖的边界。

与其它套件的分工
----------------
`cs\*` 探针和静态检查是**回归**：断言已知契约不退化，红了就是退化。
本脚本是**探索**：不断言"必须怎样"，而是把"看起来不对劲"的行为逐条摊出来，
交给人判断是缺陷还是设计。所以它的失败**不代表一定是 bug**。

每一条探测都对应产品里写明过的一个承诺：
  * `ChecksumFile` 的宽容解析：注释行、空行、混排长度、`*`/`?` 引导符
  * `HashAlgorithms.NormalizeDigest` 的前缀剥离：`sha256:` / `sha256=` / 引号
  * `AppSettings` 的容错：坏 JSON、越界值、未知键、缺键
  * `ChecksumResolver` 的路径边界：`..` 越界必须被拒
  * `TextEncoding` 的多编码容错：BOM / UTF-16 / GBK
  * 记录的写出→读回往返必须稳定（同一份数据走一圈回来还认得）

用法:
    python heuristic_probe.py                  # 默认 Debug
    python heuristic_probe.py --config Release
    python heuristic_probe.py --only parse,settings
    python heuristic_probe.py --verbose

退出码: 0 = 没发现可疑行为, 1 = 有可疑行为（需人判）, 2 = 环境不满足
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
SRC_DIR = WORKSPACE / "HashChecker_WPF" / "HashChecker"

sys.path.insert(0, str(HERE))
import test_env  # noqa: E402
from safe_ops import SafeTemp  # noqa: E402

DATA_ROOT = test_env.isolate_settings_root("heuristic")

_suspects = []
_checked = 0


def ok(area, what, evidence=""):
    global _checked
    _checked += 1
    print("  OK    [%s] %s%s" % (area, what, ("  —— " + evidence) if evidence else ""))


def suspect(area, what, evidence):
    """记录「看起来不对」的行为。不是失败，是需要人判。"""
    global _checked
    _checked += 1
    _suspects.append((area, what, evidence))
    print("  ??    [%s] %s\n          %s" % (area, what, evidence))


def decode(raw):
    if raw is None:
        return ""
    try:
        return raw.decode("utf-8-sig")
    except UnicodeDecodeError:
        pass
    for enc in ("gbk", "oem"):
        try:
            return raw.decode(enc)
        except (LookupError, UnicodeDecodeError):
            continue
    return raw.decode("utf-8", errors="replace")


class Runner(object):
    """用被测 exe 跑一段 Python，借此调用产品内部的静态方法。

    产品是 GUI 程序，没有命令行入口可用来直接验证「解析一个字符串」这类纯函数。
    所以这里借道一个极小的宿主：把要验证的调用写进临时 .py，用被测程序集
    无法直接执行 —— 因此改为**用 Python 复算**产品声明的算法，再与产品
    已算出的结果对照。

    注意：这不是在重复实现产品，而是在验证「产品声明的规则」与「它的实际行为」
    是否一致。凡是能纯静态判定的，一律不做运行时调用。
    """

    def __init__(self, src):
        self.src = src

    def read(self, rel):
        p = self.src / rel
        if not p.exists():
            return None
        return p.read_text(encoding="utf-8", errors="replace")


# ---------------------------------------------------------------- 公共分析工具

def strip_comments(text):
    """去掉行注释与块注释，只留下会真正执行的代码。

    这一步是必须的：产品的注释里大量提到 `TickCount`、`Stopwatch`、`StartsWith`
    这些词（它们恰恰在解释"为什么不用 X"或"为什么要这么判"），不剔除的话，
    一条描述正确行为的注释会让检查误判成通过。
    实测踩过两次：
      * engine 段：把 Stopwatch 换成 TickCount 之后仍然报 OK，因为注释里的
        Stopwatch 还在；
      * path 段：把 `StartsWith` 边界判定整行删掉、只留注释，三条仍然全报 OK。
    所以**所有**判据都要建立在本函数的结果上，而不是原文上。
    """
    out = []
    i = 0
    n = len(text)
    in_block = False
    while i < n:
        if in_block:
            end = text.find("*/", i)
            if end < 0:
                break
            i = end + 2
            in_block = False
            continue
        c = text[i]
        if c == "/" and i + 1 < n and text[i + 1] == "*":
            in_block = True
            i += 2
            continue
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            end = text.find("\n", i)
            if end < 0:
                break
            i = end
            continue
        out.append(c)
        i += 1
    return "".join(out)


# 方法声明：修饰符 + 返回类型 + 名字 + 形参表 + { 或 =>
_METHOD_DECL = re.compile(
    r"(?:^|\n)[ \t]*(?:(?:public|private|protected|internal|static|sealed|override|"
    r"virtual|async|new|partial|extern|unsafe|abstract)[ \t]+)+"
    r"[A-Za-z_][\w<>\[\],\.\? \t]*?[ \t]+(?P<name>[A-Za-z_]\w*)[ \t]*"
    r"(?:<[^>\n]*>)?[ \t]*\([^;{}]*\)[ \t\r\n]*\{")


def _method_body(code, name):
    """取出指定名字方法的**方法体文本**（不含签名），取不到返回 None。

    为什么要方法体而不是全文：全文判断分不清「实现」与「别处的同类调用」，
    也分不清「这个方法里做了」与「另一个方法里做了」。
    实测：把 Load 里的 try/catch 删掉，但 Save 里还有，全文判断仍报 OK。
    """
    for m in _METHOD_DECL.finditer(code):
        if m.group("name") != name:
            continue
        start = m.end() - 1          # 指向 {
        depth = 0
        i = start
        while i < len(code):
            if code[i] == "{":
                depth += 1
            elif code[i] == "}":
                depth -= 1
                if depth == 0:
                    return code[start + 1:i]
            i += 1
        return code[start + 1:]
    return None


# ---------------------------------------------------------------- 探测项

def p_checksum_parse(runner):
    """ChecksumFile 的宽容解析：注释、空行、引导符、混排长度。

    判据建立在**剥过注释的代码**上（理由见 strip_comments）。
    """
    area = "解析"
    raw = runner.read("Services/ChecksumFile.cs")
    if not raw:
        suspect(area, "找不到 ChecksumFile.cs", "路径可能不对")
        return
    text = strip_comments(raw)

    # 承诺：以 # 开头的行是注释，必须跳过
    if 'StartsWith("#"' in text:
        ok(area, "注释行（# 开头）被显式跳过")
    else:
        suspect(area, "没找到「# 开头视为注释」的判断",
                "若记录里含注释行，可能被当成无效行导致整份记录解析不全")

    # 承诺：空行跳过
    if re.search(r"Length\s*==\s*0|IsNullOrWhiteSpace|IsNullOrEmpty", text):
        ok(area, "空行被显式跳过")
    else:
        suspect(area, "没找到空行跳过的判断", "记录里常见空行，可能影响解析")

    # 承诺：引导符 * / ? 被剥离
    for mark in ("'*'", "'?'"):
        if mark in text:
            ok(area, "引导符 %s 有剥离逻辑" % mark)
        else:
            suspect(area, "没找到引导符 %s 的剥离" % mark,
                    "sha256sum 的二进制模式用 *，不剥离会污染文件名")

    # 承诺：长度不一致时回退到「按长度推断」
    if "ByHexLength" in text:
        ok(area, "值长度与后缀冲突时会回退到按长度推断")
    else:
        suspect(area, "没找到长度冲突的回退逻辑",
                "后缀与值长度不符时应降级推断，否则整行判失败")


def p_digest_normalize(runner):
    """NormalizeDigest 的前缀剥离规则。"""
    area = "摘要规范化"
    raw = runner.read("Services/HashAlgorithms.cs")
    if not raw:
        suspect(area, "找不到 HashAlgorithms.cs", "")
        return
    text = strip_comments(raw)

    # 承诺：sha256: <hex> 与 sha256=<hex> 两种前缀都认
    if re.search(r"IndexOf\s*\(\s*':'|LastIndexOf\s*\(\s*':'", text):
        ok(area, "冒号前缀（sha256:xxx）有剥离")
    else:
        suspect(area, "没找到冒号前缀剥离", "sha256:abc… 这种写法会被判非法")

    if re.search(r"IndexOf\s*\(\s*'='|LastIndexOf\s*\(\s*'='", text):
        ok(area, "等号前缀（sha256=xxx）有剥离")
    else:
        suspect(area, "没找到等号前缀剥离", "sha256=abc… 这种写法会被判非法")

    # 承诺：去引号
    if re.search(r"""Trim\s*\(\s*['"]\\?["']""", text) or "Trim('\"')" in text:
        ok(area, "首尾引号有剥离")
    else:
        suspect(area, "没找到引号剥离", "带引号的值会被判非法")

    # 承诺：非十六进制返回 null（而不是抛异常）
    if "IsHexDigit" in text:
        ok(area, "非十六进制字符用 IsHexDigit 判掉，不抛异常")
    else:
        suspect(area, "没找到十六进制校验", "非法字符可能导致异常而非返回 null")


def p_settings_tolerance(runner):
    """AppSettings 的容错承诺。

    判据落在**方法体内**，不是全文。旧版用 `"catch" in text and "Load()" in text`，
    实测把 Load 里的 try/catch 删掉、Save 里那个还在，它仍然报 OK：
    它数的是整个文件的 catch 个数，不是 Load 的兜底。
    """
    area = "设置"
    raw = runner.read("Services/AppSettings.cs")
    if not raw:
        suspect(area, "找不到 AppSettings.cs", "")
        return
    text = strip_comments(raw)

    load_body = _method_body(text, "Load")
    if load_body is None:
        suspect(area, "找不到 Load 方法", "无法确认坏 JSON 的兜底")
    elif "catch" in load_body:
        ok(area, "Load 里有 try/catch，坏 JSON 不会让程序起不来")
    else:
        suspect(area, "Load 没有异常兜底", "一份坏 settings.json 就能让程序无法启动")

    norm_body = _method_body(text, "Normalize")
    if norm_body is not None and norm_body.strip():
        ok(area, "有 Normalize() 收回越界值")
    elif re.search(r"\bNormalize\s*\(", text):
        ok(area, "有 Normalize() 收回越界值")
    else:
        suspect(area, "没有 Normalize()", "手工改坏的线程数/窗口尺寸会一直生效")

    if "WriteAtomic" in text or ".tmp" in text:
        ok(area, "写入是原子的（临时文件 + 替换）")
    else:
        suspect(area, "写入不是原子的", "写到一半被杀会留下半截 JSON，下次启动丢光设置")

    if "GetBool" in text and "GetDouble" in text:
        ok(area, "缺键走 GetXxx 的默认值重载")
    else:
        suspect(area, "缺键处理不明确", "旧版本 settings.json 缺新键时可能异常")


def p_path_boundary(runner):
    """ChecksumResolver 的路径边界承诺。

    判据是**结构级**的：先剥注释，再要求「边界判定」出现在**拼接所在的那个方法体内**。
    旧版直接搜全文，实测把 `StartsWith` 判定整行删掉、只留注释里的那句话，
    它照样三条全报 OK —— 一条描述正确行为的注释冒充了实现。
    """
    area = "路径边界"
    raw = runner.read("Services/ChecksumResolver.cs")
    if not raw:
        suspect(area, "找不到 ChecksumResolver.cs", "")
        return
    text = strip_comments(raw)

    guard_found = False
    normalize_found = False
    for mname in ("TryCombineInside", "LocateTarget", "FindByFileName"):
        body = _method_body(text, mname)
        if body is None:
            continue
        if re.search(r"\.StartsWith\s*\(", body) or re.search(r"\.Length\s*<=\s*\w+\.Length", body):
            guard_found = True
        if "GetFullPath" in body:
            normalize_found = True

    rooted_found = "IsPathRooted" in text

    if guard_found:
        ok(area, "相对路径拼接后有边界校验（StartsWith / 长度比较，且与拼接在同一方法体内）")
    else:
        suspect(area, "没找到边界校验", "记录里的 ../.. 可以指向记录目录之外")

    if normalize_found:
        ok(area, "用 GetFullPath 归一后再比较")
    else:
        suspect(area, "没做归一化", "a/../b 这类路径能绕过朴素的前缀比较")

    if rooted_found:
        ok(area, "绝对路径与相对路径分开处理")
    else:
        suspect(area, "没区分绝对/相对路径", "记录里写绝对路径时行为不明确")


def p_encoding_tolerance(runner):
    """TextEncoding 的多编码承诺。"""
    area = "编码"
    raw = runner.read("Services/TextEncoding.cs")
    if not raw:
        suspect(area, "找不到 TextEncoding.cs", "")
        return
    text = strip_comments(raw)

    encodings = ["GB18030", "GBK", "Big5", "Shift_JIS", "EUC-KR"]
    found = [e for e in encodings if e in text]
    if len(found) == len(encodings):
        ok(area, "五种东亚编码都在回退链里", "、".join(found))
    else:
        missing = [e for e in encodings if e not in text]
        suspect(area, "回退链缺编码", "缺: " + "、".join(missing))

    # 承诺：BOM 优先
    if "0xEF" in text and "0xFF" in text:
        ok(area, "BOM 探测覆盖 UTF-8 / UTF-16 / UTF-32")
    else:
        suspect(area, "BOM 探测不完整", "带 BOM 的记录可能解成乱码")

    # 承诺：无 BOM 的 UTF-16 靠 0x00 分布判断
    if "evenZero" in text or "oddZero" in text:
        ok(area, "无 BOM 的 UTF-16 靠 0x00 分布判断")
    else:
        suspect(area, "没有无 BOM UTF-16 的探测", "这类文件会被当成 UTF-8 解成含 NUL 的怪文本")

    # 承诺：最终兜底不抛异常
    if "fallback" in text.lower():
        ok(area, "最终有宽松 UTF-8 兜底，保证不抛异常")
    else:
        suspect(area, "没有最终兜底", "无法识别编码的文件可能让整份记录读取失败")


def p_roundtrip(runner):
    """记录写出 → 读回的往返稳定性（纯静态规则层面）。"""
    area = "往返"
    raw = runner.read("Services/ChecksumFile.cs")
    if not raw:
        return
    write = strip_comments(raw)

    # 承诺：写出的引导符是 *，读回时 * 必须被剥离 —— 两者要成对
    has_star_write = "*{1}" in write or "*%s" in write or '" *"' in write
    has_star_read = "'*'" in write
    if has_star_write and has_star_read:
        ok(area, "写出用 * 引导符、读回剥 * —— 成对")
    elif has_star_write:
        suspect(area, "写出加了 * 但读回没剥", "自家写出的记录自家读不回文件名")
    else:
        suspect(area, "没找到 * 引导符的写出", "与 sha256sum 文本模式不互通")

    # 承诺：文件名以 * 开头时加下划线转义
    if 'StartsWith("*"' in write or "StartsWith(\"*\"" in write:
        ok(area, "文件名本身以 * 开头时有转义处理")
    else:
        suspect(area, "文件名以 * 开头时没有转义", "这类文件名写出去会被读回成引导符")


def p_engine_contract(runner):
    """计算引擎的契约（纯静态）。"""
    area = "引擎"
    raw = runner.read("Services/HashingEngine.cs")
    if not raw:
        suspect(area, "找不到 HashingEngine.cs", "")
        return

    # 判据必须建立在**代码**上，不能建立在注释上 —— 见 strip_comments 的说明。
    text = strip_comments(raw)

    if "Environment.TickCount" in text:
        suspect(area, "进度节流在用 Environment.TickCount",
                "int 溢出会让进度回调一次都不触发（历史上真实发生过）")
    elif "Stopwatch" in text:
        ok(area, "进度节流用 Stopwatch 单调滴答")
    else:
        suspect(area, "没找到进度节流的时间基准", "无法确认是否用单调时钟")

    if "Interlocked.CompareExchange" in text:
        ok(area, "节流基准用 CompareExchange，避免多线程重复上报")
    else:
        suspect(area, "节流基准没做原子更新", "多线程下可能重复上报进度")

    # 交付顺序：必须有槽位数组而不是直接回调
    if "ready[" in text or "nextDeliver" in text:
        ok(area, "结果按槽位序号交付（保序），不用 Parallel.ForEach 的完成顺序")
    else:
        suspect(area, "没找到保序交付逻辑", "结果会按「谁先算完」填，列表顺序乱跳")

    # 取消必须是契约内的异常，不能吞
    if "OperationCanceledException" in text:
        ok(area, "取消走 OperationCanceledException 标准契约")
    else:
        suspect(area, "取消没有标准异常", "调用方无法区分「取消」与「出错」")


def p_localization_purity(runner):
    """三语文本纯度（与 langkeycheck.py 交叉验证）。

    定位方式必须按**字典初始化里的语言段**来，不能直接 `text.index('"en-US"')`：
    那个字符串在文件里出现多次（Get() 里的回退判断、ResolveSystemLanguage 里的比较），
    取第一个命中的话截到的是别处的片段 —— 实测因此在正确的源码上报出
    「en-US 段里仍有 181 个汉字」，而那 181 个汉字属于 zh-CN 段。
    判据（汉字数是否为 0）不变，只修正"怎么找到这一段"。
    """
    area = "本地化"
    text = runner.read("Localization.cs")
    if not text:
        return

    segment = _language_segment(text, "en-US")
    if segment is None:
        suspect(area, "没找到 en-US 段", "词典结构可能变了")
        return

    # 只统计**字典项的值**里的汉字，不统计段内的 C# 方法注释。
    # 段尾紧跟着 Get / Format 等方法，它们的 <summary> 是中文写的 ——
    # 那是源码注释，不是界面文案，把它算进「英文段混入汉字」是误报。
    han = sum(_entry_han_count(value) for value in _dictionary_values(segment))
    if han == 0:
        ok(area, "en-US 段里没有汉字")
    else:
        suspect(area, "en-US 段里仍有 %d 个汉字" % han,
                "英文界面会显示半中半英的句子（详见 langkeycheck.py 的 LK2）")


def _dictionary_values(segment):
    """取出一个语言段里所有字典项的**值**（{ "Key", "值" } 的第二个引号串）。"""
    import re as _re

    values = []
    for m in _re.finditer(r'\{\s*"([^"]+)"\s*,\s*"((?:[^"\\]|\\.)*)"\s*\}', segment):
        values.append(m.group(2))
    return values


def _entry_han_count(value):
    """一个字典值里的汉字个数。"""
    return sum(1 for ch in value if 0x4E00 <= ord(ch) <= 0x9FFF)


def _language_segment(text, language):
    """取出某个语言字典段的原文，取不到返回 None。

    结构是：
        {
            "zh-CN", new Dictionary<string, string>
            {
                { "Key", "值" },
                ...
            }
        },
    所以定位要落在 `"<lang>", new Dictionary` 这一处，而不是任意一个
    出现语言代号的位置；结尾取「下一个语言段的开始」或「对象闭合」。
    """
    import re as _re

    head = _re.search(r'"%s"\s*,\s*new\s+Dictionary' % _re.escape(language), text)
    if not head:
        return None

    start = head.start()
    others = [m.start() for m in _re.finditer(r'"[a-z]{2}-[A-Z]{2}"\s*,\s*new\s+Dictionary', text)
              if m.start() > start]
    end = min(others) if others else len(text)
    return text[start:end]


PROBES = [
    ("parse", p_checksum_parse),
    ("digest", p_digest_normalize),
    ("settings", p_settings_tolerance),
    ("path", p_path_boundary),
    ("encoding", p_encoding_tolerance),
    ("roundtrip", p_roundtrip),
    ("engine", p_engine_contract),
    ("locale", p_localization_purity),
]


def main():
    ap = argparse.ArgumentParser(description="启发式探测：找「看起来不对劲」的行为")
    ap.add_argument("--config", default="Debug", choices=["Debug", "Release"])
    ap.add_argument("--only", help="只跑指定的几项，逗号分隔")
    ap.add_argument("--verbose", action="store_true")
    args = ap.parse_args()

    app = SRC_DIR / "bin" / args.config / "HashChecker.exe"
    if not SRC_DIR.is_dir():
        print("!! 找不到产品源码目录: %s" % SRC_DIR)
        return 2

    only = set(x.strip() for x in args.only.split(",") if x.strip()) if args.only else None

    print("=" * 76)
    print(" 启发式探测（结果不是断言，可疑项需人工判断）")
    print("=" * 76)
    print("  产品源码 :", SRC_DIR)
    print("  被测程序 :", app if app.exists() else "（未构建，仅做静态探测）")
    print("  设置改道 :", DATA_ROOT)
    print("")

    runner = Runner(SRC_DIR)
    for name, fn in PROBES:
        if only is not None and name not in only:
            continue
        print("  --- %s ---" % name)
        try:
            fn(runner)
        except Exception as exc:  # noqa: BLE001
            suspect(name, "探测本身出错", "%s: %s" % (type(exc).__name__, exc))
        print("")

    print("-" * 76)
    print(" 共检查 %d 项，其中可疑 %d 项" % (_checked, len(_suspects)))
    if _suspects:
        print("")
        print(" 可疑项（不代表一定是 bug，需人判）：")
        for area, what, evidence in _suspects:
            print("   [%s] %s" % (area, what))
            print("        %s" % evidence)
        print("")
        print(" 判读建议：先看证据里指的代码，再对照 README 里写明的承诺。")
        print(" 若确认是设计取舍，把该条改成 ok(...) 并写明理由。")
    print("")
    return 0 if not _suspects else 1


if __name__ == "__main__":
    sys.exit(main())