using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using GlamSpector.Models;
using Lumina.Excel.Sheets;

namespace GlamSpector.Services;

public sealed class InspectReader
{
    // CharacterInspect's Examine inventory still reserves raw slot 5 for the
    // removed waist/belt slot. Keeping an explicit map prevents every slot after
    // Hands from being shifted by one.
    private static readonly IReadOnlyDictionary<int, string> SlotNames = new Dictionary<int, string>
    {
        [0] = "Main Hand",
        [1] = "Off Hand",
        [2] = "Head",
        [3] = "Body",
        [4] = "Hands",
        [6] = "Legs",
        [7] = "Feet",
        [8] = "Earrings",
        [9] = "Necklace",
        [10] = "Bracelets",
        [11] = "Right Ring",
        [12] = "Left Ring",
    };

    private readonly IGameGui gameGui;
    private readonly IObjectTable objectTable;
    private readonly IDataManager dataManager;

    // CharaView.ModelData is updated independently from the Examine inventory.
    // During live testing we observed the Facewear IDs briefly read as 0 on one
    // sample even though the immediately adjacent diagnostic sample contained the
    // correct ID. Cache the last non-zero Facewear value for the currently
    // inspected entity so a transient zero does not erase Facewear from a capture.
    private uint cachedFacewearEntityId;
    private FacewearDiagnostics? cachedFacewear;
    private uint cachedFreeCompanyEntityId;
    private string? cachedFreeCompanyName;

    public InspectReader(IGameGui gameGui, IObjectTable objectTable, IDataManager dataManager)
    {
        this.gameGui = gameGui;
        this.objectTable = objectTable;
        this.dataManager = dataManager;
    }

    private void ResetObservationCaches()
    {
        cachedFacewearEntityId = 0;
        cachedFacewear = null;
        cachedFreeCompanyEntityId = 0;
        cachedFreeCompanyName = null;
    }

    public unsafe uint GetCurrentInspectEntityId()
    {
        var addon = gameGui.GetAddonByName("CharacterInspect");
        if (addon.IsNull || !addon.IsVisible || !addon.IsReady)
            throw new InvalidOperationException("The Inspect window is not open and ready.");

        var agentPtr = gameGui.FindAgentInterface(addon);
        if (agentPtr.IsNull)
            throw new InvalidOperationException("The Inspect agent is not available.");

        var agent = (AgentInspect*)agentPtr.Address;
        if (agent == null || agent->CurrentEntityId == 0)
            throw new InvalidOperationException("No inspected character is currently selected.");

        return agent->CurrentEntityId;
    }

    public unsafe bool TryGetCurrentInspectEntityId(out uint entityId)
    {
        entityId = 0;
        try
        {
            var addon = gameGui.GetAddonByName("CharacterInspect");
            if (addon.IsNull || !addon.IsVisible || !addon.IsReady)
                return false;

            var agentPtr = gameGui.FindAgentInterface(addon);
            if (agentPtr.IsNull)
                return false;

            var agent = (AgentInspect*)agentPtr.Address;
            if (agent == null || agent->CurrentEntityId == 0)
                return false;

            entityId = agent->CurrentEntityId;
            return true;
        }
        catch
        {
            entityId = 0;
            return false;
        }
    }

    private unsafe (ushort Id0, ushort Id1) ReadGlassesIds(AgentInspect* agent)
    {
        var modelData = &agent->CharaView.ModelData;
        var glasses = (ushort*)((byte*)modelData + 0x1A);
        return (glasses[0], glasses[1]);
    }

    private unsafe FacewearDiagnostics ResolveFacewear(AgentInspect* agent, ushort id0, ushort id1, string source)
    {
        var glassesSheet = dataManager.GetExcelSheet<Glasses>();

        string? Resolve(ushort id)
        {
            if (id == 0)
                return null;
            return glassesSheet.TryGetRow(id, out var row)
                ? row.Name.ToString()
                : null;
        }

        return new FacewearDiagnostics
        {
            GlassesId0 = id0,
            GlassesName0 = Resolve(id0),
            GlassesId1 = id1,
            GlassesName1 = Resolve(id1),
            CharaViewState = agent->CharaView.State,
            CharacterLoaded = agent->CharaView.CharacterLoaded,
            Source = source,
        };
    }

    private unsafe FacewearDiagnostics ReadFacewearDiagnostics(AgentInspect* agent, uint entityId)
    {
        if (cachedFacewearEntityId != entityId)
        {
            cachedFacewearEntityId = entityId;
            cachedFacewear = null;
        }

        var (id0, id1) = ReadGlassesIds(agent);
        if (id0 != 0 || id1 != 0)
        {
            var live = ResolveFacewear(agent, id0, id1, "live");
            cachedFacewear = live;
            return live;
        }

        if (cachedFacewear is not null)
        {
            return new FacewearDiagnostics
            {
                GlassesId0 = cachedFacewear.GlassesId0,
                GlassesName0 = cachedFacewear.GlassesName0,
                GlassesId1 = cachedFacewear.GlassesId1,
                GlassesName1 = cachedFacewear.GlassesName1,
                CharaViewState = agent->CharaView.State,
                CharacterLoaded = agent->CharaView.CharacterLoaded,
                Source = "cached",
            };
        }

        return ResolveFacewear(agent, 0, 0, "live");
    }


