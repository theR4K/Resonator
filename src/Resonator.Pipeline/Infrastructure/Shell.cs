using System.Diagnostics;
using System.IO;
using System.Text;

namespace Resonator.Pipeline.Infrastructure;

/// <summary>Runs external tools (git, dotnet).</summary>
public static class Shell
{
    /// <summary>Run a process to completion, capturing stdout and stderr together.</summary>
    public static (int ExitCode, string Output) Capture(string exe, params string[] args)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            },
        };
        foreach (string a in args) process.StartInfo.ArgumentList.Add(a);

        var buffer = new StringWriter();
        var output = TextWriter.Synchronized(buffer);   // stdout and stderr events arrive on different threads
        process.OutputDataReceived += (_, e) => { if (e.Data != null) output.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.WriteLine(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.WaitForExit();
        process.WaitForExit();   // the no-timeout overload also drains the async readers
        return (process.ExitCode, buffer.ToString());
    }
}
