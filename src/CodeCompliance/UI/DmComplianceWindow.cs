using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.UI;
using CodeCompliance.Core.Dm;
using CodeCompliance.Core.Mcp;
using CodeCompliance.Reporting;
// Autodesk.Revit.DB is not imported wholesale: it carries its own Grid, Binding, Color and
// Transform, which would collide with the WPF types this window is built from.
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;
using Document = Autodesk.Revit.DB.Document;
using Element = Autodesk.Revit.DB.Element;
using ElementId = Autodesk.Revit.DB.ElementId;
using Transaction = Autodesk.Revit.DB.Transaction;

namespace CodeCompliance.UI
{
    /// <summary>One row of the findings grid.</summary>
    public class DmFindingRow
    {
        public DmFindingRow(DmFinding finding)
        {
            Finding = finding;
        }

        public DmFinding Finding { get; }

        public string Severity => Finding.SeverityText;
        public string Phase => Finding.GroupText;
        public string Scope => Finding.Scope;
        public string Issue => Finding.Title;
        public string Modification => Finding.FixKindText;
        public string Parameter => Finding.ParameterName;
        public string Affected => Finding.CheckedCount > 0
            ? Finding.AffectedCount.ToString(CultureInfo.InvariantCulture) + " / " +
              Finding.CheckedCount.ToString(CultureInfo.InvariantCulture)
            : Finding.AffectedCount.ToString(CultureInfo.InvariantCulture);

        /// <summary>Whether "Fix this issue" can apply this finding without a person.</summary>
        public string AutoFix => DmFixService.CanFix(Finding) ? "Yes" : "—";
    }

    /// <summary>One row of the element list of the selected finding.</summary>
    public class DmElementRow
    {
        public DmElementRow(long id, string category, string name, string level)
        {
            Id = id;
            Category = category;
            ElementName = name;
            Level = level;
        }

        public long Id { get; }
        public string IdText => Id.ToString(CultureInfo.InvariantCulture);
        public string Category { get; }
        public string ElementName { get; }
        public string Level { get; }
    }

    /// <summary>
    /// The DM BIM Compliance dashboard: runs the audit over the open model, shows every issue
    /// with the elements it affects and the type of modification it needs, lets any single
    /// element be picked and framed in a 3D section box, and hands out the Revit MCP prompt
    /// that lets Claude do the fix.
    ///
    /// The window is <b>modeless</b>: Revit stays fully usable next to it, so an element can be
    /// highlighted, inspected and edited in Revit without ever closing the dashboard. Because
    /// of that, every call into Revit is queued on <see cref="DmRevitTask"/> and executed by
    /// Revit itself in a valid API context. The audit options, the filters and the window
    /// geometry are remembered in <see cref="DmUiSettings"/> and restored the next time.
    ///
    /// Code-only WPF (no XAML) like every APG dialog.
    /// </summary>
    public class DmComplianceWindow : Window
    {
        private const int MaxElementRows = 500;

        private readonly UIApplication _uiApp;
        private readonly DmRevitTask _task;
        private readonly DmUiSettings _settings;

        private readonly ObservableCollection<DmFindingRow> _rows = new ObservableCollection<DmFindingRow>();
        private readonly List<DmFindingRow> _allRows = new List<DmFindingRow>();
        private readonly ObservableCollection<DmElementRow> _elementRows = new ObservableCollection<DmElementRow>();

        private readonly DataGrid _grid;
        private readonly DataGrid _elementGrid;
        private readonly ComboBox _stage = new ComboBox { Width = 130 };
        private readonly ComboBox _severityFilter = new ComboBox { Width = 120 };
        private readonly ComboBox _groupFilter = new ComboBox { Width = 240 };
        private readonly ComboBox _modificationFilter = new ComboBox { Width = 160 };
        private readonly TextBox _search = new TextBox { Width = 180 };
        private readonly CheckBox _conditional = new CheckBox { Content = "Include conditional attributes" };
        private readonly CheckBox _naming = new CheckBox { Content = "Check object naming" };
        private readonly CheckBox _practices = new CheckBox { Content = "Check modelling practices" };
        private readonly CheckBox _highlightOnSelect = new CheckBox { Content = "Highlight in 3D on select" };
        private readonly CheckBox _selectInModel = new CheckBox { Content = "Select in model" };

        private readonly TextBlock _critical = Number("0", ApgTheme.Red);
        private readonly TextBlock _errors = Number("0", ApgTheme.Accent);
        private readonly TextBlock _warnings = Number("0", ApgTheme.Muted);
        private readonly TextBlock _elements = Number("0", ApgTheme.Navy);
        private readonly TextBlock _readiness = Number("–", ApgTheme.Green);

