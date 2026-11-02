using System.Collections.Generic;
using Siemens.Engineering;
using Siemens.Engineering.HW;
using Siemens.Engineering.HW.Features;
using Siemens.Engineering.SW;

namespace TiaImporter
{
    internal sealed class PlcInfo
    {
        public string DeviceName;
        public string CpuName;
        public string TypeIdentifier;
        public List<string> IpAddresses = new List<string>();
        public PlcSoftware Software;
    }

    internal static class TiaHelpers
    {
        /// <summary>Finds every PLC in the project, including those inside device groups.</summary>
        public static List<PlcInfo> FindPlcs(Project project)
        {
            var result = new List<PlcInfo>();
            foreach (Device device in project.Devices) Walk(device, result);
            foreach (DeviceUserGroup group in project.DeviceGroups) WalkGroup(group, result);
            return result;
        }

        private static void WalkGroup(DeviceUserGroup group, List<PlcInfo> result)
        {
            foreach (Device device in group.Devices) Walk(device, result);
            foreach (DeviceUserGroup sub in group.Groups) WalkGroup(sub, result);
        }

        private static void Walk(Device device, List<PlcInfo> result)
        {
            foreach (DeviceItem item in device.DeviceItems) WalkItem(device, item, result);
        }

        private static void WalkItem(Device device, DeviceItem item, List<PlcInfo> result)
        {
            var container = item.GetService<SoftwareContainer>();
            if (container?.Software is PlcSoftware plc)
            {
                var info = new PlcInfo
                {
                    DeviceName = device.Name,
                    CpuName = item.Name,
                    TypeIdentifier = item.TypeIdentifier,
                    Software = plc,
                };
                CollectIps(item, info.IpAddresses);
                result.Add(info);
            }
            foreach (DeviceItem sub in item.DeviceItems) WalkItem(device, sub, result);
        }

        private static void CollectIps(DeviceItem item, List<string> ips)
        {
            var nic = item.GetService<NetworkInterface>();
            if (nic != null)
            {
                foreach (var node in nic.Nodes)
                {
                    try
                    {
                        if (node.GetAttribute("Address") is string ip && ip.Length > 0) ips.Add(ip);
                    }
                    catch (EngineeringException) { /* node without an IP (e.g. PROFIBUS) */ }
                }
            }
            foreach (DeviceItem sub in item.DeviceItems) CollectIps(sub, ips);
        }
    }
}
