using Microsoft.FSharp.Core;
using SFD.FileModifier.Core;
using Spectre.Console;

namespace SFD.FileModifier.TUI.Interactive;

/// One titled block of the information pane: a title plus aligned label/value rows.
public sealed record InfoSection(string Title, IReadOnlyList<(string Label, string Value)> Rows);

/// One selectable entry of the operations pane. Run() prompts for any missing
/// input, applies the operation to the in-memory document and returns true when
/// the document was modified (false = cancelled or nothing to save).
public sealed record TuiOperation(string Label, Func<bool> Run);

/// Everything the two-pane session needs to render and act, regardless of the
/// underlying file kind (map or extension script).
public sealed class FileViewModel
{
    public required string SourcePath { get; init; }
    public required string FileName { get; init; }
    public required string Kind { get; init; }
    public required string ExpectedExtension { get; init; }
    public required long SizeBytes { get; init; }
    public required Func<IReadOnlyList<InfoSection>> BuildSections { get; init; }
    public required IReadOnlyList<TuiOperation> Operations { get; init; }
    public required Action<string> Save { get; init; }
}

// Small helpers bridging F# Core types (options, DUs) and display formatting.
internal static class View
{
    // F# options compile to a null reference for None when consumed from C#,
    // and to an FSharpOption<T> instance for Some.
    public static string? String(FSharpOption<string>? option) =>
        option?.Value;

    public static DateTime? Date(FSharpOption<DateTime>? option) =>
        option?.Value;

    public static string GuidOrNone(FSharpOption<Guid>? option) =>
        option is not null ? option.Value.ToString() : "[dim](none)[/]";

    public static byte[]? Bytes(FSharpOption<byte[]>? option) =>
        option is not null ? option.Value : null;

    public static string None(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "[dim](none)[/]" : Markup.Escape(value!);

    public static string YesNo(bool value) => value ? "[green]yes[/]" : "[dim]no[/]";

    public static string HumanSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:F1} MB" : $"{bytes / 1024.0:F1} KB";

    public static string LineInfo(string source)
    {
        if (string.IsNullOrEmpty(source))
        {
            return "[dim](empty)[/]";
        }

        var lines = source.Split('\n').Length;
        return $"{lines:N0} lines, {source.Length:N0} chars";
    }

    public static string TagName(SfdTag tag) =>
        tag.IsAdventureMap ? "Adventure Map"
        : tag.IsMeleeMap ? "Melee Map"
        : tag.IsBotSupport ? "Bot Support"
        : tag.IsSingleplayer ? "Singleplayer"
        : tag.IsMultiplayer ? "Multiplayer"
        : tag.IsOptimizedFor16Players ? "Optimized for 16 Players"
        : tag.IsCustomizedGameplayRules ? "Customized Gameplay/Rules"
        : $"Unknown tag ({((SfdTag.UnknownTag)tag).Item})";

    public static SfdTag TagFromName(string name) => name switch
    {
        "Adventure Map" => SfdTag.AdventureMap,
        "Melee Map" => SfdTag.MeleeMap,
        "Bot Support" => SfdTag.BotSupport,
        "Singleplayer" => SfdTag.Singleplayer,
        "Multiplayer" => SfdTag.Multiplayer,
        "Optimized for 16 Players" => SfdTag.OptimizedFor16Players,
        "Customized Gameplay/Rules" => SfdTag.CustomizedGameplayRules,
        _ => throw new ArgumentException($"Unknown tag name '{name}'.", nameof(name)),
    };
}
