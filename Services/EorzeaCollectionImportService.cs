using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using GlamSpector.Models;
using Lumina.Excel.Sheets;
using SixLabors.ImageSharp;

namespace GlamSpector.Services;

/// <summary>
/// Imports one user-supplied Eorzea Collection glamour page at a time. It does
/// not crawl catalogue/search pages. The importer uses the page's public HTML,
/// resolves equipment/dyes against the local FFXIV sheets and stores copies of
/// the glamour images in GlamSpector's plugin-data directory.
/// </summary>
public sealed class EorzeaCollectionImportService : IDisposable
{
    private const int MaxPageBytes = 4 * 1024 * 1024;
    private const int MaxImageBytes = 20 * 1024 * 1024;
    private const int MaxImageCandidates = 24;
    private const int MaxSavedImages = 8;

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

    private static readonly Regex AttributeUrlRegex = new(
        """(?:src|data-src|data-lazy-src)\s*=\s*["'](?<url>[^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex SrcSetRegex = new(
        """(?:srcset|data-srcset)\s*=\s*["'](?<value>[^"']+)["']""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex EmbeddedImageUrlRegex = new(
        """https?:\\?/\\?/[^\s"'<>]+?(?:\.jpe?g|\.png|\.webp|\.avif)(?:\?[^\s"'<>]*)?""",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

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
    private readonly HttpClient httpClient;

    public EorzeaCollectionImportService(IDataManager dataManager, string importRoot)
    {
        // Resolve game-data names once on plugin construction (the normal Dalamud
        // thread) so the asynchronous HTTP continuation never needs to touch an
        // IDataManager service from a worker thread.
        lookups = BuildLookups(dataManager);
        this.importRoot = importRoot;
        Directory.CreateDirectory(importRoot);

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
        };
        httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(25),
        };

        // Identify the feature while still using a browser-compatible UA shape;
        // some sites reject the default .NET user agent before serving HTML.
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
            "Chrome/139.0 Safari/537.36 GlamSpector/0.3.12.1");
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
    }

    public void Dispose() => httpClient.Dispose();

    public async Task<EorzeaCollectionImportResult> ImportAsync(string inputUrl, CancellationToken cancellationToken = default)
    {
        var pageUri = ValidateUrl(inputUrl, out var glamourId);
        var html = await DownloadPageAsync(pageUri, cancellationToken).ConfigureAwait(false);
        return await ImportHtmlAsync(pageUri, glamourId, html, browserFallback: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fallback for sites that serve the page to the user's normal browser but
    /// reject non-browser HTTP clients. The user explicitly copies that one
    /// glamour page's HTML source from their browser and pastes it into
    /// GlamSpector. No attempt is made to bypass the site's request filtering.
    /// </summary>
    public async Task<EorzeaCollectionImportResult> ImportFromPageSourceAsync(
        string inputUrl,
        string pageSource,
        CancellationToken cancellationToken = default)
    {
        var pageUri = ValidateUrl(inputUrl, out var glamourId);
        if (string.IsNullOrWhiteSpace(pageSource))
            throw new InvalidOperationException("Paste the copied Eorzea Collection page source first.");

        // Keep the same safety ceiling as the direct HTTP path. Character count
        // is checked first to avoid accepting an obviously huge clipboard value;
        // the UTF-8 byte count is the authoritative limit.
        if (pageSource.Length > MaxPageBytes || Encoding.UTF8.GetByteCount(pageSource) > MaxPageBytes)
            throw new InvalidOperationException("The pasted Eorzea Collection page source is larger than GlamSpector's 4 MB safety limit.");

        return await ImportHtmlAsync(pageUri, glamourId, pageSource, browserFallback: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<EorzeaCollectionImportResult> ImportHtmlAsync(
        Uri pageUri,
        int glamourId,
        string html,
        bool browserFallback,
        CancellationToken cancellationToken)
    {
        var title = ParseTitle(html) ?? $"Eorzea Collection Glamour #{glamourId}";
        var creator = ParseCreator(html);
        var warnings = new List<string>();
        if (browserFallback)
            warnings.Add("Imported from page source copied from your browser because direct plugin page requests are blocked by Eorzea Collection.");

        var pieces = ParsePieces(html, lookups, warnings);
        var facewear = ParseFacewear(html, lookups);
        if (pieces.Count == 0 && facewear is null)
            throw new InvalidOperationException(
                "GlamSpector could read the supplied Eorzea Collection page source, but could not identify any equipment on it. " +
                "Make sure you copied the full page source (Ctrl+U, then Ctrl+A / Ctrl+C), not just the address or a small text selection.");

        var pageDirectory = Path.Combine(importRoot, glamourId.ToString());
        Directory.CreateDirectory(pageDirectory);
        var markerPath = Path.Combine(pageDirectory, $"{glamourId}.ecglam");
        var previouslyCachedImages = Directory
            .EnumerateFiles(pageDirectory, "source-*.png", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var imageUrls = ExtractImageUrls(html, pageUri);
        var imagePaths = await DownloadImagesAsync(imageUrls, pageUri, pageDirectory, cancellationToken).ConfigureAwait(false);

        string cardPath;
        if (imagePaths.Count > 0)
        {
            cardPath = imagePaths[0];

            // A successful refresh becomes the new authoritative local gallery.
            // Delete only old numbered files that are no longer part of it. This
            // happens after downloading so a transient network failure cannot wipe
            // a previously useful cached import.
            var keep = imagePaths
                .Select(Path.GetFullPath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var staleImage in previouslyCachedImages)
            {
                if (keep.Contains(Path.GetFullPath(staleImage)))
                    continue;
                try { File.Delete(staleImage); } catch { }
            }
            try { if (File.Exists(markerPath)) File.Delete(markerPath); } catch { }
        }
        else if (previouslyCachedImages.Count > 0)
        {
            imagePaths = previouslyCachedImages;
            cardPath = imagePaths[0];
            warnings.Add("The source pictures could not be refreshed, so GlamSpector kept the previously cached copies for this Eorzea Collection page.");
        }
        else
        {
            cardPath = markerPath;
            await File.WriteAllTextAsync(cardPath, pageUri.AbsoluteUri, cancellationToken).ConfigureAwait(false);
            warnings.Add("No source image could be downloaded; the equipment recipe was imported without a picture.");
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

    private async Task<string> DownloadPageAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Referrer = new Uri("https://ffxiv.eorzeacollection.com/");
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new InvalidOperationException(
                "Eorzea Collection returned HTTP 403 to the direct plugin request. The page may still work normally in your browser. " +
                "Use the Browser fallback in Import EC: open the same page in your browser, press Ctrl+U, Ctrl+A, Ctrl+C, then paste the page source into GlamSpector.");

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxPageBytes)
            throw new InvalidOperationException("The Eorzea Collection page response was unexpectedly large, so the import was stopped.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > MaxPageBytes)
                throw new InvalidOperationException("The Eorzea Collection page response exceeded GlamSpector's safety limit.");
            memory.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(memory.ToArray());
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

    private static List<Uri> ExtractImageUrls(string html, Uri pageUri)
    {
        var result = new List<Uri>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var forced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? raw, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return;
            raw = WebUtility.HtmlDecode(raw.Trim());
            if (raw.StartsWith("//", StringComparison.Ordinal))
                raw = "https:" + raw;
            if (!Uri.TryCreate(pageUri, raw, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return;

            var path = uri.AbsolutePath.ToLowerInvariant();
            var looksLikeImage = path.EndsWith(".jpg") || path.EndsWith(".jpeg") || path.EndsWith(".png") ||
                                 path.EndsWith(".webp") || path.EndsWith(".avif") || path.Contains("/image") ||
                                 path.Contains("/glamour");
            if (!force && !looksLikeImage)
                return;
            if (path.Contains("logo") || path.Contains("favicon") || path.Contains("icon-") || path.Contains("avatar"))
                return;

            var key = uri.GetLeftPart(UriPartial.Path);
            if (force)
                forced.Add(key);
            if (seen.Add(key))
                result.Add(uri);
        }

        Add(GetMeta(html, "og:image"), force: true);
        Add(GetMeta(html, "twitter:image"), force: true);

        // Prefer the largest srcset candidate before the ordinary <img src>
        // thumbnail. Many responsive sites list srcset widths from small to big.
        foreach (Match match in SrcSetRegex.Matches(html))
        {
            var candidates = match.Groups["value"].Value.Split(',');
            foreach (var candidate in candidates.Reverse())
            {
                var url = candidate.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                Add(url);
            }
        }

        foreach (Match match in AttributeUrlRegex.Matches(html))
            Add(match.Groups["url"].Value);

        // Some frameworks serialize gallery URLs inside page-state JSON with
        // escaped slashes rather than normal HTML attributes.
        foreach (Match match in EmbeddedImageUrlRegex.Matches(html))
            Add(match.Value.Replace("\\/", "/"));

        // Keep the explicit social-preview image first, then favour EC's glamour
        // image hosts/paths over generic page graphics. This prevents a busy page
        // full of small gear icons from consuming the candidate budget before the
        // rest of the submitted gallery is reached.
        return result
            .Select((uri, index) => new { Uri = uri, Index = index, Key = uri.GetLeftPart(UriPartial.Path) })
            .OrderBy(candidate => forced.Contains(candidate.Key) ? 0 : ImageCandidatePriority(candidate.Uri))
            .ThenBy(candidate => candidate.Index)
            .Take(MaxImageCandidates)
            .Select(candidate => candidate.Uri)
            .ToList();
    }

    private static int ImageCandidatePriority(Uri uri)
    {
        if (uri.Host.StartsWith("cam.", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (uri.AbsolutePath.Contains("/glamour", StringComparison.OrdinalIgnoreCase))
            return 2;
        return 3;
    }

    private async Task<List<string>> DownloadImagesAsync(
        IReadOnlyList<Uri> imageUrls,
        Uri pageUri,
        string pageDirectory,
        CancellationToken cancellationToken)
    {
        var results = new List<string>();
        var hashes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var imageUri in imageUrls)
        {
            if (results.Count >= MaxSavedImages)
                break;

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, imageUri);
                request.Headers.Referrer = pageUri;
                request.Headers.Accept.ParseAdd("image/avif,image/webp,image/apng,image/svg+xml,image/*,*/*;q=0.8");
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    continue;
                if (response.Content.Headers.ContentLength is > MaxImageBytes)
                    continue;

                var bytes = await ReadLimitedBytesAsync(response.Content, MaxImageBytes, cancellationToken).ConfigureAwait(false);
                if (bytes.Length == 0)
                    continue;

                var hash = Convert.ToHexString(SHA256.HashData(bytes));
                if (!hashes.Add(hash))
                    continue;

                using var image = Image.Load(bytes);
                // Ignore logos, avatars and tiny thumbnails. EC glamour photos
                // are comfortably above this size in normal use.
                if (image.Width < 480 || image.Height < 360)
                    continue;

                var path = Path.Combine(pageDirectory, $"source-{results.Count + 1:00}.png");
                image.SaveAsPng(path);
                results.Add(path);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // A single gallery image being unavailable should not prevent
                // importing the rest of the glamour page.
            }
        }

        return results;
    }

    private static async Task<byte[]> ReadLimitedBytesAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var memory = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (memory.Length + read > limit)
                return [];
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }
}
