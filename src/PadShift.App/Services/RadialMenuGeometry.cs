namespace PadShift.App.Services;

/// <summary>
/// Sector math for the eight-sector radial menu. Sectors are centred on the eight compass points:
/// index 0 is the top sector (12 o'clock), 1 is top-right, 2 is right, ... 7 is top-left. The angle
/// is measured clockwise from "up" so it matches both the mouse and a right-stick vector
/// (<c>dx = right, dy = down</c>), where the screen Y axis points down.
/// </summary>
public static class RadialMenuGeometry
{
    public const int DefaultSectorCount = 8;
    public const double DefaultDeadZone = 24;
    public const double DefaultRadius = 190;

    /// <summary>Clockwise angle from 12 o'clock, in [0, 360).</summary>
    public static double AngleFromTopClockwise(double dx, double dy)
    {
        if (dx == 0 && dy == 0)
        {
            return 0;
        }

        double degrees = Math.Atan2(dx, -dy) * (180.0 / Math.PI);
        if (degrees < 0)
        {
            degrees += 360;
        }

        return degrees >= 360 ? 0 : degrees;
    }

    /// <summary>
    /// Maps an angle to a sector index. The boundaries sit halfway between sector centres, so for
    /// eight sectors 0° and 22.49° are sector 0, 22.5° is sector 1, 45° (centre of sector 1) is 1 and
    /// 337.5° wraps back to sector 0.
    /// </summary>
    public static int SectorIndex(double angle, int sectorCount = DefaultSectorCount)
    {
        if (sectorCount <= 0)
        {
            return -1;
        }

        double step = 360.0 / sectorCount;
        double normalized = angle % 360;
        if (normalized < 0)
        {
            normalized += 360;
        }

        int index = (int)Math.Floor((normalized + (step / 2.0)) / step) % sectorCount;
        return index < 0 ? index + sectorCount : index;
    }

    /// <summary>
    /// Point-based hit test around a centre. Returns -1 inside the dead zone, outside the ring, or
    /// for a non-positive sector count.
    /// </summary>
    public static int HitTest(
        double centerX,
        double centerY,
        double x,
        double y,
        int sectorCount = DefaultSectorCount,
        double deadZone = DefaultDeadZone,
        double radius = DefaultRadius)
    {
        if (sectorCount <= 0)
        {
            return -1;
        }

        double dx = x - centerX;
        double dy = y - centerY;
        double distanceSquared = (dx * dx) + (dy * dy);
        if (distanceSquared < deadZone * deadZone || distanceSquared > radius * radius)
        {
            return -1;
        }

        return SectorIndex(AngleFromTopClockwise(dx, dy), sectorCount);
    }

    /// <summary>Centre angle of a sector in standard screen coordinates (0° = +X, clockwise).</summary>
    public static double SectorCenterAngle(int index, int sectorCount = DefaultSectorCount) =>
        sectorCount <= 0 ? 0 : (index * (360.0 / sectorCount)) - 90;

    /// <summary>Point on a circle; angles are screen-space degrees (positive = clockwise).</summary>
    public static (double X, double Y) PointOnCircle(double centerX, double centerY, double radius, double angleDegrees)
    {
        double radians = angleDegrees * (Math.PI / 180.0);
        return (centerX + (radius * Math.Cos(radians)), centerY + (radius * Math.Sin(radians)));
    }
}
