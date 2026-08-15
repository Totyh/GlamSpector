using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using GlamSpector.Models;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace GlamSpector.Services;

/// <summary>
/// Encodes the visible glamour itself (item IDs, dyes and Facewear) into a short,
/// versioned text code. Character identity, screenshots, ratings, tags, notes and
/// other personal Library metadata are deliberately not included.
/// </summary>
public sealed class GlamCodeService
{
    public const string Prefix = "GS1:";

    private static readonly IReadOnlyDictionary<int, string> SlotNames = new Dictionary<int, string>
    {
        [0] = "Main Hand",
        [1] = "Off Hand",
        [2] = "Head",
        [3] = "Body",
        [4] = "Hands",
        [6] = "Legs",
        [7] = "Feet",
        [8] = "Earrings",
        [9] = "Necklace",
        [10] = "Bracelets",
        [11] = "Right Ring",
        [12] = "Left Ring",
    };

    private readonly IDataManager dataManager;

    public GlamCodeService(IDataManager dataManager)
    {
        this.dataManager = dataManager;
    }

    public string Encode(LibraryEntry entry) => Encode(entry.Pieces, entry.FacewearId);

    public string Encode(IEnumerable<GlamourPiece> pieces, ushort facewearId)
    {
        var normalized = pieces
            .Where(piece => piece.DisplayItemId != 0 && SlotNames.ContainsKey(piece.RawSlotIndex))
            .GroupBy(piece => piece.RawSlotIndex)
            .Select(group => group.First())
            .OrderBy(piece => piece.RawSlotIndex)
            .ToList();

        if (normalized.Count == 0)
            throw new InvalidOperationException("This library entry has no structured glamour pieces to share.");
        if (normalized.Count > 13)
            throw new InvalidOperationException("This glamour contains too many equipment slots for Glam Code v1.");

        // v1 payload:
        // [version:1][pieceCount:1]
        // repeated [rawSlot:1][displayItemId:4 LE][stain1:1][stain2:1]
        // [facewearId:2 LE][checksum:4]
        var payloadLengthWithoutChecksum = 2 + (normalized.Count * 7) + 2;
        var bytes = new byte[payloadLengthWithoutChecksum + 4];
        bytes[0] = 1;
        bytes[1] = checked((byte)normalized.Count);

        var offset = 2;
        foreach (var piece in normalized)
        {
            bytes[offset++] = checked((byte)piece.RawSlotIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), piece.DisplayItemId);
            offset += 4;
            bytes[offset++] = piece.Stain1Id;
            bytes[offset++] = piece.Stain2Id;
        }

        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset, 2), facewearId);
        offset += 2;

        var checksum = SHA256.HashData(bytes.AsSpan(0, offset));
        checksum.AsSpan(0, 4).CopyTo(bytes.AsSpan(offset, 4));

        return Prefix + ToBase64Url(bytes);
    }

    public GlamourSnapshot Decode(string code)
    {
        if (string.IsNullOrWhiteSpace(code))
            throw new InvalidOperationException("Paste a Glam Code first.");

        var normalizedCode = Normalize(code);
        if (!normalizedCode.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This does not look like a GlamSpector Glam Code (expected GS1:...).");

        byte[] bytes;
        try
        {
            bytes = FromBase64Url(normalizedCode[Prefix.Length..]);
        }
        catch (FormatException)
        {
            throw new InvalidOperationException("The Glam Code contains invalid Base64 data.");
        }

        if (bytes.Length < 8)
            throw new InvalidOperationException("The Glam Code is too short.");
        if (bytes[0] != 1)
            throw new InvalidOperationException($"Unsupported Glam Code version {bytes[0]}.");

        var pieceCount = bytes[1];
        if (pieceCount == 0 || pieceCount > 13)
            throw new InvalidOperationException("The Glam Code has an invalid equipment-slot count.");

        var expectedLength = 2 + (pieceCount * 7) + 2 + 4;
        if (bytes.Length != expectedLength)
            throw new InvalidOperationException("The Glam Code length does not match its contents.");

        var dataLength = bytes.Length - 4;
        var expectedChecksum = SHA256.HashData(bytes.AsSpan(0, dataLength));
        if (!CryptographicOperations.FixedTimeEquals(bytes.AsSpan(dataLength, 4), expectedChecksum.AsSpan(0, 4)))
            throw new InvalidOperationException("The Glam Code checksum does not match. It may have been mistyped or truncated.");

        var itemSheet = dataManager.GetExcelSheet<Item>();
        var stainSheet = dataManager.GetExcelSheet<Stain>();
        var glassesSheet = dataManager.GetExcelSheet<Glasses>();
        var pieces = new List<GlamourPiece>(pieceCount);
        var seenSlots = new HashSet<int>();
        var offset = 2;

        for (var i = 0; i < pieceCount; i++)
        {
            var rawSlot = bytes[offset++];
            var itemId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            offset += 4;
            var stain1Id = bytes[offset++];
            var stain2Id = bytes[offset++];

            if (!SlotNames.TryGetValue(rawSlot, out var slotName))
                throw new InvalidOperationException($"The Glam Code contains unsupported equipment slot {rawSlot}.");
            if (!seenSlots.Add(rawSlot))
                throw new InvalidOperationException($"The Glam Code contains equipment slot {rawSlot} more than once.");
            if (itemId == 0)
                throw new InvalidOperationException($"The Glam Code contains an empty item in {slotName}.");

            var itemName = itemSheet.TryGetRow(itemId, out var item)
                ? item.Name.ToString()
                : $"Item #{itemId}";
            var stain1Name = stain1Id != 0
                ? stainSheet.TryGetRow(stain1Id, out var stain1Row) ? stain1Row.Name.ToString() : $"Dye #{stain1Id}"
                : null;
            var stain2Name = stain2Id != 0
                ? stainSheet.TryGetRow(stain2Id, out var stain2Row) ? stain2Row.Name.ToString() : $"Dye #{stain2Id}"
                : null;

            // Glam Codes intentionally contain only the visible glamour. Treat the
            // display item itself as the item to Try On; there is no hidden stat item.
            pieces.Add(new GlamourPiece
            {
                RawSlotIndex = rawSlot,
                SlotName = slotName,
                EquippedItemId = itemId,
                GlamourItemId = 0,
                DisplayItemId = itemId,
                DisplayItemName = itemName,
                Stain1Id = stain1Id,
                Stain1Name = stain1Name,
                Stain2Id = stain2Id,
                Stain2Name = stain2Name,
            });
        }

        var facewearId = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
        string? facewearName = null;
        if (facewearId != 0 && glassesSheet.TryGetRow(facewearId, out var glasses))
            facewearName = glasses.Name.ToString();

        return new GlamourSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            CharacterName = "Shared Glam",
            HomeWorld = "Glam Code",
            Pieces = pieces,
            Facewear = facewearId != 0
                ? new FacewearDiagnostics
                {
                    GlassesId0 = facewearId,
                    GlassesName0 = facewearName,
                    Source = "glam-code",
                }
                : null,
        };
    }

    public static string Normalize(string code) =>
        new(code.Where(ch => !char.IsWhiteSpace(ch)).ToArray());

    private static string ToBase64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 += (base64.Length % 4) switch
        {
            0 => string.Empty,
            2 => "==",
            3 => "=",
            _ => throw new FormatException("Invalid Base64Url length."),
        };
        return Convert.FromBase64String(base64);
    }
}
