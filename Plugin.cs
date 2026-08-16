using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Command;
using Dalamud.Game.Inventory;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamSpector.Models;
using GlamSpector.Services;
using GlamSpector.UI;

namespace GlamSpector;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/glamspector";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ITextureReadbackProvider TextureReadbackProvider { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerStateService { get; private set; } = null!;

    public Configuration Configuration { get; }

    private readonly InspectReader inspectReader;
    private readonly PreviewCaptureService previewCaptureService;
    private readonly GlamCardRenderer glamCardRenderer;
    private readonly ConfigUi configUi;
    private readonly InventoryOwnershipService inventoryOwnershipService;
    private readonly GlamCodeService glamCodeService;
    private readonly EorzeaCollectionImportService eorzeaCollectionImportService;
    private readonly LibraryStore? libraryStore;
    private readonly LibraryUi? libraryUi;
    private readonly string? libraryInitializationError;
    private volatile bool captureInProgress;
    private volatile bool captureRequested;
    private PlateCapturePrompt? plateCapturePrompt;
    private AutoPlateCaptureState? autoPlateCapture;
    private InspectCapturePreparation? inspectCapturePreparation;
    private volatile bool captureReadyAfterInspectFocus;
    private ushort previousFocusedAddonIdAfterCapture;
    private ushort inspectAddonIdDuringCapture;
    private TryOnQueueState? tryOnQueue;
    private PendingLibraryItemAction? pendingLibraryItemAction;
    private volatile bool personalPreviewCaptureInProgress;

    private sealed class PlateCapturePrompt
    {
        public required long EntryId { get; init; }
        public required uint EntityId { get; init; }
        public required string CharacterName { get; init; }
        public required string HomeWorld { get; init; }
    }

    private sealed class AutoPlateCaptureState
    {
        public required long EntryId { get; init; }
        public required uint EntityId { get; init; }
        public required string CharacterName { get; init; }
        public required string HomeWorld { get; init; }
        public required DateTime StartedAtUtc { get; init; }
        public required bool OpenedByGlamSpector { get; init; }
        public DateTime? ReadySinceUtc { get; set; }
    }

    private sealed class InspectCapturePreparation
    {
        public bool FocusApplied { get; set; }
        public int FramesRemaining { get; set; }
        public ushort PreviousFocusedAddonId { get; set; }
        public ushort InspectAddonId { get; set; }
    }

    private sealed class TryOnQueueState
    {
        public required string CharacterName { get; init; }
        public required System.Collections.Generic.List<GlamourPiece> Pieces { get; init; }
        public int Index { get; set; }
        public int FramesUntilNext { get; set; }
        public int Failed { get; set; }
    }

    private enum LibraryItemActionType
    {
        TryOn,
        LinkInChat,
    }

    private sealed class PendingLibraryItemAction
    {
        public required LibraryItemActionType Type { get; init; }
        public required GlamourPiece Piece { get; init; }
    }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        if (Configuration.Version < 4)
        {
            // Existing M3.x installs did not have automatic Plate settings. Opt
            // them into the new agreed default while keeping every older setting.
            Configuration.AdventurerPlateCaptureMode = AdventurerPlateCaptureMode.Automatic;
            Configuration.CloseAutoOpenedAdventurerPlate = true;
            Configuration.CapturePortraitRecipeWithPlate = true;
            Configuration.AdventurerPlateTimeoutSeconds = 3f;
            Configuration.NotifyAdventurerPlate = true;
            Configuration.Version = 4;
        }

        if (Configuration.Version < 5)
        {
            // M3.5 could request a viewport capture as soon as Plate data became
            // ready. On some clients the native Plate render lags that data by a
            // noticeable fraction of a second, producing a crop of the world
            // behind the Plate. Keep it visibly settled before capture instead.
            Configuration.AdventurerPlateSettleSeconds = 1.0f;
            Configuration.Version = 5;
        }

        if (Configuration.Version < 6)
        {
            // M3.5.2 introduced native CharacterInspect focusing before sampling.
            Configuration.BringInspectToFrontBeforeCapture = true;
            Configuration.HideGlamSpectorWindowsDuringCapture = true;
            Configuration.Version = 6;
        }

        if (Configuration.Version < 7)
        {
            // M3.5.3 moves all native Focus() calls out of Dalamud's ImGui Draw
            // callback and onto the Framework.Update thread. Mutating FFXIV's
            // addon focus lists while the UI is being rendered can destabilize a
            // plugin. GlamSpector's ImGui windows do not need to be hidden because
            // the Inspect viewport capture is taken before ImGui is rendered.
            Configuration.HideGlamSpectorWindowsDuringCapture = false;
            Configuration.Version = 7;
        }

        if (Configuration.Version < 8)
        {
            // M3.7.1 adds only Library-side metadata (ratings) and local
            // ownership-cache timing; no existing user preference needs changing.
            Configuration.Version = 8;
        }

        if (Configuration.Version < 9)
        {
            // M3.12 adds per-entry media folders and personal Fitting Room
            // previews. Existing capture paths remain valid; no user preference
            // needs to be changed during this configuration-version bump.
            Configuration.Version = 9;
        }

        if (Configuration.Version < 10)
        {
            // M3.14 makes managed Library captures preview-first. No existing
            // preference is changed; SaveRawPreview now only affects captures
            // made while automatic Library indexing is disabled.
            Configuration.Version = 10;
        }

        if (Configuration.Version < 11)
        {
            // M3.15 remembers useful Library presentation state. Existing users
            // start with the established defaults; search text and transient UI
            // state deliberately remain session-only.
            Configuration.LibrarySortMode = 0;
            Configuration.LibraryRatingFilter = 0;
            Configuration.LibraryOwnershipFilter = 0;
            Configuration.LibraryWantedFilter = 0;
            Configuration.LibraryPlateFilter = 0;
            Configuration.LibraryFiltersExpanded = false;
            Configuration.LibraryListWidth = 360f;
            Configuration.LibraryTagsNotesExpanded = false;
            Configuration.LibraryFilesSharingExpanded = false;
            Configuration.LibraryEntryExpanded = false;
            Configuration.LibrarySelectedEntryId = 0;
            Configuration.Version = 11;
        }

        Configuration.Save();

        if (string.IsNullOrWhiteSpace(Configuration.OutputDirectory))
        {
            Configuration.OutputDirectory = Path.Combine(PluginInterface.ConfigDirectory.FullName, "Captures");
            Configuration.Save();
        }

        inspectReader = new InspectReader(GameGui, ObjectTable, DataManager);
        previewCaptureService = new PreviewCaptureService(GameGui, TextureProvider, TextureReadbackProvider);
        glamCardRenderer = new GlamCardRenderer();
        configUi = new ConfigUi(Configuration);
        inventoryOwnershipService = new InventoryOwnershipService(
            GameInventory,
            DataManager,
            PlayerStateService,
            Path.Combine(PluginInterface.ConfigDirectory.FullName, "glamspector-ownership-cache.json"));
        glamCodeService = new GlamCodeService(DataManager);
        var libraryMediaRoot = Path.Combine(Configuration.OutputDirectory, "LibraryMedia");
        eorzeaCollectionImportService = new EorzeaCollectionImportService(
            DataManager,
            Path.Combine(libraryMediaRoot, "EorzeaCollection"));

        try
        {
            var databasePath = Path.Combine(PluginInterface.ConfigDirectory.FullName, "glamspector-library.db");
            libraryStore = new LibraryStore(databasePath, libraryMediaRoot);
            libraryUi = new LibraryUi(
                libraryStore,
                TextureProvider,
                Configuration,
                CopyLibraryCard,
                OpenLibraryCard,
                OpenLibraryFolder,
                AttachOpenAdventurerPlate,
                QueueTryOnGlam,
                CapturePersonalPreview,
                GenerateShareCardFromPreview,
                CopyImageToClipboard,
                QueueTryOnItem,
                QueueLinkItemInChat,
                inventoryOwnershipService,
                glamCodeService,
                eorzeaCollectionImportService,
                configUi.Open);
        }
        catch (Exception ex)
        {
            libraryInitializationError = ex.Message;
            Log.Error(ex, "Could not initialize the GlamSpector library. Capture will remain available.");
        }

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "capture | library | config | debug | ownership-debug — capture, browse, configure, or print diagnostics",
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenConfigUi += configUi.Open;
        PluginInterface.UiBuilder.OpenMainUi += OpenMainUi;
        Framework.Update += OnFrameworkUpdate;

        Log.Information("GlamSpector Milestone 3.14.0 loaded.");
    }

    private void WriteOwnershipDiagnostic()
    {
        try
        {
            var report = inventoryOwnershipService.BuildGlamourDresserOutfitDiagnostics();
            var path = Path.Combine(PluginInterface.ConfigDirectory.FullName, "GlamSpector-ownership-debug.txt");
            File.WriteAllText(path, report);
            ChatGui.Print($"Ownership diagnostic written to {path}", "GlamSpector");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not write ownership diagnostic.");
            ChatGui.PrintError($"Could not write ownership diagnostic: {ex.Message}", "GlamSpector");
        }
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configUi.Open;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        Framework.Update -= OnFrameworkUpdate;
        eorzeaCollectionImportService.Dispose();
        CommandManager.RemoveHandler(CommandName);
    }

    private void OpenMainUi()
    {
        if (libraryUi is not null)
            libraryUi.Open();
        else
            configUi.Open();
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "capture":
            case "snap":
                captureRequested = true;
                break;
            case "library":
            case "lib":
            case "":
                if (libraryUi is not null)
                    libraryUi.Toggle();
                else
                    ChatGui.PrintError($"The library is unavailable: {libraryInitializationError ?? "unknown initialization error"}", "GlamSpector");
                break;
            case "config":
            case "settings":
                configUi.Toggle();
                break;
            case "debug":
            case "diag":
                ChatGui.Print(inspectReader.GetDiagnostics(), "GlamSpector");
                break;
            case "ownership-debug":
            case "owned-debug":
            case "dresser-debug":
                WriteOwnershipDiagnostic();
                break;
            default:
                ChatGui.Print("Use /glamspector capture, library, config, debug, or ownership-debug.", "GlamSpector");
                break;
        }
    }

    private void Draw()
    {
        inspectReader.ObserveCurrentInspect();

        // Automatic Plate capture still needs ImGui viewport access, so it is
        // advanced from Draw. Native CharacterInspect focusing is deliberately
        // advanced from Framework.Update instead (see OnFrameworkUpdate).
        UpdateAutomaticAdventurerPlateCapture();

        // BeginCapture uses ImGui.GetMainViewport(), so after the framework-thread
        // focus preparation has completed we start the actual viewport capture here.
        if (captureReadyAfterInspectFocus && !captureInProgress)
        {
            captureReadyAfterInspectFocus = false;
            StartCaptureNow();
        }

        var suppressGlamSpectorUi = Configuration.HideGlamSpectorWindowsDuringCapture &&
                                    (inspectCapturePreparation is not null || captureInProgress || autoPlateCapture is not null);

        if (!suppressGlamSpectorUi)
        {
            configUi.Draw();
            libraryUi?.Draw();
            DrawAdventurerPlatePrompt();
            DrawInspectCaptureButton();
        }

        if (captureRequested && !captureInProgress && inspectCapturePreparation is null)
        {
            captureRequested = false;
            TryStartCapture();
        }
    }

    private void DrawInspectCaptureButton()
    {
        var inspect = GameGui.GetAddonByName("CharacterInspect");
        if (inspect.IsNull || !inspect.IsVisible || inspect.ScaledSize.X <= 0 || inspect.ScaledSize.Y <= 0)
            return;

        var busy = captureInProgress || inspectCapturePreparation is not null || autoPlateCapture is not null || plateCapturePrompt is not null;

        var buttonSize = new Vector2(
            inspect.ScaledSize.X * 0.348f,
            inspect.ScaledSize.Y * 0.037f);
        var anchor = inspect.Position + new Vector2(
            inspect.ScaledSize.X * 0.258f,
            inspect.ScaledSize.Y * 0.943f);

        ImGui.SetNextWindowViewport(ImGui.GetMainViewport().ID);
        ImGui.SetNextWindowPos(anchor, ImGuiCond.Always);
        ImGui.SetNextWindowBgAlpha(0f);

        var flags = ImGuiWindowFlags.NoDecoration |
                    ImGuiWindowFlags.NoMove |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoFocusOnAppearing |
                    ImGuiWindowFlags.NoNav |
                    ImGuiWindowFlags.AlwaysAutoResize;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, Math.Max(6f, buttonSize.Y * 0.45f));
        try
        {
            if (ImGui.Begin("###GlamSpectorInspectCapture", flags))
            {
                ImGui.BeginDisabled(busy);
                var buttonText = captureInProgress ? "Capturing…" : inspectCapturePreparation is not null ? "Preparing…" : autoPlateCapture is not null ? "Plate…" : plateCapturePrompt is not null ? "Plate?" : "Capture";
                if (ImGui.Button(buttonText, buttonSize))
                    TryStartCapture();
                ImGui.EndDisabled();

                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(busy ? "Finish the current GlamSpector capture first." : "Create a Glam Card from this character");
            }
            ImGui.End();
        }
        finally
        {
            ImGui.PopStyleVar(2);
        }
    }

    private void TryStartCapture()
    {
        if (captureInProgress || inspectCapturePreparation is not null || autoPlateCapture is not null || plateCapturePrompt is not null)
            return;

        if (Configuration.BringInspectToFrontBeforeCapture)
        {
            try
            {
                BeginInspectCapturePreparation();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Could not prepare CharacterInspect for GlamSpector capture.");
                ChatGui.PrintError(ex.Message, "GlamSpector");
            }
            return;
        }

        StartCaptureNow();
    }

    private void BeginInspectCapturePreparation()
    {
        // Do not call FFXIV native Focus() from UiBuilder.Draw. The Draw callback
        // runs while Dalamud/FFXIV are rendering UI; changing the native focused
        // addon lists at that point can invalidate the structures being iterated.
        // We only queue the request here and perform the native call on the next
        // Framework.Update tick.
        inspectCapturePreparation = new InspectCapturePreparation
        {
            FocusApplied = false,
            FramesRemaining = 2,
            PreviousFocusedAddonId = 0,
            InspectAddonId = 0,
        };
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (inspectCapturePreparation is not null)
        {
            try
            {
                UpdateInspectCapturePreparationOnFrameworkThread();
            }
            catch (Exception ex)
            {
                inspectCapturePreparation = null;
                captureReadyAfterInspectFocus = false;
                previousFocusedAddonIdAfterCapture = 0;
                inspectAddonIdDuringCapture = 0;
                Log.Error(ex, "Could not prepare CharacterInspect for GlamSpector capture on the framework thread.");
                ChatGui.PrintError($"Could not prepare Inspect for capture: {ex.Message}", "GlamSpector");
            }
        }

        if (pendingLibraryItemAction is not null)
        {
            try
            {
                ProcessPendingLibraryItemAction();
            }
            catch (Exception ex)
            {
                pendingLibraryItemAction = null;
                Log.Error(ex, "Could not perform GlamSpector item action.");
                ChatGui.PrintError($"Item action failed: {ex.Message}", "GlamSpector");
            }
        }

        if (tryOnQueue is not null)
        {
            try
            {
                UpdateTryOnQueueInstance();
            }
            catch (Exception ex)
            {
                tryOnQueue = null;
                Log.Error(ex, "Could not apply GlamSpector Try On outfit.");
                ChatGui.PrintError($"Try On failed: {ex.Message}", "GlamSpector");
            }
        }
    }

    private unsafe void UpdateInspectCapturePreparationOnFrameworkThread()
    {
        if (inspectCapturePreparation is not { } state)
            return;

        if (!state.FocusApplied)
        {
            var inspect = GameGui.GetAddonByName<AddonCharacterInspect>("CharacterInspect");
            if (inspect == null || !((AtkUnitBase*)inspect)->IsVisible)
                throw new InvalidOperationException("The Inspect window is not open.");

            var unitManager = (AtkUnitManager*)RaptureAtkUnitManager.Instance();
            if (unitManager == null)
                throw new InvalidOperationException("The FFXIV UI manager is unavailable.");

            var inspectUnit = (AtkUnitBase*)inspect;
            state.PreviousFocusedAddonId =
                unitManager->FocusedAddon != null && unitManager->FocusedAddon != inspectUnit
                    ? unitManager->FocusedAddon->Id
                    : (ushort)0;
            state.InspectAddonId = inspectUnit->Id;

            // AtkUnitBase.Focus() is FFXIV's native focus path. Calling it from
            // Framework.Update avoids mutating native addon lists during ImGui Draw.
            inspectUnit->Focus();
            state.FocusApplied = true;
            return;
        }

        if (state.FramesRemaining > 0)
        {
            state.FramesRemaining--;
            return;
        }

        previousFocusedAddonIdAfterCapture = state.PreviousFocusedAddonId;
        inspectAddonIdDuringCapture = state.InspectAddonId;
        inspectCapturePreparation = null;
        captureReadyAfterInspectFocus = true;
    }

    private void CapturePersonalPreview(LibraryEntry entry)
    {
        if (libraryStore is null)
        {
            ChatGui.PrintError("The GlamSpector Library is unavailable.", "GlamSpector");
            return;
        }

        if (personalPreviewCaptureInProgress)
        {
            ChatGui.PrintError("A personal preview capture is already in progress.", "GlamSpector");
            return;
        }

        try
        {
            // This samples the native Fitting Room's central character viewport
            // before Dalamud ImGui is drawn, so the Library itself can remain
            // open while the user composes the shot. It never re-runs Try On;
            // the current rotation/zoom is captured exactly as the player left it.
            var request = previewCaptureService.BeginTryOnCharacterCapture();
            personalPreviewCaptureInProgress = true;
            _ = FinishPersonalPreviewCaptureAsync(entry.Id, request);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not start Fitting Room personal preview capture.");
            ChatGui.PrintError($"Could not capture Fitting Room preview: {ex.Message}", "GlamSpector");
        }
    }

    private async Task FinishPersonalPreviewCaptureAsync(long entryId, CaptureRequest request)
    {
        string? savedPath = null;
        string? errorMessage = null;
        try
        {
            using var texture = await request.TextureTask;
            var pngBytes = await previewCaptureService.EncodePngAsync(texture);
            if (libraryStore is null)
                throw new InvalidOperationException("The GlamSpector Library is unavailable.");

            savedPath = libraryStore.AddPersonalPreview(entryId, pngBytes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save Fitting Room personal preview.");
            errorMessage = ex.Message;
        }
        finally
        {
            _ = Framework.Run(() =>
            {
                personalPreviewCaptureInProgress = false;
                if (savedPath is not null)
                {
                    libraryUi?.NotifyLibraryChanged(entryId);
                    if (Configuration.NotifyCaptureSuccess)
                        ChatGui.Print($"Saved personal preview → {savedPath}", "GlamSpector");
                }
                if (errorMessage is not null)
                    ChatGui.PrintError($"Personal preview capture failed: {errorMessage}", "GlamSpector");
            });
        }
    }

    private void GenerateShareCardFromPreview(LibraryEntry entry, PersonalPreview preview)
    {
        _ = GenerateShareCardFromPreviewAsync(entry.Id, preview.Id, preview.Path);
    }

    private async Task GenerateShareCardFromPreviewAsync(long entryId, long previewId, string previewPath)
    {
        string? savedPath = null;
        string? errorMessage = null;
        try
        {
            if (libraryStore is null)
                throw new InvalidOperationException("The GlamSpector Library is unavailable.");
            if (!File.Exists(previewPath))
                throw new FileNotFoundException("The selected personal preview PNG no longer exists.", previewPath);

            var entry = libraryStore.Get(entryId) ?? throw new InvalidOperationException("The selected Library entry no longer exists.");
            var preview = entry.PersonalPreviews.FirstOrDefault(candidate => candidate.Id == previewId);
            if (preview is null || !File.Exists(preview.Path))
                throw new InvalidOperationException("The selected personal preview no longer exists.");

            var previewBytes = await File.ReadAllBytesAsync(preview.Path);
            var snapshot = SnapshotFromLibraryEntry(entry);
            var shareTitle = !string.IsNullOrWhiteSpace(entry.SourceTitle)
                ? entry.SourceTitle
                : "Saved Glamour";
            var shareSubtitle = string.Equals(entry.SourceKind, "EorzeaCollection", StringComparison.OrdinalIgnoreCase)
                ? string.IsNullOrWhiteSpace(entry.SourceCreator)
                    ? "Personal preview · Eorzea Collection recipe"
                    : $"Personal preview · Eorzea Collection · {entry.SourceCreator}"
                : $"Personal preview · recipe from {entry.CharacterName} @ {entry.HomeWorld}";
            var cardBytes = await glamCardRenderer.RenderAsync(
                snapshot,
                previewBytes,
                cleanItemLevelOverlay: false,
                titleOverride: shareTitle,
                subtitleOverride: shareSubtitle);

            savedPath = libraryStore.AddGeneratedShareCard(entryId, previewId, cardBytes);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not generate a share card from the personal preview.");
            errorMessage = ex.Message;
        }
        finally
        {
            _ = Framework.Run(() =>
            {
                if (savedPath is not null)
                {
                    libraryUi?.NotifyShareCardGenerated(entryId);
                    if (Configuration.NotifyCaptureSuccess)
                        ChatGui.Print($"Generated share card → {savedPath}", "GlamSpector");
                }

                if (errorMessage is not null)
                    ChatGui.PrintError($"Share-card generation failed: {errorMessage}", "GlamSpector");
            });
        }
    }

    private static GlamourSnapshot SnapshotFromLibraryEntry(LibraryEntry entry) => new()
    {
        CapturedAtUtc = entry.CapturedAtUtc,
        CharacterName = entry.CharacterName,
        HomeWorld = entry.HomeWorld,
        FreeCompanyName = entry.FreeCompanyName,
        Pieces = new List<GlamourPiece>(entry.Pieces),
        Facewear = entry.FacewearId != 0 || !string.IsNullOrWhiteSpace(entry.FacewearName)
            ? new FacewearDiagnostics
            {
                GlassesId0 = entry.FacewearId,
                GlassesName0 = entry.FacewearName,
                Source = "library-share-card",
            }
            : null,
    };

    private void CopyImageToClipboard(string path)
    {
        _ = CopyImageToClipboardAsync(path);
    }

    private async Task CopyImageToClipboardAsync(string path)
    {
        try
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("The PNG no longer exists.", path);

            var bytes = await File.ReadAllBytesAsync(path);
            await previewCaptureService.CopyPngBytesToClipboardAsync(
                bytes,
                Path.GetFileNameWithoutExtension(path));

            if (Configuration.NotifyClipboard)
                _ = Framework.Run(() => ChatGui.Print("Copied image to clipboard.", "GlamSpector"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not copy GlamSpector image to clipboard.");
            _ = Framework.Run(() => ChatGui.PrintError($"Could not copy image: {ex.Message}", "GlamSpector"));
        }
    }

    private void QueueTryOnItem(GlamourPiece piece)
    {
        if (piece.DisplayItemId == 0)
            return;

        if (tryOnQueue is not null || pendingLibraryItemAction is not null)
        {
            ChatGui.PrintError("Another GlamSpector item/Try On action is still being processed.", "GlamSpector");
            return;
        }

        // Queue native UI interaction for Framework.Update rather than invoking
        // FFXIVClientStructs from the ImGui context-menu callback.
        pendingLibraryItemAction = new PendingLibraryItemAction
        {
            Type = LibraryItemActionType.TryOn,
            Piece = piece,
        };
    }

    private void QueueLinkItemInChat(GlamourPiece piece)
    {
        if (piece.DisplayItemId == 0)
            return;

        if (tryOnQueue is not null || pendingLibraryItemAction is not null)
        {
            ChatGui.PrintError("Another GlamSpector item/Try On action is still being processed.", "GlamSpector");
            return;
        }

        pendingLibraryItemAction = new PendingLibraryItemAction
        {
            Type = LibraryItemActionType.LinkInChat,
            Piece = piece,
        };
    }

    private unsafe void ProcessPendingLibraryItemAction()
    {
        var action = pendingLibraryItemAction;
        if (action is null)
            return;

        // Clear first so a native UI failure cannot leave the Library action
        // permanently wedged in a busy state.
        pendingLibraryItemAction = null;

        switch (action.Type)
        {
            case LibraryItemActionType.TryOn:
            {
                // This deliberately behaves like trying a single native item:
                // do not force Save/Delete Outfit on. If the player already has
                // that Fitting Room mode enabled, FFXIV naturally retains the
                // rest of their current preview.
                var piece = action.Piece;
                var ok = AgentTryon.TryOn(0, piece.DisplayItemId, piece.Stain1Id, piece.Stain2Id, 0, false);
                if (!ok)
                    ChatGui.PrintError($"Could not try on {piece.DisplayItemName}.", "GlamSpector");
                break;
            }

            case LibraryItemActionType.LinkInChat:
            {
                var module = AgentModule.Instance();
                if (module == null)
                    throw new InvalidOperationException("The game UI agent module is unavailable.");

                var chatAgent = (AgentChatLog*)module->GetAgentByInternalId(AgentId.ChatLog);
                if (chatAgent == null)
                    throw new InvalidOperationException("The FFXIV chat agent is unavailable.");

                // AgentChatLog.LinkItem is the same native item-link path used
                // by FFXIV's own item context menu. It inserts the link into the
                // chat input; GlamSpector never presses Enter/sends it.
                chatAgent->LinkItem(action.Piece.DisplayItemId);
                break;
            }
        }
    }

    private void QueueTryOnGlam(LibraryEntry entry)
    {
        if (tryOnQueue is not null)
        {
            ChatGui.PrintError("A GlamSpector Try On outfit is already being loaded.", "GlamSpector");
            return;
        }

        // The Fitting Room normally replaces the previous item unless the native
        // "Save/Delete Outfit" toggle is enabled. GlamSpector loads a complete
        // outfit, so enable that mode automatically before queueing the pieces.
        // The Try On agent exists even before its addon is visible; we also
        // re-assert the flag after each TryOn call below in case opening the
        // window resets its state.
        EnsureTryOnSaveDeleteOutfitEnabled();

        var pieces = entry.Pieces
            .Where(piece => piece.DisplayItemId != 0)
            // Load body last. Some body glamours are one-piece models that hide
            // hands/legs/feet. If those subordinate slots are tried on after the
            // body, FFXIV can make them visible again and the final preview no
            // longer matches the inspected character. Letting the body item be
            // the final Try On operation makes its native hide-slot behaviour win.
            .OrderBy(piece => piece.RawSlotIndex == 3 ? 1 : 0)
            .ThenBy(piece => piece.RawSlotIndex)
            .ToList();
        if (pieces.Count == 0)
        {
            ChatGui.PrintError("This library entry has no structured gear to try on.", "GlamSpector");
            return;
        }

        tryOnQueue = new TryOnQueueState
        {
            CharacterName = entry.CharacterName,
            Pieces = pieces,
            Index = 0,
            FramesUntilNext = 0,
        };
    }

    private void UpdateTryOnQueueInstance()
    {
        var state = tryOnQueue;
        if (state is null)
            return;

        if (state.FramesUntilNext > 0)
        {
            state.FramesUntilNext--;
            return;
        }

        if (state.Index >= state.Pieces.Count)
        {
            var loaded = state.Pieces.Count - state.Failed;
            var suffix = state.Failed == 0 ? string.Empty : $" ({state.Failed} piece{(state.Failed == 1 ? string.Empty : "s")} could not be loaded)";
            ChatGui.Print($"Loaded {loaded}/{state.Pieces.Count} pieces from {state.CharacterName} into Try On{suffix}. Facewear is not applied yet.", "GlamSpector");
            tryOnQueue = null;
            return;
        }

        var piece = state.Pieces[state.Index++];

        // GlamSpector is a glamour catalogue, so Try On should list the item the
        // player is actually visually wearing. Passing the equipped/stat item as
        // the base item and the glamour as GlamourId makes the model look correct,
        // but FFXIV labels the Try On row with the hidden stat item. Instead, try
        // on the resolved visible item directly and apply the captured dyes to it.
        var ok = AgentTryon.TryOn(0, piece.DisplayItemId, piece.Stain1Id, piece.Stain2Id, 0, false);

        // AgentTryon exposes the same boolean used by FFXIV's native
        // "Save/Delete Outfit" button. Re-enable it after every item because
        // the first TryOn call can initialise/reset the Fitting Room state.
        EnsureTryOnSaveDeleteOutfitEnabled();

        if (!ok)
            state.Failed++;

        // Give the native Try On agent time to consume/update each slot before
        // pushing the next piece. This also avoids doing a burst of native UI
        // operations in one framework tick.
        state.FramesUntilNext = 2;
    }


    private static unsafe void EnsureTryOnSaveDeleteOutfitEnabled()
    {
        var module = AgentModule.Instance();
        if (module == null)
            return;

        var agent = (AgentTryon*)module->GetAgentByInternalId(AgentId.Tryon);
        if (agent == null)
            return;

        agent->SaveDeleteOutfit = true;
    }

    private void StartCaptureNow()
    {
        try
        {
            var snapshot = inspectReader.ReadCurrentInspect();
            var captureRequest = previewCaptureService.BeginCapture(Configuration.CropPaddingPixels);
            snapshot.Preview = captureRequest.Diagnostics;

            captureInProgress = true;
            _ = FinishCaptureAsync(snapshot, captureRequest);
        }
        catch (Exception ex)
        {
            RestorePreviousFocusedAddon();
            Log.Error(ex, "Could not start GlamSpector capture.");
            ChatGui.PrintError(ex.Message, "GlamSpector");
        }
    }

    private unsafe void RestorePreviousFocusedAddon()
    {
        var previousId = previousFocusedAddonIdAfterCapture;
        var inspectId = inspectAddonIdDuringCapture;
        previousFocusedAddonIdAfterCapture = 0;
        inspectAddonIdDuringCapture = 0;

        if (previousId == 0 || inspectId == 0)
            return;

        var unitManager = (AtkUnitManager*)RaptureAtkUnitManager.Instance();
        if (unitManager == null)
            return;

        // Do not steal focus back if the user deliberately focused something else
        // while the capture was being encoded. Only restore when Inspect still owns
        // focus as a result of GlamSpector's preparation step.
        if (unitManager->FocusedAddon == null || unitManager->FocusedAddon->Id != inspectId)
            return;

        var previous = unitManager->GetAddonById(previousId);
        if (previous != null && previous->IsVisible)
            previous->Focus();
    }

    private async Task FinishCaptureAsync(GlamourSnapshot snapshot, CaptureRequest captureRequest)
    {
        string? successMessage = null;
        string? errorMessage = null;
        string? libraryWarning = null;
        long? libraryEntryId = null;

        try
        {
            Directory.CreateDirectory(Configuration.OutputDirectory);

            var safeCharacter = MakeSafeFilePart(snapshot.CharacterName);
            var safeWorld = MakeSafeFilePart(snapshot.HomeWorld);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var baseName = $"{safeCharacter}_{safeWorld}_{stamp}";

            // M3.12 keeps newly indexed captures together in one per-entry media
            // directory. Existing flat captures remain fully supported. When
            // automatic Library indexing is disabled, preserve the old flat output
            // behaviour because there is no Library entry to own the directory.
            var captureDirectory = Configuration.AutoAddToLibrary && libraryStore is not null
                ? libraryStore.CreateCaptureMediaDirectory(baseName)
                : Configuration.OutputDirectory;
            var cardPath = Configuration.AutoAddToLibrary && libraryStore is not null
                ? Path.Combine(captureDirectory, "glam-card.png")
                : Path.Combine(captureDirectory, baseName + ".png");
            var rawPath = Configuration.AutoAddToLibrary && libraryStore is not null
                ? Path.Combine(captureDirectory, "raw-preview.png")
                : Path.Combine(captureDirectory, baseName + "_preview.png");
            var jsonPath = Configuration.AutoAddToLibrary && libraryStore is not null
                ? Path.Combine(captureDirectory, "diagnostic.json")
                : Path.Combine(captureDirectory, baseName + ".json");

            using var texture = await captureRequest.TextureTask;
            var previewBytes = await previewCaptureService.EncodePngAsync(texture);

            // M3.14 makes the character preview the Library-first visual.
            // Managed Library captures therefore always keep this image even when
            // the old optional SaveRawPreview setting is disabled. Outside the
            // Library we preserve the existing opt-in raw-preview behaviour.
            var keepPreviewImage = (Configuration.AutoAddToLibrary && libraryStore is not null) || Configuration.SaveRawPreview;
            if (keepPreviewImage)
                await File.WriteAllBytesAsync(rawPath, previewBytes);

            var cardBytes = await glamCardRenderer.RenderAsync(
                snapshot,
                previewBytes,
                Configuration.CleanupItemLevelOverlay);

            await File.WriteAllBytesAsync(cardPath, cardBytes);

            if (Configuration.CopyToClipboard)
            {
                await previewCaptureService.CopyPngBytesToClipboardAsync(
                    cardBytes,
                    Path.GetFileNameWithoutExtension(cardPath));
            }

            if (Configuration.WriteDiagnosticJson)
            {
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                {
                    WriteIndented = true,
                });
                await File.WriteAllTextAsync(jsonPath, json);
            }

            if (Configuration.AutoAddToLibrary && libraryStore is not null)
            {
                try
                {
                    libraryEntryId = libraryStore.AddCapture(
                        snapshot,
                        cardPath,
                        keepPreviewImage ? rawPath : null,
                        Configuration.WriteDiagnosticJson ? jsonPath : null);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Glam Card was saved, but could not be added to the library.");
                    libraryWarning = $"Card saved, but library indexing failed: {ex.Message}";
                }
            }

            successMessage = $"Captured Glam Card for {snapshot.CharacterName} @ {snapshot.HomeWorld} → {cardPath}";
        }
        catch (Exception ex)
        {
            Log.Error(ex, "GlamSpector capture failed.");
            errorMessage = $"Capture failed: {ex.Message}";
        }
        finally
        {
            _ = Framework.Run(() =>
            {
                captureInProgress = false;
                RestorePreviousFocusedAddon();
                if (libraryEntryId.HasValue)
                {
                    libraryUi?.NotifyLibraryChanged(libraryEntryId.Value);
                    QueueAdventurerPlateCaptureIfConfigured(libraryEntryId.Value, snapshot);
                }
                if (successMessage is not null && Configuration.NotifyCaptureSuccess)
                    ChatGui.Print(successMessage, "GlamSpector");
                if (libraryWarning is not null)
                    ChatGui.PrintError(libraryWarning, "GlamSpector");
                if (errorMessage is not null)
                    ChatGui.PrintError(errorMessage, "GlamSpector");
            });
        }
    }

    private void CopyLibraryCard(LibraryEntry entry)
    {
        _ = CopyLibraryCardAsync(entry);
    }

    private async Task CopyLibraryCardAsync(LibraryEntry entry)
    {
        try
        {
            if (!File.Exists(entry.CardPath))
                throw new FileNotFoundException("The saved Glam Card PNG no longer exists.", entry.CardPath);

            var bytes = await File.ReadAllBytesAsync(entry.CardPath);
            await previewCaptureService.CopyPngBytesToClipboardAsync(
                bytes,
                Path.GetFileNameWithoutExtension(entry.CardPath));

            if (Configuration.NotifyClipboard)
                _ = Framework.Run(() => ChatGui.Print("Copied Glam Card to clipboard.", "GlamSpector"));
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not copy library Glam Card.");
            _ = Framework.Run(() => ChatGui.PrintError($"Could not copy card: {ex.Message}", "GlamSpector"));
        }
    }

    private void QueueAdventurerPlateCaptureIfConfigured(long entryId, GlamourSnapshot snapshot)
    {
        if (libraryStore is null || !Configuration.AutoAddToLibrary)
            return;

        switch (Configuration.AdventurerPlateCaptureMode)
        {
            case AdventurerPlateCaptureMode.Off:
                return;
            case AdventurerPlateCaptureMode.Ask:
                plateCapturePrompt = new PlateCapturePrompt
                {
                    EntryId = entryId,
                    EntityId = snapshot.EntityId,
                    CharacterName = snapshot.CharacterName,
                    HomeWorld = snapshot.HomeWorld,
                };
                return;
            case AdventurerPlateCaptureMode.Automatic:
                StartAutomaticAdventurerPlateCapture(entryId, snapshot.EntityId, snapshot.CharacterName, snapshot.HomeWorld);
                return;
        }
    }

    private void DrawAdventurerPlatePrompt()
    {
        if (plateCapturePrompt is not { } prompt)
            return;

        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowViewport(viewport.ID);
        ImGui.SetNextWindowPos(viewport.Pos + (viewport.Size * 0.5f), ImGuiCond.Appearing, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(430f, 0f), ImGuiCond.Appearing);

        var flags = ImGuiWindowFlags.AlwaysAutoResize |
                    ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoCollapse;

        var open = true;
        if (ImGui.Begin("Capture Adventurer Plate?###GlamSpectorPlatePrompt", ref open, flags))
        {
            ImGui.TextWrapped($"Glam Card captured for {prompt.CharacterName} @ {prompt.HomeWorld}. Capture and attach their Adventurer Plate too?");
            ImGui.Spacing();

            if (ImGui.Button("Capture Plate", new Vector2(150f, 0f)))
            {
                plateCapturePrompt = null;
                StartAutomaticAdventurerPlateCapture(prompt.EntryId, prompt.EntityId, prompt.CharacterName, prompt.HomeWorld);
            }
            ImGui.SameLine();
            if (ImGui.Button("Skip", new Vector2(100f, 0f)))
                plateCapturePrompt = null;
        }
        ImGui.End();

        if (!open)
            plateCapturePrompt = null;
    }

    private unsafe void StartAutomaticAdventurerPlateCapture(long entryId, uint entityId, string characterName, string homeWorld)
    {
        try
        {
            if (libraryStore is null)
                throw new InvalidOperationException("The GlamSpector library is unavailable.");

            if (autoPlateCapture is not null)
                return;

            var gameObject = ObjectTable.SearchByEntityId(entityId);
            if (gameObject is null || !gameObject.IsValid() || gameObject.Address == 0)
                throw new InvalidOperationException("The inspected character is no longer nearby, so their Adventurer Plate could not be requested.");

            var module = AgentModule.Instance();
            if (module == null)
                throw new InvalidOperationException("The game UI agent module is unavailable.");

            var agent = (AgentCharaCard*)module->GetAgentByInternalId(AgentId.CharaCard);
            if (agent == null)
                throw new InvalidOperationException("The Adventurer Plate agent is unavailable.");

            var existingAddon = GameGui.GetAddonByName("CharaCard");
            var wasAlreadyOpen = !existingAddon.IsNull && existingAddon.IsVisible;

            agent->OpenCharaCard((GameObject*)gameObject.Address);
            autoPlateCapture = new AutoPlateCaptureState
            {
                EntryId = entryId,
                EntityId = entityId,
                CharacterName = characterName,
                HomeWorld = homeWorld,
                StartedAtUtc = DateTime.UtcNow,
                OpenedByGlamSpector = !wasAlreadyOpen,
                ReadySinceUtc = null,
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Automatic Adventurer Plate request could not be started.");
            if (Configuration.NotifyAdventurerPlate)
                ChatGui.PrintError($"Glam Card saved, but Adventurer Plate was unavailable: {ex.Message}", "GlamSpector");
        }
    }

    private unsafe void UpdateAutomaticAdventurerPlateCapture()
    {
        if (autoPlateCapture is not { } state)
            return;

        var now = DateTime.UtcNow;
        var timeout = Math.Clamp(Configuration.AdventurerPlateTimeoutSeconds, 1f, 10f);
        if (state.ReadySinceUtc is null && (now - state.StartedAtUtc).TotalSeconds > timeout)
        {
            FinishAutomaticPlateFailure(state, "Timed out waiting for the Adventurer Plate to load.");
            return;
        }

        try
        {
            var addon = GameGui.GetAddonByName("CharaCard");
            if (addon.IsNull || !addon.IsVisible)
                return;

            var agentPtr = GameGui.FindAgentInterface(addon);
            if (agentPtr.IsNull)
                return;

            var agent = (AgentCharaCard*)agentPtr.Address;
            if (agent == null || agent->Data == null)
                return;

            var data = agent->Data;
            if (data->EntityId != 0 && data->EntityId != state.EntityId)
                return;

            var plateName = data->Name.ToString();
            if (string.IsNullOrWhiteSpace(plateName))
                return;
            if (!string.Equals(plateName, state.CharacterName, StringComparison.OrdinalIgnoreCase))
                return;

            if (data->IsNotCreated)
            {
                FinishAutomaticPlateFailure(state, $"{state.CharacterName} has not created an Adventurer Plate.");
                return;
            }

            if (data->PortraitTexture == null)
            {
                state.ReadySinceUtc = null;
                return;
            }

            // Agent data can report fully loaded before the native Plate has
            // actually reached the swap chain. Hold the Plate visibly on-screen
            // for a real-time settle interval instead of assuming a handful of
            // framework frames is enough.
            state.ReadySinceUtc ??= now;
            var settleSeconds = Math.Clamp(Configuration.AdventurerPlateSettleSeconds, 0.25f, 3f);
            if ((now - state.ReadySinceUtc.Value).TotalSeconds < settleSeconds)
                return;

            var entry = libraryStore?.Get(state.EntryId);
            if (entry is null)
            {
                FinishAutomaticPlateFailure(state, "The new Library entry could not be found.");
                return;
            }

            PortraitSettingsSnapshot? portraitSettings = null;
            if (Configuration.CapturePortraitRecipeWithPlate)
                portraitSettings = CapturePortraitSettings(agent);

            var request = previewCaptureService.BeginAddonCapture("CharaCard", "Adventurer Plate", autoUpdate: true, takeBeforeImGuiRender: true);
            var closeAfter = Configuration.CloseAutoOpenedAdventurerPlate && state.OpenedByGlamSpector;
            autoPlateCapture = null;
            _ = FinishAdventurerPlateCaptureAsync(entry, request, portraitSettings, closeAfter, automatic: true);
        }
        catch (Exception ex)
        {
            FinishAutomaticPlateFailure(state, ex.Message, ex);
        }
    }

    private void FinishAutomaticPlateFailure(AutoPlateCaptureState state, string message, Exception? exception = null)
    {
        if (exception is not null)
            Log.Warning(exception, "Automatic Adventurer Plate capture failed.");
        else
            Log.Warning($"Automatic Adventurer Plate capture failed: {message}");

        autoPlateCapture = null;
        if (Configuration.CloseAutoOpenedAdventurerPlate && state.OpenedByGlamSpector)
            CloseAdventurerPlateAgent();

        if (Configuration.NotifyAdventurerPlate)
            ChatGui.PrintError($"Glam Card saved; Adventurer Plate not attached: {message}", "GlamSpector");
    }

    private static unsafe void CloseAdventurerPlateAgent()
    {
        try
        {
            var module = AgentModule.Instance();
            if (module == null)
                return;
            var agent = module->GetAgentByInternalId(AgentId.CharaCard);
            if (agent != null && agent->IsAgentActive())
                agent->Hide();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Could not close the automatically opened Adventurer Plate.");
        }
    }

    private unsafe void AttachOpenAdventurerPlate(LibraryEntry entry)
    {
        try
        {
            if (libraryStore is null)
                throw new InvalidOperationException("The GlamSpector library is unavailable.");

            var addon = GameGui.GetAddonByName("CharaCard");
            if (addon.IsNull || !addon.IsVisible)
                throw new InvalidOperationException("Open the character's Adventurer Plate first, then click Attach open Plate again.");

            var agentPtr = GameGui.FindAgentInterface(addon);
            if (agentPtr.IsNull)
                throw new InvalidOperationException("The Adventurer Plate agent is unavailable.");

            var agent = (AgentCharaCard*)agentPtr.Address;
            if (agent == null || agent->Data == null)
                throw new InvalidOperationException("The Adventurer Plate is still loading.");

            var plateName = agent->Data->Name.ToString();
            if (!string.IsNullOrWhiteSpace(plateName) &&
                !string.Equals(plateName, entry.CharacterName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"The open Adventurer Plate belongs to {plateName}, but the selected library entry is {entry.CharacterName}.");
            }

            // M3.4: preserve the target's exported portrait recipe while the
            // Adventurer Plate agent is still populated. This is read-only for
            // now; a later milestone can attempt to import it into the player's
            // own portrait editor after we validate the data on several plates.
            var portraitSettings = Configuration.CapturePortraitRecipeWithPlate
                ? CapturePortraitSettings(agent)
                : null;

            var request = previewCaptureService.BeginAddonCapture("CharaCard", "Adventurer Plate", autoUpdate: true, takeBeforeImGuiRender: true);
            _ = FinishAdventurerPlateCaptureAsync(entry, request, portraitSettings, closeAfterCapture: false, automatic: false);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not start Adventurer Plate capture.");
            ChatGui.PrintError(ex.Message, "GlamSpector");
        }
    }

    private static unsafe PortraitSettingsSnapshot CapturePortraitSettings(AgentCharaCard* agent)
    {
        if (agent == null || agent->Data == null)
            throw new InvalidOperationException("The Adventurer Plate portrait data is unavailable.");

        var data = agent->Data;
        var view = &data->CharaView;

        // AgentCharaCard.Storage places ExportedPortraitData at 0x978 and the
        // next field at 0x9AC, so the current exported payload is 0x34 bytes.
        // Keep the opaque payload as Base64 as well as a human-readable summary.
        // The opaque copy is what we can later feed back through the game's
        // ImportPortraitData function if cross-character testing proves safe.
        const int exportedPortraitDataSize = 0x34;
        var raw = new byte[exportedPortraitDataSize];
        fixed (byte* destination = raw)
        {
            Buffer.MemoryCopy(
                &(data->PortraitData),
                destination,
                exportedPortraitDataSize,
                exportedPortraitDataSize);
        }

        return new PortraitSettingsSnapshot
        {
            FormatVersion = 1,
            Source = "AdventurerPlate",
            RawExportedPortraitDataBase64 = Convert.ToBase64String(raw),
            CameraPositionX = view->CameraPosition.X,
            CameraPositionY = view->CameraPosition.Y,
            CameraPositionZ = view->CameraPosition.Z,
            CameraPositionW = view->CameraPosition.W,
            CameraTargetX = view->CameraTarget.X,
            CameraTargetY = view->CameraTarget.Y,
            CameraTargetZ = view->CameraTarget.Z,
            CameraTargetW = view->CameraTarget.W,
            CameraYaw = view->CameraYaw,
            CameraPitch = view->CameraPitch,
            CameraDistance = view->CameraDistance,
            ImageRotation = view->ImageRotation,
            CameraZoom = view->CameraZoom,
            DirectionalLightingColorRed = view->DirectionalLightingColorRed,
            DirectionalLightingColorGreen = view->DirectionalLightingColorGreen,
            DirectionalLightingColorBlue = view->DirectionalLightingColorBlue,
            DirectionalLightingBrightness = view->DirectionalLightingBrightness,
            DirectionalLightingVerticalAngle = view->DirectionalLightingVerticalAngle,
            DirectionalLightingHorizontalAngle = view->DirectionalLightingHorizontalAngle,
            AmbientLightingColorRed = view->AmbientLightingColorRed,
            AmbientLightingColorGreen = view->AmbientLightingColorGreen,
            AmbientLightingColorBlue = view->AmbientLightingColorBlue,
            AmbientLightingBrightness = view->AmbientLightingBrightness,
            PoseClassJob = view->PoseClassJob,
            Background = view->BannerBg,
            CharacterVisible = view->CharacterVisible,
            PlateBase = data->PlateDesign.BasePlate,
            PlateTopBorder = data->PlateDesign.TopBorder,
            PlateBottomBorder = data->PlateDesign.BottomBorder,
            BannerBackground = data->BannerBg,
            BannerFrame = data->BannerFrame,
            BannerDecoration = data->BannerDecoration,
        };
    }

    private async Task FinishAdventurerPlateCaptureAsync(LibraryEntry entry, CaptureRequest request, PortraitSettingsSnapshot? portraitSettings, bool closeAfterCapture, bool automatic)
    {
        try
        {
            using var texture = await request.TextureTask;

            // Keep the Plate open while an auto-updating viewport texture sees a
            // few more presented frames. The Plate is native FFXIV UI, so we
            // sample the main viewport before Dalamud ImGui is rendered; this
            // keeps the Plate while excluding GlamSpector/other plugin windows.
            await Task.Delay(250);
            var bytes = await previewCaptureService.EncodePngAsync(texture);
            var folder = Path.GetDirectoryName(entry.CardPath) ?? Configuration.OutputDirectory;
            Directory.CreateDirectory(folder);
            var fullFolder = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var mediaRoot = libraryStore is null
                ? string.Empty
                : Path.GetFullPath(libraryStore.MediaRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var isManagedMedia = mediaRoot.Length > 0 &&
                                 (string.Equals(fullFolder, mediaRoot, StringComparison.OrdinalIgnoreCase) ||
                                  fullFolder.StartsWith(mediaRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var platePath = isManagedMedia
                ? Path.Combine(folder, "adventurer-plate.png")
                : Path.Combine(folder, Path.GetFileNameWithoutExtension(entry.CardPath) + "_plate.png");
            await File.WriteAllBytesAsync(platePath, bytes);

            libraryStore!.SetAdventurerPlatePath(entry.Id, platePath);
            if (portraitSettings is not null)
                libraryStore.SetPortraitSettings(entry.Id, portraitSettings);
            _ = Framework.Run(() =>
            {
                if (closeAfterCapture)
                    CloseAdventurerPlateAgent();
                libraryUi?.NotifyLibraryChanged(entry.Id);
                if ((automatic && Configuration.NotifyAdventurerPlate) || (!automatic && Configuration.NotifyCaptureSuccess))
                    ChatGui.Print($"Attached Adventurer Plate for {entry.CharacterName} @ {entry.HomeWorld} → {platePath}", "GlamSpector");
            });
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Adventurer Plate capture failed.");
            _ = Framework.Run(() =>
            {
                if (closeAfterCapture)
                    CloseAdventurerPlateAgent();
                if (!automatic || Configuration.NotifyAdventurerPlate)
                    ChatGui.PrintError($"Adventurer Plate capture failed: {ex.Message}", "GlamSpector");
            });
        }
    }

    private static void OpenLibraryCard(LibraryEntry entry)
    {
        if (!File.Exists(entry.CardPath))
        {
            ChatGui.PrintError("The saved Glam Card PNG no longer exists.", "GlamSpector");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = entry.CardPath,
            UseShellExecute = true,
        });
    }

    private static void OpenLibraryFolder(LibraryEntry entry)
    {
        var folder = Path.GetDirectoryName(entry.CardPath);
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
        {
            ChatGui.PrintError("The capture folder no longer exists.", "GlamSpector");
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = folder,
            UseShellExecute = true,
        });
    }

    private static string MakeSafeFilePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = value.Select(c => invalid.Contains(c) ? '_' : c).ToArray();
        var result = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(result) ? "Unknown" : result;
    }
}
