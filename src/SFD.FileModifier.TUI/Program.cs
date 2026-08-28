// SFD.FileModifier.TUI - TUI entry point.

using System.Globalization;
using SFD.FileModifier.TUI.Commands;
using Spectre.Console.Cli;

// Keep framework-generated help text in English regardless of the OS language.
CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

var app = new CommandApp<DefaultCommand>();

app.Configure(config =>
{
    config.SetApplicationName(AppInfo.Name);
});

var exitCode = app.Run(args);

return exitCode;
