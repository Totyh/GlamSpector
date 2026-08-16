using Dalamud.Bindings.ImGui;

namespace GlamSpector.UI;

public sealed class ConfigUi
{
    private readonly Configuration configuration;
    private bool isOpen;
    private string outputDirectory;

    public ConfigUi(Configuration configuration)
    {
        this.configuration = configuration;
        outputDirectory = configuration.OutputDirectory;
    }

    public void Toggle() => isOpen = !isOpen;
    public void Open() => isOpen = true;

    public void Draw()
    {
        if (!isOpen)
            return;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(700, 620), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("GlamSpector Settings###GlamSpectorConfig", ref isOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.TextWrapped("GlamSpector captures inspected glamours into a preview-first Library. M3.15 adds editable local Library titles and remembers useful Library layout/filter state, while source identity, full cards, personal previews, generated share cards, Adventurer Plates, ownership and Wanted data remain attached to each entry.");
        ImGui.Spacing();

        ImGui.InputText("Output folder", ref outputDirectory, 1024);

        var copy = configuration.CopyToClipboard;
        if (ImGui.Checkbox("Copy final Glam Card to clipboard", ref copy))
            configuration.CopyToClipboard = copy;

        var autoLibrary = configuration.AutoAddToLibrary;
        if (ImGui.Checkbox("Automatically add successful captures to Library", ref autoLibrary))
            configuration.AutoAddToLibrary = autoLibrary;

        var cleanup = configuration.CleanupItemLevelOverlay;
        if (ImGui.Checkbox("Remove top-right item-level overlay from preview", ref cleanup))
            configuration.CleanupItemLevelOverlay = cleanup;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Post-processes the small top-right ilvl area in the portrait shared by the Full Card and automatic Inspect Preview. Disable this if the character, hair, hat, or weapon overlaps the ilvl digits.");

        var raw = configuration.SaveRawPreview;
        if (ImGui.Checkbox("Also save Inspect preview PNG for non-Library captures", ref raw))
            configuration.SaveRawPreview = raw;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("M3.14 Library captures always keep the Inspect preview because it is the default Library image. This option only controls extra raw-preview files when automatic Library indexing is disabled.");

        var writeJson = configuration.WriteDiagnosticJson;
        if (ImGui.Checkbox("Write diagnostic glamour JSON", ref writeJson))
            configuration.WriteDiagnosticJson = writeJson;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Optional diagnostic sidecar. New library entries do not require JSON; keep it enabled only for debugging or if you want portable metadata files.");

        var padding = configuration.CropPaddingPixels;
        if (ImGui.SliderFloat("Preview crop padding", ref padding, 0f, 40f, "%.0f px"))
            configuration.CropPaddingPixels = padding;

        var raiseInspect = configuration.BringInspectToFrontBeforeCapture;
        if (ImGui.Checkbox("Bring Inspect window to front before capture", ref raiseInspect))
            configuration.BringInspectToFrontBeforeCapture = raiseInspect;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Uses FFXIV's native addon focus on the game framework thread before sampling the character preview, then waits two frames. This targets native FFXIV windows only; GlamSpector never manipulates external Windows applications.");

        var hideOwnWindows = configuration.HideGlamSpectorWindowsDuringCapture;
        if (ImGui.Checkbox("Temporarily hide GlamSpector windows during capture", ref hideOwnWindows))
            configuration.HideGlamSpectorWindowsDuringCapture = hideOwnWindows;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Optional. The Inspect preview is captured before Dalamud ImGui rendering, so GlamSpector windows normally do not need to be hidden. Leave this off unless you specifically prefer the cleaner capture animation.");

        ImGui.Separator();
        ImGui.TextUnformatted("Adventurer Plate capture");

        ImGui.SetNextItemWidth(220f);
        var currentPlateModeLabel = configuration.AdventurerPlateCaptureMode switch
        {
            AdventurerPlateCaptureMode.Off => "Off",
            AdventurerPlateCaptureMode.Ask => "Ask after Glam Card",
            _ => "Automatic",
        };
        if (ImGui.BeginCombo("Auto-capture Adventurer Plate", currentPlateModeLabel))
        {
            foreach (var mode in System.Enum.GetValues<AdventurerPlateCaptureMode>())
            {
                var label = mode switch
                {
                    AdventurerPlateCaptureMode.Off => "Off",
                    AdventurerPlateCaptureMode.Ask => "Ask after Glam Card",
                    _ => "Automatic",
                };
                var selected = mode == configuration.AdventurerPlateCaptureMode;
                if (ImGui.Selectable(label, selected))
                    configuration.AdventurerPlateCaptureMode = mode;
                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("After the Glam Card is safely captured, GlamSpector can ask the game to open the inspected character's Adventurer Plate and attach it to the same Library entry. Plate failure never cancels the Glam Card.");

        ImGui.BeginDisabled(configuration.AdventurerPlateCaptureMode == AdventurerPlateCaptureMode.Off);

        var closePlate = configuration.CloseAutoOpenedAdventurerPlate;
        if (ImGui.Checkbox("Close Plate after automatic capture", ref closePlate))
            configuration.CloseAutoOpenedAdventurerPlate = closePlate;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only closes a Plate that GlamSpector opened itself. A Plate you already had open is left alone.");

        var recipe = configuration.CapturePortraitRecipeWithPlate;
        if (ImGui.Checkbox("Capture portrait recipe with Plate", ref recipe))
            configuration.CapturePortraitRecipeWithPlate = recipe;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Stores the read-only camera/lighting/background/exported portrait data used by the future portrait-preset companion plugin.");

        var timeout = configuration.AdventurerPlateTimeoutSeconds;
        if (ImGui.SliderFloat("Plate loading timeout", ref timeout, 1f, 10f, "%.1f s"))
            configuration.AdventurerPlateTimeoutSeconds = timeout;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Maximum time to wait for the Plate data and portrait texture to become available. The render-settle delay below starts only after the Plate is ready.");

        var settle = configuration.AdventurerPlateSettleSeconds;
        if (ImGui.SliderFloat("Plate render settle time", ref settle, 0.25f, 3f, "%.2f s"))
            configuration.AdventurerPlateSettleSeconds = settle;
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Keeps the automatically opened Plate visible after its data is ready before GlamSpector captures it. Plate sampling happens before Dalamud ImGui rendering, so GlamSpector and other plugin windows should not appear in the saved Plate. Increase this if auto-captures show the world behind the Plate instead.");

        ImGui.EndDisabled();

        if (!configuration.AutoAddToLibrary && configuration.AdventurerPlateCaptureMode != AdventurerPlateCaptureMode.Off)
            ImGui.TextDisabled("Auto Plate attachment requires 'Automatically add successful captures to Library'.");

        ImGui.Separator();
        ImGui.TextUnformatted("Chat notifications");

        var notifyCapture = configuration.NotifyCaptureSuccess;
        if (ImGui.Checkbox("Glam Card capture success", ref notifyCapture))
            configuration.NotifyCaptureSuccess = notifyCapture;

        var notifyPlate = configuration.NotifyAdventurerPlate;
        if (ImGui.Checkbox("Adventurer Plate success / unavailable", ref notifyPlate))
            configuration.NotifyAdventurerPlate = notifyPlate;

        var notifyDelete = configuration.NotifyDelete;
        if (ImGui.Checkbox("Library remove / delete", ref notifyDelete))
            configuration.NotifyDelete = notifyDelete;

        var notifyImportExport = configuration.NotifyImportExport;
        if (ImGui.Checkbox("Import / export", ref notifyImportExport))
            configuration.NotifyImportExport = notifyImportExport;

        var notifyClipboard = configuration.NotifyClipboard;
        if (ImGui.Checkbox("Copy to clipboard", ref notifyClipboard))
            configuration.NotifyClipboard = notifyClipboard;

        ImGui.TextDisabled("Glam Card capture errors are always shown. Automatic Plate failures can be silenced independently above.");

        ImGui.Spacing();
        if (ImGui.Button("Save settings"))
        {
            configuration.OutputDirectory = outputDirectory.Trim();
            configuration.AdventurerPlateTimeoutSeconds = System.Math.Clamp(configuration.AdventurerPlateTimeoutSeconds, 1f, 10f);
            configuration.AdventurerPlateSettleSeconds = System.Math.Clamp(configuration.AdventurerPlateSettleSeconds, 0.25f, 3f);
            configuration.Save();
        }

        ImGui.End();
    }
}
