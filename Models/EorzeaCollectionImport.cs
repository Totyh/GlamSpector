using System.Collections.Generic;

namespace GlamSpector.Models;

public sealed class EorzeaCollectionImportResult
{
    public int GlamourId { get; init; }
    public string SourceUrl { get; init; } = string.Empty;
    public string Title { get; init; } = "Eorzea Collection Glamour";
    public string? Creator { get; init; }
    public GlamourSnapshot Snapshot { get; init; } = new();
    public string CardPath { get; init; } = string.Empty;
    public List<string> SourceImagePaths { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}
