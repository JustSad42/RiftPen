using System.Windows;

namespace RiftPen;

public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static double Dot(Vec3 a, Vec3 b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
    public static Vec3 Cross(Vec3 a, Vec3 b) =>
        new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
}

/// <summary>One reading of the replay camera from Riot's Replay API.</summary>
public sealed record CameraState(Vec3 Position, double RotX, double RotY, double RotZ, double Fov, string Mode, bool Attached = false)
{
    public static bool SameView(CameraState? a, CameraState? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Position == b.Position && a.RotX == b.RotX && a.RotY == b.RotY && a.RotZ == b.RotZ && a.Fov == b.Fov;
    }
}

/// <summary>
/// Converts between League map ground positions and overlay pixels using the replay camera.
/// League uses a left-handed world: X = east, Y = up, Z = north (blue base near 0,0; red base near 14800,14800).
/// </summary>
public sealed class Projector
{
    Vec3 eye, fwd, right, up;
    double tanX, tanY, ground;
    Rect vp;

    public bool Valid { get; private set; }

    public void Invalidate() => Valid = false;

    public void Update(CameraState cam, Rect viewport, CalibrationSettings cal)
    {
        if (viewport.Width < 10 || viewport.Height < 10) { Valid = false; return; }

        double yawRaw = cal.SwapYawPitch ? cam.RotY : cam.RotX;
        double pitchRaw = cal.SwapYawPitch ? cam.RotX : cam.RotY;
        double yaw = Rad((cal.InvertYaw ? -yawRaw : yawRaw) + cal.YawOffset);
        double pitch = Rad(cal.InvertPitch ? -pitchRaw : pitchRaw);
        double roll = Rad(cam.RotZ);

        // yaw 0 faces +Z (north); positive pitch looks down.
        fwd = new Vec3(Math.Sin(yaw) * Math.Cos(pitch), -Math.Sin(pitch), Math.Cos(yaw) * Math.Cos(pitch));
        right = new Vec3(Math.Cos(yaw), 0, -Math.Sin(yaw));
        up = Vec3.Cross(fwd, right);
        if (Math.Abs(roll) > 1e-9)
        {
            double c = Math.Cos(roll), s = Math.Sin(roll);
            var r2 = right * c + up * s;
            var u2 = up * c - right * s;
            right = r2; up = u2;
        }

        double aspect = viewport.Width / viewport.Height;
        double fov = Rad(Math.Clamp(cam.Fov * cal.FovScale, 1, 170));
        if (cal.FovIsHorizontal) { tanX = Math.Tan(fov / 2); tanY = tanX / aspect; }
        else { tanY = Math.Tan(fov / 2); tanX = tanY * aspect; }

        eye = cam.Position;
        ground = cal.GroundHeight;
        vp = viewport;
        Valid = true;
    }

    /// <summary>Map ground point (world X, world Z) to overlay pixels.</summary>
    public bool TryProject(double wx, double wz, out Point p)
    {
        p = default;
        if (!Valid) return false;
        var d = new Vec3(wx - eye.X, ground - eye.Y, wz - eye.Z);
        double zc = Vec3.Dot(d, fwd);
        if (zc < 1) return false; // behind the camera
        double nx = Vec3.Dot(d, right) / (zc * tanX);
        double ny = Vec3.Dot(d, up) / (zc * tanY);
        if (Math.Abs(nx) > 40 || Math.Abs(ny) > 40) return false;
        p = new Point(vp.X + (nx + 1) * 0.5 * vp.Width, vp.Y + (1 - ny) * 0.5 * vp.Height);
        return true;
    }

    /// <summary>The ground point at the centre of the view: the champion, when the camera is locked onto one.</summary>
    public bool TryLookAt(out Point world) =>
        TryUnproject(new Point(vp.X + vp.Width / 2, vp.Y + vp.Height / 2), out world);

    /// <summary>Overlay pixel to map ground point (X = world X, Y = world Z).</summary>
    public bool TryUnproject(Point p, out Point world)
    {
        world = default;
        if (!Valid) return false;
        double nx = (p.X - vp.X) / vp.Width * 2 - 1;
        double ny = 1 - (p.Y - vp.Y) / vp.Height * 2;
        var dir = fwd + right * (nx * tanX) + up * (ny * tanY);
        if (Math.Abs(dir.Y) < 1e-9) return false;
        double t = (ground - eye.Y) / dir.Y;
        if (t <= 0) return false; // ray points above the horizon
        world = new Point(eye.X + dir.X * t, eye.Z + dir.Z * t);
        return true;
    }

    static double Rad(double deg) => deg * Math.PI / 180.0;
}
