using Microsoft.FSharp.Collections;
using SFD.FileModifier.Core;
using Spectre.Console;

namespace SFD.FileModifier.TUI.Interactive;

// Builds the two-pane view model for an extension script file. Script worlds
// are never played in-game, so no chapters, world settings or per-chapter
// scripts appear here: only metadata, locks and the embedded C# source.
public static class ScriptSession
{
    public static FileViewModel Build(string filePath, SfdScript script)
    {
        return new FileViewModel
        {
            SourcePath = filePath,
            FileName = Path.GetFileName(filePath),
            Kind = "extension script",
            ExpectedExtension = ".sfde",
            SizeBytes = new FileInfo(filePath).Length,
            BuildSections = () => Sections(filePath, script),
            Operations = Operations(filePath, script),
            Save = path => script.Save(path),
        };
    }

    private static IReadOnlyList<InfoSection> Sections(string filePath, SfdScript script)
    {
        var info = script.Info;
        var source = View.String(script.ScriptSource) ?? "";

        return
        [
            new("File", new List<(string, string)>
            {
                ("Path", Markup.Escape(Path.GetFullPath(filePath))),
                ("Size", View.HumanSize(new FileInfo(filePath).Length)),
            }),
            new("Identity", new List<(string, string)>
            {
                ("Name", View.None(info.Name)),
                ("Author", View.None(info.Author)),
                ("Guid", info.Guid.ToString()),
                ("Original guid", View.GuidOrNone(info.OriginalGuid)),
                ("Version code", View.None(View.String(info.Version))),
                ("Publish id", info.PublishExternalId.Length > 0 ? info.PublishExternalId : "[dim](none)[/]"),
                ("Saved", View.Date(info.SaveDate) is { } date ? date.ToString("yyyy-MM-dd HH:mm") : "[dim](never)[/]"),
            }),
            new("Classification", new List<(string, string)>
            {
                ("Category", $"{script.MapCategory} [dim](mapType {info.MapType})[/]"),
                ("Max players", script.MaxPlayers.ToString()),
                ("Tags", script.Tags.Select(View.TagName).ToList() is { Count: > 0 } tags ? Markup.Escape(string.Join(", ", tags)) : "[dim](none)[/]"),
                ("Game modes", script.GameModes.Length > 0 ? Markup.Escape(string.Join(", ", script.GameModes)) : "[dim](none)[/]"),
                ("Template", View.YesNo(script.IsTemplate)),
            }),
            new("Locks", new List<(string, string)>
            {
                ("Officially locked", View.YesNo(info.IsOfficial)),
                ("Edit lock", View.YesNo(info.EditLock)),
            }),
            new("Source", new List<(string, string)>
            {
                ("Length", View.LineInfo(source)),
                ("Characters", source.Length.ToString("N0")),
            }),
        ];
    }

    private static readonly string[] Run =
    [
        "Adventure Map", "Melee Map", "Bot Support", "Singleplayer",
        "Multiplayer", "Optimized for 16 Players", "Customized Gameplay/Rules",
    ];

    private static IReadOnlyList<TuiOperation> Operations(string filePath, SfdScript script)
    {
        return
        [
            new("Toggle official lock", () =>
            {
                if (script.Info.IsOfficial)
                {
                    script.UnlockOfficial();
                }
                else
                {
                    script.LockOfficial();
                }

                return true;
            }),
            new("Toggle author lock (edit lock)", () =>
            {
                script.SetAuthorLock(!script.Info.EditLock);
                return true;
            }),
            new("Set version code…", () =>
            {
                script.SetVersion(Ask.Text("Version code (e.g. v.1.3.4.3):", View.String(script.Info.Version)));
                return true;
            }),
            new("Set publish ID…", () =>
            {
                script.SetPublishId(Ask.Text("Publish ID (10+ digits):", script.Info.PublishExternalId.Length > 0 ? script.Info.PublishExternalId : null));
                return true;
            }),
            new("Set map category…", () =>
            {
                var pick = Ask.Choose("Set map category:", ["Versus", "Custom", "Campaign", "Survival", "Challenge"]);
                script.SetMapCategory(pick switch
                {
                    "Versus" => SfdMapCategory.Versus,
                    "Custom" => SfdMapCategory.Custom,
                    "Campaign" => SfdMapCategory.Campaign,
                    "Survival" => SfdMapCategory.Survival,
                    _ => SfdMapCategory.Challenge,
                });
                return true;
            }),
            new("Set max players…", () =>
            {
                script.SetMaxPlayers(Ask.Int("Set max players (1-16):", 1, 16));
                return true;
            }),
            new("Set tags…", () =>
            {
                var names = Run;
                var current = script.Tags.Select(View.TagName).Where(names.Contains).ToList();
                var picked = Ask.MultiChoose(
                    "Set tags:",
                    names,
                    current,
                    note: "[dim]Unknown tag ids currently in the file will be replaced.[/]");

                script.SetTags(ListModule.OfSeq(picked.Select(View.TagFromName)));
                return true;
            }),
            new("Toggle template flag", () =>
            {
                script.SetTemplate(!script.Info.IsTemplate);
                return true;
            }),
            new("Set game modes…", () =>
            {
                var modes = SfdGameModes.Known.ToArray();
                var current = script.GameModes.Where(modes.Contains).ToList();
                var picked = Ask.MultiChoose("Set game modes:", modes, current);
                script.SetGameModes(ListModule.OfSeq(picked));
                return true;
            }),
            new("Export script source…", () =>
            {
                var baseName = Path.GetFileNameWithoutExtension(script.Info.Name.Length > 0 ? script.Info.Name : "script");
                var path = Ask.SavePath($"{baseName}.cs");

                if (path is null)
                {
                    return false;
                }

                File.WriteAllText(path, View.String(script.ScriptSource) ?? "");
                AnsiConsole.MarkupLine($"[green]Exported:[/] {Markup.Escape(Path.GetFullPath(path))}");
                return false;
            }),
            new("Export thumbnail…", () =>
            {
                var thumbnail = View.Bytes(script.Thumbnail);

                if (thumbnail is null)
                {
                    AnsiConsole.MarkupLine("[yellow]This file has no thumbnail.[/]");
                    return false;
                }

                var suggested = $"{Path.GetFileNameWithoutExtension(filePath)}_thumbnail.jpg";
                var path = Ask.SavePath(suggested);

                if (path is null)
                {
                    return false;
                }

                File.WriteAllBytes(path, thumbnail);
                AnsiConsole.MarkupLine($"[green]Exported:[/] {Markup.Escape(Path.GetFullPath(path))}");
                return false;
            }),
            new("Replace script source…", () =>
            {
                var path = Ask.ExistingPath("Path to replacement script source:");
                var source = File.ReadAllText(path);

                script.SetScriptSource(source);
                return true;
            }),
        ];
    }
}
