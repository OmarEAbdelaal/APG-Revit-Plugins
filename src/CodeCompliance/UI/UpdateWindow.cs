using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using CodeCompliance.Core;

namespace CodeCompliance.UI
{
    /// <summary>
    /// Small notification shown (once per new version) when a newer suite release
    /// exists on GitHub. Downloading opens the browser; the DLL is locked while
    /// Revit runs, so the user installs after closing Revit.
    /// </summary>
    public class UpdateWindow : Window
    {
        public UpdateWindow(UpdateInfo info)
        {
            Title = "Update available – APG Revit Plugins";
            Width = 460;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ApgTheme.Apply(this);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = ApgTheme.CreateHeader("Update available",
                "APG Revit Plugins " + info.Latest.ToString(3));
            Grid.SetRow((FrameworkElement)header, 0);
            root.Children.Add(header);

            var body = new StackPanel { Margin = new Thickness(16, 14, 16, 14) };
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            body.Children.Add(new TextBlock
            {
                Text = "A newer version of the APG Revit Plugins suite is available.\n" +
                       "You have " + info.Current.ToString(3) + "  →  new version " + info.Latest.ToString(3) + ".",
                TextWrapping = TextWrapping.Wrap
            });
            body.Children.Add(new TextBlock
            {
                Text = "Download the installer, close Revit, run it (it updates in place), " +
                       "then start Revit again.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = ApgTheme.Muted,
                Margin = new Thickness(0, 6, 0, 0)
            });

            var notes = new TextBlock { Margin = new Thickness(0, 6, 0, 0) };
            var link = new Hyperlink(new Run("View release notes")) { Foreground = ApgTheme.Accent };
            link.Click += (_, _) => Open(info.ReleaseUrl);
            notes.Inlines.Add(link);
            body.Children.Add(notes);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 12, 0, 0)
            };
            var download = ApgTheme.PrimaryButton("Download update");
            download.Margin = new Thickness(0, 0, 8, 0);
            download.IsDefault = true;
            download.Click += (_, _) =>
            {
                Open(info.DownloadUrl);
                Close();
            };
            var later = ApgTheme.SecondaryButton("Later");
            later.IsCancel = true;
            later.Click += (_, _) => Close();
            buttons.Children.Add(download);
            buttons.Children.Add(later);
            body.Children.Add(buttons);

            Content = root;
        }

        private static void Open(string url)
            => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
