using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace MwbSwb.Host;

/// <summary>
/// Open-source tray glyph: two Win10-like speakers back-to-back.
/// Left arcs = RX; right arcs = TX.
/// Center: up arrow = TX upload, down arrow = RX download.
/// </summary>
internal sealed class SwbTrayIcon : IDisposable
{
    private readonly Dictionary<(int Rx, int Tx), Icon> _cache = new();
    private int _lastTxArcs = -1;
    private int _lastRxArcs = -1;

    public Icon Idle => GetOrCreate(0, 0);

    public bool TryUpdate(NotifyIcon tray, float tx, float rx)
    {
        var t = ArcCount(tx);
        var r = ArcCount(rx);
        if (t == _lastTxArcs && r == _lastRxArcs)
            return false;
        _lastTxArcs = t;
        _lastRxArcs = r;
        tray.Icon = GetOrCreate(r, t);
        tray.Text = BuildTip(r, t);
        return true;
    }

    public void Dispose()
    {
        foreach (var icon in _cache.Values)
            icon.Dispose();
        _cache.Clear();
    }

    private Icon GetOrCreate(int rxArcs, int txArcs)
    {
        var key = (rxArcs, txArcs);
        if (_cache.TryGetValue(key, out var cached))
            return cached;
        var icon = CreateIcon(rxArcs, txArcs);
        _cache[key] = icon;
        return icon;
    }

    internal static int ArcCount(float level)
    {
        if (level < 0.02f) return 0;
        if (level < 0.12f) return 1;
        if (level < 0.35f) return 2;
        return 3;
    }

    private static string BuildTip(int rxArcs, int txArcs)
    {
        static string Side(string name, int n) =>
            n == 0 ? string.Format("{0}: quiet", name) : string.Format("{0}: {1}/3", name, n);
        return string.Format("SWB  {0}  |  {1}", Side("RX\u2193", rxArcs), Side("TX\u2191", txArcs));
    }

    private static Icon CreateIcon(int rxArcs, int txArcs)
    {
        using var bmp = Render(32, rxArcs, txArcs);
        var hIcon = bmp.GetHicon();
        try
        {
            using var tmp = Icon.FromHandle(hIcon);
            return (Icon)tmp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static Bitmap Render(int size, int rxArcs, int txArcs)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        var body = Color.FromArgb(245, 245, 245);
        var stroke = Color.FromArgb(220, 220, 220);
        var dim = Color.FromArgb(90, 245, 245, 245);
        using var fill = new SolidBrush(body);
        using var dimFill = new SolidBrush(dim);
        using var edge = new Pen(stroke, Math.Max(0.8f, size / 28f));
        using var arcPen = new Pen(body, Math.Max(1.2f, size / 12f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        float s = size;
        float cy = s * 0.52f;
        float magnetW = s * 0.13f;
        float magnetH = s * 0.22f;
        float coneLen = s * 0.18f;
        float coneHalf = s * 0.20f;
        float joinGap = s * 0.02f;

        var leftMagnet = new RectangleF(s * 0.5f - joinGap - magnetW, cy - magnetH / 2f, magnetW, magnetH);
        var rightMagnet = new RectangleF(s * 0.5f + joinGap, cy - magnetH / 2f, magnetW, magnetH);

        PointF[] leftCone =
        {
            new PointF(leftMagnet.Left, cy - magnetH * 0.28f),
            new PointF(leftMagnet.Left, cy + magnetH * 0.28f),
            new PointF(leftMagnet.Left - coneLen, cy + coneHalf),
            new PointF(leftMagnet.Left - coneLen, cy - coneHalf),
        };
        PointF[] rightCone =
        {
            new PointF(rightMagnet.Right, cy - magnetH * 0.28f),
            new PointF(rightMagnet.Right, cy + magnetH * 0.28f),
            new PointF(rightMagnet.Right + coneLen, cy + coneHalf),
            new PointF(rightMagnet.Right + coneLen, cy - coneHalf),
        };

        g.FillPolygon(fill, leftCone);
        g.FillRectangle(fill, leftMagnet);
        g.FillPolygon(fill, rightCone);
        g.FillRectangle(fill, rightMagnet);
        g.DrawPolygon(edge, leftCone);
        g.DrawRectangle(edge, leftMagnet.X, leftMagnet.Y, leftMagnet.Width, leftMagnet.Height);
        g.DrawPolygon(edge, rightCone);
        g.DrawRectangle(edge, rightMagnet.X, rightMagnet.Y, rightMagnet.Width, rightMagnet.Height);

        DrawArcs(g, arcPen, s, cy, leftMagnet.Left - coneLen, true, rxArcs);
        DrawArcs(g, arcPen, s, cy, rightMagnet.Right + coneLen, false, txArcs);

        // Center: up = TX upload, down = RX download
        float cx = s * 0.5f;
        float arrowW = s * 0.10f;
        float arrowH = s * 0.09f;
        DrawArrowUp(g, txArcs > 0 ? fill : dimFill, cx, cy - s * 0.16f, arrowW, arrowH);
        DrawArrowDown(g, rxArcs > 0 ? fill : dimFill, cx, cy + s * 0.16f, arrowW, arrowH);

        return bmp;
    }

    private static void DrawArrowUp(Graphics g, Brush brush, float cx, float tipY, float w, float h)
    {
        PointF[] pts =
        {
            new PointF(cx, tipY - h / 2f),
            new PointF(cx + w / 2f, tipY + h / 2f),
            new PointF(cx - w / 2f, tipY + h / 2f),
        };
        g.FillPolygon(brush, pts);
    }

    private static void DrawArrowDown(Graphics g, Brush brush, float cx, float tipY, float w, float h)
    {
        PointF[] pts =
        {
            new PointF(cx, tipY + h / 2f),
            new PointF(cx + w / 2f, tipY - h / 2f),
            new PointF(cx - w / 2f, tipY - h / 2f),
        };
        g.FillPolygon(brush, pts);
    }

    private static void DrawArcs(Graphics g, Pen pen, float size, float cy, float tipX, bool leftSide, int count)
    {
        if (count <= 0) return;
        for (var i = 1; i <= count; i++)
        {
            float radius = size * (0.09f + 0.085f * i);
            var rect = new RectangleF(tipX - radius, cy - radius, radius * 2f, radius * 2f);
            if (leftSide)
                g.DrawArc(pen, rect, 110f, 140f);
            else
                g.DrawArc(pen, rect, -70f, 140f);
        }
    }
}
