using System;
using System.IO;
using Newtonsoft.Json;

namespace CodeCompliance.Core.Dm
{
    /// <summary>
    /// What the DM BIM Compliance dashboard remembers between sessions: the audit options,
    /// the filters that were last used, the working preferences and the window geometry, so
    /// re-opening the dashboard picks the work up exactly where it was left.
    ///
    /// Stored as JSON next to the other APG settings
    /// (<c>%LOCALAPPDATA%\APGRevitPlugins\DmCompliance\dm-ui-settings.json</c>). A missing or
    /// damaged file simply means the defaults — it never stops the dashboard from opening.
    /// </summary>
    public sealed class DmUiSettings
    {
        /// <summary>Folder the dashboard settings live in.</summary>
        public static string Folder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "APGRevitPlugins", "DmCompliance");

        public static string FilePath => Path.Combine(Folder, "dm-ui-settings.json");

        // ── audit options ───────────────────────────────────────────────────────

        /// <summary>0 = final permit, 1 = preliminary permit.</summary>
        [JsonProperty("stageIndex")]
        public int StageIndex { get; set; }

        [JsonProperty("includeConditional")]
        public bool IncludeConditional { get; set; }

        [JsonProperty("checkObjectNaming")]
        public bool CheckObjectNaming { get; set; } = true;

        [JsonProperty("checkModellingPractices")]
        public bool CheckModellingPractices { get; set; } = true;

        // ── filters ─────────────────────────────────────────────────────────────

        [JsonProperty("severityFilter")]
        public string SeverityFilter { get; set; } = "All";

        [JsonProperty("phaseFilter")]
        public string PhaseFilter { get; set; } = "All phases";

        [JsonProperty("modificationFilter")]
        public string ModificationFilter { get; set; } = "All modifications";

        [JsonProperty("search")]
        public string Search { get; set; } = "";

        // ── working preferences ─────────────────────────────────────────────────

        /// <summary>Frame the selected finding in the 3D view as soon as it is selected.</summary>
        [JsonProperty("highlightOnSelect")]
        public bool HighlightOnSelect { get; set; }

        /// <summary>Also select the elements in Revit when a finding or element is picked.</summary>
        [JsonProperty("selectInModel")]
        public bool SelectInModel { get; set; } = true;

        /// <summary>Run the audit automatically when the dashboard opens.</summary>
        [JsonProperty("runOnOpen")]
        public bool RunOnOpen { get; set; } = true;

        // ── window geometry ─────────────────────────────────────────────────────

        [JsonProperty("left")]
        public double Left { get; set; } = double.NaN;

        [JsonProperty("top")]
        public double Top { get; set; } = double.NaN;

        [JsonProperty("width")]
        public double Width { get; set; } = 1180;

        [JsonProperty("height")]
        public double Height { get; set; } = 860;

        [JsonProperty("maximized")]
        public bool Maximized { get; set; }

        public static DmUiSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var loaded = JsonConvert.DeserializeObject<DmUiSettings>(File.ReadAllText(FilePath));
                    if (loaded != null)
                    {
                        loaded.SeverityFilter ??= "All";
                        loaded.PhaseFilter ??= "All phases";
                        loaded.ModificationFilter ??= "All modifications";
                        loaded.Search ??= "";
                        if (loaded.Width < 700) loaded.Width = 1180;
                        if (loaded.Height < 500) loaded.Height = 860;
                        return loaded;
                    }
                }
            }
            catch
            {
                // a damaged settings file must never stop the dashboard from opening
            }
            return new DmUiSettings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(this, Formatting.Indented));
            }
            catch
            {
                // a read-only profile folder is not worth an error dialog
            }
        }
    }
}
