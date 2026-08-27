using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using InventoryItem = FFXIVClientStructs.FFXIV.Client.Game.InventoryItem;

namespace GlamSpector.Services;

/// <summary>
/// Optional, read-only Allagan Tools IPC integration. Results are supplemental
/// positive evidence only: a zero count or unavailable IPC never proves that an
/// item is missing.
/// </summary>
public sealed class AllaganToolsOwnershipService : IDisposable
{
    private const int QueriesPerBatch = 8;
    private static readonly TimeSpan AvailableProbeInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan UnavailableProbeInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan QueryBatchInterval = TimeSpan.FromMilliseconds(250);
    // Inventory-change events are the primary invalidation path. The long TTL
    // is only a safety net for missed events/provider reloads and comfortably
    // exceeds a full several-thousand-item background sweep.
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(15);

    // CriticalCommonLib InventoryType numeric values accepted by
    // AllaganTools.ItemCountOwned. This is intentionally a strict personal-only
    // allowlist: player bags/equipment/Armoury, Armoire, Glamour Dresser,
    // saddlebags, and the active character's retainers. FC and housing/shared
    // containers are excluded even though AT's "belongs to active character"
    // relationship may include them.
    private static readonly uint[] PersonalInventoryTypes =
    [
        0, 1, 2, 3,                         // Player bags
        1000, 1001,                         // Equipped / gear-set-backed personal items
        2500, 2501,                         // Armoire, Glamour Dresser
        3200, 3201, 3202, 3203, 3204,
        3205, 3206, 3207, 3208, 3209,
        3300, 3400, 3500,                   // Armoury
        4000, 4001, 4100, 4101,             // Saddlebags
        10000, 10001, 10002, 10003, 10004,
        10005, 10006,                        // RetainerBag0..RetainerBag6
        11000,                               // RetainerEquippedGear
        12002,                               // RetainerMarket
    ];

    private readonly Configuration configuration;
    private readonly IPlayerState playerState;
    private readonly ICallGateSubscriber<bool> isInitialized;
    private readonly ICallGateSubscriber<ulong> currentCharacter;
    private readonly ICallGateSubscriber<uint, bool, uint[], uint> itemCountOwned;
    private readonly ICallGateSubscriber<bool, bool> initializedEvent;
    private readonly ICallGateSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool> itemAddedEvent;
    private readonly ICallGateSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool> itemRemovedEvent;
    private readonly object sync = new();
    private readonly Dictionary<uint, CachedCount> cache = [];
    private readonly Queue<uint> pending = [];
    private readonly Queue<uint> priorityPending = [];
    private readonly HashSet<uint> pendingSet = [];
    private readonly HashSet<uint> priorityPendingSet = [];
    private readonly HashSet<uint> bulkScanOutstanding = [];
    private DateTime nextProbeUtc = DateTime.MinValue;
    private DateTime nextQueryBatchUtc = DateTime.MinValue;
    private DateTime nextSubscriptionAttemptUtc = DateTime.MinValue;
    private DateTime? lastRefreshUtc;
    private ulong activeCharacterId;
    private ulong localCharacterId;
    private bool detected;
    private bool initialized;
    private bool disposed;
    private bool initializedEventSubscribed;
    private bool itemAddedEventSubscribed;
    private bool itemRemovedEventSubscribed;
    private bool enabledLastUpdate;
    private bool bulkScanHasSnapshot;
    private bool bulkScanCompleted;
    private int bulkScanTotal;
    private long bulkScanGeneration;
    private bool probeRequested = true;
    private long totalQueries;

    private readonly record struct CachedCount(uint Count, DateTime CheckedAtUtc);

    public readonly record struct OwnershipScanProgress(
        bool HasSnapshot,
        bool Active,
        int Completed,
        int Total,
        long Generation);

