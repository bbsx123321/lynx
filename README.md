# 灵犀 Lynx — 免费开源的 Windows 手柄映射器 / Free open-source gamepad mapper

![icon](assets/icon/padshift-256.png)

[官网 / Website](https://bbsx123321.github.io/lynx/) ·
[下载 / Download](https://github.com/bbsx123321/lynx/releases/latest) ·
[国内加速 / CN mirror](https://gh-proxy.com/https://github.com/bbsx123321/lynx/releases/latest/download/Lynx.exe)

**灵犀 Lynx**（取"心有灵犀一点通"——按键与映射心意相通）把物理手柄映射为键盘、鼠标和虚拟手柄。
免费、开源、无账号、无广告。

## 功能一览

| 模块 | 说明 |
|---|---|
| **可交互 3D 手柄** | 主窗口实时 3D 手柄：点击部件绑定、拖动摇杆演示、按下发光；拖动翻转查看背面、一键复位；三种外形（DualSense / Xbox 360 / Switch Pro） |
| **配置库** | 每游戏多套配置、封面与描述、预设库、导入/导出、绑定清单导出与游戏内悬浮卡 |
| **映射引擎** | 层/跳层（12 层）、6 种激活器、组合键掩码、摇杆分区、Turbo/Toggle、宏（键鼠+手柄混合步骤）、每层多输出混合（键盘+鼠标+虚拟手柄同帧） |
| **调校** | 摇杆曲线/死区/灵敏度、摇杆当鼠标、陀螺仪映射与校准向导 |
| **输出** | SendInput / Interception 键鼠注入；ViGEm 虚拟 Xbox 360 / DS4；UDP 输出镜像；虚拟手柄性能档（8/4/2ms） |
| **安全行为** | 失焦自动停映射、停宏热键（Ctrl+Alt+M）、映射悬浮卡热键（Ctrl+Alt+B）、热键冲突校验、退出超时保护 |
| **叠加层** | 点击穿透 toast、准星+放大镜、径向菜单（8 扇区，手柄组合键/热键） |
| **中英双语** | 界面简体中文 / English 即时切换（默认中文）；官网双语（默认英文） |
| **本地 API** | HTTP + SSE，进程内嵌；CLI 工具 |
| **托盘常驻** | 关窗收托盘映射继续 |

## 运行（绿色版）

```
dist\Lynx.exe
```

- **自包含单文件**（~84 MB，win-x64）：Win10 1809+ / Win11 x64 直接双击，无需安装 .NET；
- 虚拟手柄输出需要 **[ViGEmBus](https://github.com/nefarius/ViGEmBus)**（未装时自动降级纯键鼠并提示）；
- 远程桌面/虚拟机界面卡顿时用 `--safe-graphics` 启动。

## 开发

```powershell
dotnet build
dotnet test      # 268 个测试
```

CI：GitHub Actions（`.github/workflows/build.yml`）每次推送自动构建并上传产物；打 `v*` 标签自动发布 Release。

## 3D 模型许可

三种手柄外形来自开放许可资源（来源与转换说明见 `assets/3d/*/LICENSE.txt`）：
DualSense（CC0）、Xbox 360 / Switch Pro（MIT）。

## 目录

```
website/               官网（gh-pages）
src/PadShift.Core/     领域模型与配置序列化
src/PadShift.Input/    设备发现与报文解析
src/PadShift.Output/   输入注入与虚拟手柄
src/PadShift.Engine/   映射引擎（4ms 循环）
src/PadShift.Api/      本地 HTTP API
src/PadShift.App/      WPF 客户端（3D 视图、双语、更新检查）
tests/PadShift.Tests/  单元测试
tools/                 模型导出/合并、图标生成脚本
```
