using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace CodeCompliance.Core
{
    /// <summary>
    /// Detects stairs in the model and manages the shared parameter that marks a stair
    /// as an escape (egress) stair. The parameter is a Yes/No instance parameter named
    /// <see cref="ParameterName"/> bound to the Stairs category, so the choice is stored
    /// in the model itself and survives sessions, schedules and tags.
    /// </summary>
    public static class EscapeStairService
    {
        public const string ParameterName = "CC_IsEscapeStair";
        private const string SharedParameterGroup = "CodeCompliance";

        /// <summary>All placed stairs in the model.</summary>
        public static IList<Stairs> CollectStairs(Document doc)
        {
            return new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Stairs)
                .WhereElementIsNotElementType()
                .OfType<Stairs>()
                .ToList();
        }

        public static bool IsEscapeStair(Element stair)
        {
            Parameter? p = stair.LookupParameter(ParameterName);
            return p != null && p.HasValue && p.AsInteger() == 1;
        }

        public static void SetEscapeStair(Element stair, bool isEscape)
        {
            stair.LookupParameter(ParameterName)?.Set(isEscape ? 1 : 0);
        }

        /// <summary>
        /// Binds the escape-stair parameter to the Stairs category if it is not bound yet.
        /// Must be called inside an open transaction.
        /// </summary>
        public static void EnsureParameter(Document doc)
        {
            DefinitionBindingMapIterator it = doc.ParameterBindings.ForwardIterator();
            while (it.MoveNext())
            {
                if (it.Key is Definition existing && existing.Name == ParameterName)
                    return;
            }

            Application app = doc.Application;
            string originalFile = app.SharedParametersFilename;
            string tempFile = Path.Combine(Path.GetTempPath(), "CodeCompliance_SharedParams.txt");
            if (!File.Exists(tempFile))
                File.WriteAllText(tempFile, string.Empty);

            try
            {
                app.SharedParametersFilename = tempFile;
                DefinitionFile defFile = app.OpenSharedParameterFile();
                DefinitionGroup group = defFile.Groups.get_Item(SharedParameterGroup)
                                        ?? defFile.Groups.Create(SharedParameterGroup);
                Definition def = group.Definitions.get_Item(ParameterName);
                if (def == null)
                {
                    var options = new ExternalDefinitionCreationOptions(ParameterName, SpecTypeId.Boolean.YesNo)
                    {
                        Description = "Marks this stair as an escape (egress) stair for fire-fighting code compliance checks."
                    };
                    def = group.Definitions.Create(options);
                }

                CategorySet categories = app.Create.NewCategorySet();
                categories.Insert(doc.Settings.Categories.get_Item(BuiltInCategory.OST_Stairs));
                InstanceBinding binding = app.Create.NewInstanceBinding(categories);
                doc.ParameterBindings.Insert(def, binding, GroupTypeId.IdentityData);
            }
            finally
            {
                if (!string.IsNullOrEmpty(originalFile))
                    app.SharedParametersFilename = originalFile;
            }
        }
    }
}
