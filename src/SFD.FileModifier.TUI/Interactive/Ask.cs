using Spectre.Console;

namespace SFD.FileModifier.TUI.Interactive;

// Thin wrappers over Spectre prompts so operation builders stay terse.
internal static class Ask
{
    public static string Text(string label, string? defaultValue = null)
    {
        var prompt = new TextPrompt<string>($"[bold]{label}[/]").ShowChoices(false);

        if (!string.IsNullOrEmpty(defaultValue))
        {
            prompt.DefaultValue(defaultValue);
        }

        return AnsiConsole.Prompt(prompt);
    }

    public static int Int(string label, int min, int max)
    {
        var prompt = new TextPrompt<int>($"[bold]{label}[/]")
            .Validate(value => value is >= 1 and <= 16
                ? ValidationResult.Success()
                : ValidationResult.Error($"[red]Must be between {min} and {max}.[/]"));

        return AnsiConsole.Prompt(prompt);
    }

    public static string Choose(string title, string[] options) =>
        AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title($"[bold]{title}[/]")
                .AddChoices(options));

    public static int ChooseIndex(string title, string[] options)
    {
        var indexes = Enumerable.Range(0, options.Length).ToList();

        return AnsiConsole.Prompt(
            new SelectionPrompt<int>()
                .Title($"[bold]{title}[/]")
                .AddChoices(indexes)
                .UseConverter(i => options[i]));
    }

    public static List<string> MultiChoose(
        string title,
        string[] options,
        IEnumerable<string> preselected,
        string? note = null)
    {
        var prompt = new MultiSelectionPrompt<string>()
            .Title($"[bold]{title}[/]")
            .AddChoices(options)
            .InstructionsText(note ?? "[dim](space toggles, enter confirms)[/]");

        foreach (var item in preselected)
        {
            prompt.Select(item);
        }

        return AnsiConsole.Prompt(prompt);
    }

    // Empty input cancels (returns null); enter alone accepts the suggestion.
    public static string? SavePath(string suggested)
    {
        var path = AnsiConsole.Prompt(
            new TextPrompt<string>("[bold]Save output as[/] [dim](enter accepts, empty cancels)[/]:")
                .DefaultValue(suggested)
                .AllowEmpty());

        return path.Length == 0 ? null : path;
    }

    public static string ExistingPath(string label) =>
        AnsiConsole.Prompt(
            new TextPrompt<string>($"[bold]{label}[/]")
                .Validate(p => File.Exists(p)
                    ? ValidationResult.Success()
                    : ValidationResult.Error("[red]File not found.[/]")));
}
