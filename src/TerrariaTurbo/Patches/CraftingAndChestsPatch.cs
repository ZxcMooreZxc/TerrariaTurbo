using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.UI;
using Terraria.GameInput;
using Terraria.UI;
using Terraria.WorldBuilding;
using TerrariaTurbo.Diagnostics;

namespace TerrariaTurbo.Patches
{
    /// <summary>
    /// Fixes per-frame crafting and chest scanning bottlenecks in Terraria 1.4.6.1:
    /// 1) Recipe.UpdateRecipeList() runs every frame unconditionally while inventory/crafting UI is open,
    ///    scanning 8,000 chests, 5,929 bank tiles, 1,000 projectiles, and 3,700 recipes.
    ///    We fingerprint inputs + nearby chest contents and skip redundant recomputations when unchanged.
    /// 2) NearbyChests.GetChestsInRangeOf() replaces linear 8,000-slot Vector2.Distance (sqrt) scans with
    ///    a compact active-chest index + squared-distance filtering.
    /// 3) NearbyChests.GetBanksInRangeOf() hoists the 58-slot Closed Void Bag scan outside the 77x77 tile loop
    ///    and caches static bank tiles when the player has not moved across tiles.
    /// 4) NewCraftingUI.SetupGamepadPoints() skips snap-point/LINQ rebuilding when not using a gamepad.
    /// </summary>
    public static class CraftingAndChestsPatch
    {
        private static AccessTools.FieldRef<List<PositionedChest>> _scratchChestsRef;
        private static AccessTools.FieldRef<Dictionary<int, int>> _ownedItemsRef;
        private static AccessTools.FieldRef<List<Chest>> _recipeChestsRef;

        private delegate void CollectGuideRecipesDelegate();
        private delegate void TryRefocusingRecipeDelegate(int oldRecipe);
        private delegate void CollectItemsDelegate(Item[] currentInventory, int slotCap);
        private delegate void AddFakeCountsDelegate();

        private static CollectGuideRecipesDelegate _collectGuideRecipes;
        private static TryRefocusingRecipeDelegate _tryRefocusingRecipe;
        private static CollectItemsDelegate _collectItems;
        private static AddFakeCountsDelegate _addFakeCounts;

        // Compact active-chest index cache
        private static readonly int[] _activeChestIndices = new int[8000];
        private static int _activeChestCount = -1;
        private static uint _lastChestIndexSweepTick = uint.MaxValue;

        // Static bank tile cache for NearbyChests.GetBanksInRangeOf
        private struct CachedBankTile
        {
            public int ContainerIndex;
            public Vector2 WorldPos;
        }
        private static readonly List<CachedBankTile> _cachedStaticBanks = new List<CachedBankTile>(8);
        private static int _cachedBankTileX = int.MinValue;
        private static int _cachedBankTileY = int.MinValue;
        private static float _cachedBankRange = -1f;
        private static uint _cachedBankTick = uint.MaxValue;

        // Recipe.UpdateRecipeList fingerprint cache
        private static bool _hasCachedRecipeState;
        private static ulong _lastRecipeFingerprint;
        private static int _lastNumAvailable;
        private static bool _cacheDirty = true;

        private static readonly List<PositionedChest> _nearbyChestSnapshot = new List<PositionedChest>(16);

        public static void InvalidateRecipeCache()
        {
            _cacheDirty = true;
            _activeChestCount = -1;
            _cachedBankTileX = int.MinValue;
        }

