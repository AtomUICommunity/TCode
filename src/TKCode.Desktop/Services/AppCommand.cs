namespace TKCode.Desktop.Services;

internal enum AppCommandKind
{
    /// <summary>没有命令行指令：显示并激活窗口（再次启动 TKCode、点 Dock 图标）。</summary>
    Show,
    NewSession,
    OpenProject,
    Quit,
}

/// <summary>
/// 命令行指令：Windows 任务栏跳转列表的每个条目都是「TKCode.exe + 参数」，再次启动的进程把它转发给已运行的实例后退出
/// （<see cref="SingleInstance"/>）；首次启动时带的指令在窗口创建后执行。
/// </summary>
internal readonly record struct AppCommand(AppCommandKind Kind, string? Argument = null)
{
    public const string NewSessionFlag = "--new-session";
    public const string OpenProjectFlag = "--open-project";
    public const string QuitFlag = "--quit";

    public static AppCommand Show { get; } = new(AppCommandKind.Show);
    public static AppCommand NewSession { get; } = new(AppCommandKind.NewSession);
    public static AppCommand Quit { get; } = new(AppCommandKind.Quit);

    public static AppCommand OpenProject(string path) => new(AppCommandKind.OpenProject, path);

    /// <summary>无法识别的参数一律当作 <see cref="AppCommandKind.Show"/>；多个指令时先出现的生效。</summary>
    public static AppCommand Parse(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case NewSessionFlag:
                    return NewSession;
                case QuitFlag:
                    return Quit;
                case OpenProjectFlag when i + 1 < args.Count && args[i + 1].Length > 0:
                    return OpenProject(args[i + 1]);
            }
        }
        return Show;
    }

    public string[] ToArguments() => Kind switch
    {
        AppCommandKind.NewSession => [NewSessionFlag],
        AppCommandKind.Quit => [QuitFlag],
        AppCommandKind.OpenProject when Argument is { Length: > 0 } => [OpenProjectFlag, Argument],
        _ => [],
    };

    /// <summary>拼成一行命令行参数（Windows 跳转列表的 IShellLink.SetArguments），路径带引号。</summary>
    public string ToCommandLine() => string.Join(' ', ToArguments().Select(Quote));

    private static string Quote(string arg)
    {
        if (arg.Length > 0 && !arg.AsSpan().ContainsAny(' ', '\t', '"'))
        {
            return arg;
        }
        // 反斜杠只有紧挨着引号时才需要加倍：C:\dir\ → "C:\dir\\"
        var trailing = 0;
        while (trailing < arg.Length && arg[arg.Length - 1 - trailing] == '\\')
        {
            trailing++;
        }
        return "\"" + arg.Replace("\"", "\\\"") + new string('\\', trailing) + "\"";
    }
}
