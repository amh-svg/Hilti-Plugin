// -----------------------------------------------------------------------
// Title: Detail Views - layout planner
// Summary: Pure layout maths for the Detail Views button. Given the views
//          each set wants and the size of a sheet frame, decides which sheet
//          every view goes on, at what scale and where.
// Key logic: One band per set, at most MaxViewsPerLine views per line. A
//            set's 2D views all share one scale; its 3D view auto-fits on its
//            own. The setup window's preview and the real placement both call
//            Plan(), so the diagram and the sheets cannot disagree. Nothing
//            here touches the Revit API.
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AVES.Commands;

internal enum DetailTargetMode
{
    /// <summary>New sheet(s); every set shares sheets, a band per set.</summary>
    NewShared,

    /// <summary>New sheet(s); each set gets a sheet of its own.</summary>
    NewPerSet,

    /// <summary>Views are staged below an existing sheet's title block.</summary>
    ExistingSheet,
}

/// <summary>The views a set can produce, in the order they are laid out.</summary>
internal static class DetailKeys
{
    public static readonly string[] Order =
        ["3D", "TOP", "FRONT", "BACK", "LEFT", "RIGHT", "BOTTOM"];

    public static readonly string[] Labels =
        ["3D", "Top", "Front", "Back", "Left", "Right", "Bottom"];

    public static bool Is3D(string key) => key == "3D";
}

/// <summary>One view's drawn size on the model, in feet (what the viewport
/// would draw at 1:1).</summary>
internal sealed record PlanView(string Key, double Width, double Height);

internal sealed record PlanSet(string Name, IReadOnlyList<PlanView> Views);

/// <summary>A view with its scale and its position inside the page, measured
/// from the centre of the page's usable area (+Y up), in sheet feet.</summary>
internal sealed record PlannedView(
    int SetIndex, string Key, int Scale,
    double CenterX, double CenterY, double DrawnWidth, double DrawnHeight);

internal sealed class PlannedSheet
{
    /// <summary>0-based page. For an existing sheet, page 0 sits directly
    /// below the title block and later pages stack further down.</summary>
    public int PageIndex { get; init; }

    public List<int> SetIndexes { get; } = [];
    public List<PlannedView> Views { get; } = [];

    /// <summary>First line of each set's band, for captions in the preview.</summary>
    public List<(int SetIndex, double Top)> Bands { get; } = [];

    public string Number { get; set; } = "";
    public string Name { get; set; } = "";
}

internal sealed class DetailPlan
{
    public List<PlannedSheet> Sheets { get; } = [];
    public List<string> Warnings { get; } = [];

    /// <summary>Shared mode only: the sets did not fit on one sheet.</summary>
    public bool NeedsExtraSheets { get; set; }
}

internal static class DetailPlanner
{
    /// <summary>Fraction of the title block's extent that views may occupy.</summary>
    public const double UsableFraction = 0.90;

    public const int MaxViewsPerLine = 4;
    public const int LinesPerSheet = 3;

    /// <summary>How much of a cell a view may fill; the rest is the gutter.</summary>
    public const double CellFill = 0.92;

    public const int ScaleWarnAbove = 100;

    private const int MaxScale = 500;

    public static int LineCount(int viewCount) =>
        viewCount <= 0 ? 0 : (viewCount + MaxViewsPerLine - 1) / MaxViewsPerLine;

    /// <summary>Centre of page <paramref name="page"/> relative to the centre of
    /// an existing sheet's title block, for staged (existing sheet) layouts.</summary>
    public static double StagingOffsetY(int page, double fullHeight, double gap) =>
        -(page + 1) * (fullHeight + gap);

