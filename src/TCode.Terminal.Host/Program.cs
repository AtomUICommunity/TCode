using TCode.Terminal.Host;

// tcode-ptyhost：终端会话的独立宿主进程（docs/terminal/04-pty-host.md）。
// M0 只实现 --self-test：在发布后的 AOT 二进制里验证原生库加载、核心解析和真实 PTY 往返（12 §7 的 aot-smoke）。
return args switch
{
    ["--self-test", ..] => await SelfTest.RunAsync(Console.Out),
    ["--version"] => PrintVersion(),
    _ => Usage(),
};

static int PrintVersion()
{
    Console.WriteLine($"tcode-ptyhost {typeof(SelfTest).Assembly.GetName().Version} (libghostty-vt {TCode.Terminal.Core.GhosttyRuntime.Version})");
    return 0;
}

static int Usage()
{
    Console.Error.WriteLine("usage: tcode-ptyhost --self-test | --version");
    return 2;
}
