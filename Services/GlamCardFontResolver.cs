using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SixLabors.Fonts;

namespace GlamSpector.Services;

internal static class GlamCardFontResolver
{
    internal const string BundledFamilyName = "Noto Sans";
    internal const string BundledRegularResourceName =
        "GlamSpector.Assets.Fonts.NotoSans-Regular.ttf";
    internal const string BundledBoldResourceName =
        "GlamSpector.Assets.Fonts.NotoSans-Bold.ttf";

    internal static IReadOnlyList<string> PreferredFamilyNames { get; } =
        ["Segoe UI", "Arial", "Tahoma", "Verdana"];

    public static FontFamily Resolve()
    {
        Exception? systemFontFailure = null;
        try
        {
            var selected = SelectFamily(SystemFonts.Families);
            if (selected is not null)
                return selected.Value;
        }
        catch (Exception ex)
        {
            // System-font discovery is optional. Wine and other environments can
            // expose no usable families (or fail while enumerating them), so keep
            // the packaged fallback independent from this path.
            systemFontFailure = ex;
        }

        try
        {
            return LoadBundledFamily(OpenBundledResource);
        }
        catch (Exception ex)
        {
            throw CreateBundledFontException(ex, systemFontFailure);
        }
    }

    internal static FontFamily? SelectFamily(IEnumerable<FontFamily> families)
    {
        var usableFamilies = families
            .Where(IsUsable)
            .ToList();

        foreach (var preferredName in PreferredFamilyNames)
        {
            foreach (var family in usableFamilies)
            {
                if (string.Equals(family.Name, preferredName, StringComparison.OrdinalIgnoreCase))
                    return family;
            }
        }

        // Preserve SixLabors' exposed order after the explicit deterministic
        // preferences: the first remaining usable family is the final fallback.
        return usableFamilies.Count > 0 ? usableFamilies[0] : null;
    }

    internal static FontFamily LoadBundledFamily(Func<string, Stream?> openResource)
    {
        ArgumentNullException.ThrowIfNull(openResource);

        using var regular = openResource(BundledRegularResourceName)
            ?? throw new InvalidDataException(
                $"Packaged font resource '{BundledRegularResourceName}' is missing.");
        using var bold = openResource(BundledBoldResourceName)
            ?? throw new InvalidDataException(
                $"Packaged font resource '{BundledBoldResourceName}' is missing.");

        var collection = new FontCollection();
        collection.Add(regular);
        collection.Add(bold);

        if (!collection.TryGet(BundledFamilyName, out var family) || !IsUsable(family))
        {
            throw new InvalidDataException(
                $"Packaged font resources did not provide usable {BundledFamilyName} Regular and Bold faces.");
        }

        return family;
    }

    private static Stream? OpenBundledResource(string resourceName) =>
        typeof(GlamCardFontResolver).Assembly.GetManifestResourceStream(resourceName);

    private static bool IsUsable(FontFamily family)
    {
        try
        {
            _ = family.CreateFont(12, FontStyle.Regular);
            _ = family.CreateFont(12, FontStyle.Bold);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static InvalidOperationException CreateBundledFontException(
        Exception bundledFontFailure,
        Exception? systemFontFailure)
    {
        const string message =
            "GlamSpector could not render the card because its packaged Noto Sans fallback font " +
            "could not be loaded. Reinstall or repair the GlamSpector plugin package.";
        var innerException = systemFontFailure is null
            ? bundledFontFailure
            : new AggregateException(systemFontFailure, bundledFontFailure);
        return new InvalidOperationException(message, innerException);
    }
}