    public AllaganToolsOwnershipService(
        IDalamudPluginInterface pluginInterface,
        IPlayerState playerState,
        Configuration configuration)
    {
        this.playerState = playerState;
        this.configuration = configuration;
        isInitialized = pluginInterface.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");
        currentCharacter = pluginInterface.GetIpcSubscriber<ulong>("AllaganTools.CurrentCharacter");
        itemCountOwned = pluginInterface.GetIpcSubscriber<uint, bool, uint[], uint>("AllaganTools.ItemCountOwned");
        initializedEvent = pluginInterface.GetIpcSubscriber<bool, bool>("AllaganTools.Initialized");
        itemAddedEvent = pluginInterface.GetIpcSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool>("AllaganTools.ItemAdded");
        itemRemovedEvent = pluginInterface.GetIpcSubscriber<(uint, InventoryItem.ItemFlags, ulong, uint), bool>("AllaganTools.ItemRemoved");

        // Merely obtaining call-gate subscribers does not invoke the provider.
        // Event subscriptions are installed later only after explicit opt-in.
    }

    public bool Enabled => configuration.EnableAllaganToolsIntegration;

    public bool Detected
    {
        get { lock (sync) return detected; }
    }

    public bool Initialized
    {
        get { lock (sync) return initialized; }
    }

    public string StatusText
    {
        get
        {
            lock (sync)
            {
                if (!configuration.EnableAllaganToolsIntegration)
                    return detected ? "Detected, disabled" : "Not detected";
                if (!detected)
                    return "Not detected";
                if (!initialized)
                    return "Detected, enabled but not initialized";
                return "Active";
            }
        }
    }

    /// <summary>
    /// Returns only cached positive evidence. Missing/current-zero, disabled,
    /// or unavailable results remain false/unknown. A stale result is queued
    /// for refresh without synchronous IPC; an existing positive remains usable
    /// until that refresh resolves or the integration/character becomes invalid.
    /// </summary>
    public bool TryGetOwned(uint itemId)
    {
        if (itemId == 0 || !configuration.EnableAllaganToolsIntegration)
            return false;

        lock (sync)
        {
            if (disposed || !detected || !initialized)
                return false;

            var now = DateTime.UtcNow;
            if (!cache.TryGetValue(itemId, out var value) || now - value.CheckedAtUtc >= CacheLifetime)
                EnqueueLocked(itemId);

            return cache.TryGetValue(itemId, out value) && value.Count > 0;
        }
    }

    /// <summary>
    /// Runs on Framework.Update. IPC probing and count queries never execute in
    /// Library Draw, and each batch is deliberately bounded to avoid frame-rate
    /// work scaling with the whole Library.
    /// </summary>
    public void Update()
    {
        if (disposed)
            return;

        var now = DateTime.UtcNow;
        var enabled = configuration.EnableAllaganToolsIntegration;
        lock (sync)
        {
            if (enabled != enabledLastUpdate)
            {
                enabledLastUpdate = enabled;
                activeCharacterId = 0;
                initialized = false;
                cache.Clear();
                pending.Clear();
                priorityPending.Clear();
                pendingSet.Clear();
                priorityPendingSet.Clear();
                ResetBulkScanLocked();
                probeRequested = true;
                nextSubscriptionAttemptUtc = DateTime.MinValue;
            }
        }

        if (enabled)
            EnsureEventSubscriptions(now);
        else
            RemoveEventSubscriptions(now);

        var currentLocalId = playerState.IsLoaded ? playerState.ContentId : 0;
        lock (sync)
        {
            if (currentLocalId != localCharacterId)
            {
                localCharacterId = currentLocalId;
                activeCharacterId = 0;
                initialized = false;
                cache.Clear();
                pending.Clear();
                priorityPending.Clear();
                pendingSet.Clear();
                priorityPendingSet.Clear();
                ResetBulkScanLocked();
                probeRequested = true;
            }
        }

        bool shouldProbe;
        lock (sync)
            shouldProbe = probeRequested || now >= nextProbeUtc;
        if (shouldProbe)
            Probe(now, currentLocalId);

        if (!enabled)
            return;

        lock (sync)
        {
            if (!detected || !initialized || now < nextQueryBatchUtc)
                return;
            nextQueryBatchUtc = now + QueryBatchInterval;
        }

        for (var i = 0; i < QueriesPerBatch; i++)
        {
            uint itemId;
            lock (sync)
            {
                if (disposed || !detected || !initialized || !TryDequeueLocked(out itemId))
                    return;
            }

            try
            {
                // currentCharacterOnly=true is combined with the strict
                // PersonalInventoryTypes allowlist above. HQ/NQ flags do not
                // require separate calls because AT counts the base item ID.
                var count = itemCountOwned.InvokeFunc(itemId, true, PersonalInventoryTypes);
                lock (sync)
                {
                    if (disposed)
                        return;
                    cache[itemId] = new CachedCount(count, DateTime.UtcNow);
                    if (bulkScanOutstanding.Remove(itemId) && bulkScanOutstanding.Count == 0)
                        bulkScanCompleted = true;
                    lastRefreshUtc = DateTime.UtcNow;
                    totalQueries++;
                }
            }
            catch
            {
                // TryDequeueLocked removed this member from pendingSet. Keep it
                // queued across the temporary availability failure so a fixed
                // bulk-scan snapshot cannot remain permanently outstanding.
                lock (sync)
                {
                    if (!disposed)
                        EnqueueLocked(itemId);
                }
                MarkUnavailableAfterIpcFailure();
                return;
            }
        }
    }

