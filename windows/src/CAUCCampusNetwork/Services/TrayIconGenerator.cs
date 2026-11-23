using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace CAUCCampusNetwork.Services;

public enum TrayIconVisualState
{
    BusyInitial,
    DualStackOnline,
    Partial,
    Offline,
}

public static class TrayIconGenerator
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon CreateIcon(TrayIconVisualState state, bool isSystemDark)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var primaryStroke = isSystemDark
            ? Color.FromArgb(245, 245, 247, 250)
            : Color.FromArgb(245, 28, 33, 41);

        var accentBlue = Color.FromArgb(255, 10, 132, 255);
        var greenBadge = Color.FromArgb(255, 34, 197, 94);
        var orangeBadge = Color.FromArgb(255, 245, 158, 11);

        if (state == TrayIconVisualState.BusyInitial)
        {
            using var arcPen = new Pen(accentBlue, 2.7f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawArc(arcPen, 5f, 5f, 22f, 22f, -35f, 125f);
            g.DrawArc(arcPen, 5f, 5f, 22f, 22f, 145f, 125f);

            using var dotBrush = new SolidBrush(accentBlue);
            g.FillEllipse(dotBrush, 13.5f, 13.5f, 5f, 5f);
        }
        else if (state == TrayIconVisualState.Offline)
        {
            using var wifiPen = new Pen(primaryStroke, 2.4f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawArc(wifiPen, 4f, 6f, 24f, 24f, 225f, 90f);
            g.DrawArc(wifiPen, 8.5f, 10.5f, 15f, 15f, 225f, 90f);

            using var dotBrush = new SolidBrush(primaryStroke);
            g.FillEllipse(dotBrush, 14f, 20.5f, 4f, 4f);

            DrawStatusBadge(g, orangeBadge, isSystemDark, drawExclamation: true);
        }
        else
        {
            using var globePen = new Pen(primaryStroke, 2.0f);
            g.DrawEllipse(globePen, 4.5f, 4.5f, 21f, 21f);
            g.DrawEllipse(globePen, 10.2f, 4.5f, 9.6f, 21f);
            g.DrawLine(globePen, 5f, 15f, 25f, 15f);

            var badgeColor = state == TrayIconVisualState.DualStackOnline ? greenBadge : orangeBadge;
            DrawStatusBadge(g, badgeColor, isSystemDark, drawExclamation: false);
        }

        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    private static void DrawStatusBadge(
        Graphics g,
        Color badgeColor,
        bool isSystemDark,
        bool drawExclamation)
    {
        var haloColor = isSystemDark
            ? Color.FromArgb(230, 24, 24, 27)
            : Color.FromArgb(235, 255, 255, 255);

        using var haloBrush = new SolidBrush(haloColor);
        g.FillEllipse(haloBrush, 18.5f, 18.5f, 12.5f, 12.5f);

        using var badgeBrush = new SolidBrush(badgeColor);
        g.FillEllipse(badgeBrush, 20f, 20f, 9.5f, 9.5f);

        if (drawExclamation)
        {
            using var bangPen = new Pen(Color.White, 1.8f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round,
            };
            g.DrawLine(bangPen, 24.75f, 21.8f, 24.75f, 25.0f);
            using var bangDot = new SolidBrush(Color.White);
            g.FillEllipse(bangDot, 23.85f, 26.5f, 1.8f, 1.8f);
        }
    }
}
