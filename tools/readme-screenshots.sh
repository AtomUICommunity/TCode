#!/usr/bin/env bash
# 重新生成 README 截图（docs/images/*-en.png / *-zh.png）：无头渲染，预置示例项目与会话，不发出真实模型请求。
# 用法: tools/readme-screenshots.sh [输出目录=docs/images]
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="$(cd "$(dirname "${1:-docs/images}")" && pwd)/$(basename "${1:-docs/images}")"

dotnet build src/TCode.AgentHost -v q
dotnet build tests/TCode.Desktop.Tests -v q
TCODE_README_SCREENSHOTS="$OUT" dotnet test tests/TCode.Desktop.Tests --no-build --filter "FullyQualifiedName~ReadmeScreenshotTests"
ls -lh "$OUT"
