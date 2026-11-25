using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Graphics.Light;
using Terraria.Utilities;
using TerrariaTurbo.Diagnostics;

namespace TerrariaTurbo.Patches
{
    /// <summary>
    /// Optimizes LightMap.Blur (eliminates 4 delegate/closure allocations per pass and uses
    /// bounds-check-free unsafe pointers in BlurLine) and TileLightScanner.ExportTo
    /// (eliminates closure allocation, per-tile WorldGen.InWorld calls, and redundant 2D Tile[,] indexing).
    /// </summary>
    public static class LightingPipelinePatch
    {
        private static AccessTools.FieldRef<LightMap, Vector3[]> _colorsRef;
        private static AccessTools.FieldRef<LightMap, LightMaskMode[]> _maskRef;
        private static AccessTools.FieldRef<LightMap, FastRandom> _randomRef;

        private static AccessTools.FieldRef<TileLightScanner, bool> _drawInvisibleWallsRef;
        private static AccessTools.FieldRef<TileLightScanner, FastRandom> _scannerRandomRef;

        private delegate void FastParallelForDelegate(int fromInclusive, int toExclusive, Delegate callback, object context);
        private static Delegate _blurVerticalDelegate;
        private static Delegate _blurHorizontalDelegate;
        private static Delegate _scanColumnsDelegate;
        private static MethodInfo _fastParallelForMethod;
        private static Type _parallelForActionType;

        private delegate LightMaskMode GetTileMaskDelegate(TileLightScanner scanner, Tile tile);
        private static GetTileMaskDelegate _getTileMask;

        private sealed class ScanContext
        {
            public TileLightScanner Scanner;
            public Rectangle Area;
            public LightMap OutputMap;
        }

        private static readonly ConcurrentStack<ScanContext> _scanPool = new ConcurrentStack<ScanContext>();

        public static void Apply(Harmony harmony)
        {
            _colorsRef = AccessTools.FieldRefAccess<LightMap, Vector3[]>("_colors");
            _maskRef = AccessTools.FieldRefAccess<LightMap, LightMaskMode[]>("_mask");
            _randomRef = AccessTools.FieldRefAccess<LightMap, FastRandom>("_random");

            _drawInvisibleWallsRef = AccessTools.FieldRefAccess<TileLightScanner, bool>("_drawInvisibleWalls");
            _scannerRandomRef = AccessTools.FieldRefAccess<TileLightScanner, FastRandom>("_random");

            Type fastParallelType = AccessTools.TypeByName("ReLogic.Threading.FastParallel");
            _parallelForActionType = AccessTools.TypeByName("ReLogic.Threading.ParallelForAction");
            _fastParallelForMethod = AccessTools.Method(fastParallelType, "For", new[] { typeof(int), typeof(int), _parallelForActionType, typeof(object) });

            _blurVerticalDelegate = Delegate.CreateDelegate(
                _parallelForActionType,
                typeof(LightingPipelinePatch).GetMethod(nameof(BlurVerticalSlice), BindingFlags.NonPublic | BindingFlags.Static));

            _blurHorizontalDelegate = Delegate.CreateDelegate(
                _parallelForActionType,
                typeof(LightingPipelinePatch).GetMethod(nameof(BlurHorizontalSlice), BindingFlags.NonPublic | BindingFlags.Static));

            _scanColumnsDelegate = Delegate.CreateDelegate(
                _parallelForActionType,
                typeof(LightingPipelinePatch).GetMethod(nameof(ScanColumns), BindingFlags.NonPublic | BindingFlags.Static));

            MethodInfo getTileMaskInfo = AccessTools.Method(typeof(TileLightScanner), "GetTileMask", new[] { typeof(Tile) });
            _getTileMask = (GetTileMaskDelegate)Delegate.CreateDelegate(typeof(GetTileMaskDelegate), getTileMaskInfo);

            _scanPool.Push(new ScanContext());

            MethodInfo blurTarget = AccessTools.Method(typeof(LightMap), "Blur");
            MethodInfo blurPrefix = AccessTools.Method(typeof(LightingPipelinePatch), nameof(LightMapBlurPrefix));
            harmony.Patch(blurTarget, prefix: new HarmonyMethod(blurPrefix));

            MethodInfo exportTarget = AccessTools.Method(typeof(TileLightScanner), "ExportTo",
                new[] { typeof(Rectangle), typeof(LightMap), typeof(TileLightScannerOptions) });
            MethodInfo exportPrefix = AccessTools.Method(typeof(LightingPipelinePatch), nameof(ExportToPrefix));
            harmony.Patch(exportTarget, prefix: new HarmonyMethod(exportPrefix));
        }