    public void RequestRefreshKnownItems()
    {
        lock (sync)
        {
            if (disposed)
                return;
            StartBulkScanLocked(cache.Keys.Concat(pendingSet).ToArray(), force: true);
        }
    }

    public void EnsureBulkScan(IEnumerable<uint> itemIds)
    {
        if (!configuration.EnableAllaganToolsIntegration)
            return;

        lock (sync)
        {
            if (disposed || !detected || !initialized || (bulkScanHasSnapshot && !bulkScanCompleted))
                return;

            var unique = itemIds.Where(x => x != 0).Distinct().ToArray();
            if (!bulkScanHasSnapshot)
            {
                StartBulkScanLocked(unique, force: false);
                return;
            }

            // Once the initial snapshot is complete, only stale cached members
            // begin a safety-TTL cycle. Event-invalidated single items retain
            // their fixed-generation independence and do not make the bulk
            // denominator jump around.
            var now = DateTime.UtcNow;
            var stale = unique
                .Where(itemId => cache.TryGetValue(itemId, out var value)
                                 && now - value.CheckedAtUtc >= CacheLifetime)
                .ToArray();
            if (stale.Length > 0)
                StartBulkScanLocked(stale, force: true);
        }
    }

    public OwnershipScanProgress GetScanProgress()
    {
        lock (sync)
        {
            if (!configuration.EnableAllaganToolsIntegration || !bulkScanHasSnapshot)
                return default;
            return new OwnershipScanProgress(
                HasSnapshot: true,
                Active: !bulkScanCompleted,
                Completed: Math.Max(0, bulkScanTotal - bulkScanOutstanding.Count),
                Total: bulkScanTotal,
                Generation: bulkScanGeneration);
        }
    }

    public void PrioritizeItems(IEnumerable<uint> itemIds)
    {
        if (!configuration.EnableAllaganToolsIntegration)
            return;

        lock (sync)
        {
            if (disposed || !detected || !initialized)
                return;

            var now = DateTime.UtcNow;
            foreach (var itemId in itemIds.Where(x => x != 0).Distinct())
            {
                var alreadyQueued = pendingSet.Contains(itemId);
                if (!alreadyQueued
                    && cache.TryGetValue(itemId, out var value)
                    && now - value.CheckedAtUtc < CacheLifetime)
                    continue;

                if (pendingSet.Add(itemId))
                {
                    priorityPendingSet.Add(itemId);
                    priorityPending.Enqueue(itemId);
                }
                else if (priorityPendingSet.Add(itemId))
                {
                    // The normal queue may already contain this ID. The
                    // priority copy wins; its later normal copy is skipped.
                    priorityPending.Enqueue(itemId);
                }
            }
        }
    }

    public string GetItemDiagnostics(uint itemId)
    {
        if (itemId == 0)
            return "invalid";

        lock (sync)
        {
            if (!configuration.EnableAllaganToolsIntegration)
                return "disabled";
            if (!detected)
                return "unavailable";
            if (!initialized)
                return "not-initialized";

            var queued = pendingSet.Contains(itemId);
            if (cache.TryGetValue(itemId, out var value))
            {
                var status = value.Count > 0 ? $"cached-positive({value.Count})" : "cached-zero";
                return queued ? $"{status}, refresh-pending" : status;
            }

            return queued ? "pending" : "not-queried";
        }
    }

