namespace PadShift.Cli;

/// <summary>Chinese help text for <c>help</c> / usage errors.</summary>
public static class HelpText
{
    public const string Text =
        """
        PadShift 命令行工具（协议 v2.6，与 the v2.6 wire protocol CLI 对齐）

        用法：
          PadShift.Cli apply --config <路径> [--slot N] [--controller <GUID>] [--port <端口>]
          PadShift.Cli select_slot --slot N [--controller <GUID>] [--port <端口>]
          PadShift.Cli clear_slot --slot N [--controller <GUID>] [--port <端口>]
          PadShift.Cli remap --on|--off [--controller <GUID>] [--port <端口>]
          PadShift.Cli version [--port <端口>]
          PadShift.Cli help

        参数：
          --config <路径>      要应用的 .rewasd 配置文件（apply 必填）
          --slot N             槽位 1–4（apply 默认 1；select_slot/clear_slot 必填）
          --controller <GUID>  目标控制器 GUID；省略表示所有手柄
          --port <端口>        指定 API 端口；省略时依次尝试 35474、35475、35476
          --on / --off         打开或关闭映射（remap 必填）

        退出码：
          0  成功（HTTP 2xx）
          1  请求失败或无法连接 PadShift API
          2  参数错误

        """;
}
