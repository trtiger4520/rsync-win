namespace RsyncWin.Perf;

internal sealed class CommandLine
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _switches = new(StringComparer.OrdinalIgnoreCase);

    private CommandLine(string command) => Command = command;

    public string Command { get; }

    public static CommandLine Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
            return new CommandLine("help");

        var result = new CommandLine(args[0].ToLowerInvariant());
        for (int i = 1; i < args.Length; i++)
        {
            string token = args[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument: {token}");

            if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                result._values[token] = args[++i];
            else
                result._switches.Add(token);
        }
        return result;
    }

    public string Get(string name, string defaultValue) => _values.GetValueOrDefault(name, defaultValue);

    public string? GetOptional(string name) => _values.GetValueOrDefault(name);

    public int GetInt(string name, int defaultValue)
    {
        string? value = GetOptional(name);
        return value is null ? defaultValue : int.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public bool Has(string name) => _switches.Contains(name);
}
