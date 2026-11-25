using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HarmonyLib;
using Microsoft.Win32;
using TerrariaTurbo.Diagnostics;
using TerrariaTurbo.Patches;

namespace TerrariaTurbo
{
    public static class Program
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);

        private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

        [STAThread]
        private static int Main(string[] args)
        {
            PerfConfig config = PerfConfig.ParseArgs(args, out string[] gameArgs);
            string terrariaDir = ResolveTerrariaDirectory(config.TerrariaDir);

            if (string.IsNullOrEmpty(terrariaDir) || !File.Exists(Path.Combine(terrariaDir, "Terraria.exe")))
            {
                Console.Error.WriteLine("[TerrariaTurbo] Could not locate Terraria.exe.");
                Console.Error.WriteLine("Pass --terraria-dir=\"C:\\path\\to\\Terraria\"");
                return 1;
            }

            Console.WriteLine($"[TerrariaTurbo] Terraria directory: {terrariaDir}");
            Environment.CurrentDirectory = terrariaDir;

            TerrariaAssemblyResolver.Register(terrariaDir);

            PreloadNativeDll(Path.Combine(terrariaDir, "msvcr100.dll"));
            PreloadNativeDll(Path.Combine(terrariaDir, "msvcp100.dll"));
            PreloadNativeDll(Path.Combine(terrariaDir, "d3dx9_41.dll"));
            PreloadNativeDll(Path.Combine(terrariaDir, "X3DAudio1_7.dll"));

            var harmony = new Harmony("com.terrariaturbo.patches");
            ApplyPatches(harmony, config);

            Console.WriteLine("[TerrariaTurbo] All enabled Harmony patches active. Launching Terraria 1.4.6.1...");

            AppDomain.CurrentDomain.ProcessExit += (_, __) =>
            {
                if (config.Verbose)
                    Console.WriteLine("[TerrariaTurbo] Session telemetry: " + PerfTelemetry.FormatSummary());
            };

            LaunchTerraria(gameArgs);
            return 0;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void ApplyPatches(Harmony harmony, PerfConfig config)
        {
            if (config.EnableFastParallel)
            {
                FastParallelPatch.Apply(harmony);
                Console.WriteLine("[TerrariaTurbo] [+] FastParallel zero-alloc worker pool patch applied.");
            }
            if (config.EnableLightingPipeline)
            {
                LightingPipelinePatch.Apply(harmony);
                Console.WriteLine("[TerrariaTurbo] [+] LightMap + TileLightScanner unsafe/zero-closure patch applied.");
            }
            if (config.EnableCraftingAndChests)
            {
                CraftingAndChestsPatch.Apply(harmony);
                Console.WriteLine("[TerrariaTurbo] [+] Recipe.UpdateRecipeList + NearbyChests + NewCraftingUI patch applied.");
            }
        }

        private static void LaunchTerraria(string[] gameArgs)
        {
            Type programType = AccessTools.TypeByName("Terraria.Program");
            MethodInfo launchGame = AccessTools.Method(programType, "LaunchGame", new[] { typeof(string[]), typeof(bool) });
            launchGame.Invoke(null, new object[] { gameArgs, false });
        }

        private static void PreloadNativeDll(string fullPath)
        {
            if (File.Exists(fullPath))
                LoadLibraryEx(fullPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
        }

        private static string ResolveTerrariaDirectory(string explicitDir)
        {
            if (!string.IsNullOrEmpty(explicitDir))
                return explicitDir;

            string defaultSteam = @"C:\Program Files (x86)\Steam\steamapps\common\Terraria";
            if (File.Exists(Path.Combine(defaultSteam, "Terraria.exe")))
                return defaultSteam;

            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string steamPath = key?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(steamPath))
                    {
                        string candidate = Path.Combine(steamPath.Replace('/', Path.DirectorySeparatorChar), "steamapps", "common", "Terraria");
                        if (File.Exists(Path.Combine(candidate, "Terraria.exe")))
                            return candidate;
                    }
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
