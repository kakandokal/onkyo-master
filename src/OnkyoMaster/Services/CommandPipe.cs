using System.IO;
using System.IO.Pipes;

namespace OnkyoMaster.Services;

/// <summary>
/// Lets a second launch of the exe hand its command-line arguments to the instance already running,
/// which is how scripts and shortcuts drive the app ("OnkyoMaster.exe vol-up").
/// </summary>
public static class CommandPipe
{
    private const string PipeName = "OnkyoMaster.Commands";

    /// <summary>Sends commands to the running instance. Returns false if it does not answer.</summary>
    public static bool Send(IEnumerable<string> commands)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(2000);
            using var writer = new StreamWriter(pipe);
            foreach (var command in commands) writer.WriteLine(command);
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Accepts commands until cancelled. <paramref name="onCommand"/> runs on a thread-pool thread.</summary>
    public static async Task ListenAsync(Action<string> onCommand, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(pipe);
                while (await reader.ReadLineAsync(ct) is { } line)
                {
                    if (line.Trim().Length > 0) onCommand(line.Trim());
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                // The client went away mid-message; wait for the next one.
            }
        }
    }
}