        private readonly TextBlock _detailTitle = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold };
        private readonly TextBlock _detailText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = ApgTheme.Muted };
        private readonly TextBlock _detailFix = new TextBlock { TextWrapping = TextWrapping.Wrap };
        private readonly TextBox _prompt = new TextBox
        {
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Height = 112,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.0
        };
        private readonly TextBlock _status = new TextBlock { Foreground = ApgTheme.Muted, VerticalAlignment = VerticalAlignment.Center };

        private readonly Button _fix = ApgTheme.PrimaryButton("Fix this issue");
        private readonly Button _mcp = ApgTheme.SecondaryButton("MCP server");

        private DmAuditResult? _result;
        private bool _restoring;
        private bool _auditRunning;
        private bool _fixRunning;

        public DmComplianceWindow(UIApplication uiApp, DmRevitTask task)
        {
            _uiApp = uiApp;
            _task = task;
            _settings = DmUiSettings.Load();

            Title = "DM BIM Compliance – APG Revit Plugins";
            MinWidth = 900;
            MinHeight = 620;
            Width = _settings.Width;
            Height = _settings.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
            ApgTheme.Apply(this);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // toolbar + tiles
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star) }); // findings
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star) }); // elements + detail
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // footer

            string model = uiApp.ActiveUIDocument?.Document?.Title ?? "(no model)";
            var header = ApgTheme.CreateHeader("DM BIM Compliance",
                model + "  ·  Dubai BIM Standard " + DmKnowledgeBase.StandardVersion +
                "  ·  Revit " + uiApp.Application.VersionNumber);
            Grid.SetRow((FrameworkElement)header, 0);
            root.Children.Add(header);

            var top = new StackPanel { Margin = new Thickness(16, 10, 16, 0) };
            Grid.SetRow(top, 1);
            root.Children.Add(top);
            top.Children.Add(BuildToolbar());
            top.Children.Add(BuildTiles());
            top.Children.Add(BuildFilters());

            _grid = BuildGrid();
            var gridCard = ApgTheme.Card(_grid);
            gridCard.Margin = new Thickness(16, 8, 16, 0);
            Grid.SetRow(gridCard, 2);
            root.Children.Add(gridCard);

            _elementGrid = BuildElementGrid();
            var bottom = BuildBottom();
            bottom.Margin = new Thickness(16, 8, 16, 0);
            Grid.SetRow(bottom, 3);
            root.Children.Add(bottom);

            var footer = BuildFooter();
            Grid.SetRow(footer, 4);
            root.Children.Add(footer);

            Content = root;

            RestoreSettings();
            SourceInitialized += (_, _) => AttachToRevit();
            Loaded += (_, _) =>
            {
                RestoreGeometry();
                UpdateMcpButton();
                if (_settings.RunOnOpen)
                    RunAudit();
                else
                    Say("Ready. Click \"Run audit\" to check the open model.");
            };
            Closing += (_, _) => StoreSettings();
        }

        /// <summary>
        /// Makes Revit the owner of the dashboard: the window floats over Revit, minimises with
        /// it and never disappears behind it, while Revit itself stays fully usable.
        /// </summary>
        private void AttachToRevit()
        {
            try
            {
                new WindowInteropHelper(this) { Owner = _uiApp.MainWindowHandle };
            }
            catch
            {
                // an owner-less window still works, it just does not follow Revit
            }
        }

        // ── layout ──────────────────────────────────────────────────────────────

        private UIElement BuildToolbar()
        {
            var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };

            panel.Children.Add(Label("Permit stage"));
            _stage.Items.Add("Final permit");
            _stage.Items.Add("Preliminary permit");
            _stage.SelectedIndex = 0;
            _stage.VerticalAlignment = VerticalAlignment.Center;
            _stage.Margin = new Thickness(0, 0, 14, 0);
            panel.Children.Add(_stage);

            _conditional.VerticalAlignment = VerticalAlignment.Center;
            _conditional.Margin = new Thickness(0, 0, 14, 0);
            panel.Children.Add(_conditional);

            _naming.VerticalAlignment = VerticalAlignment.Center;
            _naming.Margin = new Thickness(0, 0, 14, 0);
            panel.Children.Add(_naming);

            _practices.VerticalAlignment = VerticalAlignment.Center;
            _practices.Margin = new Thickness(0, 0, 14, 0);
            _practices.ToolTip = "DM's recommended modelling practices: wall and column constraints, level " +
                                 "association, space coverage and height, finishes as IfcCovering, clashes with " +
                                 "the linked models. They read the model geometry and are the slowest phase.";
            panel.Children.Add(_practices);

            Button run = ApgTheme.PrimaryButton("Run audit");
            run.Margin = new Thickness(0, 0, 10, 0);
            run.Click += (_, _) => RunAudit();
            panel.Children.Add(run);

            panel.Children.Add(_status);
            return panel;
        }

        private UIElement BuildTiles()
        {
            var tiles = new UniformGrid { Rows = 1, Columns = 5, Margin = new Thickness(0, 0, 0, 6) };
            tiles.Children.Add(Tile(_critical, "Critical"));
            tiles.Children.Add(Tile(_errors, "Errors"));
            tiles.Children.Add(Tile(_warnings, "Warnings"));
            tiles.Children.Add(Tile(_elements, "Elements to modify"));
            tiles.Children.Add(Tile(_readiness, "Submission readiness"));
            return tiles;
        }

        private static UIElement Tile(TextBlock number, string label)
        {
            var stack = new StackPanel();
            stack.Children.Add(number);
            stack.Children.Add(new TextBlock
            {
                Text = label.ToUpperInvariant(),
                FontSize = 10.0,
                Foreground = ApgTheme.Muted,
                Margin = new Thickness(0, 2, 0, 0)
            });
            var card = ApgTheme.Card(stack);
            card.Margin = new Thickness(0, 0, 8, 8);
            return card;
        }

        private static TextBlock Number(string text, Brush brush)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 22.0,
                FontWeight = FontWeights.SemiBold,
                Foreground = brush
            };
        }

        private UIElement BuildFilters()
        {
            var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };

            panel.Children.Add(Label("Severity"));
            _severityFilter.Items.Add("All");
            _severityFilter.Items.Add("Critical");
            _severityFilter.Items.Add("Error");
            _severityFilter.Items.Add("Warning");
            _severityFilter.SelectedIndex = 0;
            _severityFilter.Margin = new Thickness(0, 0, 14, 0);
            _severityFilter.SelectionChanged += (_, _) => ApplyFilters();
            panel.Children.Add(_severityFilter);

            panel.Children.Add(Label("Phase"));
            _groupFilter.Items.Add("All phases");
            foreach (DmCheckGroup group in Enum.GetValues(typeof(DmCheckGroup)).Cast<DmCheckGroup>())
                _groupFilter.Items.Add(DmFinding.GroupName(group));
            _groupFilter.SelectedIndex = 0;
            _groupFilter.Margin = new Thickness(0, 0, 14, 0);
            _groupFilter.SelectionChanged += (_, _) => ApplyFilters();
            panel.Children.Add(_groupFilter);

            panel.Children.Add(Label("Modification"));
            _modificationFilter.Items.Add("All modifications");
            foreach (DmFixKind kind in Enum.GetValues(typeof(DmFixKind)).Cast<DmFixKind>())
                _modificationFilter.Items.Add(new DmFinding { FixKind = kind }.FixKindText);
            _modificationFilter.SelectedIndex = 0;
            _modificationFilter.Margin = new Thickness(0, 0, 14, 0);
            _modificationFilter.SelectionChanged += (_, _) => ApplyFilters();
            panel.Children.Add(_modificationFilter);

            panel.Children.Add(Label("Search"));
            _search.VerticalAlignment = VerticalAlignment.Center;
            _search.Margin = new Thickness(0, 0, 14, 0);
            _search.TextChanged += (_, _) => ApplyFilters();
            panel.Children.Add(_search);

            Button clearFilters = ApgTheme.SecondaryButton("Clear filters");
            clearFilters.Margin = new Thickness(0, 0, 0, 0);
            clearFilters.Click += (_, _) => ClearFilters();
            panel.Children.Add(clearFilters);

            return panel;
        }

        private static TextBlock Label(string text)
        {
            return new TextBlock
            {
                Text = text,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Foreground = ApgTheme.Muted
            };
        }

        private DataGrid EmptyGrid()
        {
            return new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                IsReadOnly = true,
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
        }

        private DataGrid BuildGrid()
        {
            DataGrid grid = EmptyGrid();
            grid.SelectionMode = DataGridSelectionMode.Single;
            grid.ItemsSource = _rows;
            grid.MinHeight = 160;

            grid.Columns.Add(Column("Severity", nameof(DmFindingRow.Severity), 78));
            grid.Columns.Add(Column("Phase", nameof(DmFindingRow.Phase), 210));
            grid.Columns.Add(Column("Scope", nameof(DmFindingRow.Scope), 130));
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Issue",
                Binding = new Binding(nameof(DmFindingRow.Issue)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            grid.Columns.Add(Column("Type of modification", nameof(DmFindingRow.Modification), 140));
            grid.Columns.Add(Column("Attribute", nameof(DmFindingRow.Parameter), 140));
            grid.Columns.Add(Column("Affected", nameof(DmFindingRow.Affected), 90));
            grid.Columns.Add(Column("Auto-fix", nameof(DmFindingRow.AutoFix), 70));

            grid.SelectionChanged += (_, _) => ShowDetail();
            grid.LoadingRow += (_, e) =>
            {
                if (e.Row.Item is DmFindingRow row)
                {
                    switch (row.Finding.Severity)
                    {
                        case DmSeverity.Critical:
                            e.Row.Foreground = ApgTheme.Red;
                            break;
                        case DmSeverity.Error:
                            e.Row.Foreground = ApgTheme.Accent;
                            break;
                        default:
                            e.Row.Foreground = ApgTheme.Text;
                            break;
                    }
                }
            };
            return grid;
        }

        /// <summary>
        /// The elements of the selected finding, one per row: picking one selects it in Revit
        /// and — with "Highlight in 3D on select" ticked — frames that single element in the
        /// compliance view, while the dashboard and Revit both stay open.
        /// </summary>
        private DataGrid BuildElementGrid()
        {
            DataGrid grid = EmptyGrid();
            grid.SelectionMode = DataGridSelectionMode.Extended;
            grid.ItemsSource = _elementRows;
            grid.MinHeight = 120;

            grid.Columns.Add(Column("Element id", nameof(DmElementRow.IdText), 90));
            grid.Columns.Add(Column("Category", nameof(DmElementRow.Category), 120));
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Name / type",
                Binding = new Binding(nameof(DmElementRow.ElementName)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            grid.Columns.Add(Column("Level", nameof(DmElementRow.Level), 110));

            grid.SelectionChanged += (_, _) => ElementSelected();
            grid.MouseDoubleClick += (_, _) => HighlightSelectedElements();
            return grid;
        }

        private static DataGridTextColumn Column(string header, string path, double width)
        {
            return new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding(path),
                Width = new DataGridLength(width)
            };
        }

        private FrameworkElement BuildBottom()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(430) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var left = new DockPanel();
            var elementsHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            DockPanel.SetDock(elementsHeader, Dock.Top);
            elementsHeader.Children.Add(new TextBlock
            {
                Text = "ELEMENTS OF THIS FINDING",
                FontSize = 10.0,
                FontWeight = FontWeights.Bold,
                Foreground = ApgTheme.Navy,
                VerticalAlignment = VerticalAlignment.Center
            });
            left.Children.Add(elementsHeader);

            var elementButtons = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            DockPanel.SetDock(elementButtons, Dock.Bottom);

            Button zoom = ApgTheme.PrimaryButton("Highlight selected element");
            zoom.Margin = new Thickness(0, 0, 8, 0);
            zoom.ToolTip = "Frames the picked element in the 3D compliance view. Revit stays usable and the " +
                           "dashboard stays open.";
            zoom.Click += (_, _) => HighlightSelectedElements();
            elementButtons.Children.Add(zoom);

            Button selectOne = ApgTheme.SecondaryButton("Select in Revit");
            selectOne.Margin = new Thickness(0, 0, 8, 0);
            selectOne.Click += (_, _) => SelectElements(SelectedElementIds());
            elementButtons.Children.Add(selectOne);

            _highlightOnSelect.VerticalAlignment = VerticalAlignment.Center;
            _highlightOnSelect.Margin = new Thickness(0, 0, 10, 0);
            elementButtons.Children.Add(_highlightOnSelect);

            _selectInModel.VerticalAlignment = VerticalAlignment.Center;
            elementButtons.Children.Add(_selectInModel);

            left.Children.Add(elementButtons);
            left.Children.Add(_elementGrid);

            Border leftCard = ApgTheme.Card(left);
            leftCard.Margin = new Thickness(0, 0, 8, 0);
            Grid.SetColumn(leftCard, 0);
            grid.Children.Add(leftCard);

            FrameworkElement detail = BuildDetail();
            Grid.SetColumn(detail, 1);
            grid.Children.Add(detail);

            return grid;
        }

        private FrameworkElement BuildDetail()
        {
            var stack = new StackPanel();
            stack.Children.Add(_detailTitle);
            _detailText.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(_detailText);
            _detailFix.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(_detailFix);

            stack.Children.Add(new TextBlock
            {
                Text = "SUGGESTED PROMPT FOR CLAUDE (REVIT MCP)",
                FontSize = 10.0,
                FontWeight = FontWeights.Bold,
                Foreground = ApgTheme.Navy,
                Margin = new Thickness(0, 10, 0, 4)
            });
            stack.Children.Add(_prompt);

            var buttons = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };

            _fix.Margin = new Thickness(0, 0, 8, 0);
            _fix.ToolTip = "Apply this finding's fix to the open model now — no Claude, no MCP link needed. " +
                           "One transaction, so Ctrl+Z reverts it. Values that cannot be derived from the model " +
                           "are left alone rather than guessed.";
            _fix.Click += (_, _) => ApplyFix();
            buttons.Children.Add(_fix);

            Button select = ApgTheme.SecondaryButton("Select all in model");
            select.Margin = new Thickness(0, 0, 8, 0);
            select.Click += (_, _) => SelectElements(Selected?.ElementIds.ToList() ?? new List<long>());
            buttons.Children.Add(select);

            Button highlight = ApgTheme.PrimaryButton("Highlight in 3D section box");
            highlight.Margin = new Thickness(0, 0, 8, 0);
            highlight.Click += (_, _) => Highlight(Selected?.ElementIds.ToList() ?? new List<long>());
            buttons.Children.Add(highlight);

            Button copy = ApgTheme.SecondaryButton("Copy prompt");
            copy.Margin = new Thickness(0, 0, 8, 0);
            copy.Click += (_, _) => CopyPrompt();
            buttons.Children.Add(copy);

            Button copyScript = ApgTheme.SecondaryButton("Copy script only");
            copyScript.Margin = new Thickness(0, 0, 8, 0);
            copyScript.ToolTip = "The C# the prompt asks Claude to send with send_code_to_revit.";
            copyScript.Click += (_, _) => CopyScript();
            buttons.Children.Add(copyScript);

            Button copyAll = ApgTheme.SecondaryButton("Copy fix-all prompt");
            copyAll.Margin = new Thickness(0, 0, 8, 0);
            copyAll.Click += (_, _) => CopyAllPrompts();
            buttons.Children.Add(copyAll);

            stack.Children.Add(buttons);

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Content = stack
            };
            return ApgTheme.Card(scroll);
        }

        private UIElement BuildFooter()
        {
            var panel = new DockPanel { Margin = new Thickness(16, 8, 16, 12) };

            Button close = ApgTheme.PrimaryButton("Close");
            close.IsCancel = true;
            close.Click += (_, _) => Close();
            DockPanel.SetDock(close, Dock.Right);
            panel.Children.Add(close);

            var left = new WrapPanel();

            Button bind = ApgTheme.SecondaryButton("Bind DM parameters");
            bind.Margin = new Thickness(0, 0, 8, 0);
            bind.ToolTip = "Create the Dubai Municipality shared parameters from the data shipped with the " +
                           "plugin and bind them to the categories that need them. No DM file needed.";
            bind.Click += (_, _) => BindParameters();
            left.Children.Add(bind);

            Button report = ApgTheme.SecondaryButton("Export report");
            report.Margin = new Thickness(0, 0, 8, 0);
            report.Click += (_, _) => ExportReport();
            left.Children.Add(report);

            Button data = ApgTheme.SecondaryButton("DM rule data");
            data.Margin = new Thickness(0, 0, 8, 0);
            data.ToolTip = "Write the Dubai Municipality rule files — the IDS rules, the Appendix B and C " +
                           "tables and the recommended modelling practices — next to the reports so they can " +
                           "be updated when DM revises the standard.";
            data.Click += (_, _) => ExportRuleData();
            left.Children.Add(data);

            Button clear = ApgTheme.SecondaryButton("Clear highlight");
            clear.Margin = new Thickness(0, 0, 8, 0);
            clear.Click += (_, _) => ClearHighlight();
            left.Children.Add(clear);

            _mcp.Margin = new Thickness(0, 0, 8, 0);
            _mcp.ToolTip = "Switch the Revit MCP server on without leaving the dashboard, so Claude can pick up " +
                           "the prompt of a finding this tool cannot fix on its own.";
            _mcp.Click += (_, _) => StartMcpServer();
            left.Children.Add(_mcp);

            panel.Children.Add(left);
            return panel;
        }

        // ── settings ────────────────────────────────────────────────────────────

        /// <summary>Puts the options and filters of the last session back into the controls.</summary>
        private void RestoreSettings()
        {
            _restoring = true;
            try
            {
                _stage.SelectedIndex = _settings.StageIndex == 1 ? 1 : 0;
                _conditional.IsChecked = _settings.IncludeConditional;
                _naming.IsChecked = _settings.CheckObjectNaming;
                _practices.IsChecked = _settings.CheckModellingPractices;
                _highlightOnSelect.IsChecked = _settings.HighlightOnSelect;
                _selectInModel.IsChecked = _settings.SelectInModel;

                Select(_severityFilter, _settings.SeverityFilter, "All");
                Select(_groupFilter, _settings.PhaseFilter, "All phases");
                Select(_modificationFilter, _settings.ModificationFilter, "All modifications");
                _search.Text = _settings.Search ?? "";
            }
            finally
            {
                _restoring = false;
            }
        }

        private void RestoreGeometry()
        {
            try
            {
                if (!double.IsNaN(_settings.Left) && !double.IsNaN(_settings.Top) &&
                    _settings.Left > -5000 && _settings.Top > -5000 &&
                    _settings.Left < SystemParameters.VirtualScreenWidth &&
                    _settings.Top < SystemParameters.VirtualScreenHeight)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = _settings.Left;
                    Top = _settings.Top;
                }
                if (_settings.Maximized)
                    WindowState = WindowState.Maximized;
            }
            catch
            {
                // a screen that no longer exists just means the default position
            }
        }

        /// <summary>Remembers the options, the filters and the window geometry for next time.</summary>
        private void StoreSettings()
        {
            _settings.StageIndex = _stage.SelectedIndex;
            _settings.IncludeConditional = _conditional.IsChecked == true;
            _settings.CheckObjectNaming = _naming.IsChecked == true;
            _settings.CheckModellingPractices = _practices.IsChecked == true;
            _settings.HighlightOnSelect = _highlightOnSelect.IsChecked == true;
            _settings.SelectInModel = _selectInModel.IsChecked == true;
            _settings.SeverityFilter = _severityFilter.SelectedItem as string ?? "All";
            _settings.PhaseFilter = _groupFilter.SelectedItem as string ?? "All phases";
            _settings.ModificationFilter = _modificationFilter.SelectedItem as string ?? "All modifications";
            _settings.Search = _search.Text ?? "";
            _settings.Maximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal)
            {
                _settings.Left = Left;
                _settings.Top = Top;
                _settings.Width = Width;
                _settings.Height = Height;
            }
            _settings.Save();
        }

        private static void Select(ComboBox box, string value, string fallback)
        {
            int index = box.Items.IndexOf(value ?? "");
            box.SelectedItem = index >= 0 ? value : fallback;
        }

        private void ClearFilters()
        {
            _restoring = true;
            _severityFilter.SelectedIndex = 0;
            _groupFilter.SelectedIndex = 0;
            _modificationFilter.SelectedIndex = 0;
            _search.Text = "";
            _restoring = false;
            ApplyFilters();
        }

        // ── audit ───────────────────────────────────────────────────────────────

        private void RunAudit()
        {
            if (_auditRunning)
            {
                Say("The audit is still running…");
                return;
            }

            var options = new DmAuditOptions
            {
                Stage = _stage.SelectedIndex == 1 ? DmPermitStage.Preliminary : DmPermitStage.Final,
                IncludeConditional = _conditional.IsChecked == true,
                CheckObjectNaming = _naming.IsChecked == true,
                CheckModellingPractices = _practices.IsChecked == true
            };
            StoreSettings();

            _auditRunning = true;
            Cursor = Cursors.Wait;
            Say("Running the audit over the open model…");

            _task.Run(app =>
            {
                DmAuditResult? result = null;
                string message;
                try
                {
                    UIDocument? uiDoc = app.ActiveUIDocument;
                    if (uiDoc?.Document == null)
                    {
                        message = "Open a Revit model first.";
                    }
                    else
                    {
                        result = DmAuditService.Run(uiDoc.Document, app.Application.VersionNumber, options);
                        message = "";
                    }
                }
                catch (Exception ex)
                {
                    message = "The audit failed: " + ex.Message;
                }

                Dispatcher.Invoke(() =>
                {
                    _auditRunning = false;
                    Cursor = Cursors.Arrow;
                    if (result == null)
                    {
                        Say(message);
                        return;
                    }
                    ShowResult(result);
                });
            });
        }

        private void ShowResult(DmAuditResult result)
        {
            _result = result;

            _allRows.Clear();
            foreach (DmFinding finding in result.Findings
                         .OrderBy(f => (int)f.Severity)
                         .ThenBy(f => (int)f.Group)
                         .ThenByDescending(f => f.AffectedCount))
            {
                _allRows.Add(new DmFindingRow(finding));
            }

            _critical.Text = result.Count(DmSeverity.Critical).ToString(CultureInfo.InvariantCulture);
            _errors.Text = result.Count(DmSeverity.Error).ToString(CultureInfo.InvariantCulture);
            _warnings.Text = result.Count(DmSeverity.Warning).ToString(CultureInfo.InvariantCulture);
            _elements.Text = result.AffectedElements.ToString(CultureInfo.InvariantCulture);
            _readiness.Text = result.ReadinessPercent.ToString(CultureInfo.InvariantCulture) + "%";
            _readiness.Foreground = result.ReadinessPercent >= 90 ? ApgTheme.Green :
                                    result.ReadinessPercent >= 60 ? ApgTheme.Accent : ApgTheme.Red;

            ApplyFilters();

            Say(_allRows.Count + " finding(s) across " + result.Checks.Count + " checks · rules from " +
                result.KnowledgeBaseSource + " · " + DmKnowledgeBase.IdsRules.Count + " IDS rules and " +
                DmKnowledgeBase.ModellingPractices.Count + " modelling practices loaded");
        }

        private void ApplyFilters()
        {
            if (_restoring)
                return;

            string severity = _severityFilter.SelectedItem as string ?? "All";
            string group = _groupFilter.SelectedItem as string ?? "All phases";
            string modification = _modificationFilter.SelectedItem as string ?? "All modifications";
            string search = _search.Text.Trim();

            _rows.Clear();
            foreach (DmFindingRow row in _allRows)
            {
                if (severity != "All" && !row.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (group != "All phases" && row.Phase != group)
                    continue;
                if (modification != "All modifications" && row.Modification != modification)
                    continue;
                if (search.Length > 0 &&
                    row.Issue.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                    row.Scope.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                    row.Parameter.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                    row.Finding.PracticeId.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                _rows.Add(row);
            }

            if (_rows.Count > 0)
                _grid.SelectedIndex = 0;
            else
                ShowDetail();
        }

        private DmFinding? Selected => (_grid.SelectedItem as DmFindingRow)?.Finding;

        private void ShowDetail()
        {
            DmFinding? finding = Selected;
            _elementRows.Clear();

            if (finding == null)
            {
                _detailTitle.Text = _allRows.Count == 0
                    ? "No findings — run the audit, or the model already satisfies every check that was run."
                    : "Select a finding to see the details, its elements and the prompt.";
                _detailText.Text = "";
                _detailFix.Text = "";
                _prompt.Text = "";
                _fix.IsEnabled = false;
                return;
            }

            _detailTitle.Text = "[" + finding.SeverityText + "] " + finding.Scope + " — " + finding.Title;
            _detailText.Text = finding.Detail +
                               (finding.Reference.Length > 0 ? "\nReference: " + finding.Reference : "");
            _detailFix.Text = finding.FixKindText + ": " + finding.FixAction +
                              (finding.SampleValue.Length > 0 ? "   (DM sample value: " + finding.SampleValue + ")" : "");
            _prompt.Text = finding.McpPrompt;

            bool fixable = DmFixService.CanFix(finding);
            _fix.IsEnabled = fixable && !_fixRunning;
            _fix.ToolTip = fixable
                ? DmFixService.Describe(finding) + "  Runs as one transaction — Ctrl+Z reverts it."
                : DmFixService.WhyNot(finding);

            LoadElements(finding);
        }

        /// <summary>
        /// Reads the category, name and level of the elements of a finding — in Revit's own
        /// context, because the dashboard has none — and fills the element list.
        /// </summary>
        private void LoadElements(DmFinding finding)
        {
            if (!finding.HasElements)
                return;

            List<long> ids = finding.ElementIds.Take(MaxElementRows).ToList();
            int total = finding.ElementIds.Count;

            _task.Run(app =>
            {
                var rows = new List<DmElementRow>();
                try
                {
                    Document? doc = app.ActiveUIDocument?.Document;
                    if (doc != null)
                    {
                        foreach (long raw in ids)
                        {
                            Element? element = doc.GetElement(new ElementId(raw));
                            if (element == null)
                                continue;
                            rows.Add(new DmElementRow(raw, element.Category?.Name ?? "",
                                                      SafeName(element), LevelName(doc, element)));
                        }
                    }
                }
                catch
                {
                    // a model closed meanwhile simply leaves the list empty
                }

                Dispatcher.Invoke(() =>
                {
                    // Only fill the list when the same finding is still selected.
                    if (Selected != finding)
                        return;
                    _elementRows.Clear();
                    foreach (DmElementRow row in rows)
                        _elementRows.Add(row);
                    if (total > rows.Count)
                        Say(rows.Count + " of " + total + " element(s) listed (the full list is in the CSV report).");
                });
            });
        }

        private static string SafeName(Element element)
        {
            try
            {
                return element.Name ?? "";
            }
            catch
            {
                return "";
            }
        }

        private static string LevelName(Document doc, Element element)
        {
            try
            {
                if (element.LevelId != ElementId.InvalidElementId)
                    return doc.GetElement(element.LevelId)?.Name ?? "";
            }
            catch
            {
                // elements without a level are shown without one
            }
            return "";
        }

        // ── element actions ─────────────────────────────────────────────────────

        private List<long> SelectedElementIds()
        {
            return _elementGrid.SelectedItems
                .OfType<DmElementRow>()
                .Select(r => r.Id)
                .ToList();
        }

        private void ElementSelected()
        {
            List<long> ids = SelectedElementIds();
            if (ids.Count == 0)
                return;
            if (_highlightOnSelect.IsChecked == true)
                Highlight(ids);
            else if (_selectInModel.IsChecked == true)
                SelectElements(ids);
        }

        private void HighlightSelectedElements()
        {
            List<long> ids = SelectedElementIds();
            if (ids.Count == 0)
            {
                Say("Pick one or more elements in the list first.");
                return;
            }
            Highlight(ids);
        }

        private void SelectElements(List<long> ids)
        {
            if (ids.Count == 0)
            {
                Say("This finding is not tied to specific elements.");
                return;
            }

            _task.Run(app =>
            {
                string message;
                try
                {
                    UIDocument? uiDoc = app.ActiveUIDocument;
                    if (uiDoc == null)
                    {
                        message = "No model is open.";
                    }
                    else
                    {
                        var elementIds = ids.Select(id => new ElementId(id)).ToList();
                        uiDoc.Selection.SetElementIds(elementIds);
                        try
                        {
                            uiDoc.ShowElements(elementIds);
                        }
                        catch
                        {
                            // an element that is not visible in the active view is still selected
                        }
                        message = elementIds.Count + " element(s) selected in the model. " +
                                  "Revit stays usable — the dashboard is still open.";
                    }
                }
                catch (Exception ex)
                {
                    message = "Could not select the elements: " + ex.Message;
                }
                Dispatcher.Invoke(() => Say(message));
            });
        }

        private void Highlight(List<long> ids)
        {
            if (ids.Count == 0)
            {
                Say("This finding is not tied to specific elements, so there is nothing to frame.");
                return;
            }

            _task.Run(app =>
            {
                string message;
                try
                {
                    UIDocument? uiDoc = app.ActiveUIDocument;
                    if (uiDoc == null)
                    {
                        message = "No model is open.";
                    }
                    else
                    {
                        DmHighlightResult highlight = DmHighlightService.Show(uiDoc, ids);
                        message = highlight.Message;
                    }
                }
                catch (Exception ex)
                {
                    message = "Could not create the section box view: " + ex.Message;
                }
                Dispatcher.Invoke(() => Say(message));
            });
        }

        private void ClearHighlight()
        {
            _task.Run(app =>
            {
                string message;
                try
                {
                    Document? doc = app.ActiveUIDocument?.Document;
                    if (doc == null)
                    {
                        message = "No model is open.";
                    }
                    else
                    {
                        DmHighlightService.Clear(doc);
                        message = "Highlight colours removed from \"" + DmHighlightService.ViewName + "\".";
                    }
                }
                catch (Exception ex)
                {
                    message = "Could not clear the highlight: " + ex.Message;
                }
                Dispatcher.Invoke(() => Say(message));
            });
        }

        // ── applying the fix ────────────────────────────────────────────────────

        /// <summary>
        /// Applies the selected finding's fix to the model directly — no Claude, no MCP link.
        /// The change is confirmed first, runs in a single named transaction and is followed by
        /// a fresh audit so the finding disappears (or shows what is left).
        /// </summary>
        private void ApplyFix()
        {
            DmFinding? finding = Selected;
            if (finding == null)
                return;
            if (_fixRunning)
            {
                Say("A fix is still running…");
                return;
            }
            if (!DmFixService.CanFix(finding))
            {
                Say(DmFixService.WhyNot(finding));
                MessageBox.Show(this,
                    DmFixService.WhyNot(finding) +
                    "\n\nUse \"Copy prompt\" and let Claude do it over the Revit MCP link instead.",
                    "This one is not applied automatically",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MessageBoxResult answer = MessageBox.Show(this,
                DmFixService.Describe(finding) +
                "\n\nThis writes to the open model. It runs as one transaction, so Ctrl+Z reverts it.\n\n" +
                "Apply it now?",
                "Fix: " + finding.Title,
                MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);
            if (answer != MessageBoxResult.OK)
                return;

            var stage = _stage.SelectedIndex == 1 ? DmPermitStage.Preliminary : DmPermitStage.Final;
            bool conditional = _conditional.IsChecked == true;

            _fixRunning = true;
            _fix.IsEnabled = false;
            Cursor = Cursors.Wait;
            Say("Applying the fix…");

            _task.Run(app =>
            {
                string message;
                bool changed = false;
                try
                {
                    Document? doc = app.ActiveUIDocument?.Document;
                    if (doc == null)
                    {
                        message = "Open a Revit model first.";
                    }
                    else
                    {
                        DmFixOutcome outcome = DmFixService.Apply(doc, finding, stage, conditional);
                        message = outcome.Message;
                        changed = outcome.ChangedAnything;
                    }
                }
                catch (Exception ex)
                {
                    message = "The fix failed: " + ex.Message;
                }

                Dispatcher.Invoke(() =>
                {
                    _fixRunning = false;
                    _fix.IsEnabled = true;
                    Cursor = Cursors.Arrow;
                    Say(message);
                    if (changed)
                        RunAudit();
                });
            });
        }

        // ── the Revit MCP server ────────────────────────────────────────────────

        /// <summary>
        /// Switches the Revit MCP server on from here, so a finding this tool does not fix on
        /// its own can be handed to Claude without going back to the ribbon.
        /// </summary>
        private void StartMcpServer()
        {
            if (McpSocketService.Instance.IsRunning)
            {
                Say("The MCP server is already running on port " + McpSocketService.Instance.Port +
                    " with " + McpSocketService.Instance.CommandCount + " command(s). Paste the prompt into Claude.");
                UpdateMcpButton();
                return;
            }

            Cursor = Cursors.Wait;
            Say("Starting the Revit MCP server…");

            _task.Run(app =>
            {
                string message;
                try
                {
                    // Start must run in a Revit API context: the command sets create ExternalEvents.
                    McpSettings settings = McpSettings.Load();
                    McpSocketService.Instance.Start(app, settings);
                    message = "MCP server ON, port " + McpSocketService.Instance.Port + ", " +
                              McpSocketService.Instance.CommandCount + " command(s)" +
                              (McpInstaller.IsCommandsInstalled
                                  ? ". Paste the prompt into Claude — the Revit tools are live."
                                  : ". The downloaded command sets are missing, so only the built-in commands are " +
                                    "available: run MCP Setup ▸ Install / Update.");
                }
                catch (SocketException ex)
                {
                    message = "Could not open the port: " + ex.Message +
                              " Another Revit session is probably already listening on it — switch it off there, " +
                              "or change the port in MCP Setup.";
                }
                catch (Exception ex)
                {
                    message = "Could not start the MCP server: " + ex.Message;
                }

                Dispatcher.Invoke(() =>
                {
                    Cursor = Cursors.Arrow;
                    Say(message);
                    UpdateMcpButton();
                });
            });
        }

        private void UpdateMcpButton()
        {
            bool running = McpSocketService.Instance.IsRunning;
            _mcp.Content = running
                ? "MCP server ON (port " + McpSocketService.Instance.Port + ")"
                : "Start MCP server";
            _mcp.IsEnabled = !running;
        }

        // ── prompts, parameters and reports ─────────────────────────────────────

        private void CopyPrompt()
        {
            DmFinding? finding = Selected;
            if (finding == null)
                return;
            Copy(finding.McpPrompt, "Prompt copied. Paste it into Claude with the Revit MCP server running.");
        }

        private void CopyScript()
        {
            DmFinding? finding = Selected;
            if (finding == null)
                return;
            if (finding.FixScript.Length == 0)
            {
                Say("This finding is not fixed by a script — the prompt explains what to do instead.");
                return;
            }
            Copy(finding.FixScript,
                 "Script copied. Send it to Revit with the revit-mcp tool send_code_to_revit.");
        }

        private void CopyAllPrompts()
        {
            if (_result == null)
                return;
            Copy(DmPromptBuilder.ForAudit(_result),
                 "Fix-all prompt copied (" + _result.Findings.Count + " findings). Paste it into Claude.");
        }

        /// <summary>
        /// Creates the DM shared parameters from the plugin's own data and binds them to the
        /// categories the audit needs them on, then re-runs the audit.
        /// </summary>
        private void BindParameters()
        {
            var stage = _stage.SelectedIndex == 1 ? DmPermitStage.Preliminary : DmPermitStage.Final;
            bool conditional = _conditional.IsChecked == true;

            Cursor = Cursors.Wait;
            Say("Binding the DM shared parameters…");

            _task.Run(app =>
            {
                string message;
                bool rerun = false;
                try
                {
                    Document? doc = app.ActiveUIDocument?.Document;
                    if (doc == null)
                    {
                        message = "Open a Revit model first.";
                    }
                    else
                    {
                        var bindings = DmSharedParameters.RequiredBindings(stage, conditional);
                        DmBindResult bind;
                        using (var transaction = new Transaction(doc, "DM compliance – bind DM shared parameters"))
                        {
                            transaction.Start();
                            bind = DmSharedParameters.Bind(doc, bindings);
                            transaction.Commit();
                        }
                        message = bind.Summary + " Parameter file: " + bind.FilePath;
                        rerun = true;
                    }
                }
                catch (Exception ex)
                {
                    message = "Could not bind the DM parameters: " + ex.Message;
                }

                Dispatcher.Invoke(() =>
                {
                    Cursor = Cursors.Arrow;
                    Say(message);
                    if (rerun)
                        RunAudit();
                });
            });
        }

        private void Copy(string text, string message)
        {
            try
            {
                Clipboard.SetText(text);
                Say(message);
            }
            catch (Exception ex)
            {
                Say("Could not copy to the clipboard: " + ex.Message);
            }
        }

        private void ExportReport()
        {
            if (_result == null)
            {
                Say("Run the audit first.");
                return;
            }
            try
            {
                (string html, string csv, string prompts) = DmComplianceReportWriter.Write(_result);
                Say("Report written: " + html);
                Open(html);
                _ = csv;
                _ = prompts;
            }
            catch (Exception ex)
            {
                Say("Could not write the report: " + ex.Message);
            }
        }

        private void ExportRuleData()
        {
            try
            {
                int written = DmKnowledgeBase.ExportEmbedded(out string folder);
                Say(written + " DM rule file(s) written to " + folder +
                    ". Edit them there when DM revises the standard; the plugin reads them first.");
                Open(folder);
            }
            catch (Exception ex)
            {
                Say("Could not export the rule data: " + ex.Message);
            }
        }

        private static void Open(string path)
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch
            {
                // opening Explorer is a convenience only
            }
        }

        private void Say(string message)
        {
            _status.Text = message;
        }
    }
}
