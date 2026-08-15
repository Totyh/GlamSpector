using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GlamSpector.Models;
using SixLabors.Fonts;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Drawing;
using SixLabors.ImageSharp.Drawing.Processing;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace GlamSpector.Services;

public sealed class GlamCardRenderer
{
    private const int CardWidth = 1600;
    private const int CardHeight = 1000;

    private static readonly Color Panel = Color.FromRgb(24, 47, 62);
    private static readonly Color PanelDeep = Color.FromRgb(13, 27, 37);
    private static readonly Color Gold = Color.FromRgb(207, 170, 88);
    private static readonly Color GoldSoft = Color.FromRgb(161, 132, 72);
    private static readonly Color Text = Color.FromRgb(238, 235, 220);
    private static readonly Color Muted = Color.FromRgb(170, 188, 197);
    private static readonly Color Ribbon = Color.FromRgb(104, 43, 48);
    private static readonly Color Divider = Color.FromRgb(67, 91, 103);

    private readonly FontFamily fontFamily;

    public GlamCardRenderer()
    {
        // GlamSpector is Windows-only with XIVLauncher/Dalamud. Segoe UI gives us a
        // dependable system font without shipping any font files inside the plugin.
        fontFamily = SystemFonts.Get("Segoe UI");
    }

    public async Task<byte[]> RenderAsync(
        GlamourSnapshot snapshot,
        ReadOnlyMemory<byte> previewPng,
        bool cleanItemLevelOverlay,
        string? titleOverride = null,
        string? subtitleOverride = null,
        CancellationToken cancellationToken = default)
    {
        using var sourcePreview = Image.Load<Rgba32>(previewPng.ToArray());
        using var preview = PreparePreview(sourcePreview, cleanItemLevelOverlay);
        using var card = new Image<Rgba32>(CardWidth, CardHeight, new Rgba32(17, 35, 48));

        var previewDest = FitInside(preview.Width, preview.Height, new Rectangle(72, 188, 495, 730));
        using var scaledPreview = preview.Clone(x => x.Resize(previewDest.Width, previewDest.Height, KnownResamplers.Bicubic));

        card.Mutate(ctx =>
        {
            // Plate-inspired frame and header ribbon. This is intentionally our own
            // theme rather than a copy of an Adventurer Plate asset.
            FillRect(ctx, PanelDeep, 18, 18, CardWidth - 36, CardHeight - 36);
            DrawRect(ctx, Gold, 4f, 24, 24, CardWidth - 48, CardHeight - 48);
            DrawRect(ctx, GoldSoft, 1.5f, 35, 35, CardWidth - 70, CardHeight - 70);

            FillRect(ctx, Ribbon, 42, 38, CardWidth - 84, 104);
            DrawRect(ctx, GoldSoft, 2f, 42, 38, CardWidth - 84, 104);

            var headerTitle = string.IsNullOrWhiteSpace(titleOverride) ? snapshot.CharacterName : titleOverride!;
            DrawText(ctx, headerTitle, 66, 55, 43, FontStyle.Bold, Text, 700);
            var identityLine = !string.IsNullOrWhiteSpace(subtitleOverride)
                ? subtitleOverride!
                : string.IsNullOrWhiteSpace(snapshot.FreeCompanyName)
                    ? $"@ {snapshot.HomeWorld}"
                    : $"@ {snapshot.HomeWorld}   •   FC: {snapshot.FreeCompanyName}";
            DrawText(ctx, identityLine, 66, 105, 25, FontStyle.Regular, Muted, 1380);

            var previewPanel = new Rectangle(52, 168, 535, 770);
            FillRect(ctx, Panel, previewPanel.X, previewPanel.Y, previewPanel.Width, previewPanel.Height);
            DrawRect(ctx, GoldSoft, 2f, previewPanel.X, previewPanel.Y, previewPanel.Width, previewPanel.Height);

            ctx.DrawImage(scaledPreview, new Point(previewDest.X, previewDest.Y), 1f);
            DrawRect(ctx, Gold, 2f, previewDest.X, previewDest.Y, previewDest.Width, previewDest.Height);

            var rightPanel = new Rectangle(615, 168, 930, 770);
            FillRect(ctx, Panel, rightPanel.X, rightPanel.Y, rightPanel.Width, rightPanel.Height);
            DrawRect(ctx, GoldSoft, 2f, rightPanel.X, rightPanel.Y, rightPanel.Width, rightPanel.Height);

            DrawText(ctx, "GLAMOUR", 650, 192, 25, FontStyle.Bold, Gold, 800);
            FillRect(ctx, Divider, 650, 232, 860, 2);

            DrawMainGearSection(ctx, 650, 258, 405, snapshot);

            DrawSection(ctx, "ACCESSORIES", 1080, 258, 410, snapshot.Pieces,
                ["Earrings", "Necklace", "Bracelets", "Right Ring", "Left Ring"]);

            FillRect(ctx, Divider, 650, 760, 840, 2);
            DrawText(ctx, "WEAPONS", 650, 784, 22, FontStyle.Bold, Gold, 400);
            DrawWeapon(ctx, Find(snapshot.Pieces, "Main Hand"), "Main Hand", 650, 826, 400);
            DrawWeapon(ctx, Find(snapshot.Pieces, "Off Hand"), "Off Hand", 1080, 826, 410);
        });

        await using var output = new MemoryStream();
        await card.SaveAsPngAsync(output, cancellationToken);
        return output.ToArray();
    }

