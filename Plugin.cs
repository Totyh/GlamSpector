using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Threading;
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
    private const double InspectViewportCaptureTimeoutSeconds = 10.0;
    private const double InspectCaptureAttemptTimeoutSeconds = 30.0;
    private const double AutomaticPlateDeadlineGraceSeconds = 2.0;

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
    private readonly object captureLifecycleSync = new();
    private readonly CancellationTokenSource pluginLifetimeCancellation = new();
    private readonly CancellationToken pluginLifetimeToken;
    private int pluginLifetimeOperations;
    private int pluginLifetimeCancellationOperations;
    private bool pluginLifetimeCleanupRequested;
    private bool pluginLifetimeCancellationDisposed;
    private volatile bool captureInProgress;
    private volatile bool captureRequested;
    private PlateCapturePrompt? plateCapturePrompt;
    private AutoPlateCaptureState? autoPlateCapture;
    private InspectCapturePreparation? inspectCapturePreparation;
    private volatile bool captureReadyAfterInspectFocus;
    private uint captureReadyEntityId;
    private long captureReadyGeneration;
    private long nextInspectCaptureGeneration;
    private long latestInspectCaptureGeneration;
    private long focusOwnerGeneration;
    private ushort focusedPreviousAddonId;
    private ushort focusedInspectAddonId;
    private InspectCaptureAttempt? activeInspectCapture;
    private InspectCaptureAttempt? latestRetiredInspectCapture;
    private readonly HashSet<InspectCaptureAttempt> inspectCaptureAttemptsAwaitingDisposal = [];
    private TryOnQueueState? tryOnQueue;
    private PendingLibraryItemAction? pendingLibraryItemAction;
    private volatile bool personalPreviewCaptureInProgress;
    private volatile bool disposed;

    private sealed class PlateCapturePrompt
    {
        public required long EntryId { get; init; }
        public required long OriginatingInspectGeneration { get; init; }
        public required uint EntityId { get; init; }
        public required string CharacterName { get; init; }
        public required string HomeWorld { get; init; }
    }

    private sealed class AutoPlateCaptureState
    {
        public required long EntryId { get; init; }
        public required long OriginatingInspectGeneration { get; init; }
        public required uint EntityId { get; init; }
        public required string CharacterName { get; init; }
        public required string HomeWorld { get; init; }
        public required DateTime StartedAtUtc { get; init; }
        public required bool OpenedByGlamSpector { get; init; }
        public DateTime? ReadySinceUtc { get; set; }
    }

    private sealed class InspectCapturePreparation
    {
        public required long Generation { get; init; }
        public required uint EntityId { get; init; }
        public required DateTime StartedAtUtc { get; init; }
        public bool FocusApplied { get; set; }
        public int FramesRemaining { get; set; }
        public ushort PreviousFocusedAddonId { get; set; }
        public ushort InspectAddonId { get; set; }
    }

    private sealed class InspectCaptureAttempt
    {
        public required long Generation { get; init; }
        public required uint EntityId { get; init; }
        public required DateTime StartedAtUtc { get; init; }
        public required DateTime DeadlineUtc { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }
        public required CancellationToken Token { get; init; }
        public InspectCaptureStage Stage { get; set; } = InspectCaptureStage.WaitTexture;
        public DateTime StageStartedAtUtc { get; set; }
        public bool TexturePending { get; set; } = true;
        public bool ProviderTaskSettled { get; set; }
        public bool WorkerSettled { get; set; }
        public bool Retired { get; set; }
        public string? RetirementReason { get; set; }
        public bool CancellationRequested { get; set; }
        public string? CancellationReason { get; set; }
        public int CancellationOperations { get; set; }
        public bool CleanupRequested { get; set; }
        public bool CancellationDisposed { get; set; }
    }

    private enum InspectCaptureStage
    {
        WaitTexture,
        EncodeReadback,
        PreparePreview,
        RenderCard,
        EncodeCard,
        EncodePortrait,
        WritePreview,
        WriteCard,
        WriteJson,
        Clipboard,
        LibraryDatabase,
        Finalize,
    }

    private readonly record struct FocusRestoreState(long Generation, ushort PreviousId, ushort InspectId);

    private readonly record struct InspectCaptureCompletion(
        FocusRestoreState FocusRestore,
        bool CompletedWithAuthority);

    private readonly record struct StagedInspectFile(
        string TemporaryPath,
        string FinalPath,
        bool FinalExistedBefore);

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
        pluginLifetimeToken = pluginLifetimeCancellation.Token;
        var savedConfiguration = PluginInterface.GetPluginConfig() as Configuration;
        var hadSavedConfiguration = savedConfiguration is not null;
        Configuration = savedConfiguration ?? new Configuration();
        if (!hadSavedConfiguration)
        {
            // Establish a fresh installation's silent baseline before the
            // constructor's existing early configuration saves. This remains
            // quiet even if a later service initialization fails and Dalamud
            // retries plugin loading.
            Configuration.LastSeenPluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString();
        }
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

        if (Configuration.Version < 12)
        {
            // M3.15.2 adds persisted plugin-version observation. Its bootstrap
            // is handled after successful initialization so a genuinely fresh
            // install can remain quiet while an existing install is recognized.
            Configuration.Version = 12;
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

        HandleVersionUpdateNotification(hadSavedConfiguration);
        Log.Information("GlamSpector Milestone 3.15.5 loaded.");
    }

    private void HandleVersionUpdateNotification(bool hadSavedConfiguration)
    {
        var currentVersion = typeof(Plugin).Assembly.GetName().Version;
        if (currentVersion is null)
            return;

        var currentText = currentVersion.ToString();
        var lastSeenText = Configuration.LastSeenPluginVersion?.Trim();
        var shouldAnnounce = false;

        if (string.IsNullOrWhiteSpace(lastSeenText))
        {
            // GetPluginConfig returning null is a reliable first-install signal
            // for this configuration lifecycle. Existing installs upgrading to
            // the first notification-aware build announce once; fresh installs
            // silently establish their baseline.
            shouldAnnounce = hadSavedConfiguration;
        }
        else if (System.Version.TryParse(lastSeenText, out var lastSeenVersion))
        {
            if (currentVersion < lastSeenVersion)
                return; // A downgrade is not an update; retain the newer baseline.
            if (currentVersion == lastSeenVersion)
                return;
            shouldAnnounce = currentVersion > lastSeenVersion;
        }
        else
        {
            // Malformed/legacy data is reset safely without claiming an update.
            Log.Warning("Ignoring malformed last-seen GlamSpector version '{Version}'.", lastSeenText);
        }

        Configuration.LastSeenPluginVersion = currentText;
        Configuration.Save();
        if (shouldAnnounce)
            ChatGui.Print($"GlamSpector updated to version {currentText}", "GlamSpector");
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
        InspectCaptureAttempt? activeCapture;
        lock (captureLifecycleSync)
        {
            if (disposed)
                return;

            // Publish disposal and invalidate every queued generation before
            // cancellation can resume an asynchronous continuation.
            disposed = true;
            latestInspectCaptureGeneration = ++nextInspectCaptureGeneration;
            captureRequested = false;
            inspectCapturePreparation = null;
            captureReadyAfterInspectFocus = false;
            captureReadyGeneration = 0;
            captureReadyEntityId = 0;
            focusOwnerGeneration = 0;
            focusedPreviousAddonId = 0;
            focusedInspectAddonId = 0;
            activeCapture = activeInspectCapture;
            autoPlateCapture = null;
            plateCapturePrompt = null;
            personalPreviewCaptureInProgress = false;
            pluginLifetimeCleanupRequested = true;
            pluginLifetimeCancellationOperations++;
        }

        try
        {
            pluginLifetimeCancellation.Cancel();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "A capture lifetime cancellation callback failed during unload.");
        }
        finally
        {
            CancellationTokenSource? disposeLifetimeCancellation;
            lock (captureLifecycleSync)
            {
                pluginLifetimeCancellationOperations--;
                DisposeSettledInspectCaptureCancellationsLocked();
                disposeLifetimeCancellation = TryTakePluginLifetimeCancellationForDisposalLocked();
            }
            disposeLifetimeCancellation?.Dispose();
        }

        if (activeCapture is not null)
        {
            RetireInspectCaptureAttempt(
                activeCapture,
                "plugin-dispose",
                "GlamSpector is unloading.");
        }

        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= configUi.Open;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMainUi;
        Framework.Update -= OnFrameworkUpdate;
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
                lock (captureLifecycleSync)
                {
                    if (!disposed)
                        captureRequested = true;
                }
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
                ChatGui.Print(GetCaptureLifecycleDiagnostics(), "GlamSpector");
                if (libraryUi is not null)
                    ChatGui.Print(libraryUi.GetPerformanceDiagnostics(), "GlamSpector");
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
        MonitorActiveInspectCaptureIdentity();

        // Automatic Plate capture still needs ImGui viewport access, so it is
        // advanced from Draw. Native CharacterInspect focusing is deliberately
        // advanced from Framework.Update instead (see OnFrameworkUpdate).
        UpdateAutomaticAdventurerPlateCapture();

        // BeginCapture uses ImGui.GetMainViewport(), so after the framework-thread
        // focus preparation has completed we start the actual viewport capture here.
        long readyGeneration = 0;
        uint expectedEntityId = 0;
        lock (captureLifecycleSync)
        {
            if (captureReadyAfterInspectFocus && !captureInProgress && !disposed)
            {
                readyGeneration = captureReadyGeneration;
                expectedEntityId = captureReadyEntityId;
                captureReadyAfterInspectFocus = false;
                captureReadyGeneration = 0;
                captureReadyEntityId = 0;
            }
        }
        if (readyGeneration != 0)
            StartCaptureNow(readyGeneration, expectedEntityId);

        bool inspectCaptureBusy;
        lock (captureLifecycleSync)
            inspectCaptureBusy = inspectCapturePreparation is not null || captureInProgress;
        var suppressGlamSpectorUi = Configuration.HideGlamSpectorWindowsDuringCapture &&
                                    (inspectCaptureBusy || autoPlateCapture is not null);

        if (!suppressGlamSpectorUi)
        {
            configUi.Draw();
            libraryUi?.Draw();
            DrawAdventurerPlatePrompt();
            DrawInspectCaptureButton();
        }

        var startRequestedCapture = false;
        lock (captureLifecycleSync)
        {
            if (captureRequested && !captureInProgress && inspectCapturePreparation is null && !disposed)
            {
                captureRequested = false;
                startRequestedCapture = true;
            }
        }
        if (startRequestedCapture)
            TryStartCapture();
    }

    private void MonitorActiveInspectCaptureIdentity()
    {
        InspectCaptureAttempt? attempt;
        lock (captureLifecycleSync)
        {
            attempt = activeInspectCapture;
            if (attempt is null || attempt.Retired)
                return;
        }

        if (inspectReader.TryGetCurrentInspectEntityId(out var currentEntityId))
        {
            if (currentEntityId != attempt.EntityId)
            {
                RetireInspectCaptureAttempt(
                    attempt,
                    "target-changed",
                    $"The inspected character changed from 0x{attempt.EntityId:X8} to 0x{currentEntityId:X8}.");
            }
            return;
        }

        // A closed/unready/zero-entity Inspect is conclusive while the viewport
        // request is still pending. After texture acquisition it may merely be a
        // transient duty/ObjectTable transition, so the whole-attempt watchdog is
        // the fallback unless a valid different entity appears.
        if (attempt.TexturePending)
        {
            RetireInspectCaptureAttempt(
                attempt,
                "inspect-unavailable",
                "The Inspect window closed or became unavailable before the preview capture completed.",
                onlyWhileTexturePending: true);
        }
    }

    private bool IsPluginLifetimeValid() => !pluginLifetimeToken.IsCancellationRequested;

    private void QueueLifetimeFrameworkCallback(Action callback, long captureGeneration = 0)
    {
        lock (captureLifecycleSync)
        {
            if (disposed ||
                pluginLifetimeToken.IsCancellationRequested ||
                (captureGeneration != 0 && latestInspectCaptureGeneration != captureGeneration))
            {
                return;
            }

            _ = Framework.Run(() =>
            {
                lock (captureLifecycleSync)
                {
                    if (disposed ||
                        pluginLifetimeToken.IsCancellationRequested ||
                        (captureGeneration != 0 && latestInspectCaptureGeneration != captureGeneration))
                    {
                        return;
                    }

                    callback();
                }
            });
        }
    }

    private void AcquirePluginLifetimeOperation()
    {
        lock (captureLifecycleSync)
        {
            if (disposed || pluginLifetimeToken.IsCancellationRequested)
                throw new OperationCanceledException("GlamSpector is unloading.");
            pluginLifetimeOperations++;
        }
    }

    private void ReleasePluginLifetimeOperation()
    {
        CancellationTokenSource? disposeLifetimeCancellation;
        lock (captureLifecycleSync)
        {
            if (pluginLifetimeOperations <= 0)
                throw new InvalidOperationException("A GlamSpector lifetime operation was released more than once.");

            pluginLifetimeOperations--;
            disposeLifetimeCancellation = TryTakePluginLifetimeCancellationForDisposalLocked();
        }
        disposeLifetimeCancellation?.Dispose();
    }

    private CancellationTokenSource? TryTakePluginLifetimeCancellationForDisposalLocked()
    {
        if (!pluginLifetimeCleanupRequested ||
            pluginLifetimeOperations != 0 ||
            pluginLifetimeCancellationOperations != 0 ||
            pluginLifetimeCancellationDisposed)
        {
            return null;
        }

        pluginLifetimeCancellationDisposed = true;
        return pluginLifetimeCancellation;
    }

    private long AllocateInspectCaptureGeneration()
    {
        lock (captureLifecycleSync)
        {
            if (disposed)
                throw new OperationCanceledException("GlamSpector is unloading.");
            var generation = ++nextInspectCaptureGeneration;
            latestInspectCaptureGeneration = generation;
            return generation;
        }
    }

    private bool IsCurrentGeneration(long generation)
    {
        lock (captureLifecycleSync)
            return !disposed && generation != 0 && latestInspectCaptureGeneration == generation;
    }

    private bool RetireInspectCaptureAttempt(
        InspectCaptureAttempt attempt,
        string retirementReason,
        string reason,
        bool onlyWhileTexturePending = false)
    {
        var cancel = false;
        FocusRestoreState focusRestore = default;
        lock (captureLifecycleSync)
        {
            if (!ReferenceEquals(activeInspectCapture, attempt) ||
                attempt.Retired ||
                (onlyWhileTexturePending && !attempt.TexturePending))
                return false;

            attempt.Retired = true;
            attempt.RetirementReason = retirementReason;
            attempt.CancellationRequested = true;
            attempt.CancellationReason = reason;
            attempt.CancellationOperations++;
            attempt.CleanupRequested = true;
            activeInspectCapture = null;
            latestRetiredInspectCapture = attempt;
            captureRequested = false;
            inspectCapturePreparation = null;
            captureReadyAfterInspectFocus = false;
            captureReadyGeneration = 0;
            captureReadyEntityId = 0;
            focusRestore = TakeFocusRestoreStateLocked(attempt.Generation);

            // Lifecycle availability is independent from the abandoned worker's
            // physical completion. Publish idle only after all ownership state is
            // cleared so a new generation sees a consistent lifecycle.
            captureInProgress = false;
            cancel = true;
        }

        if (!cancel)
            return false;

        try
        {
            attempt.Cancellation.Cancel();
        }
        catch (Exception ex)
        {
            if (IsPluginLifetimeValid())
                Log.Warning(ex, "An Inspect capture cancellation callback failed during retirement.");
        }
        finally
        {
            lock (captureLifecycleSync)
            {
                attempt.CancellationOperations--;
                TryDisposeInspectCaptureCancellationLocked(attempt);
            }
        }

        // A valid replacement Inspect already represents new user activity; an
        // old generation must not take focus away from it. Other retirements may
        // restore only through the existing generation/lifetime-guarded callback.
        if (retirementReason != "target-changed")
            QueueFocusRestore(focusRestore);
        if (IsPluginLifetimeValid())
        {
            if (retirementReason is "deadline" or "texture-timeout" or "inspect-unavailable")
            {
                Log.Warning($"GlamSpector Inspect capture retired ({retirementReason}): {reason}");
                QueueLifetimeFrameworkCallback(
                    () => ChatGui.PrintError(reason, "GlamSpector"),
                    attempt.Generation);
            }
            else
            {
                Log.Debug($"GlamSpector Inspect capture retired ({retirementReason}): {reason}");
            }
        }
        return true;
    }

    private InspectCaptureCompletion CompleteInspectCaptureAttempt(InspectCaptureAttempt attempt)
    {
        FocusRestoreState focusRestore = default;
        var completedWithAuthority = false;

        lock (captureLifecycleSync)
        {
            if (ReferenceEquals(activeInspectCapture, attempt))
            {
                completedWithAuthority = InspectCaptureHasAuthorityLocked(attempt);
                activeInspectCapture = null;
                captureRequested = false;
                inspectCapturePreparation = null;
                captureReadyAfterInspectFocus = false;
                captureReadyGeneration = 0;
                captureReadyEntityId = 0;
                focusRestore = TakeFocusRestoreStateLocked(attempt.Generation);

                // Publish idle last. Every reader that can start a new attempt
                // takes this same lock, so it sees a fully cleared old attempt.
                captureInProgress = false;
            }

            attempt.WorkerSettled = true;
            attempt.CleanupRequested = true;
            TryDisposeInspectCaptureCancellationLocked(attempt);
        }

        return new InspectCaptureCompletion(focusRestore, completedWithAuthority);
    }

    private void MarkInspectProviderTaskSettled(InspectCaptureAttempt attempt)
    {
        lock (captureLifecycleSync)
        {
            attempt.TexturePending = false;
            attempt.ProviderTaskSettled = true;
            TryDisposeInspectCaptureCancellationLocked(attempt);
        }
    }

    private void TryDisposeInspectCaptureCancellationLocked(
        InspectCaptureAttempt attempt)
    {
        if (!attempt.CleanupRequested ||
            !attempt.ProviderTaskSettled ||
            !attempt.WorkerSettled ||
            attempt.CancellationOperations != 0 ||
            pluginLifetimeCancellationOperations != 0 ||
            attempt.CancellationDisposed)
        {
            return;
        }

        attempt.CancellationDisposed = true;
        inspectCaptureAttemptsAwaitingDisposal.Remove(attempt);
        attempt.Cancellation.Dispose();
    }

    private void DisposeSettledInspectCaptureCancellationsLocked()
    {
        foreach (var attempt in inspectCaptureAttemptsAwaitingDisposal.ToArray())
            TryDisposeInspectCaptureCancellationLocked(attempt);
    }

    private string GetCaptureLifecycleDiagnostics()
    {
        var now = DateTime.UtcNow;
        var hasCurrentInspect = inspectReader.TryGetCurrentInspectEntityId(out var currentInspectEntityId);
        bool inProgress;
        bool requested;
        bool ready;
        uint readyEntityId;
        long readyGeneration;
        bool hasPreparation;
        bool preparationFocused;
        string preparationText;
        string captureText;
        string retiredWorkerText;
        string capturePhase;
        lock (captureLifecycleSync)
        {
            inProgress = captureInProgress;
            requested = captureRequested;
            ready = captureReadyAfterInspectFocus;
            readyEntityId = captureReadyEntityId;
            readyGeneration = captureReadyGeneration;
            hasPreparation = inspectCapturePreparation is not null;
            preparationFocused = inspectCapturePreparation?.FocusApplied == true;
            preparationText = inspectCapturePreparation is { } preparation
                ? $"gen={preparation.Generation},entity=0x{preparation.EntityId:X8},focus={preparation.FocusApplied},frames={preparation.FramesRemaining},elapsed={(now - preparation.StartedAtUtc).TotalSeconds:0.0}s"
                : "none";
            if (activeInspectCapture is { } capture)
            {
                var stageName = FormatInspectCaptureStage(capture.Stage);
                var textureState = capture.TexturePending ? "pending" : capture.ProviderTaskSettled ? "ready" : "settling";
                var mismatch = hasCurrentInspect && currentInspectEntityId != capture.EntityId;
                var textureDeadline = capture.Stage == InspectCaptureStage.WaitTexture
                    ? $",textureElapsed={(now - capture.StageStartedAtUtc).TotalSeconds:0.0}s/{InspectViewportCaptureTimeoutSeconds:0.0}s"
                    : string.Empty;
                captureText = $"gen={capture.Generation},entity=0x{capture.EntityId:X8},currentInspect={(hasCurrentInspect ? $"0x{currentInspectEntityId:X8}" : "none")},stage={stageName},elapsed={(now - capture.StartedAtUtc).TotalSeconds:0.0}s/{(capture.DeadlineUtc - capture.StartedAtUtc).TotalSeconds:0.0}s,stageElapsed={(now - capture.StageStartedAtUtc).TotalSeconds:0.0}s{textureDeadline},texture={textureState},worker={(capture.WorkerSettled ? "settled" : "pending")},mismatch={mismatch},retired={capture.Retired},cancel={capture.CancellationRequested}";
                capturePhase = stageName;
            }
            else
            {
                captureText = "none";
                capturePhase = "idle";
            }

            retiredWorkerText = latestRetiredInspectCapture is { Retired: true, WorkerSettled: false } retired
                ? $"gen={retired.Generation},entity=0x{retired.EntityId:X8},currentInspect={(hasCurrentInspect ? $"0x{currentInspectEntityId:X8}" : "none")},stage={FormatInspectCaptureStage(retired.Stage)},elapsed={(now - retired.StartedAtUtc).TotalSeconds:0.0}s,stageElapsed={(now - retired.StageStartedAtUtc).TotalSeconds:0.0}s,reason={retired.RetirementReason ?? "unknown"},texture={(retired.TexturePending ? "pending" : "ready")},worker=pending,mismatch={hasCurrentInspect && currentInspectEntityId != retired.EntityId},cancel={retired.CancellationRequested}"
                : "none";
        }
        var plate = autoPlateCapture;
        var phase = inProgress
            ? capturePhase
            : plate is not null
                ? plate.ReadySinceUtc.HasValue ? "plate-settle" : "plate-load"
                : hasPreparation
                    ? preparationFocused ? "inspect-wait" : "inspect-focus"
                    : ready
                        ? "inspect-ready"
                        : requested ? "requested" : "idle";

        var plateText = plate is null
            ? "none"
            : $"gen={plate.OriginatingInspectGeneration},entity=0x{plate.EntityId:X8},ready={plate.ReadySinceUtc.HasValue},elapsed={(now - plate.StartedAtUtc).TotalSeconds:0.0}s/{GetAutomaticPlateOverallTimeoutSeconds():0.0}s";

        return $"Capture lifecycle: phase={phase}; inProgress={inProgress}; requested={requested}; " +
               $"prep=[{preparationText}]; ready=[gen={readyGeneration},entity=0x{readyEntityId:X8}]; capture=[{captureText}]; retiredWorker=[{retiredWorkerText}]; plate=[{plateText}].";
    }

    private static string FormatInspectCaptureStage(InspectCaptureStage stage) => stage switch
    {
        InspectCaptureStage.WaitTexture => "wait-texture",
        InspectCaptureStage.EncodeReadback => "encode-readback",
        InspectCaptureStage.PreparePreview => "prepare-preview",
        InspectCaptureStage.RenderCard => "render-card",
        InspectCaptureStage.EncodeCard => "encode-card",
        InspectCaptureStage.EncodePortrait => "encode-portrait",
        InspectCaptureStage.WritePreview => "write-preview",
        InspectCaptureStage.WriteCard => "write-card",
        InspectCaptureStage.WriteJson => "write-json",
        InspectCaptureStage.Clipboard => "clipboard",
        InspectCaptureStage.LibraryDatabase => "library-db",
        InspectCaptureStage.Finalize => "finalize",
        _ => "unknown",
    };

    private void DrawInspectCaptureButton()
    {
        var inspect = GameGui.GetAddonByName("CharacterInspect");
        if (inspect.IsNull || !inspect.IsVisible || inspect.ScaledSize.X <= 0 || inspect.ScaledSize.Y <= 0)
            return;

        bool captureBusy;
        lock (captureLifecycleSync)
            captureBusy = captureInProgress || inspectCapturePreparation is not null;
        var busy = captureBusy || autoPlateCapture is not null || plateCapturePrompt is not null;

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
                string buttonText;
                lock (captureLifecycleSync)
                    buttonText = captureInProgress ? "Capturing…" : inspectCapturePreparation is not null ? "Preparing…" : autoPlateCapture is not null ? "Plate…" : plateCapturePrompt is not null ? "Plate?" : "Capture";
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
        lock (captureLifecycleSync)
        {
            if (disposed || captureInProgress || inspectCapturePreparation is not null)
                return;
        }
        if (autoPlateCapture is not null || plateCapturePrompt is not null)
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

        StartCaptureNow(AllocateInspectCaptureGeneration(), 0);
    }

    private void BeginInspectCapturePreparation()
    {
        // Do not call FFXIV native Focus() from UiBuilder.Draw. The Draw callback
        // runs while Dalamud/FFXIV are rendering UI; changing the native focused
        // addon lists at that point can invalidate the structures being iterated.
        // We only queue the request here and perform the native call on the next
        // Framework.Update tick.
        var entityId = inspectReader.GetCurrentInspectEntityId();
        var generation = AllocateInspectCaptureGeneration();
        lock (captureLifecycleSync)
        {
            inspectCapturePreparation = new InspectCapturePreparation
            {
                Generation = generation,
                EntityId = entityId,
                StartedAtUtc = DateTime.UtcNow,
                FocusApplied = false,
                FramesRemaining = 2,
                PreviousFocusedAddonId = 0,
                InspectAddonId = 0,
            };
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        InspectCapturePreparation? preparation;
        lock (captureLifecycleSync)
            preparation = inspectCapturePreparation;
        if (preparation is not null)
        {
            try
            {
                UpdateInspectCapturePreparationOnFrameworkThread();
            }
            catch (Exception ex)
            {
                var reportFailure = false;
                lock (captureLifecycleSync)
                {
                    if (ReferenceEquals(inspectCapturePreparation, preparation))
                    {
                        inspectCapturePreparation = null;
                        captureReadyAfterInspectFocus = false;
                        captureReadyGeneration = 0;
                        captureReadyEntityId = 0;
                        captureRequested = false;
                        if (!disposed && latestInspectCaptureGeneration == preparation.Generation)
                        {
                            RestorePreviousFocusedAddon(
                                preparation.PreviousFocusedAddonId,
                                preparation.InspectAddonId);
                            reportFailure = true;
                        }
                    }
                }
                if (reportFailure)
                {
                    Log.Error(ex, "Could not prepare CharacterInspect for GlamSpector capture on the framework thread.");
                    ChatGui.PrintError($"Could not prepare Inspect for capture: {ex.Message}", "GlamSpector");
                }
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
        lock (captureLifecycleSync)
        {
            UpdateInspectCapturePreparationOnFrameworkThreadLocked();
        }
    }

    private unsafe void UpdateInspectCapturePreparationOnFrameworkThreadLocked()
    {
        var state = inspectCapturePreparation;
        if (state is null || disposed || latestInspectCaptureGeneration != state.Generation)
            return;

        var currentEntityId = inspectReader.GetCurrentInspectEntityId();
        if (currentEntityId != state.EntityId)
        {
            throw new InvalidOperationException(
                "The inspected character changed while capture was being prepared. Start a fresh capture.");
        }

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

        if (!ReferenceEquals(inspectCapturePreparation, state) || disposed ||
            latestInspectCaptureGeneration != state.Generation)
        {
            return;
        }

        focusOwnerGeneration = state.Generation;
        focusedPreviousAddonId = state.PreviousFocusedAddonId;
        focusedInspectAddonId = state.InspectAddonId;
        inspectCapturePreparation = null;
        captureReadyGeneration = state.Generation;
        captureReadyEntityId = state.EntityId;
        captureReadyAfterInspectFocus = true;
    }

    private void CapturePersonalPreview(LibraryEntry entry)
    {
        if (!IsPluginLifetimeValid())
            return;

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

        var lifetimeOperationHeld = false;
        try
        {
            // This samples the native Fitting Room's central character viewport
            // before Dalamud ImGui is drawn, so the Library itself can remain
            // open while the user composes the shot. It never re-runs Try On;
            // the current rotation/zoom is captured exactly as the player left it.
            AcquirePluginLifetimeOperation();
            lifetimeOperationHeld = true;
            lock (captureLifecycleSync)
            {
                if (disposed || pluginLifetimeToken.IsCancellationRequested)
                    throw new OperationCanceledException("GlamSpector is unloading.");

                var request = previewCaptureService.BeginTryOnCharacterCapture(pluginLifetimeToken);
                personalPreviewCaptureInProgress = true;
                _ = FinishPersonalPreviewCaptureAsync(entry.Id, request);
                lifetimeOperationHeld = false;
            }
        }
        catch (Exception ex)
        {
            if (lifetimeOperationHeld)
                ReleasePluginLifetimeOperation();
            if (IsPluginLifetimeValid())
            {
                Log.Error(ex, "Could not start Fitting Room personal preview capture.");
                ChatGui.PrintError($"Could not capture Fitting Room preview: {ex.Message}", "GlamSpector");
            }
        }
    }

    private async Task FinishPersonalPreviewCaptureAsync(long entryId, CaptureRequest request)
    {
        string? savedPath = null;
        string? errorMessage = null;
        var textureAcquired = false;
        try
        {
            using var texture = await request.TextureTask.WaitAsync(pluginLifetimeToken);
            textureAcquired = true;
            var pngBytes = await previewCaptureService.EncodePngAsync(texture, pluginLifetimeToken);
            pluginLifetimeToken.ThrowIfCancellationRequested();
            if (libraryStore is null)
                throw new InvalidOperationException("The GlamSpector Library is unavailable.");

            lock (captureLifecycleSync)
            {
                pluginLifetimeToken.ThrowIfCancellationRequested();
                if (disposed)
                    throw new OperationCanceledException(pluginLifetimeToken);
                savedPath = libraryStore.AddPersonalPreview(entryId, pngBytes);
            }
        }
        catch (OperationCanceledException) when (pluginLifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsPluginLifetimeValid())
            {
                Log.Error(ex, "Could not save Fitting Room personal preview.");
                errorMessage = ex.Message;
            }
        }
        finally
        {
            var releaseLifetimeHere = textureAcquired;
            if (!textureAcquired)
                _ = ObserveLateCaptureTextureAsync(request.TextureTask);

            try
            {
                QueueLifetimeFrameworkCallback(() =>
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
            finally
            {
                if (releaseLifetimeHere)
                    ReleasePluginLifetimeOperation();
            }
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

    private void StartCaptureNow(long generation, uint expectedEntityId)
    {
        InspectCaptureAttempt? attempt = null;
        CancellationTokenSource? unownedCancellation = null;
        var lifetimeOperationHeld = false;
        try
        {
            if (!IsCurrentGeneration(generation))
                return;

            if (expectedEntityId == 0)
                expectedEntityId = inspectReader.GetCurrentInspectEntityId();

            var currentEntityId = inspectReader.GetCurrentInspectEntityId();
            if (currentEntityId != expectedEntityId)
            {
                throw new InvalidOperationException(
                    "The inspected character changed before capture started. Start a fresh capture.");
            }

            var snapshot = inspectReader.ReadCurrentInspect(expectedEntityId);
            if (inspectReader.GetCurrentInspectEntityId() != expectedEntityId)
            {
                throw new InvalidOperationException(
                    "The inspected character changed before its preview was requested. Start a fresh capture.");
            }

            var attemptStartedAtUtc = DateTime.UtcNow;
            unownedCancellation = CancellationTokenSource.CreateLinkedTokenSource(pluginLifetimeToken);
            attempt = new InspectCaptureAttempt
            {
                Generation = generation,
                EntityId = expectedEntityId,
                StartedAtUtc = attemptStartedAtUtc,
                DeadlineUtc = attemptStartedAtUtc.AddSeconds(InspectCaptureAttemptTimeoutSeconds),
                StageStartedAtUtc = attemptStartedAtUtc,
                Cancellation = unownedCancellation,
                Token = unownedCancellation.Token,
            };

            lock (captureLifecycleSync)
            {
                if (disposed || latestInspectCaptureGeneration != generation || activeInspectCapture is not null)
                    throw new OperationCanceledException("This Inspect capture attempt is no longer active.");

                activeInspectCapture = attempt;
                inspectCaptureAttemptsAwaitingDisposal.Add(attempt);
                captureRequested = false;
                // Publish busy last after the owning generation and CTS exist.
                captureInProgress = true;
            }
            unownedCancellation = null;

            lock (captureLifecycleSync)
            {
                if (disposed ||
                    latestInspectCaptureGeneration != generation ||
                    !ReferenceEquals(activeInspectCapture, attempt))
                {
                    throw new OperationCanceledException("This Inspect capture attempt is no longer active.");
                }

                AcquirePluginLifetimeOperation();
                lifetimeOperationHeld = true;
                var captureRequest = previewCaptureService.BeginCapture(
                    Configuration.CropPaddingPixels,
                    attempt.Token);
                snapshot.Preview = captureRequest.Diagnostics;

                _ = RetireInspectCaptureAtTextureDeadlineAsync(attempt);
                _ = RetireInspectCaptureAtOverallDeadlineAsync(attempt);
                _ = FinishCaptureAsync(snapshot, captureRequest, attempt);
                lifetimeOperationHeld = false;
                attempt = null;
            }
        }
        catch (Exception ex)
        {
            if (lifetimeOperationHeld)
                ReleasePluginLifetimeOperation();
            unownedCancellation?.Dispose();
            FocusRestoreState focusRestore;
            if (attempt is not null)
            {
                MarkInspectProviderTaskSettled(attempt);
                focusRestore = CompleteInspectCaptureAttempt(attempt).FocusRestore;
            }
            else
            {
                lock (captureLifecycleSync)
                    focusRestore = TakeFocusRestoreStateLocked(generation);
            }
            QueueFocusRestore(focusRestore);
            if (IsPluginLifetimeValid())
            {
                Log.Error(ex, "Could not start GlamSpector capture.");
                ChatGui.PrintError(ex.Message, "GlamSpector");
            }
        }
    }

    private async Task RetireInspectCaptureAtTextureDeadlineAsync(InspectCaptureAttempt attempt)
    {
        await Task.Delay(TimeSpan.FromSeconds(InspectViewportCaptureTimeoutSeconds));

        RetireInspectCaptureAttempt(
            attempt,
            "texture-timeout",
            $"Capture timed out after {InspectViewportCaptureTimeoutSeconds:0} seconds waiting for the Inspect preview.",
            onlyWhileTexturePending: true);
    }

    private async Task RetireInspectCaptureAtOverallDeadlineAsync(InspectCaptureAttempt attempt)
    {
        var delay = attempt.DeadlineUtc - DateTime.UtcNow;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay);

        RetireInspectCaptureAttempt(
            attempt,
            "deadline",
            $"Capture exceeded the {InspectCaptureAttemptTimeoutSeconds:0}-second overall Inspect deadline.");
    }

    private FocusRestoreState TakeFocusRestoreStateLocked(long generation)
    {
        if (focusOwnerGeneration != generation)
            return default;

        var state = new FocusRestoreState(generation, focusedPreviousAddonId, focusedInspectAddonId);
        focusOwnerGeneration = 0;
        focusedPreviousAddonId = 0;
        focusedInspectAddonId = 0;
        return state;
    }

    private void QueueFocusRestore(FocusRestoreState state)
    {
        if (state.Generation == 0 || state.PreviousId == 0 || state.InspectId == 0)
            return;

        QueueLifetimeFrameworkCallback(
            () =>
            {
                // This callback executes on the game framework thread. No newer
                // preparation can interleave between the helper's ownership
                // check and the native focus operation below.
                RestorePreviousFocusedAddon(state.PreviousId, state.InspectId);
            },
            state.Generation);
    }

    private static unsafe void RestorePreviousFocusedAddon(ushort previousId, ushort inspectId)
    {

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

    private void EnsureInspectCaptureCanCommit(InspectCaptureAttempt attempt)
    {
        attempt.Token.ThrowIfCancellationRequested();
        pluginLifetimeToken.ThrowIfCancellationRequested();
        lock (captureLifecycleSync)
        {
            if (!InspectCaptureHasAuthorityLocked(attempt))
            {
                throw new OperationCanceledException("This Inspect capture attempt no longer owns the active lifecycle.");
            }
        }
    }

    private bool InspectCaptureHasAuthority(InspectCaptureAttempt attempt)
    {
        lock (captureLifecycleSync)
            return InspectCaptureHasAuthorityLocked(attempt);
    }

    private bool InspectCaptureHasAuthorityLocked(InspectCaptureAttempt attempt) =>
        !disposed &&
        !pluginLifetimeToken.IsCancellationRequested &&
        !attempt.Retired &&
        !attempt.Token.IsCancellationRequested &&
        ReferenceEquals(activeInspectCapture, attempt) &&
        latestInspectCaptureGeneration == attempt.Generation;

    private void SetInspectCaptureStage(InspectCaptureAttempt attempt, InspectCaptureStage stage)
    {
        EnsureInspectCaptureCanCommit(attempt);
        lock (captureLifecycleSync)
        {
            if (!InspectCaptureHasAuthorityLocked(attempt))
                throw new OperationCanceledException("This Inspect capture attempt no longer owns the active lifecycle.");

            attempt.Stage = stage;
            attempt.StageStartedAtUtc = DateTime.UtcNow;
        }
    }

    private async Task FinishCaptureAsync(
        GlamourSnapshot snapshot,
        CaptureRequest captureRequest,
        InspectCaptureAttempt attempt)
    {
        string? successMessage = null;
        string? errorMessage = null;
        string? libraryWarning = null;
        long? libraryEntryId = null;
        var textureAcquired = false;
        Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap? texture = null;
        var stagedFiles = new List<StagedInspectFile>();
        string? managedCaptureDirectory = null;
        var finalFilePromotionStarted = false;
        var finalFilesPublished = false;

        try
        {
            var safeCharacter = MakeSafeFilePart(snapshot.CharacterName);
            var safeWorld = MakeSafeFilePart(snapshot.HomeWorld);
            var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            var baseName = $"{safeCharacter}_{safeWorld}_{stamp}";

            texture = await captureRequest.TextureTask.WaitAsync(attempt.Token);
            textureAcquired = true;
            lock (captureLifecycleSync)
            {
                attempt.TexturePending = false;
                attempt.ProviderTaskSettled = true;
            }

            EnsureInspectCaptureCanCommit(attempt);
            SetInspectCaptureStage(attempt, InspectCaptureStage.EncodeReadback);
            var previewBytes = await previewCaptureService.EncodePngAsync(texture, attempt.Token);
            EnsureInspectCaptureCanCommit(attempt);

            var renderedCapture = await glamCardRenderer.RenderCaptureAsync(
                snapshot,
                previewBytes,
                Configuration.CleanupItemLevelOverlay,
                attempt.Token,
                stage => SetInspectCaptureStage(attempt, stage switch
                {
                    GlamCardRenderStage.PreparePreview => InspectCaptureStage.PreparePreview,
                    GlamCardRenderStage.RenderCard => InspectCaptureStage.RenderCard,
                    GlamCardRenderStage.EncodeCard => InspectCaptureStage.EncodeCard,
                    GlamCardRenderStage.EncodePortrait => InspectCaptureStage.EncodePortrait,
                    _ => throw new ArgumentOutOfRangeException(nameof(stage)),
                }));
            var cardBytes = renderedCapture.CardPng;
            EnsureInspectCaptureCanCommit(attempt);

            string captureDirectory;
            EnsureInspectCaptureCanCommit(attempt);
            Directory.CreateDirectory(Configuration.OutputDirectory);
            captureDirectory = Configuration.AutoAddToLibrary && libraryStore is not null
                ? libraryStore.CreateCaptureMediaDirectory(baseName)
                : Configuration.OutputDirectory;
            if (Configuration.AutoAddToLibrary && libraryStore is not null)
                managedCaptureDirectory = captureDirectory;
            EnsureInspectCaptureCanCommit(attempt);

            var cardPath = Configuration.AutoAddToLibrary && libraryStore is not null
                ? Path.Combine(captureDirectory, "glam-card.png")
                : Path.Combine(captureDirectory, baseName + ".png");
            var rawPath = Configuration.AutoAddToLibrary && libraryStore is not null
                ? Path.Combine(captureDirectory, "raw-preview.png")
                : Path.Combine(captureDirectory, baseName + "_preview.png");
            var jsonPath = Configuration.AutoAddToLibrary && libraryStore is not null
                ? Path.Combine(captureDirectory, "diagnostic.json")
                : Path.Combine(captureDirectory, baseName + ".json");

            // M3.14 makes the character preview the Library-first visual.
            // Managed Library captures therefore always keep this image even when
            // the old optional SaveRawPreview setting is disabled. Outside the
            // Library we preserve the existing opt-in raw-preview behaviour.
            var keepPreviewImage = (Configuration.AutoAddToLibrary && libraryStore is not null) || Configuration.SaveRawPreview;
            if (keepPreviewImage)
            {
                // RenderCaptureAsync prepares this portrait once and uses that
                // exact image for both the Full Card and the saved automatic
                // Inspect preview. Personal Fitting Room previews remain on
                // their independent native capture path.
                var storedPreviewBytes = renderedCapture.PreparedPortraitPng;
                SetInspectCaptureStage(attempt, InspectCaptureStage.WritePreview);
                stagedFiles.Add(await WriteInspectStagingFileAsync(
                    attempt,
                    rawPath,
                    storedPreviewBytes));
            }

            SetInspectCaptureStage(attempt, InspectCaptureStage.WriteCard);
            stagedFiles.Add(await WriteInspectStagingFileAsync(attempt, cardPath, cardBytes));

            if (Configuration.WriteDiagnosticJson)
            {
                var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
                {
                    WriteIndented = true,
                });
                SetInspectCaptureStage(attempt, InspectCaptureStage.WriteJson);
                stagedFiles.Add(await WriteInspectStagingTextFileAsync(attempt, jsonPath, json));
            }

            if (Configuration.CopyToClipboard)
            {
                SetInspectCaptureStage(attempt, InspectCaptureStage.Clipboard);
                await previewCaptureService.CopyPngBytesToClipboardAsync(
                    cardBytes,
                    Path.GetFileNameWithoutExtension(cardPath),
                    attempt.Token);
                EnsureInspectCaptureCanCommit(attempt);
            }

            SetInspectCaptureStage(attempt, InspectCaptureStage.Finalize);
            finalFilePromotionStarted = true;
            PublishInspectStagingFiles(attempt, stagedFiles);
            finalFilesPublished = true;

            if (Configuration.AutoAddToLibrary && libraryStore is not null)
            {
                try
                {
                    SetInspectCaptureStage(attempt, InspectCaptureStage.LibraryDatabase);
                    libraryEntryId = libraryStore.AddCapture(
                        snapshot,
                        cardPath,
                        keepPreviewImage ? rawPath : null,
                        Configuration.WriteDiagnosticJson ? jsonPath : null,
                        () => InspectCaptureHasAuthority(attempt));
                    EnsureInspectCaptureCanCommit(attempt);
                }
                catch (OperationCanceledException) when (!InspectCaptureHasAuthority(attempt))
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (IsPluginLifetimeValid())
                    {
                        Log.Error(ex, "Glam Card was saved, but could not be added to the library.");
                        libraryWarning = $"Card saved, but library indexing failed: {ex.Message}";
                    }
                }
            }

            SetInspectCaptureStage(attempt, InspectCaptureStage.Finalize);
            EnsureInspectCaptureCanCommit(attempt);
            successMessage = $"Captured Glam Card for {snapshot.CharacterName} @ {snapshot.HomeWorld} → {cardPath}";
        }
        catch (OperationCanceledException) when (
            attempt.Token.IsCancellationRequested ||
            pluginLifetimeToken.IsCancellationRequested ||
            !IsCurrentGeneration(attempt.Generation))
        {
            // Retirement owns user-visible cancellation reporting. A worker may
            // resume long after a newer generation starts, so it must remain
            // silent and only unwind its local resources here.
        }
        catch (Exception ex)
        {
            if (IsPluginLifetimeValid())
            {
                Log.Error(ex, "GlamSpector capture failed.");
                errorMessage = $"Capture failed: {ex.Message}";
            }
        }
        finally
        {
            try
            {
                texture?.Dispose();
            }
            catch (Exception ex)
            {
                if (IsPluginLifetimeValid())
                    Log.Warning(ex, "Could not dispose the Inspect capture texture cleanly.");
            }
            CleanupInspectStagingFiles(stagedFiles);
            if (!finalFilesPublished && managedCaptureDirectory is not null)
                TryDeleteEmptyInspectCaptureDirectory(managedCaptureDirectory);

            var releaseLifetimeHere = textureAcquired;
            if (!textureAcquired)
            {
                _ = ObserveLateCaptureTextureAsync(captureRequest.TextureTask, attempt);
            }

            var completion = CompleteInspectCaptureAttempt(attempt);
            if (!completion.CompletedWithAuthority &&
                finalFilePromotionStarted &&
                !libraryEntryId.HasValue &&
                managedCaptureDirectory is not null)
            {
                CleanupPublishedInspectFiles(stagedFiles);
                TryDeleteEmptyInspectCaptureDirectory(managedCaptureDirectory);
            }
            try
            {
                if (completion.CompletedWithAuthority)
                {
                    QueueLifetimeFrameworkCallback(() =>
                    {
                        RestorePreviousFocusedAddon(
                            completion.FocusRestore.PreviousId,
                            completion.FocusRestore.InspectId);
                        if (libraryEntryId.HasValue)
                        {
                            libraryUi?.NotifyLibraryChanged(libraryEntryId.Value);
                            QueueAdventurerPlateCaptureIfConfigured(
                                libraryEntryId.Value,
                                snapshot,
                                attempt.Generation);
                        }
                        if (successMessage is not null && Configuration.NotifyCaptureSuccess)
                            ChatGui.Print(successMessage, "GlamSpector");
                        if (libraryWarning is not null)
                            ChatGui.PrintError(libraryWarning, "GlamSpector");
                        if (errorMessage is not null)
                            ChatGui.PrintError(errorMessage, "GlamSpector");
                    }, attempt.Generation);
                }
            }
            finally
            {
                if (releaseLifetimeHere)
                    ReleasePluginLifetimeOperation();
            }
        }
    }

    private async Task<StagedInspectFile> WriteInspectStagingFileAsync(
        InspectCaptureAttempt attempt,
        string finalPath,
        ReadOnlyMemory<byte> bytes)
    {
        EnsureInspectCaptureCanCommit(attempt);
        var temporaryPath = GetInspectStagingPath(finalPath, attempt.Generation);
        var finalExistedBefore = File.Exists(finalPath);
        try
        {
            await File.WriteAllBytesAsync(temporaryPath, bytes, attempt.Token);
            EnsureInspectCaptureCanCommit(attempt);
            return new StagedInspectFile(temporaryPath, finalPath, finalExistedBefore);
        }
        catch
        {
            TryDeleteInspectStagingFile(temporaryPath);
            throw;
        }
    }

    private async Task<StagedInspectFile> WriteInspectStagingTextFileAsync(
        InspectCaptureAttempt attempt,
        string finalPath,
        string contents)
    {
        EnsureInspectCaptureCanCommit(attempt);
        var temporaryPath = GetInspectStagingPath(finalPath, attempt.Generation);
        var finalExistedBefore = File.Exists(finalPath);
        try
        {
            await File.WriteAllTextAsync(temporaryPath, contents, attempt.Token);
            EnsureInspectCaptureCanCommit(attempt);
            return new StagedInspectFile(temporaryPath, finalPath, finalExistedBefore);
        }
        catch
        {
            TryDeleteInspectStagingFile(temporaryPath);
            throw;
        }
    }

    private void PublishInspectStagingFiles(
        InspectCaptureAttempt attempt,
        IReadOnlyList<StagedInspectFile> stagedFiles)
    {
        EnsureInspectCaptureCanCommit(attempt);
        foreach (var staged in stagedFiles)
        {
            EnsureInspectCaptureCanCommit(attempt);
            File.Move(staged.TemporaryPath, staged.FinalPath, overwrite: true);
            EnsureInspectCaptureCanCommit(attempt);
        }
    }

    private static string GetInspectStagingPath(string finalPath, long generation) =>
        finalPath + $".glamspector-gen-{generation}.tmp";

    private static void CleanupInspectStagingFiles(IEnumerable<StagedInspectFile> stagedFiles)
    {
        foreach (var staged in stagedFiles)
            TryDeleteInspectStagingFile(staged.TemporaryPath);
    }

    private static void CleanupPublishedInspectFiles(IEnumerable<StagedInspectFile> stagedFiles)
    {
        foreach (var staged in stagedFiles)
        {
            if (staged.FinalExistedBefore)
                continue;

            try
            {
                if (File.Exists(staged.FinalPath))
                    File.Delete(staged.FinalPath);
            }
            catch
            {
                // Best-effort rollback of media published immediately before a
                // retirement race. Never delete a path that predated the attempt.
            }
        }
    }

    private static void TryDeleteInspectStagingFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort. A worker-owned write may still be unwinding after
            // cancellation; the generation-specific name prevents publication.
        }
    }

    private static void TryDeleteEmptyInspectCaptureDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch
        {
            // Best-effort cleanup only; never risk deleting non-empty user data.
        }
    }

    private async Task ObserveLateCaptureTextureAsync(
        Task<Dalamud.Interface.Textures.TextureWraps.IDalamudTextureWrap> textureTask,
        InspectCaptureAttempt? inspectAttempt = null)
    {
        try
        {
            using var texture = await textureTask;
        }
        catch
        {
            // The abandoned provider request was cancelled or failed and has no
            // texture to release.
        }
        finally
        {
            if (inspectAttempt is not null)
                MarkInspectProviderTaskSettled(inspectAttempt);
            ReleasePluginLifetimeOperation();
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

    private void QueueAdventurerPlateCaptureIfConfigured(
        long entryId,
        GlamourSnapshot snapshot,
        long originatingInspectGeneration)
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
                    OriginatingInspectGeneration = originatingInspectGeneration,
                    EntityId = snapshot.EntityId,
                    CharacterName = snapshot.CharacterName,
                    HomeWorld = snapshot.HomeWorld,
                };
                return;
            case AdventurerPlateCaptureMode.Automatic:
                StartAutomaticAdventurerPlateCapture(
                    entryId,
                    originatingInspectGeneration,
                    snapshot.EntityId,
                    snapshot.CharacterName,
                    snapshot.HomeWorld);
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
                StartAutomaticAdventurerPlateCapture(
                    prompt.EntryId,
                    prompt.OriginatingInspectGeneration,
                    prompt.EntityId,
                    prompt.CharacterName,
                    prompt.HomeWorld);
            }
            ImGui.SameLine();
            if (ImGui.Button("Skip", new Vector2(100f, 0f)))
                plateCapturePrompt = null;
        }
        ImGui.End();

        if (!open)
            plateCapturePrompt = null;
    }

    private unsafe void StartAutomaticAdventurerPlateCapture(
        long entryId,
        long originatingInspectGeneration,
        uint entityId,
        string characterName,
        string homeWorld)
    {
        lock (captureLifecycleSync)
        {
            if (disposed || pluginLifetimeToken.IsCancellationRequested)
                return;

            try
            {
                if (libraryStore is null)
                    throw new InvalidOperationException("The GlamSpector library is unavailable.");

                if (autoPlateCapture is not null)
                    return;

                var gameObject = ObjectTable.SearchByEntityId(entityId);
                if (gameObject is null || !gameObject.IsValid() || gameObject.Address == 0)
                {
                    Log.Debug(
                        $"Automatic Adventurer Plate skipped: Inspect gen={originatingInspectGeneration}, " +
                        $"entity=0x{entityId:X8} ({characterName}) is no longer nearby.");
                    return;
                }

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
                    OriginatingInspectGeneration = originatingInspectGeneration,
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
    }

    private unsafe void UpdateAutomaticAdventurerPlateCapture()
    {
        lock (captureLifecycleSync)
        {
            if (!disposed)
                UpdateAutomaticAdventurerPlateCaptureLocked();
        }
    }

    private unsafe void UpdateAutomaticAdventurerPlateCaptureLocked()
    {
        if (autoPlateCapture is not { } state)
            return;
        if (!IsPluginLifetimeValid())
        {
            autoPlateCapture = null;
            return;
        }

        if (inspectReader.TryGetCurrentInspectEntityId(out var currentInspectEntityId) &&
            currentInspectEntityId != state.EntityId)
        {
            AbandonActiveAutomaticPlateCapture(
                state,
                $"Inspect moved to entity=0x{currentInspectEntityId:X8} while this attempt still owned entity=0x{state.EntityId:X8}.");
            return;
        }

        var lifetimeOperationHeld = false;
        var now = DateTime.UtcNow;
        var elapsedSeconds = (now - state.StartedAtUtc).TotalSeconds;
        var loadTimeoutSeconds = Math.Clamp(Configuration.AdventurerPlateTimeoutSeconds, 1f, 10f);
        var settleSeconds = Math.Clamp(Configuration.AdventurerPlateSettleSeconds, 0.25f, 3f);
        var overallTimeoutSeconds = loadTimeoutSeconds + settleSeconds + AutomaticPlateDeadlineGraceSeconds;
        if (elapsedSeconds > overallTimeoutSeconds)
        {
            FinishAutomaticPlateFailure(state, "Timed out before the Adventurer Plate capture could complete.");
            return;
        }

        if (state.ReadySinceUtc is null && elapsedSeconds > loadTimeoutSeconds)
        {
            FinishAutomaticPlateFailure(state, "Timed out waiting for the Adventurer Plate to load.");
            return;
        }

        try
        {
            var addon = GameGui.GetAddonByName("CharaCard");
            if (addon.IsNull || !addon.IsVisible)
            {
                if (state.ReadySinceUtc.HasValue)
                {
                    AbandonActiveAutomaticPlateCapture(
                        state,
                        "the Plate closed while its portrait was settling.");
                }
                return;
            }

            var agentPtr = GameGui.FindAgentInterface(addon);
            if (agentPtr.IsNull)
            {
                if (state.ReadySinceUtc.HasValue)
                {
                    AbandonActiveAutomaticPlateCapture(
                        state,
                        "the Plate agent became unavailable while settling.");
                }
                return;
            }

            var agent = (AgentCharaCard*)agentPtr.Address;
            if (agent == null || agent->Data == null)
            {
                if (state.ReadySinceUtc.HasValue)
                {
                    AbandonActiveAutomaticPlateCapture(
                        state,
                        "the Plate data became unavailable while settling.");
                }
                return;
            }

            var data = agent->Data;
            if (data->EntityId != 0 && data->EntityId != state.EntityId)
            {
                AbandonActiveAutomaticPlateCapture(
                    state,
                    $"the Plate changed to entity=0x{data->EntityId:X8}.");
                return;
            }

            var plateName = data->Name.ToString();
            if (string.IsNullOrWhiteSpace(plateName))
            {
                if (state.ReadySinceUtc.HasValue)
                {
                    AbandonActiveAutomaticPlateCapture(
                        state,
                        "the Plate lost its character identity while settling.");
                }
                return;
            }
            if (!string.Equals(plateName, state.CharacterName, StringComparison.OrdinalIgnoreCase))
            {
                AbandonActiveAutomaticPlateCapture(
                    state,
                    $"the Plate changed to {plateName}.");
                return;
            }

            if (data->IsNotCreated)
            {
                FinishAutomaticPlateFailure(state, $"{state.CharacterName} has not created an Adventurer Plate.");
                return;
            }

            if (data->PortraitTexture == null)
            {
                if (state.ReadySinceUtc.HasValue)
                {
                    FinishAutomaticPlateFailure(state, "The Adventurer Plate portrait became unavailable while settling.");
                    return;
                }
                return;
            }

            // Agent data can report fully loaded before the native Plate has
            // actually reached the swap chain. Hold the Plate visibly on-screen
            // for a real-time settle interval instead of assuming a handful of
            // framework frames is enough.
            state.ReadySinceUtc ??= now;
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

            AcquirePluginLifetimeOperation();
            lifetimeOperationHeld = true;
            lock (captureLifecycleSync)
            {
                if (disposed ||
                    pluginLifetimeToken.IsCancellationRequested ||
                    !ReferenceEquals(autoPlateCapture, state))
                {
                    throw new OperationCanceledException("This automatic Adventurer Plate attempt is no longer active.");
                }

                var request = previewCaptureService.BeginAddonCapture(
                    "CharaCard",
                    "Adventurer Plate",
                    autoUpdate: true,
                    takeBeforeImGuiRender: true,
                    cancellationToken: pluginLifetimeToken);
                var closeAfter = Configuration.CloseAutoOpenedAdventurerPlate && state.OpenedByGlamSpector;
                autoPlateCapture = null;
                _ = FinishAdventurerPlateCaptureAsync(
                    entry,
                    request,
                    portraitSettings,
                    closeAfter,
                    automatic: true,
                    automaticOwner: state);
                lifetimeOperationHeld = false;
            }
        }
        catch (Exception ex)
        {
            if (lifetimeOperationHeld)
                ReleasePluginLifetimeOperation();
            if (!IsPluginLifetimeValid())
            {
                if (ReferenceEquals(autoPlateCapture, state))
                    autoPlateCapture = null;
                return;
            }
            FinishAutomaticPlateFailure(state, ex.Message, ex);
        }
    }

    private double GetAutomaticPlateOverallTimeoutSeconds()
    {
        var loadTimeoutSeconds = Math.Clamp(Configuration.AdventurerPlateTimeoutSeconds, 1f, 10f);
        var settleSeconds = Math.Clamp(Configuration.AdventurerPlateSettleSeconds, 0.25f, 3f);
        return loadTimeoutSeconds + settleSeconds + AutomaticPlateDeadlineGraceSeconds;
    }

    private void AbandonActiveAutomaticPlateCapture(AutoPlateCaptureState state, string reason)
    {
        if (!ReferenceEquals(autoPlateCapture, state))
            return;

        autoPlateCapture = null;
        LogAutomaticPlateAbandonment(state, reason);

        if (Configuration.CloseAutoOpenedAdventurerPlate &&
            state.OpenedByGlamSpector &&
            IsAutomaticPlateStillOwned(state))
        {
            CloseAdventurerPlateAgent();
        }
    }

    private void LogAutomaticPlateAbandonment(AutoPlateCaptureState state, string reason)
    {
        lock (captureLifecycleSync)
        {
            if (disposed || pluginLifetimeToken.IsCancellationRequested)
                return;

            Log.Debug(
                $"Automatic Adventurer Plate abandoned: Inspect gen={state.OriginatingInspectGeneration}, " +
                $"entity=0x{state.EntityId:X8} ({state.CharacterName}); {reason}");
        }
    }

    private void FinishAutomaticPlateFailure(
        AutoPlateCaptureState state,
        string message,
        Exception? exception = null,
        bool closeAutoOpenedPlate = true)
    {
        if (!ReferenceEquals(autoPlateCapture, state))
            return;

        autoPlateCapture = null;

        if (exception is not null)
            Log.Warning(exception, "Automatic Adventurer Plate capture failed.");
        else
            Log.Warning($"Automatic Adventurer Plate capture failed: {message}");

        if (closeAutoOpenedPlate &&
            Configuration.CloseAutoOpenedAdventurerPlate &&
            state.OpenedByGlamSpector &&
            IsAutomaticPlateStillOwned(state))
        {
            CloseAdventurerPlateAgent();
        }

        if (Configuration.NotifyAdventurerPlate)
            ChatGui.PrintError($"Glam Card saved; Adventurer Plate not attached: {message}", "GlamSpector");
    }

    private unsafe bool IsAutomaticPlateStillOwned(AutoPlateCaptureState state)
    {
        var addon = GameGui.GetAddonByName("CharaCard");
        if (addon.IsNull || !addon.IsVisible)
            return false;

        var agentPtr = GameGui.FindAgentInterface(addon);
        if (agentPtr.IsNull)
            return false;

        var agent = (AgentCharaCard*)agentPtr.Address;
        if (agent == null || agent->Data == null)
            return false;

        var data = agent->Data;
        var name = data->Name.ToString();
        return data->EntityId != 0 &&
               data->EntityId == state.EntityId &&
               !string.IsNullOrWhiteSpace(name) &&
               string.Equals(name, state.CharacterName, StringComparison.OrdinalIgnoreCase);
    }

    private Task<bool> IsAutomaticPlateStillOwnedOnFrameworkAsync(AutoPlateCaptureState state)
    {
        lock (captureLifecycleSync)
        {
            if (disposed || pluginLifetimeToken.IsCancellationRequested)
                return Task.FromResult(false);

            return Framework.Run(() =>
            {
                lock (captureLifecycleSync)
                {
                    return !disposed &&
                           !pluginLifetimeToken.IsCancellationRequested &&
                           IsAutomaticPlateStillOwned(state);
                }
            });
        }
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
        if (!IsPluginLifetimeValid())
            return;

        var lifetimeOperationHeld = false;
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

            AcquirePluginLifetimeOperation();
            lifetimeOperationHeld = true;
            lock (captureLifecycleSync)
            {
                if (disposed || pluginLifetimeToken.IsCancellationRequested)
                    throw new OperationCanceledException("GlamSpector is unloading.");

                var request = previewCaptureService.BeginAddonCapture(
                    "CharaCard",
                    "Adventurer Plate",
                    autoUpdate: true,
                    takeBeforeImGuiRender: true,
                    cancellationToken: pluginLifetimeToken);
                _ = FinishAdventurerPlateCaptureAsync(
                    entry,
                    request,
                    portraitSettings,
                    closeAfterCapture: false,
                    automatic: false,
                    automaticOwner: null);
                lifetimeOperationHeld = false;
            }
        }
        catch (Exception ex)
        {
            if (lifetimeOperationHeld)
                ReleasePluginLifetimeOperation();
            if (!IsPluginLifetimeValid())
                return;
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

    private async Task FinishAdventurerPlateCaptureAsync(
        LibraryEntry entry,
        CaptureRequest request,
        PortraitSettingsSnapshot? portraitSettings,
        bool closeAfterCapture,
        bool automatic,
        AutoPlateCaptureState? automaticOwner)
    {
        var textureAcquired = false;
        try
        {
            using var texture = await request.TextureTask.WaitAsync(pluginLifetimeToken);
            textureAcquired = true;

            // Keep the Plate open while an auto-updating viewport texture sees a
            // few more presented frames. The Plate is native FFXIV UI, so we
            // sample the main viewport before Dalamud ImGui is rendered; this
            // keeps the Plate while excluding GlamSpector/other plugin windows.
            await Task.Delay(250, pluginLifetimeToken);
            if (automaticOwner is not null &&
                !await IsAutomaticPlateStillOwnedOnFrameworkAsync(automaticOwner))
            {
                LogAutomaticPlateAbandonment(
                    automaticOwner,
                    "the Plate closed or changed identity before screenshot encoding.");
                return;
            }

            var bytes = await previewCaptureService.EncodePngAsync(texture, pluginLifetimeToken);
            pluginLifetimeToken.ThrowIfCancellationRequested();
            if (automaticOwner is not null &&
                !await IsAutomaticPlateStillOwnedOnFrameworkAsync(automaticOwner))
            {
                LogAutomaticPlateAbandonment(
                    automaticOwner,
                    "the Plate closed or changed identity during screenshot encoding.");
                return;
            }

            var folder = Path.GetDirectoryName(entry.CardPath) ?? Configuration.OutputDirectory;
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

            Task writeTask;
            lock (captureLifecycleSync)
            {
                pluginLifetimeToken.ThrowIfCancellationRequested();
                if (disposed)
                    throw new OperationCanceledException(pluginLifetimeToken);
                Directory.CreateDirectory(folder);
                writeTask = File.WriteAllBytesAsync(platePath, bytes, pluginLifetimeToken);
            }
            await writeTask;

            lock (captureLifecycleSync)
            {
                pluginLifetimeToken.ThrowIfCancellationRequested();
                if (disposed)
                    throw new OperationCanceledException(pluginLifetimeToken);
                libraryStore!.SetAdventurerPlatePath(entry.Id, platePath);
                if (portraitSettings is not null)
                    libraryStore.SetPortraitSettings(entry.Id, portraitSettings);
            }

            QueueLifetimeFrameworkCallback(() =>
            {
                if (closeAfterCapture && automaticOwner is not null && IsAutomaticPlateStillOwned(automaticOwner))
                    CloseAdventurerPlateAgent();
                libraryUi?.NotifyLibraryChanged(entry.Id);
                if ((automatic && Configuration.NotifyAdventurerPlate) || (!automatic && Configuration.NotifyCaptureSuccess))
                    ChatGui.Print($"Attached Adventurer Plate for {entry.CharacterName} @ {entry.HomeWorld} → {platePath}", "GlamSpector");
            });
        }
        catch (OperationCanceledException) when (pluginLifetimeToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (!IsPluginLifetimeValid())
                return;

            Log.Error(ex, "Adventurer Plate capture failed.");
            QueueLifetimeFrameworkCallback(() =>
            {
                if (closeAfterCapture && automaticOwner is not null && IsAutomaticPlateStillOwned(automaticOwner))
                    CloseAdventurerPlateAgent();
                if (!automatic || Configuration.NotifyAdventurerPlate)
                    ChatGui.PrintError($"Adventurer Plate capture failed: {ex.Message}", "GlamSpector");
            });
        }
        finally
        {
            if (textureAcquired)
                ReleasePluginLifetimeOperation();
            else
                _ = ObserveLateCaptureTextureAsync(request.TextureTask);
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
