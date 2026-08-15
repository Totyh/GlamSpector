using System;
using System.IO;
using System.Linq;
using NumericsVector2 = System.Numerics.Vector2;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Common.Math;
using FFXIVClientStructs.FFXIV.Component.GUI;
using GlamSpector.Models;

namespace GlamSpector.Services;

public sealed class PreviewCaptureService
{
    private readonly IGameGui gameGui;
    private readonly ITextureProvider textureProvider;
    private readonly ITextureReadbackProvider readbackProvider;

    public PreviewCaptureService(
        IGameGui gameGui,
        ITextureProvider textureProvider,
        ITextureReadbackProvider readbackProvider)
    {
        this.gameGui = gameGui;
        this.textureProvider = textureProvider;
        this.readbackProvider = readbackProvider;
    }

    public unsafe CaptureRequest BeginCapture(float paddingPixels)
    {
        var addon = gameGui.GetAddonByName<AddonCharacterInspect>("CharacterInspect");
        if (addon == null)
            throw new InvalidOperationException("The Inspect window is not open.");

        AtkResNode* previewNode = null;
        var boundsSource = string.Empty;

        var component = addon->PreviewController.Component;
        if (component != null && component->AtkResNode != null)
        {
            previewNode = component->AtkResNode;
            boundsSource = "PreviewController.Component";
        }
        else if (addon->PreviewController.CollisionNode != null)
        {
            previewNode = (AtkResNode*)addon->PreviewController.CollisionNode;
            boundsSource = "PreviewController.CollisionNode";
        }

        float left;
        float top;
        float right;
        float bottom;

        if (previewNode != null)
        {
            Bounds bounds = default;
            previewNode->GetBounds(&bounds);
            left = bounds.Pos1.X - paddingPixels;
            top = bounds.Pos1.Y - paddingPixels;
            right = bounds.Pos2.X + paddingPixels;
            bottom = bounds.Pos2.Y + paddingPixels;
        }
        else
        {
            var addonInfo = gameGui.GetAddonByName("CharacterInspect");
            if (addonInfo.IsNull || !addonInfo.IsVisible || addonInfo.ScaledSize.X <= 0 || addonInfo.ScaledSize.Y <= 0)
                throw new InvalidOperationException("The Inspect character preview bounds are unavailable.");

            left = addonInfo.Position.X + addonInfo.ScaledSize.X * 0.238f - paddingPixels;
            top = addonInfo.Position.Y + addonInfo.ScaledSize.Y * 0.342f - paddingPixels;
            right = addonInfo.Position.X + addonInfo.ScaledSize.X * 0.738f + paddingPixels;
            bottom = addonInfo.Position.Y + addonInfo.ScaledSize.Y * 0.867f + paddingPixels;
            boundsSource = "AddonRelativeFallback";
        }

        var viewport = ImGui.GetMainViewport();
        var viewportPos = viewport.Pos;
        var viewportSize = viewport.Size;
        if (viewportSize.X <= 0 || viewportSize.Y <= 0)
            throw new InvalidOperationException("The main game viewport is not ready.");

        var uv0 = new NumericsVector2(
            (left - viewportPos.X) / viewportSize.X,
            (top - viewportPos.Y) / viewportSize.Y);
        var uv1 = new NumericsVector2(
            (right - viewportPos.X) / viewportSize.X,
            (bottom - viewportPos.Y) / viewportSize.Y);

        uv0 = NumericsVector2.Clamp(uv0, NumericsVector2.Zero, NumericsVector2.One);
        uv1 = NumericsVector2.Clamp(uv1, NumericsVector2.Zero, NumericsVector2.One);

        if (uv1.X <= uv0.X || uv1.Y <= uv0.Y)
            throw new InvalidOperationException("Calculated preview crop is empty.");

        var args = new ImGuiViewportTextureArgs
        {
            ViewportId = viewport.ID,
            AutoUpdate = false,
            TakeBeforeImGuiRender = true,
            KeepTransparency = false,
            Uv0 = uv0,
            Uv1 = uv1,
        };

        var textureTask = textureProvider.CreateFromImGuiViewportAsync(args, "GlamSpector Inspect Preview");

        var diagnostics = new PreviewCaptureDiagnostics
        {
            BoundsSource = boundsSource,
            Left = (int)left,
            Top = (int)top,
            Right = (int)right,
            Bottom = (int)bottom,
            Uv0X = uv0.X,
            Uv0Y = uv0.Y,
            Uv1X = uv1.X,
            Uv1Y = uv1.Y,
        };

        return new CaptureRequest(textureTask, diagnostics);
    }


