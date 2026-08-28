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
        // Restore the terminal (colors, cursor, main buffer) before dying.
        AnsiConsole.Reset();
        AnsiConsole.Write(new ControlCode("\x1b[?1049l"));
    }

    // Renders the two-pane screen in the alternate screen buffer and blocks
    // until the user either picks an operation (returns its index) or quits
    // (returns -1). Redraws whenever the selection changes or the terminal is
    // resized.
    private int ShowMenu()
    {
        int? chosen = null;
        var count = _vm.Operations.Count;
        var selected = Math.Clamp(_selected, 0, count - 1);
        var layout = BuildLayout();
        layout["info"].Update(InfoPanel(_vm.BuildSections()));

        AnsiConsole.Cursor.Hide();
        EnterAlternateScreen();

        try
        {
            var lastSize = (Width: 0, Height: 0);
            var needsRender = true;

            while (chosen is null)
            {
                // Re-measure and redraw when the terminal was resized.
                var size = (Console.WindowWidth, Console.WindowHeight);
                if (size != lastSize)
                {
                    lastSize = size;
                    needsRender = true;
                }

                if (needsRender)
                {
                    needsRender = false;

                    // Force the profile to the live terminal size so Layout
                    // measures against the current window, not a cached one.
                    AnsiConsole.Profile.Width = Console.WindowWidth;
                    AnsiConsole.Profile.Height = Console.WindowHeight;

                    Render(layout, selected);
                    AnsiConsole.Cursor.SetPosition(0, 0);
                    AnsiConsole.Write(layout);
                }

                if (Console.KeyAvailable)
                {
                    switch (Console.ReadKey(true).Key)
                    {
                        case ConsoleKey.UpArrow:
                        case ConsoleKey.K:
                            selected = (selected + count - 1) % count;
                            needsRender = true;
                            break;

                        case ConsoleKey.DownArrow:
                        case ConsoleKey.J:
                            selected = (selected + 1) % count;
                            needsRender = true;
                            break;

                        case ConsoleKey.Enter:
                            chosen = selected;
                            break;

                        case ConsoleKey.Escape:
                        case ConsoleKey.Q:
                            chosen = -1;
                            break;
                    }
                }
                else
                {
                    Thread.Sleep(50);
                }
            }
        }
        finally
        {
            ExitAlternateScreen();
            AnsiConsole.Cursor.Show();
        }

        _selected = selected;
        return chosen ?? -1;
    }

    private static void EnterAlternateScreen() =>
        AnsiConsole.Write(new ControlCode("\x1b[?1049h\x1b[H"));

    private static void ExitAlternateScreen() =>
        AnsiConsole.Write(new ControlCode("\x1b[?1049l"));

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
            .Border(BoxBorder.Rounded)
            .Expand());

        layout["ops"].Update(new Panel(OpsTable(selected))
            .Border(BoxBorder.Rounded)
            .Expand()
            .Header(" Operations ", Justify.Left));

        layout["footer"].Update(new Panel(new Markup(
                $"[dim]↑/↓ move · Enter apply · Esc quit[/]   {(_dirty ? "[yellow]● modified[/]" : "[dim]○ unmodified[/]")}"))
            .Border(BoxBorder.Rounded)
            .Expand());
    }

    private static Panel InfoPanel(IReadOnlyList<InfoSection> sections)
    {
        var rows = new List<IRenderable>();
        var labelWidth = sections
            .SelectMany(s => s.Rows)
            .Select(r => r.Label.Length)
            .DefaultIfEmpty(0)
            .Max();

        foreach (var section in sections)
        {
            rows.Add(new Markup($"[bold cyan]{Markup.Escape(section.Title)}[/]"));

            var table = new Table()
                .HideHeaders()
                .NoBorder()
                .Expand()
                .AddColumn(new TableColumn(string.Empty) { Width = labelWidth })
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
            .Expand()
            .Header(" Information ", Justify.Left);
    }

    private Table OpsTable(int selected)
    {
        var table = new Table()
            .HideHeaders()
            .NoBorder()
            .Expand()
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
