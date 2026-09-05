using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CodeCompliance.Core;

namespace CodeCompliance.UI
{
    /// <summary>A floor type the user can build the ramp with (UI-side DTO, no Revit types).</summary>
    public class FloorTypeItem
    {
        public string Name { get; set; } = "";
        public long Id { get; set; }
        public double ThicknessM { get; set; }

        public override string ToString()
            => ThicknessM > 0 ? $"{Name}  ({ThicknessM:F2} m)" : Name;
    }

    /// <summary>
    /// Input dialog for the parking ramp creator. Mirrors the standalone
    /// "Parking Ramp Calculator" app: the user picks the ramp type and the target
    /// parameter to solve for (R, h or S), enters the other two, and the dialog
    /// computes the design and shows Dubai Building Code compliance (Annex B,
    /// Tables B.9 / B.10) live, before anything is created in the model.
    /// Built in code (no XAML) so it compiles identically for every Revit target.
    /// </summary>
    public class RampInputWindow : Window
    {
        private static readonly Brush Green = ApgTheme.Green;
        private static readonly Brush Red = ApgTheme.Red;
        private static readonly Brush Muted = ApgTheme.Muted;
        private static readonly Brush Blue = ApgTheme.Accent;

        private readonly RampPath _path;
        private readonly bool _singleArc;
        private readonly bool _variableWidth;

        private readonly ComboBox _typeBox;
        private readonly ComboBox _lanesBox;
        private readonly TextBox _laneWidthBox;
        private readonly ComboBox _floorTypeBox;
        private readonly Dictionary<RampLineLocation, RadioButton> _locationRadios = new Dictionary<RampLineLocation, RadioButton>();
        private readonly Dictionary<RampSolveTarget, RadioButton> _solveRadios = new Dictionary<RampSolveTarget, RadioButton>();
        private readonly Dictionary<RampEndAnchor, RadioButton> _anchorRadios = new Dictionary<RampEndAnchor, RadioButton>();
        private readonly TextBox _hBox;
        private readonly TextBox _sBox;
        private readonly TextBox _rBox;
        private readonly TextBlock _error;
        private readonly Button _okButton;

        private readonly Dictionary<string, TextBlock> _resultLabels = new Dictionary<string, TextBlock>();
        private readonly Dictionary<string, TextBlock> _complianceLabels = new Dictionary<string, TextBlock>();

        public bool Confirmed { get; private set; }
        public RampCalcResult? Result { get; private set; }
        public int Lanes { get; private set; } = 1;
        public double LaneWidth { get; private set; }
        public RampLineLocation Location { get; private set; } = RampLineLocation.Center;
        public double TotalWidth => LaneWidth * Lanes;

        /// <summary>
        /// Signed lateral offset (m, + = left of travel) of the slope/stationing
        /// reference line: the centreline of the innermost lane for multi-lane
        /// curved ramps (the code-governing line), 0 otherwise (including for a
        /// drawn outline, whose width already varies as drawn).
        /// </summary>
        public double DesignOffset { get; private set; }

        /// <summary>Which end of the drawn path stays put when the ramp is built to exactly R.</summary>
        public RampEndAnchor Anchor { get; private set; } = RampEndAnchor.Start;

        /// <summary>
        /// Path station where the ramp begins. 0 when the start is the fixed end;
        /// negative when the end is fixed and the ramp has to start before the
        /// drawn path to reach the required run.
        /// </summary>
        public double StartStation { get; private set; }

        public long FloorTypeId => (_floorTypeBox.SelectedItem as FloorTypeItem)?.Id ?? -1;

        /// <summary>Thickness of the chosen floor type (used for the loop clearance check).</summary>
        public double SlabThickness
        {
            get
            {
                double t = (_floorTypeBox.SelectedItem as FloorTypeItem)?.ThicknessM ?? 0;
                return t > 0 ? t : 0.30;
            }
        }