        public static void Apply(Harmony harmony)
        {
            _scratchChestsRef = AccessTools.StaticFieldRefAccess<List<PositionedChest>>(
                AccessTools.Field(typeof(NearbyChests), "_scratch"));
            _ownedItemsRef = AccessTools.StaticFieldRefAccess<Dictionary<int, int>>(
                AccessTools.Field(typeof(Recipe), "_ownedItems"));
            _recipeChestsRef = AccessTools.StaticFieldRefAccess<List<Chest>>(
                AccessTools.Field(typeof(Recipe), "_recipeChests"));

            _collectGuideRecipes = (CollectGuideRecipesDelegate)Delegate.CreateDelegate(
                typeof(CollectGuideRecipesDelegate),
                AccessTools.Method(typeof(Recipe), "CollectGuideRecipes"));
            _tryRefocusingRecipe = (TryRefocusingRecipeDelegate)Delegate.CreateDelegate(
                typeof(TryRefocusingRecipeDelegate),
                AccessTools.Method(typeof(Recipe), "TryRefocusingRecipe"));
            _collectItems = (CollectItemsDelegate)Delegate.CreateDelegate(
                typeof(CollectItemsDelegate),
                AccessTools.Method(typeof(Recipe), "CollectItems"));
            _addFakeCounts = (AddFakeCountsDelegate)Delegate.CreateDelegate(
                typeof(AddFakeCountsDelegate),
                AccessTools.Method(typeof(Recipe), "AddFakeCountsForItemGroups"));

            harmony.Patch(
                AccessTools.Method(typeof(NearbyChests), nameof(NearbyChests.GetChestsInRangeOf), new[] { typeof(Vector2), typeof(float) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(CraftingAndChestsPatch), nameof(GetChestsInRangeOfPrefix))));

            harmony.Patch(
                AccessTools.Method(typeof(NearbyChests), nameof(NearbyChests.GetBanksInRangeOf), new[] { typeof(Player), typeof(float) }),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(CraftingAndChestsPatch), nameof(GetBanksInRangeOfPrefix))));

