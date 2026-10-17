namespace TKCode.Agent.Coding;

/// <summary>
/// TKCode 的数据目录，默认 ~/.tkcode；可用环境变量 TKCODE_HOME 覆盖（测试与便携部署用，桌面端会把它传给 Host 子进程）。
/// </summary>
public static class TKCodePaths
{
    public const string HomeVariable = TKCode.Config.ConfigPaths.HomeVariable;

    public static string Home => TKCode.Config.ConfigPaths.Home;
}
