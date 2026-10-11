using FreeXplorer.Cli;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

return Cli.Run(args, new CliContext(Console.Out, Console.Error, Console.In, cancel.Token));
