namespace TKCode.Desktop.Icons;

/// <summary>文件树 / 标签页的文件类型图标：Lucide 图标 + 调色板颜色键（TKCode.File*）。</summary>
public static class FileIcons
{
    public static (LucideKind Icon, string Color) For(string name, bool isDirectory)
    {
        if (isDirectory)
        {
            return (LucideKind.Folder, "TKCode.TextSecondary");
        }
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("dockerfile", StringComparison.Ordinal) || lower == ".dockerignore" || lower.StartsWith("docker-compose", StringComparison.Ordinal))
        {
            return (LucideKind.Box, "TKCode.FileBlue");
        }
        if (lower is ".gitignore" or ".gitattributes" or ".gitmodules")
        {
            return (LucideKind.GitBranch, "TKCode.FileRed");
        }
        if (lower.StartsWith(".env", StringComparison.Ordinal))
        {
            return (LucideKind.Settings, "TKCode.FileGray");
        }
        return Path.GetExtension(lower) switch
        {
            ".md" or ".mdx" or ".txt" or ".rst" => (LucideKind.FileText, "TKCode.FileBlue"),
            ".json" or ".jsonc" or ".json5" => (LucideKind.Braces, "TKCode.FileYellow"),
            ".cs" or ".csx" or ".fs" or ".vb" => (LucideKind.FileCode, "TKCode.FilePurple"),
            ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".props" or ".targets" => (LucideKind.Layers, "TKCode.FilePurple"),
            ".ts" or ".tsx" or ".mts" or ".cts" => (LucideKind.FileCode, "TKCode.FileBlue"),
            ".js" or ".jsx" or ".mjs" or ".cjs" => (LucideKind.FileCode, "TKCode.FileYellow"),
            ".py" or ".go" or ".kt" or ".java" or ".swift" or ".c" or ".h" or ".cpp" or ".hpp" => (LucideKind.FileCode, "TKCode.FileBlue"),
            ".rs" or ".rb" or ".php" => (LucideKind.FileCode, "TKCode.FileOrange"),
            ".axaml" or ".xaml" or ".xml" or ".html" or ".htm" or ".vue" or ".svelte" => (LucideKind.FileCode, "TKCode.FileOrange"),
            ".css" or ".scss" or ".less" => (LucideKind.FileCode, "TKCode.FileBlue"),
            ".yml" or ".yaml" or ".toml" or ".ini" or ".editorconfig" or ".conf" => (LucideKind.Settings, "TKCode.FileRed"),
            ".sql" or ".db" or ".sqlite" => (LucideKind.Database, "TKCode.FileOrange"),
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" or ".ico" => (LucideKind.Image, "TKCode.FileGreen"),
            ".sh" or ".zsh" or ".bash" or ".ps1" or ".cmd" or ".bat" => (LucideKind.SquareTerminal, "TKCode.FileGreen"),
            _ => (LucideKind.File, "TKCode.FileGray"),
        };
    }
}
