#!/usr/bin/env bash
# 把展示页部署到 Cloudflare Pages（直传模式，无需 Git 集成）。
#
# 前置（只需一次）：
#   1. 安装 Node.js（含 npx）；
#   2. 导出 Cloudflare API Token（见 docs/deploy-cloudflare.md 的权限清单）：
#        export CLOUDFLARE_API_TOKEN=xxxx
#   3. （首次）创建项目：脚本会自动创建，也可手动：
#        npx wrangler pages project create lynx --production-branch main
#
# 用法：
#   bash scripts/deploy-pages.sh            # 部署 website/ 到生产分支
#   bash scripts/deploy-pages.sh preview    # 部署到预览分支（不影响生产）
set -euo pipefail

cd "$(dirname "$0")/.."

PROJECT="${CF_PAGES_PROJECT:-lynx}"
BRANCH="main"
if [[ "${1:-}" == "preview" ]]; then
  BRANCH="preview-$(date +%Y%m%d%H%M%S)"
fi

echo "==> 部署 website/ 到 Cloudflare Pages：项目=$PROJECT 分支=$BRANCH"
npx --yes wrangler@latest pages deploy website \
  --project-name "$PROJECT" \
  --branch "$BRANCH" \
  --commit-dirty=true

echo "==> 完成。生产地址：https://$PROJECT.pages.dev"
