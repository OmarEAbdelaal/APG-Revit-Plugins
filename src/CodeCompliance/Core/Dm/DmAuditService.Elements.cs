using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;

namespace CodeCompliance.Core.Dm
{
    public static partial class DmAuditService
    {
        // ── 3. Rooms, spaces and units ──────────────────────────────────────────

        private static void CheckRoomsAndSpaces(Document doc, DmAuditResult result,
                                                DmParameters parameters, DmAuditOptions options)
        {
            int before = result.Findings.Count;

            List<Element> rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .ToList();

            if (rooms.Count == 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.SpacesAndUnits,
                    Severity = DmSeverity.Critical,
                    Scope = "Rooms",
                    Title = "The model contains no rooms",
                    Detail = "DM builds the building card (built-up, gross, net and gross floor areas, units " +
                             "and parking counts) from IfcSpace. Every internal space and every accessible " +
                             "roof or terrace must be modelled as a room.",
                    Reference = "Dubai BIM Standard, Appendix B — Room (IfcSpace); building card generator",
                    FixKind = DmFixKind.ModelChange,
                    FixAction = "Place rooms in every enclosed space of every level before exporting.",
                    CheckedCount = 0,
                    AffectedCount = 1
                }, result.ModelTitle);
                Summarize(result, DmCheckGroup.SpacesAndUnits, "Rooms, usage codes and unit data", 0, before);
                return;
            }

            var unplaced = new List<Element>();
            var unenclosed = new List<Element>();
            var placed = new List<Element>();
            var badNumbers = new List<Element>();
            var missingNames = new List<Element>();
            var byNumber = new Dictionary<string, List<Element>>(StringComparer.OrdinalIgnoreCase);

            foreach (Element element in rooms)
            {
                var room = element as Autodesk.Revit.DB.Architecture.Room;
                if (room == null)
                    continue;

                if (room.Location == null)
                {
                    unplaced.Add(room);
                    continue;
                }
                if (room.Area <= 1e-6)
                {
                    unenclosed.Add(room);
                    continue;
                }
                placed.Add(room);

                string number = room.Number ?? "";
                string name = RoomName(room);

                if (!RoomNumberPattern.IsMatch(number))
                    badNumbers.Add(room);
                if (name.Length == 0 || name.Equals("Room", StringComparison.OrdinalIgnoreCase))
                    missingNames.Add(room);

                if (number.Length > 0)
                {
                    if (!byNumber.TryGetValue(number, out List<Element>? list))
                    {
                        list = new List<Element>();
                        byNumber[number] = list;
                    }
                    list.Add(room);
                }
            }

