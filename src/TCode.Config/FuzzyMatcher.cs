namespace TCode.Config;

/// <summary>模糊匹配的结果：分数越高越好；<see cref="Positions"/> 为命中字符在候选中的下标（升序，用于高亮）。</summary>
public readonly record struct FuzzyMatch(int Score, int[] Positions);

/// <summary>
/// 模糊匹配（docs/design/composer.md §8.2），参考 fzf v2 的打分：查询字符按顺序出现在候选中即命中（不区分大小写）。
/// - 每个命中字符加分；命中在单词边界（开头、路径分隔符、-_. 与空白之后）或驼峰边界上额外加分，首个字符的边界加成加倍；
/// - 连续命中加分；中间的空隙按开头与延长分别扣分；
/// - <c>nameStart</c> 之后（文件名部分）的命中额外加分，让文件名命中排在目录命中之前。
/// 用动态规划求最高分的对齐；先做一遍子序列检查，绝大多数不匹配的候选在这里就被排除。
/// </summary>
public static class FuzzyMatcher
{
    private const int ScoreMatch = 16;
    private const int GapStart = 3;
    private const int GapExtension = 1;
    private const int BonusBoundary = 8;
    private const int BonusCamel = 7;
    private const int BonusConsecutive = 4;
    private const int BonusName = 2;
    private const int FirstCharMultiplier = 2;

    /// <summary>匹配并给出命中位置；不匹配时为 null。空查询匹配任何候选（分数 0）。</summary>
    public static FuzzyMatch? Match(string query, string candidate, int nameStart = 0) => Run(query, candidate, nameStart, positions: true);

    /// <summary>只算分数（不记录位置，用于大量候选的初筛）；不匹配时为 null。</summary>
    public static int? Score(string query, string candidate, int nameStart = 0) => Run(query, candidate, nameStart, positions: false)?.Score;

    /// <summary>路径中文件名部分的起点（最后一个分隔符之后）。</summary>
    public static int NameStart(string path)
    {
        var trimmed = path.AsSpan().TrimEnd('/');
        return trimmed.LastIndexOfAny('/', '\\') + 1;
    }

    private static FuzzyMatch? Run(string query, string candidate, int nameStart, bool positions)
    {
        if (query.Length == 0)
        {
            return new FuzzyMatch(0, []);
        }
        if (query.Length > candidate.Length || !IsSubsequence(query, candidate))
        {
            return null;
        }
        var n = candidate.Length;
        // 只保留上一行与当前行（大量候选打分时不分配堆内存）；记录位置时另外保存回溯表
        const int StackLimit = 256;
        var rented = n > StackLimit ? System.Buffers.ArrayPool<int>.Shared.Rent(n * 5) : null;
        var buffer = rented is null ? stackalloc int[n * 5] : rented.AsSpan(0, n * 5);
        try
        {
            var from = positions ? new int[query.Length, n] : null;
            var (top, last) = Align(query, candidate, nameStart, buffer, from);
            if (last < 0)
            {
                return null;
            }
            if (from is null)
            {
                return new FuzzyMatch(top, []);
            }
            var hits = new int[query.Length];
            for (int i = query.Length - 1, j = last; i >= 0; i--)
            {
                hits[i] = j;
                j = i > 0 ? from[i, j] : j;
            }
            return new FuzzyMatch(top, hits);
        }
        finally
        {
            if (rented is not null)
            {
                System.Buffers.ArrayPool<int>.Shared.Return(rented);
            }
        }
    }

