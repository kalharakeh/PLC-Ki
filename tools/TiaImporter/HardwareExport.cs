using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;

namespace TiaImporter
{
    /// <summary>
    /// Writes hardware.json: every device of the project (PLC stations, ET 200 stations, drives, robots, other
    /// PROFINET devices) with its modules, slots, order numbers, firmware, I/O addresses, channels and network
    /// addresses. PLC-Ki draws the rack from it and matches the channels to the PLC tags by address.
    /// Read-only towards the project. Every attribute is read on its own: a device that lacks one (GSD devices
    /// have fewer) just leaves it out, so one odd device never stops the export.
    /// </summary>
    internal static class HardwareExport
    {
        public static int Write(Project project, string file, TextWriter log)
        {
            var devices = new List<object>();
            var seen = new HashSet<Device>();
            void Add(Device d)
            {
                if (d == null || !seen.Add(d)) return;
                try { devices.Add(DeviceInfo(d)); }
                catch (Exception ex) { log.WriteLine($"  hardware: {d.Name}: {ex.Message}"); }
            }
            foreach (Device d in project.Devices) Add(d);
            foreach (DeviceUserGroup g in project.DeviceGroups) Walk(g, Add);
            try { foreach (Device d in project.UngroupedDevicesGroup.Devices) Add(d); }
            catch (Exception) { /* older Openness: ungrouped devices are in project.Devices */ }

            var doc = new Dictionary<string, object>
            {
                ["format"] = "plcki-hardware-1",
                ["project"] = project.Name,
                ["exportedAt"] = DateTime.UtcNow.ToString("o"),
                ["devices"] = devices,
            };
            File.WriteAllText(file, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
            log.WriteLine($"Hardware: {devices.Count} device(s) written to {Path.GetFileName(file)}.");
            return devices.Count;
        }

        private static void Walk(DeviceUserGroup group, Action<Device> add)
        {
            foreach (Device d in group.Devices) add(d);
            foreach (DeviceUserGroup sub in group.Groups) Walk(sub, add);
        }

        private static Dictionary<string, object> DeviceInfo(Device device)
        {
            var d = new Dictionary<string, object>
            {
                ["name"] = device.Name,
                ["typeIdentifier"] = Safe(() => device.TypeIdentifier),
            };
            Put(d, "typeName", Attr(device, "TypeName"));
            Put(d, "comment", Comment(device));
            d["items"] = device.DeviceItems.Cast<DeviceItem>().Select(i => ItemInfo(i, 0)).Where(x => x != null).ToList();
            return d;
        }

        private static Dictionary<string, object> ItemInfo(DeviceItem item, int depth)
        {
            if (depth > 6) return null;
            var o = new Dictionary<string, object>
            {
                ["name"] = item.Name,
                ["typeIdentifier"] = Safe(() => item.TypeIdentifier),
            };
            Put(o, "position", Safe(() => (object)item.PositionNumber));
            Put(o, "orderNumber", Attr(item, "OrderNumber"));
            Put(o, "firmware", Attr(item, "FirmwareVersion"));
            Put(o, "typeName", Attr(item, "TypeName"));
            Put(o, "classification", Safe(() => item.Classification.ToString()));
            Put(o, "comment", Comment(item));
            Put(o, "builtIn", Attr(item, "IsBuiltIn"));

            var addresses = new List<object>();
            try
            {
                foreach (Address a in item.Addresses)
                {
                    var x = new Dictionary<string, object>();
                    Put(x, "io", Attr(a, "IoType")?.ToString());
                    Put(x, "start", Attr(a, "StartAddress"));
                    Put(x, "length", Attr(a, "Length"));
                    if (x.Count > 0) addresses.Add(x);
                }
            }
            catch (Exception) { /* no addresses on this item */ }
            if (addresses.Count > 0) o["addresses"] = addresses;

            var channels = new List<object>();
            try
            {
                foreach (Channel c in item.Channels)
                {
                    var x = new Dictionary<string, object>();
                    Put(x, "type", Attr(c, "Type")?.ToString());
                    Put(x, "io", Attr(c, "IoType")?.ToString());
                    Put(x, "number", Attr(c, "Number"));
                    if (x.Count > 0) channels.Add(x);
                }
            }
            catch (Exception) { /* no channels on this item */ }
            if (channels.Count > 0) o["channels"] = channels;

            var nic = Safe(() => item.GetService<NetworkInterface>());
            if (nic != null)
            {
                var nodes = new List<object>();
                try
                {
                    foreach (Node n in nic.Nodes)
                    {
                        var x = new Dictionary<string, object>();
                        Put(x, "name", Safe(() => n.Name));
                        Put(x, "type", Attr(n, "NodeType")?.ToString());
                        Put(x, "address", Attr(n, "Address"));
                        Put(x, "pnName", Attr(n, "PnDeviceName"));
                        Put(x, "subnet", Safe(() => n.ConnectedSubnet?.Name));
                        nodes.Add(x);
                    }
                }
                catch (Exception) { /* no nodes */ }
                if (nodes.Count > 0) o["nodes"] = nodes;
                try
                {
                    var sys = nic.IoConnectors.Cast<IoConnector>().Select(c => Safe(() => c.ConnectedToIoSystem?.Name)).FirstOrDefault(s => s != null);
                    Put(o, "ioSystem", sys);
                    var own = nic.IoControllers.Cast<IoController>().Select(c => Safe(() => c.IoSystem?.Name)).FirstOrDefault(s => s != null);
                    Put(o, "controlsIoSystem", own);
                }
                catch (Exception) { /* not a PROFINET interface */ }
            }

            var subs = item.DeviceItems.Cast<DeviceItem>().Select(i => ItemInfo(i, depth + 1)).Where(x => x != null).ToList();
            if (subs.Count > 0) o["items"] = subs;
            return o;
        }

        private static string Comment(IEngineeringObject o)
        {
            var c = Attr(o, "Comment");
            if (c is MultilingualText mt)
            {
                foreach (MultilingualTextItem i in mt.Items)
                    if (!string.IsNullOrWhiteSpace(i.Text)) return i.Text;
                return null;
            }
            return c as string;
        }

        /// <summary>One attribute, or null when this object does not have it.</summary>
        private static object Attr(IEngineeringObject o, string name)
        {
            try { return o.GetAttribute(name); }
            catch (Exception) { return null; }
        }

        private static T Safe<T>(Func<T> f) where T : class
        {
            try { return f(); }
            catch (Exception) { return null; }
        }

        private static void Put(Dictionary<string, object> d, string key, object value)
        {
            if (value == null) return;
            if (value is string s && s.Length == 0) return;
            d[key] = value is Enum ? value.ToString() : value;
        }
    }
}