        public RampInputWindow(RampPath path, IReadOnlyList<FloorTypeItem> floorTypes, long defaultFloorTypeId)
        {
            _path = path;
            _singleArc = path.HasArc && path.Segments.Count == 1;
            _variableWidth = path.IsVariableWidth;

            Title = "Parking Ramp – APG Revit Plugins";
            Width = 760;
            Height = 700;
            MinWidth = 640;
            MinHeight = 520;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.CanResize;
            ApgTheme.Apply(this);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var band = ApgTheme.CreateHeader("Parking Ramp Designer",
                "Ramp Creator  ·  Dubai Building Code Annex B, B.7.2.2");
            Grid.SetRow((FrameworkElement)band, 0);
            root.Children.Add(band);

            var header = new TextBlock
            {
                Text = (_variableWidth
                            ? $"Path: drawn outline, centerline length {path.DrawnLength:F2} m, " +
                              $"width {path.MinWidthAlongPath():F2}-{path.MaxWidthAlongPath():F2} m. "
                            : $"Path: {path.Segments.Count} segment(s), drawn length {path.DrawnLength:F2} m" +
                              (path.HasArc ? " (includes arcs). " : ". ")) +
                       "The ramp starts at the start point of the first line/curve you drew or " +
                       "picked. Enter two of the three key parameters (h, S, R); the third is " +
                       "solved per Dubai Building Code Annex B Tables B.9 / B.10. The ramp is " +
                       "created as one continuous floor slab shaped with slab-shape (modify " +
                       "sub-elements) points.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted,
                Margin = new Thickness(16, 10, 16, 8)
            };
            Grid.SetRow(header, 1);
            root.Children.Add(header);

            var body = new Grid { Margin = new Thickness(16, 0, 16, 0) };
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(body, 2);
            root.Children.Add(body);

            // ── Left column: inputs ─────────────────────────────────────────────
            var leftScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Margin = new Thickness(0, 0, 10, 0)
            };
            Grid.SetColumn(leftScroll, 0);
            body.Children.Add(leftScroll);
            var left = new StackPanel();
            leftScroll.Content = left;

            left.Children.Add(SectionHeader("Ramp configuration"));

            left.Children.Add(FieldLabel("Ramp type (Table B.9)"));
            _typeBox = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
            foreach (RampType t in AllowedTypes())
                _typeBox.Items.Add(t);
            _typeBox.SelectedIndex = 0;
            _typeBox.SelectionChanged += (_, _) => OnTypeChanged();
            left.Children.Add(_typeBox);

            left.Children.Add(FieldLabel("Number of lanes"));
            _lanesBox = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
            foreach (int nLanes in new[] { 1, 2, 3 })
                _lanesBox.Items.Add(nLanes);
            _lanesBox.SelectedIndex = 0;
            left.Children.Add(_lanesBox);
            left.Children.Add(new TextBlock
            {
                Text = "For multi-lane curved ramps, slope and run are measured along the " +
                       "centreline of the innermost lane (nearest the inner curve).",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6)
            });

            left.Children.Add(FieldLabel(_variableWidth
                ? "Lane width [m]  (from the drawn outline's narrowest point)"
                : "Lane width [m]  (min per Table B.9)"));
            _laneWidthBox = NumberBox();
            left.Children.Add(_laneWidthBox);
            if (_variableWidth)
            {
                _lanesBox.IsEnabled = false;
                _lanesBox.SelectedIndex = 0;
                _laneWidthBox.IsEnabled = false;
                _laneWidthBox.Text = path.MinWidthAlongPath().ToString("F2", CultureInfo.InvariantCulture);
            }

            left.Children.Add(FieldLabel("Floor type (ramp is built from floors)"));
            _floorTypeBox = new ComboBox { Margin = new Thickness(0, 2, 0, 6) };
            int selectedIndex = 0;
            for (int i = 0; i < floorTypes.Count; i++)
            {
                _floorTypeBox.Items.Add(floorTypes[i]);
                if (floorTypes[i].Id == defaultFloorTypeId)
                    selectedIndex = i;
            }
            if (_floorTypeBox.Items.Count > 0)
                _floorTypeBox.SelectedIndex = selectedIndex;
            left.Children.Add(_floorTypeBox);

