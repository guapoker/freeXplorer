using System.Runtime.InteropServices;
using FreeXplorer.Cli;

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};

// kill and a closed terminal stop things the same way Ctrl+C does, so a mount gets unmounted
var signals = new List<PosixSignalRegistration>();
if (!OperatingSystem.IsWindows())
{
    foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGHUP })
    {
        signals.Add(PosixSignalRegistration.Create(signal, context =>
        {
            context.Cancel = true;
            cancel.Cancel();
        }));
    }
}

try
{
    return Cli.Run(args, new CliContext(Console.Out, Console.Error, Console.In, cancel.Token));
}
finally
{
    foreach (var registration in signals)
        registration.Dispose();
}
