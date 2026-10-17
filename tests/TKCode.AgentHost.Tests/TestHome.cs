using System.Runtime.CompilerServices;

namespace TKCode.AgentHost.Tests;

/// <summary>测试一律使用临时数据目录，避免往用户的 ~/.tkcode 写入命令日志、子 Agent 转录等文件。</summary>
internal static class TestHome
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(TKCode.Config.ConfigPaths.HomeVariable)))
        {
            Environment.SetEnvironmentVariable(TKCode.Config.ConfigPaths.HomeVariable,
                Directory.CreateTempSubdirectory("tkcode-test-home-").FullName);
        }
    }
}
