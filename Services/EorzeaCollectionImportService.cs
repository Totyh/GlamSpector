using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using GlamSpector.Models;
using Lumina.Excel.Sheets;

namespace GlamSpector.Services;

/// <summary>
/// Imports one user-supplied Eorzea Collection glamour page source at a time.
/// GlamSpector never requests the page or its images: the user opens the page
/// in their browser and pastes its HTML here for local parsing.
/// </summary>
public sealed class EorzeaCollectionImportService
{
    private const int MaxPageBytes = 4 * 1024 * 1024;

    private static readonly Regex GlamourPathRegex = new(
        @"^/glamour/(?<id>\d+)(?:/[^/?#]+)?/?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex SlotClassRegex = new(
        @"c-gear-slot-(?<slot>[a-z0-9-]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ItemNameClassRegex = new(
        @"c-gear-slot-item-name[^>]*>(?<value>.*?)</(?:span|div|p|a)>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex TagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex ScriptStyleRegex = new(
        @"<(script|style|noscript)\b[^>]*>.*?</\1>",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex MetaRegex = new(
        """<meta\b[^>]*(?:property|name)\s*=\s*["'](?<name>[^"']+)["'][^>]*content\s*=\s*["'](?<content>[^"']*)["'][^>]*>|<meta\b[^>]*content\s*=\s*["'](?<content2>[^"']*)["'][^>]*(?:property|name)\s*=\s*["'](?<name2>[^"']+)["'][^>]*>""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ByCreatorRegex = new(
        """\bby\s+(?<creator>[^\r\n<]{2,80}?)(?:\s+from\s+[«"']|\s*[|–—-]\s*Eorzea Collection|\r|\n|<)""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly IReadOnlyDictionary<string, (int RawSlot, string Name)> SlotTokens =
        new Dictionary<string, (int, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["weapon"] = (0, "Main Hand"),
            ["mainhand"] = (0, "Main Hand"),
            ["main-hand"] = (0, "Main Hand"),
            ["offhand"] = (1, "Off Hand"),
            ["off-hand"] = (1, "Off Hand"),
            ["shield"] = (1, "Off Hand"),
            ["head"] = (2, "Head"),
            ["body"] = (3, "Body"),
            ["chest"] = (3, "Body"),
            ["hands"] = (4, "Hands"),
            ["hand"] = (4, "Hands"),
            ["legs"] = (6, "Legs"),
            ["leg"] = (6, "Legs"),
            ["feet"] = (7, "Feet"),
            ["foot"] = (7, "Feet"),
            ["earrings"] = (8, "Earrings"),
            ["earring"] = (8, "Earrings"),
            ["ears"] = (8, "Earrings"),
            ["necklace"] = (9, "Necklace"),
            ["neck"] = (9, "Necklace"),
            ["bracelets"] = (10, "Bracelets"),
            ["bracelet"] = (10, "Bracelets"),
            ["wrists"] = (10, "Bracelets"),
            ["ring"] = (11, "Right Ring"),
            ["rings"] = (11, "Right Ring"),
            ["right-ring"] = (11, "Right Ring"),
            ["right-ring-finger"] = (11, "Right Ring"),
            ["left-ring"] = (12, "Left Ring"),
            ["left-ring-finger"] = (12, "Left Ring"),
        };

    private readonly LookupData lookups;
    private readonly string importRoot;

    public EorzeaCollectionImportService(IDataManager dataManager, string importRoot)
    {
        // Resolve game-data names once on plugin construction (the normal Dalamud
        // thread); pasted HTML can then be parsed without touching game data.
        lookups = BuildLookups(dataManager);
        this.importRoot = importRoot;
        Directory.CreateDirectory(importRoot);
    }

    /// <summary>
    /// The user explicitly copies one glamour page's HTML source from their
    /// browser and pastes it into GlamSpector. No network request is made.
    /// </summary>
    public async Task<EorzeaCollectionImportResult> ImportFromPageSourceAsync(
        string inputUrl,
        string pageSource,
        CancellationToken cancellationToken = default)
    {
        var pageUri = ValidateUrl(inputUrl, out var glamourId);
        if (string.IsNullOrWhiteSpace(pageSource))
            throw new InvalidOperationException("Paste the copied Eorzea Collection page source first.");

        // Character count is checked first to avoid accepting an obviously huge
        // clipboard value; the UTF-8 byte count is the authoritative limit.
        if (pageSource.Length > MaxPageBytes || Encoding.UTF8.GetByteCount(pageSource) > MaxPageBytes)
            throw new InvalidOperationException("The pasted Eorzea Collection page source is larger than GlamSpector's 4 MB safety limit.");

        return await ImportHtmlAsync(pageUri, glamourId, pageSource, cancellationToken).ConfigureAwait(false);
    }

    public string NormalizePageUrl(string inputUrl) => ValidateUrl(inputUrl, out _).AbsoluteUri;

    private async Task<EorzeaCollectionImportResult> ImportHtmlAsync(
        Uri pageUri,
        int glamourId,
        string html,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var title = ParseTitle(html) ?? $"Eorzea Collection Glamour #{glamourId}";
        var creator = ParseCreator(html);
        var warnings = new List<string>();

        var pieces = ParsePieces(html, lookups, warnings);
        var facewear = ParseFacewear(html, lookups);
        if (pieces.Count == 0 && facewear is null)
            throw new InvalidOperationException(
                "GlamSpector could read the supplied Eorzea Collection page source, but could not identify any equipment on it. " +
                "Make sure you copied the full page source (Ctrl+U, then Ctrl+A / Ctrl+C), not just the address or a small text selection.");

        var pageDirectory = Path.Combine(importRoot, glamourId.ToString());
        Directory.CreateDirectory(pageDirectory);
        var markerPath = Path.Combine(pageDirectory, $"{glamourId}.ecglam");
        var imagePaths = Directory
            .EnumerateFiles(pageDirectory, "source-*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        string cardPath;
        if (imagePaths.Count > 0)
        {
            cardPath = imagePaths[0];
            warnings.Add("Previously cached Eorzea Collection source images were retained; GlamSpector did not request remote images.");
        }
        else
        {
            cardPath = markerPath;
            if (!File.Exists(cardPath))
                await File.WriteAllTextAsync(cardPath, pageUri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
            warnings.Add("No local source image is available; the equipment recipe was imported without a picture.");
        }

        var snapshot = new GlamourSnapshot
        {
            CapturedAtUtc = DateTime.UtcNow,
            CharacterName = title,
            HomeWorld = "Eorzea Collection",
            Pieces = pieces,
            Facewear = facewear,
        };

        return new EorzeaCollectionImportResult
        {
            GlamourId = glamourId,
            SourceUrl = pageUri.AbsoluteUri,
            Title = title,
            Creator = creator,
            Snapshot = snapshot,
            CardPath = cardPath,
            SourceImagePaths = imagePaths,
            Warnings = warnings,
        };
    }

    private static Uri ValidateUrl(string inputUrl, out int glamourId)
    {
        if (!Uri.TryCreate(inputUrl?.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException("Paste a full Eorzea Collection glamour URL first.");

        var host = uri.Host.TrimEnd('.');
        if (!host.Equals("ffxiv.eorzeacollection.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("www.ffxiv.eorzeacollection.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("eorzeacollection.com", StringComparison.OrdinalIgnoreCase) &&
            !host.Equals("www.eorzeacollection.com", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("This importer only accepts Eorzea Collection glamour URLs.");

        var match = GlamourPathRegex.Match(uri.AbsolutePath);
        if (!match.Success || !int.TryParse(match.Groups["id"].Value, out glamourId))
            throw new InvalidOperationException("Expected an Eorzea Collection URL like /glamour/350011/petals-and-lace.");

        // Normalize to HTTPS and the canonical FFXIV host; preserve the supplied
        // path/slug but drop fragments and tracking query parameters.
        var builder = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1,
            Host = "ffxiv.eorzeacollection.com",
            Query = string.Empty,
            Fragment = string.Empty,
        };
        return builder.Uri;
    }

    private sealed class LookupData
    {
        public Dictionary<string, uint> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, byte> Stains { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, ushort> Glasses { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private static LookupData BuildLookups(IDataManager dataManager)
    {
        var result = new LookupData();
        foreach (var item in dataManager.GetExcelSheet<Item>())
        {
            var name = item.Name.ToString().Trim();
            if (name.Length == 0)
                continue;
            result.Items.TryAdd(name, item.RowId);
        }

        foreach (var stain in dataManager.GetExcelSheet<Stain>())
        {
            var name = stain.Name.ToString().Trim();
            if (name.Length == 0 || stain.RowId > byte.MaxValue)
                continue;
            result.Stains.TryAdd(name, checked((byte)stain.RowId));
        }

        foreach (var glasses in dataManager.GetExcelSheet<Glasses>())
        {
            var name = glasses.Name.ToString().Trim();
            if (name.Length == 0 || glasses.RowId > ushort.MaxValue)
                continue;
            result.Glasses.TryAdd(name, checked((ushort)glasses.RowId));
        }

        return result;
    }

    private List<GlamourPiece> ParsePieces(string html, LookupData lookups, List<string> warnings)
    {
        var found = new Dictionary<int, GlamourPiece>();

        // c-gear-slot-* is also used by descendant classes such as
        // c-gear-slot-item-name. Keep only actual equipment-slot class tokens
        // before using matches as section boundaries, otherwise the item-name
        // element itself can accidentally truncate its parent slot segment.
        var slotMatches = SlotClassRegex
            .Matches(html)
            .Cast<Match>()
            .Where(match => IsRecognizedSlotToken(match.Groups["slot"].Value))
            .ToList();

        for (var index = 0; index < slotMatches.Count; index++)
        {
            var match = slotMatches[index];
            var rawToken = match.Groups["slot"].Value;
            if (!TryMapSlotToken(rawToken, found.Keys, out var slot))
                continue;

            var start = match.Index;
            var end = index + 1 < slotMatches.Count ? slotMatches[index + 1].Index : Math.Min(html.Length, start + 8000);
            if (end <= start)
                continue;
            var segment = html[start..end];

            var itemName = ExtractClassValue(segment, ItemNameClassRegex) ??
                           FindFirstKnownValue(ExtractVisibleLines(segment), lookups.Items);
            if (string.IsNullOrWhiteSpace(itemName) || !lookups.Items.TryGetValue(itemName, out var itemId))
                continue;

            var dyes = FindStains(ExtractVisibleLines(segment), lookups.Stains);
            found[slot.RawSlot] = CreatePiece(slot.RawSlot, slot.Name, itemId, itemName, dyes);
        }

        // Current search-engine snapshots of EC expose the equipment in plain
        // text as "HEAD / item / dye" etc. Run this as a supplement even when
        // the historical class parser found some pieces: it only fills missing
        // slots, so mixed/new page layouts can still contribute accessories or
        // other sections without overwriting already-resolved gear.
        ParseVisibleTextFallback(html, lookups, found);

        if (found.Count == 0)
            return [];

        if (found.Values.Any(piece => piece.DisplayItemName.StartsWith("Item #", StringComparison.Ordinal)))
            warnings.Add("One or more Eorzea Collection item names could not be resolved in the local FFXIV Item sheet.");

        return found.Values.OrderBy(piece => piece.RawSlotIndex).ToList();
    }

    private void ParseVisibleTextFallback(string html, LookupData lookups, Dictionary<int, GlamourPiece> found)
    {
        var lines = ExtractVisibleLines(html);
        (int RawSlot, string Name)? currentSlot = null;
        var ringUseCount = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (TryMapVisibleSlotLabel(line, ref ringUseCount, out var mappedSlot))
            {
                currentSlot = mappedSlot;
                continue;
            }

            // FACE/FACEWEAR is parsed separately as a Glasses unlock rather than
            // a normal Item row. Do not let the previous equipment slot bleed
            // into that section.
            if (IsVisibleSlotBoundary(line))
            {
                currentSlot = null;
                continue;
            }

            if (currentSlot is null || !lookups.Items.TryGetValue(line, out var itemId))
                continue;

            var slot = currentSlot.Value;
            if (found.ContainsKey(slot.RawSlot))
                continue;

            var nearby = lines
                .Skip(i + 1)
                .TakeWhile(line => !IsVisibleSlotBoundary(line))
                .Take(12)
                .ToList();
            var dyes = FindStains(nearby, lookups.Stains);
            found[slot.RawSlot] = CreatePiece(slot.RawSlot, slot.Name, itemId, line, dyes);
        }
    }

    private FacewearDiagnostics? ParseFacewear(string html, LookupData lookups)
    {
        var lines = ExtractVisibleLines(html);
        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Equals("Facewear", StringComparison.OrdinalIgnoreCase) &&
                !lines[i].Equals("Glasses", StringComparison.OrdinalIgnoreCase) &&
                !lines[i].Equals("Face", StringComparison.OrdinalIgnoreCase))
                continue;

            foreach (var line in lines.Skip(i + 1).Take(8))
            {
                if (!lookups.Glasses.TryGetValue(line, out var id))
                    continue;
                return new FacewearDiagnostics
                {
                    GlassesId0 = id,
                    GlassesName0 = line,
                    Source = "eorzea-collection",
                };
            }
        }

        var classMatches = Regex.Matches(html, """c-gear-slot-(?:facewear|glasses|face)(?:[\s"']|$)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        foreach (Match classMatch in classMatches)
        {
            var segmentLength = Math.Min(5000, html.Length - classMatch.Index);
            var linesNear = ExtractVisibleLines(html.Substring(classMatch.Index, segmentLength));
            foreach (var line in linesNear)
            {
                if (lookups.Glasses.TryGetValue(line, out var id))
                    return new FacewearDiagnostics
                    {
                        GlassesId0 = id,
                        GlassesName0 = line,
                        Source = "eorzea-collection",
                    };
            }
        }

        return null;
    }

    private static GlamourPiece CreatePiece(
        int rawSlot,
        string slotName,
        uint itemId,
        string itemName,
        IReadOnlyList<(byte Id, string Name)> dyes)
    {
        var first = dyes.Count > 0 ? dyes[0] : default;
        var second = dyes.Count > 1 ? dyes[1] : default;
        return new GlamourPiece
        {
            RawSlotIndex = rawSlot,
            SlotName = slotName,
            EquippedItemId = itemId,
            GlamourItemId = 0,
            DisplayItemId = itemId,
            DisplayItemName = itemName,
            Stain1Id = first.Id,
            Stain1Name = first.Id != 0 ? first.Name : null,
            Stain2Id = second.Id,
            Stain2Name = second.Id != 0 ? second.Name : null,
        };
    }

    private static bool IsRecognizedSlotToken(string rawToken)
    {
        var token = rawToken.Trim().ToLowerInvariant();
        if (token.StartsWith("item-", StringComparison.Ordinal))
            token = token[5..];
        return SlotTokens.ContainsKey(token);
    }

    private static bool TryMapSlotToken(string rawToken, IEnumerable<int> existingSlots, out (int RawSlot, string Name) slot)
    {
        var token = rawToken.Trim().ToLowerInvariant();
        if (token.StartsWith("item-", StringComparison.Ordinal))
            token = token[5..];

        if (!SlotTokens.TryGetValue(token, out slot))
            return false;

        if (slot.RawSlot == 11 && existingSlots.Contains(11))
            slot = (12, "Left Ring");
        return true;
    }

    private static bool IsVisibleSlotBoundary(string line)
    {
        var normalized = line.Trim().TrimEnd(':').Replace("_", " ").ToUpperInvariant();
        return normalized is
            "WEAPON" or "MAIN HAND" or "MAINHAND" or
            "OFF HAND" or "OFFHAND" or "SHIELD" or
            "HEAD" or "BODY" or "CHEST" or "HANDS" or "LEGS" or "FEET" or
            "EARRINGS" or "EARS" or "NECKLACE" or "NECK" or
            "BRACELETS" or "WRISTS" or "RING" or "RINGS" or
            "RIGHT RING" or "LEFT RING" or
            "FACE" or "FACEWEAR" or "GLASSES";
    }

    private static bool TryMapVisibleSlotLabel(string line, ref int ringUseCount, out (int RawSlot, string Name) slot)
    {
        var normalized = line.Trim().TrimEnd(':').Replace("_", " ").ToUpperInvariant();
        slot = normalized switch
        {
            "WEAPON" or "MAIN HAND" or "MAINHAND" => (0, "Main Hand"),
            "OFF HAND" or "OFFHAND" or "SHIELD" => (1, "Off Hand"),
            "HEAD" => (2, "Head"),
            "BODY" or "CHEST" => (3, "Body"),
            "HANDS" => (4, "Hands"),
            "LEGS" => (6, "Legs"),
            "FEET" => (7, "Feet"),
            "EARRINGS" or "EARS" => (8, "Earrings"),
            "NECKLACE" or "NECK" => (9, "Necklace"),
            "BRACELETS" or "WRISTS" => (10, "Bracelets"),
            "RING" or "RINGS" => (++ringUseCount <= 1 ? (11, "Right Ring") : (12, "Left Ring")),
            "RIGHT RING" => (11, "Right Ring"),
            "LEFT RING" => (12, "Left Ring"),
            _ => default,
        };
        return slot.Name is not null;
    }

    private static List<(byte Id, string Name)> FindStains(IEnumerable<string> lines, IReadOnlyDictionary<string, byte> stains)
    {
        var result = new List<(byte, string)>(2);
        foreach (var raw in lines)
        {
            if (!TryParseDyeLine(raw, stains, out var dye))
                continue;

            // Keep channel order exactly as EC presents it. In particular,
            // "Undyed / Jet Black" must become stain1=0, stain2=Jet Black, and
            // the same dye is valid in both channels.
            result.Add(dye);
            if (result.Count == 2)
                break;
        }
        return result;
    }

    private static bool TryParseDyeLine(
        string raw,
        IReadOnlyDictionary<string, byte> stains,
        out (byte Id, string Name) dye)
    {
        var candidate = CollapseWhitespace(raw);
        candidate = candidate.TrimStart('⬤', '◯', '●', '○', '•', ' ', '\t');

        foreach (var prefix in new[] { "Dye 1:", "Dye 2:", "Dye:", "Color 1:", "Color 2:", "Colour 1:", "Colour 2:" })
        {
            if (candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                candidate = candidate[prefix.Length..].Trim();
                break;
            }
        }

        if (candidate.Equals("Undyed", StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            dye = (0, "Undyed");
            return true;
        }

        if (stains.TryGetValue(candidate, out var id) && id != 0)
        {
            dye = (id, candidate);
            return true;
        }

        dye = default;
        return false;
    }

    private static string? FindFirstKnownValue(IEnumerable<string> lines, IReadOnlyDictionary<string, uint> knownValues)
    {
        foreach (var line in lines)
        {
            if (knownValues.ContainsKey(line))
                return line;
        }
        return null;
    }

    private static string? ExtractClassValue(string segment, Regex regex)
    {
        var match = regex.Match(segment);
        return match.Success ? CleanText(match.Groups["value"].Value) : null;
    }

    private static List<string> ExtractVisibleLines(string html)
    {
        var withoutScripts = ScriptStyleRegex.Replace(html, "\n");
        var withBreaks = Regex.Replace(withoutScripts, @"</?(?:br|p|div|li|h[1-6]|section|article|span|a|dt|dd|tr|td|th)\b[^>]*>", "\n", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var text = TagRegex.Replace(withBreaks, " ");
        text = WebUtility.HtmlDecode(text);
        return text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CollapseWhitespace)
            .Where(line => line.Length > 0 && line.Length <= 180)
            .ToList();
    }

    private static string CleanText(string value) => CollapseWhitespace(WebUtility.HtmlDecode(TagRegex.Replace(value, " "))).Trim();

    private static string CollapseWhitespace(string value) =>
        Regex.Replace(value ?? string.Empty, @"\s+", " ", RegexOptions.CultureInvariant).Trim();

    private static string? ParseTitle(string html)
    {
        var title = GetMeta(html, "og:title") ?? GetMeta(html, "twitter:title");
        if (!string.IsNullOrWhiteSpace(title))
            return TrimSiteSuffix(title);

        var match = Regex.Match(html, @"<title[^>]*>(?<value>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
        return match.Success ? TrimSiteSuffix(CleanText(match.Groups["value"].Value)) : null;
    }

    private static string? ParseCreator(string html)
    {
        var author = GetMeta(html, "author");
        if (!string.IsNullOrWhiteSpace(author))
            return author.Trim();

        var description = GetMeta(html, "og:description") ?? GetMeta(html, "description");
        if (!string.IsNullOrWhiteSpace(description))
        {
            var descriptionMatch = ByCreatorRegex.Match(description);
            if (descriptionMatch.Success)
                return CollapseWhitespace(descriptionMatch.Groups["creator"].Value).Trim(' ', '«', '»', '\'', '"');
        }

        var visibleLines = ExtractVisibleLines(html);

        // Current EC pages expose a dedicated "Creator" label followed by a
        // display-name/server line such as "Klein Rider ⧫ Sargatanas". Prefer
        // the display name only; the original source URL remains stored too.
        for (var i = 0; i < visibleLines.Count - 1; i++)
        {
            if (!visibleLines[i].Equals("Creator", StringComparison.OrdinalIgnoreCase))
                continue;

            var candidate = visibleLines[i + 1];
            var serverSeparator = candidate.IndexOf('⧫');
            if (serverSeparator >= 0)
                candidate = candidate[..serverSeparator];
            candidate = CollapseWhitespace(candidate).Trim(' ', '«', '»', '\'', '"');
            if (candidate.Length is >= 2 and <= 80)
                return candidate;
        }

        var visible = string.Join("\n", visibleLines.Take(300));
        var match = ByCreatorRegex.Match(visible);
        return match.Success
            ? CollapseWhitespace(match.Groups["creator"].Value).Trim(' ', '«', '»', '\'', '"')
            : null;
    }

    private static string TrimSiteSuffix(string title)
    {
        foreach (var suffix in new[] { " | Eorzea Collection", " - Eorzea Collection", " — Eorzea Collection", " – Eorzea Collection" })
        {
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return title[..^suffix.Length].Trim();
        }
        return title.Trim();
    }

    private static string? GetMeta(string html, string requestedName)
    {
        foreach (Match match in MetaRegex.Matches(html))
        {
            var name = match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["name2"].Value;
            if (!name.Equals(requestedName, StringComparison.OrdinalIgnoreCase))
                continue;
            var content = match.Groups["content"].Success ? match.Groups["content"].Value : match.Groups["content2"].Value;
            return WebUtility.HtmlDecode(content).Trim();
        }
        return null;
    }

}
