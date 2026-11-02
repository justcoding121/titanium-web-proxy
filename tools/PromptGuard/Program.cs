using System.Diagnostics;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace Titanium.PromptGuard;

/// <summary>
///     Answers certificate and firewall dialogs so a test run cannot sit on one.
///     Yes and Allow are used only on an ephemeral CI VM. A developer machine gets No or Cancel.
/// </summary>
internal static class Program
{
    private static readonly string[] FirewallTitles = ["Windows Security Alert", "Windows Security", "Windows Defender Firewall"];
    private static readonly string[] CertificateTitles = ["Security Warning", "Root Certificate Store"];

    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "fixture")
        {
            return RunFixture(args);
        }

        if (args.Length > 0 && args[0] == "bind")
        {
            return RunBind(args);
        }

        return RunGuard(args);
    }

    private static int RunGuard(string[] args)
    {
        var seconds = ReadInt(args, "--seconds", 30);
        var ownProcess = ReadValue(args, "--own-process");
        var logPath = ReadValue(args, "--log") ?? Path.Combine("TestResults", "prompt-guard.json");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(logPath)) ?? ".");
        var ephemeral = IsEphemeral();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var sawConsent = false;

        using var automation = new UIA3Automation();
        while (DateTime.UtcNow < deadline)
        {
            if (Process.GetProcessesByName("consent").Length > 0)
            {
                sawConsent = true;
                Append(logPath, new GuardEvent("uac", "", "consent.exe", "detected", ephemeral));
                Thread.Sleep(2000);
                if (Process.GetProcessesByName("consent").Length > 0)
                {
                    Append(logPath, new GuardEvent("uac", "", "consent.exe", "unanswered", ephemeral));
                    return 3;
                }
            }

            var desktop = automation.GetDesktop();
            foreach (var window in desktop.FindAllChildren(cf => cf.ByControlType(ControlType.Window)))
            {
                if (!OwnedBy(window, ownProcess))
                {
                    continue;
                }

                var title = window.Name ?? "";
                if (FirewallTitles.Any(title.Contains))
                {
                    var button = ephemeral ? "Allow access" : "Cancel";
                    TryClick(window, button, "Cancel");
                    Append(logPath, new GuardEvent("firewall", title, ownProcess ?? "", button, ephemeral));
                }
                else if (CertificateTitles.Any(title.Contains))
                {
                    var button = ephemeral ? "Yes" : "No";
                    TryClick(window, button, "Cancel");
                    Append(logPath, new GuardEvent("certificate", title, ownProcess ?? "", button, ephemeral));
                }
            }

            Thread.Sleep(200);
        }

        return sawConsent ? 3 : 0;
    }

    private static int RunFixture(string[] args)
    {
        var title = args.Length > 1 ? args[1] : "Windows Security Alert";
        var resultPath = args.Length > 2 ? args[2] : "fixture-result.txt";
        var buttons = title.Contains("Security", StringComparison.Ordinal) && title.Contains("Warning", StringComparison.Ordinal)
            ? new[] { "&Yes", "&No" }
            : new[] { "Allow access", "Cancel" };
        using var form = new Form
        {
            Text = title,
            Width = 420,
            Height = 180,
            StartPosition = FormStartPosition.CenterScreen,
        };
        var left = 20;
        foreach (var label in buttons)
        {
            var name = label.TrimStart('&');
            var button = new System.Windows.Forms.Button { Text = label, Left = left, Top = 40, Width = 140, Height = 32 };
            button.Click += (_, _) =>
            {
                File.WriteAllText(resultPath, name);
                form.Close();
            };
            form.Controls.Add(button);
            left += 160;
        }

        Application.Run(form);
        return File.Exists(resultPath) ? 0 : 1;
    }

    private static int RunBind(string[] args)
    {
        var seconds = ReadInt(args, "--seconds", 8);
        using var socket = new System.Net.Sockets.Socket(
            System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Dgram,
            System.Net.Sockets.ProtocolType.Udp);
        socket.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0));
        Thread.Sleep(TimeSpan.FromSeconds(seconds));
        return 0;
    }

    private static bool OwnedBy(AutomationElement window, string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return true;
        }

        try
        {
            var pid = window.Properties.ProcessId.ValueOrDefault;
            if (pid <= 0)
            {
                return false;
            }

            return Process.GetProcessById(pid).ProcessName.Contains(processName, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void TryClick(AutomationElement window, string preferred, string fallback)
    {
        try
        {
            Click(window, preferred, fallback);
        }
        catch (Exception)
        {
            // The fixture closes itself inside the button handler, so Invoke can throw after the click.
        }
    }

    private static void Click(AutomationElement window, string preferred, string fallback)
    {
        var buttons = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
        var match = buttons.FirstOrDefault(button => string.Equals(button.Name, preferred, StringComparison.OrdinalIgnoreCase))
                    ?? buttons.FirstOrDefault(button => string.Equals(button.Name, fallback, StringComparison.OrdinalIgnoreCase));
        match?.AsButton().Invoke();
    }

    private static bool IsEphemeral()
    {
        if (string.Equals(Environment.GetEnvironmentVariable("TITANIUM_EPHEMERAL_VM"), "1", StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal)
               && string.Equals(Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"), "github-hosted", StringComparison.Ordinal);
    }

    private static void Append(string path, GuardEvent entry)
    {
        File.AppendAllText(path, JsonSerializer.Serialize(entry) + Environment.NewLine);
    }

    private static string? ReadValue(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.Ordinal))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int ReadInt(string[] args, string name, int fallback)
    {
        var text = ReadValue(args, name);
        return int.TryParse(text, out var value) ? value : fallback;
    }

    private sealed record GuardEvent(string Kind, string Title, string Process, string Action, bool Ephemeral);
}
