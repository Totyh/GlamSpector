using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Dalamud.Game.Inventory;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamSpector.Models;
using Lumina.Excel.Sheets;
using CabinetSheet = Lumina.Excel.Sheets.Cabinet;

namespace GlamSpector.Services;

/// <summary>
/// Best-effort ownership lookup for the current character. Positive matches are
/// reliable; a missing result is deliberately shown as unknown because unloaded
/// retainers (and other account/character storage) can still contain the item.
///
/// M3.7 additionally consumes FFXIV's own ItemFinder cache for the Glamour
/// Dresser and saddlebags, and checks the Armoire whenever its server data is
/// loaded. This mirrors the data the game's /isearch feature keeps locally.
/// </summary>
public sealed class InventoryOwnershipService
{
    private readonly IGameInventory inventory;
    private readonly IDataManager dataManager;
    private readonly IPlayerState playerState;
    private readonly AllaganToolsOwnershipService allaganTools;
    private readonly string persistentCachePath;
    private readonly Dictionary<uint, uint> cabinetRowByItem = [];
    private readonly Dictionary<uint, HashSet<string>> locationsByItem = [];
    private readonly HashSet<uint> directDresserItemIds = [];
    private readonly HashSet<uint> expandedDresserItemIds = [];
    private readonly Dictionary<ulong, PersistentDresserCacheEntry> persistentDresserCaches = [];
    private ulong expandedDresserContentId;
    private DateTime? expandedDresserUpdatedUtc;
    private bool expandedDresserLoadedFromDisk;
    private DateTime lastRefreshUtc = DateTime.MinValue;

    public bool GlamourDresserCached { get; private set; }
    public bool SaddlebagCached { get; private set; }
    public bool ArmoireLoaded { get; private set; }
    public int GlamourDresserOutfitSlotCount { get; private set; }
    public bool ExpandedGlamourDresserCached => expandedDresserItemIds.Count > 0;
    public int ExpandedGlamourDresserItemCount => expandedDresserItemIds.Count;
    public bool ExpandedGlamourDresserLoadedFromDisk => expandedDresserLoadedFromDisk;
    public DateTime? ExpandedGlamourDresserUpdatedUtc => expandedDresserUpdatedUtc;

    private sealed class PersistentDresserCacheFile
    {
        public int Version { get; set; } = 1;
        public Dictionary<string, PersistentDresserCacheEntry> Characters { get; set; } = [];
    }

    private sealed class PersistentDresserCacheEntry
    {
        public DateTime UpdatedAtUtc { get; set; }
        public List<uint> ItemIds { get; set; } = [];
    }

    private static readonly (GameInventoryType Type, string Label)[] Containers =
    [
        (GameInventoryType.Inventory1, "Inventory"),
        (GameInventoryType.Inventory2, "Inventory"),
        (GameInventoryType.Inventory3, "Inventory"),
        (GameInventoryType.Inventory4, "Inventory"),
        (GameInventoryType.EquippedItems, "Equipped"),
        (GameInventoryType.ArmoryMainHand, "Armoury"),
        (GameInventoryType.ArmoryOffHand, "Armoury"),
        (GameInventoryType.ArmoryHead, "Armoury"),
        (GameInventoryType.ArmoryBody, "Armoury"),
        (GameInventoryType.ArmoryHands, "Armoury"),
        (GameInventoryType.ArmoryLegs, "Armoury"),
        (GameInventoryType.ArmoryFeets, "Armoury"),
        (GameInventoryType.ArmoryEar, "Armoury"),
        (GameInventoryType.ArmoryNeck, "Armoury"),
        (GameInventoryType.ArmoryWrist, "Armoury"),
        (GameInventoryType.ArmoryRings, "Armoury"),
        (GameInventoryType.SaddleBag1, "Saddlebag"),
        (GameInventoryType.SaddleBag2, "Saddlebag"),
        (GameInventoryType.PremiumSaddleBag1, "Premium Saddlebag"),
        (GameInventoryType.PremiumSaddleBag2, "Premium Saddlebag"),
        (GameInventoryType.RetainerPage1, "Loaded retainer"),
        (GameInventoryType.RetainerPage2, "Loaded retainer"),
        (GameInventoryType.RetainerPage3, "Loaded retainer"),
        (GameInventoryType.RetainerPage4, "Loaded retainer"),
        (GameInventoryType.RetainerPage5, "Loaded retainer"),
        (GameInventoryType.RetainerPage6, "Loaded retainer"),
        (GameInventoryType.RetainerPage7, "Loaded retainer"),
        (GameInventoryType.RetainerEquippedItems, "Loaded retainer"),
        (GameInventoryType.RetainerMarket, "Retainer market"),
    ];

