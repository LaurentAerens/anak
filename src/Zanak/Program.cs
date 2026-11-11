using Zanak.Uci;

namespace Zanak;

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        using var uci = new UciHandler(Console.In, Console.Out);
        await uci.RunAsync();
    }
}