    public CaptureRequest BeginAddonCapture(string addonName, string debugName, float insetPixels = 0f, bool autoUpdate = false, bool takeBeforeImGuiRender = true)
    {
        var addon = gameGui.GetAddonByName(addonName);
        if (addon.IsNull || !addon.IsVisible || addon.ScaledSize.X <= 0 || addon.ScaledSize.Y <= 0)
            throw new InvalidOperationException($"The {debugName} window is not open.");

        var viewport = ImGui.GetMainViewport();
        var viewportPos = viewport.Pos;
        var viewportSize = viewport.Size;
        if (viewportSize.X <= 0 || viewportSize.Y <= 0)
            throw new InvalidOperationException("The main game viewport is not ready.");

        var left = addon.Position.X + insetPixels;
        var top = addon.Position.Y + insetPixels;
        var right = addon.Position.X + addon.ScaledSize.X - insetPixels;
        var bottom = addon.Position.Y + addon.ScaledSize.Y - insetPixels;

        var uv0 = new NumericsVector2((left - viewportPos.X) / viewportSize.X, (top - viewportPos.Y) / viewportSize.Y);
        var uv1 = new NumericsVector2((right - viewportPos.X) / viewportSize.X, (bottom - viewportPos.Y) / viewportSize.Y);
        uv0 = NumericsVector2.Clamp(uv0, NumericsVector2.Zero, NumericsVector2.One);
        uv1 = NumericsVector2.Clamp(uv1, NumericsVector2.Zero, NumericsVector2.One);

        if (uv1.X <= uv0.X || uv1.Y <= uv0.Y)
            throw new InvalidOperationException($"Calculated {debugName} crop is empty.");

        var args = new ImGuiViewportTextureArgs
        {
            ViewportId = viewport.ID,
            AutoUpdate = autoUpdate,
            TakeBeforeImGuiRender = takeBeforeImGuiRender,
            KeepTransparency = false,
            Uv0 = uv0,
            Uv1 = uv1,
        };

        var task = textureProvider.CreateFromImGuiViewportAsync(args, $"GlamSpector {debugName}");
        return new CaptureRequest(task, new PreviewCaptureDiagnostics
        {
            BoundsSource = $"Addon:{addonName}",
            Left = (int)left, Top = (int)top, Right = (int)right, Bottom = (int)bottom,
            Uv0X = uv0.X, Uv0Y = uv0.Y, Uv1X = uv1.X, Uv1Y = uv1.Y,
        });
    }