            left.Children.Add(SectionHeader("Drawn line represents"));
            var locPanel = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (RampLineLocation loc in new[] { RampLineLocation.Left, RampLineLocation.Center, RampLineLocation.Right })
            {
                var rb = new RadioButton
                {
                    Content = loc == RampLineLocation.Left ? "Left edge"
                            : loc == RampLineLocation.Right ? "Right edge" : "Centerline",
                    GroupName = "loc",
                    Margin = new Thickness(0, 2, 14, 6),
                    IsChecked = loc == RampLineLocation.Center,
                    IsEnabled = !_variableWidth
                };
                _locationRadios[loc] = rb;
                locPanel.Children.Add(rb);
            }
            left.Children.Add(locPanel);
            left.Children.Add(new TextBlock
            {
                Text = _variableWidth
                    ? "Not used — the left and right edges were drawn explicitly, so the ramp's " +
                      "actual width and starting point (the left edge's start) come straight from the outline."
                    : "Left/right are relative to the direction the path was drawn (direction of " +
                      "travel, going up) — the ramp starts at that line's start point.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6)
            });

            left.Children.Add(SectionHeader("Build to the exact run — fixed end"));
            var anchorItems = new (RampEndAnchor Anchor, string Text)[]
            {
                (RampEndAnchor.Start, "Start of the sketch (the top end moves)"),
                (RampEndAnchor.End, "End of the sketch (the bottom end moves)"),
            };
            foreach ((RampEndAnchor anchor, string text) in anchorItems)
            {
                var rb = new RadioButton
                {
                    Content = text,
                    GroupName = "anchor",
                    Margin = new Thickness(0, 2, 0, 2),
                    IsChecked = anchor == RampEndAnchor.Start
                };
                _anchorRadios[anchor] = rb;
                left.Children.Add(rb);
            }
            left.Children.Add(new TextBlock
            {
                Text = "The ramp is always built to exactly the computed run R. The fixed end stays " +
                       "where you drew it; the other end is extended along the drawn geometry " +
                       "(straight on, or around the same curve) or stopped short as needed.",
                TextWrapping = TextWrapping.Wrap,
                Foreground = Muted,
                FontSize = 11,
                Margin = new Thickness(0, 0, 0, 6)
            });

            left.Children.Add(SectionHeader("Solve for (target parameter)"));
            var solveItems = new (RampSolveTarget target, string text)[]
            {
                (RampSolveTarget.TotalRun, "Total run R [m]"),
                (RampSolveTarget.FloorHeight, "Floor-to-floor height h [m]"),
                (RampSolveTarget.Slope, "Main slope S [%]"),
            };
            foreach ((RampSolveTarget target, string text) in solveItems)
            {
                var rb = new RadioButton
                {
                    Content = text,
                    GroupName = "solve",
                    Margin = new Thickness(0, 2, 0, 2),
                    IsChecked = target == RampSolveTarget.TotalRun
                };
                rb.Checked += (_, _) => OnSolveTargetChanged();
                _solveRadios[target] = rb;
                left.Children.Add(rb);
            }

            left.Children.Add(SectionHeader("Input values"));
            left.Children.Add(FieldLabel("h — floor-to-floor height [m]"));
            _hBox = NumberBox();
            left.Children.Add(_hBox);
            left.Children.Add(FieldLabel("S — main ramp slope [%]"));
            _sBox = NumberBox();
            left.Children.Add(_sBox);
            left.Children.Add(FieldLabel("R — total horizontal run [m]"));
            _rBox = NumberBox();
            left.Children.Add(_rBox);

            Button calc = ApgTheme.PrimaryButton("Calculate + check compliance");
            calc.Margin = new Thickness(0, 10, 0, 4);
            calc.Click += (_, _) => Calculate();
            left.Children.Add(calc);

