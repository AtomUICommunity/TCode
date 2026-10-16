using TCode.Agent.Coding.Workspace;
using Xunit;

namespace TCode.Agent.Tests;

public sealed class PermissionPolicyTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("tcode-perm-").FullName;
    private readonly string _outside = Directory.CreateTempSubdirectory("tcode-perm-outside-").FullName;

    private PermissionVerdict Evaluate(ToolPermission permission, PermissionMode mode = PermissionMode.Ask, SessionApprovals? approvals = null) =>
        PermissionPolicy.Evaluate(permission, mode, new WorkspaceRoots(_root), approvals ?? new SessionApprovals());

    [Fact]
    public void Read_InsideWorkspace_IsAllowed_OutsideAsksWithDirectoryKey()
    {
        Assert.True(Evaluate(new(ToolAccess.Read, [Path.Combine(_root, "a.txt")])).Allowed);

        var outside = Evaluate(new(ToolAccess.Read, [Path.Combine(_outside, "b.txt")]));
        Assert.False(outside.Allowed);
        Assert.Equal("read", outside.Kind);

        var approvals = new SessionApprovals();
        approvals.Add(outside.SessionKey!);
        Assert.True(Evaluate(new(ToolAccess.Read, [Path.Combine(_outside, "c.txt")]), approvals: approvals).Allowed);
        Assert.True(Evaluate(new(ToolAccess.Read, [Path.Combine(_outside, "b.txt")]), PermissionMode.AutoApprove).Allowed);
    }

    [Fact]
    public void Read_ThroughSymlinkLeavingWorkspace_Asks()
    {
        var link = Path.Combine(_root, "link");
        Directory.CreateSymbolicLink(link, _outside);
        Assert.False(Evaluate(new(ToolAccess.Read, [Path.Combine(link, "secret.txt")])).Allowed);
    }

    [Fact]
    public void Write_AsksUntilAllowedForSession_AndAutoApproveStillAsksOutsideWorkspace()
    {
        var inside = new ToolPermission(ToolAccess.Write, [Path.Combine(_root, "a.cs")]);
        var verdict = Evaluate(inside);
        Assert.False(verdict.Allowed);
        Assert.Equal(("fileChange", "edit"), (verdict.Kind, verdict.SessionKey));

        var approvals = new SessionApprovals();
        approvals.Add("edit");
        Assert.True(Evaluate(inside, approvals: approvals).Allowed);
        Assert.True(Evaluate(inside, PermissionMode.AutoApprove).Allowed);

        var outside = Evaluate(new(ToolAccess.Write, [Path.Combine(_outside, "a.cs")]), PermissionMode.AutoApprove, approvals);
        Assert.False(outside.Allowed);
        Assert.Null(outside.SessionKey);
        Assert.True(Evaluate(new(ToolAccess.Write, [Path.Combine(_outside, "a.cs")]), PermissionMode.FullAccess).Allowed);
    }

    [Theory]
    [InlineData("ls -la", true)]
    [InlineData("git status && git diff --stat", true)]
    [InlineData("rg -n 'foo|bar' src | head -20", true)]
    [InlineData("git log --oneline -5", true)]
    [InlineData("find . -name '*.cs'", true)]
    [InlineData("cat /etc/passwd", false)]
    [InlineData("cat ../secret", false)]
    [InlineData("find . -delete", false)]
    [InlineData("ls > out.txt", false)]
    [InlineData("echo $(rm -rf x)", false)]
    [InlineData("rm -rf build", false)]
    [InlineData("git branch -D main", false)]
    [InlineData("git push", false)]
    [InlineData("FOO=1 ls", false)]
    [InlineData("ls &", false)]
    public void Execute_ReadOnlyCommandsInsideWorkspaceAreAllowed(string command, bool allowed)
    {
        Assert.Equal(allowed, Evaluate(new(ToolAccess.Execute, Command: command)).Allowed);
    }

    [Fact]
    public void Execute_SessionApprovalIsScopedToCommandPrefix()
    {
        var verdict = Evaluate(new(ToolAccess.Execute, Command: "npm run build"));
        Assert.Equal("command:npm run", verdict.SessionKey);
        var approvals = new SessionApprovals();
        approvals.Add(verdict.SessionKey!);
        Assert.True(Evaluate(new(ToolAccess.Execute, Command: "npm run test"), approvals: approvals).Allowed);
        Assert.False(Evaluate(new(ToolAccess.Execute, Command: "npm publish"), approvals: approvals).Allowed);
        Assert.Equal("git commit", PermissionPolicy.CommandPrefix("git commit -m 'x'"));
        Assert.Equal("python3", PermissionPolicy.CommandPrefix("python3 script.py"));
    }

    [Fact]
    public void Network_IsKeyedByDomain()
    {
        var verdict = Evaluate(new(ToolAccess.Network, Domain: "Example.com"));
        Assert.Equal(("network", "network:example.com"), (verdict.Kind, verdict.SessionKey));
        Assert.Equal("network:search", Evaluate(new(ToolAccess.Network)).SessionKey);
        Assert.True(Evaluate(ToolPermission.NoAccess).Allowed);
    }
}
