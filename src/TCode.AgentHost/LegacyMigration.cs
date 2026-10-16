using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TCode.Ai;
using TCode.Config;

namespace TCode.AgentHost;

/// <summary>
/// 把旧的 ~/.tcode/models.json 合并进 settings.json（已存在的厂商不覆盖），原文件改名为 models.json.bak。
/// 旧格式：providers.&lt;id&gt; = { api: "openai-completions", models: [{ id, supportsImages, … }] }。
/// </summary>
internal static class LegacyMigration
{
    public static void Run(ILogger logger)
    {
        var legacy = Path.Combine(ConfigPaths.Home, "models.json");
        if (!File.Exists(legacy))
        {
            return;
        }
        try
        {
            var root = JsonNode.Parse(File.ReadAllText(legacy), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject;
            var providers = root?["providers"] as JsonObject ?? [];
            SettingsFile.Update(settings =>
            {
                var existing = settings["providers"] as JsonObject;
                foreach (var (id, node) in providers)
                {
                    if (node is not JsonObject old || existing?.ContainsKey(id) == true)
                    {
                        continue;
                    }
                    var provider = SettingsFile.Provider(settings, id);
                    foreach (var (key, value) in old)
                    {
                        switch (key)
                        {
                            case "api":
                                provider["api"] = value?.GetValue<string>() switch
                                {
                                    KnownApis.OpenAIChatCompletions => "chat",
                                    KnownApis.OpenAIResponses => "responses",
                                    KnownApis.AnthropicMessages => "messages",
                                    var other => other,
                                };
                                break;
                            case "models" when value is JsonArray list:
                                var models = new JsonObject();
                                foreach (var item in list.OfType<JsonObject>())
                                {
                                    var modelId = item["id"]?.GetValue<string>();
                                    if (modelId is null)
                                    {
                                        continue;
                                    }
                                    var model = new JsonObject();
                                    foreach (var (field, fieldValue) in item)
                                    {
                                        var renamed = field switch
                                        {
                                            "id" => null,
                                            "supportsImages" => "images",
                                            "supportsFastMode" => "fastMode",
                                            _ => field,
                                        };
                                        if (renamed is not null)
                                        {
                                            model[renamed] = fieldValue?.DeepClone();
                                        }
                                    }
                                    models[modelId] = model;
                                }
                                provider["models"] = models;
                                break;
                            default:
                                provider[key] = value?.DeepClone();
                                break;
                        }
                    }
                }
            });
            File.Move(legacy, legacy + ".bak", overwrite: true);
            logger.LogInformation("Migrated {Legacy} into settings.json", legacy);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Could not migrate {Legacy}", legacy);
        }
    }
}
