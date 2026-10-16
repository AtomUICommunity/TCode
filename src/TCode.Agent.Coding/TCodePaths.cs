namespace TCode.Agent.Coding;

/// <summary>
/// TCode 的数据目录，默认 ~/.tcode；可用环境变量 TCODE_HOME 覆盖（测试与便携部署用，桌面端会把它传给 Host 子进程）。
/// </summary>
public static class TCodePaths
{
    public const string HomeVariable = TCode.Config.ConfigPaths.HomeVariable;

    public static string Home => TCode.Config.ConfigPaths.Home;
}
