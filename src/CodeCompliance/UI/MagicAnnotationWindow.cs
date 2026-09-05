using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using CodeCompliance.Core.Annotation;

namespace CodeCompliance.UI
{
    /// <summary>
    /// The Magic Annotation checklist: every run the user explicitly ticks which
    /// annotation types to place (nothing is preselected from the view scale, by
    /// request). Items that do not apply to the active view type are disabled with
    /// an explanation. Built in code (no XAML) like every APG dialog.
    /// </summary>
    public class MagicAnnotationWindow : Window
    {
        private readonly List<CheckBox> _boxes = new List<CheckBox>();

        private readonly CheckBox _overallDims;
        private readonly CheckBox _gridDims;
        private readonly CheckBox _openingDims;
        private readonly CheckBox _levelDims;
        private readonly CheckBox _roomTags;
        private readonly CheckBox _doorTags;
        private readonly CheckBox _windowTags;
        private readonly CheckBox _wallTags;
        private readonly CheckBox _spotElevations;
        private readonly CheckBox _rampSlopes;
        private readonly CheckBox _stairPaths;
        private readonly CheckBox _callouts;
        private readonly CheckBox _replaceExisting;

        public bool Confirmed { get; private set; }

        public MagicAnnotationWindow(string viewName, int viewScale, bool isPlan)
        {
            Title = "Magic Annotation – APG Revit Plugins";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            ApgTheme.Apply(this);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var header = ApgTheme.CreateHeader("Magic Annotation",
                viewName + "  ·  1 : " + viewScale);
            Grid.SetRow((FrameworkElement)header, 0);
            root.Children.Add(header);

            var body = new StackPanel { Margin = new Thickness(16, 12, 16, 14) };
            Grid.SetRow(body, 1);
            root.Children.Add(body);

            body.Children.Add(new TextBlock
            {
                Text = "Tick the annotations to place in this view. Everything is created in " +
                       "one step (one undo), and re-running replaces what Magic Annotation " +
                       "placed before.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = ApgTheme.Muted,
                Margin = new Thickness(0, 0, 0, 4)
            });

            string planOnly = "Available in plan views only.";
            string sectionOnly = "Available in section and elevation views only.";

            body.Children.Add(ApgTheme.SectionHeader("Dimensions"));
            var dims = new StackPanel();
            _overallDims = Add(dims, "Overall dimensions",
                isPlan ? "First-to-last grid, outside the grid string."
                       : "Lowest-to-highest level, outside the level string.", true);
            _gridDims = Add(dims, "Grid dimensions",
                "Every grid, along the bottom and left of the drawing.", isPlan, planOnly);
            _openingDims = Add(dims, "Detailed opening dimensions",
                "Doors/windows along each exterior wall, wall end to wall end.", isPlan, planOnly);
            _levelDims = Add(dims, "Level dimensions",
                "Floor-to-floor string on the right side of the view.", !isPlan, sectionOnly);
            body.Children.Add(ApgTheme.Card(dims));

            body.Children.Add(ApgTheme.SectionHeader("Tags"));
            var tags = new StackPanel();
            _roomTags = Add(tags, "Room tags", "Centre of each room; already-tagged rooms are skipped.", isPlan, planOnly);
            _doorTags = Add(tags, "Door tags", "Beside each door, using the default loaded door tag.", isPlan, planOnly);
            _windowTags = Add(tags, "Window tags", "Beside each window, on the exterior side.", isPlan, planOnly);
            _wallTags = Add(tags, "Wall tags", "Mid-point of each wall.", true);
            body.Children.Add(ApgTheme.Card(tags));

            body.Children.Add(ApgTheme.SectionHeader("Symbols"));
            var symbols = new StackPanel();
            _spotElevations = Add(symbols, "Spot elevations at stairs and ramps",
                "Lowest and highest walkable surface of each stair/ramp.", isPlan, planOnly);
            _rampSlopes = Add(symbols, "Ramp slope notes",
                "\"UP  S = x.x%\" at each ramp (exact for APG-created ramps).", isPlan, planOnly);
            _stairPaths = Add(symbols, "Stair path arrows",
                "Revit up/down path annotation for stairs missing one.", isPlan, planOnly);
            _callouts = Add(symbols, "Suggest callouts",
                "Lists stairs, ramps and wet areas that deserve a callout — nothing is created.", true);
            body.Children.Add(ApgTheme.Card(symbols));

            var optionsPanel = new StackPanel();
            _replaceExisting = Add(optionsPanel, "Replace previous Magic Annotation run",
                "Deletes what this tool placed in the view last time before placing anew.", true);
            _replaceExisting.IsChecked = true;
            body.Children.Add(ApgTheme.Card(optionsPanel));

            var buttons = new Grid { Margin = new Thickness(0, 4, 0, 0) };
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var selectAll = ApgTheme.SecondaryButton("Select all");
            selectAll.MinWidth = 80;
            selectAll.Click += (_, _) => SetAll(true);
            buttons.Children.Add(selectAll);

            var selectNone = ApgTheme.SecondaryButton("None");
            selectNone.MinWidth = 64;
            selectNone.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(selectNone, 1);
            selectNone.Click += (_, _) => SetAll(false);
            buttons.Children.Add(selectNone);

            var ok = ApgTheme.PrimaryButton("Annotate");
            ok.IsDefault = true;
            Grid.SetColumn(ok, 3);
            ok.Click += (_, _) =>
            {
                Confirmed = true;
                Close();
            };
            buttons.Children.Add(ok);

            var cancel = ApgTheme.SecondaryButton("Cancel");
            cancel.IsCancel = true;
            cancel.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(cancel, 4);
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(cancel);

            body.Children.Add(buttons);
            Content = root;
        }

