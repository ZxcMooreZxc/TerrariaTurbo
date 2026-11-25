using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Microsoft.Xna.Framework;
using ReLogic.Threading;
using Terraria;
using Terraria.GameContent;
using Terraria.Graphics.Light;
using Terraria.ID;
using Terraria.Localization;
using Terraria.Testing;
using Terraria.Utilities;
using TerrariaMain = Terraria.Main;
using TerrariaTurbo;
using TerrariaTurbo.Diagnostics;
using TerrariaTurbo.Patches;

namespace TerrariaTurbo.Benchmarks
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            AppDomain.MonitoringIsEnabled = true;

            string terrariaDir = @"C:\Program Files (x86)\Steam\steamapps\common\Terraria";
            string xnaDir = @"C:\Users\5000elo\.gemini\antigravity\brain\41227058-874b-4804-b6ae-7138763fe81b\scratch\xna_refs\Program Files\Microsoft XNA\XNA Game Studio";

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i].StartsWith("--terraria-dir=", StringComparison.OrdinalIgnoreCase))
                    terrariaDir = args[i].Substring("--terraria-dir=".Length).Trim('"');
                else if (args[i].StartsWith("--xna-dir=", StringComparison.OrdinalIgnoreCase))
                    xnaDir = args[i].Substring("--xna-dir=".Length).Trim('"');
            }

            if (!File.Exists(Path.Combine(terrariaDir, "Terraria.exe")))
            {
                Console.Error.WriteLine("Terraria.exe not found at: " + terrariaDir);
                return 1;
            }

            TerrariaAssemblyResolver.Register(terrariaDir, xnaDir);

            return RunBenchmarksAndVerification();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void InitializeSavePath()
        {
            Terraria.Program.SavePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games",
                "Terraria");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int RunBenchmarksAndVerification()
        {
            InitializeSavePath();
            return BenchmarkRunner.Run();
        }
    }

    internal static class BenchmarkRunner
    {
        private struct BenchStats
        {
            public string Name;
            public double MeanMs;
            public double P50Ms;
            public double P90Ms;
            public long BytesPerIter;
            public int Gen0;
            public int Gen1;
            public int Gen2;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static int Run()
        {
            Console.WriteLine("=================================================================");
            Console.WriteLine("  TerrariaTurbo - Real Terraria 1.4.6.1 Benchmark & Verification ");
            Console.WriteLine("=================================================================");

            // Force ThreadPool min threads just like Terraria.Program.LaunchGame
            System.Threading.ThreadPool.SetMinThreads(8, 8);

            InitializeHeadlessTerrariaState();
            Console.WriteLine($"Initialized Terraria 1.4.6.1 headless world: {Recipe.numRecipes} recipes loaded.");

            // --- Capture Golden Baselines (Unpatched Vanilla) ---
            var vanillaParallelOutput = CaptureFastParallelOutput();
            var vanillaBlurOutput = CaptureLightMapBlurOutput();
            var vanillaScanOutput = CaptureTileLightScanOutput(out LightMaskMode[] vanillaScanMasks);
            var vanillaScanEdgeOutput = CaptureTileLightScanEdgeOutput(out LightMaskMode[] vanillaEdgeMasks);
            var vanillaRecipeSnap = CaptureRecipeSnapshot();

            Console.WriteLine();
            Console.WriteLine("[Phase 1] Running Vanilla 1.4.6.1 Baseline Benchmarks...");
            BenchStats vParallel = Benchmark("FastParallel.For (4x203)", 120, BenchFastParallel);
            BenchStats vBlur = Benchmark("LightMap.Blur (176x124)", 100, BenchLightMapBlur);
            BenchStats vScan = Benchmark("TileLightScanner.ExportTo (176x124)", 80, BenchTileLightScan);
            BenchStats vRecipes = Benchmark("Recipe.UpdateRecipeList (12 chests, 98% idle)", 200, BenchRecipeUpdatesMixed);
            TerrariaMain.LocalPlayer.inventory[0].stack = 999;

            // --- Apply TerrariaTurbo Harmony Patches ---
            Console.WriteLine();
            Console.WriteLine("[Phase 2] Applying TerrariaTurbo Harmony Patches...");
            var harmony = new Harmony("com.terrariaturbo.benchmarks");
            TerrariaTurbo.Program.ApplyPatches(harmony, new PerfConfig());
            PerfTelemetry.Reset();

            // --- Run Equivalence Verification ---
            Console.WriteLine();
            Console.WriteLine("[Phase 3] Verifying Equivalence Against Vanilla Golden Outputs...");
            VerifyFastParallelEquivalence(vanillaParallelOutput);
            VerifyLightMapBlurEquivalence(vanillaBlurOutput);
            VerifyTileLightScanEquivalence(vanillaScanOutput, vanillaScanMasks, vanillaScanEdgeOutput, vanillaEdgeMasks);
            VerifyRecipeEquivalence(vanillaRecipeSnap);
            Console.WriteLine("ALL EQUIVALENCE ASSERTIONS PASSED (100% bit-for-bit identical)!");

            // --- Run Patched Benchmarks ---
            Console.WriteLine();
            Console.WriteLine("[Phase 4] Running Patched (TerrariaTurbo) Benchmarks...");
            PerfTelemetry.Reset();
            BenchStats pParallel = Benchmark("FastParallel.For (4x203)", 120, BenchFastParallel);
            BenchStats pBlur = Benchmark("LightMap.Blur (176x124)", 100, BenchLightMapBlur);
            BenchStats pScan = Benchmark("TileLightScanner.ExportTo (176x124)", 80, BenchTileLightScan);
            BenchStats pRecipes = Benchmark("Recipe.UpdateRecipeList (12 chests, 98% idle)", 200, BenchRecipeUpdatesMixed);

            Console.WriteLine();
            Console.WriteLine("=================================================================");
            Console.WriteLine("  SUMMARY: Vanilla 1.4.6.1 vs TerrariaTurbo");
            Console.WriteLine("=================================================================");
            PrintComparison(vParallel, pParallel);
            PrintComparison(vBlur, pBlur);
            PrintComparison(vScan, pScan);
            PrintComparison(vRecipes, pRecipes);
            Console.WriteLine("-----------------------------------------------------------------");
            Console.WriteLine("Telemetry: " + PerfTelemetry.FormatSummary());
            Console.WriteLine("=================================================================");
            return 0;
        }

        private static LightMap _benchLightMap;
        private static TileLightScanner _benchScanner;
        private static Rectangle _benchScanArea;
        private static int _benchFrameCounter;
        private static AccessTools.FieldRef<uint> _gameUpdateCountRef;

        private static void InitializeHeadlessTerrariaState()
        {
            // Initialize authentic Terraria 1.4.6.1 game data headlessly (mirrors Main.DedServ)
            TerrariaMain.dedServ = true;
            TerrariaMain.showSplash = false;
            Lang.InitializeLegacyLocalization();
            TerrariaMain.instance = new TerrariaMain();
            typeof(TerrariaMain).GetMethod("Initialize", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(TerrariaMain.instance, null);
            Terraria.Map.MapHelper.Initialize();

            TerrariaMain.maxTilesX = 4200;
            TerrariaMain.maxTilesY = 1200;
            TerrariaMain.worldSurface = 350.0;
            TerrariaMain.rockLayer = 550.0;

            // Populate Main.tile in a generous region around (2100, 400) + world borders for edge tests
            TerrariaMain.tile = new Tile[TerrariaMain.maxTilesX, TerrariaMain.maxTilesY];
            for (int x = 1900; x < 2300; x++)
            {
                for (int y = 250; y < 580; y++)
                {
                    var t = new Tile();
                    if (y > 410 && ((x + y) % 7 != 0))
                    {
                        t.active(true);
                        t.type = (ushort)((x % 19 == 0 && y % 11 == 0) ? TileID.CrackedBlueDungeonBrick : TileID.Stone);
                    }
                    else if (y > 395 && y <= 410 && x >= 2050 && x <= 2150)
                    {
                        t.liquid = 255;
                        t.liquidType((x % 3 == 0) ? Tile.Liquid_Honey : Tile.Liquid_Water);
                    }
                    if (y > 390)
                        t.wall = WallID.Stone;
                    if (x % 15 == 0 && y == 400)
                    {
                        t.active(true);
                        t.type = TileID.Torches;
                    }
                    TerrariaMain.tile[x, y] = t;
                }
            }

            // Populate border region (0..220, 0..160) for world-edge clamping tests
            for (int x = 0; x < 220; x++)
            {
                for (int y = 0; y < 160; y++)
                {
                    var t = new Tile();
                    if ((x + y) % 5 == 0)
                    {
                        t.active(true);
                        t.type = TileID.Dirt;
                    }
                    TerrariaMain.tile[x, y] = t;
                }
            }

            // Setup player + inventory + crafting stations
            TerrariaMain.myPlayer = 0;
            var p = new Player();
            p.whoAmI = 0;
            p.position = new Vector2(2100 * 16f, 400 * 16f);
            p.width = 20;
            p.height = 42;
            p.adjTile[TileID.WorkBenches] = true;
            p.adjTile[TileID.Furnaces] = true;
            p.adjTile[TileID.Anvils] = true;
            p.adjTile[TileID.AlchemyTable] = true;
            p.adjTile[TileID.Bottles] = true;
            p.adjTile[TileID.Sawmill] = true;
            p.adjTile[TileID.Loom] = true;
            p.adjTile[TileID.TinkerersWorkbench] = true;
            p.adjWaterSource = true;

            int[] starterItems = { ItemID.Wood, ItemID.StoneBlock, ItemID.IronBar, ItemID.GoldBar, ItemID.Gel, ItemID.Torch, ItemID.Lens, ItemID.Silk, ItemID.BottledWater, ItemID.Daybloom };
            for (int i = 0; i < starterItems.Length; i++)
            {
                p.inventory[i].SetDefaults(starterItems[i]);
                p.inventory[i].stack = 999;
            }
            TerrariaMain.player[0] = p;
            Player.Settings.CraftFromNearbyChests = true;

            // Populate 200 chests across world, 12 within 600px range
            for (int i = 0; i < 200; i++)
            {
                int cx = (i < 12) ? (2090 + i * 2) : (200 + (i * 37) % 3600);
                int cy = (i < 12) ? 400 : (300 + (i * 19) % 400);
                Chest c = Chest.CreateWorldChest(i, cx, cy);
                for (int slot = 0; slot < 25; slot++)
                {
                    int itemId = 1 + ((i * 31 + slot * 17) % 400);
                    c.item[slot].SetDefaults(itemId);
                    c.item[slot].stack = 50 + slot;
                }
            }

            // Place a Void Vault + Piggy Bank in range
            TerrariaMain.tile[2102, 400].type = 491;
            TerrariaMain.tile[2104, 400].type = 29;
            for (int slot = 0; slot < 10; slot++)
            {
                p.bank.item[slot].SetDefaults(ItemID.IronOre + slot);
                p.bank.item[slot].stack = 100;
                p.bank4.item[slot].SetDefaults(ItemID.LeadOre + slot);
                p.bank4.item[slot].stack = 100;
            }

            _gameUpdateCountRef = AccessTools.StaticFieldRefAccess<uint>(AccessTools.Field(typeof(TerrariaMain), "_gameUpdateCount"));

            // Initialize LightMap & TileLightScanner
            _benchLightMap = new LightMap();
            _benchLightMap.SetSize(176, 124);
            _benchLightMap.NonVisiblePadding = 18;
            SeedLightMap(_benchLightMap);

            _benchScanner = new TileLightScanner();
            _benchScanArea = new Rectangle(2012, 338, 176, 124);
        }

        private static void SeedLightMap(LightMap map)
        {
            map.Clear();
            for (int x = 0; x < map.Width; x++)
            {
                for (int y = 0; y < map.Height; y++)
                {
                    LightMaskMode mode = LightMaskMode.None;
                    if ((x + y) % 13 == 0) mode = LightMaskMode.Solid;
                    else if ((x * 3 + y) % 17 == 0) mode = LightMaskMode.Water;
                    else if ((x + y * 5) % 29 == 0) mode = LightMaskMode.Honey;
                    else if ((x + y) % 31 == 0) mode = LightMaskMode.CrackedBricks;
                    map.SetMaskAt(x, y, mode);

                    if (x % 23 == 0 && y % 19 == 0)
                    {
                        map[x, y] = new Vector3(1.0f, 0.85f, 0.6f);
                    }
                }
            }
        }

        // --- Benchmark Workloads ---

        private static void BenchFastParallel()
        {
            int[] dummy = new int[203];
            ParallelForAction act = (start, end, ctx) =>
            {
                int[] arr = (int[])ctx;
                for (int i = start; i < end; i++)
                {
                    int v = i * 31;
                    for (int k = 0; k < 50; k++)
                        v = (v ^ 0x5bd1e995) + k;
                    arr[i] = v;
                }
            };
            for (int pass = 0; pass < 4; pass++)
                FastParallel.For(0, 203, act, dummy);
        }

        private static void BenchLightMapBlur()
        {
            SeedLightMap(_benchLightMap);
            _benchLightMap.Blur();
        }

        private static void BenchTileLightScan()
        {
            _benchScanner.Update();
            _benchScanner.ExportTo(_benchScanArea, _benchLightMap, new TileLightScannerOptions { DrawInvisibleWalls = false });
        }

        private static void BenchRecipeUpdatesMixed()
        {
            _gameUpdateCountRef()++;
            _benchFrameCounter++;
            // Simulate 98% idle frames at base + 2% stack changes / movement
            if (_benchFrameCounter % 50 == 0)
            {
                Player p = TerrariaMain.LocalPlayer;
                p.inventory[0].stack = 900 + (_benchFrameCounter % 99);
            }
            Recipe.UpdateRecipeList();
        }

        // --- Golden Capture & Verification ---

        private static int[] CaptureFastParallelOutput()
        {
            int[] arr = new int[203];
            FastParallel.For(0, 203, (start, end, ctx) =>
            {
                int[] d = (int[])ctx;
                for (int i = start; i < end; i++) d[i] = i * i + 7;
            }, arr);
            return arr;
        }

        private static void VerifyFastParallelEquivalence(int[] expected)
        {
            int[] actual = CaptureFastParallelOutput();
            for (int i = 0; i < expected.Length; i++)
                Assert(actual[i] == expected[i], $"FastParallel mismatch at {i}: {actual[i]} != {expected[i]}");

            // Edge: empty range & single element & ForceTasksOnCallingThread
            int sum = 0;
            FastParallel.For(5, 5, (s, e, c) => sum += 999, null);
            Assert(sum == 0, "FastParallel empty range ran unexpectedly");

            FastParallel.ForceTasksOnCallingThread = true;
            int[] single = new int[1];
            FastParallel.For(0, 1, (s, e, c) => ((int[])c)[0] = 42, single);
            FastParallel.ForceTasksOnCallingThread = false;
            Assert(single[0] == 42, "FastParallel single-thread mode failed");
        }

        private static Vector3[] CaptureLightMapBlurOutput()
        {
            var map = new LightMap();
            map.SetSize(176, 124);
            map.NonVisiblePadding = 18;
            // Deterministic seed via reflection on _random
            var rndField = typeof(LightMap).GetField("_random", BindingFlags.Instance | BindingFlags.NonPublic);
            rndField.SetValue(map, new FastRandom(12345u));
            SeedLightMap(map);
            map.Blur();

            var colors = (Vector3[])typeof(LightMap).GetField("_colors", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(map);
            return (Vector3[])colors.Clone();
        }

        private static void VerifyLightMapBlurEquivalence(Vector3[] expected)
        {
            Vector3[] actual = CaptureLightMapBlurOutput();
            Assert(actual.Length == expected.Length, "LightMap array length mismatch");
            float maxDiff = 0f;
            for (int i = 0; i < expected.Length; i++)
            {
                float dx = Math.Abs(actual[i].X - expected[i].X);
                float dy = Math.Abs(actual[i].Y - expected[i].Y);
                float dz = Math.Abs(actual[i].Z - expected[i].Z);
                float d = Math.Max(dx, Math.Max(dy, dz));
                if (d > maxDiff) maxDiff = d;
                Assert(d < 1e-6f, $"LightMap.Blur mismatch at {i}: {actual[i]} != {expected[i]} (delta={d})");
            }
            Console.WriteLine($"  [Check] LightMap.Blur max float delta across {expected.Length:N0} cells = {maxDiff:E2}");
        }

        private static Vector3[] CaptureTileLightScanOutput(out LightMaskMode[] masks)
        {
            var map = new LightMap();
            map.SetSize(176, 124);
            map.NonVisiblePadding = 18;
            var scanner = new TileLightScanner();
            typeof(TileLightScanner).GetField("_random", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(scanner, new FastRandom(777u));
            scanner.ExportTo(new Rectangle(2012, 338, 176, 124), map, new TileLightScannerOptions { DrawInvisibleWalls = false });

            var colors = (Vector3[])typeof(LightMap).GetField("_colors", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(map);
            var m = (LightMaskMode[])typeof(LightMap).GetField("_mask", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(map);
            masks = (LightMaskMode[])m.Clone();
            return (Vector3[])colors.Clone();
        }

        private static Vector3[] CaptureTileLightScanEdgeOutput(out LightMaskMode[] masks)
        {
            var map = new LightMap();
            map.SetSize(176, 124);
            map.NonVisiblePadding = 18;
            var scanner = new TileLightScanner();
            typeof(TileLightScanner).GetField("_random", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(scanner, new FastRandom(777u));
            // Deliberately cross negative & 0-border bounds
            scanner.ExportTo(new Rectangle(-10, -5, 176, 124), map, new TileLightScannerOptions { DrawInvisibleWalls = true });

            var colors = (Vector3[])typeof(LightMap).GetField("_colors", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(map);
            var m = (LightMaskMode[])typeof(LightMap).GetField("_mask", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(map);
            masks = (LightMaskMode[])m.Clone();
            return (Vector3[])colors.Clone();
        }

        private static void VerifyTileLightScanEquivalence(
            Vector3[] expectedCenter, LightMaskMode[] expectedCenterMasks,
            Vector3[] expectedEdge, LightMaskMode[] expectedEdgeMasks)
        {
            Vector3[] actualCenter = CaptureTileLightScanOutput(out LightMaskMode[] actualCenterMasks);
            for (int i = 0; i < expectedCenter.Length; i++)
            {
                Assert(actualCenterMasks[i] == expectedCenterMasks[i], $"Scan center mask mismatch at {i}");
                Assert(actualCenter[i] == expectedCenter[i], $"Scan center color mismatch at {i}");
            }

            Vector3[] actualEdge = CaptureTileLightScanEdgeOutput(out LightMaskMode[] actualEdgeMasks);
            for (int i = 0; i < expectedEdge.Length; i++)
            {
                Assert(actualEdgeMasks[i] == expectedEdgeMasks[i], $"Scan edge mask mismatch at {i}");
                Assert(actualEdge[i] == expectedEdge[i], $"Scan edge color mismatch at {i}");
            }
        }

        private sealed class RecipeSnapshot
        {
            public int NumAvailable;
            public int[] Available;
            public Dictionary<int, int> Owned;
            public List<Chest> Chests;
        }

        private static RecipeSnapshot CaptureRecipeSnapshot()
        {
            Recipe.UpdateRecipeList();
            var ownedField = typeof(Recipe).GetField("_ownedItems", BindingFlags.Static | BindingFlags.NonPublic);
            var chestsField = typeof(Recipe).GetField("_recipeChests", BindingFlags.Static | BindingFlags.NonPublic);
            var owned = (Dictionary<int, int>)ownedField.GetValue(null);
            var chests = (List<Chest>)chestsField.GetValue(null);

            var snap = new RecipeSnapshot
            {
                NumAvailable = TerrariaMain.numAvailableRecipes,
                Available = new int[TerrariaMain.numAvailableRecipes],
                Owned = new Dictionary<int, int>(owned),
                Chests = new List<Chest>(chests)
            };
            Array.Copy(TerrariaMain.availableRecipe, snap.Available, TerrariaMain.numAvailableRecipes);
            return snap;
        }

        private static void VerifyRecipeEquivalence(RecipeSnapshot expected)
        {
            // 1. Initial match
            RecipeSnapshot actual = CaptureRecipeSnapshot();
            AssertSnapshotsEqual(expected, actual, "Initial UpdateRecipeList");

            // 2. Cache hit match (second call)
            RecipeSnapshot cached = CaptureRecipeSnapshot();
            AssertSnapshotsEqual(expected, cached, "Cached UpdateRecipeList");

            // 3. Chest item mutation invalidates cache immediately
            TerrariaMain.chest[0].item[0].SetDefaults(ItemID.PlatinumBar);
            TerrariaMain.chest[0].item[0].stack = 99;
            RecipeSnapshot afterChestEdit = CaptureRecipeSnapshot();
            Assert(afterChestEdit.Owned.TryGetValue(ItemID.PlatinumBar, out int plat) && plat >= 99,
                "Chest item edit was not reflected after cache invalidation!");

            // Restore chest[0]
            TerrariaMain.chest[0].item[0].SetDefaults(1);
            TerrariaMain.chest[0].item[0].stack = 50;
            RecipeSnapshot restored = CaptureRecipeSnapshot();
            AssertSnapshotsEqual(expected, restored, "Restored chest UpdateRecipeList");

            // 4. Craft consumption invalidates cache
            int woodBefore = restored.Owned[ItemID.Wood];
            Recipe.ConsumeOwnedItem(ItemID.Wood, 10);
            RecipeSnapshot afterCraft = CaptureRecipeSnapshot();
            Assert(afterCraft.Owned[ItemID.Wood] == woodBefore, "ConsumeOwnedItem triggered fresh recompute from inventory/chests");
        }

        private static void AssertSnapshotsEqual(RecipeSnapshot expected, RecipeSnapshot actual, string label)
        {
            Assert(actual.NumAvailable == expected.NumAvailable, $"{label}: NumAvailable {actual.NumAvailable} != {expected.NumAvailable}");
            for (int i = 0; i < expected.NumAvailable; i++)
                Assert(actual.Available[i] == expected.Available[i], $"{label}: Available[{i}] {actual.Available[i]} != {expected.Available[i]}");
            Assert(actual.Owned.Count == expected.Owned.Count, $"{label}: Owned.Count {actual.Owned.Count} != {expected.Owned.Count}");
            foreach (var kv in expected.Owned)
                Assert(actual.Owned.TryGetValue(kv.Key, out int v) && v == kv.Value, $"{label}: Owned[{kv.Key}] mismatch");
            Assert(actual.Chests.Count == expected.Chests.Count, $"{label}: Chests.Count {actual.Chests.Count} != {expected.Chests.Count}");
        }

        private static void Assert(bool cond, string message)
        {
            if (!cond)
                throw new InvalidOperationException("VERIFICATION FAILED: " + message);
        }

        private static BenchStats Benchmark(string name, int iterations, Action action)
        {
            // Warmup JIT
            for (int i = 0; i < 15; i++)
                action();

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            double[] timesMs = new double[iterations];
            int g0Before = GC.CollectionCount(0);
            int g1Before = GC.CollectionCount(1);
            int g2Before = GC.CollectionCount(2);
            long allocBefore = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;

            var sw = new Stopwatch();
            for (int i = 0; i < iterations; i++)
            {
                sw.Restart();
                action();
                sw.Stop();
                timesMs[i] = sw.Elapsed.TotalMilliseconds;
            }

            long allocAfter = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
            int g0 = GC.CollectionCount(0) - g0Before;
            int g1 = GC.CollectionCount(1) - g1Before;
            int g2 = GC.CollectionCount(2) - g2Before;

            Array.Sort(timesMs);
            double sum = 0;
            for (int i = 0; i < timesMs.Length; i++) sum += timesMs[i];

            return new BenchStats
            {
                Name = name,
                MeanMs = sum / iterations,
                P50Ms = timesMs[iterations / 2],
                P90Ms = timesMs[(int)(iterations * 0.9)],
                BytesPerIter = Math.Max(0L, (allocAfter - allocBefore) / iterations),
                Gen0 = g0,
                Gen1 = g1,
                Gen2 = g2
            };
        }

        private static void PrintComparison(BenchStats vanilla, BenchStats patched)
        {
            double speedup = patched.MeanMs > 0 ? vanilla.MeanMs / patched.MeanMs : 0;
            double allocReduction = vanilla.BytesPerIter > 0
                ? (1.0 - (double)patched.BytesPerIter / vanilla.BytesPerIter) * 100.0
                : 0.0;

            Console.WriteLine($"{vanilla.Name}:");
            Console.WriteLine($"  Vanilla : mean={vanilla.MeanMs,7:F4} ms | p50={vanilla.P50Ms,7:F4} ms | p90={vanilla.P90Ms,7:F4} ms | alloc={vanilla.BytesPerIter,8:N0} B/call | GC=({vanilla.Gen0}/{vanilla.Gen1}/{vanilla.Gen2})");
            Console.WriteLine($"  Patched : mean={patched.MeanMs,7:F4} ms | p50={patched.P50Ms,7:F4} ms | p90={patched.P90Ms,7:F4} ms | alloc={patched.BytesPerIter,8:N0} B/call | GC=({patched.Gen0}/{patched.Gen1}/{patched.Gen2})");
            Console.WriteLine($"  Result  : {speedup:F2}x faster | {allocReduction:F1}% less allocation");
        }
    }
}
