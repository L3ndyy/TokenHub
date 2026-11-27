using System.Windows;

namespace TokenHub;

/// <summary>
/// One binary, two modes. <c>--mcp-memory</c> starts the stdio MCP server that OpenCode
/// spawns to read and write the assistant's long-term memory; anything else starts the
/// normal WPF window. Living in the same exe keeps the installer a single payload.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (Array.Exists(args, a => string.Equals(a, "--mcp-memory", StringComparison.OrdinalIgnoreCase)))
        {
            return Services.MemoryMcp.Run();
        }

        // Headless self-report, used by the CI smoke job and handy in a terminal.
        if (Array.Exists(args, a => string.Equals(a, "--version", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine(Services.Updater.CurrentVersion);
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}