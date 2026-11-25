using System.Threading;

namespace TerrariaTurbo.Diagnostics
{
    public static class PerfTelemetry
    {
        public static long FastParallelCalls;
        public static long FastParallelChunks;
        public static long LightMapBlurCalls;
        public static long TileLightScanCalls;
        public static long RecipeUpdateCalls;
        public static long RecipeCacheHits;
        public static long RecipeCacheMisses;
        public static long NearbyChestCalls;
        public static long NearbyBankCalls;

        public static void Reset()
        {
            Interlocked.Exchange(ref FastParallelCalls, 0);
            Interlocked.Exchange(ref FastParallelChunks, 0);
            Interlocked.Exchange(ref LightMapBlurCalls, 0);
            Interlocked.Exchange(ref TileLightScanCalls, 0);
            Interlocked.Exchange(ref RecipeUpdateCalls, 0);
            Interlocked.Exchange(ref RecipeCacheHits, 0);
            Interlocked.Exchange(ref RecipeCacheMisses, 0);
            Interlocked.Exchange(ref NearbyChestCalls, 0);
            Interlocked.Exchange(ref NearbyBankCalls, 0);
        }

        public static string FormatSummary()
        {
            long totalRecipes = Volatile.Read(ref RecipeUpdateCalls);
            long hits = Volatile.Read(ref RecipeCacheHits);
            double hitRate = totalRecipes > 0 ? (hits * 100.0 / totalRecipes) : 0.0;
            return string.Format(
                "FastParallel={0} calls ({1} chunks) | LightBlur={2} | TileScan={3} | RecipeUpdates={4} (cache hits={5}, {6:F1}%)",
                Volatile.Read(ref FastParallelCalls),
                Volatile.Read(ref FastParallelChunks),
                Volatile.Read(ref LightMapBlurCalls),
                Volatile.Read(ref TileLightScanCalls),
                totalRecipes,
                hits,
                hitRate);
        }
    }
}