    /// <summary>
    /// 动态规划对齐：score[j] 为查询前 i+1 个字符已对齐、第 i 个落在候选第 j 个字符时的最高分（int.MinValue 表示不可达）。
    /// 返回最高分与最后一个查询字符的位置（不匹配时位置为 -1）。
    /// </summary>
    private static (int Score, int Last) Align(string query, string candidate, int nameStart, Span<int> buffer, int[,]? from)
    {
        var n = candidate.Length;
        var bonuses = buffer[..n];
        var previous = buffer.Slice(n, n);
        var previousRun = buffer.Slice(n * 2, n);
        var current = buffer.Slice(n * 3, n);
        var currentRun = buffer.Slice(n * 4, n);
        for (var j = 0; j < n; j++)
        {
            bonuses[j] = BonusAt(candidate, j) + (j >= nameStart ? BonusName : 0);
        }
        for (var i = 0; i < query.Length; i++)
        {
            var q = char.ToLowerInvariant(query[i]);
            // 空隙路线的最佳来源：max over k (previous[k] - 空隙罚分)，随 j 递推
            var carry = int.MinValue;
            var carryFrom = -1;
            for (var j = 0; j < n; j++)
            {
                if (i > 0 && j >= 2 && previous[j - 2] != int.MinValue)
                {
                    var fresh = previous[j - 2] - GapStart;
                    var extended = carry == int.MinValue ? int.MinValue : carry - GapExtension;
                    if (fresh >= extended)
                    {
                        carry = fresh;
                        carryFrom = j - 2;
                    }
                    else
                    {
                        carry = extended;
                    }
                }
                else if (carry != int.MinValue)
                {
                    carry -= GapExtension;
                }
                current[j] = int.MinValue;
                currentRun[j] = 0;
                if (char.ToLowerInvariant(candidate[j]) != q)
                {
                    continue;
                }
                var bonus = bonuses[j];
                if (i == 0)
                {
                    // 第一个字符：之前的字符都算空隙，但只扣一次开头罚分（不按长度累计，避免过度偏向开头）
                    current[j] = ScoreMatch + bonus * FirstCharMultiplier - (j > 0 ? GapStart : 0);
                    currentRun[j] = 1;
                    continue;
                }
                var best = int.MinValue;
                var bestFrom = -1;
                var run = 1;
                if (j > 0 && previous[j - 1] != int.MinValue)
                {
                    // 连续命中：沿用这一串开头的边界加成（与 fzf 一致），至少给连续加成
                    var chained = Math.Max(BonusConsecutive, Math.Max(bonus, bonuses[j - previousRun[j - 1]]));
                    best = previous[j - 1] + ScoreMatch + chained;
                    bestFrom = j - 1;
                    run = previousRun[j - 1] + 1;
                }
                if (carry != int.MinValue && carry + ScoreMatch + bonus > best)
                {
                    best = carry + ScoreMatch + bonus;
                    bestFrom = carryFrom;
                    run = 1;
                }
                if (best == int.MinValue)
                {
                    continue;
                }
                current[j] = best;
                currentRun[j] = run;
                if (from is not null)
                {
                    from[i, j] = bestFrom;
                }
            }
            if (i + 1 < query.Length)
            {
                current.CopyTo(previous);
                currentRun.CopyTo(previousRun);
            }
        }
        var last = -1;
        var top = int.MinValue;
        for (var j = 0; j < n; j++)
        {
            if (current[j] > top)
            {
                top = current[j];
                last = j;
            }
        }
        return (top, last);
    }

    private static bool IsSubsequence(string query, string candidate)
    {
        var i = 0;
        foreach (var c in candidate)
        {
            if (char.ToLowerInvariant(c) == char.ToLowerInvariant(query[i]) && ++i == query.Length)
            {
                return true;
            }
        }
        return false;
    }

    private static int BonusAt(string text, int index)
    {
        if (index == 0)
        {
            return BonusBoundary;
        }
        var previous = text[index - 1];
        var current = text[index];
        if (previous is '/' or '\\' or '-' or '_' or '.' or ':' || char.IsWhiteSpace(previous))
        {
            return BonusBoundary;
        }
        if (char.IsLower(previous) && char.IsUpper(current) || !char.IsDigit(previous) && char.IsDigit(current))
        {
            return BonusCamel;
        }
        return 0;
    }
}
