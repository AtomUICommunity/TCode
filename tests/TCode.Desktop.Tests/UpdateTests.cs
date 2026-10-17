using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using TCode.Desktop.Services;
using TCode.Desktop.Updates;
using Xunit;

namespace TCode.Desktop.Tests;

/// <summary>应用内更新（docs/design/auto-update.md §10）：版本比较、清单验签、Release 选择、下载续传、调度、启动恢复、安装命令。</summary>
public sealed class UpdateTests
{
    private const string Rid = "linux-x64";
    private static readonly Version Os = new(14, 0);

    // ---- SemVer ----

    [Theory]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-rc.1", "1.0.0")]
    [InlineData("0.0.0-dev", "0.1.0")]
    [InlineData("1.9.9", "1.10.0")]
    [InlineData("v1.2.0", "1.2.1+abc")]
    public void SemVersion_Orders(string lower, string higher)
    {
        var a = SemVersion.Parse(lower);
        var b = SemVersion.Parse(higher);
        Assert.True(a < b);
        Assert.True(b > a);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    [InlineData("")]
    public void SemVersion_RejectsInvalid(string text) => Assert.False(SemVersion.TryParse(text, out _));

    [Fact]
    public void SemVersion_IgnoresBuildMetadata() => Assert.Equal(SemVersion.Parse("1.2.3+abc"), SemVersion.Parse("v1.2.3"));

    // ---- 清单验签 ----

    [Fact]
    public void Manifest_VerifiesSignature_AndRejectsTampering()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bytes = ManifestBytes("1.2.0", ("x.tar.gz", [1, 2, 3]));
        var signature = Sign(key, bytes);
        Assert.NotNull(ManifestVerifier.VerifyAndParse(bytes, signature, [key.ExportSubjectPublicKeyInfoPem()]));

        var tampered = (byte[])bytes.Clone();
        tampered[^3] ^= 1;
        Assert.Null(ManifestVerifier.VerifyAndParse(tampered, signature, [key.ExportSubjectPublicKeyInfoPem()]));

        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Null(ManifestVerifier.VerifyAndParse(bytes, signature, [other.ExportSubjectPublicKeyInfoPem()]));
        // 换钥过渡：任意一把公钥通过即可；格式错误的公钥不影响其他公钥
        Assert.NotNull(ManifestVerifier.VerifyAndParse(bytes, signature, ["not a key", other.ExportSubjectPublicKeyInfoPem(), key.ExportSubjectPublicKeyInfoPem()]));
    }

