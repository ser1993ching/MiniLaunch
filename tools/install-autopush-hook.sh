#!/usr/bin/env bash
# =============================================================================
#  安装全局 git 钩子：提交后自动推送到 GitHub
#
#  用法：
#      bash tools/install-autopush-hook.sh          # 安装
#      bash tools/install-autopush-hook.sh --uninstall
#
#  做法：
#      把 tools/git-hooks/* 复制到 ~/.githooks/，并设置全局 core.hooksPath。
#      这样**所有仓库**的 commit 都会触发 post-commit → 自动推 GitHub。
#
#  注意：设置全局 core.hooksPath 后，各仓库 .git/hooks 下的钩子默认不再触发，
#      因此本目录额外提供了 pre-commit / commit-msg / pre-push 三个转交器，
#      它们会把调用转回仓库自己的同名钩子（已在 post-commit 内部处理 post-commit）。
# =============================================================================
set -euo pipefail

SRC_DIR="$(cd "$(dirname "$0")/git-hooks" && pwd)"
HOOKS_DIR="$HOME/.githooks"

if [ "${1:-}" = "--uninstall" ]; then
  git config --global --unset core.hooksPath 2>/dev/null || true
  rm -rf "$HOOKS_DIR"
  echo "已卸载全局钩子（core.hooksPath 已清除，$HOOKS_DIR 已删除）"
  exit 0
fi

mkdir -p "$HOOKS_DIR"
cp -f "$SRC_DIR"/* "$HOOKS_DIR"/
chmod +x "$HOOKS_DIR"/* 2>/dev/null || true

git config --global core.hooksPath "$HOOKS_DIR"

echo "已安装全局钩子："
echo "  目录      : $HOOKS_DIR"
echo "  core.hooksPath = $HOOKS_DIR"
ls -1 "$HOOKS_DIR" | sed 's/^/    - /'
echo
echo "行为：每次 git commit 后自动检查 GitHub 上是否已存在对应仓库："
echo "      存在 → 自动推送；不存在 → 跳过（不报错）。"
echo "      关闭：ML_NO_AUTOPUSH=1 git commit ...  /  git config autoPush.enabled false"