    private unsafe (string? Name, string Source) ReadFreeCompany(AgentInspect* agent, uint entityId)
    {
        if (cachedFreeCompanyEntityId != entityId)
        {
            cachedFreeCompanyEntityId = entityId;
            cachedFreeCompanyName = null;
        }

        // FetchFreeCompanyStatus is transient: live testing showed it can already
        // be back at 0 while the Inspect window still displays the FC. Prefer the
        // actual GuildName buffer and use the status only as diagnostics.
        try
        {
            var live = agent->FreeCompany.GuildName.ToString();
            if (!string.IsNullOrWhiteSpace(live))
            {
                cachedFreeCompanyName = live;
                return (live, "live");
            }
        }
        catch
        {
        }

        return !string.IsNullOrWhiteSpace(cachedFreeCompanyName)
            ? (cachedFreeCompanyName, "cached")
            : (null, "none");
    }

    /// <summary>
    /// Samples the current Inspect CharaView once per UI frame and remembers the
    /// last non-zero Facewear IDs for the current inspected entity. This makes the
    /// one-click capture resilient to short-lived zeroes in CharaView.ModelData.
    /// </summary>
    public unsafe void ObserveCurrentInspect()
    {
        try
        {
            var addon = gameGui.GetAddonByName("CharacterInspect");
            if (addon.IsNull || !addon.IsVisible || !addon.IsReady)
            {
                ResetObservationCaches();
                return;
            }

            var agentPtr = gameGui.FindAgentInterface(addon);
            if (agentPtr.IsNull)
            {
                ResetObservationCaches();
                return;
            }

            var agent = (AgentInspect*)agentPtr.Address;
            if (agent == null || agent->CurrentEntityId == 0)
            {
                ResetObservationCaches();
                return;
            }

            var entityId = agent->CurrentEntityId;
            if (cachedFacewearEntityId != entityId)
            {
                cachedFacewearEntityId = entityId;
                cachedFacewear = null;
            }

            _ = ReadFreeCompany(agent, entityId);

            var (id0, id1) = ReadGlassesIds(agent);
            if (id0 == 0 && id1 == 0)
                return;

            if (cachedFacewear is not null &&
                cachedFacewear.GlassesId0 == id0 &&
                cachedFacewear.GlassesId1 == id1)
                return;

            cachedFacewear = ResolveFacewear(agent, id0, id1, "live");
        }
        catch
        {
            // Observation is best-effort and must never affect normal UI drawing.
            ResetObservationCaches();
        }
    }

