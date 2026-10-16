namespace TCode.Config;

public enum KeySource
{
    None,
    /// <summary>settings.json 中的明文。</summary>
    Settings,
    /// <summary>环境变量（{env:VAR}、$VAR 或目录登记的变量名）。</summary>
    Environment,
    /// <summary>显式写成 ""：本地服务，不需要 Key。</summary>
    NotRequired,
}

public sealed record ResolvedKey(string? Value, KeySource Source, string? Variable)
{
    public bool IsAvailable => Source == KeySource.NotRequired || !string.IsNullOrEmpty(Value);
}

/// <summary>API Key 的写法：明文、{env:VAR}、$VAR；"" 表示无需 Key。</summary>
public static class Secrets
{
    public static ResolvedKey Resolve(string? spec, IReadOnlyList<string>? fallbackVariables = null)
    {
        if (spec is not null)
        {
            if (spec.Length == 0)
            {
                return new ResolvedKey(null, KeySource.NotRequired, null);
            }
            if (VariableOf(spec) is { } variable)
            {
                return new ResolvedKey(Environment.GetEnvironmentVariable(variable), KeySource.Environment, variable);
            }
            return new ResolvedKey(spec, KeySource.Settings, null);
        }
        foreach (var variable in fallbackVariables ?? [])
        {
            if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value)
            {
                return new ResolvedKey(value, KeySource.Environment, variable);
            }
        }
        return new ResolvedKey(null, KeySource.None, fallbackVariables is { Count: > 0 } vars ? vars[0] : null);
    }

    /// <summary>{env:VAR} 或 $VAR 中的变量名；不是引用时返回 null。</summary>
    public static string? VariableOf(string spec)
    {
        if (spec.StartsWith("{env:", StringComparison.Ordinal) && spec.EndsWith('}'))
        {
            return spec[5..^1].Trim();
        }
        if (spec.Length > 1 && spec[0] == '$' && spec.Skip(1).All(c => char.IsLetterOrDigit(c) || c == '_'))
        {
            return spec[1..];
        }
        return null;
    }

    /// <summary>界面上显示的遮罩：sk-ant-…a1b2。</summary>
    public static string Mask(string value) =>
        value.Length <= 10 ? new string('•', value.Length) : $"{value[..6]}…{value[^4..]}";
}
