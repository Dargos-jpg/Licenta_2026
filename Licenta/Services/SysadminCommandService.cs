using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Security.Principal;
using System.Text;
using WifiSignalOptimizer.Models;

namespace WifiSignalOptimizer.Services;

public enum TerminalLineType { Command, Output, Success, Error, Warning, Info, Muted }

public record TerminalLine(string Text, TerminalLineType Type);

public class SysadminCommandService
{
    // available commands with descriptions for Tab autocomplete
    public static readonly Dictionary<string, string> Commands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["help"]      = "List all available commands",
        ["clear"]     = "Clear the terminal",
        ["ping"]      = "ping <host> [-n count]  — ICMP ping",
        ["tracert"]   = "tracert <host>  — trace route to host",
        ["ipconfig"]  = "ipconfig [/all | /flushdns | /release | /renew]",
        ["nslookup"]  = "nslookup <host> [server]  — DNS lookup",
        ["netstat"]   = "netstat [-a | -n | -o]  — active connections",
        ["arp"]       = "arp -a  — display ARP table",
        ["route"]     = "route print  — display routing table",
        ["wifi"]      = "wifi [scan | list | profiles | password <ssid>]",
        ["adapter"]   = "adapter [list | enable <name> | disable <name>]",
        ["gateway"]   = "Show default gateway and router IP",
        ["router"]    = "router open  — open router admin page in browser",
        ["whoami"]    = "Display current Windows user",
        ["hostname"]  = "Display computer hostname",
        ["sysinfo"]   = "Display network system info",
        ["dns"]       = "dns [show | flush | set <primary> [secondary]]",
    };

    // sub-commands for second-level autocomplete
    public static readonly Dictionary<string, string[]> SubCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["wifi"]    = ["scan", "list", "profiles", "password"],
        ["adapter"] = ["list", "enable", "disable"],
        ["dns"]     = ["show", "flush", "set"],
        ["router"]  = ["open"],
        ["ipconfig"]= ["/all", "/flushdns", "/release", "/renew"],
    };

    private CancellationTokenSource? _cts;

    public bool IsRunning => _cts != null && !_cts.IsCancellationRequested;

    public void Cancel()
    {
        _cts?.Cancel();
        _cts = null;
    }

    // main entry point — parse and dispatch the command
    public async IAsyncEnumerable<TerminalLine> ExecuteAsync(
        string input,
        IReadOnlyList<WifiNetwork> visibleNetworks)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) yield break;

        var cmd = parts[0].ToLowerInvariant();
        var args = parts.Skip(1).ToArray();

        switch (cmd)
        {
            case "help":
                foreach (var line in HelpLines()) yield return line;
                break;

            case "clear":
            case "cls":
                yield return new TerminalLine("__CLEAR__", TerminalLineType.Info);
                break;

            case "ping":
                await foreach (var l in ShellStreamAsync($"ping {string.Join(" ", args)}", ct))
                    yield return l;
                break;

            case "tracert":
                await foreach (var l in ShellStreamAsync($"tracert {string.Join(" ", args)}", ct))
                    yield return l;
                break;

            case "ipconfig":
                await foreach (var l in ShellStreamAsync($"ipconfig {string.Join(" ", args)}", ct))
                    yield return l;
                break;

            case "nslookup":
                await foreach (var l in ShellStreamAsync($"nslookup {string.Join(" ", args)}", ct))
                    yield return l;
                break;

            case "netstat":
                await foreach (var l in ShellStreamAsync($"netstat {string.Join(" ", args)}", ct))
                    yield return l;
                break;

            case "arp":
                await foreach (var l in ShellStreamAsync($"arp {string.Join(" ", args)}", ct))
                    yield return l;
                break;

            case "route":
                await foreach (var l in ShellStreamAsync($"route {string.Join(" ", args)}", ct))
                    yield return l;
                break;

            case "wifi":
                await foreach (var l in WifiCommandAsync(args, visibleNetworks, ct))
                    yield return l;
                break;

            case "adapter":
                await foreach (var l in AdapterCommandAsync(args, ct))
                    yield return l;
                break;

            case "dns":
                await foreach (var l in DnsCommandAsync(args, ct))
                    yield return l;
                break;

            case "gateway":
                foreach (var l in GatewayLines()) yield return l;
                break;

            case "router":
                foreach (var l in RouterCommand(args)) yield return l;
                break;

            case "whoami":
                await foreach (var l in ShellStreamAsync("whoami", ct)) yield return l;
                break;

            case "hostname":
                yield return new TerminalLine(Dns.GetHostName(), TerminalLineType.Success);
                break;

            case "sysinfo":
                foreach (var l in SysInfoLines()) yield return l;
                break;

            default:
                yield return new TerminalLine($"'{cmd}' is not a recognized command. Type 'help' for the list.", TerminalLineType.Error);
                break;
        }

        _cts = null;
    }

    // streams shell output line by line
    private static async IAsyncEnumerable<TerminalLine> ShellStreamAsync(string command, [EnumeratorCancellation] CancellationToken ct)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c chcp 65001 >nul & {command}")
        {
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding  = Encoding.UTF8,
        };

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc == null) yield break;

            string? outLine;
            while ((outLine = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false)) is not null
                   && !ct.IsCancellationRequested)
            {
                yield return new TerminalLine(outLine, ClassifyLine(outLine));
            }

            string? errLine;
            while ((errLine = await proc.StandardError.ReadLineAsync(ct).ConfigureAwait(false)) is not null
                   && !ct.IsCancellationRequested)
            {
                yield return new TerminalLine(errLine, TerminalLineType.Error);
            }

            if (!ct.IsCancellationRequested)
                await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (ct.IsCancellationRequested)
            {
                try { proc?.Kill(entireProcessTree: true); } catch { }
            }
            proc?.Dispose();
        }
    }

    // heuristic coloring for raw shell output
    private static TerminalLineType ClassifyLine(string line)
    {
        var l = line.ToLowerInvariant();
        if (l.Contains("error") || l.Contains("failed") || l.Contains("esuec") || l.Contains("unreachable"))
            return TerminalLineType.Error;
        if (l.Contains("warning") || l.Contains("timeout") || l.Contains("request timed out"))
            return TerminalLineType.Warning;
        if (l.Contains("reply from") || l.Contains("bytes=") || l.Contains("successful"))
            return TerminalLineType.Success;
        return TerminalLineType.Output;
    }

    // ─── wifi sub-commands ───────────────────────────────────────────────────

    private static async IAsyncEnumerable<TerminalLine> WifiCommandAsync(
        string[] args, IReadOnlyList<WifiNetwork> networks, [EnumeratorCancellation] CancellationToken ct)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";

        switch (sub)
        {
            case "list":
                if (networks.Count == 0)
                {
                    yield return new TerminalLine("No networks detected. Run 'wifi scan' first.", TerminalLineType.Warning);
                    yield break;
                }
                yield return new TerminalLine($"{"SSID",-30} {"Band",-8} {"dBm",-6} {"Ch",-5} {"SNR",-5} Quality", TerminalLineType.Info);
                yield return new TerminalLine(new string('─', 70), TerminalLineType.Muted);
                foreach (var n in networks.OrderByDescending(n => n.SignalStrength))
                    yield return new TerminalLine(
                        $"{n.SSID,-30} {n.BandLabel,-8} {n.SignalStrength,-6} {n.Channel,-5} {n.EstimatedSnr,-5} {n.SnrQuality}",
                        n.SignalStrength > -50 ? TerminalLineType.Success :
                        n.SignalStrength > -70 ? TerminalLineType.Warning : TerminalLineType.Error);
                yield return new TerminalLine($"{networks.Count} network(s) found.", TerminalLineType.Muted);
                break;

            case "profiles":
                await foreach (var l in ShellStreamAsync("netsh wlan show profiles", ct)) yield return l;
                break;

            case "password":
                var ssid = string.Join(" ", args.Skip(1));
                if (string.IsNullOrWhiteSpace(ssid))
                {
                    yield return new TerminalLine("Usage: wifi password <ssid>", TerminalLineType.Warning);
                    yield break;
                }
                await foreach (var l in ShellStreamAsync($"netsh wlan show profile name=\"{ssid}\" key=clear", ct))
                    yield return l;
                break;

            case "scan":
                yield return new TerminalLine("Triggering WiFi scan via system adapter...", TerminalLineType.Info);
                await foreach (var l in ShellStreamAsync("netsh wlan show networks mode=bssid", ct)) yield return l;
                break;

            default:
                yield return new TerminalLine($"Unknown wifi sub-command '{sub}'. Use: scan | list | profiles | password <ssid>", TerminalLineType.Error);
                break;
        }
    }

    // ─── adapter sub-commands ────────────────────────────────────────────────

    private static async IAsyncEnumerable<TerminalLine> AdapterCommandAsync(string[] args, [EnumeratorCancellation] CancellationToken ct)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant() ?? "list";

        switch (sub)
        {
            case "list":
                yield return new TerminalLine($"{"Name",-35} {"Status",-12} {"Type",-15} IP", TerminalLineType.Info);
                yield return new TerminalLine(new string('─', 80), TerminalLineType.Muted);
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    var ip = adapter.GetIPProperties().UnicastAddresses
                        .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        ?.Address.ToString() ?? "—";
                    var status = adapter.OperationalStatus == OperationalStatus.Up ? "UP" : "DOWN";
                    var type = adapter.NetworkInterfaceType.ToString();
                    var lineType = adapter.OperationalStatus == OperationalStatus.Up
                        ? TerminalLineType.Success : TerminalLineType.Muted;
                    yield return new TerminalLine($"{adapter.Name,-35} {status,-12} {type,-15} {ip}", lineType);
                }
                break;

            case "enable":
            case "disable":
                var name = string.Join(" ", args.Skip(1));
                if (string.IsNullOrWhiteSpace(name))
                {
                    yield return new TerminalLine($"Usage: adapter {sub} <adapter-name>", TerminalLineType.Warning);
                    yield break;
                }
                var netshCmd = $"netsh interface set interface \"{name}\" {(sub == "enable" ? "enable" : "disable")}";
                if (IsAdministrator())
                {
                    await foreach (var l in ShellStreamAsync(netshCmd, ct))
                        yield return l;
                }
                else
                {
                    yield return new TerminalLine("Requires administrator privileges — launching elevated process...", TerminalLineType.Warning);
                    string? elevErrMsg = null;
                    bool elevOk = false;
                    try
                    {
                        Process.Start(new ProcessStartInfo("cmd.exe", $"/c {netshCmd}")
                        {
                            Verb            = "runas",
                            UseShellExecute = true,
                            CreateNoWindow  = true
                        });
                        elevOk = true;
                    }
                    catch (Exception ex) { elevErrMsg = ex.Message; }

                    if (elevOk)
                    {
                        yield return new TerminalLine("Elevated command sent. Run 'adapter list' to verify.", TerminalLineType.Success);
                    }
                    else
                    {
                        yield return new TerminalLine($"Elevation cancelled or failed: {elevErrMsg}", TerminalLineType.Error);
                        yield return new TerminalLine("Tip: Right-click the app icon → Run as administrator for seamless admin commands.", TerminalLineType.Info);
                    }
                }
                break;

            default:
                yield return new TerminalLine("Usage: adapter [list | enable <name> | disable <name>]", TerminalLineType.Error);
                break;
        }
    }

    // ─── dns sub-commands ────────────────────────────────────────────────────

    private static async IAsyncEnumerable<TerminalLine> DnsCommandAsync(string[] args, [EnumeratorCancellation] CancellationToken ct)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant() ?? "show";

        switch (sub)
        {
            case "show":
                await foreach (var l in ShellStreamAsync("ipconfig /all | findstr /i \"DNS\"", ct)) yield return l;
                break;

            case "flush":
                yield return new TerminalLine("Flushing DNS resolver cache...", TerminalLineType.Info);
                await foreach (var l in ShellStreamAsync("ipconfig /flushdns", ct)) yield return l;
                break;

            case "set":
                var primary   = args.ElementAtOrDefault(1) ?? "";
                var secondary = args.ElementAtOrDefault(2) ?? "";
                if (string.IsNullOrWhiteSpace(primary))
                {
                    yield return new TerminalLine("Usage: dns set <primary-ip> [secondary-ip]", TerminalLineType.Warning);
                    yield break;
                }
                yield return new TerminalLine("Detecting active WiFi adapter...", TerminalLineType.Info);
                var wifi = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                                      && n.OperationalStatus == OperationalStatus.Up);
                if (wifi == null)
                {
                    yield return new TerminalLine("No active WiFi adapter found.", TerminalLineType.Error);
                    yield break;
                }
                await foreach (var l in ShellStreamAsync(
                    $"netsh interface ip set dns \"{wifi.Name}\" static {primary}", ct)) yield return l;
                if (!string.IsNullOrWhiteSpace(secondary))
                    await foreach (var l in ShellStreamAsync(
                        $"netsh interface ip add dns \"{wifi.Name}\" {secondary} index=2", ct)) yield return l;
                yield return new TerminalLine($"DNS set to {primary}" + (secondary != "" ? $" / {secondary}" : ""), TerminalLineType.Success);
                break;

            default:
                yield return new TerminalLine("Usage: dns [show | flush | set <primary> [secondary]]", TerminalLineType.Error);
                break;
        }
    }

    // ─── gateway / router ────────────────────────────────────────────────────

    private static IEnumerable<TerminalLine> GatewayLines()
    {
        yield return new TerminalLine($"{"Interface",-35} {"Gateway",-18} Status", TerminalLineType.Info);
        yield return new TerminalLine(new string('─', 65), TerminalLineType.Muted);
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up))
        {
            foreach (var gw in adapter.GetIPProperties().GatewayAddresses)
            {
                yield return new TerminalLine(
                    $"{adapter.Name,-35} {gw.Address,-18} UP",
                    TerminalLineType.Success);
            }
        }
    }

    private static IEnumerable<TerminalLine> RouterCommand(string[] args)
    {
        var sub = args.FirstOrDefault()?.ToLowerInvariant() ?? "open";
        if (sub != "open")
        {
            yield return new TerminalLine("Usage: router open", TerminalLineType.Warning);
            yield break;
        }

        var gw = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().GatewayAddresses)
            .FirstOrDefault()?.Address.ToString();

        if (gw == null)
        {
            yield return new TerminalLine("No gateway found. Are you connected to a network?", TerminalLineType.Error);
            yield break;
        }

        yield return new TerminalLine($"Opening router admin panel at http://{gw} ...", TerminalLineType.Info);
        Process.Start(new ProcessStartInfo($"http://{gw}") { UseShellExecute = true });
        yield return new TerminalLine($"Launched browser → http://{gw}", TerminalLineType.Success);
    }

    // ─── sysinfo ─────────────────────────────────────────────────────────────

    private static IEnumerable<TerminalLine> SysInfoLines()
    {
        yield return new TerminalLine("── Network System Info ──────────────────────────────", TerminalLineType.Info);
        yield return new TerminalLine($"Hostname      : {Dns.GetHostName()}", TerminalLineType.Output);
        yield return new TerminalLine($"User          : {Environment.UserName}", TerminalLineType.Output);
        yield return new TerminalLine($"OS            : {Environment.OSVersion}", TerminalLineType.Output);
        yield return new TerminalLine($"Processors    : {Environment.ProcessorCount}", TerminalLineType.Output);
        yield return new TerminalLine("", TerminalLineType.Output);
        yield return new TerminalLine("── Active Interfaces ────────────────────────────────", TerminalLineType.Info);
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var ip = n.GetIPProperties().UnicastAddresses
                .FirstOrDefault(a => a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                ?.Address.ToString() ?? "—";
            yield return new TerminalLine($"  {n.Name,-32} {n.NetworkInterfaceType,-20} {ip}", TerminalLineType.Success);
        }
    }

    // ─── help ─────────────────────────────────────────────────────────────────

    private static IEnumerable<TerminalLine> HelpLines()
    {
        yield return new TerminalLine("── WiFi Signal Optimizer — Network Terminal ─────────", TerminalLineType.Info);
        yield return new TerminalLine("  Tab          autocomplete command", TerminalLineType.Muted);
        yield return new TerminalLine("  ↑ / ↓        navigate command history", TerminalLineType.Muted);
        yield return new TerminalLine("  Ctrl+C       cancel running command", TerminalLineType.Muted);
        yield return new TerminalLine("", TerminalLineType.Output);
        yield return new TerminalLine("── Commands ─────────────────────────────────────────", TerminalLineType.Info);
        foreach (var (cmd, desc) in Commands.OrderBy(k => k.Key))
            yield return new TerminalLine($"  {cmd,-14} {desc}", TerminalLineType.Output);
    }

    // ─── autocomplete ─────────────────────────────────────────────────────────

    public static List<string> GetSuggestions(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];

        var parts = input.TrimStart().Split(' ', StringSplitOptions.None);
        var cmd   = parts[0];

        // first word: match command names
        if (parts.Length == 1)
            return Commands.Keys.Where(k => k.StartsWith(cmd, StringComparison.OrdinalIgnoreCase))
                           .OrderBy(k => k).ToList();

        // second word: match sub-commands
        if (parts.Length == 2 && SubCommands.TryGetValue(cmd, out var subs))
        {
            var partial = parts[1];
            return subs.Where(s => s.StartsWith(partial, StringComparison.OrdinalIgnoreCase))
                       .OrderBy(s => s)
                       .Select(s => $"{cmd} {s}")
                       .ToList();
        }

        return [];
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