    private static Image<Rgba32> PreparePreview(Image<Rgba32> source, bool cleanItemLevelOverlay)
    {
        // M2.6 deliberately removes the native silver Inspect-preview frame.
        // GlamSpector already draws its own gold frame around the portrait on the
        // final card, and keeping the game frame made ilvl-overlay cleanup fragile.
        // Working with only the portrait/background gives us a much more robust
        // cleanup and a cleaner, single-frame result.
        var trimBottom = Math.Clamp((int)MathF.Round(source.Height * 0.018f), 0, Math.Max(0, source.Height - 1));
        var workingHeight = Math.Max(1, source.Height - trimBottom);
        using var working = source.Clone(ctx => ctx.Crop(new Rectangle(0, 0, source.Width, workingHeight)));

        if (cleanItemLevelOverlay && working.Width > 40 && working.Height > 40)
        {
            // Remove the external ilvl icon/digits by extending a nearby clean
            // background sample across the full contaminated top-right region.
            // Because the native silver frame is cropped away afterwards, there is
            // no need to restore/mirror any frame pixels and therefore no path for
            // cyan digit fragments to leak back into the final card.
            var left = Math.Clamp((int)MathF.Round(working.Width * 0.545f), 0, working.Width - 1);
            var bottom = Math.Clamp((int)MathF.Round(working.Height * 0.075f), 1, working.Height);
            var sampleX = Math.Clamp((int)MathF.Round(working.Width * 0.505f), 0, Math.Max(0, left - 1));

            for (var y = 0; y < bottom; y++)
            {
                var sample = working[sampleX, y];
                for (var x = left; x < working.Width; x++)
                    working[x, y] = sample;
            }
        }

        // Strip the game's native silver preview frame from all four sides.
        // The values are proportional so this continues to behave sensibly across
        // UI scale / resolution changes while leaving almost all portrait pixels.
        // The native Inspect border is noticeably thicker on the left/right than the
        // top/bottom. 2.6% still left a sliver at some UI scales, so crop a
        // little more aggressively horizontally. This remains well inside the
        // empty portrait margin and does not touch a normally-centered character.
        var insetX = Math.Clamp((int)MathF.Round(working.Width * 0.050f), 10, Math.Max(10, working.Width / 8));
        var insetTop = Math.Clamp((int)MathF.Round(working.Height * 0.018f), 6, Math.Max(6, working.Height / 8));
        var insetBottom = Math.Clamp((int)MathF.Round(working.Height * 0.012f), 4, Math.Max(4, working.Height / 8));

        var cropWidth = Math.Max(1, working.Width - insetX * 2);
        var cropHeight = Math.Max(1, working.Height - insetTop - insetBottom);

        return working.Clone(ctx => ctx.Crop(new Rectangle(insetX, insetTop, cropWidth, cropHeight)));
    }

    private static void FillRect(IImageProcessingContext ctx, Color color, float x, float y, float width, float height)
    {
        var path = new RectangularPolygon(x, y, width, height);
        ctx.Fill(color, path);
    }

    private static void DrawRect(IImageProcessingContext ctx, Color color, float thickness, float x, float y, float width, float height)
    {
        var path = new RectangularPolygon(x, y, width, height);
        ctx.Draw(color, thickness, path);
    }


    private void DrawMainGearSection(
        IImageProcessingContext ctx,
        float x,
        float y,
        float width,
        GlamourSnapshot snapshot)
    {
        DrawText(ctx, "MAIN GEAR", x, y, 21, FontStyle.Bold, Gold, width);
        var rowY = y + 43;
        var facewearName = snapshot.Facewear?.DisplayName;

        foreach (var slot in new[] { "Head", "Body", "Hands", "Legs", "Feet" })
        {
            var piece = Find(snapshot.Pieces, slot);
            DrawPiece(ctx, piece, slot, x, rowY, width);

            if (slot == "Head" && !string.IsNullOrWhiteSpace(facewearName))
            {
                // Keep Facewear visually subordinate to the normal Head item and
                // omit it entirely when none is equipped. If the head item is dyed,
                // place Facewear on the next line so both bits of information survive.
                var hasHeadDye = piece is not null && !string.IsNullOrEmpty(BuildDyeLine(piece));
                var facewearY = rowY + (hasHeadDye ? 72 : 53);
                var facewear = FitText($"Facewear: {facewearName}", width, 14, 12, FontStyle.Regular);
                DrawText(ctx, facewear.Text, x, facewearY, facewear.Size, FontStyle.Regular, Muted, width);
                rowY += hasHeadDye ? 106 : 92;
            }
            else
            {
                rowY += 87;
            }
        }
    }

