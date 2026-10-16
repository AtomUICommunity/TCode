using System.Runtime.CompilerServices;

namespace TCode.Agent.Tests;

/// <summary>测试一律使用临时数据目录，避免往用户的 ~/.tcode 写入命令日志、子 Agent 转录等文件。</summary>
internal static class TestHome
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(TCode.Agent.Coding.TCodePaths.HomeVariable)))
        {
            Environment.SetEnvironmentVariable(TCode.Agent.Coding.TCodePaths.HomeVariable,
                Directory.CreateTempSubdirectory("tcode-test-home-").FullName);
        }
    }
}
