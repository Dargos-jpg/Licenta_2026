using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WifiSignalOptimizer.Models;
using WifiSignalOptimizer.Services;

namespace WifiSignalOptimizer.Views;

public partial class SysadminTerminalView : UserControl
{
    private readonly List<string> _history = [];
    private int _historyIndex = -1;
    private string _inputSnapshot = "";

    // injected by parent before first use
    public Func<IReadOnlyList<WifiNetwork>>? GetNetworks { get; set; }

    private readonly SysadminCommandService _svc = new();

    // color table indexed by TerminalLineType
    private static readonly Dictionary<TerminalLineType, Brush> _colors = new()
    {
        [TerminalLineType.Command] = new SolidColorBrush(Color.FromRgb(0xE8, 0x79, 0xF9)), // purple
        [TerminalLineType.Output]  = new SolidColorBrush(Color.FromRgb(0xC0, 0xC0, 0xD8)), // light gray
        [TerminalLineType.Success] = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)), // green
        [TerminalLineType.Error]   = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)), // red
        [TerminalLineType.Warning] = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)), // amber
        [TerminalLineType.Info]    = new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)), // blue
        [TerminalLineType.Muted]   = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x52)), // dim
    };

    public SysadminTerminalView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        RefreshInfoBar();
        PrintWelcome();
        InputBox.Focus();
    }

    // ─── info bar ────────────────────────────────────────────────────────────

    private void RefreshInfoBar()
    {
        TbHostname.Text = $"@{System.Net.Dns.GetHostName()}";

        var wifi = NetworkInterface.GetAllNetworkInterfaces()
            .FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                              && n.OperationalStatus == OperationalStatus.Up);

        if (wifi != null)
        {
            TbConnected.Text       = "Connected";
            TbConnected.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            StatusDot.Fill         = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            TbInterface.Text       = wifi.Name;
        }
        else
        {
            TbConnected.Text       = "No WiFi";
            TbConnected.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            StatusDot.Fill         = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            TbInterface.Text       = "";
        }
    }

    // ─── output helpers ──────────────────────────────────────────────────────

    private void PrintWelcome()
    {
        AppendLine("WiFi Signal Optimizer — Network Terminal v2.0", TerminalLineType.Info);
        AppendLine("Type 'help' for available commands. Tab = autocomplete, ↑↓ = history.", TerminalLineType.Muted);
        AppendLine("", TerminalLineType.Output);
    }

    private void AppendLine(string text, TerminalLineType type)
    {
        var doc = OutputBox.Document;
        var para = new Paragraph(new Run(text))
        {
            Margin   = new Thickness(0),
            Padding  = new Thickness(0),
            Foreground = _colors.TryGetValue(type, out var b) ? b : _colors[TerminalLineType.Output],
        };
        doc.Blocks.Add(para);
        OutputScroller.ScrollToEnd();
    }

    private void AppendPromptEcho(string text)
    {
        var doc  = OutputBox.Document;
        var para = new Paragraph { Margin = new Thickness(0, 4, 0, 0), Padding = new Thickness(0) };
        para.Inlines.Add(new Run("net ❯ ")
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
        });
        para.Inlines.Add(new Run(text)
        {
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x79, 0xF9)),
        });
        doc.Blocks.Add(para);
        OutputScroller.ScrollToEnd();
    }

    private void ClearOutput()
    {
        OutputBox.Document.Blocks.Clear();
    }

    // ─── command execution ────────────────────────────────────────────────────

    private async void ExecuteInput()
    {
        var input = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(input)) return;

        // update history
        if (_history.Count == 0 || _history[^1] != input)
            _history.Add(input);
        _historyIndex = _history.Count;

        InputBox.Clear();
        HideSuggestions();
        AppendPromptEcho(input);

        BtnRun.IsEnabled  = false;
        BtnStop.IsEnabled = true;

        var networks = GetNetworks?.Invoke() ?? [];

        await foreach (var line in _svc.ExecuteAsync(input, networks))
        {
            if (line.Text == "__CLEAR__")
            {
                ClearOutput();
                continue;
            }
            AppendLine(line.Text, line.Type);
        }

        BtnRun.IsEnabled  = true;
        BtnStop.IsEnabled = false;
        AppendLine("", TerminalLineType.Output);
        InputBox.Focus();
        RefreshInfoBar();
    }

    // ─── input events ─────────────────────────────────────────────────────────

    private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                ExecuteInput();
                break;

            case Key.Tab:
                e.Handled = true;
                HandleTabAutocomplete();
                break;

            case Key.Up:
                e.Handled = true;
                NavigateHistory(-1);
                break;

            case Key.Down:
                e.Handled = true;
                NavigateHistory(+1);
                break;

            case Key.C when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                _svc.Cancel();
                AppendLine("^C", TerminalLineType.Warning);
                BtnRun.IsEnabled  = true;
                BtnStop.IsEnabled = false;
                break;

            case Key.Escape:
                HideSuggestions();
                break;
        }
    }

    private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // live suggestions while typing
        var text = InputBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            HideSuggestions();
            return;
        }
        var suggestions = SysadminCommandService.GetSuggestions(text);
        if (suggestions.Count == 0 || (suggestions.Count == 1 && suggestions[0] == text.Trim()))
        {
            HideSuggestions();
        }
        else
        {
            SuggestionsList.ItemsSource = suggestions;
            SuggestionsPanel.Visibility = Visibility.Visible;
        }
    }

    // ─── autocomplete ─────────────────────────────────────────────────────────

    private void HandleTabAutocomplete()
    {
        var suggestions = SysadminCommandService.GetSuggestions(InputBox.Text);
        if (suggestions.Count == 1)
        {
            InputBox.Text = suggestions[0] + " ";
            InputBox.CaretIndex = InputBox.Text.Length;
            HideSuggestions();
        }
        else if (suggestions.Count > 1)
        {
            SuggestionsList.ItemsSource = suggestions;
            SuggestionsPanel.Visibility = Visibility.Visible;
            SuggestionsList.SelectedIndex = 0;
        }
    }

    private void SuggestionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SuggestionsList.SelectedItem is string selected)
        {
            InputBox.Text = selected + " ";
            InputBox.CaretIndex = InputBox.Text.Length;
            HideSuggestions();
            InputBox.Focus();
        }
    }

    private void HideSuggestions()
    {
        SuggestionsPanel.Visibility = Visibility.Collapsed;
        SuggestionsList.ItemsSource = null;
    }

    // ─── history navigation ────────────────────────────────────────────────────

    private void NavigateHistory(int direction)
    {
        if (_history.Count == 0) return;

        if (_historyIndex == _history.Count)
            _inputSnapshot = InputBox.Text;

        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);

        InputBox.Text = _historyIndex == _history.Count
            ? _inputSnapshot
            : _history[_historyIndex];

        InputBox.CaretIndex = InputBox.Text.Length;
    }

    // ─── button handlers ──────────────────────────────────────────────────────

    private void BtnRun_Click(object sender, RoutedEventArgs e) => ExecuteInput();

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _svc.Cancel();
        AppendLine("Command cancelled.", TerminalLineType.Warning);
        BtnRun.IsEnabled  = true;
        BtnStop.IsEnabled = false;
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        ClearOutput();
        InputBox.Focus();
    }
}
