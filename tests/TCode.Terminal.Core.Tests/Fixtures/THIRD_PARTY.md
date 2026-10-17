# 回放夹具的来源与许可

由 `tools/terminal/fixtures/convert-fixtures.py` 转换（只读取上游数据文件，不执行上游代码）。每个 `.cast` 的头部 `tcode.source` 记录了来源路径与提交。

| 目录 | 来源 | 提交 | 许可 | 内容 |
|---|---|---|---|---|
| `xtermjs/` | https://github.com/xtermjs/xterm.js `test/fixtures/escape_sequence_files` | `991857a1b1ca` | MIT（Copyright (c) 2017-2019 The xterm.js authors；部分素材源自 MarkLodato/vt100-parser，MIT） | `.in` → `.cast`（前置 `ESC c ESC [H`，后置 `ESC t`，与上游 run_tests.py 相同）；`.text`（xterm 的实际输出）→ `.ref.txt` |
| `alacritty/` | https://github.com/alacritty/alacritty `alacritty_terminal/tests/ref` | `542147067dd0` | Apache-2.0 / MIT | `alacritty.recording` → `.cast`；`grid.json` 的视口文本 → `.ref.txt`；`config.json` 的 history_size → 头部 `tcode.scrollback` |

`.grid` 是 TCode 自己的输出（回归锁定），`.ref.txt` 是上游参照；二者的已知差异见 `known-differences.txt`。
