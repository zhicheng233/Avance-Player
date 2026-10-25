#!/usr/bin/env bash
# 把本目录的 .mmd 渲染成 PNG。
#   用法：./render.sh            渲染全部
#         ./render.sh 03-lyrics  只渲染指定的一张（不带扩展名）
# 需要 Node.js 22+ 与 Chromium；安装说明见 README.md。
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CLI_JS="${MERMAID_CLI_JS:-/tmp/opencode/uml-render/node_modules/@mermaid-js/mermaid-cli/src/cli.js}"
PUPPETEER_CFG="${MERMAID_PUPPETEER_CFG:-/tmp/opencode/uml-render/puppeteer.json}"
SCALE="${MERMAID_SCALE:-2}"

if [[ ! -f "$CLI_JS" ]]; then
  echo "找不到 mermaid-cli：$CLI_JS" >&2
  echo "先按 README.md 的步骤安装，或用 MERMAID_CLI_JS 指向它。" >&2
  exit 1
fi

if [[ $# -gt 0 ]]; then
  targets=("$@")
else
  targets=()
  while IFS= read -r file; do
    targets+=("$(basename "$file" .mmd)")
  done < <(find "$HERE" -maxdepth 1 -name '*.mmd' | sort)
fi

for name in "${targets[@]}"; do
  src="$HERE/${name%.mmd}.mmd"
  out="$HERE/${name%.mmd}.png"
  echo "==> ${name}.mmd -> ${name}.png"
  node "$CLI_JS" -i "$src" -o "$out" -p "$PUPPETEER_CFG" -s "$SCALE" -b white
done

echo "完成。"
