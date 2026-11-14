// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace Kitesurf.Windows.DemoEngine;

/// <summary>Page state and GDI+ painting for the demo engine.</summary>
internal sealed class DemoPage
{
    private const int NavHeight = 64;
    private const int ContentBottom = 1560;

    private static readonly Color PageBackground = Color.FromArgb(0x0B, 0x0F, 0x16);
    private static readonly Color Ink = Color.FromArgb(0xE8, 0xF1, 0xFA);
    private static readonly Color Dim = Color.FromArgb(0x8C, 0x9B, 0xB2);
    private static readonly Color Accent = Color.FromArgb(0x5E, 0xE7, 0xFF);
    private static readonly Color Violet = Color.FromArgb(0x8B, 0x7C, 0xFF);

    private readonly object _gate = new();
    private string _url = "about:blank";
    private string _title = "Kitesurf demo page";
    private double _scrollY;
    private int _clicks;
    private string _typed = string.Empty;
    private bool _hoverPrimary;
    private int _width = 1280;
    private int _height = 800;

    public void Configure(int width, int height)
    {
        lock (_gate)
        {
            _width = Math.Max(320, width);
            _height = Math.Max(240, height);
        }
    }

    public void Navigate(string url)
    {
        lock (_gate)
        {
            _url = url;
            _title = "Kitesurf demo — " + ShortName(url);
            _scrollY = 0;
            _hoverPrimary = false;
        }
    }

    public string Title
    {
        get { lock (_gate) return _title; }
    }

    public string Url
    {
        get { lock (_gate) return _url; }
    }

    public int Clicks
    {
        get { lock (_gate) return _clicks; }
    }

    public string Typed
    {
        get { lock (_gate) return _typed; }
    }

    public void ScrollBy(double deltaY)
    {
        lock (_gate)
        {
            _scrollY = Math.Clamp(_scrollY + deltaY, 0, Math.Max(0, ContentBottom - _height));
        }
    }

    public void ClickAt(double x, double y)
    {
        lock (_gate)
        {
            var primary = PrimaryButtonRect(_width);
            if (x >= primary.Left && x <= primary.Right && y + _scrollY >= primary.Top && y + _scrollY <= primary.Bottom)
                _clicks++;
        }
    }

    public void SetHover(double x, double y)
    {
        lock (_gate)
        {
            var primary = PrimaryButtonRect(_width);
            _hoverPrimary = x >= primary.Left && x <= primary.Right
                            && y + _scrollY >= primary.Top && y + _scrollY <= primary.Bottom;
        }
    }

    public void Type(string text)
    {
        lock (_gate) _typed += text;
    }

    public void Backspace()
    {
        lock (_gate)
        {
            if (_typed.Length > 0) _typed = _typed[..^1];
        }
    }

    public byte[] RenderJpeg(int width, int height, long quality)
    {
        using var bitmap = Render(width, height);
        using var stream = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bitmap.Save(stream, codec, parameters);
        return stream.ToArray();
    }

    public byte[] RenderPng(int width, int height)
    {
        using var bitmap = Render(width, height);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }

    private Bitmap Render(int width, int height)
    {
        width = Math.Max(320, width);
        height = Math.Max(240, height);

        lock (_gate)
        {
            _width = width;
            _height = height;

            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.Clear(PageBackground);

            var scroll = (float)_scrollY;

            DrawBackdrop(graphics, width, height);
            DrawContent(graphics, width, height, scroll);
            DrawNav(graphics, width);

            return bitmap;
        }
    }

    private static void DrawBackdrop(Graphics graphics, int width, int height)
    {
        using var wash = new LinearGradientBrush(
            new Rectangle(0, 0, width, Math.Max(1, height)),
            Color.FromArgb(0x10, 0x2A, 0x40),
            Color.FromArgb(0x0B, 0x0F, 0x16),
            90f);
        graphics.FillRectangle(wash, 0, 0, width, height);

        using var glow = new GraphicsPath();
        glow.AddEllipse(width * 0.55f, -height * 0.55f, width * 0.9f, height * 1.3f);
        using var pathBrush = new PathGradientBrush(glow)
        {
            CenterColor = Color.FromArgb(56, 0x5E, 0xE7, 0xFF),
            SurroundColors = new[] { Color.FromArgb(0, 0x5E, 0xE7, 0xFF) },
        };
        graphics.FillPath(pathBrush, glow);
    }