            harmony.Patch(
                AccessTools.Method(typeof(Recipe), nameof(Recipe.UpdateRecipeList)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(CraftingAndChestsPatch), nameof(UpdateRecipeListPrefix))));

            harmony.Patch(
                AccessTools.Method(typeof(Recipe), nameof(Recipe.ClearAvailableRecipes)),
                prefix: new HarmonyMethod(AccessTools.Method(typeof(CraftingAndChestsPatch), nameof(ClearAvailableRecipesPrefix))));

            // Invalidate recipe fingerprint whenever crafting mutates _ownedItems or chests change
            harmony.Patch(
                AccessTools.Method(typeof(Recipe), nameof(Recipe.SubtractOwnedItem)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(CraftingAndChestsPatch), nameof(MarkDirty))));
            harmony.Patch(
                AccessTools.Method(typeof(Recipe), nameof(Recipe.ConsumeOwnedItem)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(CraftingAndChestsPatch), nameof(MarkDirty))));

            MethodInfo createChest = AccessTools.Method(typeof(Chest), nameof(Chest.CreateChest));
            if (createChest != null)
                harmony.Patch(createChest, postfix: new HarmonyMethod(AccessTools.Method(typeof(CraftingAndChestsPatch), nameof(MarkChestListDirty))));

            MethodInfo destroyChest = AccessTools.Method(typeof(Chest), nameof(Chest.DestroyChest));
            if (destroyChest != null)
                harmony.Patch(destroyChest, postfix: new HarmonyMethod(AccessTools.Method(typeof(CraftingAndChestsPatch), nameof(MarkChestListDirty))));

            MethodInfo assignChest = AccessTools.Method(typeof(Chest), nameof(Chest.Assign));
            if (assignChest != null)
                harmony.Patch(assignChest, postfix: new HarmonyMethod(AccessTools.Method(typeof(CraftingAndChestsPatch), nameof(MarkChestListDirty))));

            MethodInfo removeChest = AccessTools.Method(typeof(Chest), nameof(Chest.RemoveChest));
            if (removeChest != null)
                harmony.Patch(removeChest, postfix: new HarmonyMethod(AccessTools.Method(typeof(CraftingAndChestsPatch), nameof(MarkChestListDirty))));

            MethodInfo clearChests = AccessTools.Method(typeof(Chest), nameof(Chest.Clear));
            if (clearChests != null)
                harmony.Patch(clearChests, postfix: new HarmonyMethod(AccessTools.Method(typeof(CraftingAndChestsPatch), nameof(MarkChestListDirty))));

            MethodInfo resetRecipes = AccessTools.Method(typeof(NewCraftingUI), "ResetRecipes");
            if (resetRecipes != null)
                harmony.Patch(resetRecipes, postfix: new HarmonyMethod(AccessTools.Method(typeof(CraftingAndChestsPatch), nameof(OnNewCraftingUIReset))));

            MethodInfo setupGamepad = AccessTools.Method(typeof(NewCraftingUI), "SetupGamepadPoints");
            if (setupGamepad != null)
                harmony.Patch(setupGamepad, prefix: new HarmonyMethod(AccessTools.Method(typeof(CraftingAndChestsPatch), nameof(SetupGamepadPointsPrefix))));
        }

        private static void MarkDirty() => _cacheDirty = true;

        private static void MarkChestListDirty()
        {
            _activeChestCount = -1;
            _cacheDirty = true;
        }

        private static void OnNewCraftingUIReset()
        {
            _cacheDirty = true;
        }

        private static bool SetupGamepadPointsPrefix()
        {
            return PlayerInput.UsingGamepad || PlayerInput.UsingGamepadUI;
        }

        private static void RebuildActiveChestIndexIfNeeded()
        {
            Chest[] chests = Main.chest;
            // Rescan all 8,000 slots if invalidated or once every 30 game ticks (~0.5s) in case
            // multiplayer packets or external mods modified Main.chest[] directly.
            uint tick = Main.GameUpdateCount;
            if (_activeChestCount >= 0 && (tick - _lastChestIndexSweepTick) < 30u)
                return;

            int count = 0;
            for (int i = 0; i < 8000; i++)
            {
                if (chests[i] != null)
                {
                    _activeChestIndices[count++] = i;
                }
            }

            _activeChestCount = count;
            _lastChestIndexSweepTick = tick;
        }

        private static bool GetChestsInRangeOfPrefix(Vector2 position, float range, ref List<PositionedChest> __result)
        {
            Interlocked.Increment(ref PerfTelemetry.NearbyChestCalls);

            if (range <= 0f)
                range = 600f;

            float rangeSq = range * range;
            RebuildActiveChestIndexIfNeeded();

            List<PositionedChest> scratch = _scratchChestsRef();
            scratch.Clear();

            Chest[] chests = Main.chest;
            int activeCount = _activeChestCount;
            float px = position.X;
            float py = position.Y;

            for (int idx = 0; idx < activeCount; idx++)
            {
                Chest chest = chests[_activeChestIndices[idx]];
                if (chest == null)
                    continue;

                float cx = chest.x * 16 + 16;
                float cy = chest.y * 16 + 16;
                float dx = cx - px;
                float dy = cy - py;
                if (dx * dx + dy * dy <= rangeSq)
                {
                    scratch.Add(new PositionedChest(chest, new Vector2(cx, cy)));
                }
            }

            __result = scratch;
            return false;
        }

        private static bool PlayerHasClosedVoidBag(Player player)
        {
            Item[] inv = player.inventory;
            for (int i = 0; i < 58; i++)
            {
                Item item = inv[i];
                if (item.stack > 0 && item.type == 5325)
                    return true;
            }
            return false;
        }

        private static bool TryGetBankForContainer(Player player, int container, bool hasClosedVoidBag, out Chest bank)
        {
            switch (container)
            {
                case -2:
                    bank = player.bank;
                    return true;
                case -3:
                    bank = player.bank2;
                    return true;
                case -4:
                    bank = player.bank3;
                    return true;
                case -5:
                    if (hasClosedVoidBag)
                    {
                        bank = null;
                        return false;
                    }
                    bank = player.bank4;
                    return true;
                default:
                    bank = null;
                    return false;
            }
        }

        private static bool ListContainsChest(List<PositionedChest> list, Chest chest)
        {
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].chest == chest)
                    return true;
            }
            return false;
        }

        private static bool GetBanksInRangeOfPrefix(Player player, float range, ref List<PositionedChest> __result)
        {
            Interlocked.Increment(ref PerfTelemetry.NearbyBankCalls);

            if (range <= 0f)
                range = 600f;

            List<PositionedChest> scratch = _scratchChestsRef();
            scratch.Clear();

            int tileRange = (int)(range / 16f + 2f);
            Point centerTile = player.Center.ToTileCoordinates();
            int left = centerTile.X - tileRange;
            int top = centerTile.Y - tileRange;
            int right = centerTile.X + tileRange + 1;
            int bottom = centerTile.Y + tileRange + 1;

            bool hasClosedVoidBag = PlayerHasClosedVoidBag(player);

            // 1. Scan active container projectiles (Money Trough, Void Bag, Chester, etc.)
            Projectile[] projectiles = Main.projectile;
            Vector2 playerCenter = player.Center;
            for (int i = 0; i < 1000; i++)
            {
                Projectile proj = projectiles[i];
                if (!proj.active)
                    continue;

                if (proj.TryGetContainerIndex(out int containerIndex))
                {
                    Rectangle hb = proj.Hitbox;
                    float cx = MathHelper.Clamp(playerCenter.X, hb.Left, hb.Right);
                    float cy = MathHelper.Clamp(playerCenter.Y, hb.Top, hb.Bottom);
                    int tx = (int)(cx / 16f);
                    int ty = (int)(cy / 16f);
                    if (tx >= left && tx < right && ty >= top && ty < bottom)
                    {
                        if (TryGetBankForContainer(player, containerIndex, hasClosedVoidBag, out Chest bank) &&
                            !ListContainsChest(scratch, bank))
                        {
                            scratch.Add(new PositionedChest(bank, proj.Center));
                        }
                    }
                }
            }

            // 2. Scan nearby bank tiles only when player crossed a tile or every 15 ticks (in case a bank tile was placed/broken)
            uint tick = Main.GameUpdateCount;
            if (_cachedBankTileX != centerTile.X ||
                _cachedBankTileY != centerTile.Y ||
                _cachedBankRange != range ||
                (tick - _cachedBankTick) >= 15u)
            {
                _cachedStaticBanks.Clear();
                Tile[,] tiles = Main.tile;
                int clampedLeft = left < 0 ? 0 : left;
                int clampedTop = top < 0 ? 0 : top;
                int clampedRight = right > Main.maxTilesX ? Main.maxTilesX : right;
                int clampedBottom = bottom > Main.maxTilesY ? Main.maxTilesY : bottom;

                // Track bitmask of found bank containers (-2..-5 -> bits 0..3) so we avoid duplicate entries
                int foundMask = 0;

                for (int x = clampedLeft; x < clampedRight; x++)
                {
                    for (int y = clampedTop; y < clampedBottom; y++)
                    {
                        Tile t = tiles[x, y];
                        if (t == null) continue;

                        int container;
                        int bit;
                        switch (t.type)
                        {
                            case 29:  container = -2; bit = 1; break;
                            case 97:  container = -3; bit = 2; break;
                            case 463: container = -4; bit = 4; break;
                            case 491: container = -5; bit = 8; break;
                            default: continue;
                        }

                        if ((foundMask & bit) != 0)
                            continue;

                        foundMask |= bit;
                        _cachedStaticBanks.Add(new CachedBankTile
                        {
                            ContainerIndex = container,
                            WorldPos = new Vector2(x * 16 + 16, y * 16 + 16)
                        });

                        if (foundMask == 15)
                            goto DoneTileScan;
                    }
                }

            DoneTileScan:
                _cachedBankTileX = centerTile.X;
                _cachedBankTileY = centerTile.Y;
                _cachedBankRange = range;
                _cachedBankTick = tick;
            }

            for (int i = 0; i < _cachedStaticBanks.Count; i++)
            {
                CachedBankTile entry = _cachedStaticBanks[i];
                if (TryGetBankForContainer(player, entry.ContainerIndex, hasClosedVoidBag, out Chest bank) &&
                    !ListContainsChest(scratch, bank))
                {
                    scratch.Add(new PositionedChest(bank, entry.WorldPos));
                }
            }

            __result = scratch;
            return false;
        }

        private static bool ClearAvailableRecipesPrefix()
        {
            int clearLen = _lastNumAvailable > Main.numAvailableRecipes ? _lastNumAvailable : Main.numAvailableRecipes;
            if (clearLen > Recipe.maxRecipes) clearLen = Recipe.maxRecipes;
            if (clearLen > 0)
                Array.Clear(Main.availableRecipe, 0, clearLen);
            Main.numAvailableRecipes = 0;
            return false;
        }

        private static ulong Mix(ulong h, ulong v)
        {
            h ^= v + 0x9e3779b97f4a7c15UL + (h << 6) + (h >> 2);
            return h;
        }

        private static ulong HashItems(ulong h, Item[] items, int cap)
        {
            for (int i = 0; i < cap; i++)
            {
                Item it = items[i];
                int st = it.stack;
                if (st > 0)
                {
                    h = Mix(h, ((ulong)(uint)i << 40) | ((ulong)(uint)it.type << 20) | (uint)st);
                }
            }
            return h;
        }

        private static ulong ComputeFingerprint(
            Player localPlayer,
            NewCraftingUI.RecipeFilter filterHack,
            bool isGuideMode,
            List<PositionedChest> nearbyChestsOut)
        {
            ulong h = 0x517cc1b727220a95UL;

            h = Mix(h, isGuideMode ? 1UL : 0UL);
            if (isGuideMode)
            {
                h = Mix(h, (uint)Main.guideItem.type);
                h = Mix(h, (uint)Main.guideItem.stack);
                h = Mix(h, (uint)Main.guideItem.Name.GetHashCode());
                return h;
            }

            h = Mix(h, (ulong)(localPlayer.chest + 10));
            h = Mix(h, localPlayer.useVoidBag() ? 1UL : 0UL);
            h = Mix(h, Player.Settings.CraftFromNearbyChests ? 1UL : 0UL);
            h = Mix(h, CraftingRequests.HasPendingRequests ? 1UL : 0UL);
            h = Mix(h, filterHack != null ? (ulong)filterHack.GetHashCode() : 0UL);

            // Environment / biome / liquid crafting flags
            ulong envFlags =
                (localPlayer.adjWaterSource ? 1UL : 0UL) |
                (localPlayer.adjHoney ? 2UL : 0UL) |
                (localPlayer.adjLava ? 4UL : 0UL) |
                (localPlayer.ZoneSnow ? 8UL : 0UL) |
                (localPlayer.ZoneGraveyard ? 16UL : 0UL) |
                (SpecialSeedFeatures.Mechdusa ? 32UL : 0UL) |
                (localPlayer.unlockedBiomeTorches ? 64UL : 0UL);
            h = Mix(h, envFlags);

            // Pack adjTile[] flags 64 bits at a time
            bool[] adjTile = localPlayer.adjTile;
            int adjLen = adjTile.Length;
            for (int baseIdx = 0; baseIdx < adjLen; baseIdx += 64)
            {
                ulong word = 0UL;
                int limit = baseIdx + 64 < adjLen ? 64 : adjLen - baseIdx;
                for (int bit = 0; bit < limit; bit++)
                {
                    if (adjTile[baseIdx + bit])
                        word |= 1UL << bit;
                }
                if (word != 0UL)
                    h = Mix(h, word ^ ((ulong)baseIdx << 1));
            }

            // Player inventory
            h = HashItems(h, localPlayer.inventory, 58);

            // Collect chest list using optimized NearbyChests
            nearbyChestsOut.Clear();
            List<Chest> recipeChests = _recipeChestsRef();
            // Build candidate chest list in a temporary buffer without clobbering _recipeChests until we know if it's a miss
            if (localPlayer.chest != -1)
            {
                Chest curr = localPlayer.GetCurrentContainer();
                if (curr != null)
                    nearbyChestsOut.Add(new PositionedChest(curr, Vector2.Zero));
            }
            if (localPlayer.useVoidBag() && localPlayer.chest != -5)
            {
                AddUniqueChest(nearbyChestsOut, localPlayer.bank4);
            }
            if (Player.Settings.CraftFromNearbyChests)
            {
                List<PositionedChest> banks = NearbyChests.GetBanksInRangeOf(localPlayer);
                for (int i = 0; i < banks.Count; i++)
                    AddUniqueChest(nearbyChestsOut, banks[i].chest);

                List<PositionedChest> chests = NearbyChests.GetChestsInRangeOf(localPlayer.position);
                for (int i = 0; i < chests.Count; i++)
                {
                    Chest c = chests[i].chest;
                    if (!c.IsLockedOrInUse())
                        nearbyChestsOut.Add(new PositionedChest(c, chests[i].position));
                }
            }

            // Hash contents of every chest in range
            h = Mix(h, (ulong)nearbyChestsOut.Count);
            for (int i = 0; i < nearbyChestsOut.Count; i++)
            {
                Chest c = nearbyChestsOut[i].chest;
                h = Mix(h, (ulong)(uint)(c.x | (c.y << 16)));
                h = HashItems(h, c.item, c.maxItems);
            }

            return h;
        }

        private static void AddUniqueChest(List<PositionedChest> target, Chest chest)
        {
            for (int i = 0; i < target.Count; i++)
            {
                if (target[i].chest == chest)
                    return;
            }
            target.Add(new PositionedChest(chest, Vector2.Zero));
        }

        private static bool UpdateRecipeListPrefix()
        {
            Interlocked.Increment(ref PerfTelemetry.RecipeUpdateCalls);

            bool isGuideMode = !Main.guideItem.IsAir && Main.guideItem.Name != "";
            Player localPlayer = Main.LocalPlayer;
            NewCraftingUI.RecipeFilter filterHack = CraftingUI.RecipeFilterHack;

            ulong fp = ComputeFingerprint(localPlayer, filterHack, isGuideMode, _nearbyChestSnapshot);

            if (!_cacheDirty && _hasCachedRecipeState && fp == _lastRecipeFingerprint && !CraftingRequests.HasPendingRequests)
            {
                Interlocked.Increment(ref PerfTelemetry.RecipeCacheHits);
                return false;
            }

            Interlocked.Increment(ref PerfTelemetry.RecipeCacheMisses);
            _cacheDirty = false;
            _hasCachedRecipeState = true;
            _lastRecipeFingerprint = fp;

            int focusRecipe = Main.focusRecipe;
            int oldRecipe = Main.availableRecipe[Main.focusRecipe];

            ClearAvailableRecipesPrefix();

            if (isGuideMode)
            {
                _collectGuideRecipes();
                _tryRefocusingRecipe(oldRecipe);
                _lastNumAvailable = Main.numAvailableRecipes;
                Main.craftingUI.VisuallyRepositionRecipes(focusRecipe);
                return false;
            }

            Dictionary<int, int> ownedItems = _ownedItemsRef();
            ownedItems.Clear();

            List<Chest> recipeChests = _recipeChestsRef();
            recipeChests.Clear();

            _collectItems(localPlayer.inventory, 58);
            for (int i = 0; i < _nearbyChestSnapshot.Count; i++)
            {
                Chest c = _nearbyChestSnapshot[i].chest;
                recipeChests.Add(c);
                _collectItems(c.item, c.maxItems);
            }

            _addFakeCounts();
            CraftingRequests.SubtractPendingRequests();

            Recipe[] recipes = Main.recipe;
            int max = Recipe.maxRecipes;
            for (int i = 0; i < max; i++)
            {
                Recipe recipe = recipes[i];
                if (recipe.createItem.type == 0)
                    break;

                if ((filterHack == null || filterHack.Accepts(recipe)) &&
                    recipe.PlayerMeetsEnvironmentConditions(localPlayer) &&
                    Recipe.CollectedEnoughItemsToCraft(recipe))
                {
                    Main.availableRecipe[Main.numAvailableRecipes++] = i;
                }
            }

            _tryRefocusingRecipe(oldRecipe);
            _lastNumAvailable = Main.numAvailableRecipes;
            _lastRecipeFingerprint = fp; // Keep fingerprint matching current state (focusRecipe could shift on miss)
            // Update focusRecipe into fingerprint so user scrolling focusRecipe doesn't invalidate if needed?
            // Wait: if user scrolls focusRecipe in Classic CraftingUI, Recipe.UpdateRecipeList doesn't need full recompute,
            // EXCEPT if oldRecipe == Main.availableRecipe[Main.focusRecipe] changes! Actually, focusRecipe ONLY changes
            // how TryRefocusingRecipe preserves focus when recipe list changes, NOT which recipes exist!
            Main.craftingUI.VisuallyRepositionRecipes(focusRecipe);
            return false;
        }
    }
}