    public string GetDiagnostics()
    {
        lock (sync)
        {
            var lastRefresh = lastRefreshUtc.HasValue
                ? $"{(DateTime.UtcNow - lastRefreshUtc.Value).TotalSeconds:0}s ago"
                : "never";
            var positives = cache.Values.Count(x => x.Count > 0);
            var characterMatch = activeCharacterId != 0 && localCharacterId != 0 && activeCharacterId == localCharacterId;
            var scan = bulkScanHasSnapshot
                ? $"{Math.Max(0, bulkScanTotal - bulkScanOutstanding.Count)}/{bulkScanTotal}#{bulkScanGeneration}"
                : "none";
            return $"Allagan Tools: enabled={(Enabled ? "yes" : "no")}; detected={(detected ? "yes" : "no")}; initialized={(initialized ? "yes" : "no")}; characterMatch={(characterMatch ? "yes" : "no")}; lastRefresh={lastRefresh}; positives={positives}; cached={cache.Count}; pending={pendingSet.Count}; priority={priorityPendingSet.Count}; scan={scan}; ipcQueries={totalQueries}.";
        }
    }

    public void Dispose()
    {
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            cache.Clear();
            pending.Clear();
            priorityPending.Clear();
            pendingSet.Clear();
            priorityPendingSet.Clear();
            ResetBulkScanLocked();
        }

        if (initializedEventSubscribed)
        {
            try
            {
                initializedEvent.Unsubscribe(OnInitializedChanged);
            }
            catch
            {
                // The provider may already have unloaded. There is no plugin
                // state left for this subscriber to mutate after disposed=true.
            }
        }

        if (itemAddedEventSubscribed)
        {
            try
            {
                itemAddedEvent.Unsubscribe(OnItemChanged);
            }
            catch
            {
                // Optional providers may already be gone. disposed=true makes
                // any already-queued callback harmless.
            }
        }