    [Fact]
    public void Manifest_ReleaseKeyIsValid()
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(ManifestVerifier.ReleaseKeys[0]);
        Assert.Equal(256, key.KeySize);
    }

    // ---- Release 选择 ----

    [Fact]
    public async Task Feed_PicksHighestVersionForChannel()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new FakeReleases(key);
        server.Add("v1.1.0");
        server.Add("v1.3.0-beta.1", prerelease: true);
        server.Add("v1.2.0", notes: "## New\n- things");
        server.Add("v9.0.0", draft: true);
        server.Add("v8.0.0", withManifest: false);
        var feed = new GitHubReleaseFeed(new HttpClient(server), [key.ExportSubjectPublicKeyInfoPem()], "https://feed.test");

        var stable = (await feed.CheckAsync(SemVersion.Parse("1.0.0"), UpdateChannel.Stable, Rid, Os, new ReleaseListCache(), default)).Candidate;
        Assert.NotNull(stable);
        Assert.Equal("1.2.0", stable.Version.ToString());
        Assert.False(stable.IsPreview);
        Assert.Equal("## New\n- things", stable.Notes);
        Assert.Equal("https://feed.test/download/v1.2.0/TCode-1.2.0-linux-x64.tar.gz", stable.AssetUrl);

        var preview = (await feed.CheckAsync(SemVersion.Parse("1.0.0"), UpdateChannel.Preview, Rid, Os, new ReleaseListCache(), default)).Candidate;
        Assert.Equal("1.3.0-beta.1", preview?.Version.ToString());
        Assert.True(preview?.IsPreview);

        Assert.Null((await feed.CheckAsync(SemVersion.Parse("1.2.0"), UpdateChannel.Stable, Rid, Os, new ReleaseListCache(), default)).Candidate);
        // 本平台没有安装程序：当作没有更新
        Assert.Null((await feed.CheckAsync(SemVersion.Parse("1.0.0"), UpdateChannel.Stable, "linux-arm64", Os, new ReleaseListCache(), default)).Candidate);
    }

    [Fact]
    public async Task Feed_RejectsBadSignatureAndMismatchedVersion()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new FakeReleases(attacker);
        server.Add("v2.0.0");
        var feed = new GitHubReleaseFeed(new HttpClient(server), [key.ExportSubjectPublicKeyInfoPem()], "https://feed.test");
        await Assert.ThrowsAsync<UpdateFeedException>(() =>
            feed.CheckAsync(SemVersion.Parse("1.0.0"), UpdateChannel.Stable, Rid, Os, new ReleaseListCache(), default));

        // tag 与清单声明的版本不一致（例如把旧清单挂到新 tag 上）
        var mismatched = new FakeReleases(key);
        mismatched.Add("v3.0.0", manifestVersion: "2.0.0");
        feed = new GitHubReleaseFeed(new HttpClient(mismatched), [key.ExportSubjectPublicKeyInfoPem()], "https://feed.test");
        await Assert.ThrowsAsync<UpdateFeedException>(() =>
            feed.CheckAsync(SemVersion.Parse("1.0.0"), UpdateChannel.Stable, Rid, Os, new ReleaseListCache(), default));
    }

    [Fact]
    public async Task Feed_SkipsWhenOsTooOld()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new FakeReleases(key);
        server.Add("v2.0.0", minOs: "15.0");
        var feed = new GitHubReleaseFeed(new HttpClient(server), [key.ExportSubjectPublicKeyInfoPem()], "https://feed.test");
        Assert.Null((await feed.CheckAsync(SemVersion.Parse("1.0.0"), UpdateChannel.Stable, Rid, new Version(14, 6), new ReleaseListCache(), default)).Candidate);
        Assert.NotNull((await feed.CheckAsync(SemVersion.Parse("1.0.0"), UpdateChannel.Stable, Rid, new Version(15, 0), new ReleaseListCache(), default)).Candidate);
    }

    [Fact]
    public async Task Feed_ReusesCachedListOn304()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new FakeReleases(key);
        server.Add("v2.0.0");
        var feed = new GitHubReleaseFeed(new HttpClient(server), [key.ExportSubjectPublicKeyInfoPem()], "https://feed.test");
        var cache = new ReleaseListCache();
        await feed.CheckAsync(SemVersion.Parse("1.0.0"), UpdateChannel.Stable, Rid, Os, cache, default);
        Assert.Equal("\"v1\"", cache.ETag);
        var result = await feed.CheckAsync(SemVersion.Parse("1.0.0"), UpdateChannel.Stable, Rid, Os, cache, default);
        Assert.Equal(1, server.NotModifiedCount);
        Assert.Equal("2.0.0", result.Candidate?.Version.ToString());
    }

    [Fact]
    public async Task Feed_FallsBackToLatestManifestWhenRateLimited()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new FakeReleases(key) { RateLimited = true };
        server.Add("v2.0.0");
        var feed = new GitHubReleaseFeed(new HttpClient(server), [key.ExportSubjectPublicKeyInfoPem()]);
        var candidate = (await feed.CheckAsync(SemVersion.Parse("1.0.0"), UpdateChannel.Preview, Rid, Os, new ReleaseListCache(), default)).Candidate;
        Assert.NotNull(candidate);
        Assert.Equal("2.0.0", candidate.Version.ToString());
        Assert.Equal($"https://github.com/{GitHubReleaseFeed.Repository}/releases/download/v2.0.0/TCode-2.0.0-linux-x64.tar.gz", candidate.AssetUrl);
        Assert.Null(candidate.Notes);
    }

    // ---- 下载 ----

    [Fact]
    public async Task Download_ResumesWithRange_AndVerifiesHash()
    {
        var payload = RandomNumberGenerator.GetBytes(300_000);
        var asset = new UpdateAsset { Name = "TCode-2.0.0-linux-x64.tar.gz", Size = payload.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(payload)) };
        var dir = Directory.CreateTempSubdirectory("tcode-dl-").FullName;
        await File.WriteAllBytesAsync(Path.Combine(dir, asset.Name + ".partial"), payload[..100_000]);
        var handler = new RangeServer(payload);
        var path = await new UpdateDownloader(new HttpClient(handler)).DownloadAsync("https://x/a", dir, asset, null, default);
        Assert.Equal("bytes=100000-", handler.LastRange);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));
        Assert.False(File.Exists(path + ".partial"));

        // 服务器不支持 Range（回 200）：从头写
        File.Delete(path);
        await File.WriteAllBytesAsync(path + ".partial", payload[..50]);
        handler.IgnoreRange = true;
        await new UpdateDownloader(new HttpClient(handler)).DownloadAsync("https://x/a", dir, asset, null, default);
        Assert.Equal(payload, await File.ReadAllBytesAsync(path));

        // 哈希不符：删除并报错
        File.Delete(path);
        var wrong = asset with { Sha256 = new string('0', 64) };
        await Assert.ThrowsAsync<UpdateFeedException>(() => new UpdateDownloader(new HttpClient(handler)).DownloadAsync("https://x/a", dir, wrong, null, default));
        Assert.False(File.Exists(path));
        Assert.False(File.Exists(path + ".partial"));
    }

    // ---- 调度与状态 ----

    [Fact]
    public async Task Scheduler_ChecksAfter60Seconds_ThenEvery6Hours_AndManualResets()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new FakeReleases(key);
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-17T00:00:00Z"));
        using var env = new ServiceEnv(key, server, time, SemVersion.Parse("1.0.0"));
        env.Service.Start();

        time.Advance(TimeSpan.FromSeconds(59));
        await Task.Delay(50);
        Assert.Equal(0, server.ListCount);
        time.Advance(TimeSpan.FromSeconds(1));
        await WaitUntil(() => server.ListCount == 1);
        await WaitUntil(() => env.Service.Status == UpdateStatus.UpToDate);
        await Task.Delay(50); // 让循环登记下一次等待

        time.Advance(UpdateService.Interval - TimeSpan.FromMinutes(1));
        await Task.Delay(50);
        Assert.Equal(1, server.ListCount);
        time.Advance(TimeSpan.FromMinutes(1));
        await WaitUntil(() => server.ListCount == 2);
        await Task.Delay(50);

        // 手动检查：立即检查，下一次自动检查推迟到 6 小时后
        time.Advance(TimeSpan.FromHours(3));
        await env.Service.CheckNowAsync();
        Assert.Equal(3, server.ListCount);
        time.Advance(TimeSpan.FromHours(3));
        await Task.Delay(50);
        Assert.Equal(3, server.ListCount);
        time.Advance(TimeSpan.FromHours(3));
        await WaitUntil(() => server.ListCount == 4);
    }

    [Fact]
    public async Task AutoCheck_ShowsCard_UnlessSkipped_AndRespectsSetting()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new FakeReleases(key);
        server.Add("v2.0.0");
        var time = new FakeTimeProvider();
        using var env = new ServiceEnv(key, server, time, SemVersion.Parse("1.0.0"));

        await env.Service.CheckAsync(manual: false);
        Assert.Equal(UpdateStatus.Available, env.Service.Status);
        Assert.True(env.Service.IsCardVisible);

        env.Service.Skip();
        Assert.False(env.Service.IsCardVisible);
        Assert.Equal("2.0.0", env.State.Current.Update.SkippedVersion);
        await env.Service.CheckAsync(manual: false);
        Assert.False(env.Service.IsCardVisible);
        // 手动检查仍然显示（在「关于」页，不弹卡片）
        await env.Service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Available, env.Service.Status);

        env.Settings.SetAutoCheckUpdates(false);
        Assert.False(env.Service.AutoCheck);
        var before = server.ListCount;
        env.Service.Start();
        time.Advance(TimeSpan.FromHours(7));
        await Task.Delay(50);
        Assert.Equal(before, server.ListCount);
    }

    [Fact]
    public async Task ManualCheckFailure_IsShown_AutoFailureIsNot()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new FakeReleases(key) { Fail = true };
        using var env = new ServiceEnv(key, server, new FakeTimeProvider(), SemVersion.Parse("1.0.0"));
        await env.Service.CheckAsync(manual: false);
        Assert.Equal(UpdateStatus.Idle, env.Service.Status);
        Assert.Null(env.Service.Error);
        await env.Service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Failed, env.Service.Status);
        Assert.NotNull(env.Service.Error);
    }

    [Fact]
    public async Task Download_ThenRestart_RestoresReady_AndDetectsResult()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new FakeReleases(key);
        server.Add("v2.0.0", notes: "**Hello**");
        var home = Directory.CreateTempSubdirectory("tcode-upd-").FullName;
        var target = new InstallTarget.WindowsInstaller(home);
        using (var env = new ServiceEnv(key, server, new FakeTimeProvider(), SemVersion.Parse("1.0.0"), home, target))
        {
            await env.Service.CheckAsync(manual: true);
            await env.Service.DownloadAsync();
            Assert.Equal(UpdateStatus.Ready, env.Service.Status);
            Assert.Equal("2.0.0", env.State.Current.Update.PendingVersion);
            Assert.True(File.Exists(Path.Combine(home, "updates", "2.0.0", "notes.md")));
        }

        // 重启（仍是旧版本）：从暂存目录恢复 Ready，更新说明也在
        using (var env = new ServiceEnv(key, server, new FakeTimeProvider(), SemVersion.Parse("1.0.0"), home, target))
        {
            env.Service.Start();
            await WaitUntil(() => env.Service.Status == UpdateStatus.Ready);
            Assert.Equal("**Hello**", env.Service.Notes);
            Assert.False(env.Service.WasInterrupted);
            // 启动了安装程序但没有完成（模拟：直接写入 InstallingVersion）
            env.State.Current.Update.InstallingVersion = "2.0.0";
            env.State.Save();
        }
        using (var env = new ServiceEnv(key, server, new FakeTimeProvider(), SemVersion.Parse("1.0.0"), home, target))
        {
            env.Service.Start();
            await WaitUntil(() => env.Service.Status == UpdateStatus.Ready);
            Assert.True(env.Service.WasInterrupted);
            Assert.True(env.Service.IsCardVisible);
            env.State.Current.Update.InstallingVersion = "2.0.0";
            env.State.Save();
        }
        // 新版本启动：已更新，清理暂存
        using (var env = new ServiceEnv(key, server, new FakeTimeProvider(), SemVersion.Parse("2.0.0"), home, target))
        {
            env.Service.Start();
            await WaitUntil(() => env.Service.JustUpdated);
            Assert.True(env.Service.IsCardVisible);
            Assert.Equal("**Hello**", env.Service.Notes);
            Assert.Null(env.State.Current.Update.PendingVersion);
            Assert.Null(env.State.Current.Update.InstallingVersion);
            Assert.False(Directory.Exists(Path.Combine(home, "updates", "2.0.0")));
        }
    }

    [Fact]
    public async Task NewerVersion_DiscardsReadyDownload()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new FakeReleases(key);
        server.Add("v2.0.0");
        var home = Directory.CreateTempSubdirectory("tcode-upd-").FullName;
        using var env = new ServiceEnv(key, server, new FakeTimeProvider(), SemVersion.Parse("1.0.0"), home, new InstallTarget.WindowsInstaller(home));
        await env.Service.CheckAsync(manual: true);
        await env.Service.DownloadAsync();
        Assert.Equal(UpdateStatus.Ready, env.Service.Status);

        await env.Service.CheckAsync(manual: false);
        Assert.Equal(1, server.ListCount);
        server.Add("v2.1.0");
        await env.Service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Available, env.Service.Status);
        Assert.Equal("2.1.0", env.Service.Candidate?.Version.ToString());
        Assert.Null(env.State.Current.Update.PendingVersion);
        Assert.False(Directory.Exists(Path.Combine(home, "updates", "2.0.0")));
    }

    [Fact]
    public async Task ManualOnly_DoesNotDownload()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new FakeReleases(key);
        server.Add("v2.0.0");
        using var env = new ServiceEnv(key, server, new FakeTimeProvider(), SemVersion.Parse("1.0.0"), target: new InstallTarget.ManualOnly(ManualReason.ReadOnly));
        await env.Service.CheckAsync(manual: true);
        Assert.Equal(UpdateStatus.Available, env.Service.Status);
        Assert.False(env.Service.CanInstallInApp);
        await env.Service.DownloadAsync();
        Assert.Equal(UpdateStatus.Available, env.Service.Status);
        Assert.Equal(0, server.AssetDownloads);
    }

    // ---- 安装 ----

    [Fact]
    public void Installer_CommandLines()
    {
        var win = UpdateInstaller.WindowsStartInfo(@"C:\u\TCode-2.0.0-win-x64-setup.exe");
        Assert.True(win.UseShellExecute);
        Assert.Equal(["/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/UPDATE=1"], win.ArgumentList);
        var mac = UpdateInstaller.MacStartInfo("/u/TCode-2.0.0-osx-arm64.pkg");
        Assert.Equal("/usr/bin/open", mac.FileName);
        Assert.Equal(["-a", "Installer", "/u/TCode-2.0.0-osx-arm64.pkg"], mac.ArgumentList);
    }

    [Fact]
    public void TeamIdentifier_ParsesCodesignOutput()
    {
        Assert.Equal("ABCDE12345", UpdateInstaller.TeamIdentifier("Executable=/x\nIdentifier=com.tcode.desktop\nTeamIdentifier=ABCDE12345\nSealed Resources version=2\n"));
        Assert.Null(UpdateInstaller.TeamIdentifier("Signature=adhoc\nTeamIdentifier=not set\n"));
    }

    [Fact]
    public void Detect_UnknownLayoutIsManual()
    {
        // 测试输出目录不是任何安装布局
        Assert.False(UpdateInstaller.Detect(AppContext.BaseDirectory).CanInstall);
    }

    [Fact]
    public async Task Linux_ExtractSwapAndRollback()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var root = Directory.CreateTempSubdirectory("tcode-linux-").FullName;
        var install = Path.Combine(root, "TCode");
        WriteTree(install, "old");
        var archive = Path.Combine(root, "update.tar.gz");
        var source = Path.Combine(root, "src");
        WriteTree(Path.Combine(source, "TCode"), "new");
        await Tar(source, archive);

        await UpdateInstaller.ExtractLinuxAsync(install, archive, default, selfTest: false);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install + ".new", "TCode")));

        // 新版本启动失败：恢复原状
        Assert.Throws<InvalidOperationException>(() => UpdateInstaller.SwapLinux(install, _ => throw new InvalidOperationException()));
        Assert.Equal("old", File.ReadAllText(Path.Combine(install, "TCode")));
        Assert.True(Directory.Exists(install + ".new"));

        string? started = null;
        UpdateInstaller.SwapLinux(install, dir => started = dir);
        Assert.Equal(install, started);
        Assert.Equal("new", File.ReadAllText(Path.Combine(install, "TCode")));
        Assert.Equal("old", File.ReadAllText(Path.Combine(install + ".old", "TCode")));
        UpdateInstaller.CleanupLeftovers(new InstallTarget.LinuxDirectory(install));
        Assert.False(Directory.Exists(install + ".old"));

        // 缺少可执行文件的包被拒绝
        File.Delete(Path.Combine(source, "TCode", "ptyhost", "tcode-ptyhost"));
        File.Delete(archive);
        await Tar(source, archive);
        await Assert.ThrowsAsync<UpdateFeedException>(() => UpdateInstaller.ExtractLinuxAsync(install, archive, default, selfTest: false));
        Assert.False(Directory.Exists(install + ".new"));
    }

    /// <summary>与 tools/release/package-linux.sh 相同的打包方式：tar -C &lt;dir&gt; -czf &lt;archive&gt; TCode。</summary>
    private static async Task Tar(string directory, string archive)
    {
        var info = new System.Diagnostics.ProcessStartInfo("tar") { ArgumentList = { "-C", directory, "-czf", archive, "TCode" } };
        info.Environment["COPYFILE_DISABLE"] = "1"; // macOS 的 bsdtar 不写 ._* 附加文件
        using var process = System.Diagnostics.Process.Start(info)!;
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
    }

    private static void WriteTree(string dir, string content)
    {
        Directory.CreateDirectory(Path.Combine(dir, "agenthost"));
        Directory.CreateDirectory(Path.Combine(dir, "ptyhost"));
        File.WriteAllText(Path.Combine(dir, "TCode"), content);
        File.WriteAllText(Path.Combine(dir, "agenthost", "TCode.AgentHost"), content);
        File.WriteAllText(Path.Combine(dir, "ptyhost", "tcode-ptyhost"), content);
    }

    // ---- 工具 ----

    internal static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(20);
        }
        Assert.True(condition());
    }

    private static byte[] Sign(ECDsa key, byte[] data) => key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    private static byte[] ManifestBytes(string version, params (string Name, byte[] Data)[] assets)
    {
        var map = new JsonObject();
        foreach (var (name, data) in assets)
        {
            map[Rid] = new JsonObject { ["name"] = name, ["size"] = data.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(data)) };
        }
        return Encoding.UTF8.GetBytes(new JsonObject { ["schema"] = 1, ["version"] = version, ["channel"] = "stable", ["assets"] = map }.ToJsonString());
    }

    /// <summary>扮演 GitHub：releases 列表（ETag）、清单与签名、安装程序；可模拟限流与故障。</summary>
    internal sealed class FakeReleases(ECDsa key) : HttpMessageHandler
    {
        private readonly List<JsonObject> _releases = [];
        private readonly Dictionary<string, byte[]> _files = new(StringComparer.Ordinal);
        private string? _latestStable;

        public bool RateLimited { get; init; }
        public bool Fail { get; set; }
        public int ListCount { get; private set; }
        public int NotModifiedCount { get; private set; }
        public int AssetDownloads { get; private set; }

        public void Add(string tag, bool prerelease = false, bool draft = false, bool withManifest = true, string? notes = null, string? manifestVersion = null,
            string? minOs = null)
        {
            var version = tag.TrimStart('v');
            var name = $"TCode-{version}-{Rid}.tar.gz";
            var payload = Encoding.UTF8.GetBytes($"installer {version}");
            var asset = new JsonObject { ["name"] = name, ["size"] = payload.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(payload)) };
            if (minOs is not null)
            {
                asset["minOs"] = minOs;
            }
            var manifest = Encoding.UTF8.GetBytes(new JsonObject
            {
                ["schema"] = 1, ["version"] = manifestVersion ?? version, ["channel"] = prerelease ? "preview" : "stable",
                ["assets"] = new JsonObject { [Rid] = asset },
            }.ToJsonString());
            var assets = new JsonArray();
            void AddAsset(string assetName, byte[] data)
            {
                _files[$"/download/{tag}/{assetName}"] = data;
                _files[$"/{GitHubReleaseFeed.Repository}/releases/download/{tag}/{assetName}"] = data;
                assets.Add(new JsonObject { ["name"] = assetName, ["size"] = data.Length, ["browser_download_url"] = $"https://feed.test/download/{tag}/{assetName}" });
            }
            AddAsset(name, payload);
            if (withManifest)
            {
                AddAsset("manifest.json", manifest);
                AddAsset("manifest.json.sig", key.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
                if (!prerelease && !draft)
                {
                    _latestStable = tag;
                }
            }
            _releases.Add(new JsonObject
            {
                ["tag_name"] = tag, ["draft"] = draft, ["prerelease"] = prerelease, ["body"] = notes, ["html_url"] = $"https://feed.test/tag/{tag}",
                ["assets"] = assets,
            });
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Fail)
            {
                throw new HttpRequestException("network down");
            }
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/releases.json", StringComparison.Ordinal) || path.EndsWith("/releases", StringComparison.Ordinal))
            {
                ListCount++;
                if (RateLimited)
                {
                    var limited = new HttpResponseMessage(HttpStatusCode.Forbidden);
                    limited.Headers.Add("X-RateLimit-Remaining", "0");
                    return Task.FromResult(limited);
                }
                var etag = $"\"v{_releases.Count}\"";
                if (request.Headers.IfNoneMatch.Any(t => t.Tag == etag))
                {
                    NotModifiedCount++;
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
                }
                var list = new JsonArray([.. _releases.Select(r => r.DeepClone())]);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(list.ToJsonString()) };
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(etag);
                return Task.FromResult(response);
            }
            var latest = $"/{GitHubReleaseFeed.Repository}/releases/latest/download/";
            if (path.StartsWith(latest, StringComparison.Ordinal) && _latestStable is { } tag)
            {
                path = $"/download/{tag}/{path[latest.Length..]}";
            }
            if (_files.TryGetValue(Uri.UnescapeDataString(path), out var data))
            {
                if (!path.Contains("manifest", StringComparison.Ordinal))
                {
                    AssetDownloads++;
                }
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(data) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class RangeServer(byte[] payload) : HttpMessageHandler
    {
        public string? LastRange { get; private set; }
        public bool IgnoreRange { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRange = request.Headers.Range?.ToString();
            if (!IgnoreRange && request.Headers.Range?.Ranges.FirstOrDefault() is { From: { } from })
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(payload[(int)from..]) });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) });
        }
    }

    /// <summary>独立的 TCODE_HOME：state.json、settings.json、updates/。</summary>
    internal sealed class ServiceEnv : IDisposable
    {
        public ServiceEnv(ECDsa key, HttpMessageHandler server, TimeProvider time, SemVersion current, string? home = null, InstallTarget? target = null)
        {
            Home = home ?? Directory.CreateTempSubdirectory("tcode-upd-").FullName;
            State = new StateService(Path.Combine(Home, "state.json"));
            Settings = new UserSettingsService(NullLogger<UserSettingsService>.Instance, Path.Combine(Home, "settings.json"));
            Service = new UpdateService(State, Settings, NullLogger.Instance, new UpdateServiceOptions
            {
                TimeProvider = time,
                CurrentVersion = current,
                Rid = Rid,
                Target = target ?? new InstallTarget.WindowsInstaller(Home),
                UpdatesDirectory = Path.Combine(Home, "updates"),
                FeedOverride = "https://feed.test",
                PublicKeys = [key.ExportSubjectPublicKeyInfoPem()],
                HttpClient = new HttpClient(server),
            });
        }

        public string Home { get; }
        public StateService State { get; }
        public UserSettingsService Settings { get; }
        public UpdateService Service { get; }

        public void Dispose()
        {
            Service.Dispose();
            Settings.Dispose();
        }
    }
}
