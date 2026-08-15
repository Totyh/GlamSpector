using System;
using System.Collections.Generic;

namespace GlamSpector.Models;

public sealed class GlamourSnapshot
{
    public DateTime CapturedAtUtc { get; init; }
    public uint EntityId { get; init; }
    public string CharacterName { get; init; } = "Unknown Character";
    public string HomeWorld { get; init; } = "Unknown World";
    public string? FreeCompanyName { get; init; }
    public List<GlamourPiece> Pieces { get; init; } = [];
    public FacewearDiagnostics? Facewear { get; init; }
    public PreviewCaptureDiagnostics? Preview { get; set; }
}

public sealed class GlamourPiece
{
    public int RawSlotIndex { get; init; }
    public string SlotName { get; init; } = string.Empty;
    public uint EquippedItemId { get; init; }
    public uint GlamourItemId { get; init; }
    public uint DisplayItemId { get; init; }
    public string DisplayItemName { get; init; } = string.Empty;
    public byte Stain1Id { get; init; }
    public string? Stain1Name { get; init; }
    public byte Stain2Id { get; init; }
    public string? Stain2Name { get; init; }
}

public sealed class FacewearDiagnostics
{
    // CharacterInspect exposes two raw Glasses row IDs in CharaViewModelData.
    // In our live test GlassesId0=312 matched the visible "Brown Tinted Sunglasses".
    // We retain both IDs/names for diagnostics and use the first non-zero resolved
    // entry as the user-facing Facewear value.
    public ushort GlassesId0 { get; init; }
    public string? GlassesName0 { get; init; }
    public ushort GlassesId1 { get; init; }
    public string? GlassesName1 { get; init; }
    public bool Detected => GlassesId0 != 0 || GlassesId1 != 0;
    public string? DisplayName =>
        GlassesId0 != 0 ? GlassesName0 ?? $"Facewear #{GlassesId0}" :
        GlassesId1 != 0 ? GlassesName1 ?? $"Facewear #{GlassesId1}" :
        null;
    public uint CharaViewState { get; init; }
    public bool CharacterLoaded { get; init; }
    public string Source { get; init; } = "live";
}

public sealed class PreviewCaptureDiagnostics
{
    public string BoundsSource { get; init; } = string.Empty;
    public int Left { get; init; }
    public int Top { get; init; }
    public int Right { get; init; }
    public int Bottom { get; init; }
    public float Uv0X { get; init; }
    public float Uv0Y { get; init; }
    public float Uv1X { get; init; }
    public float Uv1Y { get; init; }
}
