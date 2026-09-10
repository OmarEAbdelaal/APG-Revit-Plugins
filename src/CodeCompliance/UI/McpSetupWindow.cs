using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using Autodesk.Revit.UI;
using CodeCompliance.Core.Mcp;
using TextBox = System.Windows.Controls.TextBox;

namespace CodeCompliance.UI
{
    /// <summary>One row of the commands grid.</summary>
    public class McpCommandRow
    {
        public bool Enabled { get; set; }
        public string Name { get; set; } = "";
        public string SetName { get; set; } = "";
        public string Available { get; set; } = "";
        public string Description { get; set; } = "";
    }

    /// <summary>
    /// Revit MCP setup: status of the server, installation/update from GitHub, Claude
    /// configuration and the list of commands exposed to the AI. Code-only WPF, shown modal
    /// from <see cref="Commands.McpSetupCommand"/> so Revit API calls stay valid.
    /// </summary>
    public class McpSetupWindow : Window
    {
        private readonly UIApplication _uiApp;
        private readonly string _revitVersion;
        private McpSettings _settings;
        private McpReleaseInfo? _latest;

        private readonly TextBlock _serverStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _installedStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _latestStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _nodeStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _claudeStatus = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _claudeFile = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ApgTheme.Muted };
        private readonly TextBlock _message = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ApgTheme.Muted };
        private readonly Button _toggleButton;
        private readonly Button _installButton;
        private readonly Button _claudeButton;
        private readonly Button _restartClaudeButton;
        private readonly TextBox _portBox = new TextBox { Width = 60 };
        private readonly CheckBox _autoStart = new CheckBox { Content = "Start automatically when Revit starts" };
        private readonly CheckBox _autoUpdate = new CheckBox { Content = "Update server and commands automatically from GitHub" };
        private readonly DataGrid _grid;
        private readonly ObservableCollection<McpCommandRow> _rows = new ObservableCollection<McpCommandRow>();

        public McpSetupWindow(UIApplication uiApp)
        {
            _uiApp = uiApp;
            _revitVersion = uiApp.Application.VersionNumber;
            _settings = McpSettings.Load();

            Title = "Revit MCP Setup – APG Revit Plugins";
            Width = 780;
            Height = 740;
            MinWidth = 640;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApgTheme.Apply(this);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = ApgTheme.CreateHeader("Revit MCP", "Connect Claude and ChatGPT to Revit  ·  Revit " + _revitVersion);
            Grid.SetRow((FrameworkElement)header, 0);
            root.Children.Add(header);

            var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var body = new StackPanel { Margin = new Thickness(16, 10, 16, 8) };
            scroll.Content = body;
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            // ── 1. Server in Revit ──────────────────────────────────────────────
            body.Children.Add(ApgTheme.SectionHeader("1. MCP server inside Revit"));
            var serverPanel = new StackPanel();
            serverPanel.Children.Add(_serverStatus);
            var serverRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            _toggleButton = ApgTheme.PrimaryButton("Start");
            _toggleButton.Click += (_, _) => ToggleServer();
            serverRow.Children.Add(_toggleButton);
            serverRow.Children.Add(new TextBlock { Text = "Port", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 6, 0) });
            _portBox.Text = _settings.Port.ToString();
            _portBox.VerticalAlignment = VerticalAlignment.Center;
            serverRow.Children.Add(_portBox);
            _autoStart.IsChecked = _settings.AutoStart;
            _autoStart.VerticalAlignment = VerticalAlignment.Center;
            _autoStart.Margin = new Thickness(16, 0, 0, 0);
            serverRow.Children.Add(_autoStart);
            serverPanel.Children.Add(serverRow);
            body.Children.Add(ApgTheme.Card(serverPanel));

            // ── 2. Components ───────────────────────────────────────────────────
            body.Children.Add(ApgTheme.SectionHeader("2. Server, command sets and AI clients"));
            var comp = new StackPanel();
            comp.Children.Add(Row("Installed", _installedStatus));
            comp.Children.Add(Row("GitHub", _latestStatus));
            comp.Children.Add(Row("Node.js", _nodeStatus));
            comp.Children.Add(Row("Claude", _claudeStatus));
            _claudeFile.Text = McpPaths.ClaudeConfigFile;
            comp.Children.Add(Row("File", _claudeFile));
            _autoUpdate.IsChecked = _settings.AutoUpdate;
            _autoUpdate.Margin = new Thickness(0, 6, 0, 0);
            comp.Children.Add(_autoUpdate);
            var compButtons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
            _installButton = ApgTheme.PrimaryButton("Install / Update from GitHub");
            _installButton.Margin = new Thickness(0, 0, 8, 6);
            _installButton.Click += async (_, _) => await InstallAsync();
            compButtons.Children.Add(_installButton);
            _claudeButton = ApgTheme.SecondaryButton("Configure Claude");
            _claudeButton.Margin = new Thickness(0, 0, 8, 6);
            _claudeButton.Click += async (_, _) => await ConfigureClaudeAsync();
            compButtons.Children.Add(_claudeButton);
            _restartClaudeButton = ApgTheme.SecondaryButton("Restart Claude Desktop");
            _restartClaudeButton.ToolTip = "Claude Desktop reads its configuration when it starts, " +
                                           "and overwrites the file from memory while it runs.";
            _restartClaudeButton.Margin = new Thickness(0, 0, 8, 6);
            _restartClaudeButton.Click += (_, _) =>
            {
                Say(McpClaudeConfig.RestartClaudeDesktop());
                Refresh();
            };
            compButtons.Children.Add(_restartClaudeButton);
            var copy = ApgTheme.SecondaryButton("Copy config JSON");
            copy.Margin = new Thickness(0, 0, 8, 6);
            copy.Click += (_, _) =>
            {
                Clipboard.SetText(McpClaudeConfig.Snippet(_settings));
                Say("MCP configuration copied to the clipboard - paste it into ChatGPT or any other MCP client.");
            };
            compButtons.Children.Add(copy);
            var showConfig = ApgTheme.SecondaryButton("Show config file");
            showConfig.ToolTip = McpPaths.ClaudeConfigFile;
            showConfig.Margin = new Thickness(0, 0, 8, 6);
            showConfig.Click += (_, _) => Reveal(McpPaths.ClaudeConfigFile);
            compButtons.Children.Add(showConfig);
            var folder = ApgTheme.SecondaryButton("Open folder");
            folder.Margin = new Thickness(0, 0, 8, 6);
            folder.Click += (_, _) =>
            {
                McpPaths.EnsureDirectories();
                Open(McpPaths.Root);
            };
            compButtons.Children.Add(folder);
            var github = ApgTheme.SecondaryButton("GitHub releases");
            github.Margin = new Thickness(0, 0, 8, 6);
            github.Click += (_, _) => Open(McpInstaller.ReleasesUrl);
            compButtons.Children.Add(github);
            comp.Children.Add(compButtons);
            body.Children.Add(ApgTheme.Card(comp));

            // ── 3. Commands ─────────────────────────────────────────────────────
            body.Children.Add(ApgTheme.SectionHeader("3. Commands exposed to Claude and ChatGPT (Revit " + _revitVersion + ")"));
            var cmdPanel = new StackPanel();
            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                SelectionMode = DataGridSelectionMode.Single,
                ItemsSource = _rows,
                Height = 240,
                BorderBrush = ApgTheme.CardBorder,
                BorderThickness = new Thickness(1),
                Background = ApgTheme.CardBackground,
                RowBackground = ApgTheme.CardBackground,
                AlternatingRowBackground = ApgTheme.WindowBackground,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = ApgTheme.CardBorder,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeight = 24
            };
            _grid.Columns.Add(new DataGridCheckBoxColumn { Header = "On", Binding = new Binding(nameof(McpCommandRow.Enabled)), Width = new DataGridLength(40) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Command", Binding = new Binding(nameof(McpCommandRow.Name)), IsReadOnly = true, Width = new DataGridLength(190) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Set", Binding = new Binding(nameof(McpCommandRow.SetName)), IsReadOnly = true, Width = new DataGridLength(150) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Revit " + _revitVersion, Binding = new Binding(nameof(McpCommandRow.Available)), IsReadOnly = true, Width = new DataGridLength(80) });
            _grid.Columns.Add(new DataGridTextColumn { Header = "Description", Binding = new Binding(nameof(McpCommandRow.Description)), IsReadOnly = true, Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
            cmdPanel.Children.Add(_grid);
            var cmdButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var save = ApgTheme.SecondaryButton("Save command selection");
            save.Click += (_, _) => SaveSettings(true);
            cmdButtons.Children.Add(save);
            cmdButtons.Children.Add(new TextBlock
            {
                Text = "Changes apply the next time the server is started.",
                Foreground = ApgTheme.Muted,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            });
            cmdPanel.Children.Add(cmdButtons);
            body.Children.Add(ApgTheme.Card(cmdPanel));

            // ── Footer ──────────────────────────────────────────────────────────
            var footer = new DockPanel { Margin = new Thickness(16, 6, 16, 12) };
            var close = ApgTheme.PrimaryButton("Close");
            close.IsCancel = true;
            close.Click += (_, _) =>
            {
                SaveSettings(false);
                Close();
            };
            DockPanel.SetDock(close, Dock.Right);
            footer.Children.Add(close);
            _message.VerticalAlignment = VerticalAlignment.Center;
            _message.Margin = new Thickness(0, 0, 12, 0);
            footer.Children.Add(_message);
            Grid.SetRow(footer, 2);
            root.Children.Add(footer);

            Content = root;

            Refresh();
            Loaded += async (_, _) => await CheckLatestAsync();
        }

        // ── state ──────────────────────────────────────────────────────────────

        private void Refresh()
        {
            McpSocketService service = McpSocketService.Instance;
            if (service.IsRunning)
            {
                _serverStatus.Text = "RUNNING on port " + service.Port + " with " + service.CommandCount +
                                     " commands (since " + service.StartedAt?.ToString("HH:mm") + ", " +
                                     service.RequestsServed + " requests served). Claude and ChatGPT can drive this Revit session.";
                _serverStatus.Foreground = ApgTheme.Green;
                _toggleButton.Content = "Stop";
                _portBox.IsEnabled = false;
            }
            else
            {
                _serverStatus.Text = "STOPPED. Start it so the MCP server launched by Claude or ChatGPT can reach Revit.";
                _serverStatus.Foreground = ApgTheme.Red;
                _toggleButton.Content = "Start";
                _portBox.IsEnabled = true;
            }

            McpInstalledInfo installed = McpInstaller.ReadInstalled();
            List<McpCommandInfo> infos = McpCommandHost.Discover(_revitVersion, _settings);
            int available = infos.Count(i => !i.BuiltIn && i.AvailableForThisRevit);
            int total = infos.Count(i => !i.BuiltIn);
            if (!McpInstaller.IsServerInstalled && !McpInstaller.IsCommandsInstalled)
            {
                _installedStatus.Text = "Nothing installed yet for the Windows user \"" + Environment.UserName +
                                        "\" - click Install / Update from GitHub. Each account on this computer " +
                                        "gets its own copy under its own profile.";
                _installedStatus.Foreground = ApgTheme.Red;
            }
            else
            {
                _installedStatus.Text =
                    "MCP server " + (McpInstaller.IsServerInstalled ? (installed.ServerVersion ?? "?") : "missing") +
                    "  ·  command sets " + (McpInstaller.IsCommandsInstalled ? (installed.CommandsVersion ?? "?") : "missing") +
                    "  ·  " + available + " of " + total + " commands built for Revit " + _revitVersion +
                    (installed.InstalledUtc.HasValue ? "  ·  installed " + installed.InstalledUtc.Value.ToLocalTime().ToString("yyyy-MM-dd") : "");
                _installedStatus.Foreground = available > 0 && McpInstaller.IsServerInstalled ? ApgTheme.Green : ApgTheme.Red;
            }

            NodeInfo node = McpNode.Resolve();
            if (!node.Found)
            {
                _nodeStatus.Text = "not found - Install / Update from GitHub brings its own Node.js runtime.";
                _nodeStatus.Foreground = ApgTheme.Red;
            }
            else
            {
                _nodeStatus.Text = node.Describe() + (node.IsSupported ? "" : "  ·  version " + McpNode.MinimumVersion + " or newer is required");
                _nodeStatus.Foreground = node.IsSupported ? ApgTheme.Green : ApgTheme.Red;
            }

            var claudeLines = new List<string>();
            bool anyProblem = false;
            foreach (ClaudeConfigTarget target in McpClaudeConfig.AllTargets())
            {
                ClaudeConfigState state = McpClaudeConfig.Inspect(target, _settings, out string details);
                switch (state)
                {
                    case ClaudeConfigState.Configured:
                        claudeLines.Add(target.Name + ": configured");
                        break;
                    case ClaudeConfigState.PointsElsewhere:
                        claudeLines.Add(target.Name + ": entry points elsewhere (" + details + ") - click Configure Claude");
                        anyProblem = true;
                        break;
                    case ClaudeConfigState.NotConfigured:
                        claudeLines.Add(target.Name + ": not configured yet - click Configure Claude");
                        anyProblem = true;
                        break;
                    case ClaudeConfigState.Unreadable:
                        claudeLines.Add(target.Name + ": configuration file is not valid JSON");
                        anyProblem = true;
                        break;
                    default:
                        if (target.CreateIfMissing)
                        {
                            claudeLines.Add(target.Name + ": not installed (no configuration file yet)");
                            anyProblem = true;
                        }
                        break;
                }
            }
            _claudeFile.Text = McpPaths.ClaudeConfigFile;
            bool claudeRunning = McpClaudeConfig.IsClaudeDesktopRunning();
            if (claudeRunning)
                claudeLines.Add("Claude Desktop is open - it rewrites its configuration file while it runs, " +
                                "so Configure Claude will offer to restart it");
            _claudeStatus.Text = string.Join("   ·   ", claudeLines);
            _claudeStatus.Foreground = anyProblem ? ApgTheme.Red : ApgTheme.Green;
            _restartClaudeButton.IsEnabled = claudeRunning;

            _rows.Clear();
            foreach (McpCommandInfo info in infos.OrderBy(i => i.BuiltIn ? 0 : 1).ThenBy(i => i.SetName).ThenBy(i => i.Name))
            {
                _rows.Add(new McpCommandRow
                {
                    Enabled = info.Enabled,
                    Name = info.Name,
                    SetName = info.SetName,
                    Available = info.BuiltIn ? "built-in" : (info.AvailableForThisRevit ? "yes" : "no build"),
                    Description = info.Description
                });
            }
        }

        private async System.Threading.Tasks.Task CheckLatestAsync()
        {
            _latestStatus.Text = "Checking the latest release ...";
            _latestStatus.Foreground = ApgTheme.Muted;
            _latest = await McpInstaller.GetLatestReleaseAsync();
            if (_latest == null)
            {
                _latestStatus.Text = "Could not reach GitHub (offline?). Releases: " + McpInstaller.ReleasesUrl;
                _latestStatus.Foreground = ApgTheme.Muted;
                return;
            }
            Version? current = McpInstaller.ReadInstalled().Version;
            bool newer = current == null || _latest.Version > current;
            _latestStatus.Text = "Latest release " + _latest.Tag +
                                 (_latest.IsComplete ? "" : " (no downloadable assets yet)") +
                                 (newer ? "  ·  UPDATE AVAILABLE" : "  ·  you are up to date");
            _latestStatus.Foreground = newer ? ApgTheme.Accent : ApgTheme.Green;
        }

        // ── actions ────────────────────────────────────────────────────────────

        private void ToggleServer()
        {
            McpSocketService service = McpSocketService.Instance;
            try
            {
                if (service.IsRunning)
                {
                    service.Stop();
                    Say("MCP server stopped.");
                }
                else
                {
                    SaveSettings(false);
                    service.Start(_uiApp, _settings);
                    Say("MCP server started on port " + service.Port + " with " + service.CommandCount + " commands.");
                }
            }
            catch (SocketException ex)
            {
                Say("Port " + _settings.Port + " is busy (" + ex.Message + "). Another Revit session may be using it - change the port.");
            }
            catch (Exception ex)
            {
                McpLog.Error("Start/stop failed", ex);
                Say("Failed: " + ex.Message);
            }
            Refresh();
        }

        private async System.Threading.Tasks.Task InstallAsync()
        {
            if (McpSocketService.Instance.IsRunning)
            {
                Say("Stop the MCP server first: the loaded command DLLs cannot be replaced while it runs.");
                return;
            }
            _installButton.IsEnabled = false;
            try
            {
                if (_latest == null)
                    await CheckLatestAsync();
                if (_latest == null)
                {
                    Say("GitHub is not reachable. Check your internet connection and try again.");
                    return;
                }
                if (!_latest.IsComplete)
                {
                    Say("Release " + _latest.Tag + " has no server/commands zip yet. See " + _latest.ReleaseUrl);
                    return;
                }
                var progress = new Progress<string>(Say);
                string summary = await McpInstaller.InstallAsync(_latest, progress);
                Say(summary + " Restart Claude Desktop to load the new server.");
            }
            catch (Exception ex)
            {
                McpLog.Error("Install failed", ex);
                Say("Install failed: " + ex.Message);
            }
            finally
            {
                _installButton.IsEnabled = true;
                Refresh();
                await CheckLatestAsync();
            }
        }

        /// <summary>
        /// Writes the revit-mcp entry for the Windows account running Revit. Every path in it is
        /// resolved here and now from this account's own profile, so the same button produces the
        /// right file on every computer and for every user - nothing is carried over from whoever
        /// built the plugin.
        /// </summary>
        private async System.Threading.Tasks.Task ConfigureClaudeAsync()
        {
            _claudeButton.IsEnabled = false;
            try
            {
                SaveSettings(false);
                if (!McpInstaller.IsServerInstalled)
                {
                    Say("Install the MCP server first (Install / Update from GitHub), then configure Claude.");
                    return;
                }

                // Make sure the runtime the entry will name is one of ours, under this account's
                // profile, rather than a machine-wide Node.js that may be removed or upgraded.
                NodeInfo node = await McpNode.EnsureOwnAsync(new Progress<string>(Say));
                if (!node.Found)
                {
                    Say("No Node.js runtime is available and one could not be downloaded. " +
                        "Check your internet connection, or install Node.js " + McpNode.MinimumVersion + " or newer.");
                    return;
                }
                // Claude Desktop keeps the configuration in memory and writes it back over ours
                // whenever one of its own settings changes. Closing it first is the only order
                // that survives, so offer exactly that.
                ClaudeRestartMode mode = ClaudeRestartMode.Never;
                if (McpClaudeConfig.IsClaudeDesktopRunning())
                {
                    var ask = new TaskDialog("Configure Claude")
                    {
                        MainInstruction = "Claude Desktop is running.",
                        MainContent = "Claude reads this configuration when it starts and writes its own copy back " +
                                      "while it runs, which undoes changes made from outside. Closing Claude, writing " +
                                      "the configuration and starting it again is the only reliable order.\n\n" +
                                      "Any Claude Code session running inside Claude Desktop is closed as well.",
                        CommonButtons = TaskDialogCommonButtons.Cancel,
                        DefaultButton = TaskDialogResult.CommandLink1
                    };
                    ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Restart Claude Desktop and configure",
                        "Recommended: closes Claude, writes the configuration, starts Claude again.");
                    ask.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Write the configuration anyway",
                        "Claude keeps running and may overwrite the entry; you would then have to close it and try again.");
                    TaskDialogResult answer = ask.Show();
                    if (answer == TaskDialogResult.Cancel)
                    {
                        Say("Claude configuration unchanged.");
                        return;
                    }
                    if (answer == TaskDialogResult.CommandLink1)
                        mode = ClaudeRestartMode.RestartIfRunning;
                }

                ClaudeApplyOutcome outcome = McpClaudeConfig.Apply(_settings, mode);
                string tail = outcome.AnyChanged && !outcome.AnyFailed && !outcome.ClaudeRestarted && !outcome.AtRiskOfRevert
                    ? "  Start Claude Desktop to load the Revit tools."
                    : "";
                if (!outcome.AnyFailed)
                    tail += "  The entry runs " + node.Path + ".";
                Say(outcome.Summary + tail);
                if (outcome.AnyFailed)
                    Reveal(McpPaths.ClaudeConfigFile);
            }
            catch (Exception ex)
            {
                McpLog.Error("Claude configuration failed", ex);
                Say("Could not write the Claude configuration: " + ex.Message);
            }
            finally
            {
                _claudeButton.IsEnabled = true;
            }
            Refresh();
        }

        private void SaveSettings(bool announce)
        {
            try
            {
                _grid.CommitEdit(DataGridEditingUnit.Row, true);
                if (int.TryParse(_portBox.Text.Trim(), out int port) && port > 0 && port <= 65535)
                    _settings.Port = port;
                else
                    _portBox.Text = _settings.Port.ToString();
                _settings.AutoStart = _autoStart.IsChecked == true;
                _settings.AutoUpdate = _autoUpdate.IsChecked == true;
                _settings.DisabledCommands = _rows.Where(r => !r.Enabled && r.Available != "built-in").Select(r => r.Name).ToList();
                _settings.Save();
                if (announce)
                    Say("Settings saved: " + _rows.Count(r => r.Enabled) + " commands enabled, port " + _settings.Port + ".");
            }
            catch (Exception ex)
            {
                Say("Could not save settings: " + ex.Message);
            }
        }

        private void Say(string text)
        {
            _message.Text = text;
            McpLog.Info("[setup] " + text);
        }

        /// <summary>Opens Explorer with the configuration file selected, so it can be checked by hand.</summary>
        private static void Reveal(string path)
        {
            try
            {
                if (System.IO.File.Exists(path))
                    Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
                else
                    Open(System.IO.Path.GetDirectoryName(path)!);
            }
            catch
            {
                // ignore
            }
        }

        private static void Open(string target)
        {
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch
            {
                // ignore
            }
        }

        private static DockPanel Row(string caption, TextBlock value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = caption, Width = 70, Foreground = ApgTheme.Muted });
            row.Children.Add(value);
            return row;
        }
    }
}
