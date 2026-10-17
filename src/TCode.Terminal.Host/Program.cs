using TCode.Terminal.Host;

// tcode-ptyhost：终端会话的独立宿主进程（docs/terminal/04-pty-host.md）。
return args switch
{
    ["--self-test", ..] => await SelfTest.RunAsync(Console.Out),
    ["--version"] => PrintVersion(),
    _ when args.Contains("--home") => await HostMain.RunAsync(args),
    _ => Usage(),
};

static int PrintVersion()
{
    Console.WriteLine($"tcode-ptyhost {typeof(SelfTest).Assembly.GetName().Version} (libghostty-vt {TCode.Terminal.Core.GhosttyRuntime.Version})");
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("usage: tcode-ptyhost --home <dir> [--build <id>] [--ready-fd <fd>] [--log-level <level>] | --self-test | --version");
    return 2;
}
