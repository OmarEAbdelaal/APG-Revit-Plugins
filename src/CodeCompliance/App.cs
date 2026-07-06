using System;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using CodeCompliance.Core;

namespace CodeCompliance
{
    /// <summary>
    /// Entry point of the APG Revit Plugins suite. Revit calls <see cref="OnStartup"/> once
    /// when it launches and <see cref="OnShutdown"/> when it closes. Here we only build the
    /// ribbon UI; all real work happens in the commands under <c>CodeCompliance.Commands</c>.
    ///
    /// The suite owns one ribbon tab ("APG Revit Plugins") with one panel per plugin.
    /// Future APG plugins add their own panel in <see cref="CreateRibbon"/> (or create it
    /// from their own module — CreateRibbonTab tolerates the tab already existing).
    /// </summary>
    public class App : IExternalApplication
    {
        public const string TabName = "APG Revit Plugins";
        private const string FireFightingPanelName = "Code Compliance – Fire Fighting";
        private const string RampPanelName = "Ramp Creator";
        private const string SuitePanelName = "APG";

        private UIControlledApplication? _application;
        private volatile UpdateInfo? _pendingUpdate;
        private volatile bool _updateCheckDone;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                CreateRibbon(application);
                StartUpdateCheck(application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("APG Revit Plugins", "Failed to initialize the add-in:\n" + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        /// <summary>
        /// Checks GitHub for a newer suite release in the background and, when one
        /// is found, notifies once per version on Revit's UI thread (Idling event).
        /// Never blocks startup; any failure (offline, rate limit) is silent.
        /// </summary>
        private void StartUpdateCheck(UIControlledApplication application)
        {
            _application = application;
            application.Idling += OnIdlingShowUpdate;
            Task.Run(async () =>
            {
                try
                {
                    UpdateInfo? info = await UpdateChecker.CheckAsync().ConfigureAwait(false);
                    if (info != null && info.IsNewer && !UpdateChecker.WasNotified(info.Latest))
                        _pendingUpdate = info;
                }
                catch
                {
                    // never disturb Revit because of an update check
                }
                finally
                {
                    _updateCheckDone = true;
                }
            });
        }

        private void OnIdlingShowUpdate(object? sender, IdlingEventArgs e)
        {
            if (!_updateCheckDone)
                return;

            _application!.Idling -= OnIdlingShowUpdate;
            UpdateInfo? info = _pendingUpdate;
            _pendingUpdate = null;
            if (info == null)
                return;

            try
            {
                UpdateChecker.MarkNotified(info.Latest);
                new UI.UpdateWindow(info).ShowDialog();
            }
            catch
            {
                // notification is best effort only
            }
        }

        private static void CreateRibbon(UIControlledApplication application)
        {
            // CreateRibbonTab throws if the tab already exists (e.g. another APG module
            // created it first), so swallow that specific failure.
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
            }

            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            // ── Plugin 1: Code Compliance – Fire Fighting ──────────────────────
            RibbonPanel firePanel = application.CreateRibbonPanel(TabName, FireFightingPanelName);

            firePanel.AddItem(Button(
                "CodeCompliance_EscapeStairs", "Escape\nStairs",
                assemblyPath, "CodeCompliance.Commands.EscapeStairsCommand", "EscapeStairs",
                "Step 1: detect all stairs and mark which ones are escape stairs.",
                "Lists every stair in the model and lets you tick the ones used for escape. " +
                "The choice is saved to the CC_IsEscapeStair parameter on each stair."));

            firePanel.AddItem(Button(
                "CodeCompliance_TravelPaths", "Travel\nPaths",
                assemblyPath, "CodeCompliance.Commands.TravelPathsCommand", "TravelPaths",
                "Step 2: create travel distance lines from rooms to escape stairs.",
                "For every room on the active floor plan, creates a Path of Travel from the " +
                "most remote point of the room to the nearest escape stair. Paths route " +
                "automatically around walls and through doors. Run in a floor plan view."));

            firePanel.AddItem(Button(
                "CodeCompliance_EgressReport", "Egress\nReport",
                assemblyPath, "CodeCompliance.Commands.EgressReportCommand", "EgressReport",
                "Step 3: measure paths, check door fire ratings, build schedules and report.",
                "Measures each travel path, detects the doors it passes through and their fire " +
                "ratings, creates the egress schedules in the project, and exports an HTML + CSV report."));

            firePanel.AddSeparator();

            firePanel.AddItem(Button(
                "CodeCompliance_FireFightingCheck", "Model\nCheck",
                assemblyPath, "CodeCompliance.Commands.FireFightingCheckCommand", "ModelCheck",
                "Count fire-protection elements in the active model (installation test).",
                null));

            // ── Plugin 2: Ramp Creator ──────────────────────────────────────────
            RibbonPanel rampPanel = application.CreateRibbonPanel(TabName, RampPanelName);

            rampPanel.AddItem(Button(
                "CodeCompliance_ParkingRamp", "Parking\nRamp",
                assemblyPath, "CodeCompliance.Commands.ParkingRampCommand", "ParkingRamp",
                "Create a code-compliant parking ramp from a drawn model line.",
                "Draw a model line (straight ramp) or model arc (curved/helical ramp) in a plan " +
                "view, in the direction of travel going up, then run this command and select it. " +
                "Choose whether the line is the left edge, right edge or centerline, enter two of " +
                "the three key parameters (floor height h, slope S, total run R) and the third is " +
                "solved per Dubai Building Code Annex B, Tables B.9 / B.10. Compliance is checked " +
                "at the input step; the ramp with its transition zones is created as Floor elements."));

            // ── Suite panel ─────────────────────────────────────────────────────
            RibbonPanel suitePanel = application.CreateRibbonPanel(TabName, SuitePanelName);

            suitePanel.AddItem(Button(
                "CodeCompliance_About", "About\nAPG",
                assemblyPath, "CodeCompliance.Commands.AboutCommand", "Apg",
                "About APG Revit Plugins: version, plugins included, author and contact.",
                null));
        }

        private static PushButtonData Button(
            string name, string text, string assemblyPath, string className,
            string icon, string toolTip, string? longDescription)
        {
            var data = new PushButtonData(name, text, assemblyPath, className)
            {
                ToolTip = toolTip,
                Image = RibbonIcons.Get(icon + "16"),
                LargeImage = RibbonIcons.Get(icon + "32")
            };
            if (longDescription != null)
                data.LongDescription = longDescription;
            return data;
        }
    }
}