            if (unplaced.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.SpacesAndUnits,
                    Severity = DmSeverity.Critical,
                    Scope = "Rooms",
                    Title = unplaced.Count + " unplaced room(s)",
                    Detail = "Unplaced rooms carry data but no geometry. They never reach the IFC and their " +
                             "areas are missing from the building card.",
                    Reference = "DM offline self-assessment — spaces and units",
                    FixKind = DmFixKind.ModelChange,
                    FixAction = "Place each unplaced room in its space, or delete it if it is left over.",
                    CheckedCount = rooms.Count,
                    AffectedCount = unplaced.Count
                }, result.ModelTitle, unplaced, options);
            }

            if (unenclosed.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.SpacesAndUnits,
                    Severity = DmSeverity.Critical,
                    Scope = "Rooms",
                    Title = unenclosed.Count + " room(s) are not enclosed or redundant (zero area)",
                    Detail = "A room with zero area has no closed boundary. DM's building card reports -1 for " +
                             "these spaces and the area check fails.",
                    Reference = "DM offline self-assessment — spaces, area deviation ±5%",
                    FixKind = DmFixKind.ModelChange,
                    FixAction = "Close the room boundary (walls or room separation lines), or delete the " +
                                "redundant room.",
                    CheckedCount = rooms.Count,
                    AffectedCount = unenclosed.Count
                }, result.ModelTitle, unenclosed, options);
            }

            var duplicateNumbers = byNumber.Where(p => p.Value.Count > 1).ToList();
            if (duplicateNumbers.Count > 0)
            {
                var elements = duplicateNumbers.SelectMany(p => p.Value).ToList();
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.SpacesAndUnits,
                    Severity = DmSeverity.Critical,
                    Scope = "Rooms",
                    Title = duplicateNumbers.Count + " room number(s) are used more than once",
                    Detail = "Duplicated numbers (" + string.Join(", ", duplicateNumbers.Take(12).Select(p => p.Key)) +
                             (duplicateNumbers.Count > 12 ? " …" : "") +
                             ") make spaces indistinguishable in the building card and in the unit grouping.",
                    Reference = "DM offline self-assessment — spaces and units",
                    FixKind = DmFixKind.Rename,
                    FixAction = "Renumber the duplicated rooms so every room number is unique, keeping the " +
                                "level prefix scheme (e.g. F1-001).",
                    CheckedCount = placed.Count,
                    AffectedCount = elements.Count
                }, result.ModelTitle, elements, options);
            }

            if (badNumbers.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.SpacesAndUnits,
                    Severity = DmSeverity.Error,
                    Scope = "Rooms",
                    Title = badNumbers.Count + " room number(s) do not follow the level-based format",
                    Detail = "Room numbers are expected as level abbreviation, hyphen, three digits — e.g. " +
                             "F1-001, B1-014 — so spaces can be traced back to their storey.",
                    Reference = "Dubai BIM Standard — space numbering per storey",
                    FixKind = DmFixKind.Rename,
                    FixAction = "Renumber the listed rooms to <LEVEL>-<3 digits> matching their storey.",
                    ReferenceData = DmReferenceData.LevelNaming(),
                    CheckedCount = placed.Count,
                    AffectedCount = badNumbers.Count
                }, result.ModelTitle, badNumbers, options);
            }

            if (missingNames.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.SpacesAndUnits,
                    Severity = DmSeverity.Error,
                    Scope = "Rooms",
                    Title = missingNames.Count + " room(s) have no name (still \"Room\")",
                    Detail = "The room name drives the space usage classification and appears on the DM " +
                             "building card; the default name is treated as missing data.",
                    Reference = "Dubai BIM Standard, Appendix B — Room (IfcSpace)",
                    FixKind = DmFixKind.Rename,
                    FixAction = "Name every room after its function, consistent with the submitted drawings.",
                    CheckedCount = placed.Count,
                    AffectedCount = missingNames.Count
                }, result.ModelTitle, missingNames, options);
            }

            // The room names of this model, mapped to Appendix C codes, so the fix prompts can
            // carry a ready proposal instead of the whole vocabulary.
            result.SpaceUsageSuggestions = DmUsageMatcher.SuggestionTable(
                placed.Select(r => r is Autodesk.Revit.DB.Architecture.Room room ? RoomName(room) : ""));

            // Appendix B and IDS attributes for IfcSpace.
            var attributes = new List<DmAttribute>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DmAttribute attribute in DmKnowledgeBase.RequiredAttributes("Room_Spaces", options.Stage, options.IncludeConditional))
            {
                if (attribute.UserInput && seen.Add(attribute.Name))
                    attributes.Add(attribute);
            }
            foreach (DmIdsRequirement requirement in DmKnowledgeBase.IdsPropertiesFor("IFCSPACE"))
            {
                if (IsParkingAttribute(requirement.BaseName))
                    continue;
                if (seen.Add(requirement.BaseName))
                    attributes.Add(new DmAttribute
                    {
                        Name = requirement.BaseName,
                        PropertySet = requirement.PropertySet,
                        UserInput = true,
                        Description = "Required by DM's IDS rule set for IfcSpace."
                    });
            }

            CheckAttributeSet(result, parameters, options, DmCheckGroup.SpacesAndUnits, "Rooms",
                              "Room (IfcSpace)", placed, attributes, "Room_Spaces",
                              new[] { BuiltInCategory.OST_Rooms });

            // Parking spaces carry three extra attributes.
            var parkingRooms = placed.Where(r => IsParkingRoom(r, parameters)).ToList();
            if (parkingRooms.Count > 0)
            {
                var parkingAttributes = new List<DmAttribute>
                {
                    new DmAttribute { Name = "ParkingUse", UserInput = true, PropertySet = "Building Permit",
                        Description = "How the parking space is used (e.g. Private, Visitor, Handicap, Loading).", Sample = "Visitor" },
                    new DmAttribute { Name = "E-Charging", UserInput = true, PropertySet = "Building Permit",
                        Description = "Whether the parking space has an electric-vehicle charger.", Sample = "NO" },
                    new DmAttribute { Name = "HasWheelStop", UserInput = true, PropertySet = "Building Permit",
                        Description = "Whether the parking space has a wheel stop.", Sample = "YES" }
                };
                CheckAttributeSet(result, parameters, options, DmCheckGroup.SpacesAndUnits, "Parking spaces",
                                  "Parking (IfcSpace)", parkingRooms, parkingAttributes, "Parking_Spaces",
                                  new[] { BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Parking });
            }

            List<Element> parkingElements = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Parking)
                .WhereElementIsNotElementType()
                .ToList();
            if (parkingElements.Count > 0 && parkingRooms.Count == 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.SpacesAndUnits,
                    Severity = DmSeverity.Error,
                    Scope = "Parking",
                    Title = parkingElements.Count + " parking element(s) exist but no parking space is modelled as a room",
                    Detail = "DM counts parking from IfcSpace, not from Revit parking components. Parking bays " +
                             "must be rooms carrying ParkingUse, E-Charging and HasWheelStop.",
                    Reference = "Appendix A element matrix — Parking Spaces exported as IfcSpace",
                    FixKind = DmFixKind.ModelChange,
                    FixAction = "Place a room over each parking bay and fill the parking attributes, or map the " +
                                "parking family to IfcSpace on export.",
                    CheckedCount = parkingElements.Count,
                    AffectedCount = parkingElements.Count
                }, result.ModelTitle, parkingElements, options);
            }

            CheckUsageCodes(result, parameters, options, placed);
            CheckAreaReconciliation(doc, result, parameters, placed);

            Summarize(result, DmCheckGroup.SpacesAndUnits, "Rooms, usage codes and unit data", rooms.Count, before);
        }

        private static string RoomName(Autodesk.Revit.DB.Architecture.Room room)
        {
            Parameter? parameter = room.get_Parameter(BuiltInParameter.ROOM_NAME);
            string name = parameter != null ? parameter.AsString() ?? "" : "";
            return name.Trim();
        }

        private static bool IsParkingAttribute(string name)
        {
            return name.Equals("ParkingUse", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("E-Charging", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("HasWheelStop", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsParkingRoom(Element room, DmParameters parameters)
        {
            string name = "";
            var typed = room as Autodesk.Revit.DB.Architecture.Room;
            if (typed != null)
                name = RoomName(typed);
            if (name.IndexOf("PARK", StringComparison.OrdinalIgnoreCase) >= 0 ||
                name.IndexOf("CAR ", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            string usage = parameters.Value(room, "SpaceUsageDescription");
            if (usage.IndexOf("PARK", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return parameters.Value(room, "ParkingUse").Length > 0;
        }

        /// <summary>Usage codes must come from DM's Appendix C vocabularies, not free text.</summary>
        private static void CheckUsageCodes(DmAuditResult result, DmParameters parameters,
                                            DmAuditOptions options, List<Element> rooms)
        {
            CheckVocabulary(result, parameters, options, rooms, "SpaceUsageCode",
                            DmKnowledgeBase.SpaceUsageCodes, DmSeverity.Critical,
                            "Appendix C — Space usage codes (SC_xx_xx_xx)");
            CheckVocabulary(result, parameters, options, rooms, "UnitUsageCode",
                            DmKnowledgeBase.UnitUsageCodes, DmSeverity.Error,
                            "Appendix C — Unit usage codes (RE_xx, CO_xx, …)");
            CheckVocabulary(result, parameters, options, rooms, "BuildingOccupancyUsageCode",
                            DmKnowledgeBase.BuildingOccupancyCodes, DmSeverity.Error,
                            "Appendix C — Building occupancy usage codes");
            CheckVocabulary(result, parameters, options, rooms, "ZoneObjectType",
                            DmKnowledgeBase.ZoneObjectTypes, DmSeverity.Error,
                            "Appendix C — Zone categories and ZoneObjectType");

            // SpaceUsageDescription must belong to the SpaceUsageCode of the same room.
            var mismatched = new List<Element>();
            foreach (Element room in rooms)
            {
                string code = parameters.Value(room, "SpaceUsageCode").Trim();
                string description = parameters.Value(room, "SpaceUsageDescription").Trim();
                if (code.Length == 0 || description.Length == 0)
                    continue;
                if (!DmKnowledgeBase.SpaceUsageCodes.TryGetValue(code, out DmUsageCode? usage))
                    continue;
                if (!string.Equals(usage.Description.Trim(), description, StringComparison.OrdinalIgnoreCase))
                    mismatched.Add(room);
            }
            if (mismatched.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.SpacesAndUnits,
                    Severity = DmSeverity.Warning,
                    Scope = "Rooms",
                    Title = mismatched.Count + " room(s) have a SpaceUsageDescription that does not match their code",
                    Detail = "The description must be the Appendix C wording that belongs to the " +
                             "SpaceUsageCode of the same space; DM cross-checks the pair.",
                    Reference = "Appendix C — Space usage codes",
                    FixKind = DmFixKind.SetParameter,
                    ParameterName = "SpaceUsageDescription",
                    FixAction = "Rewrite SpaceUsageDescription with the Appendix C description of the room's " +
                                "SpaceUsageCode.",
                    CheckedCount = rooms.Count,
                    AffectedCount = mismatched.Count
                }, result.ModelTitle, mismatched, options);
            }
        }

        private static void CheckVocabulary(DmAuditResult result, DmParameters parameters, DmAuditOptions options,
                                            List<Element> rooms, string parameterName,
                                            IReadOnlyDictionary<string, DmUsageCode> vocabulary,
                                            DmSeverity severity, string reference)
        {
            if (vocabulary.Count == 0)
                return;

            var invalid = new List<Element>();
            var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Element room in rooms)
            {
                string value = parameters.Value(room, parameterName).Trim();
                if (value.Length == 0)
                    continue;
                if (vocabulary.ContainsKey(value))
                    continue;
                invalid.Add(room);
                values.Add(value);
            }

            if (invalid.Count == 0)
                return;

            Add(result, new DmFinding
            {
                Group = DmCheckGroup.SpacesAndUnits,
                Severity = severity,
                Scope = "Rooms",
                Title = invalid.Count + " room(s) carry a " + parameterName + " that is not a DM code",
                Detail = "Values found that are not in DM's controlled vocabulary: " +
                         string.Join(", ", values.Take(15)) + (values.Count > 15 ? " …" : "") +
                         ". DM validates these codes literally, so free text or an old code fails the check.",
                Reference = reference,
                FixKind = DmFixKind.SetParameter,
                ParameterName = parameterName,
                FixAction = "Replace the values with codes from the DM list (shipped with the plugin at " +
                            DmKnowledgeBase.OverrideFolder + ").",
                CheckedCount = rooms.Count,
                AffectedCount = invalid.Count
            }, result.ModelTitle, invalid, options);
        }

        /// <summary>Room areas per storey against the storey area attributes (DM tolerates ±5%).</summary>
        private static void CheckAreaReconciliation(Document doc, DmAuditResult result,
                                                    DmParameters parameters, List<Element> rooms)
        {
            var areaByLevel = new Dictionary<long, double>();
            foreach (Element room in rooms)
            {
                var typed = room as Autodesk.Revit.DB.Architecture.Room;
                if (typed?.Level == null)
                    continue;
                double squareMeters = UnitUtils.ConvertFromInternalUnits(typed.Area, UnitTypeId.SquareMeters);
                long levelId = typed.Level.Id.Value;
                areaByLevel[levelId] = areaByLevel.TryGetValue(levelId, out double sum) ? sum + squareMeters : squareMeters;
            }

            var deviating = new List<Element>();
            var details = new List<string>();
            foreach (KeyValuePair<long, double> pair in areaByLevel)
            {
                var level = doc.GetElement(new ElementId(pair.Key)) as Level;
                if (level == null)
                    continue;
                Parameter? declared = DmParameters.Lookup(level, "TotalBuildupArea");
                if (declared == null || DmParameters.Classify(declared) != DmParameterState.Filled ||
                    declared.StorageType != StorageType.Double)
                    continue;

                double declaredValue = declared.AsDouble();
                // The attribute is a plain number in m² in the DM template, but a length/area
                // parameter would be stored in internal units: accept whichever is close.
                double asSquareMeters = UnitUtils.ConvertFromInternalUnits(declaredValue, UnitTypeId.SquareMeters);
                double best = Math.Abs(declaredValue - pair.Value) <= Math.Abs(asSquareMeters - pair.Value)
                    ? declaredValue
                    : asSquareMeters;
                if (best <= 0)
                    continue;

                double deviation = Math.Abs(best - pair.Value) / best * 100.0;
                if (deviation <= 5.0)
                    continue;

                deviating.Add(level);
                details.Add(level.Name + ": rooms " + pair.Value.ToString("F1", CultureInfo.InvariantCulture) +
                            " m² vs TotalBuildupArea " + best.ToString("F1", CultureInfo.InvariantCulture) +
                            " m² (" + deviation.ToString("F1", CultureInfo.InvariantCulture) + "%)");
            }

            if (deviating.Count == 0)
                return;

            Add(result, new DmFinding
            {
                Group = DmCheckGroup.SpacesAndUnits,
                Severity = DmSeverity.Warning,
                Scope = "Areas",
                Title = deviating.Count + " storey area(s) deviate more than 5% from the placed rooms",
                Detail = "DM's building card generator compares the declared areas with the ones it computes " +
                         "from IfcSpace and accepts ±5%. Deviations found: " +
                         string.Join("; ", details.Take(10)) + (details.Count > 10 ? " …" : "") + ".",
                Reference = "Technical Guides Part 3 — building card, ±5% area deviation tolerance",
                FixKind = DmFixKind.Review,
                FixAction = "Reconcile the storey area attributes with the modelled rooms, or complete the " +
                            "missing rooms on those storeys.",
                CheckedCount = areaByLevel.Count,
                AffectedCount = deviating.Count
            }, result.ModelTitle, deviating, new DmAuditOptions());
        }

        // ── 4. Element attributes (Appendix B + IDS) ────────────────────────────

        private static void CheckElementAttributes(Document doc, DmAuditResult result,
                                                   DmParameters parameters, DmAuditOptions options)
        {
            int before = result.Findings.Count;
            int totalChecked = 0;

            foreach (DmElementRule rule in DmRuleCatalog.ElementRules)
            {
                List<Element> elements = Collect(doc, rule.Categories);
                if (elements.Count == 0)
                    continue;
                totalChecked += elements.Count;

                var attributes = new List<DmAttribute>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (DmAttribute attribute in DmKnowledgeBase.RequiredAttributes(rule.Table, options.Stage, options.IncludeConditional))
                {
                    if (attribute.UserInput && seen.Add(attribute.Name))
                        attributes.Add(attribute);
                }
                foreach (DmIdsRequirement requirement in DmKnowledgeBase.IdsPropertiesFor(rule.IfcEntity))
                {
                    if (seen.Add(requirement.BaseName))
                        attributes.Add(new DmAttribute
                        {
                            Name = requirement.BaseName,
                            PropertySet = requirement.PropertySet,
                            UserInput = true,
                            Description = "Required by DM's IDS rule set for " + rule.IfcEntity + "."
                        });
                }

                if (attributes.Count == 0)
                    continue;

                CheckAttributeSet(result, parameters, options, DmCheckGroup.ElementAttributes, rule.Display,
                                  rule.Display + " (" + rule.IfcEntity + ", " + rule.Discipline + " model)",
                                  elements, attributes, rule.Table, rule.Categories);
                CheckIfcMapping(result, doc, options, rule, elements);
            }

            Summarize(result, DmCheckGroup.ElementAttributes,
                      "Appendix B attributes and IDS rules per element class", totalChecked, before);
        }

        /// <summary>
        /// Checks one attribute list against one set of elements and produces at most one
        /// finding per attribute, plus one grouped finding for the attributes that are not
        /// bound to the category at all.
        /// </summary>
        private static void CheckAttributeSet(DmAuditResult result, DmParameters parameters, DmAuditOptions options,
                                              DmCheckGroup group, string scope, string reference,
                                              List<Element> elements, List<DmAttribute> attributes,
                                              string table, BuiltInCategory[] categories)
        {
            if (elements.Count == 0 || attributes.Count == 0)
                return;

            var unbound = new List<DmAttribute>();

            foreach (DmAttribute attribute in attributes)
            {
                var missing = new List<Element>();
                int notBound = 0;
                int yesNoCount = 0;
                int yesCount = 0;

                foreach (Element element in elements)
                {
                    DmParameterState state = parameters.State(element, attribute.Name);
                    if (state == DmParameterState.NotBound)
                    {
                        notBound++;
                        missing.Add(element);
                        continue;
                    }
                    if (parameters.IsYesNo(element, attribute.Name))
                    {
                        yesNoCount++;
                        if (parameters.IsYes(element, attribute.Name))
                            yesCount++;
                        continue;
                    }
                    if (state == DmParameterState.Empty)
                        missing.Add(element);
                }

                if (notBound == elements.Count)
                {
                    unbound.Add(attribute);
                    continue;
                }

                if (missing.Count > 0)
                {
                    var finding = new DmFinding
                    {
                        Group = group,
                        Severity = notBound > 0 ? DmSeverity.Critical : DmSeverity.Error,
                        Scope = scope,
                        Title = "\"" + attribute.Name + "\" missing on " + missing.Count + " of " +
                                elements.Count + " " + scope.ToLowerInvariant(),
                        Detail = (attribute.Description.Length > 0
                                     ? attribute.Description
                                     : "Attribute required by the DM building permit data set.") +
                                 (attribute.PropertySet.Length > 0 ? "  Property set: " + attribute.PropertySet + "." : "") +
                                 (notBound > 0
                                     ? "  " + notBound + " of them do not even carry the parameter, so the DM " +
                                       "shared parameter is not bound to every type involved."
                                     : ""),
                        Reference = reference,
                        FixKind = notBound > 0 ? DmFixKind.BindParameter : DmFixKind.SetParameter,
                        ParameterName = attribute.Name,
                        SampleValue = attribute.Sample,
                        FixAction = notBound > 0
                            ? "Bind the DM shared parameter \"" + attribute.Name + "\" to this category, then " +
                              "fill it on the listed elements" +
                              (attribute.Sample.Length > 0 ? " (DM sample: " + attribute.Sample + ")" : "") + "."
                            : "Fill \"" + attribute.Name + "\" on the listed elements" +
                              (attribute.Sample.Length > 0 ? " (DM sample: " + attribute.Sample + ")" : "") + ".",
                        CheckedCount = elements.Count,
                        AffectedCount = missing.Count,
                        Table = table
                    };
                    finding.Categories.AddRange(categories.Select(c => c.ToString()));
                    if (notBound > 0)
                        finding.ParametersToBind.Add(attribute.Name);
                    Add(result, finding, result.ModelTitle, missing, options);
                }
                else if (yesNoCount == elements.Count && yesCount == 0 && elements.Count > 3)
                {
                    Add(result, new DmFinding
                    {
                        Group = group,
                        Severity = DmSeverity.Warning,
                        Scope = scope,
                        Title = "\"" + attribute.Name + "\" is No on all " + elements.Count + " " +
                                scope.ToLowerInvariant(),
                        Detail = "The Yes/No attribute exists everywhere but was never set to Yes, which " +
                                 "usually means it was left at its default instead of being filled in.",
                        Reference = reference,
                        FixKind = DmFixKind.SetParameter,
                        ParameterName = attribute.Name,
                        SampleValue = attribute.Sample,
                        FixAction = "Confirm the value of \"" + attribute.Name + "\" and set Yes where it applies.",
                        CheckedCount = elements.Count,
                        AffectedCount = elements.Count,
                        Table = table
                    }, result.ModelTitle, elements, options);
                }
            }

            if (unbound.Count > 0)
            {
                var binding = new DmFinding
                {
                    Group = group,
                    Severity = DmSeverity.Critical,
                    Scope = scope,
                    Title = unbound.Count + " DM attribute(s) are not bound to " + scope.ToLowerInvariant(),
                    Detail = "None of the " + elements.Count + " elements carries: " +
                             string.Join(", ", unbound.Select(a => a.Name).Take(30)) +
                             (unbound.Count > 30 ? " …" : "") +
                             ". Without the parameter the attribute cannot be exported at all.",
                    Reference = reference,
                    FixKind = DmFixKind.BindParameter,
                    FixAction = "Create and bind these DM attributes to this category, then fill the values. " +
                                "The plugin ships the definitions and writes the shared parameter file itself: " +
                                "click \"Bind DM parameters\" in the dashboard, or run the script in the prompt.",
                    CheckedCount = elements.Count,
                    AffectedCount = elements.Count,
                    Table = table
                };
                binding.ParametersToBind.AddRange(unbound.Select(a => a.Name));
                binding.Categories.AddRange(categories.Select(c => c.ToString()));
                Add(result, binding, result.ModelTitle, elements, options);
            }
        }

        /// <summary>
        /// Categories that can export as several IFC classes must say which one they are:
        /// otherwise the exporter default decides, and DM's rule check runs the wrong rules.
        /// </summary>
        private static void CheckIfcMapping(DmAuditResult result, Document doc, DmAuditOptions options,
                                            DmElementRule rule, List<Element> elements)
        {
            string? categoryName = elements.Count > 0 ? elements[0].Category?.Name : null;
            if (categoryName == null)
                return;
            if (!DmRuleCatalog.AmbiguousCategories.Any(c => string.Equals(c, categoryName, StringComparison.OrdinalIgnoreCase)))
                return;

            var withoutMapping = new List<Element>();
            var typeCache = new Dictionary<long, bool>();
            foreach (Element element in elements)
            {
                ElementId typeId = element.GetTypeId();
                long key = typeId.Value;
                if (!typeCache.TryGetValue(key, out bool mapped))
                {
                    Element? type = doc.GetElement(typeId);
                    mapped = type != null && (HasValue(type, "IfcExportAs") || HasValue(type, "IFC_EXPORT_ELEMENT_AS"));
                    typeCache[key] = mapped;
                }
                if (!mapped && !HasValue(element, "IfcExportAs"))
                    withoutMapping.Add(element);
            }

            if (withoutMapping.Count == 0)
                return;

            List<string> ifcClasses = DmKnowledgeBase.CategoryToIfc.TryGetValue(categoryName, out List<string>? classes)
                ? classes
                : new List<string> { rule.IfcEntity };

            Add(result, new DmFinding
            {
                Group = DmCheckGroup.ElementAttributes,
                Severity = DmSeverity.Warning,
                Scope = rule.Display,
                Title = "IFC class not set explicitly on " + withoutMapping.Count + " " + rule.Display.ToLowerInvariant(),
                Detail = "DM's category mapping lets \"" + categoryName + "\" export as " +
                         string.Join(" / ", ifcClasses) + ". Elements without an explicit \"IfcExportAs\" " +
                         "follow the exporter default, which is how elements end up in the wrong IFC class.",
                Reference = "Dubai BIM E-Submission parameter mapping (Revit category → IFC class)",
                FixKind = DmFixKind.SetParameter,
                ParameterName = "IfcExportAs",
                SampleValue = ifcClasses.FirstOrDefault() ?? rule.IfcEntity,
                FixAction = "Set \"IfcExportAs\" on the element types to the intended IFC class, or load the DM " +
                            "category mapping file in the IFC export options.",
                CheckedCount = elements.Count,
                AffectedCount = withoutMapping.Count
            }, result.ModelTitle, withoutMapping, options);
        }

        private static bool HasValue(Element element, string parameterName)
        {
            return DmParameters.Classify(DmParameters.Lookup(element, parameterName)) == DmParameterState.Filled;
        }

        private static List<Element> Collect(Document doc, BuiltInCategory[] categories)
        {
            var elements = new List<Element>();
            foreach (BuiltInCategory category in categories)
            {
                try
                {
                    elements.AddRange(new FilteredElementCollector(doc)
                        .OfCategory(category)
                        .WhereElementIsNotElementType()
                        .ToElements());
                }
                catch
                {
                    // a category that does not exist in this Revit version is simply skipped
                }
            }
            return elements;
        }

        // ── 5. Object and family naming ─────────────────────────────────────────

        private static readonly char[] ForbiddenNameChars =
            { '!', '"', ',', '$', '%', '^', '&', '*', '{', '}', '[', ']', '+', '=', '<', '>', '?', '/', '\\', '|', '@', '#', ' ' };

        private static void CheckObjectNaming(Document doc, DmAuditResult result)
        {
            int before = result.Findings.Count;

            // Only types actually used in the model: unused library types are not submitted.
            var usedTypeIds = new HashSet<long>();
            foreach (DmElementRule rule in DmRuleCatalog.ElementRules)
            {
                foreach (Element element in Collect(doc, rule.Categories))
                {
                    ElementId typeId = element.GetTypeId();
                    if (typeId != ElementId.InvalidElementId)
                        usedTypeIds.Add(typeId.Value);
                }
            }

            var tooLong = new List<Element>();
            var withSpaces = new List<Element>();
            var withSymbols = new List<Element>();
            var withoutSeparator = new List<Element>();
            int checkedTypes = 0;

            foreach (long id in usedTypeIds)
            {
                Element? type = doc.GetElement(new ElementId(id));
                if (type == null)
                    continue;
                string name;
                try
                {
                    name = type.Name ?? "";
                }
                catch
                {
                    continue;
                }
                if (name.Length == 0)
                    continue;
                checkedTypes++;

                if (name.Length > 30)
                    tooLong.Add(type);
                if (name.IndexOf(' ') >= 0)
                    withSpaces.Add(type);
                if (name.IndexOfAny(ForbiddenNameChars) >= 0 && name.IndexOf(' ') < 0)
                    withSymbols.Add(type);
                if (name.IndexOf('_') < 0)
                    withoutSeparator.Add(type);
            }

            const string reference = "Dubai BIM Standard — object naming: Category_FunctionalType_Discipline_Description";
            var options = new DmAuditOptions();

            if (withoutSeparator.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.ObjectNaming,
                    Severity = DmSeverity.Error,
                    Scope = "Family and system types",
                    Title = withoutSeparator.Count + " used type name(s) have no underscore-separated fields",
                    Detail = "DM object names are built from underscore separated fields, e.g. " +
                             "DOR_INT_AR_850x2100_TIMBER. Names without fields cannot be parsed by DM's checker.",
                    Reference = reference,
                    FixKind = DmFixKind.Rename,
                    FixAction = "Rename the types to Category_FunctionalType_Discipline_Description.",
                    ReferenceData = DmReferenceData.ObjectNaming(),
                    CheckedCount = checkedTypes,
                    AffectedCount = withoutSeparator.Count
                }, result.ModelTitle, withoutSeparator, options);
            }

            if (withSpaces.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.ObjectNaming,
                    Severity = DmSeverity.Warning,
                    Scope = "Family and system types",
                    Title = withSpaces.Count + " used type name(s) contain spaces",
                    Detail = "Spaces are not allowed in DM object names; fields are separated with underscores " +
                             "and abbreviations are uppercase.",
                    Reference = reference,
                    FixKind = DmFixKind.Rename,
                    FixAction = "Replace spaces with underscores in the type names.",
                    ReferenceData = DmReferenceData.ObjectNaming(),
                    CheckedCount = checkedTypes,
                    AffectedCount = withSpaces.Count
                }, result.ModelTitle, withSpaces, options);
            }

            if (withSymbols.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.ObjectNaming,
                    Severity = DmSeverity.Warning,
                    Scope = "Family and system types",
                    Title = withSymbols.Count + " used type name(s) contain forbidden characters",
                    Detail = "Only letters, digits and underscores are allowed in DM object names.",
                    Reference = reference,
                    FixKind = DmFixKind.Rename,
                    FixAction = "Remove special characters from the type names.",
                    ReferenceData = DmReferenceData.ObjectNaming(),
                    CheckedCount = checkedTypes,
                    AffectedCount = withSymbols.Count
                }, result.ModelTitle, withSymbols, options);
            }

            if (tooLong.Count > 0)
            {
                Add(result, new DmFinding
                {
                    Group = DmCheckGroup.ObjectNaming,
                    Severity = DmSeverity.Warning,
                    Scope = "Family and system types",
                    Title = tooLong.Count + " used type name(s) are longer than 30 characters",
                    Detail = "DM limits object names to 30 characters so they stay readable in the IFC viewer " +
                             "and in the checker report.",
                    Reference = reference,
                    FixKind = DmFixKind.Rename,
                    FixAction = "Shorten the type names to 30 characters using the standard abbreviations.",
                    ReferenceData = DmReferenceData.ObjectNaming(),
                    CheckedCount = checkedTypes,
                    AffectedCount = tooLong.Count
                }, result.ModelTitle, tooLong, options);
            }

            Summarize(result, DmCheckGroup.ObjectNaming, "Family and type naming convention", checkedTypes, before);
        }
    }
}
