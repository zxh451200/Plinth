using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Plinth.App.Localization;
using Plinth.App.Modules.Demo.Data;
using QRCoder;
using SkiaSharp;

namespace Plinth.App.Modules.Demo.Services;

/// <summary>示例模块的固定 PDF 模板：当前页记录 + 一个二维码。不是通用报表引擎。</summary>
public static class DemoPdfExporter
{
    private const float PageWidth = 595;
    private const float PageHeight = 842;
    private const float Margin = 40;
    private const float RowHeight = 24;
    private const float QrSize = 88;

    public static byte[] Render(IReadOnlyList<DemoRecord> records, DateTimeOffset generatedAt)
    {
        using var stream = new MemoryStream();
        using var document = SKDocument.CreatePdf(stream);
        var typeface = SKFontManager.Default.MatchCharacter('中') ?? SKTypeface.Default;
        using var titleFont = new SKFont(typeface, 18);
        using var bodyFont = new SKFont(typeface, 10);
        using var smallFont = new SKFont(typeface, 8);
        using var dark = new SKPaint { Color = SKColor.Parse("#1F2937"), IsAntialias = true };
        using var subtle = new SKPaint { Color = SKColor.Parse("#6B7280"), IsAntialias = true };
        using var accent = new SKPaint { Color = SKColor.Parse("#2563EB"), IsAntialias = true };
        using var border = new SKPaint { Color = SKColor.Parse("#E5E7EB"), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 0.6f };
        using var band = new SKPaint { Color = SKColor.Parse("#F3F4F6"), IsAntialias = true };

        var page = 0;
        var index = 0;
        var rowsPerPage = (int)((PageHeight - 190 - 40) / RowHeight);
        do
        {
            page++;
            var canvas = document.BeginPage(PageWidth, PageHeight);
            DrawHeader(canvas, records, generatedAt, page, titleFont, bodyFont, smallFont, dark, subtle, accent, band);
            var y = 160f;
            DrawTableHeader(canvas, bodyFont, dark, band);
            y += RowHeight;

            var rowsOnPage = 0;
            while (index < records.Count && rowsOnPage < rowsPerPage)
            {
                var record = records[index++];
                DrawRow(canvas, record, y, bodyFont, dark, border);
                y += RowHeight;
                rowsOnPage++;
            }

            var footer = Loc.Format("demo.pdf.page", page);
            DrawText(canvas, footer, PageWidth - Margin, PageHeight - 24, SKTextAlign.Right, smallFont, subtle);
            document.EndPage();
        }
        while (index < records.Count);

        document.Close();
        return stream.ToArray();
    }

    internal static string BuildQrPayload(IReadOnlyList<DemoRecord> records, DateTimeOffset generatedAt)
    {
        var first = records.Count == 0 ? "" : records[0].Code;
        var last = records.Count == 0 ? "" : records[^1].Code;
        var payload = string.Create(CultureInfo.InvariantCulture,
            $"PLINTH-DEMO|generated={generatedAt:O}|count={records.Count}|first={first}|last={last}");
        return payload.Length <= 500 ? payload : payload[..500];
    }

    private static void DrawHeader(SKCanvas canvas, IReadOnlyList<DemoRecord> records, DateTimeOffset generatedAt,
        int page, SKFont titleFont, SKFont bodyFont, SKFont smallFont,
        SKPaint dark, SKPaint subtle, SKPaint accent, SKPaint band)
    {
        DrawText(canvas, AppInfo.ProductName, Margin, 58, SKTextAlign.Left, titleFont, accent);
        DrawText(canvas, Loc.Get("demo.pdf.title"), Margin, 80, SKTextAlign.Left, bodyFont, dark);
        DrawText(canvas, Loc.Format("demo.pdf.generated", generatedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)),
            Margin, 98, SKTextAlign.Left, smallFont, subtle);
        DrawText(canvas, Loc.Format("demo.pdf.records", records.Count), Margin, 114, SKTextAlign.Left, smallFont, subtle);

