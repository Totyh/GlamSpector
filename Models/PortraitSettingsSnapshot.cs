namespace GlamSpector.Models;

/// <summary>
/// A read-only snapshot of the portrait recipe exposed by an open Adventurer Plate.
/// M3.4 stores both an opaque exported payload and useful decoded fields. The opaque
/// payload is intentionally not applied to the local player's portrait yet.
/// </summary>
public sealed class PortraitSettingsSnapshot
{
    public int FormatVersion { get; init; } = 1;
    public string Source { get; init; } = "AdventurerPlate";
    public string? RawExportedPortraitDataBase64 { get; init; }

    public float CameraPositionX { get; init; }
    public float CameraPositionY { get; init; }
    public float CameraPositionZ { get; init; }
    public float CameraPositionW { get; init; }
    public float CameraTargetX { get; init; }
    public float CameraTargetY { get; init; }
    public float CameraTargetZ { get; init; }
    public float CameraTargetW { get; init; }
    public float CameraYaw { get; init; }
    public float CameraPitch { get; init; }
    public float CameraDistance { get; init; }
    public short ImageRotation { get; init; }
    public byte CameraZoom { get; init; }

    public byte DirectionalLightingColorRed { get; init; }
    public byte DirectionalLightingColorGreen { get; init; }
    public byte DirectionalLightingColorBlue { get; init; }
    public byte DirectionalLightingBrightness { get; init; }
    public short DirectionalLightingVerticalAngle { get; init; }
    public short DirectionalLightingHorizontalAngle { get; init; }
    public byte AmbientLightingColorRed { get; init; }
    public byte AmbientLightingColorGreen { get; init; }
    public byte AmbientLightingColorBlue { get; init; }
    public byte AmbientLightingBrightness { get; init; }

    public short PoseClassJob { get; init; }
    public short Background { get; init; }
    public bool CharacterVisible { get; init; }

    public ushort PlateBase { get; init; }
    public byte PlateTopBorder { get; init; }
    public byte PlateBottomBorder { get; init; }
    public ushort BannerBackground { get; init; }
    public ushort BannerFrame { get; init; }
    public ushort BannerDecoration { get; init; }
}
