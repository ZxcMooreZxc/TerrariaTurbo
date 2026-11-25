using System;
using System.IO;
using System.Reflection;

namespace TerrariaTurbo
{
    public static class TerrariaAssemblyResolver
    {
        private static string _terrariaDir;
        private static string _xnaFallbackDir;
        private static Assembly _terrariaAsm;
        private static bool _registered;

        public static void Register(string terrariaDir, string xnaFallbackDir = null)
        {
            _terrariaDir = terrariaDir;
            _xnaFallbackDir = xnaFallbackDir;
            if (_registered) return;
            _registered = true;

            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        }

        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            string shortName = new AssemblyName(args.Name).Name;
            string dllName = shortName + ".dll";

            // 1. Check already loaded assemblies
            foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(loaded.GetName().Name, shortName, StringComparison.OrdinalIgnoreCase))
                    return loaded;
            }

            // 2. Check Terraria executable
            if (string.Equals(shortName, "Terraria", StringComparison.OrdinalIgnoreCase))
            {
                string exePath = Path.Combine(_terrariaDir, "Terraria.exe");
                if (File.Exists(exePath))
                {
                    _terrariaAsm = Assembly.LoadFrom(exePath);
                    return _terrariaAsm;
                }
            }

            // 3. Check embedded DLL resources inside Terraria.exe (ReLogic, Newtonsoft.Json, Steamworks.NET, etc.)
            Assembly terraria = _terrariaAsm;
            if (terraria == null)
            {
                string exePath = Path.Combine(_terrariaDir, "Terraria.exe");
                if (File.Exists(exePath))
                    terraria = _terrariaAsm = Assembly.LoadFrom(exePath);
            }

            if (terraria != null)
            {
                string[] resNames = terraria.GetManifestResourceNames();
                for (int i = 0; i < resNames.Length; i++)
                {
                    if (resNames[i].EndsWith("." + dllName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(resNames[i], dllName, StringComparison.OrdinalIgnoreCase))
                    {
                        using (Stream stream = terraria.GetManifestResourceStream(resNames[i]))
                        {
                            if (stream != null)
                            {
                                byte[] data = new byte[stream.Length];
                                int read = 0;
                                while (read < data.Length)
                                {
                                    int n = stream.Read(data, read, data.Length - read);
                                    if (n <= 0) break;
                                    read += n;
                                }
                                return Assembly.Load(data);
                            }
                        }
                    }
                }
            }

            // 4. Check Terraria directory
            if (!string.IsNullOrEmpty(_terrariaDir))
            {
                string localDll = Path.Combine(_terrariaDir, dllName);
                if (File.Exists(localDll))
                    return Assembly.LoadFrom(localDll);
            }

            // 5. Optional fallback XNA directory (for headless benchmarks when XNA GAC is not installed)
            if (!string.IsNullOrEmpty(_xnaFallbackDir))
            {
                string xnaDll = Path.Combine(_xnaFallbackDir, dllName);
                if (File.Exists(xnaDll))
                    return Assembly.LoadFrom(xnaDll);
            }

            return null;
        }
    }
}