    public unsafe GlamourSnapshot ReadCurrentInspect(uint? expectedEntityId = null)
    {
        var addon = gameGui.GetAddonByName("CharacterInspect");
        if (addon.IsNull || !addon.IsVisible || !addon.IsReady)
            throw new InvalidOperationException("The Inspect window is not open and ready.");

        var agentPtr = gameGui.FindAgentInterface(addon);
        if (agentPtr.IsNull)
            throw new InvalidOperationException("The Inspect agent is not available.");

        var agent = (AgentInspect*)agentPtr.Address;
        if (agent == null)
            throw new InvalidOperationException("The Inspect agent is not available.");

        if (agent->FetchCharacterDataStatus == 1)
            throw new InvalidOperationException("Inspect data is still loading. Try again in a moment.");
        if (agent->FetchCharacterDataStatus == 3)
            throw new InvalidOperationException("The game reported that Inspect data failed to load.");

        var entityId = agent->CurrentEntityId;
        if (entityId == 0)
            throw new InvalidOperationException("No inspected character is currently selected.");
        if (expectedEntityId.HasValue && entityId != expectedEntityId.Value)
            throw new InvalidOperationException("The inspected character changed while capture was being prepared. Start a fresh capture.");

        var gameObject = objectTable.SearchByEntityId(entityId);
        var player = gameObject as IPlayerCharacter;

        var characterName = player?.Name.ToString() ?? gameObject?.Name.ToString() ?? "Unknown Character";
        var homeWorld = "Unknown World";
        string? freeCompanyName = null;
        if (player is not null)
        {
            try
            {
                if (player.HomeWorld.IsValid)
                    homeWorld = player.HomeWorld.Value.Name.ToString();
            }
            catch
            {
                // The actor can disappear from the object table after inspection starts.
            }
        }

        // The status flag is transient; read the populated GuildName directly and
        // keep the last non-empty value for this inspected entity.
        freeCompanyName = ReadFreeCompany(agent, entityId).Name;

        var manager = InventoryManager.Instance();
        if (manager == null)
            throw new InvalidOperationException("InventoryManager is unavailable.");

        var itemSheet = dataManager.GetExcelSheet<Item>();
        var stainSheet = dataManager.GetExcelSheet<Stain>();
        var pieces = new List<GlamourPiece>(12);

        for (var i = 0; i < 13; i++)
        {
            // Slot 5 is the old waist/belt slot and is intentionally not part of the card.
            if (!SlotNames.TryGetValue(i, out var slotName))
                continue;

            var invItem = manager->GetInventorySlot(InventoryType.Examine, i);
            if (invItem == null)
                continue;

            var equippedItemId = invItem->ItemId;
            var glamourItemId = invItem->GetGlamourId();
            var displayItemId = glamourItemId != 0 ? glamourItemId : equippedItemId;
            if (displayItemId == 0)
                continue;

            var displayItemName = itemSheet.TryGetRow(displayItemId, out var row)
                ? row.Name.ToString()
                : $"Item #{displayItemId}";

            var stain1Id = invItem->GetStain(0);
            var stain2Id = invItem->GetStain(1);

            string? stain1Name = null;
            if (stain1Id != 0)
            {
                stain1Name = stainSheet.TryGetRow(stain1Id, out var stain1)
                    ? stain1.Name.ToString()
                    : $"Dye #{stain1Id}";
            }

            string? stain2Name = null;
            if (stain2Id != 0)
            {
                stain2Name = stainSheet.TryGetRow(stain2Id, out var stain2)
                    ? stain2.Name.ToString()
                    : $"Dye #{stain2Id}";
            }

            pieces.Add(new GlamourPiece
            {
                RawSlotIndex = i,
                SlotName = slotName,
                EquippedItemId = equippedItemId,
                GlamourItemId = glamourItemId,
                DisplayItemId = displayItemId,
                DisplayItemName = displayItemName,
                Stain1Id = stain1Id,
                Stain1Name = stain1Name,
                Stain2Id = stain2Id,
                Stain2Name = stain2Name,
            });
        }

        if (pieces.Count == 0)
            throw new InvalidOperationException("The Inspect window is open, but no examined gear was found yet. Wait for the gear list to appear and try again.");

        if (agent->CurrentEntityId != entityId ||
            (expectedEntityId.HasValue && agent->CurrentEntityId != expectedEntityId.Value))
        {
            throw new InvalidOperationException("The inspected character changed while its gear was being read. Start a fresh capture.");
        }

        return new GlamourSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            EntityId = entityId,
            CharacterName = characterName,
            HomeWorld = homeWorld,
            FreeCompanyName = freeCompanyName,
            Pieces = pieces,
            Facewear = ReadFacewearDiagnostics(agent, entityId),
        };
    }

    public unsafe string GetDiagnostics()
    {
        try
        {
            var addon = gameGui.GetAddonByName("CharacterInspect");
            if (addon.IsNull)
                return "CharacterInspect addon: NULL (inspect window not found).";

            var agentPtr = gameGui.FindAgentInterface(addon);
            if (agentPtr.IsNull)
                return $"CharacterInspect addon: found; ready={addon.IsReady}; visible={addon.IsVisible}; agent=NULL.";

            var agent = (AgentInspect*)agentPtr.Address;
            if (agent == null)
                return $"CharacterInspect addon: found; ready={addon.IsReady}; visible={addon.IsVisible}; agent address=NULL.";

            var manager = InventoryManager.Instance();
            var populated = 0;
            if (manager != null)
            {
                for (var i = 0; i < 13; i++)
                {
                    var item = manager->GetInventorySlot(InventoryType.Examine, i);
                    if (item != null && item->ItemId != 0)
                        populated++;
                }
            }

            var facewear = ReadFacewearDiagnostics(agent, agent->CurrentEntityId);
            var fc = ReadFreeCompany(agent, agent->CurrentEntityId);

            return $"CharacterInspect addon: found; ready={addon.IsReady}; visible={addon.IsVisible}; " +
                   $"pos=({addon.Position.X:0},{addon.Position.Y:0}); size=({addon.ScaledSize.X:0},{addon.ScaledSize.Y:0}); " +
                   $"agentStatus={agent->FetchCharacterDataStatus}; entity=0x{agent->CurrentEntityId:X8}; examineSlots={populated}; " +
                   $"charaViewState={facewear.CharaViewState}; characterLoaded={facewear.CharacterLoaded}; " +
                   $"glassesIds=[{facewear.GlassesId0},{facewear.GlassesId1}]; facewear={facewear.DisplayName ?? "none"}; facewearSource={facewear.Source}; " +
                   $"freeCompanyStatus={agent->FetchFreeCompanyStatus}; freeCompany={fc.Name ?? "none"}; freeCompanySource={fc.Source}.";
        }
        catch (Exception ex)
        {
            return $"Diagnostics failed: {ex.GetType().Name}: {ex.Message}";
        }
    }

}