    private void DrawSection(
        IImageProcessingContext ctx,
        string title,
        float x,
        float y,
        float width,
        IReadOnlyList<GlamourPiece> pieces,
        IReadOnlyList<string> slots)
    {
        DrawText(ctx, title, x, y, 21, FontStyle.Bold, Gold, width);
        var rowY = y + 43;

        foreach (var slot in slots)
        {
            DrawPiece(ctx, Find(pieces, slot), slot, x, rowY, width);
            rowY += 87;
        }
    }

    private void DrawPiece(
        IImageProcessingContext ctx,
        GlamourPiece? piece,
        string slot,
        float x,
        float y,
        float width)
    {
        DrawText(ctx, slot, x, y, 16, FontStyle.Bold, Muted, width);

        if (piece is null)
        {
            DrawText(ctx, "—", x, y + 24, 22, FontStyle.Regular, Text, width);
            return;
        }

        var name = FitText(piece.DisplayItemName, width, 23, 15, FontStyle.Regular);
        DrawText(ctx, name.Text, x, y + 23, name.Size, FontStyle.Regular, Text, width);

        var dye = BuildDyeLine(piece);
        if (!string.IsNullOrEmpty(dye))
            DrawText(ctx, dye, x, y + 53, 15, FontStyle.Regular, Muted, width);
    }

    private void DrawWeapon(
        IImageProcessingContext ctx,
        GlamourPiece? piece,
        string slot,
        float x,
        float y,
        float width)
    {
        DrawText(ctx, slot, x, y, 16, FontStyle.Bold, Muted, width);
        if (piece is null)
        {
            DrawText(ctx, "—", x, y + 27, 22, FontStyle.Regular, Text, width);
            return;
        }

        var name = FitText(piece.DisplayItemName, width, 23, 15, FontStyle.Regular);
        DrawText(ctx, name.Text, x, y + 27, name.Size, FontStyle.Regular, Text, width);

        var dye = BuildDyeLine(piece);
        if (!string.IsNullOrEmpty(dye))
            DrawText(ctx, dye, x, y + 57, 15, FontStyle.Regular, Muted, width);
    }

    private void DrawText(
        IImageProcessingContext ctx,
        string text,
        float x,
        float y,
        float size,
        FontStyle style,
        Color color,
        float maxWidth)
    {
        var fitted = FitText(text, maxWidth, size, Math.Min(12, size), style);
        var font = fontFamily.CreateFont(fitted.Size, style);
        ctx.DrawText(fitted.Text, font, color, new PointF(x, y));
    }

    private (string Text, float Size) FitText(
        string text,
        float maxWidth,
        float preferredSize,
        float minimumSize,
        FontStyle style)
    {
        var size = preferredSize;
        while (size > minimumSize)
        {
            var font = fontFamily.CreateFont(size, style);
            if (TextMeasurer.MeasureAdvance(text, new TextOptions(font)).Width <= maxWidth)
                return (text, size);
            size -= 1f;
        }

        var minimumFont = fontFamily.CreateFont(minimumSize, style);
        if (TextMeasurer.MeasureAdvance(text, new TextOptions(minimumFont)).Width <= maxWidth)
            return (text, minimumSize);

        var candidate = text;
        while (candidate.Length > 3)
        {
            candidate = candidate[..^1];
            var ellipsized = candidate.TrimEnd() + "…";
            if (TextMeasurer.MeasureAdvance(ellipsized, new TextOptions(minimumFont)).Width <= maxWidth)
                return (ellipsized, minimumSize);
        }

        return ("…", minimumSize);
    }

    private static GlamourPiece? Find(IEnumerable<GlamourPiece> pieces, string slot) =>
        pieces.FirstOrDefault(x => x.SlotName.Equals(slot, StringComparison.Ordinal));

    private static string? BuildDyeLine(GlamourPiece piece)
    {
        var first = piece.Stain1Id != 0 ? piece.Stain1Name ?? $"Dye #{piece.Stain1Id}" : null;
        var second = piece.Stain2Id != 0 ? piece.Stain2Name ?? $"Dye #{piece.Stain2Id}" : null;

        if (first is null && second is null)
            return null;
        if (first is not null && second is not null)
            return $"Dyes: {first} / {second}";
        return $"Dye: {first ?? second}";
    }

    private static Rectangle FitInside(int sourceWidth, int sourceHeight, Rectangle bounds)
    {
        var scale = Math.Min(bounds.Width / (float)sourceWidth, bounds.Height / (float)sourceHeight);
        var width = Math.Max(1, (int)MathF.Round(sourceWidth * scale));
        var height = Math.Max(1, (int)MathF.Round(sourceHeight * scale));
        return new Rectangle(
            bounds.X + (bounds.Width - width) / 2,
            bounds.Y + (bounds.Height - height) / 2,
            width,
            height);
    }
}
