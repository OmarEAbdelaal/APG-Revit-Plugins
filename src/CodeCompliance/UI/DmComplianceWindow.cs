using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.Revit.UI;
using CodeCompliance.Core.Dm;
using CodeCompliance.Reporting;
using ComboBox = System.Windows.Controls.ComboBox;
using TextBox = System.Windows.Controls.TextBox;

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
    }

    /// <summary>
    /// The DM BIM Compliance dashboard: runs the audit over the open model, shows every issue
    /// with the elements it affects and the type of modification it needs, frames those
    /// elements in a 3D section box, and hands out the Revit MCP prompt that lets Claude do
    /// the fix. Code-only WPF (no XAML) like every APG dialog.
    /// </summary>
    public class DmComplianceWindow : Window
    {
        private readonly UIApplication _uiApp;
        private readonly ObservableCollection<DmFindingRow> _rows = new ObservableCollection<DmFindingRow>();
        private readonly List<DmFindingRow> _allRows = new List<DmFindingRow>();

        private readonly DataGrid _grid;
        private readonly ComboBox _stage = new ComboBox { Width = 130 };
        private readonly ComboBox _severityFilter = new ComboBox { Width = 130 };
        private readonly ComboBox _groupFilter = new ComboBox { Width = 250 };
        private readonly TextBox _search = new TextBox { Width = 190 };
        private readonly CheckBox _conditional = new CheckBox { Content = "Include conditional attributes" };
        private readonly CheckBox _naming = new CheckBox { Content = "Check object naming", IsChecked = true };

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
            Height = 118,
            FontFamily = new FontFamily("Consolas"),
            FontSize = 11.0
        };
        private readonly TextBlock _status = new TextBlock { Foreground = ApgTheme.Muted, VerticalAlignment = VerticalAlignment.Center };

        private DmAuditResult? _result;

        /// <summary>3D view the last highlight prepared, so the command can activate it on close.</summary>
        public Autodesk.Revit.DB.ElementId? HighlightViewId { get; private set; }

        /// <summary>Elements of the last highlight, zoomed to after the dialog closes.</summary>
        public List<Autodesk.Revit.DB.ElementId> HighlightElements { get; } = new List<Autodesk.Revit.DB.ElementId>();

        public DmComplianceWindow(UIApplication uiApp)
        {
            _uiApp = uiApp;

            Title = "DM BIM Compliance – APG Revit Plugins";
            Width = 1180;
            Height = 820;
            MinWidth = 900;
            MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ApgTheme.Apply(this);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // header
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // toolbar + tiles
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // grid
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });   // detail
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

            var detail = BuildDetail();
            detail.Margin = new Thickness(16, 8, 16, 0);
            Grid.SetRow(detail, 3);
            root.Children.Add(detail);

            var footer = BuildFooter();
            Grid.SetRow(footer, 4);
            root.Children.Add(footer);

            Content = root;

            Loaded += (_, _) => RunAudit();
        }

        // ── layout ──────────────────────────────────────────────────────────────

        private UIElement BuildToolbar()
        {
            var panel = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };

            panel.Children.Add(new TextBlock
            {
                Text = "Permit stage",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            });
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

            panel.Children.Add(Label("Search"));
            _search.VerticalAlignment = VerticalAlignment.Center;
            _search.TextChanged += (_, _) => ApplyFilters();
            panel.Children.Add(_search);

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

        private DataGrid BuildGrid()
        {
            var grid = new DataGrid
            {
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                IsReadOnly = true,
                SelectionMode = DataGridSelectionMode.Single,
                ItemsSource = _rows,
                BorderBrush = ApgTheme.CardBorder,
                BorderThickness = new Thickness(1),
                Background = ApgTheme.CardBackground,
                RowBackground = ApgTheme.CardBackground,
                AlternatingRowBackground = ApgTheme.WindowBackground,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = ApgTheme.CardBorder,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                RowHeight = 24,
                MinHeight = 180
            };

            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Severity",
                Binding = new Binding(nameof(DmFindingRow.Severity)),
                Width = new DataGridLength(78)
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Phase",
                Binding = new Binding(nameof(DmFindingRow.Phase)),
                Width = new DataGridLength(210)
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Scope",
                Binding = new Binding(nameof(DmFindingRow.Scope)),
                Width = new DataGridLength(130)
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Issue",
                Binding = new Binding(nameof(DmFindingRow.Issue)),
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Type of modification",
                Binding = new Binding(nameof(DmFindingRow.Modification)),
                Width = new DataGridLength(140)
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Attribute",
                Binding = new Binding(nameof(DmFindingRow.Parameter)),
                Width = new DataGridLength(150)
            });
            grid.Columns.Add(new DataGridTextColumn
            {
                Header = "Affected",
                Binding = new Binding(nameof(DmFindingRow.Affected)),
                Width = new DataGridLength(90)
            });

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

            Button select = ApgTheme.SecondaryButton("Select in model");
            select.Margin = new Thickness(0, 0, 8, 0);
            select.Click += (_, _) => SelectElements();
            buttons.Children.Add(select);

            Button highlight = ApgTheme.PrimaryButton("Highlight in 3D section box");
            highlight.Margin = new Thickness(0, 0, 8, 0);
            highlight.Click += (_, _) => Highlight();
            buttons.Children.Add(highlight);

            Button copy = ApgTheme.SecondaryButton("Copy prompt");
            copy.Margin = new Thickness(0, 0, 8, 0);
            copy.Click += (_, _) => CopyPrompt();
            buttons.Children.Add(copy);

            Button copyAll = ApgTheme.SecondaryButton("Copy fix-all prompt");
            copyAll.Margin = new Thickness(0, 0, 8, 0);
            copyAll.Click += (_, _) => CopyAllPrompts();
            buttons.Children.Add(copyAll);

            stack.Children.Add(buttons);

            Border card = ApgTheme.Card(stack);
            card.Margin = new Thickness(0, 0, 0, 0);
            return card;
        }

        private UIElement BuildFooter()
        {
            var panel = new DockPanel { Margin = new Thickness(16, 4, 16, 12) };

            Button close = ApgTheme.PrimaryButton("Close");
            close.IsCancel = true;
            close.Click += (_, _) => Close();
            DockPanel.SetDock(close, Dock.Right);
            panel.Children.Add(close);

            var left = new WrapPanel();

            Button report = ApgTheme.SecondaryButton("Export report");
            report.Margin = new Thickness(0, 0, 8, 0);
            report.Click += (_, _) => ExportReport();
            left.Children.Add(report);

            Button data = ApgTheme.SecondaryButton("DM rule data");
            data.Margin = new Thickness(0, 0, 8, 0);
            data.ToolTip = "Write the Dubai Municipality rule files next to the reports so they can be " +
                           "updated when DM revises the standard.";
            data.Click += (_, _) => ExportRuleData();
            left.Children.Add(data);

            Button clear = ApgTheme.SecondaryButton("Clear highlight");
            clear.Margin = new Thickness(0, 0, 8, 0);
            clear.Click += (_, _) => ClearHighlight();
            left.Children.Add(clear);

            panel.Children.Add(left);
            return panel;
        }

        // ── behaviour ───────────────────────────────────────────────────────────

        private void RunAudit()
        {
            UIDocument? uiDoc = _uiApp.ActiveUIDocument;
            if (uiDoc?.Document == null)
            {
                Say("Open a Revit model first.");
                return;
            }

            Cursor = Cursors.Wait;
            try
            {
                var options = new DmAuditOptions
                {
                    Stage = _stage.SelectedIndex == 1 ? DmPermitStage.Preliminary : DmPermitStage.Final,
                    IncludeConditional = _conditional.IsChecked == true,
                    CheckObjectNaming = _naming.IsChecked == true
                };

                _result = DmAuditService.Run(uiDoc.Document, _uiApp.Application.VersionNumber, options);

                _allRows.Clear();
                foreach (DmFinding finding in _result.Findings
                             .OrderBy(f => (int)f.Severity)
                             .ThenBy(f => (int)f.Group)
                             .ThenByDescending(f => f.AffectedCount))
                {
                    _allRows.Add(new DmFindingRow(finding));
                }

                _critical.Text = _result.Count(DmSeverity.Critical).ToString(CultureInfo.InvariantCulture);
                _errors.Text = _result.Count(DmSeverity.Error).ToString(CultureInfo.InvariantCulture);
                _warnings.Text = _result.Count(DmSeverity.Warning).ToString(CultureInfo.InvariantCulture);
                _elements.Text = _result.AffectedElements.ToString(CultureInfo.InvariantCulture);
                _readiness.Text = _result.ReadinessPercent.ToString(CultureInfo.InvariantCulture) + "%";
                _readiness.Foreground = _result.ReadinessPercent >= 90 ? ApgTheme.Green :
                                        _result.ReadinessPercent >= 60 ? ApgTheme.Accent : ApgTheme.Red;

                ApplyFilters();

                Say(_allRows.Count + " finding(s) across " + _result.Checks.Count + " checks · rules from " +
                    _result.KnowledgeBaseSource + " · " + DmKnowledgeBase.IdsRules.Count + " IDS rules loaded");
            }
            catch (Exception ex)
            {
                Say("The audit failed: " + ex.Message);
            }
            finally
            {
                Cursor = Cursors.Arrow;
            }
        }

        private void ApplyFilters()
        {
            string severity = _severityFilter.SelectedItem as string ?? "All";
            string group = _groupFilter.SelectedItem as string ?? "All phases";
            string search = _search.Text.Trim();

            _rows.Clear();
            foreach (DmFindingRow row in _allRows)
            {
                if (severity != "All" && !row.Severity.Equals(severity, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (group != "All phases" && row.Phase != group)
                    continue;
                if (search.Length > 0 &&
                    row.Issue.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                    row.Scope.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0 &&
                    row.Parameter.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
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
            if (finding == null)
            {
                _detailTitle.Text = _allRows.Count == 0
                    ? "No findings — run the audit, or the model already satisfies every check that was run."
                    : "Select a finding to see the details and the prompt.";
                _detailText.Text = "";
                _detailFix.Text = "";
                _prompt.Text = "";
                return;
            }

            _detailTitle.Text = "[" + finding.SeverityText + "] " + finding.Scope + " — " + finding.Title;
            _detailText.Text = finding.Detail +
                               (finding.Reference.Length > 0 ? "\nReference: " + finding.Reference : "") +
                               (finding.ElementLabels.Count > 0
                                   ? "\nExamples: " + string.Join("  ·  ", finding.ElementLabels.Take(4))
                                   : "");
            _detailFix.Text = finding.FixKindText + ": " + finding.FixAction +
                              (finding.SampleValue.Length > 0 ? "   (DM sample value: " + finding.SampleValue + ")" : "");
            _prompt.Text = finding.McpPrompt;
        }

        private void SelectElements()
        {
            DmFinding? finding = Selected;
            if (finding == null || !finding.HasElements)
            {
                Say("This finding is not tied to specific elements.");
                return;
            }
            UIDocument? uiDoc = _uiApp.ActiveUIDocument;
            if (uiDoc == null)
                return;

            var ids = finding.ElementIds
                .Select(id => new Autodesk.Revit.DB.ElementId(id))
                .ToList();
            try
            {
                uiDoc.Selection.SetElementIds(ids);
                Say(ids.Count + " element(s) selected in the model.");
            }
            catch (Exception ex)
            {
                Say("Could not select the elements: " + ex.Message);
            }
        }

        private void Highlight()
        {
            DmFinding? finding = Selected;
            if (finding == null || !finding.HasElements)
            {
                Say("This finding is not tied to specific elements, so there is nothing to frame.");
                return;
            }
            UIDocument? uiDoc = _uiApp.ActiveUIDocument;
            if (uiDoc == null)
                return;

            Cursor = Cursors.Wait;
            try
            {
                DmHighlightResult highlight = DmHighlightService.Show(uiDoc, finding.ElementIds);
                if (highlight.Success)
                {
                    HighlightViewId = highlight.ViewId;
                    HighlightElements.Clear();
                    HighlightElements.AddRange(highlight.Elements);
                }
                Say(highlight.Message);
            }
            catch (Exception ex)
            {
                Say("Could not create the section box view: " + ex.Message);
            }
            finally
            {
                Cursor = Cursors.Arrow;
            }
        }

        private void ClearHighlight()
        {
            UIDocument? uiDoc = _uiApp.ActiveUIDocument;
            if (uiDoc?.Document == null)
                return;
            try
            {
                DmHighlightService.Clear(uiDoc.Document);
                HighlightViewId = null;
                HighlightElements.Clear();
                Say("Highlight colours removed from \"" + DmHighlightService.ViewName + "\".");
            }
            catch (Exception ex)
            {
                Say("Could not clear the highlight: " + ex.Message);
            }
        }

        private void CopyPrompt()
        {
            DmFinding? finding = Selected;
            if (finding == null)
                return;
            Copy(finding.McpPrompt, "Prompt copied. Paste it into Claude with the Revit MCP server running.");
        }

        private void CopyAllPrompts()
        {
            if (_result == null)
                return;
            Copy(DmPromptBuilder.ForAudit(_result),
                 "Fix-all prompt copied (" + _result.Findings.Count + " findings). Paste it into Claude.");
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
