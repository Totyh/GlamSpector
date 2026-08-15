using System;

namespace GlamSpector.Models;

public sealed class WantedItem
{
    public uint ItemId { get; init; }
    public string ItemName { get; init; } = string.Empty;
    public DateTime AddedAtUtc { get; init; }
    public int UsedByCaptures { get; init; }
}

public readonly record struct OwnershipProgress(int Owned, int Total)
{
    public int Unverified => Math.Max(0, Total - Owned);
    public bool IsComplete => Total > 0 && Owned >= Total;
    public string Summary => Total <= 0 ? "No structured gear" : $"{Owned}/{Total} verified owned";
}
