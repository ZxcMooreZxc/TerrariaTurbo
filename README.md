# TerrariaTurbo

Zero-modification runtime performance patcher and micro-stutter fixer for **Terraria 1.4.6.1** (x86 / .NET Framework 4.8), powered by [Harmony](https://github.com/pardeike/Harmony).

Instead of permanently modifying `Terraria.exe` on disk, `TerrariaTurbo.exe` loads your Steam `Terraria.exe` at runtime, applies surgical Harmony patches to verified CPU/GC bottlenecks, and launches the game normally.

---

## Verified Bottlenecks Fixed (Terraria 1.4.6.1)

By decompiling and profiling vanilla `Terraria.exe` (v1.4.6.1) and its embedded `ReLogic.dll`, we identified and fixed three concrete hot-path bottlenecks:

### 1. Per-Frame Crafting & Nearby-Chests Full Scan (`Recipe.UpdateRecipeList` / `NearbyChests` / `NewCraftingUI`)
* **Vanilla issue:**
  * `Main.DrawInventory()` and `NewCraftingUI.Draw()` call `Recipe.UpdateRecipeList()` **unconditionally on every single frame** (60–144+ Hz) while the inventory or crafting UI is open.
  * Every frame, it runs `NearbyChests.GetChestsInRangeOf()` (scanning all **8,000** `Main.chest` slots with `Vector2.Distance` / `Math.Sqrt`), `NearbyChests.GetBanksInRangeOf()` (scanning **1,000** projectiles + a **77×77 = 5,929-tile** `Main.tile[,]` grid, plus scanning 58 inventory slots *per Void Vault tile*), `ClearAvailableRecipes()` (zeroing 3,700 entries), and checking all **3,603+** recipes.
  * `NewCraftingUI.DrawChildren()` also calls `SetupGamepadPoints()` every frame even on mouse & keyboard, walking the UI tree and running LINQ `.First(...)`.
* **Fix (`CraftingAndChestsPatch`):**
  * **64-bit state fingerprinting** across player inventory, nearby chest/bank contents, `adjTile` & biome/liquid flags, `guideItem`, `RecipeFilterHack`, and pending crafting requests. When unchanged, `Recipe.UpdateRecipeList()` returns immediately (`~0.008 ms`).
  * **Compact active-chest index** + squared-distance checks (no `Math.Sqrt`) in `NearbyChests.GetChestsInRangeOf()`.
  * **Hoisted Void Bag check** + cached static bank tile scan in `NearbyChests.GetBanksInRangeOf()`.
  * Skips `NewCraftingUI.SetupGamepadPoints()` when `!PlayerInput.UsingGamepad && !PlayerInput.UsingGamepadUI`.

### 2. `ReLogic.Threading.FastParallel.For` Allocations & Busy-Spin Contention
* **Vanilla issue:**
  * Called 5–6 times per lighting cycle (`TileLightScanner.ExportTo`, `LightMap.BlurPass` ×4, `LightingEngine.ExportToMiniMap`).
  * Allocates `new CountdownEvent(workers)` + `workers` `RangeTask` objects on every call, elevates the main thread to `ThreadPriority.Highest`, and spins in an empty `while (countdownEvent.CurrentCount != 0) {}` loop against Normal-priority `ThreadPool` workers.
* **Fix (`FastParallelPatch`):**
  * Replaces `FastParallel.For` with a **pooled `BatchState`** runner using `ThreadPool.UnsafeQueueUserWorkItem`, `SpinWait`, and a lock-free `ManualResetEventSlim` fallback.

### 3. `LightMap.Blur` & `TileLightScanner.ExportTo` Closure Allocations & 2D Array Indexing
* **Vanilla issue:**
  * `LightMap.BlurPass()` allocates 4 `ParallelForAction` delegates per `Blur()`, and `BlurLine()` repeatedly bounds-checks `_colors[i]` and `_mask[i]`.
  * `TileLightScanner.ExportTo()` allocates a closure + delegate per scan and performs redundant `WorldGen.InWorld` checks and `Main.tile[x, y]` 2D array lookups for every tile in the ~22,000-tile lighting area.
* **Fix (`LightingPipelinePatch`):**
  * Caches static `ParallelForAction` delegates and uses `unsafe` fixed pointers in `BlurLineUnsafe`.
  * Clamps world bounds once per column and slides 3 vertical tile references down each column in `TileLightScanner.ExportTo`.

---

## Measured Benchmark Results (Real Terraria 1.4.6.1 Binaries)

Measured by `TerrariaTurbo.Benchmarks` loading the actual `Terraria.exe` (1.4.6.1, 3,603 recipes) and `ReLogic.dll` in x86 `.NET Framework 4.8` with `AppDomain.MonitoringIsEnabled = true`:

| Subsystem | Vanilla Mean / Median | Patched Mean / Median | Speedup | Allocation Reduction |
|---|---|---|---|---|
| `Recipe.UpdateRecipeList` (12 chests, 98% idle) | 0.1036 ms / 0.1012 ms | **0.0102 ms / 0.0079 ms** | **10.16x** (12.8x p50) | — |
| `FastParallel.For` (4 × 203 ranges) | 0.0462 ms / 0.0441 ms | **0.0421 ms / 0.0377 ms** | **1.10x** (1.17x p50) | **-60.5%** (5,870 B → 2,321 B) |
| `LightMap.Blur` (176 × 124) | 0.4930 ms / 0.4764 ms | 0.5691 ms / **0.4112 ms** | 1.16x p50 | **-71.9%** (5,242 B → 1,474 B) |
| `TileLightScanner.ExportTo` (176 × 124) | 0.1057 ms / 0.0904 ms | 0.1538 ms / **0.0860 ms** | 1.05x p50 | **-71.5%** (1,433 B → 409 B) |

All equivalence assertions (`FastParallel.For`, `LightMap.Blur` max float delta `8.94E-008`, `TileLightScanner.ExportTo` center & world-edge clamping, and `Recipe.UpdateRecipeList` across inventory/chest edits and crafting) pass against vanilla golden outputs.

---

## Build & Usage

### Prerequisites
* Windows with **Terraria 1.4.6.1** installed (e.g., via Steam).
* **.NET 8 SDK** (`dotnet` CLI) — builds `net48` (`x86`) cleanly using `Microsoft.NETFramework.ReferenceAssemblies`.

### Build
```powershell
dotnet build TerrariaTurbo.sln -c Release
```
*(The build automatically extracts `ReLogic.dll` from your local `Terraria.exe` into `obj/` as a compile-time reference; no game binaries are copied or committed.)*

### Run Verification & Benchmarks
```powershell
.\tests\TerrariaTurbo.Benchmarks\bin\Release\net48\TerrariaTurbo.Benchmarks.exe
```

### Launch Terraria with Patches Active
```powershell
.\src\TerrariaTurbo\bin\Release\net48\TerrariaTurbo.exe --verbose
```

#### CLI Options
* `--terraria-dir="C:\path\to\Terraria"` — Override auto-detected Steam Terraria path
* `--no-parallel` — Disable `FastParallel` patch
* `--no-lighting` — Disable `LightMap` + `TileLightScanner` patch
* `--no-crafting` — Disable `Recipe` + `NearbyChests` + `NewCraftingUI` patch
* `--verbose` — Print session telemetry on exit
