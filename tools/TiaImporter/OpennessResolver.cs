using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace TiaImporter
{
    /// <summary>
    /// Openness assemblies live in the TIA Portal install folder and must not be copied next to the exe.
    /// This resolver finds them there when .NET asks for them.
    /// </summary>
    internal static class OpennessResolver
    {
        public static string InstallDir =
            Environment.GetEnvironmentVariable("TIA_INSTALL_DIR")
            ?? @"C:\Program Files\Siemens\Automation\Portal V20";

        public static void Register()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var name = new AssemblyName(args.Name).Name + ".dll";
                if (!name.StartsWith("Siemens.Engineering", StringComparison.OrdinalIgnoreCase))
                    return null;

                var publicApi = Path.Combine(InstallDir, "PublicAPI", "V20");
                var bin = Path.Combine(InstallDir, "Bin", "PublicAPI");
                foreach (var dir in new[] { publicApi, bin }.Where(Directory.Exists))
                {
                    var hit = Directory.EnumerateFiles(dir, name, SearchOption.AllDirectories)
                        .OrderBy(p => p.Contains("net48") ? 0 : 1)
                        .FirstOrDefault();
                    if (hit != null) return Assembly.LoadFrom(hit);
                }
                return null;
            };
        }
    }
}