            _error = new TextBlock
            {
                Foreground = Red,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            };
            left.Children.Add(_error);

            // ── Right column: results + compliance ─────────────────────────────
            var rightScroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(rightScroll, 1);
            body.Children.Add(rightScroll);
            var right = new StackPanel();
            rightScroll.Content = right;

            right.Children.Add(SectionHeader("Computed results"));
            AddResultRow(right, "h", "Floor height h");
            AddResultRow(right, "S", "Main slope S");
            AddResultRow(right, "T", "Transition slope T (= S/2)");
            AddResultRow(right, "X", "Transition length X (Table B.10)");
            AddResultRow(right, "Y", "Transition rise Y (each zone)");
            AddResultRow(right, "Yp", "Main ramp rise Y'");
            AddResultRow(right, "Xp", "Main ramp run X'");
            AddResultRow(right, "R", "Total horizontal run R");
            AddResultRow(right, "ratio", "Slope ratio (1 : n)");
            AddResultRow(right, "W", "Total ramp width W");
            if (path.HasArc)
                AddResultRow(right, "ri", "Min inner radius (from drawing)");
            if (_singleArc)
            {
                AddResultRow(right, "rc", "Centreline radius");
                AddResultRow(right, "sweep", "Sweep angle");
                AddResultRow(right, "Li", "Inner edge arc length");
                AddResultRow(right, "Lo", "Outer edge arc length");
            }

            right.Children.Add(SectionHeader("Code compliance — Table B.9"));
            AddComplianceRow(right, "slope", "Max slope");
            AddComplianceRow(right, "width", "Min lane width");
            AddComplianceRow(right, "radius", "Min inner radius");
            AddComplianceRow(right, "clearance", "Min clearance 2.4 m");
            AddComplianceRow(right, "length", "Drawn length vs required run R");

