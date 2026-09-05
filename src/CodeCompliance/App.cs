using System;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using CodeCompliance.Core;
using CodeCompliance.Core.Mcp;

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
        private const string DmPanelName = "DM BIM Compliance";
        private const string RampPanelName = "Ramp Creator";
        private const string AnnotationPanelName = "Magic Annotation";
        private const string McpPanelName = "Revit MCP";
        private const string SuitePanelName = "APG";

        private UIControlledApplication? _application;
        private volatile UpdateInfo? _pendingUpdate;
        private volatile bool _updateCheckDone;
        private volatile McpUpdateResult? _mcpUpdate;
        private volatile bool _mcpUpdateDone;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                CreateRibbon(application);
                StartUpdateCheck(application);
                StartMcpAutoStart(application);
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
            try
            {
                if (McpSocketService.Instance.IsRunning)
                    McpSocketService.Instance.Stop();
            }
            catch
            {
                // never block Revit shutdown
            }
            return Result.Succeeded;
        }

        /// <summary>
        /// Checks GitHub for a newer suite release and, when the Revit MCP connector is
        /// installed, for newer MCP server / command sets (installed silently). Results are
        /// shown once on Revit's UI thread (Idling event). Never blocks startup; failures
        /// (offline, rate limit) are silent.
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
            Task.Run(async () =>
            {
                try
                {
                    McpUpdateResult result = await McpInstaller.AutoUpdateAsync(McpSettings.Load()).ConfigureAwait(false);
                    if (result.Status == McpUpdateStatus.Updated)
                        _mcpUpdate = result;
                }
                catch
                {
                    // best effort only
                }
                finally
                {
                    _mcpUpdateDone = true;
                }
            });
        }

        private void OnIdlingShowUpdate(object? sender, IdlingEventArgs e)
        {
            if (!_updateCheckDone || !_mcpUpdateDone)
                return;

            _application!.Idling -= OnIdlingShowUpdate;

            UpdateInfo? info = _pendingUpdate;
            _pendingUpdate = null;
            if (info != null)
            {
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

            McpUpdateResult? mcp = _mcpUpdate;
            _mcpUpdate = null;
            if (mcp != null)
            {
                try
                {
                    TaskDialog.Show("Revit MCP updated",
                        mcp.Message + "\n\nRestart Claude Desktop so it loads the new MCP server. " +
                        "The new Revit commands are used the next time the MCP server is switched on.");
                }
                catch
                {
                    // best effort only
                }
            }
        }

        /// <summary>
        /// Starts the MCP socket service when Revit is fully initialized, if the user enabled
        /// auto-start in MCP Setup. ApplicationInitialized runs in a valid API context, which
        /// the command sets need to create their ExternalEvents.
        /// </summary>
        private static void StartMcpAutoStart(UIControlledApplication application)
        {
            application.ControlledApplication.ApplicationInitialized += (sender, args) =>
            {
                try
                {
                    McpSettings settings = McpSettings.Load();
                    if (!settings.AutoStart)
                        return;
                    if (!(sender is Autodesk.Revit.ApplicationServices.Application app))
                        return;
                    McpSocketService.Instance.Start(new UIApplication(app), settings);
                }
                catch (Exception ex)
                {
                    McpLog.Error("MCP auto-start failed", ex);
                }
            };
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

            // ── Plugin 2: DM BIM Compliance (Dubai Municipality e-submission) ──
            RibbonPanel dmPanel = application.CreateRibbonPanel(TabName, DmPanelName);

            dmPanel.AddItem(Button(
                "CodeCompliance_DmCompliance", "DM\nCompliance",
                assemblyPath, "CodeCompliance.Commands.DmComplianceCommand", "DmCompliance",
                "Audit the model against the Dubai Municipality BIM e-submission requirements.",
                "Checks the open model against the Dubai BIM Standard: project, site and building " +
                "attributes, level naming and the gate level, rooms, usage codes and unit data, the " +
                "Appendix B element attributes and DM's own IDS rule set, object naming, " +
                "geo-referencing and export readiness. The dashboard lists every element that has to " +
                "be modified and the type of modification, frames them in a 3D section box, and gives " +
                "you the prompt that lets Claude fix them over the Revit MCP connection."));

            dmPanel.AddItem(Button(
                "CodeCompliance_DmReport", "DM\nReport",
                assemblyPath, "CodeCompliance.Commands.DmReportCommand", "DmReport",
                "Run the DM compliance audit and export the report without opening the dashboard.",
                "Writes an HTML dashboard, a CSV of every finding with its element ids and a text " +
                "file with all Revit MCP fix prompts to Documents\\CodeCompliance."));

            // ── Plugin 3: Ramp Creator ──────────────────────────────────────────
            RibbonPanel rampPanel = application.CreateRibbonPanel(TabName, RampPanelName);

            rampPanel.AddItem(Button(
                "CodeCompliance_ParkingRamp", "Parking\nRamp",
                assemblyPath, "CodeCompliance.Commands.ParkingRampCommand", "ParkingRamp",
                "Create a code-compliant parking ramp from a drawn model line or outline.",
                "Select or draw the ramp path: one connected chain of model lines/arcs (as its " +
                "left edge, right edge or centerline), or the full outline (left edge, right " +
                "edge, start and end) for a ramp whose width varies along its run. Enter two of " +
                "the three key parameters (floor height h, slope S, total run R) and the third is " +
                "solved per Dubai Building Code Annex B, Tables B.9 / B.10. Compliance is checked " +
                "at the input step; the ramp with its transition zones is created as native Floor " +
                "elements shaped with slab-shape points."));

            // ── Plugin 4: Magic Annotation ──────────────────────────────────────
            RibbonPanel annotationPanel = application.CreateRibbonPanel(TabName, AnnotationPanelName);

            annotationPanel.AddItem(Button(
                "CodeCompliance_MagicAnnotation", "Magic\nAnnotation",
                assemblyPath, "CodeCompliance.Commands.MagicAnnotationCommand", "MagicAnnotation",
                "Annotate the active plan, section or elevation view in one step.",
                "Places the annotations you tick in a checklist: overall/grid/opening dimensions " +
                "(level dimensions in sections and elevations), room/door/window/wall tags, spot " +
                "elevations at stairs and ramps, ramp slope notes and stair path arrows — all " +
                "positioned to avoid clashing with existing annotations, in a single undo step. " +
                "Re-running the command replaces what it placed before, and stairs, ramps and wet " +
                "areas that deserve a callout are listed as suggestions."));

            // ── Plugin 5: Revit MCP (Claude ↔ Revit) ────────────────────────────
            RibbonPanel mcpPanel = application.CreateRibbonPanel(TabName, McpPanelName);

            mcpPanel.AddItem(Button(
                "CodeCompliance_McpServer", "MCP\nServer",
                assemblyPath, "CodeCompliance.Commands.McpServerCommand", "McpServer",
                "Switch the Revit MCP server on or off so Claude can read and drive this Revit session.",
                "Starts a local JSON-RPC service (port 8080 by default) that the Revit MCP server " +
                "launched by Claude Desktop connects to. While it is on, Claude can query the model, " +
                "create and modify elements, tag, color, export data and run C# code in Revit. " +
                "Click again to switch it off."));

            mcpPanel.AddItem(Button(
                "CodeCompliance_McpSetup", "MCP\nSetup",
                assemblyPath, "CodeCompliance.Commands.McpSetupCommand", "McpSetup",
                "Install or update the MCP server and Revit command sets from GitHub and configure Claude Desktop.",
                "One-stop setup: downloads the latest Revit MCP server (Node.js) and Revit command " +
                "sets from github.com/OmarEAbdelaal/revit-mcp, writes the Claude Desktop configuration, " +
                "shows Node.js and connection status and lets you choose which commands Claude may use. " +
                "Updates are installed automatically on Revit startup."));

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
