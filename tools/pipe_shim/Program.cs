using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace PipeShim;

static class Program
{
    static int Main(string[] args)
    {
        string currentExe = Environment.ProcessPath ?? "";
        string dir = Path.GetDirectoryName(currentExe) ?? "";
        string baseName = Path.GetFileNameWithoutExtension(currentExe);
        string targetExe = Path.Combine(dir, $"{baseName}_real.exe");

        if (args.Length > 0 && args[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            targetExe = Path.GetFullPath(args[0]);
        }

        if (!File.Exists(targetExe))
        {
            Console.Error.WriteLine($"[PipeShim] Target executable not found: {targetExe}");
            return 1;
        }

        var psi = new ProcessStartInfo
        {
            FileName = targetExe,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process == null) return 1;

        // Forward stdout from child to parent
        var stdoutThread = new Thread(() =>
        {
            try
            {
                var buffer = new char[4096];
                int read;
                while ((read = process.StandardOutput.Read(buffer, 0, buffer.Length)) > 0)
                {
                    Console.Out.Write(buffer, 0, read);
                    Console.Out.Flush();
                }
            }
            catch { }
        }) { IsBackground = true };
        stdoutThread.Start();

        // Forward stdin from parent to child
        try
        {
            var buffer = new char[4096];
            int read;
            while ((read = Console.In.Read(buffer, 0, buffer.Length)) > 0)
            {
                process.StandardInput.Write(buffer, 0, read);
                process.StandardInput.Flush();
            }
        }
        catch { }

        process.WaitForExit();
        return process.ExitCode;
    }
}
