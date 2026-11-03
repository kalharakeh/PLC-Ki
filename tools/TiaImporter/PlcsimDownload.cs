using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Siemens.Engineering;
using Siemens.Engineering.Connection;
using Siemens.Engineering.Download;
using Siemens.Engineering.Download.Configurations;
using Siemens.Engineering.HW;

namespace TiaImporter
{
    /// <summary>
    /// Downloads the software of one PLC in a project to a PLCSIM Advanced instance, and only there. Two checks have
    /// to pass first: the PLC's IP address in the project is the PLCSIM address given when the server was started,
    /// and the download goes through a PC interface whose name contains "PLCSIM" (the PLCSIM virtual adapter).
    /// Software only, never hardware configuration. A real PLC is never a target.
    /// </summary>
    internal sealed class PlcsimDownload
    {
        private readonly TextWriter _log;
        public PlcsimDownload(TextWriter log) { _log = log; }

        public int Run(FileInfo projectFile, string plcName, string plcsimIp)
        {
            if (string.IsNullOrWhiteSpace(plcsimIp)) throw new InvalidOperationException("No PLCSIM address configured: start the server with --plcsim-ip.");
            _log.WriteLine("Starting TIA Portal V20 without user interface...");
            using (var tia = new TiaPortal(TiaPortalMode.WithoutUserInterface))
            {
                var project = tia.Projects.Open(projectFile);
                try
                {
                    var plcs = TiaHelpers.FindPlcs(project);
                    var plc = plcName == null ? (plcs.Count == 1 ? plcs[0] : null) : plcs.FirstOrDefault(p => p.Software.Name == plcName || p.DeviceName == plcName);
                    if (plc == null) throw new InvalidOperationException($"Choose the PLC: {string.Join(", ", plcs.Select(p => p.Software.Name))}");
                    _log.WriteLine($"Project {project.Name}, PLC {plc.Software.Name}, address(es) {string.Join(", ", plc.IpAddresses)}");

                    // Check 1: the project's PLC address is the PLCSIM instance
                    if (!plc.IpAddresses.Contains(plcsimIp))
                        throw new InvalidOperationException($"Refused: {plc.Software.Name} has address {string.Join(", ", plc.IpAddresses)} in the project, not the PLCSIM address {plcsimIp}. PLC-Ki only downloads to PLCSIM.");

                    var provider = plc.Item.GetService<DownloadProvider>();
                    if (provider == null) throw new InvalidOperationException("This CPU offers no download through Openness.");

                    // Check 2: through the PLCSIM virtual adapter only
                    ConfigurationTargetInterface target = null;
                    foreach (ConfigurationMode mode in provider.Configuration.Modes)
                    {
                        foreach (ConfigurationPcInterface pc in mode.PcInterfaces)
                        {
                            _log.WriteLine($"  interface {mode.Name} / {pc.Name}");
                            if (target == null && pc.Name.IndexOf("PLCSIM", StringComparison.OrdinalIgnoreCase) >= 0)
                                foreach (ConfigurationTargetInterface t in pc.TargetInterfaces) { target = t; break; }
                        }
                    }
                    if (target == null) throw new InvalidOperationException("Refused: no PC interface with PLCSIM in its name. Is PLCSIM Advanced running with its virtual adapter?");
                    _log.WriteLine($"Downloading software to PLCSIM through {target.Name}...");

                    var result = provider.Download(target, Pre, Post, DownloadOptions.Software);
                    _log.WriteLine($"Download: {result.State}, {result.ErrorCount} error(s), {result.WarningCount} warning(s)");
                    Print(result.Messages, "  ");
                    return result.ErrorCount == 0 ? 0 : 3;
                }
                finally
                {
                    project.Close();   // the download does not change the project; nothing to save
                }
            }
        }

        /// <summary>
        /// Answers TIA's download dialogs: stop the (simulated) CPU, download consistently, overwrite and accept the
        /// differences. The answer is picked by name from each dialog's choices, so a renamed option shows up in the log
        /// instead of breaking the build. A password prompt is not answered: the download then fails with a message.
        /// </summary>
        private void Pre(DownloadConfiguration config) => Answer(config, "StopAll", "ConsistentDownload", "Overwrite", "AcceptAll", "Download", "Continue");
        private void Post(DownloadConfiguration config) => Answer(config, "StartModule", "Start");

        private void Answer(DownloadConfiguration config, params string[] preferred)
        {
            var type = config.GetType();
            var prop = type.GetProperty("CurrentSelection", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.PropertyType.IsEnum && prop.CanWrite)
            {
                var names = Enum.GetNames(prop.PropertyType);
                var pick = preferred.Select(p => names.FirstOrDefault(n => n.Equals(p, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => n != null);
                if (pick != null) prop.SetValue(config, Enum.Parse(prop.PropertyType, pick));
                _log.WriteLine($"  {type.Name}: {(pick ?? "left as " + prop.GetValue(config))} (choices {string.Join(", ", names)})");
                return;
            }
            var check = type.GetProperty("Checked", BindingFlags.Public | BindingFlags.Instance);
            if (check != null && check.PropertyType == typeof(bool) && check.CanWrite)
            {
                check.SetValue(config, true);
                _log.WriteLine($"  {type.Name}: checked");
                return;
            }
            _log.WriteLine($"  {type.Name}: no answer ({config.Message})");
        }

        private void Print(DownloadResultMessageComposition messages, string indent)
        {
            foreach (DownloadResultMessage m in messages)
            {
                if (!string.IsNullOrWhiteSpace(m.Message)) _log.WriteLine($"{indent}{m.State}: {m.Message}");
                Print(m.Messages, indent + "  ");
            }
        }
    }
}