        if (itemRemovedEventSubscribed)
        {
            try
            {
                itemRemovedEvent.Unsubscribe(OnItemChanged);
            }
            catch
            {
                // Optional providers may already be gone. disposed=true makes
                // any already-queued callback harmless.
            }
        }
    }

    private void Probe(DateTime now, ulong currentLocalId)
    {
        try
        {
            var ipcInitialized = isInitialized.InvokeFunc();
            if (!configuration.EnableAllaganToolsIntegration)
            {
                lock (sync)
                {
                    if (disposed)
                        return;
                    detected = true;
                    initialized = false;
                    activeCharacterId = 0;
                    probeRequested = false;
                    nextProbeUtc = now + AvailableProbeInterval;
                }
                return;
            }

            var ipcCharacter = ipcInitialized ? currentCharacter.InvokeFunc() : 0;
            var readyForLocalCharacter = ipcInitialized
                                         && ipcCharacter != 0
                                         && currentLocalId != 0
                                         && ipcCharacter == currentLocalId;

            lock (sync)
            {
                if (disposed)
                    return;

                detected = true;
                if (activeCharacterId != 0 && ipcCharacter != activeCharacterId)
                {
                    cache.Clear();
                    pending.Clear();
                    priorityPending.Clear();
                    pendingSet.Clear();
                    priorityPendingSet.Clear();
                    ResetBulkScanLocked();
                }

                activeCharacterId = ipcCharacter;
                initialized = readyForLocalCharacter;
                probeRequested = false;
                nextProbeUtc = now + (readyForLocalCharacter ? AvailableProbeInterval : UnavailableProbeInterval);
            }
        }
        catch
        {
            MarkUnavailableAfterIpcFailure();
        }
    }

    private void MarkUnavailableAfterIpcFailure()
    {
        lock (sync)
        {
            if (disposed)
                return;
            detected = false;
            initialized = false;
            activeCharacterId = 0;
            probeRequested = false;
            nextProbeUtc = DateTime.UtcNow + UnavailableProbeInterval;
        }
    }

    private void OnInitializedChanged(bool _)
    {
        lock (sync)
        {
            if (!disposed)
                probeRequested = true;
        }
    }

    private void EnsureEventSubscriptions(DateTime now)
    {
        lock (sync)
        {
            if (disposed || now < nextSubscriptionAttemptUtc)
                return;
            nextSubscriptionAttemptUtc = now + UnavailableProbeInterval;
        }

        // These public contracts use only primitive/BCL and FFXIVClientStructs
        // types already supplied by Dalamud; no InventoryTools or
        // CriticalCommonLib assembly is referenced.
        if (!initializedEventSubscribed)
        {
            try
            {
                initializedEvent.Subscribe(OnInitializedChanged);
                initializedEventSubscribed = true;
            }
            catch
            {
                // Periodic status probing remains the recovery path.
            }
        }

        if (!itemAddedEventSubscribed)
        {
            try
            {
                itemAddedEvent.Subscribe(OnItemChanged);
                itemAddedEventSubscribed = true;
            }
            catch
            {
                // The 60-second cache lifetime remains the fallback.
            }
        }

        if (!itemRemovedEventSubscribed)
        {
            try
            {
                itemRemovedEvent.Subscribe(OnItemChanged);
                itemRemovedEventSubscribed = true;
            }
            catch
            {
                // The 60-second cache lifetime remains the fallback.
            }
        }
    }

    private void RemoveEventSubscriptions(DateTime now)
    {
        lock (sync)
        {
            if (disposed || now < nextSubscriptionAttemptUtc)
                return;
            nextSubscriptionAttemptUtc = now + UnavailableProbeInterval;
        }

        if (initializedEventSubscribed)
        {
            try
            {
                initializedEvent.Unsubscribe(OnInitializedChanged);
                initializedEventSubscribed = false;
            }
            catch
            {
                // Retry while disabled; callbacks themselves also check state.
            }
        }

        if (itemAddedEventSubscribed)
        {
            try
            {
                itemAddedEvent.Unsubscribe(OnItemChanged);
                itemAddedEventSubscribed = false;
            }
            catch
            {
                // Retry while disabled; callbacks themselves also check state.
            }
        }

        if (itemRemovedEventSubscribed)
        {
            try
            {
                itemRemovedEvent.Unsubscribe(OnItemChanged);
                itemRemovedEventSubscribed = false;
            }
            catch
            {
                // Retry while disabled; callbacks themselves also check state.
            }
        }
    }

    private void OnItemChanged((uint ItemId, InventoryItem.ItemFlags Flags, ulong CharacterId, uint Quantity) change)
    {
        lock (sync)
        {
            if (disposed || !configuration.EnableAllaganToolsIntegration || change.ItemId == 0)
                return;

            // The event may concern any AT-known owner/container. Drop the old
            // result and re-run the strict personal-scope ItemCountOwned query;
            // the event itself is never accepted as ownership evidence.
            cache.Remove(change.ItemId);
            if (detected && initialized)
                EnqueueLocked(change.ItemId);
        }
    }

    private void EnqueueLocked(uint itemId)
    {
        if (itemId != 0 && pendingSet.Add(itemId))
            pending.Enqueue(itemId);
    }

    private void StartBulkScanLocked(IReadOnlyCollection<uint> itemIds, bool force)
    {
        var unique = itemIds.Where(x => x != 0).Distinct().ToArray();
        bulkScanGeneration++;
        bulkScanHasSnapshot = true;
        bulkScanCompleted = false;
        bulkScanTotal = unique.Length;
        bulkScanOutstanding.Clear();

        var now = DateTime.UtcNow;
        foreach (var itemId in unique)
        {
            if (!force
                && cache.TryGetValue(itemId, out var value)
                && now - value.CheckedAtUtc < CacheLifetime)
            {
                continue;
            }

            bulkScanOutstanding.Add(itemId);
            EnqueueLocked(itemId);
        }

        if (bulkScanOutstanding.Count == 0)
            bulkScanCompleted = true;
    }

    private void ResetBulkScanLocked()
    {
        bulkScanHasSnapshot = false;
        bulkScanCompleted = false;
        bulkScanTotal = 0;
        bulkScanOutstanding.Clear();
    }

    private bool TryDequeueLocked(out uint itemId)
    {
        while (priorityPending.Count > 0)
        {
            var candidate = priorityPending.Dequeue();
            priorityPendingSet.Remove(candidate);
            if (pendingSet.Remove(candidate))
            {
                itemId = candidate;
                return true;
            }
        }

        while (pending.Count > 0)
        {
            var candidate = pending.Dequeue();
            if (pendingSet.Remove(candidate))
            {
                itemId = candidate;
                return true;
            }
        }

        itemId = 0;
        return false;
    }
}
