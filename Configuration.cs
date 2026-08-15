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
    public int Version { get; set; } = 10;

    public string OutputDirectory { get; set; } = string.Empty;
    public bool CopyToClipboard { get; set; } = true;
    public bool WriteDiagnosticJson { get; set; } = false;
    public bool SaveRawPreview { get; set; } = false;
    public bool CleanupItemLevelOverlay { get; set; } = true;
    public float CropPaddingPixels { get; set; } = 4f;
    public bool AutoAddToLibrary { get; set; } = true;
    public bool BringInspectToFrontBeforeCapture { get; set; } = true;
    public bool HideGlamSpectorWindowsDuringCapture { get; set; } = false;

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

    public void Save() => Plugin.PluginInterface.SavePluginConfig(this);
}
