namespace TCode.Desktop.Icons;

/// <summary>文件树 / 标签页的文件类型图标：Lucide 图标 + 调色板颜色键（TCode.File*）。</summary>
public static class FileIcons
{
    public static (LucideKind Icon, string Color) For(string name, bool isDirectory)
    {
        if (isDirectory)
        {
            return (LucideKind.Folder, "TCode.TextSecondary");
        }
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("dockerfile", StringComparison.Ordinal) || lower == ".dockerignore" || lower.StartsWith("docker-compose", StringComparison.Ordinal))
        {
            return (LucideKind.Box, "TCode.FileBlue");
        }
        if (lower is ".gitignore" or ".gitattributes" or ".gitmodules")
        {
            return (LucideKind.GitBranch, "TCode.FileRed");
        }
        if (lower.StartsWith(".env", StringComparison.Ordinal))
        {
            return (LucideKind.Settings, "TCode.FileGray");
        }
        return Path.GetExtension(lower) switch
        {
            ".md" or ".mdx" or ".txt" or ".rst" => (LucideKind.FileText, "TCode.FileBlue"),
            ".json" or ".jsonc" or ".json5" => (LucideKind.Braces, "TCode.FileYellow"),
            ".cs" or ".csx" or ".fs" or ".vb" => (LucideKind.FileCode, "TCode.FilePurple"),
            ".sln" or ".slnx" or ".csproj" or ".fsproj" or ".props" or ".targets" => (LucideKind.Layers, "TCode.FilePurple"),
            ".ts" or ".tsx" or ".mts" or ".cts" => (LucideKind.FileCode, "TCode.FileBlue"),
            ".js" or ".jsx" or ".mjs" or ".cjs" => (LucideKind.FileCode, "TCode.FileYellow"),
            ".py" or ".go" or ".kt" or ".java" or ".swift" or ".c" or ".h" or ".cpp" or ".hpp" => (LucideKind.FileCode, "TCode.FileBlue"),
            ".rs" or ".rb" or ".php" => (LucideKind.FileCode, "TCode.FileOrange"),
            ".axaml" or ".xaml" or ".xml" or ".html" or ".htm" or ".vue" or ".svelte" => (LucideKind.FileCode, "TCode.FileOrange"),
            ".css" or ".scss" or ".less" => (LucideKind.FileCode, "TCode.FileBlue"),
            ".yml" or ".yaml" or ".toml" or ".ini" or ".editorconfig" or ".conf" => (LucideKind.Settings, "TCode.FileRed"),
            ".sql" or ".db" or ".sqlite" => (LucideKind.Database, "TCode.FileOrange"),
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".svg" or ".webp" or ".ico" => (LucideKind.Image, "TCode.FileGreen"),
            ".sh" or ".zsh" or ".bash" or ".ps1" or ".cmd" or ".bat" => (LucideKind.SquareTerminal, "TCode.FileGreen"),
            _ => (LucideKind.File, "TCode.FileGray"),
        };
    }
}
