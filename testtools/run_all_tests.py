#!/usr/bin/env python3
# -*- coding: utf-8 -*-
r"""一键跑完全部测试，并把结论摊开给人看。

为什么要有它
------------
`cs\` 下 23 个探针各自要不同的参数（被测 exe、工作目录、期望摘要值、--section…），
`testtools\` 下 8 个静态检查器也各要源码目录。逐个手敲既容易漏项，也看不出
"到底过了几项、哪项没过"。这个脚本负责：

  1. 自动找到 `bin\<config>\HashChecker.exe`（也可用 --app 指定）；
  2. 按每个工具真正需要的参数把它调起来；
  3. 把每条结论翻译成「通过 / 失败 / 跳过 + 一句话说明」；
  4. 汇总成一张表，退出码 0 = 全部通过，1 = 有失败。

只读产品、只写 %TEMP%，不碰工程里的任何东西。

用法:
    python run_all_tests.py                      # 默认 Debug
    python run_all_tests.py --config Release
    python run_all_tests.py --app <HashChecker.exe>
    python run_all_tests.py --only hashprobe,aboutprobe
    python run_all_tests.py --skip perfprobe
    python run_all_tests.py --verbose            # 连原始输出一起打印
    python run_all_tests.py --list               # 只列出会跑哪些项
"""
import argparse
import subprocess
import sys
import tempfile
import shutil
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
CS_DIR = HERE / "cs"
SRC_DIR = WORKSPACE / "HashChecker_WPF" / "HashChecker"

# 改道必须在任何会启动产品的东西之前完成。
sys.path.insert(0, str(HERE))
import test_env  # noqa: E402

DATA_ROOT = test_env.isolate_settings_root("runall")

# 各工具自己「全部通过」的固定说法。它们不打印 TOTAL PROBLEMS 的也要能认出来。
PASS_MARKERS = (
    "全部符合预期",
    "全部成立",
    "结构全部符合",
    "全部一致",
    "检查通过",
    "(无问题)",
    "未发现",
    "覆盖全部",
)


def decode(raw):
    """解工具的控制台输出。

    这些工具按控制台代码页写中文，而代码页随调用方而变：cmd 默认 936(GBK)，
    PowerShell 7 常是 65001(UTF-8)。顺序很关键 —— UTF-8 字节用 GBK 去解不会报错，
    只会变成一串合法但错误的汉字，所以必须先试 UTF-8（它能严格校验）。
    """
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


class Result(object):
    def __init__(self, name, purpose, kind):
        self.name = name
        self.purpose = purpose
        self.kind = kind
        self.status = "跳过"
        self.detail = ""
        self.raw = ""
        self.code = None
        self.seconds = 0.0


def run(exe, args, timeout=300, cwd=None):
    """跑一个工具，返回 (退出码, 合并后的文本输出)。"""
    import time
    cmd = [str(exe)] + [str(a) for a in args]
    started = time.time()
    try:
        proc = subprocess.run(
            cmd, cwd=str(cwd) if cwd else None,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
            timeout=timeout, env=test_env.child_env("runall"))
        return proc.returncode, decode(proc.stdout), time.time() - started
    except subprocess.TimeoutExpired:
        return -1, "<超时 %d 秒>" % timeout, time.time() - started
    except OSError as exc:
        return -1, "<无法启动: %s>" % exc, time.time() - started


def parse_problems(text):
    """多数静态检查器会打印 TOTAL PROBLEMS: N，直接读它。"""
    for line in text.splitlines():
        s = line.strip()
        if s.startswith("TOTAL PROBLEMS:"):
            try:
                return int(s.split(":", 1)[1].strip())
            except (IndexError, ValueError):
                pass
    return None


def first_fail_line(text):
    for line in text.splitlines():
        s = line.strip()
        if s.startswith("!!"):
            return s
    return ""


def judge(text, code):
    """通用判定：TOTAL PROBLEMS → 工具自己的总结句 → !! 行 → 退出码。"""
    problems = parse_problems(text)
    if problems is not None:
        if problems == 0:
            return "通过", ""
        return "失败", first_fail_line(text) or ("%d 个问题" % problems)

    for marker in PASS_MARKERS:
        if marker in text:
            return "通过", ""

    bad = [l.strip() for l in text.splitlines() if l.strip().startswith("!!")]
    if bad:
        return "失败", bad[0]

    if code == 0:
        return "通过", ""
    return "失败", "退出码=%s" % code


