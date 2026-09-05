using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace CodeCompliance.Core.Dm
{
    /// <summary>State of one DM attribute on one element.</summary>
    public enum DmParameterState
    {
        /// <summary>The parameter does not exist on the element nor on its type.</summary>
        NotBound = 0,
        /// <summary>The parameter exists but carries no value.</summary>
        Empty = 1,
        /// <summary>The parameter carries a value.</summary>
        Filled = 2
    }

    /// <summary>
    /// Reads DM attributes off elements the way the IFC exporter will: instance parameter
    /// first, then the element type. Type lookups are cached because a model has far fewer
    /// types than elements.
    /// </summary>
    public sealed class DmParameters
    {
        private readonly Document _doc;
        private readonly Dictionary<string, DmParameterState> _typeCache =
            new Dictionary<string, DmParameterState>(StringComparer.Ordinal);
        private readonly Dictionary<string, bool> _yesNoCache =
            new Dictionary<string, bool>(StringComparer.Ordinal);

        public DmParameters(Document doc)
        {
            _doc = doc;
        }

        /// <summary>Instance value first, then type value.</summary>
        public DmParameterState State(Element element, string name)
        {
            Parameter? instance = Lookup(element, name);
            DmParameterState state = Classify(instance);
            if (state == DmParameterState.Filled)
                return state;

            ElementId typeId = element.GetTypeId();
            DmParameterState typeState = TypeState(typeId, name);
            if (typeState == DmParameterState.Filled)
                return typeState;

            if (state == DmParameterState.Empty || typeState == DmParameterState.Empty)
                return DmParameterState.Empty;
            return DmParameterState.NotBound;
        }

        /// <summary>Value as text (instance first, then type), or "" when there is none.</summary>
        public string Value(Element element, string name)
        {
            Parameter? parameter = Lookup(element, name);
            string value = AsText(parameter);
            if (value.Length > 0)
                return value;

            Element? type = _doc.GetElement(element.GetTypeId());
            return type != null ? AsText(Lookup(type, name)) : "";
        }

        /// <summary>True when the parameter is a Yes/No parameter on this element or its type.</summary>
        public bool IsYesNo(Element element, string name)
        {
            string key = element.GetTypeId().Value.ToString() + "|" + name;
            if (_yesNoCache.TryGetValue(key, out bool cached))
                return cached;

            bool yesNo = IsYesNoParameter(Lookup(element, name));
            if (!yesNo)
            {
                Element? type = _doc.GetElement(element.GetTypeId());
                if (type != null)
                    yesNo = IsYesNoParameter(Lookup(type, name));
            }
            _yesNoCache[key] = yesNo;
            return yesNo;
        }

        /// <summary>True when a Yes/No attribute is set to Yes on the element or its type.</summary>
        public bool IsYes(Element element, string name)
        {
            Parameter? parameter = Lookup(element, name);
            if (parameter != null && parameter.StorageType == StorageType.Integer && parameter.HasValue)
                return parameter.AsInteger() != 0;

            Element? type = _doc.GetElement(element.GetTypeId());
            Parameter? typeParameter = type != null ? Lookup(type, name) : null;
            return typeParameter != null && typeParameter.StorageType == StorageType.Integer &&
                   typeParameter.HasValue && typeParameter.AsInteger() != 0;
        }

        private DmParameterState TypeState(ElementId typeId, string name)
        {
            if (typeId == ElementId.InvalidElementId)
                return DmParameterState.NotBound;

            string key = typeId.Value.ToString() + "|" + name;
            if (_typeCache.TryGetValue(key, out DmParameterState cached))
                return cached;

            Element? type = _doc.GetElement(typeId);
            DmParameterState state = type == null
                ? DmParameterState.NotBound
                : Classify(Lookup(type, name));
            _typeCache[key] = state;
            return state;
        }

        /// <summary>Parameter by name, tolerating the exceptions Revit throws on odd names.</summary>
        public static Parameter? Lookup(Element element, string name)
        {
            try
            {
                return element.LookupParameter(name);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>NotBound / Empty / Filled for one parameter.</summary>
        public static DmParameterState Classify(Parameter? parameter)
        {
            if (parameter == null)
                return DmParameterState.NotBound;

            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return string.IsNullOrWhiteSpace(parameter.AsString())
                            ? DmParameterState.Empty
                            : DmParameterState.Filled;
                    case StorageType.Integer:
                        if (IsYesNoParameter(parameter))
                            return parameter.HasValue ? DmParameterState.Filled : DmParameterState.Empty;
                        return parameter.HasValue && parameter.AsInteger() != 0
                            ? DmParameterState.Filled
                            : DmParameterState.Empty;
                    case StorageType.Double:
                        return parameter.HasValue && Math.Abs(parameter.AsDouble()) > 1e-9
                            ? DmParameterState.Filled
                            : DmParameterState.Empty;
                    case StorageType.ElementId:
                        return parameter.HasValue && parameter.AsElementId() != ElementId.InvalidElementId
                            ? DmParameterState.Filled
                            : DmParameterState.Empty;
                    default:
                        return DmParameterState.Empty;
                }
            }
            catch
            {
                return DmParameterState.Empty;
            }
        }

        public static string AsText(Parameter? parameter)
        {
            if (parameter == null || !parameter.HasValue)
                return "";
            try
            {
                switch (parameter.StorageType)
                {
                    case StorageType.String:
                        return parameter.AsString() ?? "";
                    case StorageType.Integer:
                        return IsYesNoParameter(parameter)
                            ? (parameter.AsInteger() != 0 ? "Yes" : "No")
                            : parameter.AsInteger().ToString();
                    case StorageType.Double:
                        return parameter.AsValueString() ?? parameter.AsDouble().ToString("0.###");
                    default:
                        return parameter.AsValueString() ?? "";
                }
            }
            catch
            {
                return "";
            }
        }

        private static bool IsYesNoParameter(Parameter? parameter)
        {
            if (parameter == null || parameter.StorageType != StorageType.Integer)
                return false;
            try
            {
                Definition definition = parameter.Definition;
                return definition != null && definition.GetDataType() == SpecTypeId.Boolean.YesNo;
            }
            catch
            {
                return false;
            }
        }
    }
}
