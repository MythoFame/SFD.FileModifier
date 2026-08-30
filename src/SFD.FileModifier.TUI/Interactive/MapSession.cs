using Microsoft.FSharp.Collections;
using SFD.FileModifier.Core;
using Spectre.Console;

namespace SFD.FileModifier.TUI.Interactive;

// Builds the two-pane view model for a map file: every piece of information the
// document carries, plus every operation that makes sense on a playable map
// (chapters, embedded per-chapter scripts, world settings, locks and metadata).
public static class MapSession
{
    public static FileViewModel Build(string filePath, SfdMap map)
    {
        return new FileViewModel
        {
            SourcePath = filePath,
            FileName = Path.GetFileName(filePath),
            Kind = "map",
            ExpectedExtension = ".sfdm",
            SizeBytes = new FileInfo(filePath).Length,
            BuildSections = () => Sections(filePath, map),
            Operations = Operations(filePath, map),
            Save = path => map.Save(path),
        };
    }

    private static List<InfoSection> Sections(string filePath, SfdMap map)
    {
        var info = map.Info;
        var sections = new List<InfoSection>
        {
            new("File", new List<(string, string)>
            {
                ("Path", Markup.Escape(Path.GetFullPath(filePath))),
                ("Size", View.HumanSize(new FileInfo(filePath).Length)),
                ("Parts", map.PartCount.ToString()),
                ("Thumbnail", View.YesNo(info.HasThumbnail)),
            }),
            new("Identity", new List<(string, string)>
            {
                ("Name", View.None(info.Name)),
                ("Author", View.None(info.Author)),
                ("Guid", info.Guid.ToString()),
                ("Original guid", View.GuidOrNone(info.OriginalGuid)),
                ("Version code", View.None(View.String(info.Version))),
                ("Publish id", View.None(EmptyAsNone(info.PublishExternalId))),
                ("Saved", View.Date(info.SaveDate) is { } date ? date.ToString("yyyy-MM-dd HH:mm") : "[dim](never)[/]"),
            }),
            new("Classification", new List<(string, string)>
            {
                ("Category", $"{map.MapCategory} [dim](mapType {info.MapType})[/]"),
                ("Max players", map.MaxPlayers.ToString()),
                ("Tags", TagsText(map.Tags)),
                ("Game modes", map.GameModes.Length > 0 ? Markup.Escape(string.Join(", ", map.GameModes)) : "[dim](none)[/]"),
                ("Template", View.YesNo(map.IsTemplate)),
            }),
            new("Locks", new List<(string, string)>
            {
                ("Officially locked", View.YesNo(info.IsOfficial)),
                ("Edit lock", View.YesNo(info.EditLock)),
            }),
            new("World", new List<(string, string)>
            {
                ("Camera area", View.None(View.String(map.CameraArea))),
                ("World bottom", View.None(View.String(map.WorldBottom))),
                ("Weather", View.None(View.String(map.Weather))),
                ("Start commands", View.None(View.String(map.StartCommands))),
            }),
        };

        var chapters = map.Chapters.ToList();
        if (chapters.Count > 0)
        {
            var sources = map.ScriptSources;
            var rows = new List<(string, string)>();

            for (var i = 0; i < chapters.Count; i++)
            {
                var part = chapters[i];
                var name = part.Name.Length > 0 ? Markup.Escape(part.Name) : "[dim](unnamed)[/]";
                var selectable = part.Selectable ? " [dim]· selectable[/]" : "";
                rows.Add(($"#{i}", $"{name}{selectable} — script: {View.LineInfo(sources[i])}"));
            }

            sections.Add(new InfoSection($"Chapters ({chapters.Count})", rows));
        }

        return sections;
    }

    private static readonly string[] Run =
                [
                    "Adventure Map", "Melee Map", "Bot Support", "Singleplayer",
                    "Multiplayer", "Optimized for 16 Players", "Customized Gameplay/Rules",
                ];