            // ── Buttons ─────────────────────────────────────────────────────────
            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(16, 10, 16, 14)
            };
            _okButton = ApgTheme.PrimaryButton("Create ramp floors");
            _okButton.Margin = new Thickness(0, 0, 8, 0);
            _okButton.IsEnabled = false;
            _okButton.IsDefault = true;
            _okButton.Click += (_, _) =>
            {
                Confirmed = true;
                Close();
            };
            Button cancel = ApgTheme.SecondaryButton("Cancel");
            cancel.IsCancel = true;
            cancel.Click += (_, _) => Close();
            buttons.Children.Add(_okButton);
            buttons.Children.Add(cancel);
            Grid.SetRow(buttons, 3);
            root.Children.Add(buttons);

            Content = root;
            OnTypeChanged();
        }

        private IEnumerable<RampType> AllowedTypes()
        {
            if (_path.HasArc)
            {
                yield return RampType.Curved;
                yield return RampType.Helical;
            }
            else
            {
                yield return RampType.Straight;
            }
        }

        private RampType SelectedType => (RampType)(_typeBox.SelectedItem ?? RampType.Straight);

        private RampSolveTarget SelectedSolveTarget
        {
            get
            {
                foreach (KeyValuePair<RampSolveTarget, RadioButton> kv in _solveRadios)
                    if (kv.Value.IsChecked == true)
                        return kv.Key;
                return RampSolveTarget.TotalRun;
            }
        }

        private RampLineLocation SelectedLocation
        {
            get
            {
                foreach (KeyValuePair<RampLineLocation, RadioButton> kv in _locationRadios)
                    if (kv.Value.IsChecked == true)
                        return kv.Key;
                return RampLineLocation.Center;
            }
        }

        private RampEndAnchor SelectedAnchor
        {
            get
            {
                foreach (KeyValuePair<RampEndAnchor, RadioButton> kv in _anchorRadios)
                    if (kv.Value.IsChecked == true)
                        return kv.Key;
                return RampEndAnchor.Start;
            }
        }

        private void OnTypeChanged()
        {
            if (!_variableWidth)
            {
                RampRegulations regs = RampCalculator.GetRegulations(SelectedType);
                _laneWidthBox.Text = regs.MinLaneWidth.ToString("F2", CultureInfo.InvariantCulture);
            }
            OnSolveTargetChanged();
        }

        private void OnSolveTargetChanged()
        {
            if (_hBox == null || _sBox == null || _rBox == null)
                return; // still constructing
            RampSolveTarget solve = SelectedSolveTarget;
            _hBox.IsEnabled = solve != RampSolveTarget.FloorHeight;
            _sBox.IsEnabled = solve != RampSolveTarget.Slope;
            _rBox.IsEnabled = solve != RampSolveTarget.TotalRun;

            TextBox solved = solve == RampSolveTarget.FloorHeight ? _hBox
                           : solve == RampSolveTarget.Slope ? _sBox : _rBox;
            solved.Text = "";

            // When R is a known input, prefill it from the drawn path so the model
            // and the calculation agree by default.
            if (solve != RampSolveTarget.TotalRun && string.IsNullOrWhiteSpace(_rBox.Text))
            {
                double w = EstimateTotalWidth();
                _rBox.Text = _path.CenterlineLength(SelectedLocation, w, EstimateDesignOffset(w))
                    .ToString("F3", CultureInfo.InvariantCulture);
            }
        }

        private double EstimateDesignOffset(double totalWidth)
        {
            int lanes = _lanesBox.SelectedItem is int n ? n : 1;
            return _path.DesignOffsetFor(SelectedLocation, totalWidth, lanes);
        }

        private double EstimateTotalWidth()
        {
            double laneWidth = TryParse(_laneWidthBox.Text)
                ?? RampCalculator.GetRegulations(SelectedType).MinLaneWidth;
            int lanes = _lanesBox.SelectedItem is int n ? n : 1;
            return laneWidth * lanes;
        }

        private void Calculate()
        {
            _error.Text = "";
            _okButton.IsEnabled = false;
            Result = null;

            try
            {
                RampType type = SelectedType;
                RampRegulations regs = RampCalculator.GetRegulations(type);
                Lanes = _lanesBox.SelectedItem is int n ? n : 1;
                Location = SelectedLocation;
                Anchor = SelectedAnchor;

                double? laneWidth = TryParse(_laneWidthBox.Text);
                if (!laneWidth.HasValue || laneWidth.Value <= 0)
                    throw new RampCalcException("Enter a valid lane width.");
                LaneWidth = laneWidth.Value;

                if (FloorTypeId < 0)
                    throw new RampCalcException("Select a floor type.");

                DesignOffset = _path.DesignOffsetFor(Location, TotalWidth, Lanes);

                RampSolveTarget solve = SelectedSolveTarget;
                double? h = solve == RampSolveTarget.FloorHeight ? null : RequireValue(_hBox, "h");
                double? s = solve == RampSolveTarget.Slope ? null : RequireValue(_sBox, "S");
                double? r = solve == RampSolveTarget.TotalRun ? null : RequireValue(_rBox, "R");

                RampCalcResult res = RampCalculator.Compute(h, s, r, type);
                Result = res;

                // Build to exactly R: hold the chosen end, move the other one.
                double drawnLength = _path.CenterlineLength(Location, TotalWidth, DesignOffset);
                StartStation = Anchor == RampEndAnchor.End ? drawnLength - res.R : 0.0;

                // Show the solved value back in its input box, like the app does.
                string solvedText = solve == RampSolveTarget.FloorHeight ? res.H.ToString("F4", CultureInfo.InvariantCulture)
                                  : solve == RampSolveTarget.Slope ? res.S.ToString("F4", CultureInfo.InvariantCulture)
                                  : res.R.ToString("F4", CultureInfo.InvariantCulture);
                (solve == RampSolveTarget.FloorHeight ? _hBox : solve == RampSolveTarget.Slope ? _sBox : _rBox).Text = solvedText;

                UpdateResults(res);
                bool compliant = UpdateCompliance(res, regs);
                _okButton.IsEnabled = compliant;
                if (!compliant)
                    _error.Text = "The design violates Table B.9 — adjust the inputs or redraw the path.";
            }
            catch (RampCalcException ex)
            {
                _error.Text = ex.Message;
                ClearOutputs();
            }
            catch (Exception ex)
            {
                _error.Text = "Unexpected error: " + ex.Message;
                ClearOutputs();
            }
        }

        private void UpdateResults(RampCalcResult res)
        {
            double w = TotalWidth;
            SetResult("h", $"{res.H:F3} m");
            SetResult("S", $"{res.S:F3} %");
            SetResult("T", $"{res.T:F3} %");
            SetResult("X", $"{res.X:F3} m");
            SetResult("Y", $"{res.Y:F3} m");
            SetResult("Yp", $"{res.YPrime:F3} m");
            SetResult("Xp", $"{res.XPrime:F3} m");
            SetResult("R", $"{res.R:F3} m");
            SetResult("ratio", $"1 : {100.0 / res.S:F2}");
            SetResult("W", _variableWidth
                ? $"{_path.MinWidthAlongPath():F2}-{_path.MaxWidthAlongPath():F2} m (varies, from outline)"
                : $"{w:F2} m  ({Lanes} x {LaneWidth:F2} m)");

            if (_path.HasArc)
            {
                double? ri = _path.MinInnerRadius(Location, w);
                if (ri.HasValue)
                    SetResult("ri", $"{ri.Value:F2} m");
            }
            if (_singleArc)
            {
                double rc = _path.SingleArcCenterlineRadius(Location, w) ?? 0;
                double rd = _path.SingleArcDesignRadius(Location, w, DesignOffset) ?? rc;
                if (rc > 0 && rd > 0)
                {
                    // Sweep follows the design line (inner-lane centreline when multi-lane).
                    double theta = res.R / rd;
                    SetResult("rc", $"{rc:F2} m");
                    SetResult("sweep", $"{theta * 180.0 / Math.PI:F1} deg");
                    SetResult("Li", $"{(rc - w / 2.0) * theta:F3} m");
                    SetResult("Lo", $"{(rc + w / 2.0) * theta:F3} m");
                }
            }
        }

        private bool UpdateCompliance(RampCalcResult res, RampRegulations regs)
        {
            bool ok = true;

            bool slopeOk = res.S <= regs.MaxSlopePercent + 1e-9;
            SetCompliance("slope", slopeOk,
                slopeOk ? $"OK  {res.S:F2}% <= {regs.MaxSlopePercent:F0}%"
                        : $"FAIL  {res.S:F2}% > {regs.MaxSlopePercent:F0}%");
            ok &= slopeOk;

            bool widthOk = LaneWidth >= regs.MinLaneWidth - 1e-9;
            SetCompliance("width", widthOk,
                widthOk ? $"OK  {LaneWidth:F2} m >= {regs.MinLaneWidth:F1} m"
                        : $"FAIL  {LaneWidth:F2} m < {regs.MinLaneWidth:F1} m");
            ok &= widthOk;

            double? minInner = _path.MinInnerRadius(Location, TotalWidth);
            if (minInner.HasValue && regs.MinInnerRadius.HasValue)
            {
                bool radiusOk = minInner.Value >= regs.MinInnerRadius.Value - 1e-9;
                SetCompliance("radius", radiusOk,
                    radiusOk ? $"OK  {minInner.Value:F2} m >= {regs.MinInnerRadius.Value:F1} m"
                             : $"FAIL  {minInner.Value:F2} m < {regs.MinInnerRadius.Value:F1} m");
                ok &= radiusOk;
            }
            else
            {
                SetCompliance("radius", null, "N/A (straight path)");
            }

            double? rcSingle = _path.SingleArcDesignRadius(Location, TotalWidth, DesignOffset);
            if (rcSingle.HasValue)
            {
                double? gap = RampCalculator.MinLoopClearance(res, rcSingle.Value, SlabThickness);
                if (gap.HasValue)
                {
                    bool clearOk = gap.Value >= regs.MinClearance - 1e-9;
                    SetCompliance("clearance", clearOk,
                        clearOk ? $"OK  loop gap {gap.Value:F2} m >= {regs.MinClearance:F1} m"
                                : $"FAIL  loop gap {gap.Value:F2} m < {regs.MinClearance:F1} m");
                    ok &= clearOk;
                }
                else
                {
                    SetCompliance("clearance", null, "Single turn — verify 2.4 m headroom");
                }
            }
            else
            {
                SetCompliance("clearance", null, "Verify 2.4 m headroom above ramp");
            }

            double drawn = _path.CenterlineLength(Location, TotalWidth, DesignOffset);
            double diff = res.R - drawn; // + = the ramp is longer than what was drawn
            string movingEnd = Anchor == RampEndAnchor.Start ? "End" : "Start";
            if (Math.Abs(diff) <= 0.005)
            {
                SetCompliance("length", true, $"OK  drawn path = R = {res.R:F2} m");
            }
            else if (diff > 0)
            {
                SetCompliance("length", null,
                    $"{movingEnd} extended {diff:F2} m to reach R = {res.R:F2} m (drawn {drawn:F2} m)");
            }
            else
            {
                SetCompliance("length", null,
                    $"{movingEnd} pulled back {-diff:F2} m to R = {res.R:F2} m (drawn {drawn:F2} m)");
            }

            return ok;
        }

        private void ClearOutputs()
        {
            foreach (TextBlock label in _resultLabels.Values)
                label.Text = "-";
            foreach (TextBlock label in _complianceLabels.Values)
            {
                label.Text = "-";
                label.Foreground = Muted;
            }
        }

        private void SetResult(string key, string text)
        {
            if (_resultLabels.TryGetValue(key, out TextBlock? label))
            {
                label.Text = text;
                label.Foreground = Blue;
            }
        }

        private void SetCompliance(string key, bool? ok, string text)
        {
            if (_complianceLabels.TryGetValue(key, out TextBlock? label))
            {
                label.Text = text;
                label.Foreground = ok == true ? Green : ok == false ? Red : Muted;
            }
        }

        private static double? RequireValue(TextBox box, string name)
        {
            double? v = TryParse(box.Text);
            if (!v.HasValue)
                throw new RampCalcException($"Please enter a valid number for {name}.");
            return v;
        }

        private static double? TryParse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;
            text = text.Trim();
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out double v))
                return v;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                return v;
            return null;
        }

        private static TextBlock SectionHeader(string text) => ApgTheme.SectionHeader(text);

        private static TextBlock FieldLabel(string text) => ApgTheme.FieldLabel(text);

        private static TextBox NumberBox()
        {
            return new TextBox { Margin = new Thickness(0, 2, 0, 6), Padding = new Thickness(4, 3, 4, 3) };
        }

        private void AddResultRow(Panel parent, string key, string caption)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var value = new TextBlock { Text = "-", FontWeight = FontWeights.Bold, Foreground = Muted };
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            row.Children.Add(new TextBlock { Text = caption, Foreground = Muted });
            parent.Children.Add(row);
            _resultLabels[key] = value;
        }

        private void AddComplianceRow(Panel parent, string key, string caption)
        {
            var row = new DockPanel { Margin = new Thickness(0, 1, 0, 1) };
            var value = new TextBlock { Text = "-", FontWeight = FontWeights.Bold, Foreground = Muted, TextWrapping = TextWrapping.Wrap };
            DockPanel.SetDock(value, Dock.Right);
            row.Children.Add(value);
            row.Children.Add(new TextBlock { Text = caption, Foreground = Muted });
            parent.Children.Add(row);
            _complianceLabels[key] = value;
        }
    }
}
