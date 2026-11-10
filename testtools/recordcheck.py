#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""校验记录解析 / 反推的运行时检查（调用 cs\\recordprobe\\recordprobe.exe）。

覆盖三条链路：
  1. 宽容解码：UTF-8 / UTF-8 BOM / UTF-16（有无 BOM）/ UTF-32 / GB18030 / GBK / Big5；
  2. 记录 → 反推文件：ASCIIPlayer.sha256 这种记录要能找到 ASCIIPlayer.exe；
  3. 文件 → 查记录：传入文件时找到引用它的记录，不要求记录文件名与之相同。

加上「文件名少写扩展名」与「不该被当成记录」两类负向/边界检查。

用法:
    python recordcheck.py                     # 用默认的 Debug 构建
    python recordcheck.py --app <exe>         # 指定被测程序
    python recordcheck.py --config Release
    python recordcheck.py --keep              # 保留临时目录

退出码: 0 = 全部通过, 1 = 有失败项, 2 = 环境不满足
"""
import argparse
import subprocess
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
WORKSPACE = HERE.parent
# 产品工程在「工程根\HashChecker_WPF\HashChecker」：解决方案目录外面还套了一层，
# 原来直接取 WORKSPACE/"HashChecker"，默认路径必然找不到被测程序。
PROJECT = WORKSPACE / "HashChecker_WPF" / "HashChecker"
CS_DIR = HERE / "cs"


def ensure_probe():
    """recordprobe.exe 不在就现编。"""
    probe = CS_DIR / "recordprobe" / "recordprobe.exe"
    if probe.exists():
        return probe

    print("=== 编译 recordprobe ===")
    r = subprocess.run([sys.executable, str(CS_DIR / "build_all.py"), "recordprobe"],
                       cwd=str(CS_DIR))
    if r.returncode != 0:
        return None
    return probe if probe.exists() else None


def run_probe(probe, app, work):
    """跑探针。输出跟着控制台代码页走，两种编码都试一遍。"""
    r = subprocess.run([str(probe), str(app), str(work)], capture_output=True)
    out = b""
    out += r.stdout or b""
    out += r.stderr or b""
    text = ""
    for enc in ("utf-8", "gbk"):
        try:
            text = out.decode(enc)
        except UnicodeDecodeError:
            continue
        if "\ufffd" not in text:
            break
    else:
        text = out.decode("gbk", "replace")
    return r.returncode, text


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--app", help="被测程序路径")
    ap.add_argument("--config", default="Debug", help="Debug / Release")
    ap.add_argument("--keep", action="store_true", help="保留临时目录")
    args = ap.parse_args()

    app = Path(args.app) if args.app else PROJECT / "bin" / args.config / "HashChecker.exe"
    if not app.exists():
        print("找不到被测程序: %s" % app)
        print("先构建: msbuild HashChecker.sln /t:Build /p:Configuration=%s" % args.config)
        return 2

    probe = ensure_probe()
    if probe is None:
        print("!! recordprobe.exe 编译失败")
        return 2

    work = Path(__import__("tempfile").gettempdir()) / "HashCheckerTest_recordcheck"
    print("=" * 70)
    print("校验记录解析 / 反推：运行时检查")
    print("被测程序: %s" % app)
    print("临时目录: %s" % work)
    print("=" * 70)

    code, out = run_probe(probe, app, work)
    print(out)

    bad = len([l for l in out.splitlines() if l.strip().startswith("!! ")])
    total = len([l for l in out.splitlines() if l.strip().startswith(("OK ", "!! "))])

    print("=" * 70)
    if total == 0:
        print("!! 输出未能正确解析（未读到检查项）")
        return 1
    if code == 0 and bad == 0:
        print("检查 %d 项，全部通过" % total)
        return 0

    print("检查 %d 项，不符 %d 项" % (total, bad))
    return 1


if __name__ == "__main__":
    sys.exit(main())
