using System;
using Dalamud.Configuration;

namespace GlamSpector;

public enum AdventurerPlateCaptureMode
{
    Off = 0,
    Ask = 1,
    Automatic = 2,
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 13;

    // Assembly/plugin version last observed by this installation. This is kept
    // separate from the configuration schema Version above.
    public string? LastSeenPluginVersion { get; set; }

    public string OutputDirectory { get; set; } = string.Empty;
    public bool CopyToClipboard { get; set; } = true;
    public bool WriteDiagnosticJson { get; set; } = false;
    public bool SaveRawPreview { get; set; } = false;
    public bool CleanupItemLevelOverlay { get; set; } = true;
    public float CropPaddingPixels { get; set; } = 4f;
    public bool AutoAddToLibrary { get; set; } = true;
    public bool BringInspectToFrontBeforeCapture { get; set; } = true;
    public bool HideGlamSpectorWindowsDuringCapture { get; set; } = false;

    // Optional local IPC integration. Existing installations remain opted out
    // until the user explicitly enables it.
    public bool EnableAllaganToolsIntegration { get; set; }

    // M3.5: after a normal Glam Card capture, optionally fetch the inspected
    // character's Adventurer Plate and attach it to the same library entry.
    public AdventurerPlateCaptureMode AdventurerPlateCaptureMode { get; set; } = AdventurerPlateCaptureMode.Automatic;
    public bool CloseAutoOpenedAdventurerPlate { get; set; } = true;
    public bool CapturePortraitRecipeWithPlate { get; set; } = true;
    public float AdventurerPlateTimeoutSeconds { get; set; } = 3f;
    public float AdventurerPlateSettleSeconds { get; set; } = 1.0f;

    public bool NotifyCaptureSuccess { get; set; } = true;
    public bool NotifyAdventurerPlate { get; set; } = true;
    public bool NotifyDelete { get; set; } = true;
    public bool NotifyImportExport { get; set; } = true;
    public bool NotifyClipboard { get; set; } = true;

    // M3.15: local Library presentation state. Enum-backed values are stored as
    // integers and validated by LibraryUi so unknown values from future/older
    // configurations fall back safely instead of preventing the window opening.
    public int LibrarySortMode { get; set; }
    public int LibraryRatingFilter { get; set; }
    public int LibraryOwnershipFilter { get; set; }
    public int LibraryWantedFilter { get; set; }
    public int LibraryPlateFilter { get; set; }
    public bool LibraryFiltersExpanded { get; set; }
    public float LibraryListWidth { get; set; } = 360f;
    public bool LibraryTagsNotesExpanded { get; set; }
    public bool LibraryFilesSharingExpanded { get; set; }
    public bool LibraryEntryExpanded { get; set; }
    public long LibrarySelectedEntryId { get; set; }

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