        private static void InvokeFastParallel(int fromInclusive, int toExclusive, Delegate callback, object context)
        {
            // Calls through FastParallel.For (which is patched by FastParallelPatch when enabled)
            FastParallelInvoker(fromInclusive, toExclusive, callback, context);
        }

        private static readonly Action<int, int, Delegate, object> FastParallelInvoker = BuildInvoker();

        private static Action<int, int, Delegate, object> BuildInvoker()
        {
            Type fpType = AccessTools.TypeByName("ReLogic.Threading.FastParallel");
            Type actType = AccessTools.TypeByName("ReLogic.Threading.ParallelForAction");
            MethodInfo m = AccessTools.Method(fpType, "For", new[] { typeof(int), typeof(int), actType, typeof(object) });

            var pFrom = System.Linq.Expressions.Expression.Parameter(typeof(int), "from");
            var pTo = System.Linq.Expressions.Expression.Parameter(typeof(int), "to");
            var pDel = System.Linq.Expressions.Expression.Parameter(typeof(Delegate), "del");
            var pCtx = System.Linq.Expressions.Expression.Parameter(typeof(object), "ctx");
            var call = System.Linq.Expressions.Expression.Call(
                m,
                pFrom,
                pTo,
                System.Linq.Expressions.Expression.Convert(pDel, actType),
                pCtx);
            return System.Linq.Expressions.Expression.Lambda<Action<int, int, Delegate, object>>(
                call, pFrom, pTo, pDel, pCtx).Compile();
        }

        private static bool LightMapBlurPrefix(LightMap __instance)
        {
            Interlocked.Increment(ref PerfTelemetry.LightMapBlurCalls);

            int width = __instance.Width;
            int height = __instance.Height;
            if (width <= 0 || height <= 0)
            {
                _randomRef(__instance).NextSeed();
                return false;
            }

            InvokeFastParallel(0, width, _blurVerticalDelegate, __instance);
            InvokeFastParallel(0, height, _blurHorizontalDelegate, __instance);
            InvokeFastParallel(0, width, _blurVerticalDelegate, __instance);
            InvokeFastParallel(0, height, _blurHorizontalDelegate, __instance);

            _randomRef(__instance).NextSeed();
            return false;
        }

        private static unsafe void BlurVerticalSlice(int start, int end, object context)
        {
            var map = (LightMap)context;
            Vector3[] colors = _colorsRef(map);
            LightMaskMode[] mask = _maskRef(map);
            FastRandom random = _randomRef(map);

            int height = map.Height;
            int pad = map.NonVisiblePadding;
            float decayAir = map.LightDecayThroughAir;
            float decaySolid = map.LightDecayThroughSolid;
            float decayCracked = map.LightDecayThroughCrackedBrick;
            Vector3 decayWater = map.LightDecayThroughWater;
            Vector3 decayHoney = map.LightDecayThroughHoney;

            int endDown = height - 1 - pad;
            int startUp = height - 1;

            fixed (Vector3* pColors = colors)
            fixed (LightMaskMode* pMask = mask)
            {
                for (int x = start; x < end; x++)
                {
                    int colBase = x * height;
                    BlurLineUnsafe(pColors, pMask, ref random, colBase, colBase + endDown, 1,
                        decayAir, decaySolid, decayCracked, decayWater, decayHoney);
                    BlurLineUnsafe(pColors, pMask, ref random, colBase + startUp, colBase + pad, -1,
                        decayAir, decaySolid, decayCracked, decayWater, decayHoney);
                }
            }
        }

