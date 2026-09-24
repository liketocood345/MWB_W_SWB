using MwbSwb.Core;

namespace MwbSwb.Host;

/// <summary>
/// Sync only: list poses, no staging.
/// 2D ring: drag device points on the ring.
/// 3D sphere: drag empty space to change view; select a point then drag to move device.
/// </summary>
public sealed class SpatialLayoutPanel : Panel
{
    private SoundSynchroSettings _settings = SoundSynchroSettings.LoadOrDefault();
    private string? _selectedHost;
    private bool _draggingPoint;
    private bool _draggingView;
    private Point _lastMouse;
    private double _viewYawDeg;
    private double _viewPitchDeg = 20;

    public event Action? LayoutChanged;

    public SpatialLayoutPanel()
    {
        DoubleBuffered = true;
        BackColor = Color.FromArgb(28, 28, 32);
        BorderStyle = BorderStyle.FixedSingle;
        Cursor = Cursors.Hand;
    }

    public void Bind(SoundSynchroSettings settings)
    {
        _settings = settings;
        Cursor = settings.SpatialMode == SpatialLayoutMode.SyncOnly ? Cursors.Default : Cursors.Hand;
        Invalidate();
    }

    public SpatialLayoutMode Mode
    {
        get => _settings.SpatialMode;
        set
        {
            _settings.SpatialMode = value;
            Cursor = value == SpatialLayoutMode.SyncOnly ? Cursors.Default : Cursors.Hand;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var cx = Width / 2f;
        var cy = Height / 2f;
        var radius = Math.Min(Width, Height) * 0.38f;

        using var ringPen = new Pen(Color.FromArgb(80, 180, 200), 1.5f);
        using var textBrush = new SolidBrush(Color.Gainsboro);
        using var selBrush = new SolidBrush(Color.Orange);
        using var ptBrush = new SolidBrush(Color.DeepSkyBlue);

        if (_settings.SpatialMode == SpatialLayoutMode.SyncOnly)
        {
            g.DrawString("Sync only - equal stereo mix, no surround staging", Font, textBrush, 8, 8);
            g.DrawString("Poses still exchanged for when you switch to 2D/3D.", Font, textBrush, 8, 28);
            var yList = 56f;
            foreach (var pose in _settings.Layout)
            {
                g.FillEllipse(ptBrush, 16, yList, 10, 10);
                g.DrawString(pose.HostName + "  az=" + pose.AzimuthDeg.ToString("0") + " el=" + pose.ElevationDeg.ToString("0"), Font, textBrush, 34, yList - 2);
                yList += 22;
            }
            return;
        }

        if (_settings.SpatialMode == SpatialLayoutMode.Ring2D)
        {
            g.DrawEllipse(ringPen, cx - radius, cy - radius, radius * 2, radius * 2);
            g.DrawString("2D ring - drag points (poses from each PC)", Font, textBrush, 8, 8);
            foreach (var pose in _settings.Layout)
            {
                var (x, y) = PolarToScreen(pose.AzimuthDeg, 0, pose.Radius, cx, cy, radius, false);
                var brush = string.Equals(pose.HostName, _selectedHost, StringComparison.OrdinalIgnoreCase) ? selBrush : ptBrush;
                g.FillEllipse(brush, x - 7, y - 7, 14, 14);
                g.DrawString(pose.HostName, Font, textBrush, x + 10, y - 8);
            }
        }
        else
        {
            g.DrawString("3D sphere — drag empty=view, selected point=position", Font, textBrush, 8, 8);
            g.DrawString($"view yaw={_viewYawDeg:0} pitch={_viewPitchDeg:0}  |  cyan=ref plane  yellow=normal", Font, textBrush, 8, 26);
            DrawReferenceNormalPlane(g, cx, cy, radius);
            foreach (var pose in _settings.Layout.OrderBy(p => ProjectZ(p)))
            {
                var (x, y) = PolarToScreen(pose.AzimuthDeg, pose.ElevationDeg, pose.Radius, cx, cy, radius, true);
                var brush = string.Equals(pose.HostName, _selectedHost, StringComparison.OrdinalIgnoreCase) ? selBrush : ptBrush;
                var scale = (float)(0.7 + 0.3 * (1 - ProjectZ(pose)));
                g.FillEllipse(brush, x - 7 * scale, y - 7 * scale, 14 * scale, 14 * scale);
                g.DrawString(pose.HostName, Font, textBrush, x + 10, y - 8);
            }
        }
    }

    /// <summary>
    /// Reference plane = listener equatorial plane (elevation 0). Yellow arrow = surface normal (+up).
    /// </summary>
    private void DrawReferenceNormalPlane(Graphics g, float cx, float cy, float radius)
    {
        const int segments = 48;
        var pts = new PointF[segments];
        for (var i = 0; i < segments; i++)
        {
            var az = i * (360.0 / segments);
            var (x, y) = PolarToScreen(az, 0, 1.0, cx, cy, radius, true);
            pts[i] = new PointF(x, y);
        }

        using var planeFill = new SolidBrush(Color.FromArgb(40, 0, 200, 220));
        using var planePen = new Pen(Color.FromArgb(180, 0, 220, 230), 1.5f);
        using var axisPen = new Pen(Color.FromArgb(160, 120, 120, 140), 1f)
        {
            DashStyle = System.Drawing.Drawing2D.DashStyle.Dot,
        };
        using var normalPen = new Pen(Color.Gold, 2.5f);
        using var normalBrush = new SolidBrush(Color.Gold);
        using var ringPen = new Pen(Color.FromArgb(70, 180, 200), 1f);

        g.FillPolygon(planeFill, pts);
        g.DrawPolygon(planePen, pts);

        var (fx, fy) = PolarToScreen(0, 0, 1.0, cx, cy, radius, true);
        var (bx, by) = PolarToScreen(180, 0, 1.0, cx, cy, radius, true);
        var (rx, ry) = PolarToScreen(90, 0, 1.0, cx, cy, radius, true);
        var (lx, ly) = PolarToScreen(270, 0, 1.0, cx, cy, radius, true);
        g.DrawLine(axisPen, fx, fy, bx, by);
        g.DrawLine(axisPen, rx, ry, lx, ly);
        g.DrawEllipse(ringPen, cx - radius, cy - radius, radius * 2, radius * 2);

        var (nx, ny) = PolarToScreen(0, 90, 1.0, cx, cy, radius, true);
        g.DrawLine(normalPen, cx, cy, nx, ny);
        var ang = Math.Atan2(ny - cy, nx - cx);
        var a1 = ang + Math.PI * 0.85;
        var a2 = ang - Math.PI * 0.85;
        const float ah = 10f;
        g.DrawLine(normalPen, nx, ny, nx + ah * (float)Math.Cos(a1), ny + ah * (float)Math.Sin(a1));
        g.DrawLine(normalPen, nx, ny, nx + ah * (float)Math.Cos(a2), ny + ah * (float)Math.Sin(a2));
        g.DrawString("N (normal)", Font, normalBrush, nx + 6, ny - 14);
        g.FillEllipse(Brushes.WhiteSmoke, cx - 3, cy - 3, 6, 6);
    }

    private double ProjectZ(DeviceSpatialPose pose)
    {
        var az = (pose.AzimuthDeg + _viewYawDeg) * Math.PI / 180.0;
        var el = (pose.ElevationDeg + _viewPitchDeg) * Math.PI / 180.0;
        return Math.Cos(el) * Math.Cos(az);
    }

    private (float x, float y) PolarToScreen(double azDeg, double elDeg, double rad, float cx, float cy, float R, bool sphere)
    {
        if (!sphere)
        {
            var a = azDeg * Math.PI / 180.0;
            var rr = (float)(R * Math.Clamp(rad, 0.2, 1.0));
            return (cx + rr * (float)Math.Sin(a), cy - rr * (float)Math.Cos(a));
        }

        var az = (azDeg + _viewYawDeg) * Math.PI / 180.0;
        var el = (elDeg) * Math.PI / 180.0;
        var r = R * (float)Math.Clamp(rad, 0.2, 1.0);
        var x = r * (float)(Math.Cos(el) * Math.Sin(az));
        var y = -r * (float)Math.Sin(el);
        // perspective-ish scale by depth
        var z = Math.Cos(el) * Math.Cos(az);
        var s = (float)(0.85 + 0.15 * z);
        return (cx + x * s, cy + y * s - (float)(_viewPitchDeg * 0.4));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_settings.SpatialMode == SpatialLayoutMode.SyncOnly) return;
        _lastMouse = e.Location;
        var hit = HitTest(e.Location);
        if (hit != null)
        {
            _selectedHost = hit;
            _draggingPoint = true;
            _draggingView = false;
        }
        else if (_settings.SpatialMode == SpatialLayoutMode.Sphere3D)
        {
            _draggingView = true;
            _draggingPoint = false;
        }
        else
        {
            _selectedHost = null;
        }
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingPoint && _selectedHost != null)
        {
            UpdatePoseFromScreen(_selectedHost, e.Location);
            LayoutChanged?.Invoke();
            Invalidate();
        }
        else if (_draggingView)
        {
            var dx = e.X - _lastMouse.X;
            var dy = e.Y - _lastMouse.Y;
            _viewYawDeg += dx * 0.5;
            _viewPitchDeg = Math.Clamp(_viewPitchDeg + dy * 0.4, -80, 80);
            _lastMouse = e.Location;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _draggingPoint = false;
        _draggingView = false;
    }

