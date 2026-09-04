using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace CodeCompliance.Core.Dm
{
    /// <summary>
    /// Which Revit categories carry which IFC class, which Appendix B attribute table applies
    /// to them, and which discipline model DM expects them in. This is the bridge between the
    /// knowledge base (IFC vocabulary) and the Revit model (categories).
    /// </summary>
    public sealed class DmElementRule
    {
        public DmElementRule(string display, string table, string ifcEntity, string discipline,
                             params BuiltInCategory[] categories)
        {
            Display = display;
            Table = table;
            IfcEntity = ifcEntity;
            Discipline = discipline;
            Categories = categories;
        }

        /// <summary>Name shown in the dashboard, e.g. "Walls".</summary>
        public string Display { get; }

        /// <summary>Appendix B table name (file <c>attr_&lt;Table&gt;.csv</c>).</summary>
        public string Table { get; }

        /// <summary>IFC entity in DM's IDS rule file, e.g. "IFCWALL".</summary>
        public string IfcEntity { get; }

        /// <summary>"AR", "ST" or "AR/ST" — which submission model must contain it.</summary>
        public string Discipline { get; }

        public BuiltInCategory[] Categories { get; }
    }

    /// <summary>The element rules the audit runs, in dashboard order.</summary>
    public static class DmRuleCatalog
    {
        public static IReadOnlyList<DmElementRule> ElementRules { get; } = new List<DmElementRule>
        {
            new DmElementRule("Walls", "Wall", "IFCWALL", "AR/ST",
                BuiltInCategory.OST_Walls),
            new DmElementRule("Doors", "Door", "IFCDOOR", "AR",
                BuiltInCategory.OST_Doors),
            new DmElementRule("Windows", "Window", "IFCWINDOW", "AR",
                BuiltInCategory.OST_Windows),
            new DmElementRule("Floors / slabs", "Floor_Slabs", "IFCSLAB", "AR/ST",
                BuiltInCategory.OST_Floors),
            new DmElementRule("Roofs", "Roof", "IFCROOF", "AR",
                BuiltInCategory.OST_Roofs),
            new DmElementRule("Ceilings and finishes", "Covering_Finishes", "IFCCOVERING", "AR",
                BuiltInCategory.OST_Ceilings),
            new DmElementRule("Curtain walls", "Curtain_Wall", "IFCCURTAINWALL", "AR",
                BuiltInCategory.OST_CurtaSystem),
            new DmElementRule("Curtain panels", "Plate", "IFCPLATE", "AR",
                BuiltInCategory.OST_CurtainWallPanels),
            new DmElementRule("Curtain mullions", "Member", "IFCMEMBER", "AR",
                BuiltInCategory.OST_CurtainWallMullions),
            new DmElementRule("Columns", "Column", "IFCCOLUMN", "AR/ST",
                BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns),
            new DmElementRule("Beams / framing", "Beam", "IFCBEAM", "ST",
                BuiltInCategory.OST_StructuralFraming),
            new DmElementRule("Foundations", "Foundation", "IFCFOOTING", "ST",
                BuiltInCategory.OST_StructuralFoundation),
            new DmElementRule("Railings", "Railing", "IFCRAILING", "AR",
                BuiltInCategory.OST_StairsRailing),
            new DmElementRule("Stairs", "Stair", "IFCSTAIR", "AR",
                BuiltInCategory.OST_Stairs),
            new DmElementRule("Stair flights", "StairFlight", "IFCSTAIRFLIGHT", "AR",
                BuiltInCategory.OST_StairsRuns),
            new DmElementRule("Ramps", "Ramp", "IFCRAMP", "AR",
                BuiltInCategory.OST_Ramps),
            new DmElementRule("Furniture", "Furniture", "IFCFURNITURE", "AR",
                BuiltInCategory.OST_Furniture),
            new DmElementRule("Elevators / escalators", "Transport_Element", "IFCTRANSPORTELEMENT", "AR",
                BuiltInCategory.OST_SpecialityEquipment),
            new DmElementRule("Generic models (proxies)", "Building_Element_Proxy", "IFCBUILDINGELEMENTPROXY", "AR",
                BuiltInCategory.OST_GenericModel)
        };

        /// <summary>
        /// Revit categories whose IFC class is ambiguous in DM's export mapping: an element in
        /// one of these exports as a different IFC class depending on "Export to IFC As", so
        /// the mapping has to be set deliberately rather than left to the exporter default.
        /// </summary>
        public static IReadOnlyList<string> AmbiguousCategories { get; } = new List<string>
        {
            "Walls", "Generic Models", "Site", "Topography", "Parking",
            "Specialty Equipment", "Mass", "Structural Framing", "Structural Foundations"
        };

        /// <summary>Level naming: abbreviations DM uses in practice for the first field.</summary>
        public static IReadOnlyList<string> LevelPrefixes { get; } = new List<string>
        {
            "B", "RD", "GA", "GR", "P", "M", "F", "S", "RF", "L", "LG", "SS", "T", "U"
        };
    }
}
