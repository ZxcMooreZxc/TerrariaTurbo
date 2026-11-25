using System;

namespace TerrariaTurbo
{
    public sealed class PerfConfig
    {
        public bool EnableFastParallel { get; set; } = true;
        public bool EnableLightingPipeline { get; set; } = true;
        public bool EnableCraftingAndChests { get; set; } = true;
        public bool Verbose { get; set; } = false;
        public string TerrariaDir { get; set; }

        public static PerfConfig ParseArgs(string[] args, out string[] passthroughArgs)
        {
            var cfg = new PerfConfig();
            if (args == null || args.Length == 0)
            {
                passthroughArgs = Array.Empty<string>();
                return cfg;
            }

            var pass = new System.Collections.Generic.List<string>(args.Length);
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "--no-parallel", StringComparison.OrdinalIgnoreCase))
                    cfg.EnableFastParallel = false;
                else if (string.Equals(a, "--no-lighting", StringComparison.OrdinalIgnoreCase))
                    cfg.EnableLightingPipeline = false;
                else if (string.Equals(a, "--no-crafting", StringComparison.OrdinalIgnoreCase))
                    cfg.EnableCraftingAndChests = false;
                else if (string.Equals(a, "--verbose", StringComparison.OrdinalIgnoreCase))
                    cfg.Verbose = true;
                else if (a.StartsWith("--terraria-dir=", StringComparison.OrdinalIgnoreCase))
                    cfg.TerrariaDir = a.Substring("--terraria-dir=".Length).Trim('"');
                else
                    pass.Add(a);
            }

            passthroughArgs = pass.ToArray();
            return cfg;
        }
    }
}
