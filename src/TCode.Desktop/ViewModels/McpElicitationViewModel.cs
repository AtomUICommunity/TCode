using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCode.Desktop.Localization;
using TCode.Rpc;

namespace TCode.Desktop.ViewModels;

/// <summary>表单字段的种类（规格 §5.1 的受限 schema）。</summary>
public enum McpFieldKind
{
    Text,
    Number,
    Boolean,
    Choice,
    MultiChoice,
}

public sealed partial class McpFieldOptionViewModel(string value, string label) : ObservableObject
{
    public string Value { get; } = value;
    public string Label { get; } = label;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>elicitation 表单中的一个字段。</summary>
public sealed partial class McpFieldViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required McpFieldKind Kind { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }
    public bool Required { get; init; }
    public bool IsInteger { get; init; }
    public string? Format { get; init; }
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    public double? Minimum { get; init; }
    public double? Maximum { get; init; }
    public int? MinItems { get; init; }
    public int? MaxItems { get; init; }
    public ObservableCollection<McpFieldOptionViewModel> Options { get; } = [];

    public string Label => (Title ?? Name) + (Required ? " *" : "");
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool IsText => Kind == McpFieldKind.Text;
    public bool IsNumber => Kind == McpFieldKind.Number;
    public bool IsBoolean => Kind == McpFieldKind.Boolean;
    public bool IsChoice => Kind == McpFieldKind.Choice;
    public bool IsMultiChoice => Kind == McpFieldKind.MultiChoice;

    [ObservableProperty]
    public partial string TextValue { get; set; } = "";

    [ObservableProperty]
    public partial bool BoolValue { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool HasError => Error is not null;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    partial void OnTextValueChanged(string value) => Error = null;

    partial void OnBoolValueChanged(bool value) => Error = null;

    [RelayCommand]
    private void Choose(McpFieldOptionViewModel option)
    {
        foreach (var other in Options)
        {
            other.IsSelected = other == option;
        }
        Error = null;
    }

    /// <summary>校验并取值；未填写的可选字段返回 null（不提交）。</summary>
    internal JsonNode? Read(out bool missing)
    {
        Error = null;
        missing = false;
        switch (Kind)
        {
            case McpFieldKind.Boolean:
                return JsonValue.Create(BoolValue);
            case McpFieldKind.Choice:
                var choice = Options.FirstOrDefault(o => o.IsSelected);
                missing = choice is null;
                return choice is null ? null : JsonValue.Create(choice.Value);
            case McpFieldKind.MultiChoice:
                var chosen = Options.Where(o => o.IsSelected).Select(o => (JsonNode?)JsonValue.Create(o.Value)).ToArray();
                if (MinItems is { } min && chosen.Length < min || MaxItems is { } max && chosen.Length > max)
                {
                    Error = Strings.Format(TCodeLangResourceKind.McpFieldCountFormat, MinItems ?? 0, MaxItems?.ToString() ?? "∞");
                }
                missing = chosen.Length == 0;
                return chosen.Length == 0 ? null : new JsonArray(chosen);
            case McpFieldKind.Number:
                var text = TextValue.Trim();
                if (text.Length == 0)
                {
                    missing = true;
                    return null;
                }
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || IsInteger && number != Math.Floor(number))
                {
                    Error = Strings.Get(IsInteger ? TCodeLangResourceKind.McpFieldInteger : TCodeLangResourceKind.McpFieldNumber);
                    return null;
                }
                if (Minimum is { } lo && number < lo || Maximum is { } hi && number > hi)
                {
                    Error = Strings.Format(TCodeLangResourceKind.McpFieldRangeFormat, Minimum?.ToString(CultureInfo.InvariantCulture) ?? "-∞",
                        Maximum?.ToString(CultureInfo.InvariantCulture) ?? "∞");
                }
                return IsInteger ? JsonValue.Create((long)number) : JsonValue.Create(number);
            default:
                if (TextValue.Length == 0)
                {
                    missing = true;
                    return null;
                }
                if (MinLength is { } minLength && TextValue.Length < minLength || MaxLength is { } maxLength && TextValue.Length > maxLength)
                {
                    Error = Strings.Format(TCodeLangResourceKind.McpFieldLengthFormat, MinLength ?? 0, MaxLength?.ToString() ?? "∞");
                }
                else if (!FormatOk(TextValue))
                {
                    Error = Strings.Format(TCodeLangResourceKind.McpFieldFormatFormat, Format);
                }
                return JsonValue.Create(TextValue);
        }
    }