    public CaptureRequest BeginRelativeAddonCapture(
        string addonName,
        string debugName,
        float leftRatio,
        float topRatio,
        float rightRatio,
        float bottomRatio,
        bool takeBeforeImGuiRender = true)
    {
        if (leftRatio < 0f || topRatio < 0f || rightRatio > 1f || bottomRatio > 1f ||
            rightRatio <= leftRatio || bottomRatio <= topRatio)
            throw new ArgumentOutOfRangeException(nameof(leftRatio), "Relative addon crop ratios must describe a non-empty rectangle inside 0..1.");

        var addon = gameGui.GetAddonByName(addonName);
        if (addon.IsNull || !addon.IsVisible || addon.ScaledSize.X <= 0 || addon.ScaledSize.Y <= 0)
            throw new InvalidOperationException($"The {debugName} window is not open.");

        var viewport = ImGui.GetMainViewport();
        var viewportPos = viewport.Pos;
        var viewportSize = viewport.Size;
        if (viewportSize.X <= 0 || viewportSize.Y <= 0)
            throw new InvalidOperationException("The main game viewport is not ready.");

        var left = addon.Position.X + addon.ScaledSize.X * leftRatio;
        var top = addon.Position.Y + addon.ScaledSize.Y * topRatio;
        var right = addon.Position.X + addon.ScaledSize.X * rightRatio;
        var bottom = addon.Position.Y + addon.ScaledSize.Y * bottomRatio;

        var uv0 = new NumericsVector2((left - viewportPos.X) / viewportSize.X, (top - viewportPos.Y) / viewportSize.Y);
        var uv1 = new NumericsVector2((right - viewportPos.X) / viewportSize.X, (bottom - viewportPos.Y) / viewportSize.Y);
        uv0 = NumericsVector2.Clamp(uv0, NumericsVector2.Zero, NumericsVector2.One);
        uv1 = NumericsVector2.Clamp(uv1, NumericsVector2.Zero, NumericsVector2.One);

        if (uv1.X <= uv0.X || uv1.Y <= uv0.Y)
            throw new InvalidOperationException($"Calculated {debugName} crop is empty.");

        var args = new ImGuiViewportTextureArgs
        {
            ViewportId = viewport.ID,
            AutoUpdate = false,
            TakeBeforeImGuiRender = takeBeforeImGuiRender,
            KeepTransparency = false,
            Uv0 = uv0,
            Uv1 = uv1,
        };

        var task = textureProvider.CreateFromImGuiViewportAsync(args, $"GlamSpector {debugName}");
        return new CaptureRequest(task, new PreviewCaptureDiagnostics
        {
            BoundsSource = $"Addon:{addonName}:relative({leftRatio:0.###},{topRatio:0.###},{rightRatio:0.###},{bottomRatio:0.###})",
            Left = (int)left, Top = (int)top, Right = (int)right, Bottom = (int)bottom,
            Uv0X = uv0.X, Uv0Y = uv0.Y, Uv1X = uv1.X, Uv1Y = uv1.Y,
        });
    }

    /// <summary>
    /// Capture only the central character viewport from FFXIV's native Fitting
    /// Room. The ratios deliberately include the thin native preview frame while
    /// excluding the surrounding equipment-slot buttons, bottom action strip and title bar.
    /// </summary>
    public CaptureRequest BeginTryOnCharacterCapture() =>
        BeginRelativeAddonCapture(
            "Tryon",
            "Fitting Room character preview",
            leftRatio: 0.205f,
            topRatio: 0.105f,
            rightRatio: 0.795f,
            bottomRatio: 0.879f,
            takeBeforeImGuiRender: true);

    public async Task<byte[]> EncodePngAsync(
        IDalamudTextureWrap texture,
        CancellationToken cancellationToken = default)
    {
        var pngCodec = GetPngCodec();
        await using var stream = new MemoryStream();

        await readbackProvider.SaveToStreamAsync(
            texture,
            pngCodec.ContainerGuid,
            stream,
            props: null,
            leaveWrapOpen: true,
            leaveStreamOpen: true,
            cancellationToken: cancellationToken);

        return stream.ToArray();
    }

    public async Task CopyPngBytesToClipboardAsync(
        ReadOnlyMemory<byte> pngBytes,
        string preferredName,
        CancellationToken cancellationToken = default)
    {
        using var texture = await textureProvider.CreateFromImageAsync(
            pngBytes,
            "GlamSpector Final Card",
            cancellationToken);

        await readbackProvider.CopyToClipboardAsync(
            texture,
            preferredName,
            leaveWrapOpen: true,
            cancellationToken: cancellationToken);
    }

    private Dalamud.Interface.Textures.IBitmapCodecInfo GetPngCodec()
    {
        return readbackProvider
            .GetSupportedImageEncoderInfos()
            .FirstOrDefault(x => x.Extensions.Any(ext =>
                ext.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals("png", StringComparison.OrdinalIgnoreCase)))
            ?? throw new InvalidOperationException("Dalamud did not report a PNG encoder.");
    }
}

public sealed record CaptureRequest(
    Task<IDalamudTextureWrap> TextureTask,
    PreviewCaptureDiagnostics Diagnostics);
