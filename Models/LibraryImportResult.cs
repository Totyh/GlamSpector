namespace GlamSpector.Models;

public enum LibrarySort
{
    Newest,
    Oldest,
    Character,
    World,
    Rating,
    FileSize,
}

public sealed class LibraryImportResult
{
    public int FullMetadataImported { get; init; }
    public int ImageOnlyImported { get; init; }
    public int ExistingSkipped { get; init; }
    public int Failed { get; init; }

    public int Imported => FullMetadataImported + ImageOnlyImported;

    public string ToDisplayString()
    {
        var summary = $"Imported {Imported} capture{(Imported == 1 ? string.Empty : "s")}" +
                      $" ({FullMetadataImported} full metadata, {ImageOnlyImported} image-only); " +
                      $"skipped {ExistingSkipped} already in library";
        if (Failed > 0)
            summary += $"; {Failed} failed";
        return summary + ".";
    }
}