        private static unsafe void BlurHorizontalSlice(int start, int end, object context)
        {
            var map = (LightMap)context;
            Vector3[] colors = _colorsRef(map);
            LightMaskMode[] mask = _maskRef(map);
            FastRandom random = _randomRef(map);

            int width = map.Width;
            int height = map.Height;
            int pad = map.NonVisiblePadding;
            float decayAir = map.LightDecayThroughAir;
            float decaySolid = map.LightDecayThroughSolid;
            float decayCracked = map.LightDecayThroughCrackedBrick;
            Vector3 decayWater = map.LightDecayThroughWater;
            Vector3 decayHoney = map.LightDecayThroughHoney;

            int endRight = (width - 1 - pad) * height;
            int startLeft = (width - 1) * height;
            int endLeft = pad * height;

            fixed (Vector3* pColors = colors)
            fixed (LightMaskMode* pMask = mask)
            {
                for (int y = start; y < end; y++)
                {
                    BlurLineUnsafe(pColors, pMask, ref random, y, endRight + y, height,
                        decayAir, decaySolid, decayCracked, decayWater, decayHoney);
                    BlurLineUnsafe(pColors, pMask, ref random, startLeft + y, endLeft + y, -height,
                        decayAir, decaySolid, decayCracked, decayWater, decayHoney);
                }
            }
        }

        private static unsafe void BlurLineUnsafe(
            Vector3* pColors,
            LightMaskMode* pMask,
            ref FastRandom random,
            int startIndex,
            int endIndex,
            int stride,
            float decayAir,
            float decaySolid,
            float decayCracked,
            Vector3 decayWater,
            Vector3 decayHoney)
        {
            float accX = 0f, accY = 0f, accZ = 0f;
            bool skipX = false, skipY = false, skipZ = false;
            int stop = endIndex + stride;

            for (int i = startIndex; i != stop; i += stride)
            {
                Vector3* cell = pColors + i;
                float cx = cell->X;
                float cy = cell->Y;
                float cz = cell->Z;

                if (accX < cx)
                {
                    accX = cx;
                    skipX = false;
                }
                else if (!skipX)
                {
                    if (accX < 0.0185f)
                        skipX = true;
                    else
                        cell->X = accX;
                }

                if (accY < cy)
                {
                    accY = cy;
                    skipY = false;
                }
                else if (!skipY)
                {
                    if (accY < 0.0185f)
                        skipY = true;
                    else
                        cell->Y = accY;
                }

                if (accZ < cz)
                {
                    accZ = cz;
                    skipZ = false;
                }
                else if (!skipZ)
                {
                    if (accZ < 0.0185f)
                        skipZ = true;
                    else
                        cell->Z = accZ;
                }

                if (skipX & skipZ & skipY)
                    continue;

                switch (pMask[i])
                {
                    case LightMaskMode.None:
                        if (!skipX) accX *= decayAir;
                        if (!skipY) accY *= decayAir;
                        if (!skipZ) accZ *= decayAir;
                        break;
                    case LightMaskMode.Solid:
                        if (!skipX) accX *= decaySolid;
                        if (!skipY) accY *= decaySolid;
                        if (!skipZ) accZ *= decaySolid;
                        break;
                    case LightMaskMode.CrackedBricks:
                        if (!skipX) accX *= decayCracked;
                        if (!skipY) accY *= decayCracked;
                        if (!skipZ) accZ *= decayCracked;
                        break;
                    case LightMaskMode.Water:
                    {
                        float num = (float)random.WithModifier((ulong)i).Next(98, 100) / 100f;
                        if (!skipX) accX *= decayWater.X * num;
                        if (!skipY) accY *= decayWater.Y * num;
                        if (!skipZ) accZ *= decayWater.Z * num;
                        break;
                    }
                    case LightMaskMode.Honey:
                        if (!skipX) accX *= decayHoney.X;
                        if (!skipY) accY *= decayHoney.Y;
                        if (!skipZ) accZ *= decayHoney.Z;
                        break;
                }
            }
        }

