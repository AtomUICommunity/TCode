#!/usr/bin/env python3
"""把第三方回放素材转换为 TKCode 夹具（docs/terminal/11-testing-benchmarks.md §1.1.1）。

输出到 tests/TKCode.Terminal.Core.Tests/Fixtures/<source>/：
  <name>.cast     asciicast v2 + TKCode 扩展（§2.1）：头部 + 一个 "ob"（base64 原始字节）事件
  <name>.ref.txt  上游给出的期望屏幕文本（视口各行去掉行尾空白，共 rows 行）
.grid 黄金样本不由本脚本生成：由测试在 TKCODE_UPDATE_GOLDEN=1 时用 TKCode 自己的核心写出（回归锁定）；
.ref.txt 是一致性参照，与 TKCode 的差异记录在 known-differences.txt。

用法：
  convert-fixtures.py xtermjs   <xterm.js 仓库目录>
  convert-fixtures.py alacritty <alacritty 仓库目录>
  convert-fixtures.py ghostty-fuzz <ghostty 源码目录>   # 上游 fuzz 语料的 PR 子集 → Fixtures/fuzz/corpus.jsonl（§1.1.5）
素材只在更新时转换，转换结果入库；只读取数据文件，不执行上游代码。
"""
import base64
import json
import os
import subprocess
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
OUT = os.path.join(ROOT, "tests", "TKCode.Terminal.Core.Tests", "Fixtures")


def commit_of(repo):
    return subprocess.check_output(["git", "-C", repo, "rev-parse", "HEAD"], text=True).strip()


def write_fixture(source, name, data, cols, rows, ref_lines, extra):
    d = os.path.join(OUT, source)
    os.makedirs(d, exist_ok=True)
    header = {"version": 2, "width": cols, "height": rows, "tkcode": extra}
    with open(os.path.join(d, name + ".cast"), "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps(header, ensure_ascii=False, sort_keys=True) + "\n")
        f.write(json.dumps([0.0, "ob", base64.b64encode(data).decode("ascii")]) + "\n")
    lines = [line.rstrip() for line in ref_lines][:rows]
    lines += [""] * (rows - len(lines))
    with open(os.path.join(d, name + ".ref.txt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(lines) + "\n")


def xtermjs(repo):
    src = os.path.join(repo, "test", "fixtures", "escape_sequence_files")
    commit = commit_of(repo)
    count = 0
    for fn in sorted(os.listdir(src)):
        if not fn.endswith(".in"):
            continue
        name = fn[:-3]
        text_path = os.path.join(src, name + ".text")
        if not os.path.exists(text_path):
            continue
        data = open(os.path.join(src, fn), "rb").read()
        # run_tests.py 的写法：先 RIS + 归位，再写入文件内容，最后 ESC t（无效序列，xterm 忽略）。
        # 它经由 tty 写出，ONLCR 把 \n 变成 \r\n，参照文本就是这样得到的，这里照做
        data = (b"\x1bc\x1b[H" + data + b"\x1bt").replace(b"\n", b"\r\n")
        ref = open(text_path, encoding="utf-8", errors="replace").read().split("\n")
        write_fixture("xtermjs", name, data, 80, 25, ref,
                      {"source": f"xtermjs/xterm.js@{commit[:12]}:test/fixtures/escape_sequence_files/{fn}", "license": "MIT"})
        count += 1
    print(f"xtermjs: {count} fixtures from {commit[:12]}")


def alacritty_text(grid):
    """grid.json（Alacritty 的 Grid<Cell> 序列化）→ 视口各行文本。存储是环形缓冲，inner[0] 是最底行。"""
    raw = grid["raw"]
    inner, zero, length = raw["inner"], raw["zero"], raw["len"]
    lines = grid["lines"]
    out = []
    for line in range(lines):
        index = (zero + (lines - 1 - line)) % length
        text = []
        for cell in inner[index]["inner"]:
            flags = cell["flags"]
            if "WIDE_CHAR_SPACER" in flags or "LEADING_WIDE_CHAR_SPACER" in flags:
                continue
            # Alacritty 把制表符本身存在起始格里，显示为空白
            text.append(" " if cell["c"] == "\t" else cell["c"])
            extra = cell.get("extra") or {}
            text.extend(extra.get("zerowidth") or [])
        out.append("".join(text))
    return out


def alacritty(repo):
    src = os.path.join(repo, "alacritty_terminal", "tests", "ref")
    commit = commit_of(repo)
    count = 0
    for name in sorted(os.listdir(src)):
        d = os.path.join(src, name)
        if not os.path.isdir(d):
            continue
        size = json.load(open(os.path.join(d, "size.json")))
        config = json.load(open(os.path.join(d, "config.json")))
        grid = json.load(open(os.path.join(d, "grid.json")))
        data = open(os.path.join(d, "alacritty.recording"), "rb").read()
        write_fixture("alacritty", name, data, size["columns"], size["screen_lines"], alacritty_text(grid),
                      {"source": f"alacritty/alacritty@{commit[:12]}:alacritty_terminal/tests/ref/{name}", "license": "Apache-2.0",
                       "scrollback": config.get("history_size", 0)})
        count += 1
    print(f"alacritty: {count} fixtures from {commit[:12]}")


def ghostty_fuzz(src):
    """PR 子集：osc-cmin、parser-initial、stream-initial 全部，加 stream-cmin 最小的 400 个、parser-cmin 最小的 200 个。
    每行 {"kind", "name", "data"(base64)}；kind 决定回放方式（见 FuzzTests）。"""
    corpus = os.path.join(src, "test", "fuzz-libghostty", "corpus")
    commit = commit_of(src)
    picks = [("osc", "osc-cmin", None), ("parser", "parser-initial", None), ("stream", "stream-initial", None),
             ("stream", "stream-cmin", 400), ("parser", "parser-cmin", 200)]
    out_dir = os.path.join(OUT, "fuzz")
    os.makedirs(out_dir, exist_ok=True)
    count = 0
    with open(os.path.join(out_dir, "corpus.jsonl"), "w", encoding="utf-8", newline="\n") as f:
        f.write(json.dumps({"source": f"ghostty-org/ghostty@{commit[:12]}:test/fuzz-libghostty/corpus", "license": "MIT"}) + "\n")
        for kind, folder, limit in picks:
            d = os.path.join(corpus, folder)
            files = sorted((n for n in os.listdir(d) if not n.startswith(".")), key=lambda n: (os.path.getsize(os.path.join(d, n)), n))
            for n in files[:limit] if limit else files:
                data = open(os.path.join(d, n), "rb").read()
                f.write(json.dumps({"kind": kind, "name": f"{folder}/{n}", "data": base64.b64encode(data).decode("ascii")}) + "\n")
                count += 1
    print(f"ghostty-fuzz: {count} inputs from {commit[:12]}")


if __name__ == "__main__":
    modes = {"xtermjs": xtermjs, "alacritty": alacritty, "ghostty-fuzz": ghostty_fuzz}
    if len(sys.argv) != 3 or sys.argv[1] not in modes:
        sys.exit(__doc__)
    modes[sys.argv[1]](os.path.abspath(sys.argv[2]))
