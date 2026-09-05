using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.ExtensibleStorage;

namespace CodeCompliance.Core.Annotation
{
    /// <summary>
    /// Remembers which elements each Magic Annotation run created, per view, so the
    /// next run can replace them instead of piling duplicates. The ids are stored in
    /// a DataStorage element (one per view) via extensible storage — invisible to the
    /// user and saved with the model.
    /// </summary>
    internal static class AnnotationRegistry
    {
        private static readonly Guid SchemaGuid = new Guid("7A4B95D1-3C6E-4F82-9B1D-A8E250C4F7B3");
        private const string FieldName = "CreatedElementIds";
        private const string StorageNamePrefix = "APG Magic Annotation ";

        /// <summary>Delete the elements recorded for this view; returns how many existed.</summary>
        public static int DeletePrevious(Document doc, View view)
        {
            DataStorage? storage = FindStorage(doc, view);
            if (storage == null)
                return 0;

            int removed = 0;
            foreach (long value in ReadIds(storage))
            {
                var id = new ElementId(value);
                if (doc.GetElement(id) != null)
                {
                    try
                    {
                        doc.Delete(id);
                        removed++;
                    }
                    catch
                    {
                        // already deleted as a dependent of an earlier id — fine
                    }
                }
            }
            WriteIds(storage, new List<long>());
            return removed;
        }

        /// <summary>Record the ids created by the run that just finished.</summary>
        public static void Save(Document doc, View view, IList<ElementId> created)
        {
            DataStorage storage = FindStorage(doc, view) ?? CreateStorage(doc, view);
            WriteIds(storage, created.Select(id => id.Value).ToList());
        }

        private static Schema GetSchema()
        {
            Schema? schema = Schema.Lookup(SchemaGuid);
            if (schema != null)
                return schema;

            var builder = new SchemaBuilder(SchemaGuid);
            builder.SetSchemaName("ApgMagicAnnotation");
            builder.SetReadAccessLevel(AccessLevel.Public);
            builder.SetWriteAccessLevel(AccessLevel.Public);
            builder.SetDocumentation("Element ids created by the APG Magic Annotation plugin, per view.");
            builder.AddSimpleField(FieldName, typeof(string));
            return builder.Finish();
        }

        private static DataStorage? FindStorage(Document doc, View view)
        {
            string name = StorageNamePrefix + view.Id.Value;
            return new FilteredElementCollector(doc)
                .OfClass(typeof(DataStorage))
                .Cast<DataStorage>()
                .FirstOrDefault(ds => ds.Name == name);
        }

        private static DataStorage CreateStorage(Document doc, View view)
        {
            DataStorage storage = DataStorage.Create(doc);
            storage.Name = StorageNamePrefix + view.Id.Value;
            return storage;
        }

        private static List<long> ReadIds(DataStorage storage)
        {
            var ids = new List<long>();
            Entity entity = storage.GetEntity(GetSchema());
            if (!entity.IsValid())
                return ids;
            string raw = entity.Get<string>(FieldName) ?? "";
            foreach (string part in raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                if (long.TryParse(part, out long value))
                    ids.Add(value);
            return ids;
        }

        private static void WriteIds(DataStorage storage, List<long> ids)
        {
            var entity = new Entity(GetSchema());
            entity.Set(FieldName, string.Join(",", ids));
            storage.SetEntity(entity);
        }
    }
}
