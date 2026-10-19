using AtomUI.Controls;
using Avalonia;
using Avalonia.Media;

namespace TKCode.Desktop.Icons;

public enum LucideKind
{
    SquarePen, Search, Bot, LayoutGrid, Plus, Folder, FolderPlus, ListFilter, Settings,
    PanelLeft, PanelRight, ArrowLeft, ArrowRight, ArrowUp, Mic, ChevronDown, ChevronRight,
    Check, GitBranch, Laptop, GitCompare, Globe, SquareTerminal, File, FileText, Ellipsis,
    ExternalLink, Rocket, Database, ListChecks, Bug, Square, X, RefreshCw, Star, Sun, Moon,
    Lightbulb, Wrench, Copy, Layers, Maximize, Brain, CircleAlert,
    StarFilled, Box, Paperclip, Monitor, Zap, SlidersHorizontal, CornerDownLeft, MessageSquare, ArrowDown, Languages, StopFilled, Eye, EyeOff, Image, Trash, Pencil, FolderTree, Maximize2, Minimize2, FileCode, Braces, History, BookOpen, Crosshair, Code, ZoomIn, ZoomOut, Scan,
    ShieldQuestion, ShieldCheck, ShieldAlert, Pin, Plug, LogIn, Download, Upload, Sparkles, GitCommit, MessageCircleQuestion,
    ClipboardCheck, ClipboardList,
}

/// <summary>
/// Lucide 风格的线性图标（24×24、2px 圆头描边），基于 AtomUI <see cref="Icon"/>，
/// 因此能作为 atom:Button.Icon 使用并跟随按钮前景色（悬停/禁用）。
/// </summary>
public sealed class LucideIcon : Icon
{
    public static readonly StyledProperty<LucideKind> KindProperty =
        AvaloniaProperty.Register<LucideIcon, LucideKind>(nameof(Kind));

    private static readonly Dictionary<LucideKind, DrawingInstruction[]> Cache = new();

    static LucideIcon()
    {
        AffectsRender<LucideIcon>(KindProperty);
    }

    public LucideIcon()
    {
        IconTheme = IconThemeType.Outlined;
        ViewBox = new Rect(0, 0, 24, 24);
        StrokeWidth = 2;
        StrokeLineCap = PenLineCap.Round;
        StrokeLineJoin = PenLineJoin.Round;
    }

    public LucideIcon(LucideKind kind) : this() => Kind = kind;

    public LucideKind Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    protected override IList<DrawingInstruction> DrawingInstructions => Get(Kind);

    public override Icon CreateInstance() => new LucideIcon(Kind);

    private static DrawingInstruction[] Get(LucideKind kind)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(kind, out var instructions))
            {
                instructions = LucidePaths.For(kind).Select(Create).ToArray();
                Cache[kind] = instructions;
            }
            return instructions;
        }
    }

    private static DrawingInstruction Create(string data)
    {
        var filled = data.StartsWith('#');
        return new PathDrawingInstruction
        {
            Data = StreamGeometry.Parse(filled ? data[1..] : data),
            StrokeBrush = IconBrushType.Stroke,
            FillBrush = filled ? IconBrushType.Stroke : null,
            IsStrokeEnabled = true,
            IsStrokeWidthCustomizable = true,
            IsStrokeLinecapCustomizable = true,
            IsStrokeLinejoinCustomizable = true,
        };
    }
}
