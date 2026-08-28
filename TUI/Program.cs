using System.Reflection;
using Spectre.Console;

// The version lives in TUI.csproj (<Version>). The SDK build flag
// IncludeSourceRevisionInInformationalVersion is disabled there, so the
// informational attribute contains the plain version number, nothing else.
var version =
    Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
    ?? "0.0.0";

foreach (var arg in args)
{
    switch (arg)
    {
        case "-h" or "--help":
            PrintHelp(version);
            return 0;

        case "-V" or "--version":
            AnsiConsole.MarkupLineInterpolated($"[bold]SFD.FileModifier.TUI[/] v{version}");
            return 0;

        default:
            // Handled below: the first positional argument is the file path.
            break;
    }
}

string? filePath = null;

foreach (var arg in args)
{
    if (filePath is null)
    {
        filePath = arg;
    }
    else
    {
        AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] unexpected argument '{Markup.Escape(arg)}'.");
        AnsiConsole.MarkupLine("[grey]Only one file path is accepted. Use -h for help.[/]");
        return 1;
    }
}

if (filePath is null)
{
    AnsiConsole.MarkupLine("[red]Error:[/] a file path is required.");
    AnsiConsole.WriteLine();
    PrintHelp(version);
    return 1;
}

if (!File.Exists(filePath))
{
    AnsiConsole.MarkupLineInterpolated($"[red]Error:[/] file not found '{Markup.Escape(filePath)}'.");
    return 1;
}

var extension = Path.GetExtension(filePath).ToLowerInvariant();
var kind = extension switch
{
    ".sfdm" => "map",
    ".sfde" => "extension script",
    _ => null,
};

if (kind is null)
{
    AnsiConsole.MarkupLineInterpolated(
        $"[red]Error:[/] '{Markup.Escape(filePath)}' is not a Superfighters Deluxe file. Expected .sfdm (map) or .sfde (extension script).");
    return 1;
}

AnsiConsole.Write(
    new Rule($"[green]{Markup.Escape(Path.GetFileName(filePath))}[/]")
        .RuleStyle("grey")
        .LeftJustified());

AnsiConsole.MarkupLineInterpolated($"[bold]Type[/]      : {kind}");
AnsiConsole.MarkupLineInterpolated($"[bold]Path[/]      : {Markup.Escape(Path.GetFullPath(filePath))}");
AnsiConsole.MarkupLineInterpolated($"[bold]Size[/]      : {new FileInfo(filePath).Length:N0} bytes");

return 0;

static void PrintHelp(string version)
{
    AnsiConsole.MarkupLineInterpolated($"[bold]SFD.FileModifier.TUI[/] v{version}");
    AnsiConsole.WriteLine();
    AnsiConsole.WriteLine("A tool to modify Superfighters Deluxe maps (.sfdm) and extension scripts (.sfde).");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Usage:[/]");
    AnsiConsole.MarkupLine("  SFD.FileModifier [[file]] [[options]]");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Arguments:[/]");
    AnsiConsole.MarkupLine("  [[file]]       Path to a map (.sfdm) or extension script (.sfde). Required.");
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold]Options:[/]");
    AnsiConsole.MarkupLine("  -h, --help     Show this help and exit.");
    AnsiConsole.MarkupLine("  -V, --version  Show version information and exit.");
}
