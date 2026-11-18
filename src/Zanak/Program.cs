using Zanak.Uci;

namespace Zanak;

/// <summary>
/// Standalone UCI entry point for the Zanak chess engine.
/// </summary>
public static class Program
{
    /// <summary>
    /// Runs the asynchronous UCI command loop over standard input and output.
    /// </summary>
    public static async Task Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var uci = new UciHandler(Console.In, Console.Out);
        await uci.RunAsync();
    }
}
