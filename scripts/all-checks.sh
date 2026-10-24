#!/usr/bin/env bash
# 运行全部确定性自检套件。
#
# 为什么需要这个脚本：spike 不在 Dougong.slnx 里，`dotnet build Dougong.slnx`
# 不会重建它。做反证后若忘记单独重建 spike，跑的是旧 DLL，
# 会得到"修复有效"或"仍然失败"的假结论。本项目已因此白费至少四次排查。
set -euo pipefail

DOTNET="${DOTNET:-dotnet}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

echo "== 构建 spike（必须，不能用全量构建代替）=="
"$DOTNET" build spike/Dougong.Agent.Comparison -c Release

echo
echo "== 内核测试 =="
"$DOTNET" test Dougong.slnx -c Release --no-build

echo
echo "== 自检套件 =="
suites="token schema tool-replacement mcp mcp-reload reachability approval-log wiring wire session usage shell failure store approval fileversion compaction contract"

failed=0
failed_names=""
for suite in $suites; do
  printf '%-18s ' "$suite"
  if ! output=$("$DOTNET" run --project spike/Dougong.Agent.Comparison \
        -c Release --no-build -- "$suite" 2>&1); then
    failed=$((failed + 1))
    failed_names="$failed_names $suite"
  fi
  echo "$output" | tail -1
done

echo
if [ "$failed" -eq 0 ]; then
  echo "ALL SUITES PASS"
else
  echo "FAILED SUITES:$failed_names"
  exit 1
fi
