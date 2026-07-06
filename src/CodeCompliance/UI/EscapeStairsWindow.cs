using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CodeCompliance.UI
{
    /// <summary>One row in the escape-stair selection grid.</summary>
    public class EscapeStairItem
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string BaseLevel { get; set; } = "";
        public bool IsEscape { get; set; }
    }

    /// <summary>
    /// Lets the user tick which stairs are escape (egress) stairs.
    /// Built in code (no XAML) so it compiles identically for every Revit target.
    /// </summary>
    public class EscapeStairsWindow : Window
    {
        private readonly DataGrid _grid;

        public bool Confirmed { get; private set; }
        public IReadOnlyList<EscapeStairItem> Items { get; }

        public EscapeStairsWindow(IReadOnlyList<EscapeStairItem> items)
        {
            Items = items;

            Title = "Escape Stairs – APG Revit Plugins";
            Width = 600;
            Height = 480;
            MinWidth = 480;
            MinHeight = 340;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            ApgTheme.Apply(this);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = ApgTheme.CreateHeader("Escape Stairs",
                "Code Compliance – Fire Fighting  ·  Step 1 of 3");
            Grid.SetRow((FrameworkElement)header, 0);
            root.Children.Add(header);

            var intro = new TextBlock
            {
                Text = "Tick the stairs that serve as escape (egress) stairs. " +
                       "The choice is saved to the '" + Core.EscapeStairService.ParameterName +
                       "' parameter on each stair.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = ApgTheme.Muted,
                Margin = new Thickness(16, 12, 16, 8)
            };
            Grid.SetRow(intro, 1);
            root.Children.Add(intro);

            _grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                IsReadOnly = false,
                SelectionMode = DataGridSelectionMode.Single,
                ItemsSource = items,
                Margin = new Thickness(16, 0, 16, 0),
                BorderBrush = ApgTheme.CardBorder,
                BorderThickness = new Thickness(1),
                Background = ApgTheme.CardBackground,
                RowBackground = ApgTheme.CardBackground,
                AlternatingRowBackground = ApgTheme.WindowBackground,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = ApgTheme.CardBorder,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeight = 26
            };
            _grid.Columns.Add(new DataGridCheckBoxColumn
            {
                Header = "Escape",
                Binding = new Binding(nameof(EscapeStairItem.IsEscape)),
                Width = new DataGridLength(70)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Stair",
                Binding = new Binding(nameof(EscapeStairItem.Name)),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Type",
                Binding = new Binding(nameof(EscapeStairItem.TypeName)),
                IsReadOnly = true,
                Width = new DataGridLength(150)
            });
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Base Level",
                Binding = new Binding(nameof(EscapeStairItem.BaseLevel)),
                IsReadOnly = true,
                Width = new DataGridLength(110)
            });
            Grid.SetRow(_grid, 2);
            root.Children.Add(_grid);

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 12, 16, 14)
            };
            var ok = ApgTheme.PrimaryButton("Save");
            ok.Margin = new Thickness(0, 0, 8, 0);
            ok.IsDefault = true;
            ok.Click += (_, _) =>
            {
                _grid.CommitEdit(DataGridEditingUnit.Row, true);
                Confirmed = true;
                Close();
            };
            var cancel = ApgTheme.SecondaryButton("Cancel");
            cancel.IsCancel = true;
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            Content = root;
        }
    }
}
