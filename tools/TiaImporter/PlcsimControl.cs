using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TiaImporter
{
    /// <summary>
    /// Reads, and on request switches, the operating mode (RUN/STOP) of the PLCSIM Advanced instance that has the
    /// PLCSIM address serve was started with (--plcsim-ip). Never any other CPU: the instance must report exactly
    /// that address, and a real PLC is not reachable through this API at all.
    ///
    /// The PLCSIM Advanced API (Siemens.Simatic.Simulation.Runtime.Api.x64.dll) is loaded at runtime from the
    /// PLCSIM install, through reflection, so TiaImporter still builds and runs on a PC without PLCSIM Advanced.
    /// </summary>
    internal static class PlcsimControl
    {
        private const string ApiFile = "Siemens.Simatic.Simulation.Runtime.Api.x64.dll";
        private static Assembly _api;
        private static string _apiError;
        private static readonly Dictionary<string, object> _instances = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        /// <summary>The API from the newest PLCSIM Advanced install (or PLCSIMADV_API_DIR), or null with the reason in _apiError.</summary>
        private static Assembly Api()
        {
            if (_api != null || _apiError != null) return _api;
            try
            {
                var dirs = new List<string>();
                var env = Environment.GetEnvironmentVariable("PLCSIMADV_API_DIR");
                if (!string.IsNullOrWhiteSpace(env)) dirs.Add(env);
                foreach (var root in new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles),
                }.Where(r => !string.IsNullOrEmpty(r)).Distinct())
                {
                    var api = Path.Combine(root, "Siemens", "PLCSIMADV", "API");
                    if (Directory.Exists(api))
                        dirs.AddRange(Directory.GetDirectories(api).OrderByDescending(d => Version(Path.GetFileName(d))));
                }
                var file = dirs.Select(d => Path.Combine(d, ApiFile)).FirstOrDefault(File.Exists);
                if (file == null) { _apiError = "PLCSIM Advanced is not installed on this PC (no " + ApiFile + ")."; return null; }
                _api = Assembly.LoadFrom(file);
                return _api;
            }
            catch (Exception ex)
            {
                _apiError = "Cannot load the PLCSIM Advanced API: " + ex.Message;
                return null;
            }
        }

        private static Version Version(string s) => System.Version.TryParse(s, out var v) ? v : new Version(0, 0);

        /// <summary>{found, name, state, ips, cpuType, leds} of the PLCSIM CPU at plcsimIp, or {found=false, error}.</summary>
        public static Dictionary<string, object> State(string plcsimIp)
        {
            try
            {
                var inst = Find(plcsimIp, out var error);
                if (inst == null) return new Dictionary<string, object> { ["found"] = false, ["error"] = error };
                return Describe(inst);
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["found"] = false, ["error"] = Inner(ex).Message };
            }
        }

        /// <summary>Puts the PLCSIM CPU in RUN ("run") or STOP ("stop"). Returns the new state.</summary>
        public static Dictionary<string, object> SetMode(string plcsimIp, string mode, TextWriter log)
        {
            if (mode != "run" && mode != "stop") throw new InvalidOperationException("Mode must be run or stop.");
            var inst = Find(plcsimIp, out var error) ?? throw new InvalidOperationException(error);
            var type = inst.GetType();
            var name = mode == "run" ? "Run" : "Stop";
            // Run(UInt32 timeout_ms) waits until the CPU is there; Run() returns at once
            var withTimeout = type.GetMethod(name, new[] { typeof(uint) });
            log.WriteLine($"{DateTime.Now:HH:mm:ss} PLCSIM {Get(inst, "Name")} ({plcsimIp}): {name.ToUpperInvariant()} requested by PLC-Ki.");
            try
            {
                if (withTimeout != null) withTimeout.Invoke(inst, new object[] { 15000u });
                else (type.GetMethod(name, Type.EmptyTypes) ?? throw new InvalidOperationException($"The PLCSIM API has no {name}().")).Invoke(inst, null);
            }
            catch (TargetInvocationException ex) { throw new InvalidOperationException($"PLCSIM refused {name}: {Inner(ex).Message}"); }
            return Describe(inst);
        }

        /// <summary>The instance whose controller has exactly this address; null with the reason otherwise.</summary>
        private static object Find(string plcsimIp, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(plcsimIp)) { error = "No PLCSIM address is set on the TIA helper (--plcsim-ip)."; return null; }
            var api = Api();
            if (api == null) { error = _apiError; return null; }
            var mgr = api.GetType("Siemens.Simatic.Simulation.Runtime.SimulationRuntimeManager", true);

            lock (_instances)
            {
                // A cached interface stays good while that instance runs; check it still has the address
                if (_instances.TryGetValue(plcsimIp, out var cached))
                {
                    try { if (HasIp(cached, plcsimIp)) return cached; }
                    catch (Exception) { /* instance gone */ }
                    _instances.Remove(plcsimIp);
                }

                var infos = (mgr.GetProperty("RegisteredInstanceInfo", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as Array)
                            ?? throw new InvalidOperationException("The PLCSIM API gave no instance list.");
                var names = new List<string>();
                var create = mgr.GetMethod("CreateInterface", new[] { typeof(string) })
                             ?? throw new InvalidOperationException("The PLCSIM API has no CreateInterface(name).");
                foreach (var info in infos)
                {
                    var name = Member(info, "Name") as string;
                    if (string.IsNullOrEmpty(name)) continue;
                    names.Add(name);
                    object inst;
                    try { inst = create.Invoke(null, new object[] { name }); }
                    catch (Exception) { continue; }
                    if (inst != null && HasIp(inst, plcsimIp))
                    {
                        _instances[plcsimIp] = inst;
                        return inst;
                    }
                }
                error = names.Count == 0
                    ? "No PLCSIM Advanced instance is running. Start one in the PLCSIM Advanced control panel."
                    : $"No running PLCSIM instance has the address {plcsimIp} (running: {string.Join(", ", names)}).";
                return null;
            }
        }

        private static bool HasIp(object inst, string ip) =>
            Get(inst, "ControllerIP") is Array a && a.Cast<object>().Any(x => string.Equals(x?.ToString(), ip, StringComparison.OrdinalIgnoreCase));

        private static Dictionary<string, object> Describe(object inst)
        {
            var d = new Dictionary<string, object>
            {
                ["found"] = true,
                ["name"] = Get(inst, "Name")?.ToString(),
                ["state"] = Get(inst, "OperatingState")?.ToString(),        // Off, Booting, Stop, Startup, Run, Freeze, ShuttingDown, Hold
                ["cpuType"] = Get(inst, "CPUType")?.ToString(),
                ["ips"] = (Get(inst, "ControllerIP") as Array)?.Cast<object>().Select(x => x?.ToString()).ToList(),
            };
            var leds = Leds(inst);
            if (leds.Count > 0) d["leds"] = leds;
            return d;
        }

        /// <summary>The front LEDs (RUN, STOP, ERROR, MAINT, ...) where this PLCSIM version can read them: name -> Off/On/FlashFast/FlashSlow.</summary>
        private static Dictionary<string, string> Leds(object inst)
        {
            var result = new Dictionary<string, string>();
            try
            {
                var read = inst.GetType().GetMethods().FirstOrDefault(m => m.Name.IndexOf("LED", StringComparison.OrdinalIgnoreCase) >= 0
                    && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType.IsEnum && m.ReturnType != typeof(void));
                if (read == null) return result;
                var kind = read.GetParameters()[0].ParameterType;
                foreach (var v in Enum.GetValues(kind))
                {
                    try { result[v.ToString()] = read.Invoke(inst, new[] { v })?.ToString(); }
                    catch (Exception) { /* this LED does not exist on this CPU */ }
                }
            }
            catch (Exception) { /* no LED read in this PLCSIM version */ }
            return result;
        }

        private static object Get(object o, string name)
        {
            try { return o.GetType().GetProperty(name)?.GetValue(o); }
            catch (Exception) { return null; }
        }

        private static object Member(object o, string name)
        {
            var t = o.GetType();
            var f = t.GetField(name);
            if (f != null) return f.GetValue(o);
            return t.GetProperty(name)?.GetValue(o);
        }

        private static Exception Inner(Exception ex) => ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex;
    }
}
