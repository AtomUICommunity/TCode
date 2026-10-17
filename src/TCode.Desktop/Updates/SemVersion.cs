namespace TCode.Desktop.Updates;

/// <summary>
/// SemVer 2.0 版本号（docs/design/auto-update.md §3）：主.次.修订[-预发布][+构建]。
/// 比较按 SemVer 优先级规则：构建元数据不参与比较；有预发布段的版本低于同号正式版；预发布段逐段比较，数字段按数值、数字段低于字母段。
/// </summary>
public sealed class SemVersion : IComparable<SemVersion>, IEquatable<SemVersion>
{
    private SemVersion(int major, int minor, int patch, string prerelease)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        Prerelease = prerelease;
    }

    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }

    /// <summary>预发布段（不含 '-'），正式版为空串。</summary>
    public string Prerelease { get; }

    public bool IsPrerelease => Prerelease.Length > 0;

    /// <summary>解析版本号或 tag：允许前缀 v，忽略 + 之后的构建元数据。</summary>
    public static bool TryParse(string? text, out SemVersion version)
    {
        version = null!;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var s = text.Trim();
        if (s[0] is 'v' or 'V')
        {
            s = s[1..];
        }
        if (s.IndexOf('+') is var plus and >= 0)
        {
            s = s[..plus];
        }
        var prerelease = "";
        if (s.IndexOf('-') is var dash and >= 0)
        {
            prerelease = s[(dash + 1)..];
            s = s[..dash];
            if (prerelease.Length == 0 || prerelease.Split('.').Any(p => p.Length == 0 || !p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')))
            {
                return false;
            }
        }
        var parts = s.Split('.');
        if (parts.Length != 3 || !TryNumber(parts[0], out var major) || !TryNumber(parts[1], out var minor) || !TryNumber(parts[2], out var patch))
        {
            return false;
        }
        version = new SemVersion(major, minor, patch, prerelease);
        return true;
    }

    public static SemVersion Parse(string text) =>
        TryParse(text, out var v) ? v : throw new FormatException($"Not a semantic version: '{text}'.");

    private static bool TryNumber(string s, out int value)
    {
        value = 0;
        return s.Length > 0 && s.All(char.IsAsciiDigit) && int.TryParse(s, out value);
    }

    public int CompareTo(SemVersion? other)
    {
        if (other is null)
        {
            return 1;
        }
        var c = Major.CompareTo(other.Major);
        if (c == 0)
        {
            c = Minor.CompareTo(other.Minor);
        }
        if (c == 0)
        {
            c = Patch.CompareTo(other.Patch);
        }
        return c != 0 ? c : ComparePrerelease(Prerelease, other.Prerelease);
    }

    private static int ComparePrerelease(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            // 正式版高于任何预发布版
            return a.Length == b.Length ? 0 : a.Length == 0 ? 1 : -1;
        }
        var x = a.Split('.');
        var y = b.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            var xNum = x[i].All(char.IsAsciiDigit);
            var yNum = y[i].All(char.IsAsciiDigit);
            int c;
            if (xNum && yNum)
            {
                c = x[i].Length != y[i].Length ? x[i].Length.CompareTo(y[i].Length) : string.CompareOrdinal(x[i], y[i]);
            }
            else if (xNum != yNum)
            {
                c = xNum ? -1 : 1;
            }
            else
            {
                c = string.CompareOrdinal(x[i], y[i]);
            }
            if (c != 0)
            {
                return Math.Sign(c);
            }
        }
        return x.Length.CompareTo(y.Length);
    }

    public bool Equals(SemVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemVersion v && Equals(v);

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, Prerelease);

    public override string ToString() => IsPrerelease ? $"{Major}.{Minor}.{Patch}-{Prerelease}" : $"{Major}.{Minor}.{Patch}";

    public static bool operator ==(SemVersion? a, SemVersion? b) => a is null ? b is null : a.Equals(b);
    public static bool operator !=(SemVersion? a, SemVersion? b) => !(a == b);
    public static bool operator <(SemVersion a, SemVersion b) => a.CompareTo(b) < 0;
    public static bool operator >(SemVersion a, SemVersion b) => a.CompareTo(b) > 0;
    public static bool operator <=(SemVersion a, SemVersion b) => a.CompareTo(b) <= 0;
    public static bool operator >=(SemVersion a, SemVersion b) => a.CompareTo(b) >= 0;
}
