namespace TKCode.Config;

/// <summary>把内置的 JSON Schema 写到 ~/.tkcode/settings.schema.json，settings.json 通过 "$schema": "./settings.schema.json" 引用。</summary>
public static class SettingsSchema
{
    public static string Text
    {
        get
        {
            using var stream = typeof(SettingsSchema).Assembly.GetManifestResourceStream("TKCode.Config.settings.schema.json")!;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
    }

    /// <summary>内容不同才写入，避免触发不必要的文件监听事件。</summary>
    public static void EnsureWritten(string? path = null)
    {
        path ??= ConfigPaths.SchemaFile;
        try
        {
            var text = Text;
            if (File.Exists(path) && File.ReadAllText(path) == text)
            {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        catch (IOException)
        {
            // 只影响编辑器补全
        }
    }
}
