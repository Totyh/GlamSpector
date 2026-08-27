using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;
using GlamSpector.Models;
using GlamSpector.Services;

namespace GlamSpector.UI;

public sealed class LibraryUi
{
    private readonly LibraryStore store;
    private readonly ITextureProvider textureProvider;
    private readonly Configuration configuration;
    private readonly Action<LibraryEntry> copyCard;
    private readonly Action<LibraryEntry> openCard;
    private readonly Action<LibraryEntry> openFolder;
    private readonly Action<LibraryEntry> attachAdventurerPlate;
    private readonly Action<LibraryEntry> tryOnGlam;
    private readonly Action<LibraryEntry> capturePersonalPreview;
    private readonly Action<LibraryEntry, PersonalPreview> generateShareCard;
    private readonly Action<string> copyImageToClipboard;
    private readonly Action<GlamourPiece> tryOnItem;
    private readonly Action<GlamourPiece> linkItemInChat;
    private readonly InventoryOwnershipService ownershipService;
    private readonly GlamCodeService glamCodeService;
    private readonly EorzeaCollectionImportService eorzeaCollectionImportService;
    private readonly Action openSettings;

    private bool isOpen;
    private string search = string.Empty;
    private LibrarySort sort = LibrarySort.Newest;
    private List<LibraryEntry> allEntries = [];
    private List<LibraryEntry> entries = [];
    private LibraryEntry? selected;
    private long? confirmRemoveId;
    private long? confirmDeleteId;
    private string? lastError;
    private string? importStatus;
    private MediaViewMode mediaViewMode = MediaViewMode.Primary;
    private int duplicateCandidateCount;
    private bool confirmDuplicateCleanup;
    private bool showWantedItems;
    private bool showGlamCodeImport;
    private string glamCodeImportText = string.Empty;
    private bool showEorzeaCollectionImport;
    private string eorzeaCollectionUrl = string.Empty;
    private string eorzeaCollectionPageSource = string.Empty;
    private Task<EorzeaCollectionImportResult>? eorzeaCollectionImportTask;
    private int selectedSourceImageIndex;
    private long? confirmDeletePersonalPreviewId;
    private long? confirmDeleteShareCardId;
    private List<WantedItem> wantedItems = [];
    private HashSet<uint> wantedItemIds = [];
    private Dictionary<long, OwnershipProgress> ownershipProgressByEntry = [];
    private Dictionary<long, int> wantedCountByEntry = [];
    private readonly Dictionary<long, LibraryEntryPresentation> presentationByEntry = [];
    private DateTime lastOwnershipProgressRefreshUtc = DateTime.MinValue;
    private int totalEntryCount;
    private int lastFrameRowsDrawn;
    private int lastFrameFirstRenderedIndex = -1;
    private int lastFrameLastRenderedIndex = -1;
    private int lastFrameThumbnailRequests;
    private int snapshotPrimaryMediaResolutions;
    private int performanceSampleFrames;
    private double performanceSampleTotalMilliseconds;
    private double performanceSampleMaxMilliseconds;
    private double completedAverageDrawMilliseconds;
    private double completedMaxDrawMilliseconds;

    private bool showFilters;
    private RatingFilter ratingFilter = RatingFilter.Any;
    private OwnershipFilter ownershipFilter = OwnershipFilter.Any;
    private WantedFilter wantedFilter = WantedFilter.Any;
    private PlateFilter plateFilter = PlateFilter.Any;

    private string tagsEdit = string.Empty;
    private string notesEdit = string.Empty;
    private bool tagsEditDirty;
    private bool notesEditDirty;
    private float libraryListWidth = 360f;
    private bool restoreSelectionPending = true;
    private bool applySecondarySectionState = true;
    private long? editTitleEntryId;
    private string titleEdit = string.Empty;
    private string? titleEditError;

    private enum MediaViewMode
    {
        Primary,
        CapturedPreview,
        GlamCard,
        PersonalPreviews,
        GeneratedShareCards,
        SourceImages,
        AdventurerPlate,
    }

    private sealed class LibraryEntryPresentation
    {
        public string? PrimaryImagePath { get; set; }
        public required string LocalCapturedAt { get; init; }
        public required string RatingText { get; init; }
        public required IReadOnlyList<PersonalPreview> PersonalPreviews { get; init; }
        public required IReadOnlyList<GeneratedShareCard> GeneratedShareCards { get; init; }
        public required IReadOnlyList<string> SourceImagePaths { get; init; }
        public bool HasCapturedPreview { get; init; }
        public bool HasCardImage { get; init; }
        public bool HasAdventurerPlate { get; init; }
    }

    private enum RatingFilter
    {
        Any,
        Unrated,
        OnePlus,
        TwoPlus,
        ThreePlus,
        FourPlus,
        FiveOnly,
    }

    private enum OwnershipFilter
    {
        Any,
        FullyOwned,
        HasUnverified,
    }

    private enum WantedFilter
    {
        Any,
        HasWanted,
        NoWanted,
    }

    private enum PlateFilter
    {
        Any,
        HasPlate,
        NoPlate,
    }

    public LibraryUi(
        LibraryStore store,
        ITextureProvider textureProvider,
        Configuration configuration,
        Action<LibraryEntry> copyCard,
        Action<LibraryEntry> openCard,
        Action<LibraryEntry> openFolder,
        Action<LibraryEntry> attachAdventurerPlate,
        Action<LibraryEntry> tryOnGlam,
        Action<LibraryEntry> capturePersonalPreview,
        Action<LibraryEntry, PersonalPreview> generateShareCard,
        Action<string> copyImageToClipboard,
        Action<GlamourPiece> tryOnItem,
        Action<GlamourPiece> linkItemInChat,
        InventoryOwnershipService ownershipService,
        GlamCodeService glamCodeService,
        EorzeaCollectionImportService eorzeaCollectionImportService,
        Action openSettings)
    {
        this.store = store;
        this.textureProvider = textureProvider;
        this.configuration = configuration;
        this.copyCard = copyCard;
        this.openCard = openCard;
        this.openFolder = openFolder;
        this.attachAdventurerPlate = attachAdventurerPlate;
        this.tryOnGlam = tryOnGlam;
        this.capturePersonalPreview = capturePersonalPreview;
        this.generateShareCard = generateShareCard;
        this.copyImageToClipboard = copyImageToClipboard;
        this.tryOnItem = tryOnItem;
        this.linkItemInChat = linkItemInChat;
        this.ownershipService = ownershipService;
        this.glamCodeService = glamCodeService;
        this.eorzeaCollectionImportService = eorzeaCollectionImportService;
        this.openSettings = openSettings;
        RestoreUiState();
    }

    private void RestoreUiState()
    {
        var normalized = false;
        sort = RestoreEnum(configuration.LibrarySortMode, LibrarySort.Newest, ref normalized);
        ratingFilter = RestoreEnum(configuration.LibraryRatingFilter, RatingFilter.Any, ref normalized);
        ownershipFilter = RestoreEnum(configuration.LibraryOwnershipFilter, OwnershipFilter.Any, ref normalized);
        wantedFilter = RestoreEnum(configuration.LibraryWantedFilter, WantedFilter.Any, ref normalized);
        plateFilter = RestoreEnum(configuration.LibraryPlateFilter, PlateFilter.Any, ref normalized);
        showFilters = configuration.LibraryFiltersExpanded;
        configuration.LibrarySortMode = (int)sort;
        configuration.LibraryRatingFilter = (int)ratingFilter;
        configuration.LibraryOwnershipFilter = (int)ownershipFilter;
        configuration.LibraryWantedFilter = (int)wantedFilter;
        configuration.LibraryPlateFilter = (int)plateFilter;

        if (!float.IsFinite(configuration.LibraryListWidth) || configuration.LibraryListWidth < 240f)
        {
            libraryListWidth = 360f;
            configuration.LibraryListWidth = libraryListWidth;
            normalized = true;
        }
        else
        {
            libraryListWidth = configuration.LibraryListWidth;
        }

        if (configuration.LibrarySelectedEntryId < 0)
        {
            configuration.LibrarySelectedEntryId = 0;
            normalized = true;
        }

        if (normalized)
            SaveUiState();
    }

    private static T RestoreEnum<T>(int storedValue, T fallback, ref bool normalized)
        where T : struct, Enum
    {
        if (Enum.IsDefined(typeof(T), storedValue))
            return (T)Enum.ToObject(typeof(T), storedValue);
        normalized = true;
        return fallback;
    }

    private void SaveUiState()
    {
        try
        {
            configuration.Save();
        }
        catch (Exception ex)
        {
            // UI-memory persistence must never prevent the Library itself from
            // opening or being used.
            Plugin.Log.Warning(ex, "Could not save GlamSpector Library UI state.");
        }
    }

    private void PersistFilters()
    {
        configuration.LibraryRatingFilter = (int)ratingFilter;
        configuration.LibraryOwnershipFilter = (int)ownershipFilter;
        configuration.LibraryWantedFilter = (int)wantedFilter;
        configuration.LibraryPlateFilter = (int)plateFilter;
        SaveUiState();
    }

    public void Open()
    {
        isOpen = true;
        Refresh();
    }

    public void Toggle()
    {
        isOpen = !isOpen;
        if (isOpen)
            Refresh();
    }

    public void NotifyLibraryChanged(long? selectId = null)
    {
        Refresh();
        if (selectId.HasValue)
            Select(selectId.Value);
    }

    public void NotifyShareCardGenerated(long entryId)
    {
        Refresh();
        Select(entryId);
        mediaViewMode = MediaViewMode.GeneratedShareCards;
    }