        DrawQr(canvas, BuildQrPayload(records, generatedAt), PageWidth - Margin - QrSize, 40, dark);
        using var rule = new SKPaint { Color = SKColor.Parse("#E5E7EB"), StrokeWidth = 1 };
        canvas.DrawLine(Margin, 132, PageWidth - Margin, 132, rule);
    }

    private static void DrawTableHeader(SKCanvas canvas, SKFont font, SKPaint dark, SKPaint band)
    {
        var y = 172f;
        canvas.DrawRect(new SKRect(Margin, y - 14, PageWidth - Margin, y + 10), band);
        DrawText(canvas, Loc.Get("demo.pdf.code"), Margin + 6, y, SKTextAlign.Left, font, dark);
        DrawText(canvas, Loc.Get("demo.pdf.name"), Margin + 116, y, SKTextAlign.Left, font, dark);
        DrawText(canvas, Loc.Get("demo.pdf.remark"), Margin + 266, y, SKTextAlign.Left, font, dark);
        DrawText(canvas, Loc.Get("demo.pdf.version"), PageWidth - Margin - 46, y, SKTextAlign.Left, font, dark);
    }

    private static void DrawRow(SKCanvas canvas, DemoRecord record, float y, SKFont font, SKPaint dark, SKPaint border)
    {
        var baseline = y + 16;
        DrawText(canvas, Fit(record.Code, font, 104), Margin + 6, baseline, SKTextAlign.Left, font, dark);
        DrawText(canvas, Fit(record.Name, font, 144), Margin + 116, baseline, SKTextAlign.Left, font, dark);
        DrawText(canvas, Fit(record.Remark, font, 224), Margin + 266, baseline, SKTextAlign.Left, font, dark);
        DrawText(canvas, record.Version.ToString(CultureInfo.InvariantCulture), PageWidth - Margin - 40, baseline,
            SKTextAlign.Left, font, dark);
        canvas.DrawLine(Margin, y + RowHeight, PageWidth - Margin, y + RowHeight, border);
    }

    private static void DrawQr(SKCanvas canvas, string payload, float x, float y, SKPaint dark)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var quiet = 4;
        var module = QrSize / (matrix.Count + (quiet * 2));
        using var white = new SKPaint { Color = SKColors.White };
        canvas.DrawRect(new SKRect(x, y, x + QrSize, y + QrSize), white);
        for (var row = 0; row < matrix.Count; row++)
        {
            for (var column = 0; column < matrix[row].Length; column++)
            {
                if (!matrix[row][column]) continue;
                var left = x + ((column + quiet) * module);
                var top = y + ((row + quiet) * module);
                canvas.DrawRect(new SKRect(left, top, left + module + 0.2f, top + module + 0.2f), dark);
            }
        }
    }

    /// <summary>
    /// PDF 里把文字转成矢量轮廓，避免把整套 CJK 字体嵌进文件（60 条记录从 12 MB 降到几十 KB）。
    /// 代价是文字不可检索/复制；固定打印模板接受这个取舍，需要可搜索文本时换带字体子集化的渲染器。
    /// </summary>
    private static void DrawText(SKCanvas canvas, string text, float x, float baseline, SKTextAlign align,
        SKFont font, SKPaint paint)
    {
        if (text.Length == 0) return;
        var width = font.MeasureText(text);
        var originX = align switch
        {
            SKTextAlign.Center => x - (width / 2),
            SKTextAlign.Right => x - width,
            _ => x,
        };
        using var path = font.GetTextPath(text, new SKPoint(originX, baseline));
        if (path is not null) canvas.DrawPath(path, paint);
    }

    private static string Fit(string text, SKFont font, float maxWidth)
    {
        if (font.MeasureText(text) <= maxWidth) return text;
        var length = text.Length;
        while (length > 1)
        {
            length--;
            if (char.IsHighSurrogate(text[length - 1])) length--;
            var candidate = text[..length] + "…";
            if (font.MeasureText(candidate) <= maxWidth) return candidate;
        }

        return "…";
    }
}
