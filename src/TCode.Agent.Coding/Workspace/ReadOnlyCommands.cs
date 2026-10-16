using System.Text;

namespace TCode.Agent.Coding.Workspace;

/// <summary>
/// 只读命令白名单（Ask 档下免审批）。命令按 <c>&amp;&amp; || ; |</c> 拆成片段，每段都必须命中白名单；
/// 出现重定向、命令替换、后台执行或环境变量赋值时一律视为非只读。
/// </summary>
public static class ReadOnlyCommands
{
    private static readonly HashSet<string> Simple = new(StringComparer.Ordinal)
    {
        "ls", "cat", "head", "tail", "wc", "rg", "grep", "pwd", "which", "file", "stat", "du", "tree",
    };

    private static readonly HashSet<string> GitSubcommands = new(StringComparer.Ordinal)
    {
        "status", "diff", "log", "show", "rev-parse", "ls-files", "blame",
    };

    private static readonly string[] FindWriteFlags = ["-exec", "-execdir", "-ok", "-okdir", "-delete", "-fprint", "-fprint0", "-fprintf", "-fls"];

    public static bool IsReadOnly(string command)
    {
        if (!TrySplit(command, out var segments))
        {
            return false;
        }
        return segments.Count > 0 && segments.All(IsReadOnlySegment);
    }

    private static bool IsReadOnlySegment(IReadOnlyList<string> words)
    {
        if (words.Count == 0)
        {
            return false;
        }
        var name = words[0];
        if (name.Contains('=') || name.Contains('/'))
        {
            return false;
        }
        if (Simple.Contains(name))
        {
            return true;
        }
        if (name == "find")
        {
            return !words.Any(w => FindWriteFlags.Contains(w, StringComparer.Ordinal));
        }
        if (name == "git")
        {
            var sub = words.Skip(1).FirstOrDefault(w => !w.StartsWith('-'));
            if (sub == "branch")
            {
                return words.Skip(2).All(w => w is "-a" or "-r" or "-v" or "-vv" or "--list" or "--show-current" or "--all" or "--remotes");
            }
            return sub is not null && GitSubcommands.Contains(sub) && !words.Contains("--output", StringComparer.Ordinal)
                   && !words.Any(w => w.StartsWith("--output=", StringComparison.Ordinal));
        }
        return false;
    }

    /// <summary>按引号感知的方式拆分片段与单词；遇到不支持的语法返回 false。</summary>
    private static bool TrySplit(string command, out List<List<string>> segments)
    {
        segments = [];
        var words = new List<string>();
        var word = new StringBuilder();
        var hasWord = false;
        char quote = '\0';

        void EndWord()
        {
            if (hasWord)
            {
                words.Add(word.ToString());
                word.Clear();
                hasWord = false;
            }
        }

        void EndSegment(List<List<string>> list)
        {
            EndWord();
            list.Add(words);
            words = [];
        }

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            if (quote == '\'')
            {
                if (c == '\'')
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(c);
                }
                continue;
            }
            if (quote == '"')
            {
                if (c is '`' or '$')
                {
                    return false;
                }
                if (c == '"')
                {
                    quote = '\0';
                }
                else if (c == '\\' && i + 1 < command.Length)
                {
                    word.Append(command[++i]);
                }
                else
                {
                    word.Append(c);
                }
                continue;
            }
            switch (c)
            {
                case '\'' or '"':
                    quote = c;
                    hasWord = true;
                    break;
                case '\\' when i + 1 < command.Length:
                    word.Append(command[++i]);
                    hasWord = true;
                    break;
                case ' ' or '\t':
                    EndWord();
                    break;
                case ';':
                    EndSegment(segments);
                    break;
                case '|':
                    if (i + 1 < command.Length && command[i + 1] == '|')
                    {
                        i++;
                    }
                    EndSegment(segments);
                    break;
                case '&':
                    if (i + 1 < command.Length && command[i + 1] == '&')
                    {
                        i++;
                        EndSegment(segments);
                        break;
                    }
                    return false;
                case '>' or '<' or '`' or '$' or '(' or ')' or '{' or '}' or '\n' or '\r':
                    return false;
                default:
                    word.Append(c);
                    hasWord = true;
                    break;
            }
        }
        if (quote != '\0')
        {
            return false;
        }
        EndSegment(segments);
        return segments.All(s => s.Count > 0);
    }
}
