using System;

namespace GlamSpector.Models;

public sealed class GlamSpectorPackageManifest
{
    public string Format { get; init; } = "GlamSpector";
    public int FormatVersion { get; init; } = 1;
    public DateTime ExportedAtUtc { get; init; } = DateTime.UtcNow;
    public string CardFile { get; init; } = "card.png";
    public string? AdventurerPlateFile { get; init; } = null;
    public PortraitSettingsSnapshot? PortraitSettings { get; init; }
    public GlamourSnapshot Snapshot { get; init; } = new();
}

public sealed class LibraryPackageImportResult
{
    public int Imported { get; init; }
    public int ExistingSkipped { get; init; }
    public int Failed { get; init; }

    public string ToDisplayString()
    {
        var text = $"Imported {Imported} shared package{(Imported == 1 ? string.Empty : "s")}; skipped {ExistingSkipped} already in library";
        if (Failed > 0)
            text += $"; {Failed} failed";
        return text + ".";
    }
}
