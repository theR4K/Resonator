using System;

namespace Resonator.Pipeline.Infrastructure;

/// <summary>Console output of the pipeline tools: stage banners, plain lines, highlighted warnings and errors.</summary>
public static class Log
{
    public static void Banner(string title)
    {
        Console.WriteLine();
        Colored(ConsoleColor.Cyan, "=== " + title);
    }

    public static void Info(string line = "") => Console.WriteLine(line);

    public static void Warn(string line) => Colored(ConsoleColor.Yellow, line);

    public static void Error(string line) => Colored(ConsoleColor.Red, line);

    static void Colored(ConsoleColor color, string line)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(line);
        Console.ResetColor();
    }
}