    public InventoryOwnershipService(
        IGameInventory inventory,
        IDataManager dataManager,
        IPlayerState playerState,
        string persistentCachePath,
        AllaganToolsOwnershipService allaganTools)
    {
        this.inventory = inventory;
        this.dataManager = dataManager;
        this.playerState = playerState;
        this.persistentCachePath = persistentCachePath;
        this.allaganTools = allaganTools;
        LoadPersistentDresserCaches();

        // Cabinet.IsItemInCabinet wants a Cabinet-row ID rather than an Item ID.
        // Build the mapping once from game data.
        try
        {
            foreach (var row in dataManager.GetExcelSheet<CabinetSheet>())
            {
                var itemId = row.Item.RowId;
                if (itemId != 0)
                    cabinetRowByItem[itemId] = row.RowId;
            }
        }
        catch
        {
            // Armoire checks will simply remain unavailable if the sheet cannot
            // be read for some reason. Inventory/Dresser checks still work.
        }
    }

    public void RefreshIfStale(TimeSpan? maxAge = null)
    {
        // All ownership sources are local client memory/caches. A modest cache
        // interval avoids repeatedly walking inventory/cabinet data while the
        // Library is being drawn. Character changes are handled immediately so
        // a persisted Outfit cache is restored as soon as IPlayerState is ready.
        var previousContentId = expandedDresserContentId;
        EnsureExpandedDresserCacheBelongsToCurrentCharacter();
        var characterChanged = expandedDresserContentId != 0 && expandedDresserContentId != previousContentId;

        var age = maxAge ?? TimeSpan.FromSeconds(10);
        if (characterChanged || DateTime.UtcNow - lastRefreshUtc >= age)
            Refresh();
    }

    public void Refresh()
    {
        EnsureExpandedDresserCacheBelongsToCurrentCharacter();
        locationsByItem.Clear();
        directDresserItemIds.Clear();
        GlamourDresserCached = false;
        SaddlebagCached = false;
        ArmoireLoaded = false;
        GlamourDresserOutfitSlotCount = 0;
        var loadedRetainerName = TryGetLoadedRetainerName();

        foreach (var (type, label) in Containers)
        {
            try
            {
                var items = inventory.GetInventoryItems(type);
                for (var i = 0; i < items.Length; i++)
                {
                    var itemId = items[i].BaseItemId;
                    if (itemId != 0)
                    {
                        var effectiveLabel = label == "Loaded retainer" && !string.IsNullOrWhiteSpace(loadedRetainerName)
                            ? $"Retainer: {loadedRetainerName}"
                            : label;
                        AddLocation(itemId, effectiveLabel);
                    }
                }
            }
            catch
            {
                // Some containers only exist while their subsystem is loaded.
            }
        }

        RefreshItemFinderCache();
        RefreshLiveGlamourDresser();
        ApplyExpandedGlamourDresserCache();
        RefreshArmoire();
        lastRefreshUtc = DateTime.UtcNow;
    }