    private bool FormatOk(string value) => Format switch
    {
        "email" => value.Contains('@') && value.IndexOf('@') > 0 && value.IndexOf('@') < value.Length - 1,
        "uri" => Uri.TryCreate(value, UriKind.Absolute, out _),
        "date" => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        "date-time" => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        _ => true,
    };
}

/// <summary>
/// MCP 服务器的 elicitation 请求（规格 §5.1）：显示请求方服务器；form 模式按受限 schema 生成表单，用户可修改后提交；
/// url 模式显示完整 URL 并突出域名，用户同意后才用系统浏览器打开。
/// </summary>
public sealed partial class McpElicitationViewModel : ObservableObject
{
    private readonly TaskCompletionSource<McpElicitResponse> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<string, bool> _openUrl;

    public McpElicitationViewModel(McpElicitRequest request, Func<string, bool> openUrl)
    {
        Request = request;
        _openUrl = openUrl;
        if (request.Mode == "url")
        {
            if (Uri.TryCreate(request.Url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
            {
                UrlHost = uri.IdnHost;
                IsPunycode = uri.IdnHost.Contains("xn--", StringComparison.OrdinalIgnoreCase) || uri.Host != uri.IdnHost;
                IsInsecure = uri.Scheme == "http";
            }
            else
            {
                IsUnsupported = true;
            }
        }
        else
        {
            IsUnsupported = !ParseSchema(request.SchemaJson);
        }
    }

    public McpElicitRequest Request { get; }
    public Task<McpElicitResponse> Result => _result.Task;
    public event Action<McpElicitationViewModel>? Decided;

    public string Title => Strings.Format(TCodeLangResourceKind.McpElicitTitleFormat, Request.ServerTitle);
    public string Message => Request.Message;
    public bool IsUrl => Request.Mode == "url";
    public bool IsForm => !IsUrl;
    public string? Url => Request.Url;
    public string? UrlHost { get; }
    public bool IsPunycode { get; }
    public bool IsInsecure { get; }
    public bool IsUnsupported { get; }
    public bool CanSubmit => !IsUnsupported;
    public bool ShowFormFields => IsForm && !IsUnsupported;
    public string? SchemaText => IsUnsupported ? Request.SchemaJson : null;
    public ObservableCollection<McpFieldViewModel> Fields { get; } = [];

    [ObservableProperty]
    public partial string? Error { get; private set; }

    public bool HasError => Error is not null;

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private bool ParseSchema(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }
        if (JsonNode.Parse(json) is not JsonObject schema)
        {
            return false;
        }
        var required = (schema["required"] as JsonArray)?.Select(r => r?.ToString()).ToHashSet() ?? [];
        if (schema["properties"] is not JsonObject properties)
        {
            return true;
        }
        foreach (var (name, node) in properties)
        {
            if (node is not JsonObject p || CreateField(name, p, required.Contains(name)) is not { } field)
            {
                return false;
            }
            Fields.Add(field);
        }
        return true;
    }

    private static McpFieldViewModel? CreateField(string name, JsonObject p, bool required)
    {
        var type = p["type"]?.ToString();
        string? Str(string key) => p[key] is JsonValue v ? v.ToString() : null;
        int? Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;
        double? Dbl(string key) => p[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        McpFieldViewModel field;
        if (type == "array")
        {
            if (p["items"] is not JsonObject items)
            {
                return null;
            }
            field = new McpFieldViewModel
            {
                Name = name, Kind = McpFieldKind.MultiChoice, Title = Str("title"), Description = Str("description"), Required = required,
                MinItems = Int(p["minItems"]), MaxItems = Int(p["maxItems"]),
            };
            if (!AddOptions(field, items["enum"] as JsonArray, items["anyOf"] as JsonArray ?? items["oneOf"] as JsonArray))
            {
                return null;
            }
            var defaults = (p["default"] as JsonArray)?.Select(d => d?.ToString()).ToHashSet() ?? [];
            foreach (var option in field.Options)
            {
                option.IsSelected = defaults.Contains(option.Value);
            }
            return field;
        }
        if (type == "string" && (p["enum"] is JsonArray || p["oneOf"] is JsonArray || p["anyOf"] is JsonArray))
        {
            field = new McpFieldViewModel { Name = name, Kind = McpFieldKind.Choice, Title = Str("title"), Description = Str("description"), Required = required };
            if (!AddOptions(field, p["enum"] as JsonArray, p["oneOf"] as JsonArray ?? p["anyOf"] as JsonArray))
            {
                return null;
            }
            var enumNames = (p["enumNames"] as JsonArray)?.Select(n => n?.ToString()).ToList();
            if (enumNames is not null && enumNames.Count == field.Options.Count)
            {
                var options = field.Options.Select((o, i) => new McpFieldOptionViewModel(o.Value, enumNames[i] ?? o.Value)).ToList();
                field.Options.Clear();
                options.ForEach(field.Options.Add);
            }
            if (Str("default") is { } d && field.Options.FirstOrDefault(o => o.Value == d) is { } selected)
            {
                selected.IsSelected = true;
            }
            return field;
        }
        switch (type)
        {
            case "string":
                return new McpFieldViewModel
                {
                    Name = name, Kind = McpFieldKind.Text, Title = Str("title"), Description = Str("description"), Required = required, Format = Str("format"),
                    MinLength = Int(p["minLength"]), MaxLength = Int(p["maxLength"]), TextValue = Str("default") ?? "",
                };
            case "number" or "integer":
                return new McpFieldViewModel
                {
                    Name = name, Kind = McpFieldKind.Number, Title = Str("title"), Description = Str("description"), Required = required,
                    IsInteger = type == "integer", Minimum = Dbl("minimum"), Maximum = Dbl("maximum"),
                    TextValue = p["default"] is JsonValue dv ? dv.ToJsonString() : "",
                };
            case "boolean":
                return new McpFieldViewModel
                {
                    Name = name, Kind = McpFieldKind.Boolean, Title = Str("title"), Description = Str("description"), Required = required,
                    BoolValue = p["default"] is JsonValue b && b.GetValueKind() == JsonValueKind.True,
                };
            default:
                return null;
        }
    }

    private static bool AddOptions(McpFieldViewModel field, JsonArray? values, JsonArray? titled)
    {
        if (values is not null)
        {
            foreach (var value in values.OfType<JsonValue>())
            {
                field.Options.Add(new McpFieldOptionViewModel(value.ToString(), value.ToString()));
            }
        }
        else if (titled is not null)
        {
            foreach (var option in titled.OfType<JsonObject>())
            {
                if (option["const"]?.ToString() is { } value)
                {
                    field.Options.Add(new McpFieldOptionViewModel(value, option["title"]?.ToString() ?? value));
                }
            }
        }
        return field.Options.Count > 0;
    }

    [RelayCommand]
    private void Submit()
    {
        if (IsUnsupported)
        {
            return;
        }
        if (IsUrl)
        {
            // 用户同意后才打开；accept 只表示同意，交互结果由服务器在重试时判断（规格 §5.1）
            if (!_openUrl(Request.Url!))
            {
                Error = Strings.Get(TCodeLangResourceKind.McpElicitOpenFailed);
                return;
            }
            Decide(new McpElicitResponse("accept"));
            return;
        }
        var content = new JsonObject();
        var ok = true;
        foreach (var field in Fields)
        {
            var value = field.Read(out var missing);
            if (field.Error is not null)
            {
                ok = false;
            }
            else if (missing && field.Required)
            {
                field.Error = Strings.Get(TCodeLangResourceKind.McpFieldRequired);
                ok = false;
            }
            else if (value is not null)
            {
                content[field.Name] = value;
            }
        }
        if (ok)
        {
            Decide(new McpElicitResponse("accept", content.ToJsonString()));
        }
    }

    [RelayCommand]
    private void Decline() => Decide(new McpElicitResponse("decline"));

    /// <summary>关闭（未作出选择），会话关闭或断开时同样以 cancel 结束。</summary>
    [RelayCommand]
    public void Cancel() => Decide(new McpElicitResponse("cancel"));

    private void Decide(McpElicitResponse response)
    {
        if (_result.TrySetResult(response))
        {
            Decided?.Invoke(this);
        }
    }
}
