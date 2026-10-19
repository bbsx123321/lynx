# 展示页部署到 Cloudflare Pages 指南

展示页是纯静态站（`website/index.html` + `website/assets/`），无构建步骤，Cloudflare Pages
直传即可。两种方式任选其一。

## 方式 A：Dashboard 连接 GitHub（推荐，自动跟随仓库更新）

1. 打开 <https://dash.cloudflare.com> → **Workers & Pages** → **Create** → **Pages** →
   **Connect to Git**；
2. 授权并选择仓库 `bbsx123321/lynx`；
3. 构建配置全部留空（静态站无需构建）：
   - Framework preset: `None`
   - Build command: （空）
   - Build output directory: `website`
4. **Save and Deploy**。约 30 秒后拿到 `https://lynx-xxxx.pages.dev`（可在
   *Custom domains* 里绑定自己的域名）。

之后每次 `git push`，Pages 自动重新部署——不需要任何本地工具。

> 注意：`website/_headers` 会被自动读取（HTML 不缓存、assets 一年长缓存）。

## 方式 B：命令行直传（脚本一键部署）

1. 到 <https://dash.cloudflare.com/profile/api-tokens> → **Create Token** →
   使用 **Edit Cloudflare Workers** 模板，或自定义以下权限：
   - Account / **Cloudflare Pages** / Edit
2. 导出并执行脚本：

   ```bash
   export CLOUDFLARE_API_TOKEN=你的token
   bash scripts/deploy-pages.sh            # 生产部署
   bash scripts/deploy-pages.sh preview    # 预览部署
   ```

3. 首次运行会自动创建 `lynx` 项目；最终地址 `https://lynx.pages.dev`。

## 部署后：把软件内入口指回官网（可选）

部署完成后把 `src/PadShift.App/Services/UpdateService.cs` 里的 `Website` 改为你的
Pages 地址（如 `https://lynx.pages.dev`），重打包即可让软件内「下载页」按钮打开官网。

## 本地预览（不登录、不部署）

```bash
npx wrangler pages dev website --port 8788
# 浏览器打开 http://127.0.0.1:8788
```
