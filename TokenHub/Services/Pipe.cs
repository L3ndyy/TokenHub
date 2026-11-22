using System.IO;
using System.IO.Pipes;

namespace TokenHub.Services;

/// <summary>
/// Tiny one-way status channel between the app and the Windows service (item T11).
/// The process that owns the proxy serves its StatusJson on a named pipe; the other
/// side reads it to show precise counters even when only the service is running.
/// </summary>
internal static class Pipe
{
    public const string Name = "TokenHub.Pool.Status";

    /// <summary>Serves status until cancelled. Returns when the token is signalled.</summary>
    public static void Serve(Func<string> status, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    Name, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                Task wait = server.WaitForConnectionAsync(token);
                wait.Wait(token);

                using var writer = new StreamWriter(server) { AutoFlush = true };
                writer.Write(status());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // A reader may have vanished mid-write; just cycle the pipe.
            }
        }
    }

    /// <summary>Reads the status once, or null when nothing is listening.</summary>
    public static string? Query(int timeoutMs = 800)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", Name, PipeDirection.In);
            client.Connect(timeoutMs);
            using var reader = new StreamReader(client);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }
}
