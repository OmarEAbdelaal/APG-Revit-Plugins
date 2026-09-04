using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using CodeCompliance.Core;

namespace CodeCompliance.UI
{
    /// <summary>
    /// About dialog for the whole APG Revit Plugins suite: logo, version, the plugins
    /// included, author and contact details.
    /// </summary>
    public class AboutWindow : Window
    {
        private const string RepoUrl = "https://github.com/OmarEAbdelaal/APG-Revit-Plugins";

        public AboutWindow(string revitVersion)
        {
            Title = "About APG Revit Plugins";
            Width = 520;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ApgTheme.Apply(this);

            string version =
                Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = ApgTheme.CreateHeader("APG Revit Plugins",
                "Version " + version + "  ·  Revit " + revitVersion);
            Grid.SetRow((FrameworkElement)header, 0);
            root.Children.Add(header);

            var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            body.Children.Add(ApgTheme.SectionHeader("Plugins in this suite"));
            body.Children.Add(ApgTheme.Card(PluginEntry(
                "Code Compliance – Fire Fighting",
                "Reviews fire-fighting designs against applicable codes: escape stair tagging, " +
                "travel distance paths, door fire-rating checks, egress schedules and HTML/CSV reports.")));
            body.Children.Add(ApgTheme.Card(PluginEntry(
                "Ramp Creator",
                "Creates code-compliant parking ramps from a drawn path per Dubai Building Code " +
                "Annex B (Tables B.9 / B.10), with live compliance checking before anything is built.")));
            body.Children.Add(ApgTheme.Card(PluginEntry(
                "Magic Annotation",
                "Annotates a plan, section or elevation view in one step: dimensions, tags, spot " +
                "elevations, ramp slopes and stair arrows — placed clash-free, replaceable on re-run.")));

            body.Children.Add(ApgTheme.Card(PluginEntry(
                "Revit MCP",
                "Connects Claude (Desktop / Code) to Revit through the Model Context Protocol: an in-Revit " +
                "JSON-RPC server plus an MCP server and command sets installed and updated from GitHub.")));

            body.Children.Add(ApgTheme.SectionHeader("Company & author"));
            var info = new StackPanel();
            info.Children.Add(InfoRow("Company", "APG"));
            info.Children.Add(InfoRow("Author", "Omar Elsayed"));
            info.Children.Add(InfoRow("Contact", "omar.e.abdelaal@gmail.com"));

            var linkRow = new DockPanel { Margin = new Thickness(0, 2, 0, 0) };
            linkRow.Children.Add(new TextBlock
            {
                Text = "Project",
                Width = 70,
                Foreground = ApgTheme.Muted
            });
            var link = new Hyperlink(new Run(RepoUrl)) { Foreground = ApgTheme.Accent };
            link.Click += (_, _) =>
                Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
            linkRow.Children.Add(new TextBlock(link));
            info.Children.Add(linkRow);
            body.Children.Add(ApgTheme.Card(info));

            body.Children.Add(ApgTheme.SectionHeader("Updates"));
            var updatePanel = new DockPanel();
            var updateStatus = new TextBlock
            {
                Text = "Updates are published on the GitHub releases page.",
                Foreground = ApgTheme.Muted,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            var checkButton = ApgTheme.SecondaryButton("Check for updates");
            DockPanel.SetDock(checkButton, Dock.Left);
            checkButton.Click += async (_, _) =>
            {
                checkButton.IsEnabled = false;
                updateStatus.Text = "Checking GitHub for a newer version...";
                UpdateInfo? info = await UpdateChecker.CheckAsync();
                if (info == null)
                    updateStatus.Text = "Could not reach GitHub. Check your internet connection and try again.";
                else if (info.IsNewer)
                {
                    updateStatus.Text = "Version " + info.Latest.ToString(3) + " is available.";
                    new UpdateWindow(info).ShowDialog();
                }
                else
                    updateStatus.Text = "You have the latest version (" + info.Current.ToString(3) + ").";
                checkButton.IsEnabled = true;
            };
            updatePanel.Children.Add(checkButton);
            updatePanel.Children.Add(updateStatus);
            body.Children.Add(ApgTheme.Card(updatePanel));

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 4, 0, 0)
            };
            var close = ApgTheme.PrimaryButton("Close");
            close.IsDefault = true;
            close.IsCancel = true;
            close.Click += (_, _) => Close();
            buttons.Children.Add(close);
            body.Children.Add(buttons);

            Content = root;
        }

        private static StackPanel PluginEntry(string name, string description)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = name,
                FontWeight = FontWeights.SemiBold,
                Foreground = ApgTheme.Blue
            });
            panel.Children.Add(new TextBlock
            {
                Text = description,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ApgTheme.Muted,
                Margin = new Thickness(0, 2, 0, 0)
            });
            return panel;
        }

        private static DockPanel InfoRow(string caption, string value)
        {
            var row = new DockPanel { Margin = new Thickness(0, 2, 0, 2) };
            row.Children.Add(new TextBlock { Text = caption, Width = 70, Foreground = ApgTheme.Muted });
            row.Children.Add(new TextBlock { Text = value, FontWeight = FontWeights.SemiBold });
            return row;
        }
    }
}