    public void Draw()
    {
        PollEorzeaCollectionImport();
        if (!isOpen)
            return;

        var drawStarted = Stopwatch.GetTimestamp();
        lastFrameRowsDrawn = 0;
        lastFrameFirstRenderedIndex = -1;
        lastFrameLastRenderedIndex = -1;
        lastFrameThumbnailRequests = 0;

        ImGui.SetNextWindowSize(new Vector2(1180, 760), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("GlamSpector Library###GlamSpectorLibrary", ref isOpen))
        {
            ImGui.End();
            RecordDrawPerformance(drawStarted);
            return;
        }

        ImGui.TextUnformatted("Glamour Library");
        ImGui.SameLine();
        ImGui.TextDisabled($"{entries.Count} {(entries.Count == 1 ? "entry" : "entries")}");

        DrawLibraryToolbar();

        if (showFilters)
            DrawFilterBar();

        if (lastError is not null)
        {
            ImGui.TextWrapped($"Library error: {lastError}");
        }
        else if (importStatus is not null)
        {
            ImGui.TextDisabled(importStatus);
        }

        if (confirmDuplicateCleanup)
        {
            if (duplicateCandidateCount <= 0)
            {
                ImGui.TextDisabled("No structured duplicate captures found.");
                ImGui.SameLine();
                if (ImGui.SmallButton("OK##dupnone"))
                    confirmDuplicateCleanup = false;
            }
            else
            {
                ImGui.TextWrapped($"Found {duplicateCandidateCount} older duplicate capture{(duplicateCandidateCount == 1 ? string.Empty : "s")}. The newest matching capture for each outfit will be kept.");
                if (ImGui.Button("Remove duplicate entries"))
                    CleanupDuplicates(deleteFiles: false);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Remove duplicate SQLite library entries only. Their PNG/sidecar files remain on disk and can be re-imported later.");
                ImGui.SameLine();
                if (ImGui.Button("Delete duplicate files…"))
                    CleanupDuplicates(deleteFiles: true);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Delete older duplicate Library entries and their associated preview/card/JSON/Plate files from disk.");
                ImGui.SameLine();
                if (ImGui.Button("Cancel##dups"))
                    confirmDuplicateCleanup = false;
            }
        }

        RefreshOwnershipProgressCache(force: false);
        ImGui.Separator();

        var available = ImGui.GetContentRegionAvail();
        const float splitterWidth = 7f;
        var maximumListWidth = Math.Max(280f, available.X - 360f - splitterWidth - ImGui.GetStyle().ItemSpacing.X * 2f);
        libraryListWidth = Math.Clamp(libraryListWidth, 280f, maximumListWidth);

        if (ImGui.BeginChild("##GlamSpectorLibraryList", new Vector2(libraryListWidth, 0), true))
            DrawEntryList();
        ImGui.EndChild();

        ImGui.SameLine();

        ImGui.InvisibleButton("##GlamSpectorLibrarySplitter", new Vector2(splitterWidth, available.Y));
        if (ImGui.IsItemActive())
        {
            libraryListWidth = Math.Clamp(libraryListWidth + ImGui.GetIO().MouseDelta.X, 280f, maximumListWidth);
            configuration.LibraryListWidth = libraryListWidth;
        }
        if (ImGui.IsItemDeactivated())
            SaveUiState();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Drag to resize the Library list.");

        ImGui.SameLine();

        if (ImGui.BeginChild("##GlamSpectorLibraryDetails", new Vector2(0, 0), true))
            DrawDetails();
        ImGui.EndChild();

        ImGui.End();

        if (showWantedItems)
            DrawWantedWindow();
        if (showGlamCodeImport)
            DrawGlamCodeImportWindow();
        if (showEorzeaCollectionImport)
            DrawEorzeaCollectionImportWindow();

        RecordDrawPerformance(drawStarted);
    }

    public string GetPerformanceDiagnostics()
    {
        if (!isOpen)
            return $"Library perf: open=False; total={totalEntryCount}.";

        return $"Library perf: open=True; total={totalEntryCount}; search={allEntries.Count}; matching={entries.Count}; " +
               $"range={lastFrameFirstRenderedIndex}..{lastFrameLastRenderedIndex}; rows={lastFrameRowsDrawn}; overscan=2; " +
               $"thumbnails={lastFrameThumbnailRequests}; " +
               $"mediaResolved={snapshotPrimaryMediaResolutions}/refresh; " +
               $"draw={completedAverageDrawMilliseconds:0.00}ms avg/{completedMaxDrawMilliseconds:0.00}ms max.";
    }

    private void RecordDrawPerformance(long startedTimestamp)
    {
        var elapsedMilliseconds = Stopwatch.GetElapsedTime(startedTimestamp).TotalMilliseconds;
        performanceSampleFrames++;
        performanceSampleTotalMilliseconds += elapsedMilliseconds;
        performanceSampleMaxMilliseconds = Math.Max(performanceSampleMaxMilliseconds, elapsedMilliseconds);
        if (performanceSampleFrames < 120)
            return;

        completedAverageDrawMilliseconds = performanceSampleTotalMilliseconds / performanceSampleFrames;
        completedMaxDrawMilliseconds = performanceSampleMaxMilliseconds;
        performanceSampleFrames = 0;
        performanceSampleTotalMilliseconds = 0;
        performanceSampleMaxMilliseconds = 0;
    }

