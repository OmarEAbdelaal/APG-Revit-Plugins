using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CodeCompliance.Core.Mcp
{
    /// <summary>One command declared by the command.json of a command set.</summary>
    public sealed class McpCommandInfo
    {
        public string Name { get; set; } = "";
        public string Description { get; set; } = "";
        public string SetName { get; set; } = "";
        /// <summary>DLL for the running Revit version, or null when the set has no build for it.</summary>
        public string? AssemblyPath { get; set; }
        public bool AvailableForThisRevit => AssemblyPath != null;
        public bool Enabled { get; set; } = true;
        /// <summary>True for commands implemented by the host itself (ping, search_modules, use_module).</summary>
        public bool BuiltIn { get; set; }
    }

    /// <summary>A command instance created from a command-set DLL, invoked through reflection.</summary>
    internal sealed class LoadedCommand
    {
        public LoadedCommand(string name, string setName, string description, Func<JObject, string, object?> execute)
        {
            Name = name;
            SetName = setName;
            Description = description;
            Execute = execute;
        }

        public string Name { get; }
        public string SetName { get; }
        public string Description { get; }
        public Func<JObject, string, object?> Execute { get; }
    }

    /// <summary>
    /// Discovers command sets under Commands\, loads the DLLs built for the running Revit
    /// version and executes commands by name for the socket service.
    ///
    /// Commands are recognised by shape, not by a shared SDK assembly: any public class with a
    /// string <c>CommandName</c> property and an <c>Execute(JObject, string)</c> method (the
    /// RevitMCPSDK <c>IRevitCommand</c> contract). The host therefore works with command sets
    /// compiled against any SDK version and needs no SDK reference itself.
    /// </summary>
    public sealed class McpCommandHost
    {
        private readonly Dictionary<string, LoadedCommand> _commands =
            new Dictionary<string, LoadedCommand>(StringComparer.OrdinalIgnoreCase);
        private readonly string _revitVersion;

        public McpCommandHost(string revitVersion)
        {
            _revitVersion = revitVersion;
        }

        public int Count => _commands.Count;
        public IEnumerable<string> CommandNames => _commands.Keys;

        // ── Discovery (no Revit API needed) ─────────────────────────────────────

        /// <summary>Reads every Commands\Set\command.json and resolves the DLL for this Revit version.</summary>
        public static List<McpCommandInfo> Discover(string revitVersion, McpSettings settings)
        {
            var result = new List<McpCommandInfo>
            {
                BuiltIn("ping", "Connection test: returns Revit version, plugin version and loaded command count."),
                BuiltIn("search_modules", "List the Revit commands (modules) available to the AI, optionally filtered by keyword."),
                BuiltIn("use_module", "Execute a named Revit command (module) with parameters.")
            };

            if (!Directory.Exists(McpPaths.CommandsDir))
                return result;

            foreach (string setDir in Directory.GetDirectories(McpPaths.CommandsDir).OrderBy(d => d))
            {
                string manifestPath = Path.Combine(setDir, "command.json");
                if (!File.Exists(manifestPath))
                    continue;
                try
                {
                    JObject manifest = JObject.Parse(File.ReadAllText(manifestPath));
                    string setName = manifest.Value<string>("name") ?? Path.GetFileName(setDir);
                    string versionDir = Path.Combine(setDir, revitVersion);
                    IEnumerable<JObject> commands = manifest["commands"]?.OfType<JObject>() ?? Enumerable.Empty<JObject>();
                    foreach (JObject cmd in commands)
                    {
                        string? name = cmd.Value<string>("commandName");
                        if (string.IsNullOrWhiteSpace(name))
                            continue;
                        string dllName = cmd.Value<string>("assemblyPath") ?? "";
                        string? dll = null;
                        if (Directory.Exists(versionDir))
                        {
                            string candidate = Path.Combine(versionDir, dllName);
                            if (dllName.Length > 0 && File.Exists(candidate))
                                dll = candidate;
                            else if (dllName.Length == 0)
                                dll = Directory.GetFiles(versionDir, "*.dll").FirstOrDefault();
                        }
                        result.Add(new McpCommandInfo
                        {
                            Name = name!,
                            Description = cmd.Value<string>("description") ?? "",
                            SetName = setName,
                            AssemblyPath = dll,
                            Enabled = !settings.DisabledCommands.Contains(name!, StringComparer.OrdinalIgnoreCase)
                        });
                    }
                }
                catch (Exception ex)
                {
                    McpLog.Error("Invalid command.json in " + setDir, ex);
                }
            }
            return result;
        }

        private static McpCommandInfo BuiltIn(string name, string description)
        {
            return new McpCommandInfo
            {
                Name = name,
                Description = description,
                SetName = "APG Revit Plugins",
                AssemblyPath = "",
                BuiltIn = true
            };
        }

        // ── Loading (must run in a Revit API context: commands create ExternalEvents) ──

        /// <summary>Loads every enabled, available command. Call from an IExternalCommand or a Revit event.</summary>
        public void LoadAll(UIApplication uiApp, McpSettings settings)
        {
            _commands.Clear();
            RegisterBuiltIns(uiApp);

            List<McpCommandInfo> infos = Discover(_revitVersion, settings);
            var wanted = new Dictionary<string, List<McpCommandInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (McpCommandInfo info in infos)
            {
                if (info.BuiltIn || !info.Enabled || info.AssemblyPath == null)
                    continue;
                if (!wanted.TryGetValue(info.AssemblyPath, out List<McpCommandInfo>? list))
                {
                    list = new List<McpCommandInfo>();
                    wanted[info.AssemblyPath] = list;
                }
                list.Add(info);
            }

            foreach (KeyValuePair<string, List<McpCommandInfo>> pair in wanted)
                LoadAssembly(pair.Key, pair.Value, uiApp);

            McpLog.Info("Loaded " + _commands.Count + " commands for Revit " + _revitVersion +
                        ": " + string.Join(", ", _commands.Keys.OrderBy(k => k)));
        }

        private void LoadAssembly(string dllPath, List<McpCommandInfo> wanted, UIApplication uiApp)
        {
            Assembly assembly;
            try
            {
                assembly = Assembly.LoadFrom(dllPath);
            }
            catch (Exception ex)
            {
                McpLog.Error("Cannot load " + dllPath, ex);
                return;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).Select(t => t!).ToArray();
                McpLog.Warn("Some types in " + Path.GetFileName(dllPath) + " could not be loaded: " +
                            string.Join("; ", ex.LoaderExceptions.Take(3).Select(e => e?.Message)));
            }

            var wantedNames = new HashSet<string>(wanted.Select(w => w.Name), StringComparer.OrdinalIgnoreCase);
            foreach (Type type in types)
            {
                if (type.IsAbstract || type.IsInterface || !type.IsClass)
                    continue;
                MethodInfo? execute = FindExecute(type);
                PropertyInfo? nameProp = type.GetProperty("CommandName", BindingFlags.Public | BindingFlags.Instance);
                if (execute == null || nameProp == null || nameProp.PropertyType != typeof(string))
                    continue;

                // CommandName is an instance property, so the command has to be created to read it.
                object instance;
                try
                {
                    instance = CreateInstance(type, uiApp);
                }
                catch (Exception ex)
                {
                    McpLog.Warn("Could not create " + type.FullName + ": " + (ex.InnerException ?? ex).Message);
                    continue;
                }

                string? commandName = nameProp.GetValue(instance) as string;
                if (string.IsNullOrEmpty(commandName) || !wantedNames.Contains(commandName!))
                    continue;
                if (_commands.ContainsKey(commandName!))
                    continue;

                McpCommandInfo info = wanted.First(w => string.Equals(w.Name, commandName, StringComparison.OrdinalIgnoreCase));
                _commands[commandName!] = new LoadedCommand(commandName!, info.SetName, info.Description,
                    (parameters, requestId) => Invoke(instance, execute, parameters, requestId));
            }

            foreach (McpCommandInfo missing in wanted.Where(w => !_commands.ContainsKey(w.Name)))
                McpLog.Warn("Command " + missing.Name + " not found in " + Path.GetFileName(dllPath));
        }

        private static MethodInfo? FindExecute(Type type)
        {
            foreach (MethodInfo m in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "Execute")
                    continue;
                ParameterInfo[] p = m.GetParameters();
                if (p.Length == 2 && p[0].ParameterType.FullName == "Newtonsoft.Json.Linq.JObject" && p[1].ParameterType == typeof(string))
                    return m;
            }
            return null;
        }

        private static object CreateInstance(Type type, UIApplication uiApp)
        {
            ConstructorInfo? withApp = type.GetConstructor(new[] { typeof(UIApplication) });
            if (withApp != null)
                return withApp.Invoke(new object[] { uiApp });

            object instance = Activator.CreateInstance(type)!;
            MethodInfo? init = type.GetMethod("Initialize", new[] { typeof(UIApplication) });
            init?.Invoke(instance, new object[] { uiApp });
            return instance;
        }

        /// <summary>Invokes Execute(JObject, string), even when the command set binds to another Newtonsoft.Json copy.</summary>
        private static object? Invoke(object instance, MethodInfo execute, JObject parameters, string requestId)
        {
            Type paramType = execute.GetParameters()[0].ParameterType;
            object arg = parameters;
            if (!paramType.IsInstanceOfType(parameters))
            {
                MethodInfo? parse = paramType.GetMethod("Parse", new[] { typeof(string) });
                if (parse == null)
                    throw new InvalidOperationException("Foreign JObject type has no Parse method");
                arg = parse.Invoke(null, new object[] { parameters.ToString(Formatting.None) })!;
            }
            try
            {
                return execute.Invoke(instance, new[] { arg, requestId });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        // ── Execution (called from socket threads) ─────────────────────────────

        public bool Contains(string name) => _commands.ContainsKey(name);

        /// <summary>Runs a command and returns its result as a JToken of the host Newtonsoft.Json.</summary>
        public JToken Execute(string name, JObject parameters, string requestId)
        {
            if (!_commands.TryGetValue(name, out LoadedCommand? command))
                throw new KeyNotFoundException("Method " + name + " not found");
            object? result = command.Execute(parameters, requestId);
            return ToHostToken(result);
        }

        private static JToken ToHostToken(object? result)
        {
            if (result == null)
                return JValue.CreateNull();
            if (result is JToken token)
                return token;
            string? ns = result.GetType().Namespace;
            if (ns != null && ns.StartsWith("Newtonsoft.Json.Linq", StringComparison.Ordinal))
                return JToken.Parse(result.ToString() ?? "null"); // JToken from another Newtonsoft copy
            return JToken.FromObject(result);
        }

        // ── Built-in commands ──────────────────────────────────────────────────

        private void RegisterBuiltIns(UIApplication uiApp)
        {
            string pluginVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
            _commands["ping"] = new LoadedCommand("ping", "APG Revit Plugins", "Connection test",
                (p, id) => new JObject
                {
                    ["ok"] = true,
                    ["revitVersion"] = _revitVersion,
                    ["pluginVersion"] = pluginVersion,
                    ["commands"] = _commands.Count,
                    ["document"] = uiApp.ActiveUIDocument?.Document?.Title
                });

            _commands["search_modules"] = new LoadedCommand("search_modules", "APG Revit Plugins", "List available commands",
                (p, id) =>
                {
                    string keyword = (p.Value<string>("keyword") ?? "").Trim();
                    var modules = new JArray();
                    foreach (LoadedCommand c in _commands.Values.OrderBy(c => c.Name))
                    {
                        if (c.Name == "search_modules" || c.Name == "use_module")
                            continue;
                        if (keyword.Length > 0 &&
                            c.Name.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0 &&
                            c.Description.IndexOf(keyword, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        modules.Add(new JObject { ["name"] = c.Name, ["description"] = c.Description, ["commandSet"] = c.SetName });
                    }
                    return new JObject { ["success"] = true, ["count"] = modules.Count, ["modules"] = modules };
                });

            _commands["use_module"] = new LoadedCommand("use_module", "APG Revit Plugins", "Execute a command by name",
                (p, id) =>
                {
                    string? moduleName = p.Value<string>("moduleName");
                    if (string.IsNullOrWhiteSpace(moduleName))
                        throw new ArgumentException("moduleName is required");
                    if (moduleName == "use_module" || !_commands.TryGetValue(moduleName!, out LoadedCommand? target))
                        throw new KeyNotFoundException("Module " + moduleName + " is not available. Use search_modules to list modules.");
                    JObject args = p["parameters"] as JObject ?? new JObject();
                    return target.Execute(args, id);
                });
        }
    }
}
