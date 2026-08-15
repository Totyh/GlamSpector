using System;

namespace GlamSpector.Models;

public sealed class PersonalPreview
{
    public long Id { get; init; }
    public long EntryId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string Path { get; init; } = string.Empty;
    public bool IsPrimary { get; init; }
}
