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
    /// Downloads the software of one PLC in a project. Normally to a PLCSIM Advanced instance, and only there;
    /// to a real PLC only through <see cref="RunReal"/> with its guards. Two checks have
    /// to pass first: the PLC's IP address in the project is the PLCSIM address given when the server was started,
    /// and the download goes through a PC interface whose name contains "PLCSIM" (the PLCSIM virtual adapter).
    /// Software only, never hardware configuration. 
    /// </summary>
    internal sealed class PlcsimDownload
    {
        private readonly TextWriter _log;
        private bool _real;
        public PlcsimDownload(TextWriter log) { _log = log; }

        /// <summary>
        /// Guarded download to a real PLC (kassem's choice, 3 Nov 2026). Only to addresses listed on the TIA PC
        /// (<paramref name="allowed"/>), only after a person at the TIA PC types that address (<paramref name="ask"/>),
        /// only when the CPU is already in STOP (the "stop modules" dialog is answered with no action, so TIA refuses a
        /// running CPU), and the CPU is left in STOP afterwards. Software only. Every attempt is appended to <paramref name="logFile"/>.
        /// </summary>
        public int RunReal(FileInfo projectFile, string plcName, ICollection<string> allowed, Func<string, string> ask, string logFile, string requestedBy)
        {
            if (allowed == null || allowed.Count == 0) throw new InvalidOperationException("No real PLCs are allowed on this TIA PC. Start serve with --allow-real <address,...>.");
            _real = true;
            var outcome = "not started";
            string target = "";
            try
            {
                var code = Download(projectFile, plcName, plc =>
                {
                    var ip = plc.IpAddresses.FirstOrDefault(a => allowed.Contains(a));
                    if (ip == null)
                        throw new InvalidOperationException($"Refused: {plc.Software.Name} has address {string.Join(", ", plc.IpAddresses)}, which is not in the allowed list ({string.Join(", ", allowed)}).");
                    target = $"{plc.Software.Name} at {ip}";
                    var typed = ask($"REAL PLC download: project {projectFile.Name}, {plc.Software.Name} at {ip}. The CPU must be in STOP and stays in STOP.\nType {ip} to download, anything else cancels: ");
                    if ((typed ?? "").Trim() != ip) throw new InvalidOperationException("Cancelled: the address was not confirmed at the TIA PC.");
                }, pc => pc.IndexOf("PLCSIM", StringComparison.OrdinalIgnoreCase) < 0);
                outcome = code == 0 ? "OK" : "failed (see log)";
                return code;
            }
            catch (Exception ex) { outcome = "refused: " + ex.Message; throw; }
            finally
            {
                try
                {
                    File.AppendAllText(logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}\t{Environment.UserName}@{Environment.MachineName}\trequested by {requestedBy}\t{projectFile.FullName}\t{target}\t{outcome}{Environment.NewLine}");
                }
                catch (Exception ex) { _log.WriteLine("Could not write the download log: " + ex.Message); }
            }
        }

        public int Run(FileInfo projectFile, string plcName, string plcsimIp)
        {
            if (string.IsNullOrWhiteSpace(plcsimIp)) throw new InvalidOperationException("No PLCSIM address configured: start the server with --plcsim-ip.");
            _real = false;
            return Download(projectFile, plcName, plc =>
            {
                // Check 1: the project's PLC address is the PLCSIM instance
                if (!plc.IpAddresses.Contains(plcsimIp))
                    throw new InvalidOperationException($"Refused: {plc.Software.Name} has address {string.Join(", ", plc.IpAddresses)} in the project, not the PLCSIM address {plcsimIp}. Use the real-PLC download for real PLCs.");
            }, pc => pc.IndexOf("PLCSIM", StringComparison.OrdinalIgnoreCase) >= 0);   // Check 2: through the PLCSIM virtual adapter only
        }

        private int Download(FileInfo projectFile, string plcName, Action<PlcInfo> checkTarget, Func<string, bool> pcInterfaceAllowed)
        {
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
                    checkTarget(plc);

                    var provider = plc.Item.GetService<DownloadProvider>();
                    if (provider == null) throw new InvalidOperationException("This CPU offers no download through Openness.");

                    ConfigurationTargetInterface target = null;
                    foreach (ConfigurationMode mode in provider.Configuration.Modes)
                    {
                        foreach (ConfigurationPcInterface pc in mode.PcInterfaces)
                        {
                            _log.WriteLine($"  interface {mode.Name} / {pc.Name}");
                            if (target == null && pcInterfaceAllowed(pc.Name))
                                foreach (ConfigurationTargetInterface t in pc.TargetInterfaces) { target = t; break; }
                        }
                    }
                    if (target == null) throw new InvalidOperationException(_real
                        ? "No usable PC interface to the real PLC (PLCSIM adapters are not used for real downloads)."
                        : "Refused: no PC interface with PLCSIM in its name. Is PLCSIM Advanced running with its virtual adapter?");
                    _log.WriteLine($"Downloading software to {(_real ? "the REAL PLC" : "PLCSIM")} through {target.Name}...");

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
        /// Answers TIA's download dialogs: download consistently, overwrite and accept the differences; PLCSIM is stopped
        /// and started again, a real PLC is never stopped (refused if running) and never started (left in STOP).
        /// The answer is picked by name from each dialog's choices, so a renamed option shows up in the log instead of
        /// breaking the build. A password prompt is not answered: the download then fails with a message.
        /// </summary>
        private static readonly string[] Usual = { "ConsistentDownload", "Overwrite", "AcceptAll", "Download", "Continue" };

        private void Pre(DownloadConfiguration config)
        {
            if (config.GetType().Name.IndexOf("StopModules", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // TIA only asks this when the CPU runs. A real PLC must already be in STOP, so that is a refusal.
                if (_real) throw new InvalidOperationException("Refused: the CPU is running. Put it in STOP yourself at the machine, then try again.");
                Answer(config, "StopAll");
                return;
            }
            Answer(config, Usual);
        }

        private void Post(DownloadConfiguration config)
        {
            if (config.GetType().Name.IndexOf("StartModules", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                Answer(config, _real ? "NoAction" : "StartModule");
                return;
            }
            Answer(config, Usual);
        }

        /// <returns>true when one of the preferred answers was chosen</returns>
        private bool Answer(DownloadConfiguration config, params string[] preferred)
        {
            var type = config.GetType();
            var prop = type.GetProperty("CurrentSelection", BindingFlags.Public | BindingFlags.Instance);
            if (prop != null && prop.PropertyType.IsEnum && prop.CanWrite)
            {
                var names = Enum.GetNames(prop.PropertyType);
                var pick = preferred.Select(p => names.FirstOrDefault(n => n.Equals(p, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(n => n != null);
                if (pick != null) prop.SetValue(config, Enum.Parse(prop.PropertyType, pick));
                _log.WriteLine($"  {type.Name}: {(pick ?? "left as " + prop.GetValue(config))} (choices {string.Join(", ", names)})");
                return pick != null;
            }
            var check = type.GetProperty("Checked", BindingFlags.Public | BindingFlags.Instance);
            if (check != null && check.PropertyType == typeof(bool) && check.CanWrite)
            {
                check.SetValue(config, true);
                _log.WriteLine($"  {type.Name}: checked");
                return true;
            }
            _log.WriteLine($"  {type.Name}: no answer ({config.Message})");
            return false;
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
