using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaImporter
{
    /// <summary>
    /// Writes the HMI part of the project (WinCC panels in the same TIA project) so PLC-Ki speaks the operator's
    /// language: which values an operator can change on which screen, the texts next to them, the buttons, the text
    /// lists that turn numbers into words, and the HMI tags with the PLC tag behind each.
    ///
    /// WinCC Comfort/Advanced ("classic", e.g. TP1200 Comfort): tag tables, screens, screen templates, pop-up screens
    /// and text lists as SimaticML XML, under HMI\&lt;name&gt;\. Classic Openness has no access to HMI alarms (Siemens
    /// does not offer it); the alarm texts then come from the PLC program.
    /// WinCC Unified: tags, alarms, text lists and screens read through the object model into unified.json.
    ///
    /// Everything goes through reflection on the Openness objects, so this builds against Siemens.Engineering.dll
    /// alone and an HMI that lacks something (or an Openness version that names it differently) is just skipped.
    /// Read-only towards the project.
    /// </summary>
    internal static class HmiExport
    {
        public static List<object> Write(Project project, DirectoryInfo outDir, TextWriter log, Action<string, string, Exception> problem)
        {
            var result = new List<object>();
            var found = new List<(string Device, DeviceItem Item, object Software)>();
            foreach (Device d in project.Devices) Walk(d, found);
            foreach (DeviceUserGroup g in project.DeviceGroups) WalkGroup(g, found);
            if (found.Count == 0) return result;

            var root = outDir.CreateSubdirectory("HMI");
            foreach (var (device, item, software) in found)
            {
                var typeName = software.GetType().FullName ?? "";
                var name = Str(software, "Name") ?? device;
                var unified = typeName.IndexOf("Unified", StringComparison.OrdinalIgnoreCase) >= 0;
                log.WriteLine($"HMI {name} ({(unified ? "WinCC Unified" : "WinCC Comfort/Advanced")}, {Safe(() => item.TypeIdentifier)})");
                var dir = root.CreateSubdirectory(Exporter.Safe(name));
                var counts = new Dictionary<string, int>();
                try
                {
                    if (unified) WriteUnified(software, dir, counts, log);
                    else WriteClassic(software, dir, counts, log, (kind, n, ex) => problem("hmi-" + kind, name + "/" + n, ex));
                }
                catch (Exception ex) { problem("hmi", name, ex); }
                var info = new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["device"] = device,
                    ["typeIdentifier"] = Safe(() => item.TypeIdentifier),
                    ["kind"] = unified ? "unified" : "classic",
                    ["folder"] = "HMI/" + dir.Name,
                    ["counts"] = counts,
                };
                File.WriteAllText(Path.Combine(dir.FullName, "hmi.json"), JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
                result.Add(info);
            }
            return result;
        }

        private static void WalkGroup(DeviceUserGroup group, List<(string, DeviceItem, object)> found)
        {
            foreach (Device d in group.Devices) Walk(d, found);
            foreach (DeviceUserGroup sub in group.Groups) WalkGroup(sub, found);
        }

        private static void Walk(Device device, List<(string, DeviceItem, object)> found)
        {
            foreach (DeviceItem item in device.DeviceItems) WalkItem(device, item, found, 0);
        }

        private static void WalkItem(Device device, DeviceItem item, List<(string, DeviceItem, object)> found, int depth)
        {
            if (depth > 6) return;
            SoftwareContainer container = null;
            try { container = item.GetService<SoftwareContainer>(); } catch (Exception) { /* no software here */ }
            var sw = container?.Software;
            if (sw != null && !(sw is PlcSoftware) && (sw.GetType().FullName ?? "").IndexOf("Hmi", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                found.Add((device.Name, item, sw));
                return;
            }
            foreach (DeviceItem sub in item.DeviceItems) WalkItem(device, sub, found, depth + 1);
        }

        // ---------------------------------------------------------------- classic (Comfort / Advanced)

        private static void WriteClassic(object target, DirectoryInfo dir, Dictionary<string, int> counts, TextWriter log, Action<string, string, Exception> problem)
        {
            log.WriteLine("  HMI tags...");
            counts["tagTables"] = ExportFolder(Get(target, "TagFolder"), "TagTables", dir.CreateSubdirectory("Tags"), problem, "tag-table");
            log.WriteLine("  HMI screens...");
            counts["screens"] = ExportFolder(Get(target, "ScreenFolder"), "Screens", dir.CreateSubdirectory("Screens"), problem, "screen");
            counts["templates"] = ExportFolder(Get(target, "ScreenTemplateFolder"), "ScreenTemplates", dir.CreateSubdirectory("Templates"), problem, "template");
            counts["popups"] = ExportFolder(Get(target, "ScreenPopupFolder"), "ScreenPopups", dir.CreateSubdirectory("Popups"), problem, "popup");
            log.WriteLine("  HMI text lists...");
            // Graphic lists are left out on purpose: exporting them has crashed TIA Portal.
            counts["textLists"] = ExportItems(Get(target, "TextLists"), dir.CreateSubdirectory("TextLists"), problem, "text-list");
            log.WriteLine($"  {counts["tagTables"]} tag tables, {counts["screens"]} screens, {counts["templates"]} templates, {counts["popups"]} pop-ups, {counts["textLists"]} text lists.");
        }

        /// <summary>Exports the items of <paramref name="itemsProperty"/> in this folder and all sub folders, keeping the folder names.</summary>
        private static int ExportFolder(object folder, string itemsProperty, DirectoryInfo dir, Action<string, string, Exception> problem, string kind)
        {
            if (folder == null) return 0;
            var n = ExportItems(Get(folder, itemsProperty), dir, problem, kind);
            foreach (var sub in Items(Get(folder, "Folders")))
                n += ExportFolder(sub, itemsProperty, dir.CreateSubdirectory(Exporter.Safe(Str(sub, "Name") ?? "folder")), problem, kind);
            return n;
        }

        private static int ExportItems(object composition, DirectoryInfo dir, Action<string, string, Exception> problem, string kind)
        {
            var n = 0;
            // Materialised first: exporting while an Openness enumerator is open can dispose it
            foreach (var item in Items(composition))
            {
                var name = Str(item, "Name") ?? ("item" + n);
                try
                {
                    var export = item.GetType().GetMethod("Export", new[] { typeof(FileInfo), typeof(ExportOptions) });
                    if (export == null) continue;
                    export.Invoke(item, new object[] { new FileInfo(Path.Combine(dir.FullName, Exporter.Safe(name) + ".xml")), ExportOptions.WithDefaults });
                    n++;
                }
                catch (Exception ex) { problem(kind, name, ex is TargetInvocationException t && t.InnerException != null ? t.InnerException : ex); }
            }
            return n;
        }

        // ---------------------------------------------------------------- WinCC Unified

        private static void WriteUnified(object software, DirectoryInfo dir, Dictionary<string, int> counts, TextWriter log)
        {
            var doc = new Dictionary<string, object>();
            var tags = new List<object>();
            foreach (var table in Items(Get(software, "TagTables")))
                foreach (var t in Items(Get(table, "Tags")))
                {
                    var o = Dump(t, 0);
                    o["table"] = Str(table, "Name");
                    tags.Add(o);
                }
            if (tags.Count == 0) tags.AddRange(Items(Get(software, "Tags")).Select(t => (object)Dump(t, 0)));
            doc["tags"] = tags;
            doc["discreteAlarms"] = Items(Get(software, "DiscreteAlarms")).Select(a => (object)Dump(a, 0)).ToList();
            doc["analogAlarms"] = Items(Get(software, "AnalogAlarms")).Select(a => (object)Dump(a, 0)).ToList();
            doc["textLists"] = Items(Get(software, "TextLists")).Select(l =>
            {
                var o = Dump(l, 0);
                o["entries"] = Items(Get(l, "Entries")).Select(e => (object)Dump(e, 0)).ToList();
                return (object)o;
            }).ToList();
            var screens = new List<object>();
            foreach (var s in Items(Get(software, "Screens")))
            {
                var o = Dump(s, 0);
                o["items"] = Items(Get(s, "ScreenItems")).Select(i => (object)Dump(i, 1)).ToList();
                screens.Add(o);
            }
            doc["screens"] = screens;
            counts["tags"] = tags.Count;
            counts["discreteAlarms"] = ((List<object>)doc["discreteAlarms"]).Count;
            counts["analogAlarms"] = ((List<object>)doc["analogAlarms"]).Count;
            counts["textLists"] = ((List<object>)doc["textLists"]).Count;
            counts["screens"] = screens.Count;
            log.WriteLine($"  {counts["tags"]} tags, {counts["discreteAlarms"] + counts["analogAlarms"]} alarms, {counts["screens"]} screens, {counts["textLists"]} text lists.");
            File.WriteAllText(Path.Combine(dir.FullName, "unified.json"), JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>
        /// The simple properties of an Openness object (text, numbers, yes/no, choices, linked object names,
        /// multilingual texts), and with <paramref name="deep"/> &gt; 0 its dynamizations and events, for the tag links.
        /// </summary>
        private static Dictionary<string, object> Dump(object o, int deep)
        {
            var d = new Dictionary<string, object> { ["$type"] = o.GetType().Name };
            foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0 || p.Name == "Parent") continue;
                object v;
                try { v = p.GetValue(o); } catch (Exception) { continue; }
                if (v == null) continue;
                var t = v.GetType();
                if (v is string s) { if (s.Length > 0) d[p.Name] = s; }
                else if (t.IsPrimitive || v is decimal) d[p.Name] = v;
                else if (t.IsEnum) d[p.Name] = v.ToString();
                else if (t.Name == "MultilingualText") { var x = FirstText(v); if (x != null) d[p.Name] = x; }
                else if (v is IEnumerable && deep > 0 && (p.Name == "Dynamizations" || p.Name == "EventHandlers" || p.Name == "Properties"))
                    d[p.Name] = Items(v).Take(50).Select(x => (object)Dump(x, deep - 1)).ToList();
                else if (!(v is IEnumerable) && Get(v, "Name") is string linked && linked.Length > 0 && p.Name != "Name")
                    d[p.Name] = linked; // a link to another object (tag, connection, alarm class): its name
            }
            return d;
        }

        private static string FirstText(object multilingual)
        {
            foreach (var item in Items(Get(multilingual, "Items")))
                if (Get(item, "Text") is string s && !string.IsNullOrWhiteSpace(s)) return s;
            return null;
        }

        // ---------------------------------------------------------------- reflection helpers

        private static object Get(object o, string property)
        {
            if (o == null) return null;
            try { return o.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance)?.GetValue(o); }
            catch (Exception) { return null; }
        }

        private static string Str(object o, string property) => Get(o, property) as string;

        private static List<object> Items(object composition)
        {
            var list = new List<object>();
            if (composition is IEnumerable e && !(composition is string))
            {
                try { foreach (var x in e) if (x != null) list.Add(x); }
                catch (Exception) { /* a composition this HMI does not support */ }
            }
            return list;
        }

        private static string Safe(Func<string> f)
        {
            try { return f(); } catch (Exception) { return null; }
        }
    }
}
