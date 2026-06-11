using System;
using System.Reflection;
using Autodesk.Revit.UI;

namespace CodeCompliance
{
    /// <summary>
    /// Entry point of the add-in. Revit calls <see cref="OnStartup"/> once when it launches
    /// and <see cref="OnShutdown"/> when it closes. Here we only build the ribbon UI;
    /// all real work happens in the commands under <c>CodeCompliance.Commands</c>.
    /// </summary>
    public class App : IExternalApplication
    {
        private const string TabName = "Code Compliance";
        private const string PanelName = "Fire Fighting";

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                CreateRibbon(application);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Code Compliance", "Failed to initialize the add-in:\n" + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            return Result.Succeeded;
        }

        private static void CreateRibbon(UIControlledApplication application)
        {
            // CreateRibbonTab throws if the tab already exists (e.g. another of our
            // modules created it first), so swallow that specific failure.
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
            }

            RibbonPanel panel = application.CreateRibbonPanel(TabName, PanelName);
            string assemblyPath = Assembly.GetExecutingAssembly().Location;

            panel.AddItem(new PushButtonData(
                "CodeCompliance_EscapeStairs",
                "Escape\nStairs",
                assemblyPath,
                "CodeCompliance.Commands.EscapeStairsCommand")
            {
                ToolTip = "Step 1: detect all stairs and mark which ones are escape stairs.",
                LongDescription =
                    "Lists every stair in the model and lets you tick the ones used for escape. " +
                    "The choice is saved to the CC_IsEscapeStair parameter on each stair."
            });

            panel.AddItem(new PushButtonData(
                "CodeCompliance_TravelPaths",
                "Travel\nPaths",
                assemblyPath,
                "CodeCompliance.Commands.TravelPathsCommand")
            {
                ToolTip = "Step 2: create travel distance lines from rooms to escape stairs.",
                LongDescription =
                    "For every room on the active floor plan, creates a Path of Travel from the " +
                    "most remote point of the room to the nearest escape stair. Paths route " +
                    "automatically around walls and through doors. Run in a floor plan view."
            });

            panel.AddItem(new PushButtonData(
                "CodeCompliance_EgressReport",
                "Egress\nReport",
                assemblyPath,
                "CodeCompliance.Commands.EgressReportCommand")
            {
                ToolTip = "Step 3: measure paths, check door fire ratings, build schedules and report.",
                LongDescription =
                    "Measures each travel path, detects the doors it passes through and their fire " +
                    "ratings, creates the egress schedules in the project, and exports an HTML + CSV report."
            });

            panel.AddSeparator();

            panel.AddItem(new PushButtonData(
                "CodeCompliance_FireFightingCheck",
                "Model\nCheck",
                assemblyPath,
                "CodeCompliance.Commands.FireFightingCheckCommand")
            {
                ToolTip = "Count fire-protection elements in the active model (installation test)."
            });

            panel.AddItem(new PushButtonData(
                "CodeCompliance_About",
                "About",
                assemblyPath,
                "CodeCompliance.Commands.AboutCommand")
            {
                ToolTip = "Information about the Code Compliance add-in."
            });
        }
    }
}