    private string? HitTest(Point p)
    {
        var cx = Width / 2f;
        var cy = Height / 2f;
        var radius = Math.Min(Width, Height) * 0.38f;
        foreach (var pose in _settings.Layout)
        {
            var (x, y) = PolarToScreen(pose.AzimuthDeg, pose.ElevationDeg, pose.Radius, cx, cy, radius,
                _settings.SpatialMode == SpatialLayoutMode.Sphere3D);
            if (Math.Sqrt((p.X - x)*(p.X - x) + (p.Y - y)*(p.Y - y)) <= 12)
                return pose.HostName;
        }
        return null;
    }

    private void UpdatePoseFromScreen(string host, Point p)
    {
        var pose = _settings.GetOrCreatePose(host);
        var cx = Width / 2.0;
        var cy = Height / 2.0;
        var dx = p.X - cx;
        var dy = cy - p.Y;
        if (_settings.SpatialMode == SpatialLayoutMode.Ring2D)
        {
            pose.AzimuthDeg = Math.Atan2(dx, dy) * 180.0 / Math.PI;
            if (pose.AzimuthDeg < 0) pose.AzimuthDeg += 360;
            pose.ElevationDeg = 0;
            var R = Math.Min(Width, Height) * 0.38;
            pose.Radius = Math.Clamp(Math.Sqrt(dx * dx + dy * dy) / R, 0.2, 1.0);
        }
        else
        {
            // map screen to azimuth/elevation in view space then subtract view yaw
            var az = Math.Atan2(dx, Math.Max(20, Math.Abs(dy) + 20)) * 180.0 / Math.PI;
            pose.AzimuthDeg = (az - _viewYawDeg + 360) % 360;
            pose.ElevationDeg = Math.Clamp((_viewPitchDeg * 0.2) + (-dy / (Height * 0.35)) * 60.0, -80, 80);
            pose.Radius = 1.0;
        }
    }
}