        private CheckBox Add(Panel parent, string caption, string hint, bool enabled, string? disabledHint = null)
        {
            var box = new CheckBox
            {
                Content = caption,
                IsEnabled = enabled,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0),
                ToolTip = enabled ? hint : disabledHint
            };
            parent.Children.Add(box);
            parent.Children.Add(new TextBlock
            {
                Text = enabled ? hint : (disabledHint ?? hint),
                Foreground = ApgTheme.Muted,
                FontSize = 11.0,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(19, 0, 0, 2),
                Opacity = enabled ? 1.0 : 0.6
            });
            if (enabled)
                _boxes.Add(box);
            return box;
        }

        /// <summary>Select all/none — the replace option is a mode, not an annotation, so it stays.</summary>
        private void SetAll(bool value)
        {
            foreach (CheckBox box in _boxes)
                if (!ReferenceEquals(box, _replaceExisting))
                    box.IsChecked = value;
        }

        public AnnotationOptions BuildOptions()
        {
            return new AnnotationOptions
            {
                OverallDimensions = _overallDims.IsChecked == true,
                GridDimensions = _gridDims.IsChecked == true,
                OpeningDimensions = _openingDims.IsChecked == true,
                LevelDimensions = _levelDims.IsChecked == true,
                RoomTags = _roomTags.IsChecked == true,
                DoorTags = _doorTags.IsChecked == true,
                WindowTags = _windowTags.IsChecked == true,
                WallTags = _wallTags.IsChecked == true,
                SpotElevations = _spotElevations.IsChecked == true,
                RampSlopeNotes = _rampSlopes.IsChecked == true,
                StairPaths = _stairPaths.IsChecked == true,
                SuggestCallouts = _callouts.IsChecked == true,
                ReplaceExisting = _replaceExisting.IsChecked == true
            };
        }

        public bool AnythingTicked()
        {
            AnnotationOptions o = BuildOptions();
            return o.OverallDimensions || o.GridDimensions || o.OpeningDimensions || o.LevelDimensions ||
                   o.RoomTags || o.DoorTags || o.WindowTags || o.WallTags ||
                   o.SpotElevations || o.RampSlopeNotes || o.StairPaths || o.SuggestCallouts;
        }
    }
}