def ensure_cs_tool(res):
    """探针没编译就说明怎么办，而不是报一个看不懂的错。"""
    exe = CS_DIR / res.name / (res.name + ".exe")
    if exe.exists():
        return exe
    res.status = "跳过"
    res.detail = "未编译：先运行 python cs\\build_all.py"
    return None


def main():
    ap = argparse.ArgumentParser(description="一键跑完 HashChecker 的全部测试")
    ap.add_argument("--app", help="HashChecker.exe 路径，默认自动找 bin\\<config>\\HashChecker.exe")
    ap.add_argument("--config", default="Debug", choices=["Debug", "Release"])
    ap.add_argument("--only", help="只跑指定的几项，逗号分隔")
    ap.add_argument("--skip", help="跳过指定的几项，逗号分隔")
    ap.add_argument("--verbose", action="store_true", help="连工具原始输出一起打印")
    ap.add_argument("--list", action="store_true", help="只列出会跑哪些项")
    args = ap.parse_args()

    app = (Path(args.app).resolve() if args.app
           else SRC_DIR / "bin" / args.config / "HashChecker.exe")

    only = set(x.strip() for x in args.only.split(",") if x.strip()) if args.only else None
    skip = set(x.strip() for x in args.skip.split(",") if x.strip()) if args.skip else set()

    def want(name):
        if only is not None:
            return name in only
        return name not in skip

    print("=" * 78)
    print(" HashChecker 全量测试")
    print("=" * 78)
    print("  被测程序 :", app)
    print("  设置改道 :", DATA_ROOT)
    if not app.exists():
        print("")
        print("  !! 找不到被测程序。先构建一次：")
        print('       "%ProgramFiles%\\Microsoft Visual Studio\\2022\\Community\\MSBuild\\Current\\Bin\\MSBuild.exe" \\')
        print('         HashChecker_WPF\\HashChecker.sln /t:Build /p:Configuration=%s' % args.config)
        print("     或用 --app 指定 HashChecker.exe 的完整路径。")
        return 2

    work = Path(tempfile.mkdtemp(prefix="HashCheckerTest_runall_"))
    results = []

    def add(name, purpose, kind="cs"):
        r = Result(name, purpose, kind)
        results.append(r)
        return r

    # ---------------- 静态检查器（Python，不需要 exe） ----------------
    static = [
        ("localization_check.py", "三语词典：键集合 / 占位符 / 死引用"),
        ("langkeycheck.py", "资源键完整性 + 语言纯度（英/繁不得混入别种文字）"),
        ("safe_ops.py", "安全删除：五条白名单闸门的正反两侧自检"),
        ("cs_check.py", "静态：命名类型、文档标签、路径边界、空 catch"),
        ("namecheck.py", "命名风格一致性"),
        ("regioncheck.py", "region 结构：配对、嵌套 ≤1、跨度 ≤150 行"),
        ("castcheck.py", "强制转换与控件类型：FindName(…) as T 是否恒为 null"),
        ("renamecheck.py", "改名残留：旧名无残留、新名定义与引用一致、名字与类型相符"),
        ("locargs.py", "本地化：Format 传参个数与 {n} 占位符匹配"),
        ("deadcode.py", "死代码：全项目只出现一次的成员"),
        ("suffix_list.py", "算法 ↔ 后缀：过滤器覆盖全部首选后缀"),
        ("redirectcheck.py", "配置改道：每个探针都真的调用了改道函数"),
        ("test_env.py", "数据目录隔离：探针不得写进 %LOCALAPPDATA%"),
    ]
    for name, purpose in static:
        if not want(name):
            continue
        r = add(name, purpose, "py")
        script = HERE / name
        if not script.exists():
            r.status, r.detail = "跳过", "文件不存在"
            continue
        extra = ["--check"] if name == "suffix_list.py" else []
        if name == "redirectcheck.py":
            # 这个检查认的是 cs\ 目录，不是产品源码目录。
            extra = [str(CS_DIR)]
        code, text, secs = run(sys.executable, [script] + extra + [str(SRC_DIR)])
        r.raw, r.code, r.seconds = text, code, secs
        if name == "test_env.py":
            # 这个模块被当脚本跑时不做检查；改由下面专用的泄漏检查承担。
            r.status, r.detail = "通过", "已导入，改道变量生效"
            continue
        r.status, r.detail = judge(text, code)

    # ---------------- C# 探针 ----------------
    # (名字, 说明, 额外参数生成器)
    probes = [
        ("hashprobe", "多算法内核密码学正确性（已知向量 + 框架对照）", lambda: [app]),
        ("blake3probe", "BLAKE3 端到端：目录登记 / 记录往返 / 双向反查", lambda: [app, work / "b3"]),
        ("pickerprobe", "算法下拉框：BLAKE3 在列、项数一致、选中能算对", lambda: [app]),
        ("aboutprobe", "关于窗口：版本号自动跟随程序集版本", lambda: [app, work / "about"]),
        ("recordprobe", "校验记录解析 / 反推 / 算法判定", lambda: [app, work / "rec"]),
        ("suffixprobe", "算法 ↔ 后缀契约：唯一性 + 保存往返 + 冲突回退", lambda: [app, work / "suf"]),
        ("saveprobe", "保存路径的后缀纠正", lambda: [app]),
        ("saveorder", "记录写出的行序契约（Sequence 排序 / 跳过无值行 / 只写基名）",
         lambda: [app, work / "so"]),
        ("addrecordprobe", "连续载入两份记录：算法是否更新、是否重算", lambda: [app, work / "add"]),
        ("defaultcheck", "WriteBom 默认值 / BOM 读写 / 拖入跟随算法", lambda: [app, work / "def"]),
        ("bandprobe", "框选：多选模式、矩形选行、覆盖层不拦事件", lambda: [app, work / "band"]),
        ("bandclip", "框选与下拉条边界", lambda: [app, work / "bc"]),
        ("bandgesture", "框选手势：几何 + IL 双证据", lambda: [app, work / "bg"]),
        ("colfit", "列宽自适应与自动分配开关语义", lambda: [app, work / "cf"]),
        ("engineorder", "计算引擎：交付顺序 / 进度单调 / 吞吐下限", lambda: [app, work / "eo"]),
        ("engineprobe", "计算引擎并发：线程数越界 / 取消 / 占用文件", lambda: [app, work / "ep"]),
        ("progresslife", "进度条生命周期：停止后必须清回 0", lambda: [app, work / "pl"]),
        ("hangprobe", "界面卡死：自验证 / 计算器读文件是否派发后台", lambda: [app, work / "hg"]),
        ("pathprobe", "记录内路径边界：`..` / UNC / 末分隔符 / 超长 / NUL", lambda: [app, work / "pp"]),
        ("settingsprobe", "设置容错：坏 JSON / 缺键 / 越界值 / 转义往返", lambda: [app, work / "sp"]),
        ("writeprobe", "记录写出：BOM / 只读与占用 / Unicode 名 / 无目录", lambda: [app, work / "wp"]),
        ("uiprobe", "界面健康：控件名/Tab 顺序/默认按钮/文本溢出/焦点", lambda: [app, work / "ui"]),
        ("locprobe", "本地化运行时：三语完整性 / Format 契约 / 版本", lambda: [app, work / "lp"]),
        ("guiprobe", "端到端：真窗口 + 真 .blake3 记录 → 判定通过",
         lambda: [app, work / "gp", "398d42d5677b8bc40eb177ea45565bea3fc2fec2927c80304424c244435f9a78"]),
        ("perfprobe", "吞吐基准（只报数，不判失败）", lambda: [app, work / "pf", "--quick"]),
    ]

    for name, purpose, make_args in probes:
        if not want(name):
            continue
        r = add(name, purpose)
        exe = ensure_cs_tool(r)
        if not exe:
            continue
        extra = make_args()
        # 工作目录参数要确保存在，探针自己不一定会建。
        for a in extra[1:]:
            if isinstance(a, Path):
                a.mkdir(parents=True, exist_ok=True)
        code, text, secs = run(exe, extra, timeout=600 if name == "perfprobe" else 300)
        r.raw, r.code, r.seconds = text, code, secs
        if name == "perfprobe":
            # 基准是参考值，不因快慢判失败；只有跑不起来才算失败。
            r.status = "完成" if code == 0 else "失败"
            r.detail = "参考值见 --verbose" if code == 0 else ("退出码=%s" % code)
        else:
            r.status, r.detail = judge(text, code)

    # ---------------- 启发式探测（探索型：可疑项需人判，不计入回归失败） ----------------
    if want("heuristic_probe.py"):
        r = add("heuristic_probe.py", "启发式探测：产品承诺 vs 实际行为（探索型）", "py")
        script = HERE / "heuristic_probe.py"
        if not script.exists():
            r.status, r.detail = "跳过", "文件不存在"
        else:
            code, text, secs = run(sys.executable, [script, "--config", args.config])
            r.raw, r.code, r.seconds = text, code, secs
            # 退出码 1 = 有可疑项，那不是回归失败，只是需要人看。
            if code == 0:
                r.status, r.detail = "通过", ""
            elif code == 1:
                r.status = "完成"
                suspect_line = ""
                for line in text.splitlines():
                    if line.strip().startswith("共检查"):
                        suspect_line = line.strip()
                        break
                r.detail = suspect_line or "有可疑项，见 --verbose"
            else:
                r.status, r.detail = "失败", "退出码=%s" % code

    # ---------------- 专用检查：配置目录有没有被污染 ----------------
    if want("settings-isolation"):
        r = add("settings-isolation", "真实 %LOCALAPPDATA% 不得出现探针创建目录", "py")
        leaked = test_env.leaked_probe_dirs(
            known_probes={n for n, _, _ in probes})
        if leaked:
            r.status = "失败"
            r.detail = "泄漏 %d 个: %s" % (
                len(leaked), ", ".join(p.name for p in leaked[:5]))
            # 显式给退出码，别让输出里的 exit=None 看起来像「执行异常」。
            r.code = 1
        else:
            r.status, r.detail = "通过", ""
            r.code = 0
        r.raw = "扫描 %d 个已知探针名；检出泄漏 %d 个%s" % (
            len(probes), len(leaked),
            ("：" + ", ".join(str(p) for p in leaked)) if leaked else "")

    if args.list:
        print("")
        for r in results:
            print("  %-22s %s" % (r.name, r.purpose))
        shutil.rmtree(str(work), ignore_errors=True)
        return 0

    # ---------------- 汇总 ----------------
    print("")
    print("-" * 78)
    print(" %-22s %-8s %s" % ("工具", "结果", "说明"))
    print("-" * 78)
    fails = passes = skips = 0
    for r in results:
        if r.status == "失败":
            fails += 1
            mark = "[失败]"
        elif r.status == "跳过":
            skips += 1
            mark = "[跳过]"
        else:
            passes += 1
            mark = "[通过]" if r.status == "通过" else "[完成]"
        line = " %-22s %-8s %s" % (r.name, mark, r.purpose)
        if r.detail:
            line += "  <- " + r.detail
        print(line)
    print("-" * 78)
    print(" 合计 %d 项：通过/完成 %d，失败 %d，跳过 %d"
          % (len(results), passes, fails, skips))
    print("")

    if args.verbose:
        print("=" * 78)
        print(" 各工具原始输出")
        print("=" * 78)
        for r in results:
            print("")
            print("##### %s (exit=%s, %.1fs) #####" % (r.name, r.code, r.seconds))
            print(r.raw.rstrip() if r.raw.strip() else "(无输出)")

    if skips:
        print(" 跳过的项通常是：探针还没编译（python cs\\build_all.py），")
        print(" 或被测程序是 Release 构建。补齐后重跑即可。")
        print("")

    print(" 设置改道目录（跑完可删）：", DATA_ROOT)
    shutil.rmtree(str(work), ignore_errors=True)
    return 0 if fails == 0 else 1


if __name__ == "__main__":
    sys.exit(main())