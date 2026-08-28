using SFD.FileModifier.Core;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace SFD.FileModifier.TUI.Interactive;

// The interactive two-pane session: left pane shows live document information,
// right pane lists operations. Selecting an operation applies it to memory and
// offers to save + exit; Esc quits (with a save prompt when changes are dirty).
public sealed class SessionRunner(FileViewModel vm)
{
    private readonly FileViewModel _vm = vm;
    private bool _dirty;
    private int _selected;

    public int Run()
    {
        if (Console.IsInputRedirected)
        {
            AnsiConsole.MarkupLine("[red]Error:[/] the interactive editor requires a terminal (piped input detected).");
            return 1;
        }

        Console.CancelKeyPress += OnCancel;

        try
        {
            while (true)
            {
                var choice = ShowMenu();

                if (choice < 0)
                {
                    if (_dirty)
                    {
                        if (AnsiConsole.Confirm("[bold]Unsaved changes — save and exit?[/]"))
                        {
                            if (SaveAndReport())
                            {
                                return 0;
                            }

                            continue;
                        }

                        AnsiConsole.MarkupLine("[dim]No file written.[/]");
                    }

                    return 0;
                }

                var op = _vm.Operations[choice];
                var applied = false;

                try
                {
                    applied = op.Run();
                }
                catch (SfdException ex)
                {
                    AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                }

                if (!applied)
                {
                    continue;
                }

                _dirty = true;
                AnsiConsole.MarkupLine($"[green]✓[/] {Markup.Escape(op.Label)} applied.");

                if (AnsiConsole.Confirm("[bold]Save and exit?[/]"))
                {
                    if (SaveAndReport())
                    {
                        return 0;
                    }

                    AnsiConsole.MarkupLine("[dim]Choose another operation, or press Esc to quit without saving.[/]");
                }
            }
        }
        finally
        {
            Console.CancelKeyPress -= OnCancel;
        }
    }

    private static void OnCancel(object? sender, ConsoleCancelEventArgs e)
    {
        // Restore the terminal (cursor, colors) before the process dies.
        AnsiConsole.Reset();
    }

    // Renders the two-pane screen and blocks on key input until the user
    // either picks an operation (returns its index) or quits (returns -1).
    private int ShowMenu()
    {
        int? chosen = null;
        var count = _vm.Operations.Count;
        var selected = Math.Clamp(_selected, 0, count - 1);
        var layout = BuildLayout();
        layout["info"].Update(InfoPanel(_vm.BuildSections()));

        AnsiConsole.Live(layout).Start(ctx =>
        {
            Render(layout, selected);
            ctx.Refresh();

            while (chosen is null)
            {
                var key = Console.ReadKey(true).Key;

                switch (key)
                {
                    case ConsoleKey.UpArrow:
                    case ConsoleKey.K:
                        selected = (selected + count - 1) % count;
                        break;

                    case ConsoleKey.DownArrow:
                    case ConsoleKey.J:
                        selected = (selected + 1) % count;
                        break;

                    case ConsoleKey.Enter:
                        chosen = selected;
                        break;

                    case ConsoleKey.Escape:
                    case ConsoleKey.Q:
                        chosen = -1;
                        break;
                }

                if (chosen is null)
                {
                    Render(layout, selected);
                    ctx.Refresh();
                }
            }
        });

        _selected = selected;
        return chosen ?? -1;
    }

    private static Layout BuildLayout()
    {
        var layout = new Layout("root").SplitRows(
            new Layout("header").Size(3),
            new Layout("body"),
            new Layout("footer").Size(3));

        layout["body"].SplitColumns(
            new Layout("info").Ratio(2),
            new Layout("ops").Ratio(1));

        return layout;
    }

    private void Render(Layout layout, int selected)
    {
        layout["header"].Update(new Panel(
                new Markup($"[bold]{Commands.AppInfo.Name}[/] [dim]v{Commands.AppInfo.Version}[/] — [green]{Markup.Escape(_vm.FileName)}[/] [dim]({_vm.Kind}, {View.HumanSize(_vm.SizeBytes)})[/]"))
            .Border(BoxBorder.Rounded));

        layout["ops"].Update(new Panel(OpsTable(selected))
            .Border(BoxBorder.Rounded)
            .Header(" Operations ", Justify.Left));

        layout["footer"].Update(new Panel(new Markup(
                $"[dim]↑/↓ move · Enter apply · Esc quit[/]   {(_dirty ? "[yellow]● modified[/]" : "[dim]○ unmodified[/]")}"))
            .Border(BoxBorder.Rounded));
    }

    private static Panel InfoPanel(IReadOnlyList<InfoSection> sections)
    {
        var rows = new List<IRenderable>();

        foreach (var section in sections)
        {
            rows.Add(new Markup($"[bold cyan]{Markup.Escape(section.Title)}[/]"));

            var table = new Table()
                .HideHeaders()
                .NoBorder()
                .AddColumn(new TableColumn(string.Empty))
                .AddColumn(new TableColumn(string.Empty));

            foreach (var (label, value) in section.Rows)
            {
                table.AddRow(Markup.Escape(label), value);
            }

            rows.Add(table);
            rows.Add(Text.Empty);
        }

        return new Panel(new Rows(rows))
            .Border(BoxBorder.Rounded)
            .Header(" Information ", Justify.Left);
    }

    private Table OpsTable(int selected)
    {
        var table = new Table()
            .HideHeaders()
            .NoBorder()
            .AddColumn(new TableColumn(string.Empty));

        for (var i = 0; i < _vm.Operations.Count; i++)
        {
            var label = Markup.Escape(_vm.Operations[i].Label);

            if (i == selected)
            {
                table.AddRow($"[cyan]>[/] [bold cyan]{label}[/]");
            }
            else
            {
                table.AddRow($"  {label}");
            }
        }

        return table;
    }

    private bool SaveAndReport()
    {
        while (true)
        {
            var path = Ask.SavePath(DefaultOutputPath());

            if (path is null)
            {
                return false;
            }

            if (!string.Equals(Path.GetExtension(path), _vm.ExpectedExtension, StringComparison.OrdinalIgnoreCase))
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] output must keep the [bold]{_vm.ExpectedExtension}[/] extension.");
                continue;
            }

            try
            {
                _vm.Save(path);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
                return false;
            }

            _dirty = false;
            AnsiConsole.MarkupLine($"[green]Saved:[/] [dim]{Markup.Escape(Path.GetFullPath(path))}[/]");
            return true;
        }
    }

    private string DefaultOutputPath()
    {
        var directory = Path.GetDirectoryName(_vm.SourcePath);
        if (string.IsNullOrEmpty(directory))
        {
            directory = ".";
        }

        var baseName = Path.GetFileNameWithoutExtension(_vm.SourcePath);
        if (baseName.Length == 0)
        {
            baseName = "modified";
        }

        return Path.Combine(directory, $"{baseName}_modified{_vm.ExpectedExtension}");
    }
}
