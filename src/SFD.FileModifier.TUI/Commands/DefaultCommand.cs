using System.ComponentModel;
using System.Reflection;
using SFD.FileModifier.TUI.Interactive;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SFD.FileModifier.TUI.Commands;

// The version lives in TUI.csproj (<Version>). The SDK build flag
// IncludeSourceRevisionInInformationalVersion is disabled there, so the
// informational attribute contains the plain version number, nothing else.
internal static class AppInfo
{
    public static string Version =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "0.0.0";

    public const string Name = "SFD.FileModifier.TUI";
}

[Description("Modify Superfighters Deluxe maps (.sfdm) and extension scripts (.sfde).")]
public sealed class DefaultCommand : Command<DefaultCommandSettings>
{
    protected override int Execute(CommandContext context, DefaultCommandSettings settings, CancellationToken cancellationToken)
    {
        if (settings.ShowVersion)
        {
            AnsiConsole.MarkupLineInterpolated($"[bold]{AppInfo.Name}[/] v{AppInfo.Version}");
            return 0;
        }

        if (string.IsNullOrWhiteSpace(settings.FilePath))
        {
            AnsiConsole.MarkupLine("[red]Error:[/] a file path is required.");
            AnsiConsole.MarkupLine("[dim]Use -h or --help for usage.[/]");
            return 1;
        }

        if (!File.Exists(settings.FilePath))
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Error:[/] file not found '{Markup.Escape(settings.FilePath)}'.");
            return 1;
        }

        var extension = Path.GetExtension(settings.FilePath).ToLowerInvariant();
        var kind = extension switch
        {
            ".sfdm" => "map",
            ".sfde" => "extension script",
            _ => null,
        };

        if (kind is null)
        {
            AnsiConsole.MarkupLineInterpolated(
                $"[red]Error:[/] '{Markup.Escape(settings.FilePath)}' is not a Superfighters Deluxe file. Expected .sfdm (map) or .sfde (extension script).");
            return 1;
        }

        FileViewModel vm;

        try
        {
            vm = extension == ".sfdm"
                ? MapSession.Build(settings.FilePath, Core.SfdMap.Load(settings.FilePath))
                : ScriptSession.Build(settings.FilePath, Core.SfdScript.Load(settings.FilePath));
        }
        catch (Core.SfdException ex)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        return new SessionRunner(vm).Run();
    }
}

public sealed class DefaultCommandSettings : CommandSettings
{
    [CommandArgument(0, "[file]")]
    [Description("Path to a map (.sfdm) or extension script (.sfde) (required)")]
    public string? FilePath { get; init; }

    [CommandOption("-V|--version")]
    [Description("Show version information and exit.")]
    public bool ShowVersion { get; init; }
}