    private void DrawNav(Graphics graphics, int width)
    {
        using var bar = new SolidBrush(Color.FromArgb(180, 8, 12, 18));
        graphics.FillRectangle(bar, 0, 0, width, NavHeight);
        using var line = new Pen(Color.FromArgb(40, 255, 255, 255), 1f);
        graphics.DrawLine(line, 0, NavHeight, width, NavHeight);

        var mark = new RectangleF(28, NavHeight / 2f - 11, 22, 22);
        using var markBrush = new LinearGradientBrush(
            new RectangleF(mark.X, mark.Y, mark.Width, mark.Height + 1),
            Accent,
            Violet,
            90f);
        graphics.FillPolygon(markBrush, new[]
        {
            new PointF(mark.X + mark.Width / 2, mark.Y),
            new PointF(mark.Right, mark.Y + mark.Height * 0.55f),
            new PointF(mark.X + mark.Width / 2, mark.Bottom),
            new PointF(mark.X, mark.Y + mark.Height * 0.55f),
        });

        using var wordmark = new Font("Segoe UI Semibold", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.DrawString("KITESURF", wordmark, new SolidBrush(Ink), 60, NavHeight / 2f - 9);

        using var badge = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.DrawString("DEMO ENGINE", badge, new SolidBrush(Color.FromArgb(160, Accent)), 138, NavHeight / 2f - 7);

        var links = new[] { "Overview", "Protocol", "Limits" };
        using var linkFont = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        var right = width - 28f;
        for (var index = links.Length - 1; index >= 0; index--)
        {
            var size = graphics.MeasureString(links[index], linkFont);
            right -= size.Width;
            graphics.DrawString(links[index], linkFont, new SolidBrush(Dim), right, NavHeight / 2f - size.Height / 2f);
            right -= 28;
        }
    }

    private void DrawContent(Graphics graphics, int width, int height, float scroll)
    {
        graphics.SetClip(new RectangleF(0, NavHeight, width, height - NavHeight));
        graphics.TranslateTransform(0, -scroll + NavHeight);

        var margin = Math.Max(36f, width * 0.065f);
        var contentWidth = width - margin * 2;

        using var eyebrow = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var headline = new Font("Segoe UI Semibold", Math.Clamp(width / 22f, 26f, 52f), FontStyle.Regular, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", 14f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var mono = new Font("Consolas", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);

        graphics.DrawString("NATIVE WINDOWS SHELL · LOCAL SYNTHETIC PAGE", eyebrow, new SolidBrush(Color.FromArgb(180, Accent)), margin, 128);

        graphics.DrawString("Kitesurf, in a real\nWindows window.", headline, new SolidBrush(Ink), margin - 2, 156);

        graphics.DrawString(
            "This page is painted by the demo engine inside the client. Every frame, click and keystroke "
            + "you see travels over the same CDP socket a Cloudflare-hosted Kitesurf engine uses.",
            body,
            new SolidBrush(Dim),
            new RectangleF(margin, 296, Math.Min(contentWidth, 760), 90));

        DrawUrlPill(graphics, margin, 396, Math.Min(contentWidth, 760), mono);

        var primary = PrimaryButtonRect(width);
        using var buttonPath = RoundedRect(primary, 10f);
        using var buttonBrush = new LinearGradientBrush(
            new RectangleF(primary.X, primary.Y, primary.Width, primary.Height + 1),
            _hoverPrimary ? Color.FromArgb(0x7C, 0xF2, 0xFF) : Accent,
            Violet,
            0f);
        graphics.FillPath(buttonBrush, buttonPath);

        using var buttonText = new Font("Segoe UI Semibold", 13.5f, FontStyle.Regular, GraphicsUnit.Pixel);
        var label = _clicks == 0 ? "Click this button" : $"Clicked {_clicks}×";
        var labelSize = graphics.MeasureString(label, buttonText);
        graphics.DrawString(label, buttonText, new SolidBrush(Color.FromArgb(0x08, 0x0C, 0x14)),
            primary.X + (primary.Width - labelSize.Width) / 2, primary.Y + (primary.Height - labelSize.Height) / 2);

        using var typedFont = new Font("Consolas", 13f, FontStyle.Regular, GraphicsUnit.Pixel);
        var typed = string.IsNullOrEmpty(_typed) ? "type on the keyboard to test text input" : _typed;
        graphics.DrawString("› " + typed, typedFont,
            new SolidBrush(string.IsNullOrEmpty(_typed) ? Color.FromArgb(90, Dim) : Ink),
            primary.Right + 24, primary.Y + 12);

        DrawCards(graphics, margin, 520, contentWidth, body);

        using var footer = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        graphics.DrawString(
            "Scroll with the wheel, click the button, then open the console panel (Ctrl+`) to watch this page talk CDP.",
            footer, new SolidBrush(Color.FromArgb(140, Dim)), margin, 1080);

        graphics.ResetTransform();
        graphics.ResetClip();
    }

    private void DrawUrlPill(Graphics graphics, float x, float y, float width, Font mono)
    {
        var rect = new RectangleF(x, y, width, 44);
        using var path = RoundedRect(rect, 10f);
        using var fill = new SolidBrush(Color.FromArgb(120, 12, 18, 28));
        using var border = new Pen(Color.FromArgb(60, 255, 255, 255), 1f);
        graphics.FillPath(fill, path);
        graphics.DrawPath(border, path);

        using var dot = new SolidBrush(Accent);
        graphics.FillEllipse(dot, x + 16, y + 18, 8, 8);

        var text = _url.Length > 74 ? _url[..71] + "…" : _url;
        graphics.DrawString(text, mono, new SolidBrush(Ink), x + 34, y + 13);
    }

    private void DrawCards(Graphics graphics, float margin, float top, float contentWidth, Font body)
    {
        var cards = new[]
        {
            ("Screencast", "JPEG frames, acked one by one", Accent),
            ("Input", "Mouse, wheel and text over CDP", Violet),
            ("Session", "One socket per tab", Color.FromArgb(0x5A, 0xD1, 0x9A)),
        };

        var gap = 18f;
        var cardWidth = (contentWidth - gap * (cards.Length - 1)) / cards.Length;
        using var heading = new Font("Segoe UI Semibold", 15f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var caption = new Font("Segoe UI", 12.5f, FontStyle.Regular, GraphicsUnit.Pixel);

        for (var index = 0; index < cards.Length; index++)
        {
            var rect = new RectangleF(margin + index * (cardWidth + gap), top, cardWidth, 168);
            using var path = RoundedRect(rect, 14f);
            using var fill = new SolidBrush(Color.FromArgb(150, 14, 20, 30));
            using var border = new Pen(Color.FromArgb(36, 255, 255, 255), 1f);
            graphics.FillPath(fill, path);
            graphics.DrawPath(border, path);

            using var accentBar = new SolidBrush(cards[index].Item3);
            graphics.FillRectangle(accentBar, rect.X + 22, rect.Y + 24, 3, 26);

            graphics.DrawString(cards[index].Item1, heading, new SolidBrush(Ink), rect.X + 38, rect.Y + 24);
            graphics.DrawString(cards[index].Item2, caption, new SolidBrush(Dim),
                new RectangleF(rect.X + 22, rect.Y + 74, rect.Width - 44, 70));

            using var line = new Pen(Color.FromArgb(28, 255, 255, 255), 1f);
            graphics.DrawLine(line, rect.X + 22, rect.Bottom - 38, rect.Right - 22, rect.Bottom - 38);
            graphics.DrawString("kitesurf engine · CDP", caption, new SolidBrush(Color.FromArgb(110, Dim)),
                rect.X + 22, rect.Bottom - 30);
        }
    }

    private static RectangleF PrimaryButtonRect(int width) =>
        new(Math.Max(36f, width * 0.065f), 880f, 210f, 44f);

    private static string ShortName(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri)) return uri.Host + uri.AbsolutePath.TrimEnd('/');
        return url;
    }

    private static GraphicsPath RoundedRect(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
