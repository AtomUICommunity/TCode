using System.Text;

namespace TCode.Agent.Coding.Tools.Files;

/// <summary>按行的 Myers diff，输出 unified diff（供 UI 渲染，不发给模型）。</summary>
public static class LineDiff
{
    /// <summary>编辑距离超过此值时不再细分，整段视为替换（避免超大改动耗时过久）。</summary>
    private const int MaxEditDistance = 1000;

    public enum Op
    {
        Equal,
        Delete,
        Insert,
    }

    public readonly record struct Line(Op Op, string Text, int OldNumber, int NewNumber);

    public sealed record Result(string UnifiedDiff, int Added, int Removed, int? FirstChangedLine);

    public static Result Compute(string oldText, string newText, string path, int context = 4)
    {
        var a = SplitLines(oldText);
        var b = SplitLines(newText);
        var lines = Diff(a, b);
        var added = lines.Count(l => l.Op == Op.Insert);
        var removed = lines.Count(l => l.Op == Op.Delete);
        int? first = null;
        foreach (var line in lines)
        {
            if (line.Op != Op.Equal)
            {
                first = line.Op == Op.Insert ? line.NewNumber : Math.Max(1, line.NewNumber);
                break;
            }
        }
        return new Result(Unified(lines, path, context), added, removed, first);
    }

    public static string[] SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }
        var lines = text.Split('\n');
        return text.EndsWith('\n') ? lines[..^1] : lines;
    }

    public static List<Line> Diff(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var prefix = 0;
        while (prefix < a.Count && prefix < b.Count && a[prefix] == b[prefix])
        {
            prefix++;
        }
        var suffix = 0;
        while (suffix < a.Count - prefix && suffix < b.Count - prefix && a[a.Count - 1 - suffix] == b[b.Count - 1 - suffix])
        {
            suffix++;
        }

        var result = new List<Line>(a.Count + b.Count);
        for (var i = 0; i < prefix; i++)
        {
            result.Add(new Line(Op.Equal, a[i], i + 1, i + 1));
        }
        var middle = Myers(a, prefix, a.Count - suffix, b, prefix, b.Count - suffix);
        result.AddRange(middle);
        for (var i = 0; i < suffix; i++)
        {
            var ai = a.Count - suffix + i;
            var bi = b.Count - suffix + i;
            result.Add(new Line(Op.Equal, a[ai], ai + 1, bi + 1));
        }
        return result;
    }

    private static List<Line> Myers(IReadOnlyList<string> a, int aStart, int aEnd, IReadOnlyList<string> b, int bStart, int bEnd)
    {
        var n = aEnd - aStart;
        var m = bEnd - bStart;
        var result = new List<Line>();
        if (n == 0 && m == 0)
        {
            return result;
        }
        var limit = Math.Min(n + m, MaxEditDistance);
        // k 的取值范围为 [-d-1, d+1] ⊂ [-limit-1, limit+1]；回溯需要保存每一步的 v（O(limit²) 内存）。
        var offset = limit + 1;
        var v = new int[2 * limit + 3];
        var trace = new List<int[]>();
        var found = false;
        for (var d = 0; d <= limit && !found; d++)
        {
            trace.Add((int[])v.Clone());
            for (var k = -d; k <= d; k += 2)
            {
                int x;
                if (k == -d || (k != d && v[offset + k - 1] < v[offset + k + 1]))
                {
                    x = v[offset + k + 1];
                }
                else
                {
                    x = v[offset + k - 1] + 1;
                }
                var y = x - k;
                while (x < n && y < m && a[aStart + x] == b[bStart + y])
                {
                    x++;
                    y++;
                }
                v[offset + k] = x;
                if (x >= n && y >= m)
                {
                    found = true;
                    break;
                }
            }
        }

        if (!found)
        {
            for (var i = 0; i < n; i++)
            {
                result.Add(new Line(Op.Delete, a[aStart + i], aStart + i + 1, bStart + 1));
            }
            for (var j = 0; j < m; j++)
            {
                result.Add(new Line(Op.Insert, b[bStart + j], aStart + n + 1, bStart + j + 1));
            }
            return result;
        }

        // 回溯
        var px = n;
        var py = m;
        var reversed = new List<Line>();
        for (var d = trace.Count - 1; d >= 0; d--)
        {
            var vd = trace[d];
            var k = px - py;
            int prevK;
            if (k == -d || (k != d && vd[offset + k - 1] < vd[offset + k + 1]))
            {
                prevK = k + 1;
            }
            else
            {
                prevK = k - 1;
            }
            var prevX = vd[offset + prevK];
            var prevY = prevX - prevK;
            while (px > prevX && py > prevY)
            {
                px--;
                py--;
                reversed.Add(new Line(Op.Equal, a[aStart + px], aStart + px + 1, bStart + py + 1));
            }
            if (d > 0)
            {
                if (px == prevX)
                {
                    py--;
                    reversed.Add(new Line(Op.Insert, b[bStart + py], aStart + px + 1, bStart + py + 1));
                }
                else
                {
                    px--;
                    reversed.Add(new Line(Op.Delete, a[aStart + px], aStart + px + 1, bStart + py + 1));
                }
            }
            px = prevX;
            py = prevY;
        }
        reversed.Reverse();
        return reversed;
    }

    private static string Unified(List<Line> lines, string path, int context)
    {
        var sb = new StringBuilder();
        var display = path.Replace('\\', '/');
        sb.Append("--- a/").Append(display.TrimStart('/')).Append('\n');
        sb.Append("+++ b/").Append(display.TrimStart('/')).Append('\n');
        var i = 0;
        while (i < lines.Count)
        {
            if (lines[i].Op == Op.Equal)
            {
                i++;
                continue;
            }
            var start = Math.Max(0, i - context);
            var end = i;
            // 向后扩展：变化之间的相等行不超过 2*context 时合并为一个块
            while (end < lines.Count)
            {
                if (lines[end].Op != Op.Equal)
                {
                    end++;
                    continue;
                }
                var run = 0;
                while (end + run < lines.Count && lines[end + run].Op == Op.Equal)
                {
                    run++;
                }
                if (end + run >= lines.Count || run > 2 * context)
                {
                    end = Math.Min(lines.Count, end + context);
                    break;
                }
                end += run;
            }
            var hunk = lines.GetRange(start, end - start);
            var oldStart = hunk.FirstOrDefault(l => l.Op != Op.Insert).OldNumber;
            var newStart = hunk.FirstOrDefault(l => l.Op != Op.Delete).NewNumber;
            var oldCount = hunk.Count(l => l.Op != Op.Insert);
            var newCount = hunk.Count(l => l.Op != Op.Delete);
            if (oldCount == 0)
            {
                oldStart = Math.Max(0, hunk[0].OldNumber - 1);
            }
            if (newCount == 0)
            {
                newStart = Math.Max(0, hunk[0].NewNumber - 1);
            }
            sb.Append($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@\n");
            foreach (var line in hunk)
            {
                sb.Append(line.Op switch { Op.Insert => '+', Op.Delete => '-', _ => ' ' }).Append(line.Text).Append('\n');
            }
            i = end;
        }
        return sb.ToString();
    }
}