    private static unsafe string? TryGetLoadedRetainerName()
    {
        try
        {
            // FFXIVClientStructs API 15 exposes the current RetainerManager
            // active retainer and its generated NameString property. No custom
            // signature/hook or external-plugin data is involved. The value is
            // used only for this live ownership refresh and is never persisted.
            var manager = RetainerManager.Instance();
            var retainer = manager == null ? null : manager->GetActiveRetainer();
            var name = retainer == null ? null : retainer->NameString;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch
        {
            return null;
        }
    }

    private unsafe void RefreshItemFinderCache()
    {
        try
        {
            var finder = ItemFinderModule.Instance();
            if (finder == null)
                return;

            SaddlebagCached = finder->IsSaddleBagCached;
            if (SaddlebagCached)
            {
                for (var i = 0; i < 70; i++)
                {
                    var normal = finder->SaddleBagItemIds[i];
                    var premium = finder->PremiumSaddleBagItemIds[i];
                    if (normal != 0)
                        AddLocation(normal, "Saddlebag");
                    if (premium != 0)
                        AddLocation(premium, "Premium Saddlebag");
                }
            }

            GlamourDresserCached = finder->IsGlamourDresserCached;
            if (GlamourDresserCached)
            {
                for (var i = 0; i < 800; i++)
                {
                    var itemId = finder->GlamourDresserItemIds[i];
                    var setUnlockBits = finder->GlamourDresserItemSetUnlockBits[i];
                    if (itemId != 0)
                    {
                        directDresserItemIds.Add(itemId);
                        AddLocation(itemId, "Glamour Dresser");
                    }
                    if (itemId != 0 && setUnlockBits != 0)
                        GlamourDresserOutfitSlotCount++;
                }
            }
        }
        catch
        {
            // ItemFinder is a cache; an unavailable cache should never break UI.
        }
    }


    private void EnsureExpandedDresserCacheBelongsToCurrentCharacter()
    {
        try
        {
            var contentId = playerState.IsLoaded ? playerState.ContentId : 0UL;
            if (contentId == 0)
            {
                expandedDresserItemIds.Clear();
                expandedDresserContentId = 0;
                expandedDresserUpdatedUtc = null;
                expandedDresserLoadedFromDisk = false;
                return;
            }

            if (expandedDresserContentId == contentId)
                return;

            expandedDresserItemIds.Clear();
            expandedDresserContentId = contentId;
            expandedDresserUpdatedUtc = null;
            expandedDresserLoadedFromDisk = false;

            if (!persistentDresserCaches.TryGetValue(contentId, out var cached) || cached.ItemIds.Count == 0)
                return;

            expandedDresserItemIds.UnionWith(cached.ItemIds.Where(x => x != 0));
            expandedDresserUpdatedUtc = cached.UpdatedAtUtc;
            expandedDresserLoadedFromDisk = expandedDresserItemIds.Count > 0;
        }
        catch
        {
            // If the player-state wrapper is unavailable for a frame, keep the
            // current same-character cache rather than throwing it away.
        }
    }

    private void LoadPersistentDresserCaches()
    {
        try
        {
            if (!File.Exists(persistentCachePath))
                return;

            var json = File.ReadAllText(persistentCachePath);
            var file = JsonSerializer.Deserialize<PersistentDresserCacheFile>(json);
            if (file?.Characters == null)
                return;

            foreach (var (key, value) in file.Characters)
            {
                if (ulong.TryParse(key, out var contentId) && contentId != 0 && value?.ItemIds != null)
                    persistentDresserCaches[contentId] = value;
            }
        }
        catch
        {
            // A corrupt/missing optional ownership cache must never prevent the
            // plugin from loading. Opening the Dresser again will rebuild it.
        }
    }

    private void SavePersistentDresserCache(ulong contentId, IReadOnlyCollection<uint> itemIds)
    {
        if (contentId == 0 || itemIds.Count == 0)
            return;

        try
        {
            var updated = DateTime.UtcNow;
            persistentDresserCaches[contentId] = new PersistentDresserCacheEntry
            {
                UpdatedAtUtc = updated,
                ItemIds = itemIds.Where(x => x != 0).Distinct().OrderBy(x => x).ToList(),
            };

            var file = new PersistentDresserCacheFile
            {
                Characters = persistentDresserCaches.ToDictionary(
                    x => x.Key.ToString(),
                    x => x.Value),
            };

            var directory = Path.GetDirectoryName(persistentCachePath);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var tempPath = persistentCachePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tempPath, persistentCachePath, true);

            expandedDresserUpdatedUtc = updated;
            expandedDresserLoadedFromDisk = false;
        }
        catch
        {
            // Ownership remains usable for the current session even if the
            // optional persistent cache cannot be written.
        }
    }

