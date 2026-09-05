using System.Text;
using System.Xml.Linq;

namespace Titanium.Cli.Service;

/// <summary>Pure builders for Windows binPath, systemd unit, and launchd plist text.</summary>
internal static class ServiceUnitFactory
{
    public static string BuildWindowsBinPath(string exePath, string configPath, string serviceName)
    {
        // sc.exe binPath= expects a single string; quote exe and config when they contain spaces.
        var exe = QuoteWindowsArg(exePath);
        var cfg = QuoteWindowsArg(configPath);
        var name = QuoteWindowsArg(serviceName);
        return $"{exe} run -c {cfg} --service --name {name}";
    }

    public static string BuildSystemdUnit(
        string exePath,
        string configPath,
        string workingDirectory,
        bool user)
    {
        var wantedBy = user ? "default.target" : "multi-user.target";
        var sb = new StringBuilder();
        sb.AppendLine("[Unit]");
        sb.AppendLine($"Description={ServiceDefaults.Description}");
        sb.AppendLine("After=network-online.target");
        sb.AppendLine("Wants=network-online.target");
        sb.AppendLine();
        sb.AppendLine("[Service]");
        sb.AppendLine("Type=simple");
        sb.AppendLine($"ExecStart={EscapeSystemdArg(exePath)} run -c {EscapeSystemdArg(configPath)} --service");
        sb.AppendLine($"WorkingDirectory={EscapeSystemdArg(workingDirectory)}");
        sb.AppendLine("Restart=on-failure");
        sb.AppendLine("RestartSec=5");
        sb.AppendLine();
        sb.AppendLine("[Install]");
        sb.AppendLine($"WantedBy={wantedBy}");
        return sb.ToString();
    }

    public static string BuildLaunchdPlist(
        string label,
        string exePath,
        string configPath,
        string workingDirectory,
        string standardOutPath,
        string standardErrorPath)
    {
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN",
                "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist",
                new XAttribute("version", "1.0"),
                new XElement("dict",
                    new XElement("key", "Label"),
                    new XElement("string", label),
                    new XElement("key", "ProgramArguments"),
                    new XElement("array",
                        new XElement("string", exePath),
                        new XElement("string", "run"),
                        new XElement("string", "-c"),
                        new XElement("string", configPath),
                        new XElement("string", "--service")),
                    new XElement("key", "WorkingDirectory"),
                    new XElement("string", workingDirectory),
                    new XElement("key", "RunAtLoad"),
                    new XElement("true"),
                    new XElement("key", "KeepAlive"),
                    new XElement("true"),
                    new XElement("key", "StandardOutPath"),
                    new XElement("string", standardOutPath),
                    new XElement("key", "StandardErrorPath"),
                    new XElement("string", standardErrorPath))));

        return doc.Declaration + Environment.NewLine + doc.ToString(SaveOptions.None) + Environment.NewLine;
    }

    public static string ResolveSystemdUnitPath(string name, bool user)
    {
        if (user)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home.Replace('\\', '/') + "/.config/systemd/user/" + name + ".service";
        }

        return "/etc/systemd/system/" + name + ".service";
    }

    public static string ResolveLaunchdPlistPath(string label, bool user)
    {
        if (user)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home.Replace('\\', '/') + "/Library/LaunchAgents/" + label + ".plist";
        }

        return "/Library/LaunchDaemons/" + label + ".plist";
    }

    public static string ResolveLaunchdLogDirectory(bool user)
    {
        if (user)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return home.Replace('\\', '/') + "/Library/Logs/Titanium";
        }

        return "/Library/Logs/Titanium";
    }

    internal static string QuoteWindowsArg(string value)
    {
        if (value.Length == 0)
        {
            return "\"\"";
        }

        if (value.Contains(' ', StringComparison.Ordinal) ||
            value.Contains('\t', StringComparison.Ordinal) ||
            value.Contains('"', StringComparison.Ordinal))
        {
            return "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }

    /// <summary>systemd ExecStart: quote if needed; escape `$`, `%`, and `\`.</summary>
    internal static string EscapeSystemdArg(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "%%", StringComparison.Ordinal)
            .Replace("$", "$$", StringComparison.Ordinal);

        if (escaped.Contains(' ', StringComparison.Ordinal) ||
            escaped.Contains('\t', StringComparison.Ordinal) ||
            escaped.Contains('"', StringComparison.Ordinal))
        {
            return "\"" + escaped.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        }

        return escaped;
    }
}
