namespace DocumentForge.Cli.Commands;

internal static class UiHelpers
{
    public static int Fail(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine("ERROR: " + msg);
        Console.ResetColor();
        return 1;
    }

    public static void Header(string title)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("═ " + title);
        Console.ResetColor();
    }

    public static void KV(string k, string v)
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  {k,-22} ");
        Console.ResetColor();
        Console.WriteLine(v);
    }

    public static string Blue(string s)  => $"\u001b[36m{s}\u001b[0m";
    public static string Red(string s)   => $"\u001b[31m{s}\u001b[0m";
    public static string Green(string s) => $"\u001b[32m{s}\u001b[0m";
    public static string Gray(string s)  => $"\u001b[90m{s}\u001b[0m";

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024.0:F1} MB",
        _ => $"{bytes / 1024.0 / 1024.0 / 1024.0:F2} GB"
    };
}