    private void DrawLibraryToolbar()
    {
        ImGui.SetNextItemWidth(520f);
        if (ImGui.InputText("Search##GlamSpectorLibrarySearch", ref search, 256))
            Refresh();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Search Library title, character/world, Free Company, item, slot, dye, Facewear, tags, notes, or imported source title/creator. Filtering updates as you type.");

        ImGui.SameLine();
        if (ImGui.Button("Refresh"))
            Refresh();

        ImGui.SameLine();
        if (ImGui.SmallButton("⚙##GlamSpectorLibrarySettings"))
            openSettings();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open GlamSpector settings.");

        if (ImGui.Button("Import…##GlamSpectorLibraryImport"))
            ImGui.OpenPopup("##GlamSpectorLibraryImportPopup");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Import older captures, shared packages, Glam Codes, or one Eorzea Collection glamour.");

        ImGui.SameLine();
        if (ImGui.Button($"Wanted ({wantedItems.Count})"))
            showWantedItems = !showWantedItems;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open your personal wanted-item list. Wanted status stays local and is never included in shared GlamSpector exports.");

        ImGui.SameLine();
        var activeFilterCount = ActiveFilterCount();
        if (ImGui.Button(activeFilterCount > 0 ? $"Filters ({activeFilterCount})" : "Filters"))
        {
            showFilters = !showFilters;
            configuration.LibraryFiltersExpanded = showFilters;
            SaveUiState();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Filter the Library by rating, ownership progress, wanted items, or Adventurer Plate availability.");

        ImGui.SameLine();
        ImGui.TextUnformatted("Sort");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(150f);
        if (ImGui.BeginCombo("##GlamSpectorLibrarySort", SortLabel(sort)))
        {
            foreach (var option in Enum.GetValues<LibrarySort>())
            {
                var isSelected = option == sort;
                if (ImGui.Selectable(SortLabel(option), isSelected))
                {
                    sort = option;
                    configuration.LibrarySortMode = (int)sort;
                    SaveUiState();
                    Refresh();
                }

                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        if (ImGui.Button("Library tools…##GlamSpectorLibraryTools"))
            ImGui.OpenPopup("##GlamSpectorLibraryToolsPopup");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Less-frequent Library maintenance actions.");

        DrawLibraryImportPopup();
        DrawLibraryToolsPopup();
    }

    private void DrawLibraryImportPopup()
    {
        if (!ImGui.BeginPopup("##GlamSpectorLibraryImportPopup"))
            return;

        if (ImGui.Selectable("Existing captures", false))
            ImportExisting();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scan the configured output folder for older GlamSpector PNGs and matching metadata.");

        if (ImGui.Selectable(".glamspector.zip packages", false))
            ImportPackages();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Import shared *.glamspector.zip packages placed directly in the configured output folder.");

        if (ImGui.Selectable("Glam Code…", false))
            showGlamCodeImport = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Paste a compact GS1 Glam Code containing visible gear, dyes and Facewear.");

        if (ImGui.Selectable("Eorzea Collection…", false))
            showEorzeaCollectionImport = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Import one Eorzea Collection glamour from page source you copied in your browser. GlamSpector makes no EC network requests.");

        ImGui.EndPopup();
    }

    private void DrawLibraryToolsPopup()
    {
        if (!ImGui.BeginPopup("##GlamSpectorLibraryToolsPopup"))
            return;

        if (ImGui.Selectable("Find duplicate captures…", false))
            PrepareDuplicateCleanup();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Find older captures of the same character/world with exactly the same gear, glamour IDs, dyes and Facewear. The newest capture is kept.");

        ImGui.EndPopup();
    }

    private int ActiveFilterCount()
    {
        var count = 0;
        if (ratingFilter != RatingFilter.Any) count++;
        if (ownershipFilter != OwnershipFilter.Any) count++;
        if (wantedFilter != WantedFilter.Any) count++;
        if (plateFilter != PlateFilter.Any) count++;
        return count;
    }

    private void DrawFilterBar()
    {
        ImGui.Separator();
        ImGui.TextDisabled("Library filters");
        ImGui.SameLine();

        ImGui.SetNextItemWidth(125f);
        if (ImGui.BeginCombo("##ratingFilter", RatingFilterLabel(ratingFilter)))
        {
            foreach (var option in Enum.GetValues<RatingFilter>())
            {
                if (ImGui.Selectable(RatingFilterLabel(option), option == ratingFilter))
                {
                    ratingFilter = option;
                    PersistFilters();
                    ApplyFilters();
                }
                if (option == ratingFilter)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(155f);
        if (ImGui.BeginCombo("##ownershipFilter", OwnershipFilterLabel(ownershipFilter)))
        {
            foreach (var option in Enum.GetValues<OwnershipFilter>())
            {
                if (ImGui.Selectable(OwnershipFilterLabel(option), option == ownershipFilter))
                {
                    ownershipFilter = option;
                    PersistFilters();
                    ApplyFilters();
                }
                if (option == ownershipFilter)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(125f);
        if (ImGui.BeginCombo("##wantedFilter", WantedFilterLabel(wantedFilter)))
        {
            foreach (var option in Enum.GetValues<WantedFilter>())
            {
                if (ImGui.Selectable(WantedFilterLabel(option), option == wantedFilter))
                {
                    wantedFilter = option;
                    PersistFilters();
                    ApplyFilters();
                }
                if (option == wantedFilter)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(125f);
        if (ImGui.BeginCombo("##plateFilter", PlateFilterLabel(plateFilter)))
        {
            foreach (var option in Enum.GetValues<PlateFilter>())
            {
                if (ImGui.Selectable(PlateFilterLabel(option), option == plateFilter))
                {
                    plateFilter = option;
                    PersistFilters();
                    ApplyFilters();
                }
                if (option == plateFilter)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(ActiveFilterCount() == 0);
        if (ImGui.SmallButton("Reset filters"))
        {
            ratingFilter = RatingFilter.Any;
            ownershipFilter = OwnershipFilter.Any;
            wantedFilter = WantedFilter.Any;
            plateFilter = PlateFilter.Any;
            PersistFilters();
            ApplyFilters();
        }
        ImGui.EndDisabled();
        ImGui.Separator();
    }

    private void ApplyFilters()
    {
        IEnumerable<LibraryEntry> query = allEntries;

        query = ratingFilter switch
        {
            RatingFilter.Unrated => query.Where(entry => entry.Rating == 0),
            RatingFilter.OnePlus => query.Where(entry => entry.Rating >= 1),
            RatingFilter.TwoPlus => query.Where(entry => entry.Rating >= 2),
            RatingFilter.ThreePlus => query.Where(entry => entry.Rating >= 3),
            RatingFilter.FourPlus => query.Where(entry => entry.Rating >= 4),
            RatingFilter.FiveOnly => query.Where(entry => entry.Rating >= 5),
            _ => query,
        };

        query = ownershipFilter switch
        {
            OwnershipFilter.FullyOwned => query.Where(entry =>
                ownershipProgressByEntry.TryGetValue(entry.Id, out var progress) && progress.IsComplete),
            OwnershipFilter.HasUnverified => query.Where(entry =>
                ownershipProgressByEntry.TryGetValue(entry.Id, out var progress) && progress.Total > 0 && progress.Unverified > 0),
            _ => query,
        };

        query = wantedFilter switch
        {
            WantedFilter.HasWanted => query.Where(entry =>
                wantedCountByEntry.TryGetValue(entry.Id, out var count) && count > 0),
            WantedFilter.NoWanted => query.Where(entry =>
                !wantedCountByEntry.TryGetValue(entry.Id, out var count) || count == 0),
            _ => query,
        };

        query = plateFilter switch
        {
            PlateFilter.HasPlate => query.Where(entry =>
                GetEntryPresentation(entry).HasAdventurerPlate),
            PlateFilter.NoPlate => query.Where(entry =>
                !GetEntryPresentation(entry).HasAdventurerPlate),
            _ => query,
        };

        // Category filters and transient search both affect only the left list.
        // The selected entry remains available in the details pane until an
        // underlying store lookup confirms that the row itself was deleted.
        entries = query.ToList();
    }

    private static string RatingFilterLabel(RatingFilter filter) => filter switch
    {
        RatingFilter.Unrated => "Rating: Unrated",
        RatingFilter.OnePlus => "Rating: 1★+",
        RatingFilter.TwoPlus => "Rating: 2★+",
        RatingFilter.ThreePlus => "Rating: 3★+",
        RatingFilter.FourPlus => "Rating: 4★+",
        RatingFilter.FiveOnly => "Rating: 5★",
        _ => "Rating: Any",
    };

    private static string OwnershipFilterLabel(OwnershipFilter filter) => filter switch
    {
        OwnershipFilter.FullyOwned => "Ownership: Complete",
        OwnershipFilter.HasUnverified => "Ownership: Unverified",
        _ => "Ownership: Any",
    };

    private static string WantedFilterLabel(WantedFilter filter) => filter switch
    {
        WantedFilter.HasWanted => "Wanted: Yes",
        WantedFilter.NoWanted => "Wanted: No",
        _ => "Wanted: Any",
    };

    private static string PlateFilterLabel(PlateFilter filter) => filter switch
    {
        PlateFilter.HasPlate => "Plate: Yes",
        PlateFilter.NoPlate => "Plate: No",
        _ => "Plate: Any",
    };

    private void DrawEntryList()
    {
        if (entries.Count == 0)
        {
            ImGui.TextWrapped(string.IsNullOrWhiteSpace(search)
                ? "No Library entries yet. Your next successful capture will be added automatically, or use Import… to add an existing capture, Glam Code, or Eorzea Collection recipe."
                : "No library entries match this search.");
            return;
        }

        const float rowHeight = 88f;
        const float separatorHeight = 1f;
        const int overscanRows = 2;
        var thumbnailSize = new Vector2(118f, 88f);
        var style = ImGui.GetStyle();

        // Each fixed-height row advances once after the 88 px Selectable and
        // once after its 1 px separator. Deriving the stride from live style
        // spacing keeps scrolling aligned if the user's ImGui scale changes.
        var rowStride = rowHeight + (2f * style.ItemSpacing.Y) + separatorHeight;
        var viewportHeight = Math.Max(1f, ImGui.GetWindowSize().Y - (2f * style.WindowPadding.Y));
        var totalContentHeight = entries.Count * rowStride;
        var maximumScrollY = Math.Max(0f, totalContentHeight - viewportHeight);
        var currentScrollY = ImGui.GetScrollY();
        var effectiveScrollY = Math.Clamp(currentScrollY, 0f, maximumScrollY);
        if (Math.Abs(currentScrollY - effectiveScrollY) > 0.5f)
            ImGui.SetScrollY(effectiveScrollY);

        var firstVisible = Math.Clamp((int)MathF.Floor(effectiveScrollY / rowStride), 0, entries.Count);
        var lastVisibleExclusive = Math.Clamp(
            (int)MathF.Ceiling((effectiveScrollY + viewportHeight) / rowStride),
            firstVisible,
            entries.Count);
        var firstRendered = Math.Max(0, firstVisible - overscanRows);
        var lastRenderedExclusive = Math.Min(entries.Count, lastVisibleExclusive + overscanRows);

        lastFrameFirstRenderedIndex = firstRendered;
        lastFrameLastRenderedIndex = lastRenderedExclusive - 1;

        DrawVirtualRowSpacer(firstRendered, rowStride, style.ItemSpacing.Y);
        for (var index = firstRendered; index < lastRenderedExclusive; index++)
            DrawEntryRow(entries[index], thumbnailSize);
        DrawVirtualRowSpacer(entries.Count - lastRenderedExclusive, rowStride, style.ItemSpacing.Y);
    }

    private static void DrawVirtualRowSpacer(int rowCount, float rowStride, float itemSpacingY)
    {
        if (rowCount <= 0)
            return;

        // Dummy adds one ItemSpacing.Y advance of its own. Subtract that once
        // so the skipped range occupies exactly rowCount * rowStride pixels.
        var spacerHeight = Math.Max(0f, (rowCount * rowStride) - itemSpacingY);
        ImGui.Dummy(new Vector2(1f, spacerHeight));
    }

    private void DrawEntryRow(LibraryEntry entry, Vector2 thumbnailSize)
    {
        lastFrameRowsDrawn++;
        var presentation = GetEntryPresentation(entry);
        ImGui.PushID(unchecked((int)entry.Id));
        DrawThumbnail(entry, presentation, thumbnailSize);
        ImGui.SameLine();

        var selectedNow = selected?.Id == entry.Id;
        var progress = ownershipProgressByEntry.TryGetValue(entry.Id, out var cachedProgress)
            ? cachedProgress
            : new OwnershipProgress(0, entry.Pieces.Count + (entry.FacewearId != 0 ? 1 : 0));
        var wantedCount = wantedCountByEntry.TryGetValue(entry.Id, out var cachedWantedCount) ? cachedWantedCount : 0;
        var sizeSuffix = sort == LibrarySort.FileSize ? $" · {FormatBytes(entry.TotalMediaBytes)}" : string.Empty;
        var progressText = progress.Total > 0
            ? $"{progress.Owned}/{progress.Total} verified owned{(wantedCount > 0 ? $" · {wantedCount} wanted" : string.Empty)}{sizeSuffix}"
            : $"No structured gear{sizeSuffix}";
        var label = $"{entry.DisplayTitle}\n{presentation.RatingText}{presentation.LocalCapturedAt}\n{progressText}##entry";
        if (ImGui.Selectable(label, selectedNow, ImGuiSelectableFlags.None, new Vector2(0, thumbnailSize.Y)))
            Select(entry.Id);

        ImGui.Separator();
        ImGui.PopID();
    }

    private void DrawThumbnail(LibraryEntry entry, LibraryEntryPresentation presentation, Vector2 boxSize)
    {
        var imagePath = presentation.PrimaryImagePath;
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            DrawMissingThumbnail(entry, boxSize);
            return;
        }

        lastFrameThumbnailRequests++;
        Dalamud.Bindings.ImGui.ImTextureID wrapHandle;
        int wrapWidth;
        int wrapHeight;
        try
        {
            var wrap = textureProvider.GetFromFileAbsolute(imagePath).GetWrapOrEmpty();
            wrapHandle = wrap.Handle;
            wrapWidth = wrap.Width;
            wrapHeight = wrap.Height;
        }
        catch (Exception)
        {
            presentation.PrimaryImagePath = null;
            DrawMissingThumbnail(entry, boxSize);
            return;
        }

        if (wrapWidth <= 0 || wrapHeight <= 0)
        {
            // Do not stat healthy cached paths each frame. Only probe after a
            // failed texture lookup so a file removed behind GlamSpector's back
            // degrades to the normal placeholder until the next Refresh.
            if (!File.Exists(imagePath))
            {
                presentation.PrimaryImagePath = null;
                DrawMissingThumbnail(entry, boxSize);
                return;
            }

            ImGui.Dummy(boxSize);
            return;
        }

        var imageAspect = (float)wrapWidth / wrapHeight;
        var boxAspect = boxSize.X / boxSize.Y;
        var drawSize = imageAspect > boxAspect
            ? new Vector2(boxSize.X, boxSize.X / imageAspect)
            : new Vector2(boxSize.Y * imageAspect, boxSize.Y);

        var offsetY = Math.Max(0f, (boxSize.Y - drawSize.Y) * 0.5f);
        if (offsetY > 0)
        {
            var cursor = ImGui.GetCursorPos();
            ImGui.SetCursorPosY(cursor.Y + offsetY);
            ImGui.Image(wrapHandle, drawSize);
            ImGui.SetCursorPosY(cursor.Y + boxSize.Y);
        }
        else
        {
            ImGui.Image(wrapHandle, drawSize);
        }
    }

    private static void DrawMissingThumbnail(LibraryEntry entry, Vector2 boxSize)
    {
        var start = ImGui.GetCursorPos();
        ImGui.Dummy(boxSize);
        ImGui.SetCursorPos(start + new Vector2(14f, Math.Max(8f, boxSize.Y * 0.35f)));
        if (LibraryStore.IsGlamCodePath(entry.CardPath))
            ImGui.TextDisabled("GLAM CODE");
        else if (LibraryStore.IsEorzeaCollectionMarkerPath(entry.CardPath))
            ImGui.TextDisabled("EC GLAM");
        else
            ImGui.TextDisabled("NO IMAGE");
        ImGui.SetCursorPos(new Vector2(start.X, start.Y + boxSize.Y));
    }

    private void DrawDetails()
    {
        if (selected is not { } entry)
        {
            ImGui.TextWrapped("Select a Library entry on the left to view it here.");
            return;
        }

        DrawEditableTitle(entry);

        var isEorzeaCollectionEntry = string.Equals(entry.SourceKind, "EorzeaCollection", StringComparison.OrdinalIgnoreCase);
        if (isEorzeaCollectionEntry)
        {
            var sourceTitle = string.IsNullOrWhiteSpace(entry.SourceTitle) ? "Eorzea Collection glamour" : entry.SourceTitle;
            var creatorText = string.IsNullOrWhiteSpace(entry.SourceCreator) ? string.Empty : $" · by {entry.SourceCreator}";
            ImGui.TextWrapped($"Source: {sourceTitle}{creatorText} · Eorzea Collection");
        }
        else
        {
            ImGui.TextDisabled($"Character: {entry.CharacterName} @ {entry.HomeWorld}");
        }

        if (!string.IsNullOrWhiteSpace(entry.FreeCompanyName))
            ImGui.TextDisabled($"FC: {entry.FreeCompanyName}");

        if (entry.CapturedAtUtc != DateTime.MinValue)
            ImGui.TextDisabled($"Captured {entry.CapturedAtUtc.ToLocalTime():f}");

        var previewCountText = entry.PersonalPreviews.Count > 0
            ? $" · {entry.PersonalPreviews.Count} personal preview{(entry.PersonalPreviews.Count == 1 ? string.Empty : "s")}"
            : string.Empty;
        var shareCardCountText = entry.GeneratedShareCards.Count > 0
            ? $" · {entry.GeneratedShareCards.Count} share card{(entry.GeneratedShareCards.Count == 1 ? string.Empty : "s")}"
            : string.Empty;
        ImGui.TextDisabled($"Media: {FormatBytes(entry.TotalMediaBytes)}{previewCountText}{shareCardCountText}");

        if (entry.PortraitSettings is not null)
            ImGui.TextDisabled("Portrait settings saved (read-only alpha)");

        DrawRating(entry);
        DrawOwnershipProgress(entry);
        DrawTagsAndNotes(entry);

        ImGui.Separator();
        DrawGlamourActions(entry);

        ImGui.Separator();
        DrawMediaArea(entry);

        ImGui.Separator();
        DrawEntryFileTools(entry);

        ImGui.Separator();
        if (entry.PortraitSettings is { } portrait)
        {
            ImGui.TextUnformatted("Portrait recipe");
            ImGui.SameLine();
            ImGui.TextDisabled($"Zoom {portrait.CameraZoom} · Rotation {portrait.ImageRotation}° · Background #{portrait.Background}");
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("GlamSpector saved the target's read-only portrait camera/lighting/export data. Applying it to your own portrait is intentionally not enabled yet.");
            ImGui.Spacing();
        }

        if (!string.IsNullOrWhiteSpace(entry.FacewearName))
        {
            ImGui.TextUnformatted("Facewear");
            ImGui.SameLine();
            ImGui.TextDisabled(entry.FacewearName);
            if (entry.FacewearId != 0)
            {
                ImGui.SameLine();
                var facewearOwnership = ownershipService.GetFacewear(entry.FacewearId);
                if (facewearOwnership.Owned)
                    ImGui.TextUnformatted(facewearOwnership.Summary);
                else
                    ImGui.TextDisabled(facewearOwnership.Summary);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(facewearOwnership.Tooltip);
            }
            ImGui.Spacing();
        }

        DrawPiecesTable(entry);

        ImGui.Spacing();
        var entryRemoved = DrawLibraryEntryManagement(entry);
        applySecondarySectionState = false;
        if (entryRemoved)
            return;
    }

    private void DrawEditableTitle(LibraryEntry entry)
    {
        if (editTitleEntryId == entry.Id)
        {
            ImGui.SetNextItemWidth(Math.Max(220f, Math.Min(520f, ImGui.GetContentRegionAvail().X - 150f)));
            ImGui.InputText($"Library title##title-{entry.Id}", ref titleEdit, 256);

            var valid = !string.IsNullOrWhiteSpace(titleEdit);
            ImGui.BeginDisabled(!valid);
            if (ImGui.SmallButton($"Save##saveTitle-{entry.Id}"))
                SaveDisplayTitle(entry);
            ImGui.EndDisabled();

            ImGui.SameLine();
            if (ImGui.SmallButton($"Cancel##cancelTitle-{entry.Id}"))
                CancelDisplayTitleEdit();

            if (!valid)
                ImGui.TextDisabled("Library title cannot be empty.");
            else if (!string.IsNullOrWhiteSpace(titleEditError))
                ImGui.TextWrapped(titleEditError);
            return;
        }

        ImGui.TextUnformatted(entry.DisplayTitle);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Edit title##editTitle-{entry.Id}"))
        {
            editTitleEntryId = entry.Id;
            titleEdit = entry.DisplayTitle;
            titleEditError = null;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Rename this entry in your local Library. Source attribution, recipe identity, media paths and sharing data are unchanged.");
    }

    private void SaveDisplayTitle(LibraryEntry entry)
    {
        try
        {
            store.SetDisplayTitle(entry.Id, titleEdit);
            editTitleEntryId = null;
            titleEdit = string.Empty;
            titleEditError = null;
            Refresh();
        }
        catch (Exception ex)
        {
            titleEditError = ex.Message;
        }
    }

    private void CancelDisplayTitleEdit()
    {
        editTitleEntryId = null;
        titleEdit = string.Empty;
        titleEditError = null;
    }

    private void DrawGlamourActions(LibraryEntry entry)
    {
        ImGui.TextUnformatted("Glamour actions");

        ImGui.BeginDisabled(entry.Pieces.Count == 0);
        if (ImGui.Button("Try on glam"))
            tryOnGlam(entry);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(entry.Pieces.Count == 0
                ? "This image-only entry has no structured gear recipe to try on."
                : "Open FFXIV's native Try On window and load the saved weapons/gear with their captured dyes. Facewear is not applied yet.");

        ImGui.SameLine();
        if (ImGui.Button("Capture my preview"))
            capturePersonalPreview(entry);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Capture the character viewport exactly as it currently appears in FFXIV's Fitting Room. Try on the glam first, then rotate and zoom it however you like before pressing this. The capture does not re-run Try On.");

        ImGui.BeginDisabled(entry.Pieces.Count == 0);
        if (ImGui.SmallButton("Copy Glam Code"))
            CopyGlamCode(entry);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Copy a compact text-only outfit code with visible items, both dyes and Facewear. Character identity, screenshots, ratings, Wanted state, tags and notes are not included.");

        var canMarkUnverified = entry.Pieces.Any(piece =>
            piece.DisplayItemId != 0 &&
            !wantedItemIds.Contains(piece.DisplayItemId) &&
            !ownershipService.Get(piece.DisplayItemId).Owned);
        ImGui.SameLine();
        ImGui.BeginDisabled(!canMarkUnverified);
        if (ImGui.SmallButton("Mark unverified wanted"))
            MarkUnverifiedWanted(entry);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Add every currently unverified gear item in this glam to your Wanted list. 'Unverified' does not prove you do not own it; unloaded storage may still contain it.");
    }

    private void DrawEntryFileTools(LibraryEntry entry)
    {
        if (!BeginSecondarySection(
                $"Files & sharing##entryFiles-{entry.Id}",
                configuration.LibraryFilesSharingExpanded,
                value => configuration.LibraryFilesSharingExpanded = value))
            return;

        var isGlamCodeEntry = LibraryStore.IsGlamCodePath(entry.CardPath);
        var isImageLessEntry = LibraryStore.IsImageLessPath(entry.CardPath);
        var isEorzeaCollectionEntry = string.Equals(entry.SourceKind, "EorzeaCollection", StringComparison.OrdinalIgnoreCase);

        ImGui.TextDisabled("Original capture/source media and file-level sharing tools");

        ImGui.BeginDisabled(isImageLessEntry);
        if (ImGui.Button("Copy full card"))
            copyCard(entry);
        ImGui.SameLine();
        if (ImGui.Button("Open full card PNG"))
            openCard(entry);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled) && isImageLessEntry)
            ImGui.SetTooltip("This entry has no original locally saved full-card image. Preview and generated share-card controls are available directly in the Media section.");

        ImGui.SameLine();
        if (ImGui.Button("Open entry folder"))
            openFolder(entry);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open the folder that contains this entry's original saved card or recipe marker. Personal previews and generated share cards live beneath the same managed LibraryMedia tree for new entries.");

        ImGui.BeginDisabled(isImageLessEntry || isEorzeaCollectionEntry);
        if (ImGui.Button("Export .zip"))
            ExportSelected(entry);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(isImageLessEntry
                ? "This entry has no locally saved original image to package."
                : isEorzeaCollectionEntry
                    ? "Eorzea Collection imports keep their original source images locally. Full EC source-image export is not enabled yet."
                    : "Create one shareable .glamspector.zip containing the card and all searchable glamour metadata.");

        ImGui.SameLine();
        ImGui.BeginDisabled(isGlamCodeEntry || isEorzeaCollectionEntry);
        if (ImGui.Button(string.IsNullOrWhiteSpace(entry.AdventurerPlatePath) ? "Attach open Plate" : "Replace open Plate"))
            attachAdventurerPlate(entry);
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(isGlamCodeEntry
                ? "Text-only Glam Code imports are not tied to a source character, so an Adventurer Plate cannot be attached automatically."
                : isEorzeaCollectionEntry
                    ? "Eorzea Collection imports are external references rather than an inspected in-game character, so Plate attachment is disabled."
                    : "Attach the currently open Adventurer Plate to this library entry. The plate must belong to the selected character.");

        if (isEorzeaCollectionEntry && !string.IsNullOrWhiteSpace(entry.SourceUrl))
        {
            if (ImGui.Button("Open source page"))
                OpenExternalUrl(entry.SourceUrl);
            ImGui.SameLine();
            if (ImGui.Button("Copy source URL"))
                ImGui.SetClipboardText(entry.SourceUrl);
        }
    }

    private bool DrawLibraryEntryManagement(LibraryEntry entry)
    {
        ImGui.Separator();
        if (!BeginSecondarySection(
                $"Library entry##entryManagement-{entry.Id}",
                configuration.LibraryEntryExpanded,
                value => configuration.LibraryEntryExpanded = value))
            return false;

        ImGui.TextDisabled("Removal and deletion");

        if (confirmRemoveId == entry.Id)
        {
            ImGui.TextWrapped("Remove this entry from the Library index only? Its files will stay on disk and can be imported again later.");
            if (ImGui.Button("Remove entry##removeOnlyConfirm"))
            {
                try
                {
                    store.Delete(entry.Id);
                    ClearSelection(persist: true);
                    confirmRemoveId = null;
                    confirmDeleteId = null;
                    Refresh();
                    if (configuration.NotifyDelete)
                        Plugin.ChatGui.Print($"Removed {entry.CharacterName} @ {entry.HomeWorld} from the GlamSpector Library (files kept).", "GlamSpector");
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##removeOnly"))
                confirmRemoveId = null;
            return false;
        }

        if (confirmDeleteId == entry.Id)
        {
            ImGui.TextWrapped("Permanently delete this Library entry and all of its saved files, including previews and generated share cards?");
            if (ImGui.Button("Delete entry & files##deleteFull"))
            {
                try
                {
                    store.DeleteWithFiles(entry);
                    ClearSelection(persist: true);
                    confirmRemoveId = null;
                    confirmDeleteId = null;
                    Refresh();
                    if (configuration.NotifyDelete)
                        Plugin.ChatGui.Print($"Deleted {entry.CharacterName} @ {entry.HomeWorld} from the GlamSpector Library and disk.", "GlamSpector");
                    return true;
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##deleteFull"))
                confirmDeleteId = null;
            return false;
        }

        if (ImGui.Button("Remove from library"))
        {
            confirmRemoveId = entry.Id;
            confirmDeleteId = null;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Remove only the Library index entry. Saved files remain on disk and can be imported again later.");

        ImGui.SameLine();
        if (ImGui.Button("Delete entry & files…"))
        {
            confirmDeleteId = entry.Id;
            confirmRemoveId = null;
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Permanently delete the saved full card, Inspect preview, diagnostic JSON, Adventurer Plate, source images, personal previews and generated share cards if present, then remove the Library entry.");

        return false;
    }

    private void DrawTagsAndNotes(LibraryEntry entry)
    {
        var tagCountText = entry.Tags.Count == 0 ? string.Empty : $" ({entry.Tags.Count} tag{(entry.Tags.Count == 1 ? string.Empty : "s")})";
        var noteText = string.IsNullOrWhiteSpace(entry.Notes) ? string.Empty : " · note";
        if (!BeginSecondarySection(
                $"Tags & notes{tagCountText}{noteText}##metadata-{entry.Id}",
                configuration.LibraryTagsNotesExpanded,
                value => configuration.LibraryTagsNotesExpanded = value))
            return;

        ImGui.TextUnformatted("Tags");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(Math.Max(240f, Math.Min(520f, ImGui.GetContentRegionAvail().X - 100f)));
        if (ImGui.InputText("##GlamSpectorTags", ref tagsEdit, 512))
            tagsEditDirty = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Comma-separated personal tags, for example: gothic, healer, summer. Tags are searchable and are not included in shared exports.");
        ImGui.SameLine();
        ImGui.BeginDisabled(!tagsEditDirty);
        if (ImGui.SmallButton("Save tags"))
            SaveTags(entry);
        ImGui.EndDisabled();

        ImGui.TextUnformatted("Notes");
        ImGui.SetNextItemWidth(-1f);
        if (ImGui.InputTextMultiline("##GlamSpectorNotes", ref notesEdit, 4000, new Vector2(0f, 68f)))
            notesEditDirty = true;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("A private note for this glam. Notes are included in Library search but are not included in shared exports.");
        ImGui.BeginDisabled(!notesEditDirty);
        if (ImGui.SmallButton("Save note"))
            SaveNotes(entry);
        ImGui.EndDisabled();

        if (tagsEditDirty || notesEditDirty)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("Unsaved changes");
        }
    }

    private bool BeginSecondarySection(string label, bool configuredOpen, Action<bool> update)
    {
        if (applySecondarySectionState)
            ImGui.SetNextItemOpen(configuredOpen, ImGuiCond.Always);

        var open = ImGui.CollapsingHeader(label);
        if (open != configuredOpen)
        {
            update(open);
            SaveUiState();
        }
        return open;
    }

    private void SaveTags(LibraryEntry entry)
    {
        try
        {
            var tags = tagsEdit
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            store.SetTags(entry.Id, tags);
            tagsEditDirty = false;
            Refresh();
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }
    }

    private void SaveNotes(LibraryEntry entry)
    {
        try
        {
            store.SetNotes(entry.Id, notesEdit);
            notesEditDirty = false;
            Refresh();
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }
    }

    private void LoadMetadataEditors(LibraryEntry entry, bool force = false)
    {
        if (force || !tagsEditDirty)
            tagsEdit = string.Join(", ", entry.Tags);
        if (force || !notesEditDirty)
            notesEdit = entry.Notes ?? string.Empty;
        if (force)
        {
            tagsEditDirty = false;
            notesEditDirty = false;
        }
    }

    private void DrawRating(LibraryEntry entry)
    {
        ImGui.TextUnformatted("Rating");
        ImGui.SameLine();

        for (var star = 1; star <= 5; star++)
        {
            if (star > 1)
                ImGui.SameLine(0f, 2f);

            var filled = star <= entry.Rating;
            if (ImGui.SmallButton($"{(filled ? "★" : "☆")}##rating{star}"))
            {
                try
                {
                    // Clicking the currently selected highest star again clears
                    // the rating; otherwise set it to 1-5.
                    var newRating = entry.Rating == star ? 0 : star;
                    store.SetRating(entry.Id, newRating);
                    Refresh();
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
                return;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Rate this glamour {star} star{(star == 1 ? string.Empty : "s")}. Click the current rating again to clear it.");
        }

        ImGui.SameLine();
        ImGui.TextDisabled(entry.Rating == 0 ? "Unrated" : $"{entry.Rating}/5");
    }

    private void DrawMediaArea(LibraryEntry entry)
    {
        var presentation = GetEntryPresentation(entry);
        var hasCapturedPreview = presentation.HasCapturedPreview;
        var hasCardImage = presentation.HasCardImage;
        var hasPlate = presentation.HasAdventurerPlate;
        var hasSourceImages = presentation.SourceImagePaths.Count > 0;
        var hasPersonalPreviews = presentation.PersonalPreviews.Count > 0;
        var hasGeneratedShareCards = presentation.GeneratedShareCards.Count > 0;

        ImGui.TextUnformatted("Media");
        ImGui.SameLine();
        ImGui.TextDisabled(FormatBytes(entry.TotalMediaBytes));
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Total size of the files GlamSpector currently associates with this Library entry. Duplicate paths are counted only once.");

        if (ImGui.SmallButton("Primary##mediaMode"))
            mediaViewMode = MediaViewMode.Primary;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show the Library-first image. Personal Fitting Room previews take priority; otherwise GlamSpector uses the saved Inspect preview before any full card/source image.");

        if (hasPersonalPreviews)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"My Previews ({entry.PersonalPreviews.Count})##mediaMode"))
                mediaViewMode = MediaViewMode.PersonalPreviews;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show your saved Fitting Room shots as a gallery of up to three images per row.");
        }

        if (hasGeneratedShareCards)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Share Cards ({entry.GeneratedShareCards.Count})##mediaMode"))
                mediaViewMode = MediaViewMode.GeneratedShareCards;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Shareable GlamSpector cards generated from your personal previews plus this entry's item/dye recipe.");
        }

        if (hasCapturedPreview)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Inspect Preview##mediaMode"))
                mediaViewMode = MediaViewMode.CapturedPreview;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The character-only preview captured from the original Inspect window. New Library captures always keep this image.");
        }

        if (hasCardImage)
        {
            ImGui.SameLine();
            var label = string.Equals(entry.SourceKind, "EorzeaCollection", StringComparison.OrdinalIgnoreCase)
                ? "Source Cover##mediaMode"
                : "Full Card##mediaMode";
            if (ImGui.SmallButton(label))
                mediaViewMode = MediaViewMode.GlamCard;
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Show the full saved card. Cards are secondary/share media in the preview-first Library.");
        }

        if (hasSourceImages)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton($"Source Images ({entry.SourceImagePaths.Count})##mediaMode"))
                mediaViewMode = MediaViewMode.SourceImages;
        }

        if (hasPlate)
        {
            ImGui.SameLine();
            if (ImGui.SmallButton("Adventurer Plate##mediaMode"))
                mediaViewMode = MediaViewMode.AdventurerPlate;
        }

        // If files disappear while the Library is open, never strand the user on
        // a media tab that can no longer draw anything.
        if (mediaViewMode == MediaViewMode.CapturedPreview && !hasCapturedPreview)
            mediaViewMode = MediaViewMode.Primary;
        if (mediaViewMode == MediaViewMode.GlamCard && !hasCardImage)
            mediaViewMode = MediaViewMode.Primary;
        if (mediaViewMode == MediaViewMode.PersonalPreviews && !hasPersonalPreviews)
            mediaViewMode = MediaViewMode.Primary;
        if (mediaViewMode == MediaViewMode.GeneratedShareCards && !hasGeneratedShareCards)
            mediaViewMode = MediaViewMode.Primary;
        if (mediaViewMode == MediaViewMode.SourceImages && !hasSourceImages)
            mediaViewMode = MediaViewMode.Primary;
        if (mediaViewMode == MediaViewMode.AdventurerPlate && !hasPlate)
            mediaViewMode = MediaViewMode.Primary;

        ImGui.Spacing();
        switch (mediaViewMode)
        {
            case MediaViewMode.CapturedPreview:
                DrawImagePreview(entry.RawPreviewPath!, 560f);
                break;
            case MediaViewMode.GlamCard:
                DrawImagePreview(entry.CardPath, 520f);
                break;
            case MediaViewMode.PersonalPreviews:
                DrawPersonalPreviews(entry, presentation.PersonalPreviews);
                break;
            case MediaViewMode.GeneratedShareCards:
                DrawGeneratedShareCards(entry, presentation.GeneratedShareCards);
                break;
            case MediaViewMode.SourceImages:
                DrawSourceImages(presentation.SourceImagePaths);
                break;
            case MediaViewMode.AdventurerPlate:
                DrawAdventurerPlate(entry);
                break;
            default:
                DrawPrimaryImage(entry);
                break;
        }
    }

    private void DrawPersonalPreviews(LibraryEntry entry, IReadOnlyList<PersonalPreview> previews)
    {
        if (previews.Count == 0)
        {
            ImGui.TextDisabled("No personal Fitting Room previews are saved for this entry yet.");
            return;
        }

        ImGui.TextDisabled("Newest first · up to 3 previews per row · a fresh capture becomes Primary automatically");
        var columns = Math.Clamp(previews.Count, 1, 3);
        if (!ImGui.BeginTable($"##personalPreviewGallery-{entry.Id}", columns, ImGuiTableFlags.SizingStretchProp))
            return;

        foreach (var preview in previews)
        {
            ImGui.TableNextColumn();
            ImGui.PushID(unchecked((int)preview.Id));

            var tileWidth = Math.Max(160f, ImGui.GetContentRegionAvail().X - 6f);
            DrawImagePreviewConstrained(preview.Path, tileWidth, 570f);

            if (preview.IsPrimary)
                ImGui.TextUnformatted("★ Primary");
            else
                ImGui.TextDisabled(preview.CreatedAtUtc == DateTime.MinValue
                    ? "Personal preview"
                    : preview.CreatedAtUtc.ToLocalTime().ToString("g"));

            if (!preview.IsPrimary)
            {
                if (ImGui.SmallButton("Set primary"))
                {
                    try
                    {
                        store.SetPersonalPreviewPrimary(entry.Id, preview.Id);
                        confirmDeletePersonalPreviewId = null;
                        Refresh();
                        Select(entry.Id);
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                    }
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Use this shot as the Library thumbnail and Primary image.");
                ImGui.SameLine();
            }

            ImGui.BeginDisabled(entry.Pieces.Count == 0 && entry.FacewearId == 0);
            if (ImGui.SmallButton("Create share card"))
                generateShareCard(entry, preview);
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(entry.Pieces.Count == 0 && entry.FacewearId == 0
                    ? "This entry has no structured item recipe to place on a generated share card."
                    : "Generate a shareable GlamSpector card from this preview plus the saved items, dyes and Facewear. The preview itself is kept unchanged.");

            if (ImGui.SmallButton("Open PNG"))
                OpenLocalFile(preview.Path);
            ImGui.SameLine();
            if (ImGui.SmallButton("Folder"))
                OpenLocalFolder(preview.Path);
            ImGui.SameLine();

            if (confirmDeletePersonalPreviewId == preview.Id)
            {
                ImGui.TextUnformatted("Delete?");
                ImGui.SameLine();
                if (ImGui.SmallButton("Yes"))
                {
                    try
                    {
                        store.DeletePersonalPreview(entry.Id, preview.Id);
                        confirmDeletePersonalPreviewId = null;
                        Refresh();
                        Select(entry.Id);
                        if (selected?.PersonalPreviews.Count == 0)
                            mediaViewMode = MediaViewMode.Primary;
                        if (configuration.NotifyDelete)
                            Plugin.ChatGui.Print("Deleted personal GlamSpector preview from disk.", "GlamSpector");
                        ImGui.PopID();
                        ImGui.EndTable();
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                    }
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("No"))
                    confirmDeletePersonalPreviewId = null;
            }
            else if (ImGui.SmallButton("Delete…"))
            {
                confirmDeletePersonalPreviewId = preview.Id;
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawGeneratedShareCards(LibraryEntry entry, IReadOnlyList<GeneratedShareCard> cards)
    {
        if (cards.Count == 0)
        {
            ImGui.TextDisabled("No generated share cards are saved for this entry yet.");
            return;
        }

        ImGui.TextDisabled("Generated from personal previews + the saved item/dye recipe");
        var columns = Math.Clamp(cards.Count, 1, 2);
        if (!ImGui.BeginTable($"##shareCardGallery-{entry.Id}", columns, ImGuiTableFlags.SizingStretchProp))
            return;

        foreach (var card in cards)
        {
            ImGui.TableNextColumn();
            ImGui.PushID(unchecked((int)card.Id));

            var tileWidth = Math.Max(220f, ImGui.GetContentRegionAvail().X - 6f);
            DrawImagePreviewConstrained(card.Path, tileWidth, 440f);
            ImGui.TextDisabled(card.CreatedAtUtc == DateTime.MinValue
                ? "Generated share card"
                : card.CreatedAtUtc.ToLocalTime().ToString("g"));

            if (ImGui.SmallButton("Copy"))
                copyImageToClipboard(card.Path);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Copy this generated card as an image, ready to paste into Discord or another app.");
            ImGui.SameLine();
            if (ImGui.SmallButton("Open PNG"))
                OpenLocalFile(card.Path);
            ImGui.SameLine();
            if (ImGui.SmallButton("Folder"))
                OpenLocalFolder(card.Path);
            ImGui.SameLine();

            if (confirmDeleteShareCardId == card.Id)
            {
                ImGui.TextUnformatted("Delete?");
                ImGui.SameLine();
                if (ImGui.SmallButton("Yes"))
                {
                    try
                    {
                        store.DeleteGeneratedShareCard(entry.Id, card.Id);
                        confirmDeleteShareCardId = null;
                        Refresh();
                        Select(entry.Id);
                        if (selected?.GeneratedShareCards.Count == 0)
                            mediaViewMode = MediaViewMode.Primary;
                        if (configuration.NotifyDelete)
                            Plugin.ChatGui.Print("Deleted generated GlamSpector share card from disk.", "GlamSpector");
                        ImGui.PopID();
                        ImGui.EndTable();
                        return;
                    }
                    catch (Exception ex)
                    {
                        lastError = ex.Message;
                    }
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("No"))
                    confirmDeleteShareCardId = null;
            }
            else if (ImGui.SmallButton("Delete…"))
            {
                confirmDeleteShareCardId = card.Id;
            }

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    private void DrawAdventurerPlate(LibraryEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.AdventurerPlatePath) || !File.Exists(entry.AdventurerPlatePath))
        {
            ImGui.TextDisabled("No Adventurer Plate image is attached to this capture.");
            return;
        }

        DrawImagePreview(entry.AdventurerPlatePath, 520f);
    }

    private void DrawImagePreview(string path, float maxHeight)
    {
        var wrap = textureProvider.GetFromFileAbsolute(path).GetWrapOrEmpty();
        if (wrap.Width <= 0 || wrap.Height <= 0)
        {
            ImGui.TextDisabled("Loading image preview…");
            return;
        }

        var available = ImGui.GetContentRegionAvail();
        var maxWidth = Math.Max(100f, available.X);
        var scale = Math.Min(maxWidth / wrap.Width, maxHeight / wrap.Height);
        scale = Math.Min(scale, 1f);
        ImGui.Image(wrap.Handle, new Vector2(wrap.Width * scale, wrap.Height * scale));
    }

    private void DrawImagePreviewConstrained(string path, float maxWidth, float maxHeight)
    {
        var wrap = textureProvider.GetFromFileAbsolute(path).GetWrapOrEmpty();
        if (wrap.Width <= 0 || wrap.Height <= 0)
        {
            ImGui.TextDisabled("Loading image preview…");
            return;
        }

        maxWidth = Math.Max(80f, maxWidth);
        maxHeight = Math.Max(80f, maxHeight);
        var scale = Math.Min(maxWidth / wrap.Width, maxHeight / wrap.Height);
        ImGui.Image(wrap.Handle, new Vector2(wrap.Width * scale, wrap.Height * scale));
    }

    private void DrawPrimaryImage(LibraryEntry entry)
    {
        var primaryPath = GetEntryPresentation(entry).PrimaryImagePath;
        if (!string.IsNullOrWhiteSpace(primaryPath))
        {
            var personalPreview = entry.PersonalPreviews.FirstOrDefault(preview =>
                string.Equals(preview.Path, primaryPath, StringComparison.OrdinalIgnoreCase));
            if (personalPreview is not null)
                ImGui.TextDisabled("Primary image: personal Fitting Room preview");
            else if (!string.IsNullOrWhiteSpace(entry.RawPreviewPath) &&
                     string.Equals(entry.RawPreviewPath, primaryPath, StringComparison.OrdinalIgnoreCase))
                ImGui.TextDisabled("Primary image: captured Inspect preview");
            DrawImagePreview(primaryPath, 560f);
            return;
        }

        if (LibraryStore.IsGlamCodePath(entry.CardPath))
        {
            ImGui.TextDisabled("Text-only Glam Code");
            ImGui.TextWrapped("This shared outfit has no image yet. Use Try on glam, adjust the Fitting Room camera, then press Capture my preview to add your own persistent image.");
        }
        else if (LibraryStore.IsEorzeaCollectionMarkerPath(entry.CardPath))
        {
            ImGui.TextDisabled("Eorzea Collection import without a local source image");
            ImGui.TextWrapped("The equipment recipe is available. Use Try on glam, compose the Fitting Room shot on your character, then press Capture my preview.");
        }
        else
        {
            ImGui.TextWrapped($"No usable primary image is currently available.\n{entry.CardPath}");
        }
    }

    private LibraryEntryPresentation GetEntryPresentation(LibraryEntry entry)
    {
        if (presentationByEntry.TryGetValue(entry.Id, out var presentation))
            return presentation;

        presentation = BuildEntryPresentation(entry);
        presentationByEntry[entry.Id] = presentation;
        return presentation;
    }

    private LibraryEntryPresentation BuildEntryPresentation(LibraryEntry entry)
    {
        snapshotPrimaryMediaResolutions++;
        var personalPreviews = entry.PersonalPreviews
            .Where(preview => File.Exists(preview.Path))
            .OrderByDescending(preview => preview.CreatedAtUtc)
            .ThenByDescending(preview => preview.Id)
            .ToArray();
        var shareCards = entry.GeneratedShareCards
            .Where(card => File.Exists(card.Path))
            .OrderByDescending(card => card.CreatedAtUtc)
            .ThenByDescending(card => card.Id)
            .ToArray();
        var sourceImages = entry.SourceImagePaths.Where(File.Exists).ToArray();
        var hasCapturedPreview = !string.IsNullOrWhiteSpace(entry.RawPreviewPath) && File.Exists(entry.RawPreviewPath);
        var hasCardImage = !LibraryStore.IsImageLessPath(entry.CardPath) && File.Exists(entry.CardPath);
        var hasAdventurerPlate = !string.IsNullOrWhiteSpace(entry.AdventurerPlatePath) && File.Exists(entry.AdventurerPlatePath);

        var primaryPath = personalPreviews.FirstOrDefault(preview => preview.IsPrimary)?.Path
                          ?? personalPreviews.FirstOrDefault()?.Path
                          ?? (hasCapturedPreview ? entry.RawPreviewPath : null)
                          ?? (hasCardImage ? entry.CardPath : null)
                          ?? sourceImages.FirstOrDefault();

        return new LibraryEntryPresentation
        {
            PrimaryImagePath = primaryPath,
            LocalCapturedAt = entry.CapturedAtUtc == DateTime.MinValue
                ? string.Empty
                : entry.CapturedAtUtc.ToLocalTime().ToString("g"),
            RatingText = entry.Rating > 0
                ? $"{new string('★', entry.Rating)}{new string('☆', 5 - entry.Rating)}  "
                : string.Empty,
            PersonalPreviews = personalPreviews,
            GeneratedShareCards = shareCards,
            SourceImagePaths = sourceImages,
            HasCapturedPreview = hasCapturedPreview,
            HasCardImage = hasCardImage,
            HasAdventurerPlate = hasAdventurerPlate,
        };
    }

    private static void OpenLocalFile(string path)
    {
        if (!File.Exists(path))
            return;
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private static void OpenLocalFolder(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;
        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }

    private void DrawPiecesTable(LibraryEntry entry)
    {
        if (entry.Pieces.Count == 0)
        {
            ImGui.TextDisabled("Image-only import: no structured gear metadata was available for this capture.");
            return;
        }

        if (!ImGui.BeginTable(
                "##GlamSpectorLibraryPieces",
                4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
            return;

        ImGui.TableSetupColumn("Slot", ImGuiTableColumnFlags.WidthFixed, 100f);
        ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Dye", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Owned", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableHeadersRow();

        foreach (var piece in entry.Pieces.OrderBy(p => p.RawSlotIndex))
        {
            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            ImGui.TextUnformatted(piece.SlotName);
            ImGui.TableSetColumnIndex(1);
            DrawItemLink(piece);
            ImGui.TableSetColumnIndex(2);
            ImGui.TextWrapped(FormatDyes(piece));
            ImGui.TableSetColumnIndex(3);
            var ownership = ownershipService.Get(piece.DisplayItemId);
            if (ownership.Owned)
                ImGui.TextUnformatted(ownership.Summary);
            else
                ImGui.TextDisabled(ownership.Summary);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(ownership.Tooltip);
        }

        ImGui.EndTable();
        ImGui.TextDisabled(ownershipService.CoverageSummary);
        ImGui.SameLine();
        var canRefreshOwnership = ownershipService.CanForceRefresh;
        ImGui.BeginDisabled(!canRefreshOwnership);
        if (ImGui.SmallButton(canRefreshOwnership
                ? "Refresh ownership"
                : $"Refresh ownership ({ownershipService.ManualRefreshCooldownSeconds}s)"))
        {
            if (ownershipService.ForceRefresh())
                RefreshOwnershipProgressCache(force: true);
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Rescan local FFXIV inventory and already-cached item-search data. This does not run /isearch or send a server request. The short cooldown only avoids unnecessary repeated local rescans.");
    }


    private void DrawItemLink(GlamourPiece piece)
    {
        // Treat the visible glamour item as the interactive item. The Library
        // deliberately ignores the hidden stat-bearing item underneath it.
        ImGui.PushID($"item-{piece.RawSlotIndex}-{piece.DisplayItemId}");
        try
        {
            // Selectable gives the item name a familiar hover highlight while
            // leaving normal left-clicks harmless. The actions live on the
            // right-click menu so browsing the table cannot accidentally open
            // Try On.
            var wanted = wantedItemIds.Contains(piece.DisplayItemId);
            var visibleLabel = wanted ? $"{piece.DisplayItemName}  [Wanted]" : piece.DisplayItemName;
            ImGui.Selectable($"{visibleLabel}##link", false, ImGuiSelectableFlags.None, Vector2.Zero);
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Right-click for item actions.");

            if (ImGui.BeginPopupContextItem("##itemActions"))
            {
                if (ImGui.MenuItem("Try On"))
                    tryOnItem(piece);

                if (ImGui.MenuItem("Link in chat"))
                    linkItemInChat(piece);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Insert this item into FFXIV's chat input as a native item link. GlamSpector does not send the message for you.");

                ImGui.Separator();
                if (ImGui.MenuItem(wanted ? "Remove from wanted" : "Mark as wanted"))
                    SetWanted(piece.DisplayItemId, piece.DisplayItemName, !wanted);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Wanted status is personal Library metadata and is not included in exported GlamSpector packages.");

                if (ImGui.MenuItem("Copy item name"))
                    ImGui.SetClipboardText(piece.DisplayItemName);

                ImGui.EndPopup();
            }
        }
        finally
        {
            ImGui.PopID();
        }
    }

    private static string FormatDyes(GlamourPiece piece)
    {
        var dyes = new[] { piece.Stain1Name, piece.Stain2Name }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();
        return dyes.Length == 0 ? "—" : string.Join(" / ", dyes!);
    }

    private void DrawOwnershipProgress(LibraryEntry entry)
    {
        var progress = CalculateOwnershipProgress(entry);
        if (progress.Total <= 0)
            return;

        var wantedCount = CountWantedPieces(entry);
        ImGui.TextUnformatted("Ownership");
        ImGui.SameLine();
        if (progress.IsComplete)
            ImGui.TextUnformatted($"✓ {progress.Owned}/{progress.Total} verified owned");
        else
            ImGui.TextDisabled($"{progress.Owned}/{progress.Total} verified owned · {progress.Unverified} unverified");
        if (wantedCount > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled($"· {wantedCount} wanted slot{(wantedCount == 1 ? string.Empty : "s")}");
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only positive ownership matches are definitive. Unverified items may still be on storage GlamSpector cannot currently inspect, such as an unloaded retainer.");
    }

    private OwnershipProgress CalculateOwnershipProgress(LibraryEntry entry)
    {
        var total = 0;
        var owned = 0;
        foreach (var piece in entry.Pieces)
        {
            if (piece.DisplayItemId == 0)
                continue;
            total++;
            if (ownershipService.Get(piece.DisplayItemId).Owned)
                owned++;
        }

        if (entry.FacewearId != 0)
        {
            total++;
            if (ownershipService.GetFacewear(entry.FacewearId).Owned)
                owned++;
        }

        return new OwnershipProgress(owned, total);
    }

    private int CountWantedPieces(LibraryEntry entry) =>
        entry.Pieces.Count(piece => piece.DisplayItemId != 0 && wantedItemIds.Contains(piece.DisplayItemId));

    private void SetWanted(uint itemId, string itemName, bool wanted)
    {
        try
        {
            store.SetWanted(itemId, itemName, wanted);
            RefreshWanted();
            RefreshOwnershipProgressCache(force: true);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }
    }

    private void MarkUnverifiedWanted(LibraryEntry entry)
    {
        try
        {
            foreach (var piece in entry.Pieces
                         .Where(piece => piece.DisplayItemId != 0)
                         .GroupBy(piece => piece.DisplayItemId)
                         .Select(group => group.First()))
            {
                if (!ownershipService.Get(piece.DisplayItemId).Owned)
                    store.SetWanted(piece.DisplayItemId, piece.DisplayItemName, true);
            }
            RefreshWanted();
            RefreshOwnershipProgressCache(force: true);
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }
    }

    private void RefreshOwnershipProgressCache(bool force)
    {
        if (!force && DateTime.UtcNow - lastOwnershipProgressRefreshUtc < TimeSpan.FromSeconds(5))
            return;

        ownershipService.RefreshIfStale();
        var progress = new Dictionary<long, OwnershipProgress>();
        var wantedCounts = new Dictionary<long, int>();
        foreach (var entry in allEntries)
        {
            progress[entry.Id] = CalculateOwnershipProgress(entry);
            wantedCounts[entry.Id] = CountWantedPieces(entry);
        }

        ownershipProgressByEntry = progress;
        wantedCountByEntry = wantedCounts;
        lastOwnershipProgressRefreshUtc = DateTime.UtcNow;
        ApplyFilters();
    }

    private void RefreshWanted()
    {
        wantedItems = store.GetWantedItems();
        wantedItemIds = wantedItems.Select(x => x.ItemId).ToHashSet();
    }

    private void DrawWantedWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(720f, 500f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("GlamSpector Wanted Items###GlamSpectorWantedItems", ref showWantedItems))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("Wanted items");
        ImGui.SameLine();
        ImGui.TextDisabled($"{wantedItems.Count} item{(wantedItems.Count == 1 ? string.Empty : "s")}");
        ImGui.TextDisabled("Personal collection list. It is not included in shared GlamSpector exports.");

        var canRefresh = ownershipService.CanForceRefresh;
        ImGui.BeginDisabled(!canRefresh);
        if (ImGui.Button(canRefresh ? "Refresh ownership" : $"Refresh ownership ({ownershipService.ManualRefreshCooldownSeconds}s)"))
        {
            if (ownershipService.ForceRefresh())
                RefreshOwnershipProgressCache(force: true);
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Rescan local/cached ownership data. This does not run /isearch or send a server request.");

        ImGui.SameLine();
        var ownedWanted = wantedItems.Where(item => ownershipService.Get(item.ItemId).Owned).ToList();
        ImGui.BeginDisabled(ownedWanted.Count == 0);
        if (ImGui.Button($"Clear verified owned ({ownedWanted.Count})"))
        {
            foreach (var item in ownedWanted)
                store.SetWanted(item.ItemId, item.ItemName, false);
            RefreshWanted();
            RefreshOwnershipProgressCache(force: true);
        }
        ImGui.EndDisabled();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Remove items from Wanted only when GlamSpector can positively verify that you own them. No captures or item files are deleted.");

        ImGui.Separator();
        if (wantedItems.Count == 0)
        {
            ImGui.TextWrapped("No wanted items yet. Right-click an item in any saved glam and choose 'Mark as wanted'.");
            ImGui.End();
            return;
        }

        if (ImGui.BeginTable("##GlamSpectorWantedTable", 4,
                ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Item", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Owned", ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Used by", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("Action", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableHeadersRow();

            foreach (var item in wantedItems.ToList())
            {
                ImGui.PushID(unchecked((int)item.ItemId));
                ImGui.TableNextRow();
                ImGui.TableSetColumnIndex(0);
                var tempPiece = new GlamourPiece
                {
                    DisplayItemId = item.ItemId,
                    DisplayItemName = item.ItemName,
                };
                ImGui.Selectable($"{item.ItemName}##wantedItem", false, ImGuiSelectableFlags.None, Vector2.Zero);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip("Right-click for item actions.");
                if (ImGui.BeginPopupContextItem("##wantedActions"))
                {
                    if (ImGui.MenuItem("Try On"))
                        tryOnItem(tempPiece);
                    if (ImGui.MenuItem("Link in chat"))
                        linkItemInChat(tempPiece);
                    if (ImGui.MenuItem("Copy item name"))
                        ImGui.SetClipboardText(item.ItemName);
                    ImGui.Separator();
                    if (ImGui.MenuItem("Remove from wanted"))
                        SetWanted(item.ItemId, item.ItemName, false);
                    ImGui.EndPopup();
                }

                ImGui.TableSetColumnIndex(1);
                var ownership = ownershipService.Get(item.ItemId);
                if (ownership.Owned)
                    ImGui.TextUnformatted(ownership.Summary);
                else
                    ImGui.TextDisabled(ownership.Summary);
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(ownership.Tooltip);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextUnformatted(item.UsedByCaptures == 1 ? "1 glam" : $"{item.UsedByCaptures} glams");

                ImGui.TableSetColumnIndex(3);
                if (ImGui.SmallButton("Remove"))
                    SetWanted(item.ItemId, item.ItemName, false);

                ImGui.PopID();
            }
            ImGui.EndTable();
        }

        ImGui.TextDisabled(ownershipService.CoverageSummary);
        ImGui.End();
    }

    private void Select(long id, bool persist = true)
    {
        try
        {
            selected = store.Get(id);
            if (selected is not null)
            {
                LoadMetadataEditors(selected, force: true);
                if (persist && configuration.LibrarySelectedEntryId != selected.Id)
                {
                    configuration.LibrarySelectedEntryId = selected.Id;
                    SaveUiState();
                }
            }
            else if (persist)
            {
                ClearSelection(persist: true);
            }
            mediaViewMode = MediaViewMode.Primary;
            selectedSourceImageIndex = 0;
            applySecondarySectionState = true;
            CancelDisplayTitleEdit();
            confirmDeletePersonalPreviewId = null;
            confirmDeleteShareCardId = null;
            confirmRemoveId = null;
            confirmDeleteId = null;
            lastError = null;
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }
    }

    private void Refresh()
    {
        try
        {
            var selectedId = selected?.Id;
            totalEntryCount = store.CountEntries();
            allEntries = store.Search(search, sort, 5000);
            presentationByEntry.Clear();
            snapshotPrimaryMediaResolutions = 0;
            foreach (var entry in allEntries)
                presentationByEntry[entry.Id] = BuildEntryPresentation(entry);
            RefreshWanted();
            RefreshOwnershipProgressCache(force: true);
            lastError = null;

            if (selectedId.HasValue)
            {
                var refreshedSelection = store.Get(selectedId.Value);
                if (refreshedSelection is not null)
                {
                    selected = refreshedSelection;
                    if (!presentationByEntry.ContainsKey(selected.Id))
                        presentationByEntry[selected.Id] = BuildEntryPresentation(selected);
                    LoadMetadataEditors(refreshedSelection);
                }
                else
                {
                    ClearSelection(persist: true);
                }
            }
            else if (restoreSelectionPending && configuration.LibrarySelectedEntryId > 0)
            {
                var restoredId = configuration.LibrarySelectedEntryId;
                var restored = store.Get(restoredId);
                if (restored is null)
                {
                    configuration.LibrarySelectedEntryId = 0;
                    SaveUiState();
                }
                else
                {
                    selected = restored;
                    if (!presentationByEntry.ContainsKey(selected.Id))
                        presentationByEntry[selected.Id] = BuildEntryPresentation(selected);
                    LoadMetadataEditors(restored, force: true);
                    applySecondarySectionState = true;
                }
            }

            restoreSelectionPending = false;
        }
        catch (Exception ex)
        {
            allEntries = [];
            entries = [];
            presentationByEntry.Clear();
            lastError = ex.Message;
        }
    }

    private void ClearSelection(bool persist)
    {
        selected = null;
        tagsEditDirty = false;
        notesEditDirty = false;
        tagsEdit = string.Empty;
        notesEdit = string.Empty;
        applySecondarySectionState = true;
        CancelDisplayTitleEdit();

        if (persist && configuration.LibrarySelectedEntryId != 0)
        {
            configuration.LibrarySelectedEntryId = 0;
            SaveUiState();
        }
    }

    private void CopyGlamCode(LibraryEntry entry)
    {
        try
        {
            var code = glamCodeService.Encode(entry);
            ImGui.SetClipboardText(code);
            importStatus = $"Copied Glam Code ({code.Length} characters) to the clipboard.";
            lastError = null;
            if (configuration.NotifyImportExport)
                Plugin.ChatGui.Print(importStatus, "GlamSpector");
        }
        catch (Exception ex)
        {
            importStatus = null;
            lastError = ex.Message;
        }
    }

    private void DrawGlamCodeImportWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(620f, 300f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Import Glam Code###GlamSpectorImportCode", ref showGlamCodeImport))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("Paste a GS1 Glam Code. Codes contain only the visible outfit: item IDs, both dye channels and Facewear. They do not include the source character, screenshot, rating, Wanted state, tags or notes.");
        ImGui.Spacing();
        ImGui.InputTextMultiline("##GlamCodeText", ref glamCodeImportText, 4096, new Vector2(-1f, 120f));

        ImGui.BeginDisabled(string.IsNullOrWhiteSpace(glamCodeImportText));
        if (ImGui.Button("Import to Library"))
            ImportGlamCode();
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Clear"))
            glamCodeImportText = string.Empty;
        ImGui.SameLine();
        if (ImGui.Button("Close"))
            showGlamCodeImport = false;

        ImGui.End();
    }

    private void ImportGlamCode()
    {
        try
        {
            var normalized = GlamCodeService.Normalize(glamCodeImportText);
            var snapshot = glamCodeService.Decode(normalized);
            var entryId = store.AddGlamCode(snapshot, normalized);
            importStatus = $"Imported Glam Code with {snapshot.Pieces.Count} gear piece{(snapshot.Pieces.Count == 1 ? string.Empty : "s")}{(snapshot.Facewear?.Detected == true ? " + Facewear" : string.Empty)}.";
            lastError = null;
            glamCodeImportText = string.Empty;
            showGlamCodeImport = false;
            Refresh();
            Select(entryId);
            if (configuration.NotifyImportExport)
                Plugin.ChatGui.Print(importStatus, "GlamSpector");
        }
        catch (Exception ex)
        {
            importStatus = null;
            lastError = ex.Message;
        }
    }

    private void DrawEorzeaCollectionImportWindow()
    {
        ImGui.SetNextWindowSize(new Vector2(720f, 560f), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Import from Eorzea Collection###GlamSpectorImportEC", ref showEorzeaCollectionImport))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("Eorzea Collection import is manual-only. Paste one glamour URL, open it in your normal browser, copy the full page source, then paste that HTML below. GlamSpector parses only the supplied HTML locally and performs no EC web or image requests.");
        ImGui.TextDisabled("Example: https://ffxiv.eorzeacollection.com/glamour/350011/petals-and-lace");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText("##EorzeaCollectionUrl", ref eorzeaCollectionUrl, 2048);

        var busy = eorzeaCollectionImportTask is not null;
        ImGui.BeginDisabled(busy || string.IsNullOrWhiteSpace(eorzeaCollectionUrl));
        if (ImGui.Button("Open in browser"))
            OpenEorzeaCollectionInBrowser();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.BeginDisabled(busy);
        if (ImGui.Button("Clear"))
        {
            eorzeaCollectionUrl = string.Empty;
            eorzeaCollectionPageSource = string.Empty;
        }
        ImGui.EndDisabled();
        ImGui.SameLine();
        if (ImGui.Button("Close"))
            showEorzeaCollectionImport = false;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextDisabled("Page source (required)");
        ImGui.TextWrapped("In the browser, use View Source (usually Ctrl+U), then Ctrl+A and Ctrl+C. Paste the full HTML source here. A URL by itself is not imported or fetched by GlamSpector.");
        ImGui.Spacing();
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextMultiline("##EorzeaCollectionPageSource", ref eorzeaCollectionPageSource, 4 * 1024 * 1024, new Vector2(-1f, 210f));
        ImGui.BeginDisabled(busy || string.IsNullOrWhiteSpace(eorzeaCollectionUrl) || string.IsNullOrWhiteSpace(eorzeaCollectionPageSource));
        if (ImGui.Button("Import pasted page source"))
            StartEorzeaCollectionPageSourceImport();
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextDisabled($"{eorzeaCollectionPageSource.Length:N0} characters pasted");

        if (busy)
        {
            ImGui.Spacing();
            ImGui.TextDisabled("Parsing the pasted page source locally…");
        }

        ImGui.Spacing();
        ImGui.TextDisabled("No page fetch, image download, catalogue crawl, browser automation, or cookie access is performed.");
        ImGui.End();
    }

    private void OpenEorzeaCollectionInBrowser()
    {
        try
        {
            OpenExternalUrl(eorzeaCollectionImportService.NormalizePageUrl(eorzeaCollectionUrl));
            lastError = null;
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }
    }

    private void StartEorzeaCollectionPageSourceImport()
    {
        if (eorzeaCollectionImportTask is not null)
            return;

        importStatus = "Parsing copied Eorzea Collection page source locally…";
        lastError = null;
        eorzeaCollectionImportTask = eorzeaCollectionImportService.ImportFromPageSourceAsync(
            eorzeaCollectionUrl.Trim(),
            eorzeaCollectionPageSource);
    }

    private void PollEorzeaCollectionImport()
    {
        var task = eorzeaCollectionImportTask;
        if (task is null || !task.IsCompleted)
            return;

        eorzeaCollectionImportTask = null;
        try
        {
            var imported = task.GetAwaiter().GetResult();
            var entryId = store.AddEorzeaCollectionImport(imported);
            var warningText = imported.Warnings.Count > 0 ? $" Warning: {string.Join(" ", imported.Warnings)}" : string.Empty;
            importStatus = $"Imported '{imported.Title}' from pasted Eorzea Collection page source: {imported.Snapshot.Pieces.Count} gear piece{(imported.Snapshot.Pieces.Count == 1 ? string.Empty : "s")}, {imported.SourceImagePaths.Count} retained local source image{(imported.SourceImagePaths.Count == 1 ? string.Empty : "s")}.{warningText}";
            lastError = null;
            eorzeaCollectionUrl = string.Empty;
            eorzeaCollectionPageSource = string.Empty;
            showEorzeaCollectionImport = false;
            Refresh();
            Select(entryId);
            if (selected?.SourceImagePaths.Count > 1)
                mediaViewMode = MediaViewMode.SourceImages;
            if (configuration.NotifyImportExport)
                Plugin.ChatGui.Print(importStatus, "GlamSpector");
        }
        catch (Exception ex)
        {
            importStatus = null;
            lastError = ex.GetBaseException().Message;
        }
    }

    private void DrawSourceImages(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            ImGui.TextDisabled("No locally cached source images are available.");
            return;
        }

        selectedSourceImageIndex = Math.Clamp(selectedSourceImageIndex, 0, paths.Count - 1);
        for (var i = 0; i < paths.Count; i++)
        {
            if (i > 0)
                ImGui.SameLine();
            if (ImGui.SmallButton($"{i + 1}##sourceImage{i}"))
                selectedSourceImageIndex = i;
        }
        ImGui.SameLine();
        ImGui.TextDisabled($"Image {selectedSourceImageIndex + 1} of {paths.Count}");
        DrawImagePreview(paths[selectedSourceImageIndex], 520f);
    }

    private static void OpenExternalUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            return;

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.AbsoluteUri,
            UseShellExecute = true,
        });
    }

    private void ImportExisting()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configuration.OutputDirectory) || !Directory.Exists(configuration.OutputDirectory))
            {
                importStatus = null;
                lastError = "The configured output folder does not exist.";
                return;
            }

            var result = store.ImportExistingCaptures(configuration.OutputDirectory);
            importStatus = result.ToDisplayString();
            lastError = null;
            if (configuration.NotifyImportExport)
                Plugin.ChatGui.Print(importStatus, "GlamSpector");
            Refresh();
        }
        catch (Exception ex)
        {
            importStatus = null;
            lastError = ex.Message;
        }
    }

    private void ImportPackages()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(configuration.OutputDirectory) || !Directory.Exists(configuration.OutputDirectory))
            {
                importStatus = null;
                lastError = "The configured output folder does not exist.";
                return;
            }

            var result = store.ImportSharePackages(configuration.OutputDirectory);
            importStatus = result.ToDisplayString();
            lastError = null;
            if (configuration.NotifyImportExport)
                Plugin.ChatGui.Print(importStatus, "GlamSpector");
            Refresh();
        }
        catch (Exception ex)
        {
            importStatus = null;
            lastError = ex.Message;
        }
    }

    private void ExportSelected(LibraryEntry entry)
    {
        try
        {
            var path = store.ExportPackage(entry, configuration.OutputDirectory);
            importStatus = $"Exported {Path.GetFileName(path)} to the Exports folder.";
            lastError = null;
            if (configuration.NotifyImportExport)
                Plugin.ChatGui.Print(importStatus, "GlamSpector");
        }
        catch (Exception ex)
        {
            importStatus = null;
            lastError = ex.Message;
        }
    }

    private void PrepareDuplicateCleanup()
    {
        try
        {
            duplicateCandidateCount = store.FindOlderDuplicates().Count;
            confirmDuplicateCleanup = true;
            lastError = null;
        }
        catch (Exception ex)
        {
            confirmDuplicateCleanup = false;
            lastError = ex.Message;
        }
    }

    private void CleanupDuplicates(bool deleteFiles)
    {
        try
        {
            var removed = store.CleanupOlderDuplicates(deleteFiles);
            confirmDuplicateCleanup = false;
            duplicateCandidateCount = 0;
            importStatus = deleteFiles
                ? $"Deleted {removed} older duplicate capture{(removed == 1 ? string.Empty : "s")} from the library and disk."
                : $"Removed {removed} older duplicate librar{(removed == 1 ? "y entry" : "y entries")} (files kept).";
            lastError = null;
            if (configuration.NotifyDelete)
                Plugin.ChatGui.Print(importStatus, "GlamSpector");
            Refresh();
        }
        catch (Exception ex)
        {
            lastError = ex.Message;
        }
    }

    private static string SortLabel(LibrarySort value) => value switch
    {
        LibrarySort.Newest => "Newest",
        LibrarySort.Oldest => "Oldest",
        LibrarySort.Character => "Character",
        LibrarySort.World => "World",
        LibrarySort.Rating => "Rating",
        LibrarySort.FileSize => "File size",
        _ => "Newest",
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024)
            return $"{bytes} B";
        var kb = bytes / 1024d;
        if (kb < 1024d)
            return $"{kb:0.#} KB";
        var mb = kb / 1024d;
        if (mb < 1024d)
            return $"{mb:0.##} MB";
        return $"{mb / 1024d:0.##} GB";
    }
}
