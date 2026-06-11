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

            var checkButton = new PushButtonData(
                "CodeCompliance_FireFightingCheck",
                "Run FF\nCheck",
                assemblyPath,
                "CodeCompliance.Commands.FireFightingCheckCommand")
            {
                ToolTip = "Run a fire-fighting compliance check on the active model.",
                LongDescription =
                    "Scans the active model for fire-protection elements (sprinklers, pipes, " +
                    "fittings, accessories and equipment) and reports a summary. " +
                    "Detailed code-compliance rules will be added in upcoming versions."
            };
            panel.AddItem(checkButton);

            var aboutButton = new PushButtonData(
                "CodeCompliance_About",
                "About",
                assemblyPath,
                "CodeCompliance.Commands.AboutCommand")
            {
                ToolTip = "Information about the Code Compliance add-in."
            };
            panel.AddItem(aboutButton);
        }
    }
}