        private static bool ExportToPrefix(TileLightScanner __instance, Rectangle area, LightMap outputMap, TileLightScannerOptions options)
        {
            Interlocked.Increment(ref PerfTelemetry.TileLightScanCalls);

            _drawInvisibleWallsRef(__instance) = options.DrawInvisibleWalls;

            if (!_scanPool.TryPop(out ScanContext ctx))
                ctx = new ScanContext();

            ctx.Scanner = __instance;
            ctx.Area = area;
            ctx.OutputMap = outputMap;

            InvokeFastParallel(area.Left, area.Right, _scanColumnsDelegate, ctx);

            ctx.Scanner = null;
            ctx.OutputMap = null;
            _scanPool.Push(ctx);
            return false;
        }

        private static void ScanColumns(int start, int end, object context)
        {
            var ctx = (ScanContext)context;
            TileLightScanner scanner = ctx.Scanner;
            Rectangle area = ctx.Area;
            LightMap outputMap = ctx.OutputMap;

            Vector3[] outColors = _colorsRef(outputMap);
            LightMaskMode[] outMask = _maskRef(outputMap);
            int mapHeight = outputMap.Height;

            Tile[,] tiles = Main.tile;
            int worldMaxXMinus1 = Main.maxTilesX - 1;
            int worldMaxYMinus1 = Main.maxTilesY - 1;

            int top = area.Top;
            int bottom = area.Bottom;
            int areaX = area.X;
            int areaY = area.Y;

            for (int x = start; x < end; x++)
            {
                int mapIdx = (x - areaX) * mapHeight + (top - areaY);

                // Outside world inner border [1 .. maxTilesX-2] -> all tiles fail WorldGen.InWorld(x, y, 1)
                if (x < 1 || x >= worldMaxXMinus1)
                {
                    for (int y = top; y < bottom; y++, mapIdx++)
                    {
                        outMask[mapIdx] = LightMaskMode.None;
                        outColors[mapIdx] = Vector3.Zero;
                    }
                    continue;
                }

                int safeTop = top < 1 ? 1 : top;
                int safeBottom = bottom > worldMaxYMinus1 ? worldMaxYMinus1 : bottom;

                if (safeTop >= safeBottom)
                {
                    for (int y = top; y < bottom; y++, mapIdx++)
                    {
                        outMask[mapIdx] = LightMaskMode.None;
                        outColors[mapIdx] = Vector3.Zero;
                    }
                    continue;
                }

                // Top out-of-world band (guaranteed top <= safeTop < safeBottom <= bottom)
                for (int y = top; y < safeTop; y++, mapIdx++)
                {
                    outMask[mapIdx] = LightMaskMode.None;
                    outColors[mapIdx] = Vector3.Zero;
                }

                // Slide 3 vertical tiles down column x so each tile in Main.tile[x, y] is read once
                Tile prevTile = tiles[x, safeTop - 1];
                Tile currTile = tiles[x, safeTop];

                for (int y = safeTop; y < safeBottom; y++, mapIdx++)
                {
                    Tile nextTile = tiles[x, y + 1];

                    if (currTile == null || prevTile == null || nextTile == null ||
                        tiles[x + 1, y] == null || tiles[x - 1, y] == null)
                    {
                        outMask[mapIdx] = LightMaskMode.None;
                        outColors[mapIdx] = Vector3.Zero;
                    }
                    else
                    {
                        outMask[mapIdx] = _getTileMask(scanner, currTile);
                        scanner.GetTileLight(x, y, out outColors[mapIdx]);
                    }

                    prevTile = currTile;
                    currTile = nextTile;
                }

                // Bottom out-of-world band
                for (int y = safeBottom; y < bottom; y++, mapIdx++)
                {
                    outMask[mapIdx] = LightMaskMode.None;
                    outColors[mapIdx] = Vector3.Zero;
                }
            }
        }
    }
}