    private static IReadOnlyList<TuiOperation> Operations(string filePath, SfdMap map)
    {
        return
        [
            new("Toggle official lock", () =>
            {
                if (map.Info.IsOfficial)
                {
                    map.UnlockOfficial();
                }
                else
                {
                    map.LockOfficial();
                }

                return true;
            }),
            new("Toggle author lock (edit lock)", () =>
            {
                map.SetAuthorLock(!map.Info.EditLock);
                return true;
            }),
            new("Set version code…", () =>
            {
                map.SetVersion(Ask.Text("Version code (e.g. v.1.3.4.3):", View.String(map.Info.Version)));
                return true;
            }),
            new("Set publish ID…", () =>
            {
                map.SetPublishId(Ask.Text("Publish ID (10+ digits):", EmptyAsNone(map.Info.PublishExternalId)));
                return true;
            }),
            new("Set map category…", () =>
            {
                var pick = Ask.Choose("Set map category:", ["Versus", "Custom", "Campaign", "Survival", "Challenge"]);
                map.SetMapCategory(pick switch
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
                map.SetMaxPlayers(Ask.Int("Set max players (1-16):", 1, 16));
                return true;
            }),
            new("Set tags…", () =>
            {
                var names = Run;
                var current = map.Tags.Select(View.TagName).Where(names.Contains).ToList();
                var picked = Ask.MultiChoose(
                    "Set tags:",
                    names,
                    current,
                    note: "[dim]Unknown tag ids currently in the file will be replaced.[/]");

                map.SetTags(ListModule.OfSeq(picked.Select(View.TagFromName)));
                return true;
            }),
            new("Toggle template flag", () =>
            {
                map.SetTemplate(!map.Info.IsTemplate);
                return true;
            }),
            new("Set game modes…", () =>
            {
                var modes = SfdGameModes.Known.ToArray();
                var current = map.GameModes.Where(modes.Contains).ToList();
                var picked = Ask.MultiChoose("Set game modes:", modes, current);
                map.SetGameModes(ListModule.OfSeq(picked));
                return true;
            }),
            new("Rename chapter…", () =>
            {
                var chapters = map.Chapters.ToList();

                if (chapters.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]This map has no chapters table.[/]");
                    return false;
                }

                var index = Ask.ChooseIndex(
                    "Rename which chapter?",
                    [.. chapters.Select((c, i) => $"#{i} {(c.Name.Length > 0 ? c.Name : "(unnamed)")}")]);
                var newName = Ask.Text("New chapter name:", chapters[index].Name);

                map.RenameChapter(index, newName);
                return true;
            }),
            new("Export chapter script…", () =>
            {
                var chapters = map.Chapters.ToList();
                var sources = map.ScriptSources;

                if (chapters.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]This map has no chapters table.[/]");
                    return false;
                }

                var index = Ask.ChooseIndex(
                    "Export script of which chapter?",
                    [.. chapters.Select((c, i) => $"#{i} {(c.Name.Length > 0 ? c.Name : "(unnamed)")} — {View.LineInfo(sources[i])}")]);

                var baseName = chapters[index].Name.Length > 0 ? chapters[index].Name : $"chapter{index}";
                var path = Ask.SavePath($"{SafeFileName(baseName)}.cs");

                if (path is null)
                {
                    return false;
                }

                File.WriteAllText(path, View.String(map.GetScriptSourceAt(index)) ?? "");
                AnsiConsole.MarkupLine($"[green]Exported:[/] {Markup.Escape(Path.GetFullPath(path))}");
                return false;
            }),
            new("Replace chapter script…", () =>
            {
                var chapters = map.Chapters.ToList();
                var sources = map.ScriptSources;

                if (chapters.Count == 0)
                {
                    AnsiConsole.MarkupLine("[yellow]This map has no chapters table.[/]");
                    return false;
                }

                var index = Ask.ChooseIndex(
                    "Replace script of which chapter?",
                    [.. chapters.Select((c, i) => $"#{i} {(c.Name.Length > 0 ? c.Name : "(unnamed)")} — {View.LineInfo(sources[i])}")]);
                var path = Ask.ExistingPath("Path to replacement script source:");
                var source = File.ReadAllText(path);

                map.SetScriptSourceAt(index, source);
                return true;
            }),
            new("Export thumbnail…", () =>
            {
                var thumbnail = View.Bytes(map.Thumbnail);

                if (thumbnail is null)
                {
                    AnsiConsole.MarkupLine("[yellow]This map has no thumbnail.[/]");
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
            new("Set camera area…", () =>
            {
                map.SetCameraArea(Ask.Text("Camera area (left,top,right,bottom):", View.String(map.CameraArea)));
                return true;
            }),
            new("Set world bottom…", () =>
            {
                map.SetWorldBottom(Ask.Text("World bottom (number):", View.String(map.WorldBottom)));
                return true;
            }),
            new("Set weather…", () =>
            {
                // The game stores the WeatherType enum name as the property
                // string (0 = None, 1 = Snow, 2 = Rain), so offer exactly those.
                map.SetWeather(Ask.Choose("Set weather:", ["None", "Snow", "Rain"]));
                return true;
            }),
            new("Set start commands…", () =>
            {
                map.SetStartCommands(Ask.Text("Start commands (chat commands run on map start):", View.String(map.StartCommands)));
                return true;
            }),
        ];
    }

    private static string TagsText(IEnumerable<SfdTag> tags)
    {
        var names = tags.Select(View.TagName).ToList();
        return names.Count > 0 ? Markup.Escape(string.Join(", ", names)) : "[dim](none)[/]";

    }

    private static string? EmptyAsNone(string value) => value.Length > 0 ? value : null;

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]);
        return clean.Length > 0 ? clean : "chapter";
    }
}
