namespace GlamSpector.Models;

public sealed class InventoryOwnership
{
    public bool Owned { get; init; }
    public string Summary { get; init; } = "?";
    public string Tooltip { get; init; } = string.Empty;
}
