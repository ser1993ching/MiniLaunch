#!/usr/bin/env bash
# 一键推送到所有远端（github + gitee）
# 用法：bash tools/push-all.sh [分支名]   默认 master
set -euo pipefail

BRANCH="${1:-master}"
cd "$(dirname "$0")/.."

git status --porcelain | grep -q . && {
  echo "[提示] 有未提交改动，先 commit 再推送：" >&2
  git status --short >&2
  exit 1
}

REMOTES=$(git remote | tr '\n' ' ')
if [ -z "$REMOTES" ]; then
  echo "[错误] 没有配置任何远端，先执行 git remote add ..." >&2
  exit 1
fi

fail=0
for r in $REMOTES; do
  echo "=== 推送到 $r ==="
  git push -u "$r" "$BRANCH" || { echo "[失败] $r 推送失败" >&2; fail=1; }
done

exit $fail