    /// <summary>
    /// When the native Glamour Dresser is open, its PrismBox data exposes the
    /// expanded list of usable items, including individual pieces stored inside
    /// Outfit Glamours. Cache that expanded list for the current character so
    /// ownership stays useful after the dresser window is closed.
    /// </summary>
    private unsafe void RefreshLiveGlamourDresser()
    {
        try
        {
            var agentModule = AgentModule.Instance();
            var agent = agentModule == null
                ? null
                : (AgentMiragePrismPrismBox*)agentModule->GetAgentByInternalId(AgentId.MiragePrismPrismBox);

            if (agent == null || !agent->IsDataLoaded || agent->Data == null)
                return;

            var data = agent->Data;
            if (!data->IsAsyncLoadComplete || !data->IsPopulatingComplete)
                return;

            var fresh = new HashSet<uint>();
            for (var i = 0; i < 8000; i++)
            {
                var itemId = data->PrismBoxItems[i].ItemId;
                if (itemId != 0)
                    fresh.Add(itemId);
            }

            if (fresh.Count == 0)
                return;

            expandedDresserItemIds.Clear();
            expandedDresserItemIds.UnionWith(fresh);
            if (playerState.IsLoaded)
            {
                expandedDresserContentId = playerState.ContentId;
                SavePersistentDresserCache(expandedDresserContentId, fresh);
            }
        }
        catch
        {
            // The expanded list is an optional live source. Keep the previous
            // same-character session cache if the Dresser is closing/loading.
        }
    }

    private void ApplyExpandedGlamourDresserCache()
    {
        foreach (var itemId in expandedDresserItemIds)
        {
            // ItemFinder already covers ordinary top-level dresser entries.
            // Items present only in the expanded PrismBox list are the important
            // case here: pieces bundled into Outfit Glamours.
            AddLocation(itemId, directDresserItemIds.Contains(itemId)
                ? "Glamour Dresser"
                : "Glamour Dresser (Outfit)");
        }
    }

    private unsafe void RefreshArmoire()
    {
        try
        {
            var uiState = UIState.Instance();
            if (uiState == null || !uiState->Cabinet.IsCabinetLoaded())
                return;

            ArmoireLoaded = true;
            foreach (var (itemId, cabinetRowId) in cabinetRowByItem)
            {
                if (uiState->Cabinet.IsItemInCabinet(cabinetRowId))
                    AddLocation(itemId, "Armoire");
            }
        }
        catch
        {
            ArmoireLoaded = false;
        }
    }

    private void AddLocation(uint itemId, string label)
    {
        if (!locationsByItem.TryGetValue(itemId, out var locations))
        {
            locations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            locationsByItem[itemId] = locations;
        }

        locations.Add(label);
    }

