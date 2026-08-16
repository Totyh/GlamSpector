using System;
using System.Collections.Generic;

namespace GlamSpector.Models;

public sealed class LibraryEntry
{
    public long Id { get; init; }
    public DateTime CapturedAtUtc { get; init; }
    public string DisplayTitle { get; init; } = "Untitled glamour";
    public string CharacterName { get; init; } = "Unknown Character";
    public string HomeWorld { get; init; } = "Unknown World";
    public string? FreeCompanyName { get; init; }
    public int Rating { get; init; }
    public string? Notes { get; init; }
    public string? SourceKind { get; init; }
    public string? SourceUrl { get; init; }
    public string? SourceTitle { get; init; }
    public string? SourceCreator { get; init; }
    public List<string> SourceImagePaths { get; init; } = [];
    public List<PersonalPreview> PersonalPreviews { get; init; } = [];
    public List<GeneratedShareCard> GeneratedShareCards { get; init; } = [];
    public long TotalMediaBytes { get; set; }
    public List<string> Tags { get; init; } = [];
    public string CardPath { get; init; } = string.Empty;
    public string? RawPreviewPath { get; init; }
    public string? DiagnosticJsonPath { get; init; }
    public string? AdventurerPlatePath { get; init; }
    public PortraitSettingsSnapshot? PortraitSettings { get; init; }
    public ushort FacewearId { get; init; }
    public string? FacewearName { get; init; }
    public List<GlamourPiece> Pieces { get; init; } = [];
}
