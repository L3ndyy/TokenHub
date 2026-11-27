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

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}