    public static DetailPlan Plan(
        IReadOnlyList<PlanSet> sets, double frameWidth, double frameHeight,
        DetailTargetMode mode)
    {
        var plan = new DetailPlan();

        // 1. Group the sets into pages. A set's lines never split across pages.
        var pages = new List<List<int>>();
        List<int>? current = null;
        var usedLines = 0;

        for (var i = 0; i < sets.Count; i++)
        {
            var lines = LineCount(sets[i].Views.Count);
            if (lines == 0)
            {
                plan.Warnings.Add($"{sets[i].Name}: no views are ticked.");
                continue;
            }

            var startNew = current is null
                || mode == DetailTargetMode.NewPerSet
                || usedLines + lines > LinesPerSheet;

            if (startNew)
            {
                current = [];
                pages.Add(current);
                usedLines = 0;
            }

            current!.Add(i);
            usedLines += lines;
        }

        plan.NeedsExtraSheets = mode == DetailTargetMode.NewShared && pages.Count > 1;

        // 2. Lay each page out.
        for (var p = 0; p < pages.Count; p++)
        {
            var sheet = new PlannedSheet { PageIndex = p };
            var totalLines = pages[p].Sum(i => LineCount(sets[i].Views.Count));
            var lineHeight = frameHeight / totalLines;
            var lineIndex = 0;

            foreach (var setIndex in pages[p])
            {
                sheet.SetIndexes.Add(setIndex);
                sheet.Bands.Add((setIndex, frameHeight / 2.0 - lineHeight * lineIndex));

                var views = sets[setIndex].Views;
                var lines = LineCount(views.Count);
                var perLine = (views.Count + lines - 1) / lines;
                var slices = Enumerable.Range(0, lines)
                    .Select(l => views.Skip(l * perLine).Take(perLine).ToList())
                    .ToList();

                var availHeight = lineHeight * CellFill;

                // The 3D view keeps a scale of its own: it auto-fits an equal share
                // of its line. Every 2D view of the set then shares ONE scale -- the
                // coarsest any line needs, with the 2D views of a line splitting the
                // width the 3D view leaves free in proportion to their size.
                var scale3D = new Dictionary<string, int>();
                var ratio2D = 1.0;

                foreach (var slice in slices)
                {
                    var used3DWidth = 0.0;
                    foreach (var view in slice.Where(v => DetailKeys.Is3D(v.Key)))
                    {
                        var s = PickScale(
                            view.Width, view.Height,
                            frameWidth / slice.Count * CellFill, availHeight);
                        scale3D[view.Key] = s;
                        used3DWidth += view.Width / s;
                    }

                    var twoD = slice.Where(v => !DetailKeys.Is3D(v.Key)).ToList();
                    if (twoD.Count == 0) continue;

                    var freeWidth = frameWidth * CellFill - used3DWidth;
                    ratio2D = Math.Max(ratio2D,
                        freeWidth > 1e-9 ? twoD.Sum(v => v.Width) / freeWidth : MaxScale);
                    ratio2D = Math.Max(ratio2D,
                        availHeight > 0 ? twoD.Max(v => v.Height) / availHeight : MaxScale);
                }

                var scale2D = Math.Clamp((int)Math.Ceiling(ratio2D), 1, MaxScale);

                // Left to right with equal gaps, each view centred on its line.
                foreach (var slice in slices)
                {
                    var drawn = slice
                        .Select(v =>
                        {
                            var s = DetailKeys.Is3D(v.Key) ? scale3D[v.Key] : scale2D;
                            return (View: v, Scale: s, W: v.Width / s, H: v.Height / s);
                        })
                        .ToList();

                    var gapX = (frameWidth - drawn.Sum(d => d.W)) / (drawn.Count + 1);
                    var cursor = -frameWidth / 2.0 + gapX;
                    var cy = frameHeight / 2.0 - lineHeight * (lineIndex + 0.5);

                    foreach (var d in drawn)
                    {
                        sheet.Views.Add(new PlannedView(
                            setIndex, d.View.Key, d.Scale, cursor + d.W / 2.0, cy, d.W, d.H));
                        cursor += d.W + gapX;
                    }

                    lineIndex++;
                }

                if (scale3D.Values.Any(s => s > ScaleWarnAbove))
                {
                    plan.Warnings.Add(
                        $"{sets[setIndex].Name} - 3D comes out at 1:{scale3D.Values.Max()}.");
                }
                if (views.Any(v => !DetailKeys.Is3D(v.Key)) && scale2D > ScaleWarnAbove)
                {
                    plan.Warnings.Add(
                        $"{sets[setIndex].Name} - the 2D views come out at 1:{scale2D}.");
                }
            }

            plan.Sheets.Add(sheet);
        }

        return plan;
    }

    private static int PickScale(double w, double h, double availW, double availH)
    {
        if (availW <= 0 || availH <= 0) return MaxScale;
        var s = (int)Math.Ceiling(Math.Max(w / availW, h / availH));
        return Math.Clamp(s < 1 ? 1 : s, 1, MaxScale);
    }
}

/// <summary>Sheet numbers and names for the sheets a plan will create.</summary>
internal static class DetailNaming
{
    private static readonly Regex TrailingDigits = new(@"^(.*?)(\d+)$", RegexOptions.Compiled);

    /// <summary>
    /// <paramref name="count"/> numbers starting at <paramref name="start"/>,
    /// counting up and skipping any that are already <paramref name="used"/>,
    /// keeping the prefix and zero padding of what the user typed.
    /// </summary>
    public static List<string> Allocate(string start, IEnumerable<string> used, int count)
    {
        var taken = new HashSet<string>(used, StringComparer.OrdinalIgnoreCase);
        var (prefix, n, width) = Split(start);

        var result = new List<string>(count);
        while (result.Count < count)
        {
            var candidate = prefix + n.ToString().PadLeft(width, '0');
            n++;
            if (taken.Add(candidate)) result.Add(candidate);
        }
        return result;
    }

    private static (string Prefix, long N, int Width) Split(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return ("D-", 1, 2);

        var m = TrailingDigits.Match(s);
        if (!m.Success || !long.TryParse(m.Groups[2].Value, out var n))
            return (s + "-", 1, 2);

        return (m.Groups[1].Value, n, m.Groups[2].Value.Length);
    }

    /// <summary>The default for the sheet name box.</summary>
    public static string SuggestBaseName(IReadOnlyList<string> setNames) =>
        setNames.Count == 1 ? setNames[0] : "Detail Views";

    /// <summary>Fills in Number/Name on every new sheet of the plan.</summary>
    public static void Apply(
        DetailPlan plan, IReadOnlyList<string> setNames, DetailTargetMode mode,
        string baseName, string startNumber, IEnumerable<string> usedNumbers)
    {
        if (mode == DetailTargetMode.ExistingSheet || plan.Sheets.Count == 0) return;

        var numbers = Allocate(startNumber, usedNumbers, plan.Sheets.Count);
        var name = string.IsNullOrWhiteSpace(baseName) ? "Detail Views" : baseName.Trim();

        for (var i = 0; i < plan.Sheets.Count; i++)
        {
            var sheet = plan.Sheets[i];
            sheet.Number = numbers[i];

            if (mode == DetailTargetMode.NewPerSet)
            {
                sheet.Name = setNames.Count > 1 && sheet.SetIndexes.Count > 0
                    ? $"{name} - {setNames[sheet.SetIndexes[0]]}"
                    : name;
            }
            else
            {
                sheet.Name = plan.Sheets.Count > 1
                    ? $"{name} ({i + 1}/{plan.Sheets.Count})"
                    : name;
            }
        }
    }
}
