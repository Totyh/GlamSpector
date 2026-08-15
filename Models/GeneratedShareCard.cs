using System;

namespace GlamSpector.Models;

public sealed class GeneratedShareCard
{
    public long Id { get; init; }
    public long EntryId { get; init; }
    public long? PersonalPreviewId { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public string Path { get; init; } = string.Empty;
}