    /// <summary>
    /// Writes a local-only diagnostic of FFXIV's cached Glamour Dresser slots.
    /// Outfit Glamours are represented by one dresser item plus a parallel
    /// per-slot unlock bitfield, so the normal direct-item cache is insufficient
    /// for resolving their constituent pieces. This diagnostic helps us map
    /// that bitfield without invoking /isearch or making any server request.
    /// </summary>
    public unsafe string BuildGlamourDresserOutfitDiagnostics()
    {
        var sb = new StringBuilder();
        sb.AppendLine("GlamSpector Glamour Dresser Outfit diagnostic");
        sb.AppendLine($"Generated UTC: {DateTime.UtcNow:O}");
        sb.AppendLine("This report only reads FFXIV client memory/game data; it does not run /isearch or query the server.");
        sb.AppendLine();

        try
        {
            var finder = ItemFinderModule.Instance();
            if (finder == null)
            {
                sb.AppendLine("ItemFinderModule: unavailable");
                return sb.ToString();
            }

            sb.AppendLine($"IsGlamourDresserCached={finder->IsGlamourDresserCached}");
            if (!finder->IsGlamourDresserCached)
            {
                sb.AppendLine("Open the Glamour Dresser once, then run the diagnostic again.");
                return sb.ToString();
            }

            var itemSheet = dataManager.GetExcelSheet<Item>();
            var outfitIds = new HashSet<uint>();

            sb.AppendLine();
            sb.AppendLine("Dresser slots with non-zero Outfit unlock bits:");
            for (var i = 0; i < 800; i++)
            {
                var itemId = finder->GlamourDresserItemIds[i];
                var bits = finder->GlamourDresserItemSetUnlockBits[i];
                if (itemId == 0 || bits == 0)
                    continue;

                outfitIds.Add(itemId);
                var name = itemSheet.TryGetRow(itemId, out var item) ? item.Name.ToString() : $"Item #{itemId}";
                var filterGroup = itemSheet.TryGetRow(itemId, out var item2) ? item2.FilterGroup.ToString() : "?";
                var additionalData = itemSheet.TryGetRow(itemId, out var item3) ? DescribeAdditionalData(item3) : "?";
                sb.AppendLine($"slot={i:000} itemId={itemId} name=\"{name}\" filterGroup={filterGroup} additionalData={additionalData} unlockBits=0x{bits:X4} binary={Convert.ToString(bits, 2).PadLeft(16, '0')}");
            }

            sb.AppendLine();
            sb.AppendLine($"Outfit-like cached slots: {outfitIds.Count}");
            if (outfitIds.Count == 0)
                sb.AppendLine("No slots with non-zero Outfit unlock bits were found in the current cache.");

            // Experimental clue only: check whether any ordinary Item row's
            // AdditionalData value happens to point at one of the cached Outfit
            // item IDs. We do not use this for ownership yet; the report lets us
            // validate the relationship first instead of guessing.
            if (outfitIds.Count > 0)
            {
                var candidates = new Dictionary<uint, List<string>>();
                foreach (var outfitId in outfitIds)
                    candidates[outfitId] = [];

                foreach (var row in itemSheet)
                {
                    var raw = TryGetAdditionalDataRowId(row);
                    if (raw == 0 || !candidates.TryGetValue(raw, out var list))
                        continue;
                    list.Add($"{row.RowId}:{row.Name}");
                }

                sb.AppendLine();
                sb.AppendLine("Item rows whose AdditionalData points at a cached Outfit ID (diagnostic clue only):");
                foreach (var outfitId in outfitIds.OrderBy(x => x))
                {
                    var outfitName = itemSheet.TryGetRow(outfitId, out var outfit) ? outfit.Name.ToString() : $"Item #{outfitId}";
                    var list = candidates[outfitId];
                    sb.AppendLine($"outfit {outfitId} \"{outfitName}\": {(list.Count == 0 ? "(none)" : string.Join(", ", list.Take(30)))}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("Live Glamour Dresser agent diagnostic:");
            try
            {
                var agentModule = AgentModule.Instance();
                var prismAgent = agentModule == null
                    ? null
                    : (AgentMiragePrismPrismBox*)agentModule->GetAgentByInternalId(AgentId.MiragePrismPrismBox);

                if (prismAgent == null)
                {
                    sb.AppendLine("AgentMiragePrismPrismBox: unavailable");
                }
                else
                {
                    sb.AppendLine($"IsDataLoaded={prismAgent->IsDataLoaded}");
                    if (prismAgent->Data == null)
                    {
                        sb.AppendLine("Data: null (open the Glamour Dresser, then run the diagnostic again)");
                    }
                    else
                    {
                        var data = prismAgent->Data;
                        sb.AppendLine($"IsAsyncLoadComplete={data->IsAsyncLoadComplete} IsPopulatingComplete={data->IsPopulatingComplete} IsAddonReady={data->IsAddonReady} UsedSlots={data->UsedSlots} ItemCount={data->ItemCount}");

                        var nonZero = 0;
                        var expandedMarkers = 0;
                        var targetRows = itemSheet
                            .Where(x => x.Name.ToString().Contains("Scion Traveler", StringComparison.OrdinalIgnoreCase))
                            .Select(x => (x.RowId, Name: x.Name.ToString()))
                            .ToList();

                        var matches = new Dictionary<uint, List<string>>();
                        foreach (var target in targetRows)
                            matches[target.RowId] = [];

                        var expandedSamples = new List<string>();
                        for (var i = 0; i < 8000; i++)
                        {
                            var entry = data->PrismBoxItems[i];
                            if (entry.ItemId == 0)
                                continue;

                            nonZero++;
                            var entryName = entry.Name.ToString();
                            if (entry.NumOutfitPiecesAdded > 0)
                            {
                                expandedMarkers++;
                                if (expandedSamples.Count < 80)
                                    expandedSamples.Add($"index={i:0000} slot={entry.Slot} itemId={entry.ItemId} name=\"{entryName}\" numOutfitPiecesAdded={entry.NumOutfitPiecesAdded}");
                            }

                            if (matches.TryGetValue(entry.ItemId, out var list))
                                list.Add($"index={i:0000} slot={entry.Slot} name=\"{entryName}\" numOutfitPiecesAdded={entry.NumOutfitPiecesAdded}");
                            else if (entryName.Contains("Scion Traveler", StringComparison.OrdinalIgnoreCase))
                                sb.AppendLine($"NAME-MATCH index={i:0000} slot={entry.Slot} itemId={entry.ItemId} name=\"{entryName}\" numOutfitPiecesAdded={entry.NumOutfitPiecesAdded}");
                        }

                        sb.AppendLine($"Non-zero PrismBoxItems={nonZero}; entries with NumOutfitPiecesAdded>0={expandedMarkers}");
                        sb.AppendLine("Scion Traveler Item rows vs live PrismBoxItems:");
                        foreach (var target in targetRows)
                        {
                            var list = matches[target.RowId];
                            sb.AppendLine($"item {target.RowId} \"{target.Name}\": {(list.Count == 0 ? "(not present)" : string.Join(" | ", list))}");
                        }

                        sb.AppendLine("Sample entries with NumOutfitPiecesAdded>0:");
                        foreach (var line in expandedSamples)
                            sb.AppendLine(line);
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine($"Live dresser diagnostic failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine();
            sb.AppendLine($"Diagnostic failed: {ex.GetType().Name}: {ex.Message}");
        }

        return sb.ToString();
    }

    private static string DescribeAdditionalData(Item row)
    {
        try
        {
            var value = typeof(Item).GetProperty("AdditionalData", BindingFlags.Public | BindingFlags.Instance)?.GetValue(row);
            if (value is null)
                return "null";
            var rowId = ExtractRowId(value);
            return rowId != 0 ? rowId.ToString() : value.ToString() ?? "0";
        }
        catch
        {
            return "?";
        }
    }

    private static uint TryGetAdditionalDataRowId(Item row)
    {
        try
        {
            var value = typeof(Item).GetProperty("AdditionalData", BindingFlags.Public | BindingFlags.Instance)?.GetValue(row);
            return value is null ? 0 : ExtractRowId(value);
        }
        catch
        {
            return 0;
        }
    }

    private static uint ExtractRowId(object value)
    {
        if (value is byte b) return b;
        if (value is ushort us) return us;
        if (value is uint ui) return ui;
        if (value is int i && i > 0) return (uint)i;
        if (value is long l && l > 0 && l <= uint.MaxValue) return (uint)l;

        var prop = value.GetType().GetProperty("RowId", BindingFlags.Public | BindingFlags.Instance);
        var rowIdValue = prop?.GetValue(value);
        return rowIdValue switch
        {
            byte rb => rb,
            ushort rus => rus,
            uint rui => rui,
            int ri when ri > 0 => (uint)ri,
            long rl when rl > 0 && rl <= uint.MaxValue => (uint)rl,
            _ => 0,
        };
    }

    public InventoryOwnership Get(uint itemId)
    {
        var native = GetNative(itemId);
        if (native.Owned)
            return native;

        if (allaganTools.TryGetOwned(itemId))
        {
            return new InventoryOwnership
            {
                Owned = true,
                Summary = "✓ Allagan Tools",
                Tooltip = "Owned — Allagan Tools cached personal storage. This supplemental result is read locally through Dalamud IPC; no data is uploaded or shared.",
            };
        }

        return native;
    }

    /// <summary>
    /// Resolves native GlamSpector evidence independently so its provenance
    /// always has deterministic precedence over supplemental integrations.
    /// </summary>
    public InventoryOwnership GetNative(uint itemId)
    {
        RefreshIfStale();
        if (itemId != 0 && locationsByItem.TryGetValue(itemId, out var locations) && locations.Count > 0)
        {
            return new InventoryOwnership
            {
                Owned = true,
                Summary = "✓ " + string.Join(", ", locations.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)),
                Tooltip = "Owned — game inventory/cache. GlamSpector found this visible glamour item in the listed current-character storage location(s).",
            };
        }

        return new InventoryOwnership
        {
            Owned = false,
            Summary = "?",
            Tooltip = BuildUnknownTooltip(),
        };
    }

    public void PrioritizeSupplementalChecks(IEnumerable<uint> itemIds) =>
        allaganTools.PrioritizeItems(itemIds);

    public void EnsureSupplementalBulkScan(IEnumerable<uint> itemIds)
    {
        RefreshIfStale();
        var nativeUnknown = itemIds
            .Where(itemId => itemId != 0 && !locationsByItem.ContainsKey(itemId))
            .Distinct()
            .ToArray();
        allaganTools.EnsureBulkScan(nativeUnknown);
    }

    public string GetItemDiagnostics(uint itemId, string itemName)
    {
        var native = GetNative(itemId);
        var atStatus = allaganTools.GetItemDiagnostics(itemId);
        var combined = native.Owned
            ? native.Summary
            : allaganTools.TryGetOwned(itemId) ? "✓ Allagan Tools" : "?";
        return $"{itemName} ({itemId}): native={(native.Owned ? native.Summary : "unknown")}; at={atStatus}; combined={combined}";
    }

    public unsafe InventoryOwnership GetFacewear(ushort glassesId)
    {
        if (glassesId == 0)
            return new InventoryOwnership { Owned = false, Summary = "?", Tooltip = "No Facewear ID is stored for this capture." };

        try
        {
            var playerState = PlayerState.Instance();
            if (playerState != null && playerState->IsLoaded && playerState->IsGlassesUnlocked(glassesId))
            {
                return new InventoryOwnership
                {
                    Owned = true,
                    Summary = "✓ Unlocked",
                    Tooltip = "This Facewear style is unlocked on the current character.",
                };
            }
        }
        catch
        {
            // Fall through to unknown.
        }

        return new InventoryOwnership
        {
            Owned = false,
            Summary = "?",
            Tooltip = "This Facewear style was not found as unlocked on the current character.",
        };
    }

    public string CoverageSummary
    {
        get
        {
            RefreshIfStale();
            var dresser = GlamourDresserCached
                ? ExpandedGlamourDresserCached
                    ? $"Dresser ✓ (expanded: {ExpandedGlamourDresserItemCount} items{(expandedDresserLoadedFromDisk ? ", persisted" : string.Empty)})"
                    : $"Dresser ✓ (Outfit pieces not expanded yet; Outfit slots: {GlamourDresserOutfitSlotCount})"
                : ExpandedGlamourDresserCached
                    ? $"Dresser expanded cache ✓ ({ExpandedGlamourDresserItemCount} items{(expandedDresserLoadedFromDisk ? ", persisted" : string.Empty)})"
                    : "Dresser not cached";
            var armoire = ArmoireLoaded ? "Armoire ✓" : "Armoire not loaded";
            var saddle = SaddlebagCached ? "Saddlebags ✓" : "Saddlebags live-only";
            var allagan = allaganTools.Enabled
                ? allaganTools.Initialized ? "Allagan Tools personal cache ✓" : "Allagan Tools unavailable"
                : "Allagan Tools disabled";
            return $"Ownership coverage: Inventory/Armoury ✓ · {saddle} · {dresser} · {armoire} · Retainers: currently loaded only · {allagan}";
        }
    }

    public bool CanForceRefresh => DateTime.UtcNow - lastRefreshUtc >= TimeSpan.FromSeconds(2);

    public int ManualRefreshCooldownSeconds
    {
        get
        {
            var remaining = TimeSpan.FromSeconds(2) - (DateTime.UtcNow - lastRefreshUtc);
            return remaining <= TimeSpan.Zero ? 0 : Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        }
    }

    public bool ForceRefresh()
    {
        if (!CanForceRefresh)
            return false;
        Refresh();
        allaganTools.RequestRefreshKnownItems();
        return true;
    }

    private string BuildUnknownTooltip()
    {
        var parts = new List<string>
        {
            "Not found in the storage GlamSpector can currently verify.",
            "This is NOT a definitive 'you do not own it' result.",
        };

        if (!GlamourDresserCached)
            parts.Add("Glamour Dresser data is not currently cached by FFXIV; opening the dresser once can populate the game's item-search cache.");
        else if (!ExpandedGlamourDresserCached && GlamourDresserOutfitSlotCount > 0)
            parts.Add("No expanded Outfit cache is saved for this character yet; open the Glamour Dresser once, then refresh ownership. GlamSpector will persist that expanded list for future plugin/game restarts.");
        else if (expandedDresserLoadedFromDisk && expandedDresserUpdatedUtc.HasValue)
            parts.Add($"Outfit ownership is using the last expanded Glamour Dresser snapshot saved {expandedDresserUpdatedUtc.Value.ToLocalTime():g}. Open the Dresser again to refresh it after adding or removing items.");
        if (!ArmoireLoaded)
            parts.Add("Armoire data is not currently loaded from the server.");
        parts.Add("Retainers that are not currently loaded are not checked yet.");
        if (allaganTools.Enabled)
            parts.Add(allaganTools.Initialized
                ? "Allagan Tools did not provide positive evidence in its currently cached personal storage; zero still remains unverified."
                : "Allagan Tools supplementation is enabled but not currently available/initialized.");
        return string.Join("\n", parts);
    }
}
