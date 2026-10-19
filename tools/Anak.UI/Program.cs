namespace Anak.UI;

/// <summary>
/// Entry point for Anak Visualizer local debugging UI.
/// Credit & Attribution: Inspired by open-source C# chess visualizer designs (Sebastian Lague Chess-Coding-Adventure & WinForms Chess UI, MIT License).
/// </summary>
public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
#if WINDOWS
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
#else
        Console.WriteLine("Anak.UI is a local debugging visualizer configured for Windows Desktop environments.");
#endif
    }
}
