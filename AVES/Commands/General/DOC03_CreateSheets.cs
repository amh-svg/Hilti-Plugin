// -----------------------------------------------------------------------
// Pulldown Title: Drawing Automations
// Sub-buttons:
//   - Vendor Drawings: Auto-generates a two-sheet (A3) vendor drawing set
//     per assembly, with 3D and orthographic detail views placed from
//     user-picked view templates and a titleblock.
//   - Assembly Drawings: Auto-generates a one-sheet vendor drawing per
//     assembly that additionally clusters and calls out bolts by family
//     and direction.
//   - MT Drawings: Auto-generates a single-sheet (A1) vendor drawing per
//     assembly with a condensed set of 3D/orthographic views.
//   - Korea: Auto-generates a seven-sheet set per assembly -- 3D and part
//     list on the first, then one orthographic view per sheet, oriented to
//     the assembly origin and refreshed in place on a 2D-only re-run.
// -----------------------------------------------------------------------

// Autodesk
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using AVES.Core;
// File dialogs
using Microsoft.Win32;
// System
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
// WPF
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
// ---------------------------------------------------------------------------
// Type aliases — resolve the WPF/Revit/System name collisions.
// ---------------------------------------------------------------------------
using Color = System.Windows.Media.Color;
using Ellipse = System.Windows.Shapes.Ellipse;
using Grid = System.Windows.Controls.Grid;
using Line = System.Windows.Shapes.Line;
using Panel = System.Windows.Controls.Panel;
using Path = System.IO.Path;
using Rectangle = System.Windows.Shapes.Rectangle;
using RevitException = Autodesk.Revit.Exceptions.ApplicationException;
using TextBox = System.Windows.Controls.TextBox;
using Transform = Autodesk.Revit.DB.Transform;
using WpfControl = System.Windows.Controls.Control;

namespace AVES.Commands;


// =============================================================================
// 1. AVES UI HELPER  — design-system factory methods
// =============================================================================

// Tokens, fonts, ApplyHover, the brand logo, HeaderStrip, SectionHeader,
// PrimaryButton/SecondaryButton/GhostButton and StatTile all routed to
// AVES.Core (AvTokens/AvControls) during the Phase 3 migration -- verified
// byte-for-byte/pixel-for-pixel identical first. StatusPill now routes to
// AvControls.StatusPill too: geometry unified, "warn" keeps this file's own
// "Completed with errors" wording via labelOverride (reconciliation
// decision: unify styling, keep existing per-file wording).
internal static class AvesUiHelper
{
    // ── Status pill — coloured dot + label ───────────────────────────────────
    public static Border StatusPill(string kind) =>
        AvControls.StatusPill(kind, kind == "warn" ? "Completed with errors" : null);
}

// =============================================================================
// 2. SHARED CORE
// =============================================================================

/// <summary>Internal-feet conversions for common paper sizes and limits.</summary>
internal static class RevitConstants
{
    public static readonly double A3WidthFt =
        UnitUtils.ConvertToInternalUnits(420.0, UnitTypeId.Millimeters);
    public static readonly double A3HeightFt =
        UnitUtils.ConvertToInternalUnits(297.0, UnitTypeId.Millimeters);

    public static readonly double A1WidthFt =
        UnitUtils.ConvertToInternalUnits(1189.0, UnitTypeId.Millimeters);
    public static readonly double A1HeightFt =
        UnitUtils.ConvertToInternalUnits(841.0, UnitTypeId.Millimeters);

    public const int MaxScale = 500;
}

/// <summary>Bounding-box, sizing and scale helpers.</summary>
internal static class GeometryUtils
{
    public static BoundingBoxXYZ? Union(BoundingBoxXYZ? a, BoundingBoxXYZ? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return new BoundingBoxXYZ
        {
            Min = new XYZ(
                Math.Min(a.Min.X, b.Min.X),
                Math.Min(a.Min.Y, b.Min.Y),
                Math.Min(a.Min.Z, b.Min.Z)),
            Max = new XYZ(
                Math.Max(a.Max.X, b.Max.X),
                Math.Max(a.Max.Y, b.Max.Y),
                Math.Max(a.Max.Z, b.Max.Z)),
        };
    }

    public static BoundingBoxXYZ? GetAssemblyBoundingBox(Document doc, AssemblyInstance assembly)
    {
        var total = TryGetBoundingBox(assembly);
        foreach (var id in assembly.GetMemberIds())
        {
            if (doc.GetElement(id) is { } el)
                total = Union(total, TryGetBoundingBox(el));
        }
        return total;
    }

    public static BoundingBoxXYZ? GetCombinedBoundingBox(IEnumerable<Element> elements)
    {
        BoundingBoxXYZ? total = null;
        foreach (var el in elements)
            total = Union(total, TryGetBoundingBox(el));
        return total;
    }

    public static (double X, double Y, double Z) GetSize(BoundingBoxXYZ bb) => (
        Math.Abs(bb.Max.X - bb.Min.X),
        Math.Abs(bb.Max.Y - bb.Min.Y),
        Math.Abs(bb.Max.Z - bb.Min.Z));

    public static int PickScale(double modelW, double modelH, double availW, double availH)
    {
        if (availW <= 0 || availH <= 0) return RevitConstants.MaxScale;
        var s = (int)Math.Ceiling(Math.Max(modelW / availW, modelH / availH));
        return Math.Clamp(s < 1 ? 1 : s, 1, RevitConstants.MaxScale);
    }

    private static BoundingBoxXYZ? TryGetBoundingBox(Element el)
    {
        try { return el.get_BoundingBox(null); }
        catch { return null; }
    }
}

/// <summary>View-template, view-family, view-property helpers.</summary>
internal static class ViewUtils
{
    public static IReadOnlyDictionary<string, View> GetTemplates(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Where(v => v.IsTemplate)
            .GroupBy(v => v.Name)
            .ToDictionary(g => g.Key, g => g.First());

    public static View? FindTemplateByPrefix(Document doc, string prefix) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .FirstOrDefault(v => v.IsTemplate &&
                                 v.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public static ViewFamilyType? Get3DViewFamilyType(Document doc) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == ViewFamily.ThreeDimensional);

    public static View3D? Find3DViewByName(Document doc, string name) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(View3D))
            .Cast<View3D>()
            .FirstOrDefault(v => !v.IsTemplate &&
                                 v.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static ViewSheet? FindSheetByName(Document doc, string name) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public static void SetDescription(View view, string text)
    {
        var p = view.get_Parameter(BuiltInParameter.VIEW_DESCRIPTION);
        if (p is { IsReadOnly: false }) p.Set(text);
    }

    public static void ApplySectionBox(View3D view, BoundingBoxXYZ? bbox, double padding)
    {
        if (bbox is null || view.IsTemplate) return;

        var sb = new BoundingBoxXYZ
        {
            Min = new XYZ(bbox.Min.X - padding, bbox.Min.Y - padding, bbox.Min.Z - padding),
            Max = new XYZ(bbox.Max.X + padding, bbox.Max.Y + padding, bbox.Max.Z + padding),
            Transform = Transform.Identity,
        };

        view.SetSectionBox(sb);
        view.IsSectionBoxActive = true;
    }

    /// <summary>
    /// Creates a standard orthographic <see cref="View3D"/> named
    /// <paramref name="name"/>, on <paramref name="templateId"/>, with its
    /// section box fitted to <paramref name="bbox"/> grown by
    /// <paramref name="sectionBoxPadding"/>. Single implementation shared by
    /// the Vendor and Assembly drawing commands; the padding is supplied by the
    /// caller so this stays generic. CreateIsometric is the orthographic
    /// constructor -- CreatePerspective is the one that is not.
    /// </summary>
    public static View3D CreateOrthographic3D(
        Document doc,
        ViewFamilyType vft3d,
        string name,
        ElementId templateId,
        BoundingBoxXYZ? bbox,
        double sectionBoxPadding)
    {
        var view = View3D.CreateIsometric(doc, vft3d.Id);
        view.Name = name;
        view.ViewTemplateId = templateId;
        ApplySectionBox(view, bbox, sectionBoxPadding);
        return view;
    }

    /// <summary>First view family type matching <paramref name="family"/>.</summary>
    public static ViewFamilyType? GetViewFamilyType(Document doc, ViewFamily family) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(vft => vft.ViewFamily == family);

    /// <summary>
    /// Crops <paramref name="view"/> to a world-space bounding box: the box's
    /// eight corners are pulled back into the crop box's own local frame, so
    /// the fit is correct whatever the view's orientation.
    /// </summary>
    public static void FitCropBoxToBoundingBox(View view, BoundingBoxXYZ worldBbox, double padding)
    {
        if (worldBbox is null || view.IsTemplate) return;

        var crop = view.CropBox;
        var (min, max) = LocalExtents(crop.Transform, worldBbox, padding);

        view.CropBox = new BoundingBoxXYZ
        {
            Transform = crop.Transform,
            Min = min,
            Max = max,
        };
        view.CropBoxActive = true;
        view.CropBoxVisible = false;
    }

    private static readonly PlanViewPlane[] ViewRangePlanes =
    {
        PlanViewPlane.TopClipPlane,
        PlanViewPlane.CutPlane,
        PlanViewPlane.BottomClipPlane,
        PlanViewPlane.ViewDepthPlane,
    };

    /// <summary>
    /// Opens a plan view's view range to the assembly's full Z extent, so the
    /// view is a complete projection rather than an architectural cut. A freshly
    /// created plan keeps its view family type's default range -- a narrow band
    /// about its level, cut around 1.2 m -- which clips any assembly sitting
    /// outside it: floor plans come out part-drawn, ceiling plans come out blank.
    /// <para>
    /// Every plane is derived from the padded bounding box and nothing else. The
    /// cut plane is parked on the far edge of that span -- at the top for a plan
    /// looking down, at the bottom for an RCP looking up -- so the whole assembly
    /// falls on the drawn side of it and nothing is cut through. Offsets are
    /// measured from <paramref name="level"/>, the level the view was created on,
    /// and every plane is pinned to it so no Level Above / Level Below default
    /// survives to reintroduce the clipping.
    /// </para>
    /// </summary>
    public static void FitViewRangeToBoundingBox(
        ViewPlan view, BoundingBoxXYZ worldBbox, Level level, double padding)
    {
        if (worldBbox is null || view.IsTemplate) return;

        var low = worldBbox.Min.Z - level.Elevation - padding;
        var high = worldBbox.Max.Z - level.Elevation + padding;

        using var range = view.GetViewRange();

        foreach (var plane in ViewRangePlanes)
            range.SetLevelId(plane, level.Id);

        if (view.ViewType == ViewType.CeilingPlan)
        {
            // Looking up: everything above the cut plane is drawn, and the RCP's
            // view depth runs past the top rather than below the bottom.
            range.SetOffset(PlanViewPlane.BottomClipPlane, low);
            range.SetOffset(PlanViewPlane.CutPlane, low);
            range.SetOffset(PlanViewPlane.TopClipPlane, high);
            range.SetOffset(PlanViewPlane.ViewDepthPlane, high);
        }
        else
        {
            // Looking down: everything below the cut plane is drawn, as far down
            // as the view depth.
            range.SetOffset(PlanViewPlane.ViewDepthPlane, low);
            range.SetOffset(PlanViewPlane.BottomClipPlane, low);
            range.SetOffset(PlanViewPlane.CutPlane, high);
            range.SetOffset(PlanViewPlane.TopClipPlane, high);
        }

        var errors = view.CheckPlanViewRangeValidity(range);
        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                $"Computed {view.ViewType} view range is invalid: " +
                string.Join(", ", errors));
        }

        view.SetViewRange(range);
    }

    /// <summary>
    /// Creates an orthographic section looking along <paramref name="lookDirection"/>,
    /// sized to <paramref name="worldBbox"/> (plus padding) in every axis.
    /// <para>
    /// Note for future direction-sign work: Revit reports the resulting
    /// view's <see cref="View.ViewDirection"/> as the NEGATION of the
    /// <c>BasisZ</c> requested here, not the same vector. Verified
    /// empirically while investigating a Front/Back sign bug, including at a
    /// non-orthogonal transform -- it is not an axis-aligned rounding
    /// artifact. Nothing here currently reads ViewDirection back, but
    /// anything that starts doing so needs to negate it first.
    /// </para>
    /// </summary>
    public static ViewSection CreateOrthoSection(
        Document doc,
        ElementId sectionVftId,
        BoundingBoxXYZ worldBbox,
        XYZ lookDirection,
        double padding)
    {
        var up = XYZ.BasisZ;
        var basisZ = lookDirection.Normalize();
        var basisX = up.CrossProduct(basisZ).Normalize();
        var basisY = basisZ.CrossProduct(basisX).Normalize();

        var transform = Transform.Identity;
        transform.Origin = (worldBbox.Min + worldBbox.Max) / 2.0;
        transform.BasisX = basisX;
        transform.BasisY = basisY;
        transform.BasisZ = basisZ;

        var (min, max) = LocalExtents(transform, worldBbox, padding);

        var box = new BoundingBoxXYZ
        {
            Transform = transform,
            Min = min,
            Max = max,
        };

        return ViewSection.CreateSection(doc, sectionVftId, box);
    }

    /// <summary>
    /// Creates a true orthographic view from below: a section whose camera sits
    /// under the assembly looking straight up. <paramref name="topScreenRight"/>
    /// is the screen-right of the view from above; the underside takes the
    /// opposite, which is what puts the hardware on the side the reference
    /// drawing shows.
    /// <para>
    /// A Reflected Ceiling Plan cannot do this. "Reflected" is literal -- Revit
    /// mirrors the image so it reads the same way round as the floor plan above
    /// it, and the API exposes no switch to turn that off: the reflection belongs
    /// to the view type itself. An upward section carries no such convention, so
    /// it draws the underside as it actually is.
    /// </para>
    /// <para>
    /// This is separate from <see cref="CreateOrthoSection"/> because that one
    /// derives its horizontal axis from world up, which collapses when the look
    /// direction is itself vertical.
    /// </para>
    /// </summary>
    public static ViewSection CreateUpwardSection(
        Document doc,
        ElementId sectionVftId,
        BoundingBoxXYZ worldBbox,
        XYZ topScreenRight,
        double padding)
    {
        // BasisZ points out of the screen towards the viewer, who is underneath.
        // BasisX runs against the top view's screen-right, and BasisY follows
        // from the other two so the frame stays right-handed -- a frame that is
        // not is a reflection, which is the RCP behaviour this view exists to
        // avoid.
        var basisZ = new XYZ(0, 0, 1);
        var basisX = topScreenRight.Normalize().Negate();
        var basisY = basisZ.CrossProduct(basisX).Normalize();

        var transform = Transform.Identity;
        transform.Origin = (worldBbox.Min + worldBbox.Max) / 2.0;
        transform.BasisX = basisX;
        transform.BasisY = basisY;
        transform.BasisZ = basisZ;

        var (min, max) = LocalExtents(transform, worldBbox, padding);

        var box = new BoundingBoxXYZ
        {
            Transform = transform,
            Min = min,
            Max = max,
        };

        return ViewSection.CreateSection(doc, sectionVftId, box);
    }

    /// <summary>
    /// Permanently hides everything in <paramref name="view"/> that is not part
    /// of <paramref name="assembly"/>, so the view shows that assembly alone.
    /// </summary>
    public static void IsolateAssemblyPermanently(Document doc, View view, AssemblyInstance assembly)
    {
        var keep = new HashSet<ElementId>(assembly.GetMemberIds()) { assembly.Id };

        // A view-based collector only sees what the view has actually resolved,
        // and these views were created moments ago in this same transaction.
        doc.Regenerate();

        var toHide = new FilteredElementCollector(doc, view.Id)
            .WhereElementIsNotElementType()
            .Where(e => e.Category is not null
                        && !keep.Contains(e.Id)
                        && e.CanBeHidden(view))
            .Select(e => e.Id)
            .ToList();

        if (toHide.Count == 0) return;

        try
        {
            view.HideElements(toHide);
        }
        catch
        {
            // Bulk hide is all-or-nothing; fall back to one element at a time
            // so a single stubborn element does not cost the whole isolation.
            foreach (var id in toHide)
            {
                try { view.HideElements(new List<ElementId> { id }); }
                catch { /* element refuses to hide in this view; skip it */ }
            }
        }
    }

    /// <summary>
    /// Turns <paramref name="view"/> so its screen-right runs along
    /// <paramref name="desiredRight"/> -- the API equivalent of the UI's
    /// Rotate Crop. Revit exposes no rotation member on View and
    /// View.RightDirection is read-only, so this rotates the element that
    /// carries the crop rectangle, which is what fixes the view's own axes.
    /// The rotation is the measured angle, so an Assembly Origin turned by any
    /// amount is followed, not just quarter turns.
    /// Project North, True North and the model are all left alone.
    /// </summary>
    public static void RotateCropToRightDirection(
        Document doc, View view, XYZ desiredRight, XYZ pivot)
    {
        if (view.IsTemplate) return;

        var angle = SignedPlanAngleBetween(view.RightDirection, desiredRight);
        if (angle == 0.0) return;

        var wasVisible = view.CropBoxVisible;
        view.CropBoxActive = true;
        view.CropBoxVisible = true;

        try
        {
            if (FindCropBoxElementId(doc, view) is not { } cropBoxId) return;

            var axis = Autodesk.Revit.DB.Line.CreateBound(pivot, pivot + XYZ.BasisZ);
            ElementTransformUtils.RotateElement(doc, cropBoxId, axis, angle);
        }
        finally
        {
            view.CropBoxVisible = wasVisible;
        }
    }

    /// <summary>
    /// The element carrying the view's crop rectangle. Revit hands out no direct
    /// reference to it: it only turns up in a view-scoped collector while the
    /// crop is visible. So collect once with it shown and once with it hidden --
    /// the latter inside a sub-transaction that is rolled back -- and take the
    /// difference.
    /// </summary>
    private static ElementId? FindCropBoxElementId(Document doc, View view)
    {
        doc.Regenerate();
        var shown = new FilteredElementCollector(doc, view.Id).ToElementIds();

        HashSet<ElementId> hidden;
        using (var probe = new SubTransaction(doc))
        {
            probe.Start();
            view.CropBoxVisible = false;
            doc.Regenerate();
            hidden = new HashSet<ElementId>(
                new FilteredElementCollector(doc, view.Id).ToElementIds());
            probe.RollBack();
        }

        return shown.FirstOrDefault(id => !hidden.Contains(id));
    }

    /// <summary>
    /// Signed rotation about Z that carries <paramref name="from"/> onto
    /// <paramref name="to"/>. Zero when the two already agree or either one is
    /// vertical.
    /// <para>
    /// The angle is returned as measured -- deliberately not quantised. It used
    /// to be snapped to the nearest quarter turn, which was right while the view
    /// directions were world axes and every angle was a multiple of 90 degrees.
    /// Once those directions started coming through the Assembly Origin the snap
    /// discarded any rotation under 45 degrees, which is precisely the value this
    /// is being asked for.
    /// </para>
    /// </summary>
    private static double SignedPlanAngleBetween(XYZ from, XYZ to)
    {
        if (Flatten(from) is not { } a || Flatten(to) is not { } b) return 0.0;

        var angle = Math.Atan2(a.CrossProduct(b).Z, a.DotProduct(b));

        return Math.Abs(angle) < 1e-9 ? 0.0 : angle;
    }

    private static XYZ? Flatten(XYZ v)
    {
        var flat = new XYZ(v.X, v.Y, 0);
        return flat.IsZeroLength() ? null : flat.Normalize();
    }

    /// <summary>
    /// Extents of a world-space box expressed in the local coordinates of
    /// <paramref name="frame"/>, grown by <paramref name="padding"/> all round.
    /// </summary>
    internal static (XYZ Min, XYZ Max) LocalExtents(
        Transform frame, BoundingBoxXYZ worldBbox, double padding)
    {
        var inverse = frame.Inverse;

        double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;

        foreach (var corner in Corners(worldBbox))
        {
            var p = inverse.OfPoint(corner);
            minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
            minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
        }

        return (new XYZ(minX - padding, minY - padding, minZ - padding),
                new XYZ(maxX + padding, maxY + padding, maxZ + padding));
    }

    private static IEnumerable<XYZ> Corners(BoundingBoxXYZ box)
    {
        var lo = box.Min;
        var hi = box.Max;
        for (int i = 0; i < 8; i++)
        {
            yield return new XYZ(
                (i & 1) == 0 ? lo.X : hi.X,
                (i & 2) == 0 ? lo.Y : hi.Y,
                (i & 4) == 0 ? lo.Z : hi.Z);
        }
    }

    public static Viewport? PlaceView(Document doc, ViewSheet sheet, View view, XYZ point) =>
        Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id)
            ? Viewport.Create(doc, sheet.Id, view.Id, point)
            : null;

    public static Dictionary<string, View> CreateDetailViews(
        Document doc,
        ElementId assemblyId,
        ElementId templateId,
        string assemblyName,
        NameRegistry viewNames,
        IReadOnlyDictionary<string, AssemblyDetailViewOrientation> orientations)
    {
        var result = new Dictionary<string, View>(orientations.Count);
        foreach (var (key, orientation) in orientations)
        {
            var v = AssemblyViewUtils.CreateDetailSection(doc, assemblyId, orientation);
            v.Name = viewNames.Reserve($"{assemblyName} - {key}");
            v.ViewTemplateId = templateId;
            result[key] = v;
        }
        return result;
    }
}

internal static class TitleblockUtils
{
    public static Element? FirstAvailable(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsElementType()
            .FirstElement();

    /// <summary>
    /// Every title block type in the project, keyed by the name shown to the
    /// user. Built on the same collector <see cref="FirstAvailable"/> uses.
    /// </summary>
    public static IReadOnlyDictionary<string, FamilySymbol> CollectSymbols(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsElementType()
            .OfType<FamilySymbol>()
            .GroupBy(DisplayName)
            .ToDictionary(g => g.Key, g => g.First());

    /// <summary>"Family Name : Type Name" -- what the picker lists and what the
    /// settings store, so a saved choice survives a restart.</summary>
    public static string DisplayName(FamilySymbol symbol) =>
        $"{symbol.FamilyName} : {symbol.Name}";

    /// <summary>
    /// Resolves a saved title block name against the project. Returns null only
    /// when the project has none at all; when the saved one has since been
    /// deleted or renamed it falls back to the first available and reports that
    /// through <paramref name="fellBack"/> so the caller can tell the user.
    /// </summary>
    public static FamilySymbol? Resolve(
        IReadOnlyDictionary<string, FamilySymbol> symbols,
        string? savedName,
        out bool fellBack)
    {
        fellBack = false;

        if (symbols.Count == 0) return null;

        if (!string.IsNullOrWhiteSpace(savedName) &&
            symbols.TryGetValue(savedName!, out var saved))
        {
            return saved;
        }

        fellBack = !string.IsNullOrWhiteSpace(savedName);
        return symbols.Values.First();
    }
}

internal static class LevelUtils
{
    /// <summary>Level whose elevation sits closest to <paramref name="point"/>.</summary>
    public static Level? FindNearestLevel(Document doc, XYZ point) =>
        new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .OrderBy(l => Math.Abs(l.Elevation - point.Z))
            .FirstOrDefault();
}

internal sealed class NameRegistry
{
    private readonly HashSet<string> _names;

    public NameRegistry(IEnumerable<string> seed) =>
        _names = new HashSet<string>(seed, StringComparer.OrdinalIgnoreCase);

    public string Reserve(string baseName)
    {
        if (_names.Add(baseName)) return baseName;
        for (int i = 1; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (_names.Add(candidate)) return candidate;
        }
    }
}

internal static class FailureHandling
{
    public static void SuppressWarnings(Transaction tx)
    {
        var opts = tx.GetFailureHandlingOptions();
        opts.SetFailuresPreprocessor(new SilentWarningPreprocessor());
        opts.SetClearAfterRollback(true);
        opts.SetForcedModalHandling(false);
        tx.SetFailureHandlingOptions(opts);
    }

    private sealed class SilentWarningPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor a)
        {
            foreach (var f in a.GetFailureMessages())
            {
                if (f.GetSeverity() == FailureSeverity.Warning)
                    a.DeleteWarning(f);
            }
            return FailureProcessingResult.Continue;
        }
    }
}

/// <summary>One row in the results table.</summary>
internal sealed class CreateSheetsReportRow
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string Status { get; init; } = "";   // "ok" | "warn" | "fail"
    public string Details { get; init; } = "";
}

/// <summary>Shared result reporter used by all three commands.</summary>
internal static class Reporting
{
    /// <summary>
    /// Call from commands: pass structured tuples so the window can build a table.
    /// Each entry is (assemblyName, errorMessage) — errorMessage is empty on success.
    /// </summary>
    public static void ShowResult(
        string title,
        int ok, int bad,
        IReadOnlyList<string> failLog,
        IntPtr revitHandle = default)
    {
        // Build structured row list. failLog only contains failures; we reconstruct
        // the succeeded count as rows without detail.
        var rows = new List<CreateSheetsReportRow>();
        int idx = 1;

        // Parse fail entries — original format: "  • AssemblyName: error message"
        var failParsed = failLog
            .Select(line =>
            {
                var trimmed = line.TrimStart(' ', '•').Trim();
                var colon = trimmed.IndexOf(':');
                return colon > 0
                    ? (Name: trimmed[..colon].Trim(), Error: trimmed[(colon + 1)..].Trim())
                    : (Name: trimmed, Error: "Unknown error");
            })
            .ToList();

        // Add succeeded rows (anonymous — we only know the count, not the names)
        for (int i = 0; i < ok; i++)
            rows.Add(new CreateSheetsReportRow
            {
                Index = idx++,
                Name = ok == 1 ? "Assembly" : $"Assembly {i + 1}",
                Status = "ok",
                Details = "",
            });

        // Add failed rows
        foreach (var (name, error) in failParsed)
            rows.Add(new CreateSheetsReportRow
            {
                Index = idx++,
                Name = name,
                Status = "fail",
                Details = error,
            });

        new CreateSheetsReportWindow(title, rows, ok, bad, revitHandle).ShowDialog();
    }

    /// <summary>
    /// Preferred overload: commands pass named tuples directly for accurate row names.
    /// </summary>
    public static void ShowResult(
        string title,
        IReadOnlyList<(string Name, string? Error)> results,
        IntPtr revitHandle = default)
    {
        int ok = results.Count(r => r.Error is null or "");
        int bad = results.Count - ok;

        var rows = results
            .Select((r, i) => new CreateSheetsReportRow
            {
                Index = i + 1,
                Name = r.Name,
                Status = string.IsNullOrEmpty(r.Error) ? "ok" : "fail",
                Details = r.Error ?? "",
            })
            .ToList();

        new CreateSheetsReportWindow(title, rows, ok, bad, revitHandle).ShowDialog();
    }

    /// <summary>
    /// Three-state overload: an assembly can succeed cleanly, succeed with a
    /// warning that needs a look, or fail outright. Warning rows count
    /// toward "ok" in the stat tiles -- the run did not fail, something just
    /// needs attention -- and the per-row Warning pill (BuildStatusCellTemplate
    /// already defines it; nothing produced it before this) carries the
    /// distinction instead of a fourth tile.
    /// <para>
    /// Used only by AssemblyDrawingRunner.Run (Vendor and Korea both go
    /// through it, though only Vendor's sheet-2 matching currently produces a
    /// warning -- see that method's own note). Cmd_DOC03a_Assembly and
    /// Cmd_DOC03b_MT keep the two-state overload above, unchanged.
    /// </para>
    /// </summary>
    public static void ShowResult(
        string title,
        IReadOnlyList<(string Name, string? Error, string? Warning)> results,
        IntPtr revitHandle = default)
    {
        int ok = results.Count(r => string.IsNullOrEmpty(r.Error));
        int bad = results.Count - ok;

        var rows = results
            .Select((r, i) => new CreateSheetsReportRow
            {
                Index = i + 1,
                Name = r.Name,
                Status = !string.IsNullOrEmpty(r.Error) ? "fail"
                       : !string.IsNullOrEmpty(r.Warning) ? "warn"
                       : "ok",
                Details = r.Error ?? r.Warning ?? "",
            })
            .ToList();

        new CreateSheetsReportWindow(title, rows, ok, bad, revitHandle).ShowDialog();
    }
}

/// <summary>Redesigned report dialog with a DataGrid results table.</summary>
internal sealed class CreateSheetsReportWindow : Window
{
    private readonly IReadOnlyList<CreateSheetsReportRow> _rows;
    private Button _copyBtn = null!;

    public CreateSheetsReportWindow(
        string title,
        IReadOnlyList<CreateSheetsReportRow> rows,
        int ok, int bad,
        IntPtr revitHandle = default)
    {
        _rows = rows;

        Title = title;
        Width = 700;
        Height = 560;
        MinWidth = 560;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        FontFamily = AvTokens.Ui;
        Background = AvTokens.Br(AvTokens.Surface);

        if (revitHandle != IntPtr.Zero)
            _ = new WindowInteropHelper(this) { Owner = revitHandle };

        var root = new DockPanel();

        // Header
        // ReportHeaderBar rather than HeaderStrip: a fixed 52px on Surface with
        // a 24px logo, against HeaderStrip's Auto height on Surface2 with a 28px
        // one. HeaderStrip stays for the three picker windows in this file and
        // for DOC04_AutoDimension.cs and DOC05_AutoTag.cs -- only the report moves.
        var header = AvControls.ReportHeaderBar(title);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // Footer
        var footer = BuildFooter(ok, bad);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Body
        root.Children.Add(BuildBody(ok, bad));
        Content = root;
    }

    // ── Body ─────────────────────────────────────────────────────────────────
    private UIElement BuildBody(int ok, int bad)
    {
        var outer = new DockPanel
        {
            Margin = new Thickness(20, 16, 20, 12),
        };

        // ── Top strip: stat tiles ─────────────────────────────────────────────
        var topStrip = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        DockPanel.SetDock(topStrip, Dock.Top);

        // Stat tiles — span the full width of the top strip.
        //
        // A UniformGrid, matching every other report window in the add-in: the
        // tiles stretch to equal widths across the row, which is also what the
        // design spec's `.stats { display: grid }` describes. This used to be a
        // right-aligned horizontal StackPanel, so the tiles shrink-wrapped their
        // text and clustered at the right edge instead.
        //
        // An overall status pill used to sit to their left. It was removed: the
        // counts it summarised are already carried by the Failed tile, whose
        // value turns red when non-zero, and by the footer's
        // "N succeeded · N failed · N total" caption.
        //
        // Spacing is a right margin on all but the last tile. The other windows
        // use (0,0,8,8); the bottom 8 is omitted here because topStrip already
        // carries its own bottom margin and would otherwise double up.
        var tiles = new UniformGrid
        {
            Columns = 3,
            Rows = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };

        Border MakeTile(string val, string lbl, string accent) =>
            AvControls.StatTile(val, lbl, accent);

        var tileOk = MakeTile(ok.ToString(), "Succeeded", ok > 0 ? "ok" : "");
        var tileBad = MakeTile(bad.ToString(), "Failed", bad > 0 ? "fail" : "");
        var tileTotal = MakeTile((ok + bad).ToString(), "Total", "");

        tileOk.Margin = new Thickness(0, 0, 8, 0);
        tileBad.Margin = new Thickness(0, 0, 8, 0);

        tiles.Children.Add(tileOk);
        tiles.Children.Add(tileBad);
        tiles.Children.Add(tileTotal);

        topStrip.Children.Add(tiles);
        outer.Children.Add(topStrip);
        DockPanel.SetDock(topStrip, Dock.Top);

        // ── Section label ─────────────────────────────────────────────────────
        var sectionLabel = new TextBlock
        {
            Text = "RESULTS",
            FontFamily = AvTokens.Ui,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = AvTokens.Br(AvTokens.TextFaint),
            Margin = new Thickness(0, 0, 0, 6),
        };
        DockPanel.SetDock(sectionLabel, Dock.Top);
        outer.Children.Add(sectionLabel);

        // ── DataGrid ─────────────────────────────────────────────────────────
        var grid = BuildDataGrid();
        outer.Children.Add(grid);   // LastChildFill — fills remaining height

        return outer;
    }

    private DataGrid BuildDataGrid()
    {
        var dg = new DataGrid
        {
            AutoGenerateColumns = false,
            IsReadOnly = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush = AvTokens.Br(AvTokens.Border),
            BorderBrush = AvTokens.Br(AvTokens.Border),
            BorderThickness = new Thickness(1),
            Background = AvTokens.Br(AvTokens.Surface),
            RowBackground = AvTokens.Br(AvTokens.Surface),
            AlternatingRowBackground = AvTokens.Br(AvTokens.Surface2),
            AlternationCount = 2,
            RowHeight = 38,
            CanUserReorderColumns = false,
            CanUserResizeRows = false,
            SelectionMode = DataGridSelectionMode.Single,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        // Apply column header style via XAML
        dg.Resources.Add(
            typeof(DataGridColumnHeader),
            BuildColumnHeaderStyle());

        // Apply cell style
        dg.CellStyle = BuildCellStyle();

        // ── Column: # ─────────────────────────────────────────────────────────
        dg.Columns.Add(new DataGridTextColumn
        {
            Header = "#",
            Binding = new System.Windows.Data.Binding(nameof(CreateSheetsReportRow.Index)),
            Width = new DataGridLength(36),
            ElementStyle = BuildMonoCellStyle(AvTokens.TextFaint, TextAlignment.Center),
        });

        // ── Column: Assembly Name ─────────────────────────────────────────────
        dg.Columns.Add(new DataGridTextColumn
        {
            Header = "ASSEMBLY",
            Binding = new System.Windows.Data.Binding(nameof(CreateSheetsReportRow.Name)),
            Width = new DataGridLength(1, DataGridLengthUnitType.Star),
            ElementStyle = BuildNameCellStyle(),
        });

        // ── Column: Status — pill rendered via inline XAML DataTemplate ──────
        var statusCol = new DataGridTemplateColumn
        {
            Header = "STATUS",
            Width = new DataGridLength(110),
            IsReadOnly = true,
            CellTemplate = BuildStatusCellTemplate(),
        };
        dg.Columns.Add(statusCol);

        // ── Column: Details ───────────────────────────────────────────────────
        dg.Columns.Add(new DataGridTextColumn
        {
            Header = "DETAILS",
            Binding = new System.Windows.Data.Binding(nameof(CreateSheetsReportRow.Details)),
            Width = new DataGridLength(1.4, DataGridLengthUnitType.Star),
            ElementStyle = BuildDetailsCellStyle(),
        });

        dg.ItemsSource = _rows;
        return dg;
    }

    // ── Style helpers ─────────────────────────────────────────────────────────

    private static Style BuildColumnHeaderStyle()
    {
        var s = new Style(typeof(DataGridColumnHeader));
        s.Setters.Add(new Setter(DataGridColumnHeader.FontFamilyProperty, AvTokens.Ui));
        s.Setters.Add(new Setter(DataGridColumnHeader.FontSizeProperty, 10.5));
        s.Setters.Add(new Setter(DataGridColumnHeader.FontWeightProperty, FontWeights.Bold));
        s.Setters.Add(new Setter(DataGridColumnHeader.ForegroundProperty, AvTokens.Br(AvTokens.TextFaint)));
        s.Setters.Add(new Setter(DataGridColumnHeader.BackgroundProperty, AvTokens.Br(AvTokens.Surface2)));
        s.Setters.Add(new Setter(DataGridColumnHeader.PaddingProperty, new Thickness(10, 8, 10, 8)));
        s.Setters.Add(new Setter(DataGridColumnHeader.BorderBrushProperty, AvTokens.Br(AvTokens.BorderStrong)));
        s.Setters.Add(new Setter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 0, 1.5)));
        s.Setters.Add(new Setter(DataGridColumnHeader.HorizontalContentAlignmentProperty, HorizontalAlignment.Left));
        return s;
    }

    private static Style BuildCellStyle()
    {
        var s = new Style(typeof(DataGridCell));
        s.Setters.Add(new Setter(DataGridCell.BorderThicknessProperty, new Thickness(0)));
        s.Setters.Add(new Setter(DataGridCell.FocusVisualStyleProperty, null));
        s.Setters.Add(new Setter(DataGridCell.PaddingProperty, new Thickness(0)));
        return s;
    }

    private static Style BuildMonoCellStyle(string colorHex, TextAlignment align)
    {
        var s = new Style(typeof(TextBlock));
        s.Setters.Add(new Setter(TextBlock.FontFamilyProperty, AvTokens.Mono));
        s.Setters.Add(new Setter(TextBlock.FontSizeProperty, 11.5));
        s.Setters.Add(new Setter(TextBlock.ForegroundProperty, AvTokens.Br(colorHex)));
        s.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, align));
        s.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        s.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(10, 0, 10, 0)));
        return s;
    }

    private static Style BuildNameCellStyle()
    {
        var s = new Style(typeof(TextBlock));
        s.Setters.Add(new Setter(TextBlock.FontFamilyProperty, AvTokens.Ui));
        s.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.5));
        s.Setters.Add(new Setter(TextBlock.FontWeightProperty, FontWeights.SemiBold));
        s.Setters.Add(new Setter(TextBlock.ForegroundProperty, AvTokens.Br(AvTokens.Text)));
        s.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        s.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(10, 0, 10, 0)));
        s.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        return s;
    }

    private static Style BuildDetailsCellStyle()
    {
        var s = new Style(typeof(TextBlock));
        s.Setters.Add(new Setter(TextBlock.FontFamilyProperty, AvTokens.Ui));
        s.Setters.Add(new Setter(TextBlock.FontSizeProperty, 11.5));
        s.Setters.Add(new Setter(TextBlock.ForegroundProperty, AvTokens.Br(AvTokens.TextFaint)));
        s.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center));
        s.Setters.Add(new Setter(TextBlock.PaddingProperty, new Thickness(10, 0, 10, 0)));
        s.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.NoWrap));
        s.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        // Rows are a fixed 38px, so wrapping isn't an option without changing
        // every row's height for every command that shares this window --
        // a tooltip surfaces the untruncated text without touching that.
        // Harmless on short text: a tooltip that matches the visible text
        // exactly is a no-op the user never needs to notice.
        s.Setters.Add(new Setter(
            FrameworkElement.ToolTipProperty,
            new System.Windows.Data.Binding(nameof(CreateSheetsReportRow.Details))));
        return s;
    }

    /// <summary>
    /// Builds the Status cell template entirely in inline XAML — no
    /// FrameworkElementFactory, no runtime tree mutation, no recursion.
    /// Renders an AvesUi-style pill (coloured dot + label) using DataTriggers
    /// on the bound Status string ("ok" | "fail" | "warn" | "skip").
    /// </summary>
    private static DataTemplate BuildStatusCellTemplate()
    {
        // Colour definitions matching AvesUiHelper constants
        const string okBg = "#E4F3EA"; const string okFg = "#2C7A52"; const string okLabel = "Done";
        const string failBg = "#FBE3DE"; const string failFg = "#C0381F"; const string failLabel = "Failed";
        const string warnBg = "#FAF0DA"; const string warnFg = "#9A6B12"; const string warnLabel = "Warning";
        const string skipBg = "#EDEFF2"; const string skipFg = "#5B6573"; const string skipLabel = "Skipped";

        // Build inline XAML: a Border (pill shape) containing a horizontal
        // StackPanel with a coloured Ellipse dot + TextBlock label.
        // Default (ok) colours are set on the elements; DataTriggers override
        // for each other status value.
        string xaml =
            "<DataTemplate " +
            "  xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "  xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
            "  <Border x:Name=\"pill\" CornerRadius=\"6\" Margin=\"10,0,10,0\"" +
            $"    Background=\"{okBg}\" Padding=\"8,5,10,5\"" +
            "    HorizontalAlignment=\"Left\" VerticalAlignment=\"Center\">" +
            "    <StackPanel Orientation=\"Horizontal\">" +
            $"      <Ellipse x:Name=\"dot\" Width=\"7\" Height=\"7\" Fill=\"{okFg}\"" +
            "        VerticalAlignment=\"Center\" Margin=\"0,0,6,0\"/>" +
            $"      <TextBlock x:Name=\"lbl\" Text=\"{okLabel}\" Foreground=\"{okFg}\"" +
            "        FontSize=\"11.5\" FontWeight=\"SemiBold\" VerticalAlignment=\"Center\"/>" +
            "    </StackPanel>" +
            "  </Border>" +
            "  <DataTemplate.Triggers>" +
            // fail
            $"    <DataTrigger Binding=\"{{Binding Status}}\" Value=\"fail\">" +
            $"      <Setter TargetName=\"pill\" Property=\"Background\" Value=\"{failBg}\"/>" +
            $"      <Setter TargetName=\"dot\"  Property=\"Fill\"       Value=\"{failFg}\"/>" +
            $"      <Setter TargetName=\"lbl\"  Property=\"Foreground\" Value=\"{failFg}\"/>" +
            $"      <Setter TargetName=\"lbl\"  Property=\"Text\"       Value=\"{failLabel}\"/>" +
            "    </DataTrigger>" +
            // warn
            $"    <DataTrigger Binding=\"{{Binding Status}}\" Value=\"warn\">" +
            $"      <Setter TargetName=\"pill\" Property=\"Background\" Value=\"{warnBg}\"/>" +
            $"      <Setter TargetName=\"dot\"  Property=\"Fill\"       Value=\"{warnFg}\"/>" +
            $"      <Setter TargetName=\"lbl\"  Property=\"Foreground\" Value=\"{warnFg}\"/>" +
            $"      <Setter TargetName=\"lbl\"  Property=\"Text\"       Value=\"{warnLabel}\"/>" +
            "    </DataTrigger>" +
            // skip
            $"    <DataTrigger Binding=\"{{Binding Status}}\" Value=\"skip\">" +
            $"      <Setter TargetName=\"pill\" Property=\"Background\" Value=\"{skipBg}\"/>" +
            $"      <Setter TargetName=\"dot\"  Property=\"Fill\"       Value=\"{skipFg}\"/>" +
            $"      <Setter TargetName=\"lbl\"  Property=\"Foreground\" Value=\"{skipFg}\"/>" +
            $"      <Setter TargetName=\"lbl\"  Property=\"Text\"       Value=\"{skipLabel}\"/>" +
            "    </DataTrigger>" +
            "  </DataTemplate.Triggers>" +
            "</DataTemplate>";

        return (DataTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    // ── Footer ────────────────────────────────────────────────────────────────
    private Border BuildFooter(int ok, int bad)
    {
        // Left: summary counts as text. This window reports counts rather than a
        // source document, which is its own wording and stays.
        var summary = AvControls.FooterCaption($"{ok} succeeded · {bad} failed · {ok + bad} total");

        _copyBtn = AvControls.SecondaryButtonHoverLoaded("⧉  Copy to clipboard");
        _copyBtn.Margin = new Thickness(0, 0, 8, 0);
        _copyBtn.Click += OnCopy;

        var closeBtn = AvControls.PrimaryButtonHoverLoaded("Close");
        closeBtn.IsDefault = true;
        closeBtn.IsCancel = true;
        closeBtn.Click += (_, _) => Close();

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        btnRow.Children.Add(_copyBtn);
        btnRow.Children.Add(closeBtn);

        // Buttons docked first, caption added last as the fill child: docked Left
        // it sized to content and would have pushed the buttons rather than
        // ellipsing.
        var inner = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(btnRow, Dock.Right);
        inner.Children.Add(btnRow);
        inner.Children.Add(summary);

        return AvControls.FooterStrip(inner);
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            // TSV format: #\tAssembly\tStatus\tDetails
            var sb = new StringBuilder();
            sb.AppendLine("#\tAssembly\tStatus\tDetails");
            foreach (var r in _rows)
                sb.AppendLine($"{r.Index}\t{r.Name}\t{r.Status}\t{r.Details}");

            Clipboard.SetText(sb.ToString());
            _copyBtn.Content = "✓  Copied!";
            var timer = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) => { _copyBtn.Content = "⧉  Copy to clipboard"; timer.Stop(); };
            timer.Start();
        }
        catch { }
    }
}


/// <summary>Minimal WPF list picker — styled with AVES tokens.</summary>
internal sealed class TemplatePickerWindow : Window
{
    public string? SelectedTemplateName { get; private set; }
    private readonly ListBox _listBox;
    private readonly List<string> _allNames;

    public TemplatePickerWindow(IReadOnlyList<string> names, string title,
        IntPtr revitHandle = default)
    {
        _allNames = names.ToList();

        Title = title;
        Width = 460;
        Height = 580;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;      // allow resize so long lists work
        // ToolWindow gives a close-only title bar while KEEPING resize.
        // ResizeMode.NoResize would also hide minimize/maximize but takes resizing
        // with it, which the list needs.
        WindowStyle = WindowStyle.ToolWindow;
        MinWidth = 340;
        MinHeight = 340;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        FontFamily = AvTokens.Ui;
        // Surface, not Backdrop. Backdrop is for the area BEHIND a window's
        // content when using custom chrome — not the body itself, which the
        // Refined Native token table puts on Surface.
        Background = AvTokens.Br(AvTokens.Surface);

        if (revitHandle != IntPtr.Zero)
            _ = new WindowInteropHelper(this) { Owner = revitHandle };

        var root = new DockPanel();

        // ── Header strip ──────────────────────────────────────────────────────
        var header = AvControls.HeaderStrip(title);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ── Footer ────────────────────────────────────────────────────────────
        var okBtn = AvControls.PrimaryButtonHoverLoaded("OK");
        okBtn.IsDefault = true;
        okBtn.Click += (_, _) => Confirm();

        var cancelBtn = AvControls.GhostButtonHoverLoaded("Cancel");
        cancelBtn.IsCancel = true;
        cancelBtn.Margin = new Thickness(0, 0, 8, 0);
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        // Cancel left, primary right — matches LayoutSettingsDialogBase in this
        // file and TemplateMenuWindow, so the whole flow reads the same way.
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);

        var footer = new Border
        {
            Background = AvTokens.Br(AvTokens.Surface2),
            BorderBrush = AvTokens.Br(AvTokens.Border),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14, 20, 14),
            Child = btnRow,
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // ── Body — DockPanel so ListBox fills all remaining height ────────────
        var body = new DockPanel { Margin = new Thickness(20, 16, 20, 16) };

        // ── Search box — sits directly under the header, no instruction bar ───
        // Container: Surface2 bg, 1px BorderStrong border, radius-5 look via
        // inner padding. Icon drawn as a TextBlock glyph (🔍 or plain text).
        var searchIcon = new TextBlock
        {
            Text = "⌕",
            FontSize = 15,
            Foreground = AvTokens.Br(AvTokens.TextFaint),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 6, 0),
        };

        var searchBox = new TextBox
        {
            FontFamily = AvTokens.Ui,
            FontSize = 13,
            Foreground = AvTokens.Br(AvTokens.Text),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0, 6, 8, 6),
        };

        // Placeholder text behaviour
        var placeholder = new TextBlock
        {
            Text = "Filter templates…",
            FontFamily = AvTokens.Ui,
            FontSize = 13,
            Foreground = AvTokens.Br(AvTokens.TextFaint),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            IsHitTestVisible = false,
        };

        // Show/hide placeholder
        searchBox.TextChanged += (_, _) =>
        {
            placeholder.Visibility = string.IsNullOrEmpty(searchBox.Text)
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
            FilterList(searchBox.Text);
        };

        // Overlay grid so placeholder sits behind the TextBox
        var searchInner = new Grid();
        searchInner.Children.Add(placeholder);
        searchInner.Children.Add(searchBox);

        var searchRow = new StackPanel { Orientation = Orientation.Horizontal };
        searchRow.Children.Add(searchIcon);
        searchRow.Children.Add(searchInner);
        Grid.SetColumn(searchInner, 0);

        var searchBorder = new Border
        {
            Background = AvTokens.Br(AvTokens.Surface2),
            BorderBrush = AvTokens.Br(AvTokens.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 0, 0, 8),
            Child = searchRow,
        };

        // Make the TextBox fill the remaining width inside searchRow
        searchInner.HorizontalAlignment = HorizontalAlignment.Stretch;
        searchBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        searchRow.HorizontalAlignment = HorizontalAlignment.Stretch;

        DockPanel.SetDock(searchBorder, Dock.Top);
        body.Children.Add(searchBorder);

        // ── ListBox — fills all remaining space, scrollbar always visible ─────
        _listBox = new ListBox
        {
            ItemsSource = _allNames,
            FontFamily = AvTokens.Ui,
            FontSize = 13,
            Foreground = AvTokens.Br(AvTokens.Text),
            Background = AvTokens.Br(AvTokens.Surface),
            BorderBrush = AvTokens.Br(AvTokens.Border),
            BorderThickness = new Thickness(1),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
        };
        // Shared row styling: generous padding, rounded rows, hover tint and
        // accent-coloured selection, same as the other AVES pickers.
        _listBox.ItemContainerStyle = AvControls.PickerListItemStyle();
        // Attached properties must be set via SetValue, not in the initializer.
        // Vertical scroll always on so mouse-wheel is captured by the ListBox
        // itself rather than bubbling to the window.
        _listBox.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,
            ScrollBarVisibility.Visible);
        _listBox.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,
            ScrollBarVisibility.Disabled);
        _listBox.MouseDoubleClick += (_, _) => Confirm();

        // LastChildFill = true (default) — ListBox stretches to fill body
        body.Children.Add(_listBox);

        root.Children.Add(body);
        Content = root;

        // Focus the search box immediately so the user can type right away
        Loaded += (_, _) => searchBox.Focus();
    }

    // ── Live filter ───────────────────────────────────────────────────────────
    private void FilterList(string query)
    {
        var filtered = string.IsNullOrWhiteSpace(query)
            ? _allNames
            : _allNames
                .Where(n => n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();

        _listBox.ItemsSource = filtered;

        // Re-select first match if the previous selection dropped out
        if (_listBox.SelectedItem is null && filtered.Count > 0)
            _listBox.SelectedIndex = 0;
    }

    private void Confirm()
    {
        if (_listBox.SelectedItem is not string name) return;
        SelectedTemplateName = name;
        DialogResult = true;
        Close();
    }
}

/// <summary>
/// Main menu for the three template selections. Three numbered rows, each
/// opening the existing TemplatePickerWindow as its own popup, plus a Run button
/// that stays disabled until all three are chosen.
///
/// Replaces a forced sequential wizard: the three picks were previously three
/// showings of the same window, in fixed order, with no way to revisit an earlier
/// one. Selections are now made in any order and remain visible on the menu.
///
/// Cancel semantics are deliberately unchanged — cancelling ANY popup aborts the
/// whole flow, matching the previous all-or-nothing contract that both callers
/// rely on. A popup's Cancel does not merely return to the menu.
/// </summary>
internal sealed class TemplateMenuWindow : Window
{
    private const string Placeholder = "Not selected";

    public View? V3d { get; private set; }
    public View? V2d { get; private set; }
    public View? Sched { get; private set; }

    /// <summary>Title block type the sheets will be created with.</summary>
    public FamilySymbol? Titleblock { get; private set; }

    /// <summary>Step 5 -- which categories of output this run generates.
    /// All three start checked, so an untouched Step 5 is today's behaviour.</summary>
    public bool Generate3DViews { get; private set; } = true;
    public bool GenerateSchedules { get; private set; } = true;
    public bool Generate2DViews { get; private set; } = true;

    // One pre-filtered list per step, so each picker offers only templates that
    // can actually be applied to the view that step creates.
    private readonly IReadOnlyDictionary<string, View> _view3dTemplates;
    private readonly IReadOnlyDictionary<string, View> _view2dTemplates;
    private readonly IReadOnlyDictionary<string, View> _scheduleTemplates;
    private readonly IReadOnlyDictionary<string, FamilySymbol> _titleblocks;
    private readonly IntPtr _revitHandle;

    private readonly Button _row3d;
    private readonly Button _row2d;
    private readonly Button _rowSched;
    private readonly Button _rowTitleblock;
    private readonly Button _continueBtn;

    // Assembly checklist. Rows are built once and hidden/shown by the filter, so
    // typing in the search box never disturbs what is already ticked.
    private readonly List<(AssemblyInstance Assembly, Border Row, CheckBox Box)> _assemblyRows = [];
    private readonly TextBox _assemblySearch;
    private readonly TextBlock _assemblyCount;

    /// <summary>Assemblies ticked in the right-hand column, in the order given.</summary>
    public List<AssemblyInstance> SelectedAssemblies { get; private set; } = [];

    public TemplateMenuWindow(
        IReadOnlyDictionary<string, View> view3dTemplates,
        IReadOnlyDictionary<string, View> view2dTemplates,
        IReadOnlyDictionary<string, View> scheduleTemplates,
        IReadOnlyDictionary<string, FamilySymbol> titleblocks,
        FamilySymbol? preselectedTitleblock,
        View? preselectedView3D,
        View? preselectedView2D,
        View? preselectedSchedule,
        IReadOnlyList<AssemblyInstance> assemblies,
        IntPtr revitHandle = default)
    {
        _view3dTemplates = view3dTemplates;
        _view2dTemplates = view2dTemplates;
        _scheduleTemplates = scheduleTemplates;
        _titleblocks = titleblocks;
        Titleblock = preselectedTitleblock;
        V3d = preselectedView3D;
        V2d = preselectedView2D;
        Sched = preselectedSchedule;
        _revitHandle = revitHandle;

        Title = "Drawing Automations";
        Width = 900;
        MinWidth = 720;

        // Height follows the content instead of a fixed value. A fixed height had
        // to be guessed against the rows' measured size and was ~18px short at
        // 100% scaling, worse as DPI rose, which pushed the last row under the
        // ScrollViewer and read as the window being cut off. Auto-sizing measures
        // in DIPs, so it adapts to display scaling.
        //
        // Width deliberately stays fixed: the picker boxes stretch to fill it, and
        // their value text trims with an ellipsis, which needs a bounded width to
        // trim against. SizeToContent.WidthAndHeight would grow the window to the
        // longest template name and the trimming would never engage.
        SizeToContent = SizeToContent.Height;

        // Ceiling so a menu with many assemblies cannot outgrow the display.
        MaxHeight = SystemParameters.WorkArea.Height * 0.9;

        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = AvTokens.Ui;
        // Surface, not Backdrop — see TemplatePickerWindow for the reasoning.
        Background = AvTokens.Br(AvTokens.Surface);

        if (revitHandle != IntPtr.Zero)
            _ = new WindowInteropHelper(this) { Owner = revitHandle };

        var root = new DockPanel();

        var header = AvControls.HeaderStrip("Drawing Setup");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ── Footer ────────────────────────────────────────────────────────────
        // "Continue", not "Run": this menu only advances to the settings dialog,
        // which carries the actual "▶  Run Script" button.
        _continueBtn = AvControls.PrimaryButtonHoverLoaded("Continue");
        _continueBtn.IsDefault = true;
        _continueBtn.IsEnabled = false;
        _continueBtn.Click += (_, _) => { CaptureSelectedAssemblies(); DialogResult = true; Close(); };

        var cancelBtn = AvControls.GhostButtonHoverLoaded("Cancel");
        cancelBtn.IsCancel = true;
        cancelBtn.Margin = new Thickness(0, 0, 8, 0);
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        // Cancel left, primary right — matches LayoutSettingsDialogBase in this
        // file and TemplatePickerWindow, so the whole flow reads the same way.
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(_continueBtn);

        var footer = new Border
        {
            Background = AvTokens.Br(AvTokens.Surface2),
            BorderBrush = AvTokens.Br(AvTokens.Border),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14, 20, 14),
            Child = btnRow,
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // ── Body — drawing setup on the left, assembly selection on the right ──
        var columns = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ── Left column — Generate, then the four pickers ─────────────────────
        var setupStack = new StackPanel();
        setupStack.Children.Add(new TextBlock
        {
            Text = "Choose what to generate, then a template for each step.",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = AvTokens.Br(AvTokens.TextDim),
            Margin = new Thickness(0, 0, 0, 12),
        });

        _row3d = AvControls.PickerButton(Placeholder, showChevron: true);
        _row3d.Click += (_, _) => PickInto(
            _view3dTemplates, "Select 3D View Template", _row3d, v => V3d = v);

        _row2d = AvControls.PickerButton(Placeholder, showChevron: true);
        _row2d.Click += (_, _) => PickInto(
            _view2dTemplates, "Select 2D View Template", _row2d, v => V2d = v);

        _rowSched = AvControls.PickerButton(Placeholder, showChevron: true);
        _rowSched.Click += (_, _) => PickInto(
            _scheduleTemplates, "Select Schedule Template", _rowSched, v => Sched = v);

        _rowTitleblock = AvControls.PickerButton(Placeholder, showChevron: true);
        _rowTitleblock.Click += (_, _) => PickTitleblock();

        // Nothing is chosen yet, so start every box in the faint placeholder
        // state rather than the normal text colour PickerButton defaults to.
        // A restored template shows its name straight away; anything not
        // restored keeps the faint placeholder it has always had.
        AvControls.SetPickerValue(_row3d, V3d?.Name, Placeholder);
        AvControls.SetPickerValue(_row2d, V2d?.Name, Placeholder);
        AvControls.SetPickerValue(_rowSched, Sched?.Name, Placeholder);

        // The title block is the one step that can start already answered: a
        // previously saved choice is carried in and shown straight away.
        AvControls.SetPickerValue(
            _rowTitleblock,
            Titleblock is null ? null : TitleblockUtils.DisplayName(Titleblock),
            Placeholder);

        // Generate leads: it decides which template pickers below it are even
        // relevant. Every box starts checked, so an untouched window behaves the
        // way it did before selective generation existed.
        setupStack.Children.Add(AvControls.FieldLabel(1, "Generate"));

        var (row3dViews, box3dViews) = AvControls.CheckRow("3D Views", true);
        var (row2dViews, box2dViews) = AvControls.CheckRow("2D Views", false);
        var (rowSchedules, boxSchedules) = AvControls.CheckRow("Schedules", false);

        box3dViews.IsChecked = true;
        box2dViews.IsChecked = true;
        boxSchedules.IsChecked = true;

        // Unchecking a category greys out its picker but never clears the choice,
        // so re-checking it brings the previous template straight back.
        WireGenerateRow(row3dViews, box3dViews, v =>
        {
            Generate3DViews = v;
            _row3d.IsEnabled = v;
            UpdateContinueEnabled();
        });
        WireGenerateRow(row2dViews, box2dViews, v =>
        {
            Generate2DViews = v;
            _row2d.IsEnabled = v;
            UpdateContinueEnabled();
        });
        WireGenerateRow(rowSchedules, boxSchedules, v =>
        {
            GenerateSchedules = v;
            _rowSched.IsEnabled = v;
            UpdateContinueEnabled();
        });

        setupStack.Children.Add(row3dViews);
        setupStack.Children.Add(row2dViews);
        setupStack.Children.Add(rowSchedules);
        setupStack.Children.Add(new Border { Height = 14 });

        AddStep(setupStack, 2, "3D View Template", _row3d);
        AddStep(setupStack, 3, "2D View Template", _row2d);
        AddStep(setupStack, 4, "Schedule Template", _rowSched);
        AddStep(setupStack, 5, "Title Block", _rowTitleblock);

        var setupScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = setupStack,
        };
        Grid.SetColumn(setupScroller, 0);
        columns.Children.Add(setupScroller);

        // ── Right column — assembly selection ─────────────────────────────────
        var assemblyPanel = new DockPanel();

        var assemblyHeader = AvControls.FieldLabel(6, "Assemblies");
        DockPanel.SetDock(assemblyHeader, Dock.Top);
        assemblyPanel.Children.Add(assemblyHeader);

        // Search box: the same Surface2 / BorderStrong / radius-5 wrapper with a
        // magnifier glyph that the Part List Format picker uses, so the two
        // assembly lists in this add-in look like one control.
        var searchWrap = new Border
        {
            Background = AvTokens.Br(AvTokens.Surface2),
            BorderBrush = AvTokens.Br(AvTokens.BorderStrong),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Margin = new Thickness(0, 0, 0, 10),
            Padding = new Thickness(10, 0, 10, 0),
            Height = 34,
        };

        var searchRow = new StackPanel { Orientation = Orientation.Horizontal };
        searchRow.Children.Add(new TextBlock
        {
            Text = "⌕",
            FontSize = 15,
            Foreground = AvTokens.Br(AvTokens.TextFaint),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 7, 0),
        });

        _assemblySearch = new TextBox
        {
            FontFamily = AvTokens.Ui,
            FontSize = 13,
            Foreground = AvTokens.Br(AvTokens.Text),
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 200,
            ToolTip = "Filter assemblies by name",
        };
        _assemblySearch.TextChanged += (_, _) => ApplyAssemblyFilter();

        searchRow.Children.Add(_assemblySearch);
        searchWrap.Child = searchRow;
        DockPanel.SetDock(searchWrap, Dock.Top);
        assemblyPanel.Children.Add(searchWrap);

        // Same SecondaryBtn pair the Part List Format picker uses, down to the
        // accent-coloured Select All and the 6px gap between them.
        var selectAllBtn = AvControls.SecondaryBtn("Select All", () => SetAllAssembliesChecked(true));
        selectAllBtn.Foreground = AvTokens.Br(AvTokens.Accent);
        selectAllBtn.Margin = new Thickness(0, 0, 6, 0);

        var deselectAllBtn = AvControls.SecondaryBtn("Deselect All", () => SetAllAssembliesChecked(false));
        deselectAllBtn.Margin = new Thickness(0, 0, 0, 0);

        _assemblyCount = new TextBlock
        {
            FontFamily = AvTokens.Ui,
            FontSize = 11,
            Foreground = AvTokens.Br(AvTokens.TextFaint),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };

        var toolRow = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        toolRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        toolRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(selectAllBtn, 0);
        Grid.SetColumn(deselectAllBtn, 1);
        Grid.SetColumn(_assemblyCount, 2);
        toolRow.Children.Add(selectAllBtn);
        toolRow.Children.Add(deselectAllBtn);
        toolRow.Children.Add(_assemblyCount);
        DockPanel.SetDock(toolRow, Dock.Top);
        assemblyPanel.Children.Add(toolRow);

        // Nothing starts ticked: the user opts in to what this run touches,
        // and Continue stays disabled until at least one row is chosen.
        var assemblyList = new StackPanel();
        var first = true;
        foreach (var assembly in assemblies)
        {
            var (row, box) = AvControls.CheckRow(assembly.Name, first);
            first = false;

            box.IsChecked = false;
            row.MouseLeftButtonUp += (_, _) => box.IsChecked = !(box.IsChecked == true);
            box.Checked += (_, _) => UpdateAssemblyCount();
            box.Unchecked += (_, _) => UpdateAssemblyCount();

            assemblyList.Children.Add(row);
            _assemblyRows.Add((assembly, row, box));
        }

        var assemblyScroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 420,
            Content = assemblyList,
            BorderBrush = AvTokens.Br(AvTokens.Border),
            BorderThickness = new Thickness(1),
        };
        assemblyPanel.Children.Add(assemblyScroller);

        Grid.SetColumn(assemblyPanel, 2);
        columns.Children.Add(assemblyPanel);

        root.Children.Add(columns);

        Content = root;

        UpdateAssemblyCount();

    }
    /// <summary>
    /// Makes a Generate row behave like the picker rows below it: the whole
    /// toggles, and the checkbox drives the backing flag.
    /// </summary>
    private static void WireGenerateRow(
        System.Windows.Controls.Border row,
        System.Windows.Controls.CheckBox box,
        Action<bool> assign)
    {
        row.MouseLeftButtonUp += (_, _) => box.IsChecked = !(box.IsChecked == true);
        box.Checked += (_, _) => assign(true);
        box.Unchecked += (_, _) => assign(false);
    }

    private static void AddStep(StackPanel stack, int step, string title, Button box)
    {
        stack.Children.Add(AvControls.FieldLabel(step, title));
        box.Margin = new Thickness(0, 0, 0, 16);
        stack.Children.Add(box);
    }

    /// <summary>
    /// Opens the section popup. Cancelling it aborts the entire flow rather than
    /// returning to the menu — see the class remarks on preserved cancel
    /// semantics.
    /// </summary>
    private void PickInto(
        IReadOnlyDictionary<string, View> source, string title,
        Button row, Action<View?> assign)
    {
        var picked = TemplatePicker.Pick(source, title, _revitHandle);
        if (picked is null)
        {
            DialogResult = false;
            Close();
            return;
        }

        assign(picked);
        AvControls.SetPickerValue(row, picked.Name, Placeholder);
        UpdateContinueEnabled();
    }

    /// <summary>
    /// Title block step. Unlike the template steps this one may already hold a
    /// saved value, so cancelling the popup keeps what is there rather than
    /// aborting the flow.
    /// </summary>
    private void PickTitleblock()
    {
        var picked = TemplatePicker.PickName(
            _titleblocks.Keys, "Select Title Block", _revitHandle);
        if (picked is null) return;

        Titleblock = _titleblocks[picked];
        AvControls.SetPickerValue(_rowTitleblock, picked, Placeholder);
        UpdateContinueEnabled();
    }


    /// <summary>
    /// Live filter over the assembly checklist. Rows are hidden rather than
    /// rebuilt, so what the user has already ticked survives every keystroke.
    /// </summary>
    private void ApplyAssemblyFilter()
    {
        var filter = _assemblySearch.Text?.Trim() ?? string.Empty;

        foreach (var (assembly, row, _) in _assemblyRows)
        {
            var match = filter.Length == 0
                || assembly.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;

            row.Visibility = match
                ? System.Windows.Visibility.Visible
                : System.Windows.Visibility.Collapsed;
        }

        UpdateAssemblyCount();
    }

    /// <summary>
    /// Ticks or clears every row the filter is currently showing, so Select All
    /// after a search applies to the search rather than the whole model.
    /// </summary>
    private void SetAllAssembliesChecked(bool value)
    {
        foreach (var (_, row, box) in _assemblyRows)
        {
            if (row.Visibility == System.Windows.Visibility.Visible)
                box.IsChecked = value;
        }

        UpdateAssemblyCount();
    }

    private void UpdateAssemblyCount()
    {
        var total = _assemblyRows.Count;
        var selected = _assemblyRows.Count(r => r.Box.IsChecked == true);

        // Always "x of y": the zero case reads as a state, not as an empty
        // list. Counted over every row, never the filtered subset, so a
        // narrow search still reports the whole dataset.
        _assemblyCount.Text = $"{selected} of {total} selected";

        UpdateContinueEnabled();
    }

    private void CaptureSelectedAssemblies() =>
        SelectedAssemblies = _assemblyRows
            .Where(r => r.Box.IsChecked == true)
            .Select(r => r.Assembly)
            .ToList();

    /// <summary>
    /// Continue needs a runnable request: something to draw, something to draw it
    /// on, a template for every category actually being generated, and at least
    /// one assembly. A category that is switched off needs no template.
    /// </summary>
    private void UpdateContinueEnabled()
    {
        var anyCategory = Generate3DViews || Generate2DViews || GenerateSchedules;
        var templatesReady =
            (!Generate3DViews || V3d is not null)
            && (!Generate2DViews || V2d is not null)
            && (!GenerateSchedules || Sched is not null);
        var anyAssembly = _assemblyRows.Any(r => r.Box.IsChecked == true);

        _continueBtn.IsEnabled =
            anyCategory && templatesReady && Titleblock is not null && anyAssembly;
    }
}

/// <summary>
/// Everything the setup window collects: the three view templates, the title
/// block the sheets are created with, and which categories of output this run
/// should generate. The generate flags default to true, so a run that leaves
/// Step 5 alone behaves exactly as it did before selective generation existed.
/// </summary>
internal sealed class DrawingSetup
{
    public View? V3d { get; set; }
    public View? V2d { get; set; }
    public View? Sched { get; set; }
    public FamilySymbol? Titleblock { get; set; }

    /// <summary>Assemblies the user ticked, already in the alphabetical order
    /// the collector produced.</summary>
    public IReadOnlyList<AssemblyInstance> SelectedAssemblies { get; set; } = [];

    public bool Generate3DViews { get; set; } = true;
    public bool GenerateSchedules { get; set; } = true;
    public bool Generate2DViews { get; set; } = true;

    /// <summary>
    /// A runnable request: a title block, at least one assembly, at least one
    /// category, and a template for every category actually being generated.
    /// </summary>
    public bool IsComplete =>
        Titleblock is not null
        && SelectedAssemblies.Count > 0
        && (Generate3DViews || Generate2DViews || GenerateSchedules)
        && (!Generate3DViews || V3d is not null)
        && (!Generate2DViews || V2d is not null)
        && (!GenerateSchedules || Sched is not null);
}

internal static class TemplatePicker
{
    public static View? Pick(IReadOnlyDictionary<string, View> templates, string title,
        IntPtr revitHandle = default)
    {
        var sortedNames = templates.Keys.OrderBy(k => k).ToList();
        var dialog = new TemplatePickerWindow(sortedNames, title, revitHandle);
        var ok = dialog.ShowDialog();
        if (ok != true || dialog.SelectedTemplateName is null) return null;
        return templates[dialog.SelectedTemplateName];
    }

    /// <summary>Same popup, for a plain list of names (title blocks).</summary>
    public static string? PickName(IEnumerable<string> names, string title,
        IntPtr revitHandle = default)
    {
        var sorted = names.OrderBy(n => n).ToList();
        var dialog = new TemplatePickerWindow(sorted, title, revitHandle);
        return dialog.ShowDialog() == true ? dialog.SelectedTemplateName : null;
    }

    // ── Per-step template filtering ──────────────────────────────────────────
    //
    // Every step used to receive the same unfiltered list, so each one offered
    // the other kinds' templates. That is not merely untidy: a template can only
    // be applied to a view whose ViewType it matches, so picking the wrong kind
    // failed at apply time, after the user had already made the other choices.
    //
    // All three filter the existing dictionary rather than re-collecting from the
    // document, which is what lets PickSetup keep its signature and all call
    // sites stay untouched.

    /// <summary>Templates applicable to the orthographic 3D view.</summary>
    private static IReadOnlyDictionary<string, View> View3dTemplatesOf(
        IReadOnlyDictionary<string, View> templates) =>
        FilterByType(templates, ViewType.ThreeD);

    /// <summary>
    /// Templates applicable to the assembly detail views.
    ///
    /// Elevation, Section and Detail are one interchangeable group, not three
    /// separate cases: Revit's own View Template "View Type Filter" lists them
    /// together as "Elevations, Sections, Detail Views", and that grouping is
    /// exactly the span of the ViewSection class the API returns here
    /// (AssemblyViewUtils.CreateDetailSection). So there is no need to pin down
    /// which single ViewType an assembly detail view reports as — any template
    /// from this group applies to all three.
    ///
    /// Plan-family types (FloorPlan, CeilingPlan, AreaPlan, EngineeringPlan) and
    /// DraftingView belong to different filter groups and are deliberately
    /// excluded, even though they are "2D" in the loose sense.
    /// </summary>
    private static IReadOnlyDictionary<string, View> View2dTemplatesOf(
        IReadOnlyDictionary<string, View> templates) =>
        FilterByType(templates, ViewType.Elevation, ViewType.Section, ViewType.Detail);

    /// <summary>Templates applicable to the part-list schedule.</summary>
    private static IReadOnlyDictionary<string, View> ScheduleTemplatesOf(
        IReadOnlyDictionary<string, View> templates) =>
        FilterByType(templates, ViewType.Schedule);

    private static IReadOnlyDictionary<string, View> FilterByType(
        IReadOnlyDictionary<string, View> templates, params ViewType[] kinds) =>
        templates
            .Where(kv => Array.IndexOf(kinds, kv.Value.ViewType) >= 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    /// <summary>
    /// The setup window: three view templates plus the title block the sheets
    /// will be created with. <paramref name="savedTitleblockName"/> preselects a
    /// previously stored choice; if it no longer exists the first available one
    /// is offered instead and the user is told.
    /// </summary>
    public static DrawingSetup? PickSetup(
        IReadOnlyDictionary<string, View> templates,
        Document doc,
        string? savedTitleblockName,
        string? savedView3DTemplateName,
        string? savedView2DTemplateName,
        string? savedScheduleTemplateName,
        IReadOnlyList<AssemblyInstance> assemblies,
        string alertTitle,
        IntPtr revitHandle = default)
    {
        var view3dTemplates = View3dTemplatesOf(templates);
        if (view3dTemplates.Count == 0)
        {
            AvControls.ShowAlert(alertTitle,
                "No 3D view templates found in this project.\n\n" +
                "A 3D view template is required for the assembly's 3D view. " +
                "Create one, then run this command again.",
                AlertSeverity.Warning);
            return null;
        }

        var view2dTemplates = View2dTemplatesOf(templates);
        if (view2dTemplates.Count == 0)
        {
            AvControls.ShowAlert(alertTitle,
                "No 2D view templates found in this project.\n\n" +
                "An elevation, section or detail view template is required for " +
                "the assembly's detail views. Create one, then run this command " +
                "again.",
                AlertSeverity.Warning);
            return null;
        }

        var scheduleTemplates = ScheduleTemplatesOf(templates);
        if (scheduleTemplates.Count == 0)
        {
            AvControls.ShowAlert(alertTitle,
                "No schedule view templates found in this project.\n\n" +
                "A schedule template is required for the part list. Create one, " +
                "then run this command again.",
                AlertSeverity.Warning);
            return null;
        }

        var titleblocks = TitleblockUtils.CollectSymbols(doc);
        if (titleblocks.Count == 0)
        {
            AvControls.ShowAlert(alertTitle,
                "No title block types found in this project.\n\n" +
                "A title block is required to create sheets. Load one, then run " +
                "this command again.",
                AlertSeverity.Warning);
            return null;
        }

        var preselected = TitleblockUtils.Resolve(
            titleblocks, savedTitleblockName, out var fellBack);

        if (fellBack)
        {
            AvControls.ShowAlert(alertTitle,
                $"The saved title block \"{savedTitleblockName}\" is no longer in " +
                "this project.\n\n" +
                $"\"{TitleblockUtils.DisplayName(preselected!)}\" has been selected " +
                "instead. Pick a different one in the setup window if you need to.",
                AlertSeverity.Warning);
        }

        // A saved template is restored only if a template of that name is still
        // offered for that step. Anything renamed, deleted or now filtered out
        // simply resolves to null, which is the unselected default.
        view3dTemplates.TryGetValue(savedView3DTemplateName ?? string.Empty, out var preset3d);
        view2dTemplates.TryGetValue(savedView2DTemplateName ?? string.Empty, out var preset2d);
        scheduleTemplates.TryGetValue(savedScheduleTemplateName ?? string.Empty, out var presetSched);

        var menu = new TemplateMenuWindow(
            view3dTemplates, view2dTemplates, scheduleTemplates,
            titleblocks, preselected, preset3d, preset2d, presetSched,
            assemblies, revitHandle);
        if (menu.ShowDialog() != true) return null;

        // Belt-and-braces: Continue enables only on a runnable request, but the
        // tuple contract is all-or-nothing, so anything missing here is a cancel.
        if (menu.Titleblock is null || menu.SelectedAssemblies.Count == 0)
        {
            return null;
        }

        return new DrawingSetup
        {
            V3d = menu.V3d,
            V2d = menu.V2d,
            Sched = menu.Sched,
            Titleblock = menu.Titleblock,
            SelectedAssemblies = menu.SelectedAssemblies,
            Generate3DViews = menu.Generate3DViews,
            GenerateSchedules = menu.GenerateSchedules,
            Generate2DViews = menu.Generate2DViews,
        };
    }
}


// =============================================================================
// 3. SETTINGS MODELS
// =============================================================================

public interface ICloneableSettings<T> where T : ICloneableSettings<T>
{
    T Clone();
}

public sealed class VendorDrawingsSettings : ICloneableSettings<VendorDrawingsSettings>
{
    /// <summary>Title block type chosen in the setup window, as
    /// "Family : Type". Null until the user has picked one.</summary>
    public string? TitleBlockName { get; set; }

    /// <summary>View template names last chosen in the setup window, so the
    /// next run reopens with them already selected. Names, not ElementIds:
    /// these settings are per-user and outlive any one document. Null until
    /// the user has picked one.</summary>
    public string? View3DTemplateName { get; set; }
    public string? View2DTemplateName { get; set; }
    public string? ScheduleTemplateName { get; set; }

    public double OffsetXMm { get; set; } = -250.0;
    public double OffsetYMm { get; set; } = 150.0;

    public double View3DOffsetXMm { get; set; } = -84.0;
    public double View3DOffsetYMm { get; set; } = 0.0;

    public double ScheduleOffsetXMm { get; set; } = 125.0;
    public double ScheduleOffsetYMm { get; set; } = 90.0;

    public double ClusterSpacingFactor { get; set; } = 0.20;

    /// <summary>Offset of the single 2D viewport that the Korea layout repeats
    /// across its per-view sheets. Zero keeps the viewport on the sheet centre,
    /// which is what settings files written before this existed deserialize to.
    /// Read only by the Korea layout; the Vendor layout ignores it.</summary>
    public double Views2DOffsetXMm { get; set; } = 0.0;
    public double Views2DOffsetYMm { get; set; } = 0.0;

    public double LeftPercent { get; set; } = 0.60;
    public double BottomRightReservedXPercent { get; set; } = 0.35;
    public double BottomRightReservedYPercent { get; set; } = 0.35;

    /// <summary>
    /// One-time migration marker (see SettingsGate.LoadOffsetsSettings): false
    /// on any file written before Vendor and Korea got separate offsets files.
    /// Once true, this file is never touched by the seed-from-Vendor step
    /// again, on the vendor file as much as the Korea one. Not meant to be
    /// hand-edited; safe to delete this property in a later cleanup once the
    /// migration has run for every user.
    /// </summary>
    public bool OffsetsSeeded { get; set; } = false;

    public VendorDrawingsSettings Clone() => (VendorDrawingsSettings)MemberwiseClone();

    public double OffsetXFt() => UnitUtils.ConvertToInternalUnits(OffsetXMm, UnitTypeId.Millimeters);
    public double OffsetYFt() => UnitUtils.ConvertToInternalUnits(OffsetYMm, UnitTypeId.Millimeters);
    public double View3DOffsetXFt() => UnitUtils.ConvertToInternalUnits(View3DOffsetXMm, UnitTypeId.Millimeters);
    public double View3DOffsetYFt() => UnitUtils.ConvertToInternalUnits(View3DOffsetYMm, UnitTypeId.Millimeters);
    public double ScheduleOffsetXFt() => UnitUtils.ConvertToInternalUnits(ScheduleOffsetXMm, UnitTypeId.Millimeters);
    public double ScheduleOffsetYFt() => UnitUtils.ConvertToInternalUnits(ScheduleOffsetYMm, UnitTypeId.Millimeters);
    public double Views2DOffsetXFt() => UnitUtils.ConvertToInternalUnits(Views2DOffsetXMm, UnitTypeId.Millimeters);
    public double Views2DOffsetYFt() => UnitUtils.ConvertToInternalUnits(Views2DOffsetYMm, UnitTypeId.Millimeters);
}

public sealed class AssemblyDrawingsSettings : ICloneableSettings<AssemblyDrawingsSettings>
{
    /// <summary>Title block type chosen in the setup window, as
    /// "Family : Type". Null until the user has picked one.</summary>
    public string? TitleBlockName { get; set; }

    public double AsmViewXMm { get; set; } = -365.76;
    public double AsmViewYMm { get; set; } = 243.84;
    public double BoltViewXMm { get; set; } = 365.76;
    public double BoltViewStartYMm { get; set; } = 243.84;
    public double BoltViewStepYMm { get; set; } = -182.88;
    public double SectionBoxPaddingMm { get; set; } = 60.96;

    public AssemblyDrawingsSettings Clone() => (AssemblyDrawingsSettings)MemberwiseClone();
    public double AsmViewXFt() => UnitUtils.ConvertToInternalUnits(AsmViewXMm, UnitTypeId.Millimeters);
    public double AsmViewYFt() => UnitUtils.ConvertToInternalUnits(AsmViewYMm, UnitTypeId.Millimeters);
    public double BoltViewXFt() => UnitUtils.ConvertToInternalUnits(BoltViewXMm, UnitTypeId.Millimeters);
    public double BoltViewStartYFt() => UnitUtils.ConvertToInternalUnits(BoltViewStartYMm, UnitTypeId.Millimeters);
    public double BoltViewStepYFt() => UnitUtils.ConvertToInternalUnits(BoltViewStepYMm, UnitTypeId.Millimeters);
    public double SectionBoxPaddingFt() => UnitUtils.ConvertToInternalUnits(SectionBoxPaddingMm, UnitTypeId.Millimeters);
}

public sealed class MTDrawingsSettings : ICloneableSettings<MTDrawingsSettings>
{
    /// <summary>Title block type chosen in the setup window, as
    /// "Family : Type". Null until the user has picked one.</summary>
    public string? TitleBlockName { get; set; }

    /// <summary>View template names last chosen in the setup window, so the
    /// next run reopens with them already selected. Names, not ElementIds:
    /// these settings are per-user and outlive any one document. Null until
    /// the user has picked one.</summary>
    public string? View3DTemplateName { get; set; }
    public string? View2DTemplateName { get; set; }
    public string? ScheduleTemplateName { get; set; }

    public double OffsetXMm { get; set; } = -229.0;
    public double OffsetYMm { get; set; } = 400.0;
    public bool ApplyOffsetXTwice { get; set; } = true;
    public double BundleSpacingXMm { get; set; } = 80.0;
    public double BundleSpacingYMm { get; set; } = 175.0;
    public double Views2DOffsetXMm { get; set; } = -70.0;
    public double Views2DOffsetYMm { get; set; } = 0.0;
    public double PartListOffsetXMm { get; set; } = -9.3;
    public double PartListOffsetYMm { get; set; } = 83.6;
    public double View3DOffsetXMm { get; set; } = 0.0;
    public double View3DOffsetYMm { get; set; } = 0.0;

    public MTDrawingsSettings Clone() => (MTDrawingsSettings)MemberwiseClone();
    public double OffsetXFt() => UnitUtils.ConvertToInternalUnits(OffsetXMm, UnitTypeId.Millimeters);
    public double OffsetYFt() => UnitUtils.ConvertToInternalUnits(OffsetYMm, UnitTypeId.Millimeters);
    public double BundleSpacingXFt() => UnitUtils.ConvertToInternalUnits(BundleSpacingXMm, UnitTypeId.Millimeters);
    public double BundleSpacingYFt() => UnitUtils.ConvertToInternalUnits(BundleSpacingYMm, UnitTypeId.Millimeters);
    public double Views2DOffsetXFt() => UnitUtils.ConvertToInternalUnits(Views2DOffsetXMm, UnitTypeId.Millimeters);
    public double Views2DOffsetYFt() => UnitUtils.ConvertToInternalUnits(Views2DOffsetYMm, UnitTypeId.Millimeters);
    public double PartListOffsetXFt() => UnitUtils.ConvertToInternalUnits(PartListOffsetXMm, UnitTypeId.Millimeters);
    public double PartListOffsetYFt() => UnitUtils.ConvertToInternalUnits(PartListOffsetYMm, UnitTypeId.Millimeters);
    public double View3DOffsetXFt() => UnitUtils.ConvertToInternalUnits(View3DOffsetXMm, UnitTypeId.Millimeters);
    public double View3DOffsetYFt() => UnitUtils.ConvertToInternalUnits(View3DOffsetYMm, UnitTypeId.Millimeters);
}


// =============================================================================
// 4. PERSISTENCE
// =============================================================================

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string DefaultFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AVES", "PulldownCreateSheets");

    public static T LoadOrDefault<T>(string filename) where T : class, new()
    {
        try
        {
            var path = Path.Combine(DefaultFolder, filename);
            if (!File.Exists(path)) return new T();
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path)) ?? new T();
        }
        catch { return new T(); }
    }

    public static void Save<T>(string filename, T settings) where T : class
    {
        Directory.CreateDirectory(DefaultFolder);
        File.WriteAllText(
            Path.Combine(DefaultFolder, filename),
            JsonSerializer.Serialize(settings, JsonOpts));
    }
}


// =============================================================================
// 5. UI BUILDING BLOCKS
// =============================================================================

internal sealed class NumericRow
{
    public StackPanel Container { get; }
    public TextBox TextBox { get; }
    public event Action? ValueChanged;

    public NumericRow(string label, double initial, string suffix, string? tooltip = null)
    {
        Container = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 4, 0, 4),
        };

        var lbl = new TextBlock
        {
            Text = label,
            Width = 215,
            FontFamily = AvTokens.Ui,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = AvTokens.Br(AvTokens.TextDim),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (tooltip != null) lbl.ToolTip = tooltip;

        TextBox = new TextBox
        {
            Width = 82,
            Text = initial.ToString("0.###", CultureInfo.InvariantCulture),
            FontFamily = AvTokens.Ui,
            FontSize = 13,
            Foreground = AvTokens.Br(AvTokens.Text),
            Background = AvTokens.Br(AvTokens.Surface),
            BorderBrush = AvTokens.Br(AvTokens.BorderStrong),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 5, 8, 5),
            HorizontalContentAlignment = HorizontalAlignment.Right,
        };

        // Apply rounded corners via ControlTemplate
        TextBox.Style = BuildTextBoxStyle();

        TextBox.TextChanged += (_, _) =>
        {
            if (IsValid)
            {
                TextBox.Background = AvTokens.Br(AvTokens.Surface);
                TextBox.BorderBrush = AvTokens.Br(AvTokens.BorderStrong);
            }
            else
            {
                TextBox.Background = AvTokens.Br(AvTokens.AccentTint);
                TextBox.BorderBrush = AvTokens.Br(AvTokens.Accent);
            }
            ValueChanged?.Invoke();
        };

        var suffixTb = new TextBlock
        {
            Text = $" {suffix}",
            FontFamily = AvTokens.Ui,
            FontSize = 12,
            Foreground = AvTokens.Br(AvTokens.TextFaint),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Width = 45,
        };

        Container.Children.Add(lbl);
        Container.Children.Add(TextBox);
        Container.Children.Add(suffixTb);
    }

    public bool IsValid => TryParse(TextBox.Text, out _);

    public double Value
    {
        get => TryParse(TextBox.Text, out var v) ? v : 0;
        set => TextBox.Text = value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    private static Style BuildTextBoxStyle()
    {
        // Built from inline XAML to avoid FrameworkElementFactory ambiguity
        // with Autodesk.Revit.DB types in this compilation unit.
        const string xaml =
            "<Style TargetType=\"TextBox\" " +
            "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
            "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\">" +
            "<Setter Property=\"Template\">" +
            "<Setter.Value>" +
            "<ControlTemplate TargetType=\"TextBox\">" +
            "<Border CornerRadius=\"5\" " +
            "Background=\"{TemplateBinding Background}\" " +
            "BorderBrush=\"{TemplateBinding BorderBrush}\" " +
            "BorderThickness=\"{TemplateBinding BorderThickness}\">" +
            "<ScrollViewer x:Name=\"PART_ContentHost\" Margin=\"0\"/>" +
            "</Border>" +
            "</ControlTemplate>" +
            "</Setter.Value>" +
            "</Setter>" +
            "</Style>";
        return (Style)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private static bool TryParse(string? text, out double value)
    {
        var normalized = (text ?? string.Empty).Replace(',', '.');
        return double.TryParse(normalized,
            NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}

internal sealed class BoolRow
{
    public StackPanel Container { get; }
    public CheckBox CheckBox { get; }
    public event Action? ValueChanged;

    public BoolRow(string label, bool initial, string? tooltip = null)
    {
        Container = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 5, 0, 5),
        };
        CheckBox = new CheckBox
        {
            Content = label,
            IsChecked = initial,
            FontFamily = AvTokens.Ui,
            FontSize = 13,
            Foreground = AvTokens.Br(AvTokens.Text),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (tooltip != null) CheckBox.ToolTip = tooltip;
        CheckBox.Checked += (_, _) => ValueChanged?.Invoke();
        CheckBox.Unchecked += (_, _) => ValueChanged?.Invoke();
        Container.Children.Add(CheckBox);
    }

    public bool Value
    {
        get => CheckBox.IsChecked == true;
        set => CheckBox.IsChecked = value;
    }
}

/// <summary>Thin wrapper — now delegates to AvControls.SectionHeader.</summary>
internal static class FormHelpers
{
    public static Border SectionHeader(string text) =>
        AvControls.SectionHeader(text);
}

internal sealed class LayoutPreviewCanvas : Canvas
{
    // ── Updated palette — AVES Refined Native tokens ──────────────────────────
    public static readonly SolidColorBrush SheetFill = new(Color.FromRgb(252, 252, 252));
    public static readonly SolidColorBrush SheetStroke = new(Color.FromRgb(229, 230, 234)); // Border
    public static readonly SolidColorBrush TitleBlockFill =
        new(Color.FromRgb(250, 250, 251));                                                    // Surface2

    // View colours use the status/accent palette
    public static readonly SolidColorBrush View3DFill = new(Color.FromRgb(231, 239, 251)); // InfoBg
    public static readonly SolidColorBrush View3DStroke = new(Color.FromRgb(46, 102, 168)); // InfoFg
    public static readonly SolidColorBrush View2DFill = new(Color.FromRgb(228, 243, 234)); // OkBg
    public static readonly SolidColorBrush View2DStroke = new(Color.FromRgb(44, 122, 82)); // OkFg
    public static readonly SolidColorBrush ScheduleFill = new(Color.FromRgb(250, 240, 218));// WarnBg
    public static readonly SolidColorBrush ScheduleStroke = new(Color.FromRgb(154, 107, 18));// WarnFg
    public static readonly SolidColorBrush BoltFill = new(Color.FromRgb(251, 233, 228)); // AccentTint
    public static readonly SolidColorBrush BoltStroke = new(Color.FromRgb(194, 64, 42)); // AccentDeep

    public LayoutPreviewCanvas()
    {
        Background = new SolidColorBrush(Color.FromRgb(247, 248, 250)); // Zebra
        Width = 380;
        Height = 320;
        ClipToBounds = true;
    }

    public void DrawSheetOutline(double x, double y, double w, double h, string label)
    {
        var r = new Rectangle
        {
            Width = w,
            Height = h,
            Fill = SheetFill,
            Stroke = SheetStroke,
            StrokeThickness = 1,
        };
        SetLeft(r, x); SetTop(r, y); Children.Add(r);

        var tb = new Rectangle
        {
            Width = w * 0.30,
            Height = h * 0.18,
            Fill = TitleBlockFill,
            Stroke = SheetStroke,
            StrokeThickness = 0.5,
        };
        SetLeft(tb, x + w - w * 0.30);
        SetTop(tb, y + h - h * 0.18);
        Children.Add(tb);

        Children.Add(NewLabel(label, x + 4, y + 2, 9,
            new SolidColorBrush(Color.FromRgb(139, 144, 153)))); // TextFaint
    }

    public void DrawElement(double cx, double cy, double w, double h,
        Brush fill, Brush stroke, string label)
    {
        w = Math.Max(4, w);
        h = Math.Max(4, h);

        var left = cx - w / 2;
        var top = cy - h / 2;

        var r = new Rectangle
        {
            Width = w,
            Height = h,
            Fill = fill,
            Stroke = stroke,
            StrokeThickness = 1.3,
            Opacity = 0.88,
        };
        SetLeft(r, left); SetTop(r, top); Children.Add(r);

        if (w >= 26 && h >= 14)
            Children.Add(NewLabel(label, left + 3, top + 2, 10,
                new SolidColorBrush(Color.FromRgb(31, 35, 41)), FontWeights.SemiBold));
    }

    public void DrawCrosshair(double x, double y)
    {
        Children.Add(new Line
        {
            X1 = x - 4,
            Y1 = y,
            X2 = x + 4,
            Y2 = y,
            Stroke = new SolidColorBrush(Color.FromRgb(212, 214, 220)),
            StrokeThickness = 0.7,
        });
        Children.Add(new Line
        {
            X1 = x,
            Y1 = y - 4,
            X2 = x,
            Y2 = y + 4,
            Stroke = new SolidColorBrush(Color.FromRgb(212, 214, 220)),
            StrokeThickness = 0.7,
        });
    }

    public void DrawCaption(string text, double x, double y, Brush? color = null) =>
        Children.Add(NewLabel(text, x, y, 9,
            color ?? new SolidColorBrush(Color.FromRgb(139, 144, 153))));

    private static TextBlock NewLabel(string text, double x, double y,
        double fontSize, Brush color, FontWeight? weight = null)
    {
        var t = new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontFamily = AvTokens.Ui,
            Foreground = color,
            FontWeight = weight ?? FontWeights.Normal,
        };
        SetLeft(t, x);
        SetTop(t, y);
        return t;
    }
}


// =============================================================================
// Dialog base — AVES Refined Native chrome
// =============================================================================

internal abstract class LayoutSettingsDialogBase<T> : Window
    where T : class, ICloneableSettings<T>, new()
{
    protected T Working;
    public T? Result { get; private set; }

    protected readonly StackPanel ControlsPanel;
    protected readonly LayoutPreviewCanvas Preview;
    protected readonly TextBlock StatusText;

    private readonly List<NumericRow> _numericRows = [];

    protected bool AllNumericRowsValid =>
        _numericRows.TrueForAll(r => r.IsValid);

    protected LayoutSettingsDialogBase(T initial, string title, IntPtr revitHandle)
    {
        Working = initial.Clone();

        Title = title;
        Width = 840;
        Height = 680;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        FontFamily = AvTokens.Ui;
        Background = AvTokens.Br(AvTokens.Backdrop);

        if (revitHandle != IntPtr.Zero)
            _ = new WindowInteropHelper(this) { Owner = revitHandle };

        var root = new DockPanel();

        // ── Interior header strip ─────────────────────────────────────────────
        var headerStrip = AvControls.HeaderStrip(title);
        DockPanel.SetDock(headerStrip, Dock.Top);
        root.Children.Add(headerStrip);

        // ── Footer strip ──────────────────────────────────────────────────────
        var footerStrip = BuildFooterStrip();
        DockPanel.SetDock(footerStrip, Dock.Bottom);
        root.Children.Add(footerStrip);

        // ── Main content area ─────────────────────────────────────────────────
        var contentBorder = new Border
        {
            Background = AvTokens.Br(AvTokens.Surface),
            Padding = new Thickness(20, 16, 20, 16),
        };

        var split = new Grid();
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(390) });
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        split.ColumnDefinitions.Add(new ColumnDefinition
        { Width = new GridLength(1, GridUnitType.Star) });

        // Left — scrollable controls
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        ControlsPanel = new StackPanel();
        scroll.Content = ControlsPanel;
        Grid.SetColumn(scroll, 0);
        split.Children.Add(scroll);

        // Right — preview + status
        var rightCol = new StackPanel();

        var previewLabel = new TextBlock
        {
            Text = "Layout Preview",
            FontFamily = AvTokens.Ui,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(60, 80, 110)),
            Margin = new Thickness(0, 0, 0, 6),
        };
        rightCol.Children.Add(previewLabel);

        Preview = new LayoutPreviewCanvas();
        rightCol.Children.Add(new Border
        {
            BorderBrush = AvTokens.Br(AvTokens.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Child = Preview,
        });

        var previewCaption = new TextBlock
        {
            Text = "Schematic only — element sizes are approximate; positions reflect your inputs.",
            FontSize = 9,
            FontFamily = AvTokens.Ui,
            Foreground = AvTokens.Br(AvTokens.TextFaint),
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        rightCol.Children.Add(previewCaption);

        // StatusText lives in the right column below the caption
        StatusText = new TextBlock
        {
            FontFamily = AvTokens.Ui,
            FontSize = 10,
            Foreground = AvTokens.Br(AvTokens.TextFaint),
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        rightCol.Children.Add(StatusText);

        Grid.SetColumn(rightCol, 2);
        split.Children.Add(rightCol);

        contentBorder.Child = split;
        root.Children.Add(contentBorder);
        Content = root;
    }

    // ── Footer strip: Ghost Cancel | Secondary Make Default | Primary Run ─────
    private Border BuildFooterStrip()
    {
        var cancelBtn = AvControls.GhostButtonHoverLoaded("Cancel");
        cancelBtn.IsCancel = true;
        cancelBtn.Margin = new Thickness(0, 0, 10, 0);
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };

        var makeDefaultBtn = AvControls.SecondaryButtonHoverLoaded("Make Default");
        makeDefaultBtn.Margin = new Thickness(0, 0, 10, 0);
        makeDefaultBtn.Click += (_, _) =>
        {
            if (!AllNumericRowsValid)
            {
                AvControls.ShowAlert(
                    "Invalid input",
                    "One or more values are not valid numbers. Fix the highlighted fields before saving.", AlertSeverity.Warning);
                return;
            }
            ApplyValues();
            SaveDefault();
            Preview.Children.Clear();
            RefreshPreview();
            StatusText.Text = "Saved as new default. Preview updated.";
        };

        var runBtn = AvControls.PrimaryButtonHoverLoaded("▶  Run Script");
        runBtn.IsDefault = true;
        runBtn.Click += (_, _) =>
        {
            if (!AllNumericRowsValid)
            {
                AvControls.ShowAlert(
                    "Invalid input",
                    "One or more values are not valid numbers. Fix the highlighted fields before running.", AlertSeverity.Warning);
                return;
            }
            ApplyValues();
            Result = Working;
            DialogResult = true;
            Close();
        };

        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(makeDefaultBtn);
        btnRow.Children.Add(runBtn);

        return new Border
        {
            Background = AvTokens.Br(AvTokens.Surface2),
            BorderBrush = AvTokens.Br(AvTokens.Border),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14, 20, 14),
            Child = btnRow,
        };
    }

    protected abstract void SaveDefault();
    protected abstract void BuildControls();
    protected abstract void ApplyValues();
    protected abstract void RefreshPreview();

    protected void RebuildAll()
    {
        _numericRows.Clear();
        ControlsPanel.Children.Clear();
        BuildControls();
        Preview.Children.Clear();
        RefreshPreview();
    }

    protected NumericRow Row(string label, double initial, string suffix,
        string? tooltip = null)
    {
        var row = new NumericRow(label, initial, suffix, tooltip);
        _numericRows.Add(row);
        row.ValueChanged += () =>
        {
            if (!AllNumericRowsValid) return;
            ApplyValues();
            Preview.Children.Clear();
            RefreshPreview();
        };
        ControlsPanel.Children.Add(row.Container);
        return row;
    }

    protected BoolRow CheckRow(string label, bool initial, string? tooltip = null)
    {
        var row = new BoolRow(label, initial, tooltip);
        row.ValueChanged += () =>
        {
            ApplyValues();
            Preview.Children.Clear();
            RefreshPreview();
        };
        ControlsPanel.Children.Add(row.Container);
        return row;
    }
}



// =============================================================================
// Layout descriptors — one object per sheet layout
// =============================================================================
// The settings dialog and the sheet generation read the same descriptor, so the
// preview cannot drift from what the command actually draws: adding a layout
// means adding one descriptor, not editing the dialog as well.

internal abstract class VendorLayoutDescriptor
{
    public abstract string DialogTitle { get; }

    /// <summary>Whether the dialog offers the Vendor-only 6-view cluster row.</summary>
    public abstract bool ShowClusterSection { get; }

    /// <summary>Whether the dialog offers the Korea-only offset for the single
    /// 2D viewport repeated across its per-view sheets.</summary>
    public abstract bool Show2DSheetSection { get; }

    public abstract void DrawPreview(LayoutPreviewCanvas preview, VendorDrawingsSettings s);

    public abstract string PreviewStatus(VendorDrawingsSettings s);

    public abstract IAssemblySheetLayout CreateLayout(VendorDrawingsSettings settings);

    /// <summary>
    /// Shows the settings dialog for this layout and builds the layout from
    /// what came back, or null if the user cancelled.
    /// </summary>
    /// <param name="uiApp">The active Revit UI application.</param>
    /// <param name="settingsFile">
    /// The command's own offsets file (e.g. "vendor-drawings.json" or
    /// "korea-drawings.json") -- the same name that command already uses for
    /// AssemblyDrawingProfile.TemplateSettingsFile, so each command has
    /// exactly one settings file, not two names to keep in sync.
    /// </param>
    public IAssemblySheetLayout? CreateLayoutFrom(UIApplication uiApp, string settingsFile) =>
        SettingsGate.ShowVendorDrawings(uiApp, this, settingsFile) is { } settings
            ? CreateLayout(settings)
            : null;

    public static readonly VendorLayoutDescriptor Vendor = new VendorLayoutDescriptorImpl();
    public static readonly VendorLayoutDescriptor Korea = new KoreaLayoutDescriptorImpl();

    // ── Shared preview geometry ──────────────────────────────────────────────
    protected const double SheetW = 165.0;
    protected const double SheetH = 117.0;
    protected const double LeftPad = 18.0;
    protected const double SheetTop = 8.0;

    /// <summary>
    /// Sheet 1 is the same for every layout: the 3D view and the part list, both
    /// positioned off the dialog's offsets.
    /// </summary>
    protected static void DrawSheetOne(LayoutPreviewCanvas preview)
    {
        preview.DrawSheetOutline(LeftPad, SheetTop, SheetW, SheetH, "Sheet 1 — 3D + Schedule");

        var cx = LeftPad + SheetW / 2.0;
        var cy = SheetTop + SheetH / 2.0;
        preview.DrawCrosshair(cx, cy);

        preview.DrawElement(cx - 45, cy, 60.0, 48.0,
            LayoutPreviewCanvas.View3DFill, LayoutPreviewCanvas.View3DStroke, "3D");

        preview.DrawElement(cx + 55, cy - 30, 44.0, 34.0,
            LayoutPreviewCanvas.ScheduleFill, LayoutPreviewCanvas.ScheduleStroke, "Sched");
    }

    protected static string SheetOneStatus(VendorDrawingsSettings s) =>
        $"Global offset: {s.OffsetXMm:0.#} mm X, {s.OffsetYMm:0.#} mm Y  |  " +
        $"3D view: {s.View3DOffsetXMm:0.#} mm X, {s.View3DOffsetYMm:0.#} mm Y  |  " +
        $"Schedule: {s.ScheduleOffsetXMm:0.#} mm X, {s.ScheduleOffsetYMm:0.#} mm Y";
}

/// <summary>Two A3 sheets: 3D + schedule, then the six views clustered.</summary>
internal sealed class VendorLayoutDescriptorImpl : VendorLayoutDescriptor
{
    public override string DialogTitle => "Vendor Drawings (A3, two sheets per assembly)";

    public override bool ShowClusterSection => true;

    public override bool Show2DSheetSection => false;

    public override IAssemblySheetLayout CreateLayout(VendorDrawingsSettings settings) =>
        new VendorSheetLayout(settings);

    public override void DrawPreview(LayoutPreviewCanvas preview, VendorDrawingsSettings s)
    {
        DrawSheetOne(preview);

        const double s2Top = SheetTop + SheetH + 16.0;
        preview.DrawSheetOutline(LeftPad, s2Top, SheetW, SheetH, "Sheet 2 — 6-View Cluster");

        const double cx = LeftPad + SheetW / 2.0;
        const double cy = s2Top + SheetH / 2.0;

        const double spacing = SheetW * 0.20;
        const double viewSize = 16.0;
        DrawClusterView(preview, cx, cy, viewSize, "F");
        DrawClusterView(preview, cx + spacing, cy, viewSize, "B");
        DrawClusterView(preview, cx - spacing, cy, viewSize, "R");
        DrawClusterView(preview, cx + spacing * 2, cy, viewSize, "L");
        DrawClusterView(preview, cx, cy - spacing, viewSize, "T");
        DrawClusterView(preview, cx, cy + spacing, viewSize, "Bt");
    }

    public override string PreviewStatus(VendorDrawingsSettings s) =>
        SheetOneStatus(s) +
        $"  |  Cluster spacing: {s.ClusterSpacingFactor:0.##} × sheet W";

    private static void DrawClusterView(
        LayoutPreviewCanvas preview, double cx, double cy, double size, string label) =>
        preview.DrawElement(cx, cy, size, size,
            LayoutPreviewCanvas.View2DFill, LayoutPreviewCanvas.View2DStroke, label);
}

/// <summary>
/// Seven sheets: 3D + schedule, then one view per sheet. The thumbnails mirror
/// KoreaSheetLayout -- one viewport, centred, filling the same fraction of the
/// drawable height the generated sheets use.
/// </summary>
internal sealed class KoreaLayoutDescriptorImpl : VendorLayoutDescriptor
{
    public override string DialogTitle => "Korea Drawings";

    public override bool ShowClusterSection => false;

    public override bool Show2DSheetSection => true;

    public override IAssemblySheetLayout CreateLayout(VendorDrawingsSettings settings) =>
        new KoreaSheetLayout(settings);

    public override void DrawPreview(LayoutPreviewCanvas preview, VendorDrawingsSettings s)
    {
        DrawSheetOne(preview);

        // Sheets 2-7 differ only in which view they carry, so one full-size
        // representative sheet says more than six thumbnails would.
        const double top = SheetTop + SheetH + 16.0;
        preview.DrawSheetOutline(LeftPad, top, SheetW, SheetH,
            "Representative 2D Sheet (Sheets 2\u20137)");

        // One viewport at the layout's own fraction of the drawable height --
        // the same constants the generated sheets are scaled by. Drawn at the
        // sheet centre and deliberately NOT shifted by Views2DOffset: the
        // representative sheet stands for all six, so it stays put while those
        // fields are edited. The offsets still reach the generated sheets, and
        // the status line below reports their current values.
        const double usableW = SheetW * KoreaSheetLayout.UsableSheetFraction;
        const double usableH = SheetH * KoreaSheetLayout.UsableSheetFraction;
        const double boxW = usableW * 0.72;
        const double boxH = usableH * KoreaSheetLayout.TargetHeightFraction;

        preview.DrawElement(
            LeftPad + SheetW / 2.0,
            top + SheetH / 2.0,
            boxW, boxH,
            LayoutPreviewCanvas.View2DFill, LayoutPreviewCanvas.View2DStroke, "2D VIEW");

        // Split across two lines: the preview column is too narrow for the
        // sentence to fit on one at this font size.
        preview.DrawCaption(
            "Sheets 2\u20137 are identical in layout. Only the view changes",
            LeftPad, top + SheetH + 6.0);
        preview.DrawCaption(
            "(Front, Back, Left, Right, Top, Bottom).",
            LeftPad, top + SheetH + 18.0);
    }

    public override string PreviewStatus(VendorDrawingsSettings s) =>
        SheetOneStatus(s) +
        $"  |  2D view: {s.Views2DOffsetXMm:0.#} mm X, {s.Views2DOffsetYMm:0.#} mm Y" +
        $"  |  2D viewports: {KoreaSheetLayout.TargetHeightFraction * 100:0} % of drawable height";
}

// =============================================================================
// Button 1 dialog — Vendor Drawings
// =============================================================================

internal sealed class VendorDrawingsSettingsDialog
    : LayoutSettingsDialogBase<VendorDrawingsSettings>
{
    private NumericRow _offsetX = null!, _offsetY = null!;
    private NumericRow _v3dX = null!, _v3dY = null!;
    private NumericRow _schedX = null!, _schedY = null!;
    private NumericRow? _clusterSpacing;
    private NumericRow? _views2dX, _views2dY;

    // Title, cluster row and preview all come off one descriptor, the same one
    // that builds the layout doing the actual placement.
    private readonly VendorLayoutDescriptor _descriptor;

    // The calling command's own offsets file -- see CreateLayoutFrom. "Make
    // Default" must persist to the SAME file this dialog was loaded from, or
    // Korea and Vendor go back to overwriting each other's settings.
    private readonly string _settingsFile;

    public VendorDrawingsSettingsDialog(
        VendorDrawingsSettings initial,
        IntPtr revitHandle,
        VendorLayoutDescriptor descriptor,
        string settingsFile)
        : base(initial, descriptor.DialogTitle, revitHandle)
    {
        _descriptor = descriptor;
        _settingsFile = settingsFile;
        RebuildAll();
    }

    protected override void SaveDefault() =>
        SettingsStore.Save(_settingsFile, Working);

    protected override void BuildControls()
    {
        var s = Working;

        ControlsPanel.Children.Add(FormHelpers.SectionHeader("Global Sheet Offset"));
        _offsetX = Row("Offset X", s.OffsetXMm, "mm",
            "Shifts every viewport on every sheet horizontally.");
        _offsetY = Row("Offset Y", s.OffsetYMm, "mm",
            "Shifts every viewport on every sheet vertically.");

        ControlsPanel.Children.Add(FormHelpers.SectionHeader("Sheet 1 — 3D View"));
        _v3dX = Row("3D view offset X", s.View3DOffsetXMm, "mm",
            "X position of the 3D view from sheet centre (excl. Global Offset).");
        _v3dY = Row("3D view offset Y", s.View3DOffsetYMm, "mm",
            "Y position of the 3D view from sheet centre (excl. Global Offset).");

        ControlsPanel.Children.Add(FormHelpers.SectionHeader("Sheet 1 — Schedule"));
        _schedX = Row("Schedule offset X", s.ScheduleOffsetXMm, "mm",
            "X position of the schedule from sheet centre (excl. Global Offset).");
        _schedY = Row("Schedule offset Y", s.ScheduleOffsetYMm, "mm",
            "Y position of the schedule from sheet centre (excl. Global Offset).");

        if (_descriptor.Show2DSheetSection)
        {
            ControlsPanel.Children.Add(
                FormHelpers.SectionHeader("Representative 2D Sheet (Sheets 2\u20137)"));
            _views2dX = Row("2D view offset X", s.Views2DOffsetXMm, "mm",
                "X position of the 2D view from sheet centre, applied to all six " +
                "sheets (excl. Global Offset).");
            _views2dY = Row("2D view offset Y", s.Views2DOffsetYMm, "mm",
                "Y position of the 2D view from sheet centre, applied to all six " +
                "sheets (excl. Global Offset).");
        }

        if (!_descriptor.ShowClusterSection) return;

        ControlsPanel.Children.Add(FormHelpers.SectionHeader("Sheet 2 — 6-View Cluster"));
        _clusterSpacing = Row("Cluster spacing factor", s.ClusterSpacingFactor, "× sheet W",
            "Spacing between adjacent views in the First Angle cluster.");
    }

    protected override void ApplyValues()
    {
        Working.OffsetXMm = _offsetX.Value;
        Working.OffsetYMm = _offsetY.Value;
        Working.View3DOffsetXMm = _v3dX.Value;
        Working.View3DOffsetYMm = _v3dY.Value;
        Working.ScheduleOffsetXMm = _schedX.Value;
        Working.ScheduleOffsetYMm = _schedY.Value;

        // Left at its stored value when the section is hidden: nothing edited it.
        if (_clusterSpacing is { } clusterSpacing)
            Working.ClusterSpacingFactor = clusterSpacing.Value;

        if (_views2dX is { } views2dX) Working.Views2DOffsetXMm = views2dX.Value;
        if (_views2dY is { } views2dY) Working.Views2DOffsetYMm = views2dY.Value;
    }

    protected override void RefreshPreview()
    {
        _descriptor.DrawPreview(Preview, Working);
        StatusText.Text = _descriptor.PreviewStatus(Working);
    }
}


// =============================================================================
// Button 2 dialog — Assembly Drawings
// =============================================================================

internal sealed class AssemblyDrawingsSettingsDialog
    : LayoutSettingsDialogBase<AssemblyDrawingsSettings>
{
    private NumericRow _asmX = null!, _asmY = null!;
    private NumericRow _boltX = null!, _boltStartY = null!, _boltStepY = null!;
    private NumericRow _padding = null!;

    public AssemblyDrawingsSettingsDialog(AssemblyDrawingsSettings initial, IntPtr revitHandle)
        : base(initial, "Button 2 — Assembly Drawings (Bolt Clustering)", revitHandle)
    {
        RebuildAll();
    }

    protected override void SaveDefault() =>
        SettingsStore.Save("assembly-drawings.json", Working);

    protected override void BuildControls()
    {
        var s = Working;

        ControlsPanel.Children.Add(FormHelpers.SectionHeader("Assembly 3D View (left of sheet)"));
        _asmX = Row("Position X", s.AsmViewXMm, "mm",
            "Sheet X coordinate where the assembly 3D view is placed.");
        _asmY = Row("Position Y", s.AsmViewYMm, "mm",
            "Sheet Y coordinate where the assembly 3D view is placed.");

        ControlsPanel.Children.Add(FormHelpers.SectionHeader("Bolt Cluster Views (right of sheet)"));
        _boltX = Row("Position X", s.BoltViewXMm, "mm",
            "Sheet X coordinate for the top bolt cluster view. All bolt views share this X.");
        _boltStartY = Row("Start Y (first view)", s.BoltViewStartYMm, "mm");
        _boltStepY = Row("Step Y (between views)", s.BoltViewStepYMm, "mm",
            "Negative = stack downwards from the first view.");

        ControlsPanel.Children.Add(FormHelpers.SectionHeader("Section Box"));
        _padding = Row("Padding around content", s.SectionBoxPaddingMm, "mm",
            "Extra space added around the bounding box when creating section boxes.");
    }

    protected override void ApplyValues()
    {
        Working.AsmViewXMm = _asmX.Value;
        Working.AsmViewYMm = _asmY.Value;
        Working.BoltViewXMm = _boltX.Value;
        Working.BoltViewStartYMm = _boltStartY.Value;
        Working.BoltViewStepYMm = _boltStepY.Value;
        Working.SectionBoxPaddingMm = _padding.Value;
    }

    protected override void RefreshPreview()
    {
        var s = Working;

        const double cx = 190, cy = 175;
        const double sheetW = 320, sheetH = 240;
        Preview.DrawSheetOutline(cx - sheetW / 2, cy - sheetH / 2, sheetW, sheetH,
            "Sheet (representative)");

        Preview.DrawCrosshair(cx, cy);
        Preview.DrawCaption("origin", cx + 5, cy + 5);

        Preview.DrawElement(cx - 80, cy, 76, 56,
            LayoutPreviewCanvas.View3DFill, LayoutPreviewCanvas.View3DStroke, "ASM 3D");

        const double boltCx = cx + 90;
        const double boltStartCy = cy - 44;
        const double boltStepCy = 44;
        for (int i = 0; i < 3; i++)
        {
            Preview.DrawElement(boltCx, boltStartCy + i * boltStepCy, 56, 32,
                LayoutPreviewCanvas.BoltFill, LayoutPreviewCanvas.BoltStroke, $"Bolt {i + 1}");
        }

        StatusText.Text =
            $"ASM 3D: ({s.AsmViewXMm:0.#}, {s.AsmViewYMm:0.#}) mm  |  " +
            $"Bolt X: {s.BoltViewXMm:0.#} mm, Start Y: {s.BoltViewStartYMm:0.#} mm, " +
            $"Step Y: {s.BoltViewStepYMm:0.#} mm  |  " +
            $"Section box padding: {s.SectionBoxPaddingMm:0.#} mm  |  " +
            $"Three sample bolt boxes shown — actual count depends on each assembly's clusters.";
    }
}


// =============================================================================
// Button 3 dialog — MT Drawings
// =============================================================================

internal sealed class MTDrawingsSettingsDialog
    : LayoutSettingsDialogBase<MTDrawingsSettings>
{
    private NumericRow _offsetX = null!, _offsetY = null!;
    private NumericRow _bundleX = null!, _bundleY = null!;
    private NumericRow _v2dX = null!, _v2dY = null!;
    private NumericRow _v3dX = null!, _v3dY = null!;
    private NumericRow _plX = null!, _plY = null!;

    public MTDrawingsSettingsDialog(MTDrawingsSettings initial, IntPtr revitHandle)
        : base(initial, "MT Drawings (A1, single sheet per assembly)", revitHandle)
    {
        RebuildAll();
    }

    protected override void SaveDefault() =>
        SettingsStore.Save("mtq-drawings.json", Working);

    protected override void BuildControls()
    {
        var s = Working;

        ControlsPanel.Children.Add(FormHelpers.SectionHeader("Global Sheet Offset"));
        _offsetX = Row("Offset X", s.OffsetXMm, "mm");
        _offsetY = Row("Offset Y", s.OffsetYMm, "mm");

        ControlsPanel.Children.Add(FormHelpers.SectionHeader("2D Projection Cluster"));
        _bundleX = Row("Bundle spacing X", s.BundleSpacingXMm, "mm",
            "Horizontal gap between FRONT and LEFT/RIGHT views.");
        _bundleY = Row("Bundle spacing Y", s.BundleSpacingYMm, "mm",
            "Vertical gap between FRONT and TOP views.");
        _v2dX = Row("Views 2D offset X", s.Views2DOffsetXMm, "mm");
        _v2dY = Row("Views 2D offset Y", s.Views2DOffsetYMm, "mm");

        ControlsPanel.Children.Add(FormHelpers.SectionHeader("3D View"));
        _v3dX = Row("3D view offset X", s.View3DOffsetXMm, "mm");
        _v3dY = Row("3D view offset Y", s.View3DOffsetYMm, "mm");

        ControlsPanel.Children.Add(FormHelpers.SectionHeader("Part List Schedule"));
        _plX = Row("Part list offset X", s.PartListOffsetXMm, "mm");
        _plY = Row("Part list offset Y", s.PartListOffsetYMm, "mm");
    }

    protected override void ApplyValues()
    {
        Working.OffsetXMm = _offsetX.Value;
        Working.OffsetYMm = _offsetY.Value;
        Working.BundleSpacingXMm = _bundleX.Value;
        Working.BundleSpacingYMm = _bundleY.Value;
        Working.Views2DOffsetXMm = _v2dX.Value;
        Working.Views2DOffsetYMm = _v2dY.Value;
        Working.PartListOffsetXMm = _plX.Value;
        Working.PartListOffsetYMm = _plY.Value;
        Working.View3DOffsetXMm = _v3dX.Value;
        Working.View3DOffsetYMm = _v3dY.Value;
    }

    protected override void RefreshPreview()
    {
        var s = Working;

        const double sheetWpx = 351.0;
        const double sheetHpx = 248.0;
        const double sheetLeft = 10;
        const double sheetTop = 22;

        Preview.DrawSheetOutline(sheetLeft, sheetTop, sheetWpx, sheetHpx,
            "A1 — 1189 × 841 mm");

        const double sheetCx = sheetLeft + sheetWpx / 2;
        const double sheetCy = sheetTop + sheetHpx / 2;
        Preview.DrawCrosshair(sheetCx, sheetCy);

        const double clusterCx = sheetLeft + sheetWpx * 0.20;
        const double clusterCy = sheetCy;
        const double fixedSpX = 36.0;
        const double fixedSpY = 36.0;
        const double sampleViewSize = 32;

        DrawViewBox(clusterCx, clusterCy, sampleViewSize, "F",
            LayoutPreviewCanvas.View2DFill, LayoutPreviewCanvas.View2DStroke);
        DrawViewBox(clusterCx - fixedSpX, clusterCy, sampleViewSize, "R",
            LayoutPreviewCanvas.View2DFill, LayoutPreviewCanvas.View2DStroke);
        DrawViewBox(clusterCx + fixedSpX, clusterCy, sampleViewSize, "L",
            LayoutPreviewCanvas.View2DFill, LayoutPreviewCanvas.View2DStroke);
        DrawViewBox(clusterCx, clusterCy - fixedSpY, sampleViewSize, "T",
            LayoutPreviewCanvas.View2DFill, LayoutPreviewCanvas.View2DStroke);

        Preview.DrawElement(sheetLeft + sheetWpx * 0.45, sheetCy, 44, 80,
            LayoutPreviewCanvas.View3DFill, LayoutPreviewCanvas.View3DStroke, "3D");

        Preview.DrawElement(sheetLeft + sheetWpx * 0.78, sheetCy - 80, 110, 70,
            LayoutPreviewCanvas.ScheduleFill, LayoutPreviewCanvas.ScheduleStroke, "Part List");

        StatusText.Text =
            $"Sheet offset: {s.OffsetXMm:0.#} mm X, {s.OffsetYMm:0.#} mm Y  |  " +
            $"Bundle spacing: {s.BundleSpacingXMm:0.#} mm X, {s.BundleSpacingYMm:0.#} mm Y  |  " +
            $"2D offset: ({s.Views2DOffsetXMm:0.#}, {s.Views2DOffsetYMm:0.#}) mm  |  " +
            $"3D offset: ({s.View3DOffsetXMm:0.#}, {s.View3DOffsetYMm:0.#}) mm  |  " +
            $"Part list offset: ({s.PartListOffsetXMm:0.#}, {s.PartListOffsetYMm:0.#}) mm";
    }

    private void DrawViewBox(double cx, double cy, double size, string label,
        Brush fill, Brush stroke) =>
        Preview.DrawElement(cx, cy, size, size * 0.7, fill, stroke, label);
}


// =============================================================================
// 6. SETTINGS GATE
// =============================================================================

internal static class SettingsGate
{
    // The one place both commands' offsets file names are spelled out --
    // Cmd_DOC03c_Vendor and Cmd_DOC03d_Korea reference these same constants
    // (also for AssemblyDrawingProfile.TemplateSettingsFile) rather than each
    // holding its own copy of the literal.
    public const string VendorOffsetsFile = "vendor-drawings.json";
    public const string KoreaOffsetsFile = "korea-drawings.json";

    public static VendorDrawingsSettings? ShowVendorDrawings(
        UIApplication uiApp, VendorLayoutDescriptor descriptor, string settingsFile)
    {
        var initial = LoadOffsetsSettings(settingsFile);
        var dlg = new VendorDrawingsSettingsDialog(
            initial, uiApp.MainWindowHandle, descriptor, settingsFile);
        if (dlg.ShowDialog() != true) return null;
        var result = dlg.Result;
        if (result is null) return null;
        SettingsStore.Save(settingsFile, result);
        return result;
    }

    /// <summary>
    /// Loads one command's offsets settings, migrating Korea's file the one
    /// time it has never been through this path before. Before offsets were
    /// split per command, BOTH Vendor and Korea's dialogs read and wrote
    /// vendor-drawings.json -- Korea's own file exists (Run() already writes
    /// template picks to it) but its offset fields were never anything but
    /// class defaults, since nothing had ever saved real offsets into it.
    /// Loading it plainly post-split would silently reset Korea's offsets to
    /// those defaults; this copies Korea's last-effective values (whatever
    /// vendor-drawings.json currently holds -- see the fix commit for why
    /// that snapshot's provenance is ambiguous) into Korea's own file instead,
    /// exactly once.
    /// <para>
    /// Guarded by file identity, not just the marker: the copy only ever runs
    /// when <paramref name="settingsFile"/> IS KoreaOffsetsFile. A
    /// vendor-drawings.json that itself still lacks the marker (true the
    /// first time this ships) is never a seed TARGET -- it only gets the
    /// marker set on it, so it can never end up "seeded from itself".
    /// </para>
    /// </summary>
    private static VendorDrawingsSettings LoadOffsetsSettings(string settingsFile)
    {
        var settings = SettingsStore.LoadOrDefault<VendorDrawingsSettings>(settingsFile);
        if (settings.OffsetsSeeded) return settings;

        if (settingsFile == KoreaOffsetsFile)
        {
            var vendorFilePath = Path.Combine(SettingsStore.DefaultFolder, VendorOffsetsFile);
            if (File.Exists(vendorFilePath))
            {
                var vendorSettings = SettingsStore.LoadOrDefault<VendorDrawingsSettings>(VendorOffsetsFile);
                CopyOffsetFields(from: vendorSettings, to: settings);
            }
            // Else: fresh install, nothing to seed from. settings already
            // holds class defaults from LoadOrDefault above -- correct
            // behavior, not an error.
        }

        settings.OffsetsSeeded = true;
        SettingsStore.Save(settingsFile, settings);
        return settings;
    }

    /// <summary>
    /// Every field either dialog can actually edit -- deliberately excludes
    /// TitleBlockName and the three template names, which are already
    /// correctly separate per command and must not be overwritten here.
    /// </summary>
    private static void CopyOffsetFields(VendorDrawingsSettings from, VendorDrawingsSettings to)
    {
        to.OffsetXMm = from.OffsetXMm;
        to.OffsetYMm = from.OffsetYMm;
        to.View3DOffsetXMm = from.View3DOffsetXMm;
        to.View3DOffsetYMm = from.View3DOffsetYMm;
        to.ScheduleOffsetXMm = from.ScheduleOffsetXMm;
        to.ScheduleOffsetYMm = from.ScheduleOffsetYMm;
        to.ClusterSpacingFactor = from.ClusterSpacingFactor;
        to.Views2DOffsetXMm = from.Views2DOffsetXMm;
        to.Views2DOffsetYMm = from.Views2DOffsetYMm;
        to.LeftPercent = from.LeftPercent;
        to.BottomRightReservedXPercent = from.BottomRightReservedXPercent;
        to.BottomRightReservedYPercent = from.BottomRightReservedYPercent;
    }

    public static AssemblyDrawingsSettings? ShowAssemblyDrawings(UIApplication uiApp)
    {
        var initial = SettingsStore.LoadOrDefault<AssemblyDrawingsSettings>("assembly-drawings.json");
        var dlg = new AssemblyDrawingsSettingsDialog(initial, uiApp.MainWindowHandle);
        if (dlg.ShowDialog() != true) return null;
        var result = dlg.Result;
        if (result is null) return null;
        SettingsStore.Save("assembly-drawings.json", result);
        return result;
    }

    public static MTDrawingsSettings? ShowMTDrawings(UIApplication uiApp)
    {
        var initial = SettingsStore.LoadOrDefault<MTDrawingsSettings>("mtq-drawings.json");
        var dlg = new MTDrawingsSettingsDialog(initial, uiApp.MainWindowHandle);
        if (dlg.ShowDialog() != true) return null;
        var result = dlg.Result;
        if (result is null) return null;
        SettingsStore.Save("mtq-drawings.json", result);
        return result;
    }
}


// =============================================================================
// 7. SHARED ASSEMBLY DRAWING PIPELINE
// =============================================================================
// Vendor and Korea build exactly the same views, schedule and isolation for an
// assembly; they differ only in how those end up on sheets. The pipeline below
// owns everything shared, and each command supplies an IAssemblySheetLayout for
// the part that varies.

/// <summary>Everything the shared pipeline built for one assembly.</summary>
internal sealed class AssemblySheetContext(
    Document doc,
    AssemblyInstance assembly,
    ElementId titleblockId,
    NameRegistry sheetNames,
    View3D? view3d,
    ViewSchedule? schedule,
    IReadOnlyDictionary<string, View> detailViews,
    double modelWidth,
    double modelHeight)
{
    public Document Doc { get; } = doc;
    public AssemblyInstance Assembly { get; } = assembly;
    public ElementId TitleblockId { get; } = titleblockId;
    public NameRegistry SheetNames { get; } = sheetNames;
    /// <summary>Null when Step 5 had 3D Views unchecked.</summary>
    public View3D? View3D { get; } = view3d;
    /// <summary>Null when Step 5 had Schedules unchecked.</summary>
    public ViewSchedule? Schedule { get; } = schedule;
    public IReadOnlyDictionary<string, View> DetailViews { get; } = detailViews;

    /// <summary>Widest horizontal span of the assembly.</summary>
    public double ModelWidth { get; } = modelWidth;

    /// <summary>Vertical span of the assembly.</summary>
    public double ModelHeight { get; } = modelHeight;

    /// <summary>
    /// Non-fatal issues found while placing this assembly's views, surfaced
    /// as a "warn" row in the results table (see AssemblyDrawingRunner.Run
    /// and Reporting's three-state ShowResult overload) rather than a
    /// dialog. Empty on a clean run. Only VendorSheetLayout.UpdateSecondSheet
    /// currently populates this; KoreaSheetLayout has no branch that ever
    /// declines to place a view, so it never has anything to add here.
    /// </summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>
/// The one thing that differs between the drawing commands: how the finished
/// views are distributed over sheets, at what scale, and where.
/// </summary>
internal interface IAssemblySheetLayout
{
    void PlaceViews(AssemblySheetContext ctx);
}

/// <summary>
/// The drawable area of a sheet, measured from the title block actually placed
/// on it rather than assumed from a paper size -- the sheet origin is wherever
/// the title block family puts it, so the centre has to be measured, not
/// computed from a width and a height.
/// </summary>
internal readonly record struct SheetFrame(XYZ Center, double UsableWidth, double UsableHeight)
{
    public static SheetFrame Measure(Document doc, ViewSheet sheet, double usableFraction)
    {
        // The title block instance only exists once the new sheet is regenerated.
        doc.Regenerate();

        var titleblock = new FilteredElementCollector(doc, sheet.Id)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType()
            .FirstElement();

        if (titleblock?.get_BoundingBox(sheet) is not { } bb)
        {
            throw new InvalidOperationException(
                "Could not measure the title block placed on the new sheet.");
        }

        var center = new XYZ(
            (bb.Min.X + bb.Max.X) / 2.0,
            (bb.Min.Y + bb.Max.Y) / 2.0,
            0);

        return new SheetFrame(
            center,
            Math.Abs(bb.Max.X - bb.Min.X) * usableFraction,
            Math.Abs(bb.Max.Y - bb.Min.Y) * usableFraction);
    }
}

/// <summary>
/// Stamps each 2D detail view with its own direction key (FRONT/BACK/LEFT/
/// RIGHT/TOP/BOTTOM), the assembly it belongs to, and which package drew it.
/// <para>
/// This replaces a much larger schema that also stored a derived Front/Back
/// axis and sign, a warning-condition record, and per-view extents -- all of
/// which existed to protect a GEOMETRY-DERIVED axis choice (which local axis
/// is shorter for this specific assembly). That derivation turned out to be
/// solving the wrong problem: this project's assembly origins are placed
/// deliberately, with +Y always facing the part's front, so Front/Back is a
/// fixed modeling convention, not something to infer from proportions --
/// confirmed when the derived rule agreed with the fixed one on two sampled
/// assemblies by coincidence (both happened to be longer along X) and
/// disagreed on the third. A fixed convention has no ambiguity to warn about
/// and nothing to stamp for stability, so the machinery built to guard that
/// derivation (near-tie ratio, angular-margin non-orthogonal check, drift
/// comparison, re-baselining, SchemaVersion) was removed with it, not kept
/// around unused. ONLY ViewKey survives that removal -- it has an actual
/// consumer, not just a planned one: VendorSheetLayout.UpdateSecondSheet
/// reads it (via TryRead below) to match an old viewport's role to the new
/// view that should replace it on Vendor's in-place re-run. AssemblyUniqueId/
/// PackageType alone would not serve that on their own, since without them a
/// bare "FRONT" string on some view in the document does not say which
/// package or assembly it belongs to.
/// </para>
/// <para>
/// New GUID, deliberately: the previous schema's field set was already
/// Finish()-ed (used to write real entities during this investigation), and a
/// schema's fields are frozen for good the moment that happens -- removing
/// fields requires a new one exactly as much as adding them would.
/// </para>
/// </summary>
internal static class AssemblyOrientationStorage
{
    // Fixed for the life of the add-in: changing it orphans every stamped view.
    private static readonly Guid SchemaGuid = new("8BD4CD89-CBEA-4AD2-B584-96229F6D3429");

    private const string SchemaName = "AVESAssemblyViewKey";
    // Must match <VendorId> in AVES.addin exactly -- see KoreaPackageStorage's
    // identical note below.
    private const string VendorVendorId = "Development";

    private const string FieldAssemblyUniqueId = "AssemblyUniqueId";
    private const string FieldPackageType = "PackageType";
    private const string FieldViewKey = "ViewKey"; // FRONT/BACK/LEFT/RIGHT/TOP/BOTTOM

    /// <summary>The schema, created on first use -- same pattern as
    /// KoreaPackageStorage.GetOrCreateSchema below.</summary>
    private static Autodesk.Revit.DB.ExtensibleStorage.Schema GetOrCreateSchema()
    {
        if (Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid) is { } existing)
            return existing;

        using var builder = new Autodesk.Revit.DB.ExtensibleStorage.SchemaBuilder(SchemaGuid);

        builder.SetSchemaName(SchemaName);
        builder.SetVendorId(VendorVendorId);
        builder.SetReadAccessLevel(Autodesk.Revit.DB.ExtensibleStorage.AccessLevel.Public);
        builder.SetWriteAccessLevel(Autodesk.Revit.DB.ExtensibleStorage.AccessLevel.Vendor);

        builder.AddSimpleField(FieldAssemblyUniqueId, typeof(string));
        builder.AddSimpleField(FieldPackageType, typeof(string));
        builder.AddSimpleField(FieldViewKey, typeof(string));
        // All string fields, so nothing here needs it, but: a double field
        // added later MUST call FieldBuilder.SetSpec(...) before Finish() or
        // Finish() throws "Units are required for field X" at runtime -- a
        // clean build will not catch a missing SetSpec.

        return builder.Finish();
    }

    public static void Stamp(View view, string assemblyUniqueId, string packageType, string viewKey)
    {
        var schema = GetOrCreateSchema();
        var entity = new Autodesk.Revit.DB.ExtensibleStorage.Entity(schema);

        entity.Set(FieldAssemblyUniqueId, assemblyUniqueId);
        entity.Set(FieldPackageType, packageType);
        entity.Set(FieldViewKey, viewKey);

        view.SetEntity(entity);
    }

    /// <summary>
    /// Reads the stamp off a view, for Vendor's in-place re-run to identify
    /// which of a sheet's several viewports is FRONT, TOP, and so on. False
    /// when the view was never stamped (predates this schema) or belongs to a
    /// different assembly/package -- callers fall back to name matching in
    /// that case rather than treating it as an error.
    /// </summary>
    public static bool TryRead(
        View view, out string assemblyUniqueId, out string packageType, out string viewKey)
    {
        assemblyUniqueId = "";
        packageType = "";
        viewKey = "";

        if (Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid) is not { } schema)
            return false;

        var entity = view.GetEntity(schema);
        if (!entity.IsValid()) return false;

        assemblyUniqueId = entity.Get<string>(FieldAssemblyUniqueId);
        packageType = entity.Get<string>(FieldPackageType);
        viewKey = entity.Get<string>(FieldViewKey);
        return true;
    }
}

/// <summary>
/// Recovers a 2D view's FRONT/BACK/LEFT/RIGHT/TOP/BOTTOM key from its name,
/// for views that predate AssemblyOrientationStorage's ViewKey stamp -- Step
/// B of Vendor's in-place re-run viewport matching, used by
/// VendorSheetLayout.UpdateSecondSheet when Step A (the stamp) does not
/// claim a viewport.
/// <para>
/// Strips a NameRegistry collision suffix (" (n)", see NameRegistry.Reserve)
/// before matching the trailing " - KEY" every 2D view is named with (see
/// AssemblyDrawingBuilder.Build). Returns null on no match -- an
/// unrecognized name is left alone, not guessed at.
/// </para>
/// </summary>
internal static class ViewKeyNameMatch
{
    private static readonly string[] Keys = ["FRONT", "BACK", "LEFT", "RIGHT", "TOP", "BOTTOM"];

    public static string? KeyFromViewName(string viewName)
    {
        var stripped = StripCollisionSuffix(viewName);
        foreach (var key in Keys)
        {
            if (stripped.EndsWith($" - {key}", StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return null;
    }

    private static string StripCollisionSuffix(string name)
    {
        var openParen = name.LastIndexOf(" (", StringComparison.Ordinal);
        if (openParen < 0 || !name.EndsWith(")", StringComparison.Ordinal))
            return name;

        var digits = name[(openParen + 2)..^1];
        return digits.Length > 0 && digits.All(char.IsDigit)
            ? name[..openParen]
            : name;
    }
}

/// <summary>
/// Marks each sheet of a Vendor drawing package with the assembly it
/// documents and the role it plays -- Sheet1 (3D + part list) or Sheet2
/// (six-view cluster) -- the same shape as KoreaPackageStorage below, under
/// its own GUID.
/// <para>
/// A separate schema from KoreaPackageStorage, deliberately, rather than a
/// generalized "PackageType" parameter added to it: Korea's schema and its
/// Stamp/FindPackageSheets methods are already live in stamped documents, and
/// coupling Vendor's package discovery to code Korea depends on means a
/// future Korea-only change could silently affect Vendor too. Same reasoning
/// AssemblyOrientationStorage got its own GUID for instead of extending
/// KoreaPackageStorage.
/// </para>
/// </summary>
internal static class VendorPackageStorage
{
    // Fixed for the life of the add-in: changing it orphans every stamped sheet.
    private static readonly Guid SchemaGuid = new("18FCD00C-254D-4620-9875-CE6CA438EC00");

    private const string SchemaName = "AVESVendorDrawingPackage";
    // Must match <VendorId> in AVES.addin exactly -- see KoreaPackageStorage's
    // identical note.
    private const string VendorVendorId = "Development";

    private const string FieldAssemblyId = "AssemblyId";
    private const string FieldAssemblyUniqueId = "AssemblyUniqueId";
    private const string FieldPackageType = "PackageType";
    private const string FieldSheetRole = "SheetRole";

    public const string PackageTypeVendor = "Vendor";

    /// <summary>Role of the 3D / part list sheet.</summary>
    public const string RoleSheet1 = "Sheet1";

    /// <summary>Role of the six-view cluster sheet.</summary>
    public const string RoleSheet2 = "Sheet2";

    private static Autodesk.Revit.DB.ExtensibleStorage.Schema GetOrCreateSchema()
    {
        if (Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid) is { } existing)
            return existing;

        using var builder = new Autodesk.Revit.DB.ExtensibleStorage.SchemaBuilder(SchemaGuid);

        builder.SetSchemaName(SchemaName);
        builder.SetVendorId(VendorVendorId);
        builder.SetReadAccessLevel(Autodesk.Revit.DB.ExtensibleStorage.AccessLevel.Public);
        builder.SetWriteAccessLevel(Autodesk.Revit.DB.ExtensibleStorage.AccessLevel.Vendor);

        builder.AddSimpleField(FieldAssemblyId, typeof(ElementId));
        builder.AddSimpleField(FieldAssemblyUniqueId, typeof(string));
        builder.AddSimpleField(FieldPackageType, typeof(string));
        builder.AddSimpleField(FieldSheetRole, typeof(string));

        return builder.Finish();
    }

    /// <summary>Stamps one sheet as part of this assembly's Vendor package.</summary>
    public static void Stamp(ViewSheet sheet, AssemblyInstance assembly, string role)
    {
        var schema = GetOrCreateSchema();
        var entity = new Autodesk.Revit.DB.ExtensibleStorage.Entity(schema);

        entity.Set(FieldAssemblyId, assembly.Id);
        entity.Set(FieldAssemblyUniqueId, assembly.UniqueId);
        entity.Set(FieldPackageType, PackageTypeVendor);
        entity.Set(FieldSheetRole, role);

        sheet.SetEntity(entity);
    }

    /// <summary>
    /// Every stamped sheet of this assembly's Vendor package, keyed by role.
    /// Empty when the package predates the stamp or does not exist.
    /// </summary>
    public static Dictionary<string, ViewSheet> FindPackageSheets(
        Document doc, AssemblyInstance assembly)
    {
        var found = new Dictionary<string, ViewSheet>(StringComparer.OrdinalIgnoreCase);

        if (Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid) is not { } schema)
            return found;

        var uniqueId = assembly.UniqueId;

        foreach (var sheet in new FilteredElementCollector(doc)
                     .OfClass(typeof(ViewSheet))
                     .Cast<ViewSheet>())
        {
            var entity = sheet.GetEntity(schema);
            if (!entity.IsValid()) continue;

            if (entity.Get<string>(FieldPackageType) != PackageTypeVendor) continue;
            if (entity.Get<string>(FieldAssemblyUniqueId) != uniqueId) continue;

            var role = entity.Get<string>(FieldSheetRole);
            if (!string.IsNullOrEmpty(role))
                found[role] = sheet;
        }

        return found;
    }
}

/// <summary>Whether a swap replaced the viewport's view directly, or -- because
/// Revit refused that -- the old viewport was deleted and a new one created
/// at the same position instead.</summary>
internal enum ViewportSwapResult
{
    Swapped,
    Recreated,
}

/// <summary>
/// Viewport/schedule replacement shared by every package layout that places
/// at most one viewport per sheet -- Korea's seven sheets, and Vendor's
/// sheet 1. Hoisted out of KoreaSheetLayout, which had been the only caller,
/// so Vendor can reuse it byte-for-byte rather than keeping a second copy in
/// step with the first. Vendor's sheet 2 (six viewports) needs its own
/// matching logic instead -- see VendorSheetLayout.UpdateSecondSheet.
/// </summary>
internal static class PackageSheetHelpers
{
    /// <summary>
    /// The sheet's single viewport, or null when it carries none or more than
    /// one -- anything else is not a package this command produced, so it is
    /// not guessed at.
    /// </summary>
    public static Viewport? TryGetSingleViewport(Document doc, ViewSheet sheet)
    {
        var viewportIds = sheet.GetAllViewports();
        if (viewportIds.Count != 1) return null;

        return doc.GetElement(viewportIds.First()) as Viewport;
    }

    /// <summary>
    /// Points an existing viewport at <paramref name="replacement"/>. When
    /// Revit refuses that (IsViewIdValidForViewport false -- a view-class
    /// mismatch on the existing Viewport element, such as a legacy assembly
    /// view swapping to a different view family), deletes the old viewport
    /// and creates a new one at the same position instead, copying the old
    /// viewport's type across explicitly so the replacement does not default
    /// to whatever type the document last used -- a swap never touches type,
    /// so both paths leave every viewport on a sheet looking consistent with
    /// its neighbours.
    /// <para>
    /// Either path deletes the old view and hands the replacement its name,
    /// so nothing is left orphaned and nothing keeps a "(1)" suffix once the
    /// transaction commits.
    /// </para>
    /// <para>
    /// Never silently no-ops on refusal: the version this replaced returned
    /// without doing anything, which left the sheet showing stale content
    /// with nothing reported anywhere -- exactly the failure mode this whole
    /// investigation kept surfacing. The caller gets back which path was
    /// taken so a recreate can be reported as a warning.
    /// </para>
    /// <para>
    /// The recreate branch below is defensive, not confirmed: no view pairing
    /// this project could construct was ever actually refused by
    /// IsViewIdValidForViewport, including a cross-family one (section to
    /// plan) that was assumed refusable and was not. Same-family swaps
    /// (Korea's actual production usage, both ViewSection and ViewPlan keys)
    /// were confirmed on live packages to preserve box centre, rotation,
    /// label offset and label line length exactly, with no regenerate call
    /// needed between the ViewId assignment and SetBoxCenter below. The one
    /// tested cross-family pairing that Revit DID allow -- section to plan,
    /// going through THIS swap branch, not the recreate one -- silently
    /// moved the box centre by several inches and did not restore
    /// LabelLineLength. That pairing can only arise from adopting a package
    /// old enough to carry a different view class per key than the current
    /// builder produces; nothing in current usage constructs one. Left
    /// undesigned-around rather than chased further, since the migration
    /// case it would affect may never occur at all -- see VendorPackageStorage
    /// and the Vendor re-run design notes for why.
    /// </para>
    /// </summary>
    public static ViewportSwapResult SwapOrRecreateViewport(
        Document doc, Viewport viewport, View replacement)
    {
        var oldViewId = viewport.ViewId;
        var oldViewName = (doc.GetElement(oldViewId) as View)?.Name;
        var boxCenter = viewport.GetBoxCenter();

        if (viewport.IsViewIdValidForViewport(replacement.Id))
        {
            // Captured before the swap so the viewport lands exactly where it
            // was; restored explicitly so the result does not depend on
            // ViewportPositioning. Confirmed exact on same-family swaps with
            // no intervening regenerate; NOT confirmed exact on a cross-family
            // pairing Revit happens to allow through this branch -- see this
            // method's class-level remarks.
            var rotation = viewport.Rotation;
            var labelOffset = viewport.LabelOffset;
            var labelLineLength = viewport.LabelLineLength;

            viewport.ViewId = replacement.Id;

            viewport.SetBoxCenter(boxCenter);
            viewport.Rotation = rotation;
            viewport.LabelOffset = labelOffset;
            viewport.LabelLineLength = labelLineLength;

            // Delete first, then rename: the old name is only free once the
            // old view is gone, and the replacement carries a "(1)" suffix
            // until then.
            if (oldViewId != ElementId.InvalidElementId)
                doc.Delete(oldViewId);
            if (!string.IsNullOrEmpty(oldViewName))
                replacement.Name = oldViewName;

            return ViewportSwapResult.Swapped;
        }

        // Refused: this Viewport element cannot be repointed to replacement's
        // view class. Capture what a fresh Viewport.Create cannot supply on
        // its own, then delete the old view -- which cascades to delete the
        // now-unreferenced viewport, since a Viewport cannot outlive its view.
        var sheetId = viewport.SheetId;
        var oldTypeId = viewport.GetTypeId();

        if (oldViewId != ElementId.InvalidElementId)
            doc.Delete(oldViewId);
        if (!string.IsNullOrEmpty(oldViewName))
            replacement.Name = oldViewName;

        var newViewport = Viewport.Create(doc, sheetId, replacement.Id, boxCenter);
        newViewport.SetBoxCenter(boxCenter);
        newViewport.ChangeTypeId(oldTypeId);

        return ViewportSwapResult.Recreated;
    }

    /// <summary>
    /// Swaps in a regenerated schedule.
    /// <para>
    /// ScheduleSheetInstance.ScheduleId is read-only in both 2025 and 2026, so a
    /// placed schedule cannot be rebound to another schedule view the way a
    /// Viewport can. The instance is therefore recreated -- but its exact point,
    /// rotation and segment are carried over first, and the old schedule view is
    /// deleted before the replacement takes its name, so nothing gains a "(1)"
    /// suffix and the sheet looks unchanged apart from the contents.
    /// </para>
    /// </summary>
    public static void ReplaceScheduleOnSheet(
        Document doc, ViewSheet sheet, ViewSchedule replacement, XYZ point)
    {
        // Title block revision schedules live on the same sheet and must not be
        // mistaken for the part list.
        var existing = new FilteredElementCollector(doc, sheet.Id)
            .OfClass(typeof(ScheduleSheetInstance))
            .Cast<ScheduleSheetInstance>()
            .FirstOrDefault(s => !s.IsTitleblockRevisionSchedule);

        if (existing is null)
        {
            ScheduleSheetInstance.Create(doc, sheet.Id, replacement.Id, point);
            return;
        }

        // Rotation and segment carry over -- only the position is recomputed.
        var rotation = existing.Rotation;
        var segmentIndex = existing.SegmentIndex;

        var oldScheduleId = existing.ScheduleId;
        var oldScheduleName = (doc.GetElement(oldScheduleId) as View)?.Name;

        // Instance first, then the view it pointed at: deleting the view would
        // take the instance with it before rotation and segment are read.
        doc.Delete(existing.Id);

        if (oldScheduleId != ElementId.InvalidElementId)
            doc.Delete(oldScheduleId);

        if (!string.IsNullOrEmpty(oldScheduleName))
            replacement.Name = oldScheduleName;

        var placed = ScheduleSheetInstance.Create(
            doc, sheet.Id, replacement.Id, point, segmentIndex);

        placed.Point = point;
        placed.Rotation = rotation;
    }
}

/// <summary>
/// Builds the six orthographic views, the 3D view and the part list for one
/// assembly, applies templates and isolates the assembly in every view. Layout
/// independent -- nothing here knows about sheets.
/// </summary>
internal static class AssemblyDrawingBuilder
{
    public static readonly IReadOnlyDictionary<string, string> ViewTitles =
        new Dictionary<string, string>
        {
            ["3D"] = "3D View",
            ["TOP"] = "Top View",
            ["BOTTOM"] = "Bottom View",
            ["FRONT"] = "Front View",
            ["BACK"] = "Back View",
            ["LEFT"] = "Left View",
            ["RIGHT"] = "Right View",
        };

    // FRONT/BACK look along local Y; LEFT/RIGHT along local X. Fixed, for
    // every assembly -- NOT derived from which horizontal span happens to be
    // shorter, which is what an earlier version of this code tried.
    //
    // Why fixed rather than derived: this project's assembly origins are
    // placed deliberately, with green (+Y) always facing the part's intended
    // front -- a modeling convention, not a proportion. A geometry-derived
    // "shorter axis reads as the broad face" rule agreed with this on two
    // sampled assemblies (371014, 371024 -- both happen to be longer along X)
    // and disagreed on the third (371044, longer along Y, where the derived
    // rule wrongly picked X). Two out of three agreeing was coincidence, not
    // confirmation: the derived rule was solving a different question (which
    // axis is broader) than the one that actually determines Front (which
    // axis the origin was placed to face). Restoring a fixed table is not a
    // reversion to the ORIGINAL bug -- that table pointed Front at local X;
    // this one correctly points it at local Y.
    //
    // Sign: FRONT is the POSITIVE direction along Y, BACK the negative; RIGHT
    // is the negative direction along X, LEFT the positive. The only visual
    // check actually completed in this investigation found FRONT/BACK
    // swapped under FRONT=negative-Y (on 371014/371024, where this axis was
    // already Y before the fixed convention existed) -- so FRONT=positive-Y
    // is the corrected value. A later prompt flipped this same sign in the
    // derived-rule code, but that build was never visually re-checked before
    // being superseded by the axis-derivation reversal; that unverified
    // step is not treated as confirmation here. RIGHT/LEFT were checked
    // correct throughout and are unchanged.
    internal static readonly IReadOnlyDictionary<string, XYZ> SideLookDirections =
        new Dictionary<string, XYZ>
        {
            ["FRONT"] = new XYZ(0, 1, 0),
            ["BACK"] = new XYZ(0, -1, 0),
            ["RIGHT"] = new XYZ(-1, 0, 0),
            ["LEFT"] = new XYZ(1, 0, 0),
        };

    // TOP/BOTTOM's screen-right anchor (see Build) -- deliberately its own
    // constant, not reused from SideLookDirections["FRONT"], and NOT
    // required to track FRONT's sign. This value (negative Y) is what was
    // on screen and confirmed correct for TOP/BOTTOM the one time it was
    // checked; the Front/Back sign correction above must not change it,
    // which is the whole point of splitting it out -- an earlier version of
    // this code reused Front's vector directly, so flipping Front's sign
    // silently rotated Top/Bottom as a side effect.
    internal static readonly XYZ ScreenRightAnchorLocal = new(0, -1, 0);

    // Breathing room left around the assembly in the six 2D views. Small
    // enough that the crop hugs the model, large enough that tags and
    // annotation on the outermost members do not get clipped.
    private const double CropPaddingMm = 10.0;

    public static AssemblySheetContext Build(
        Document doc,
        AssemblyInstance assembly,
        DrawingSetup setup,
        ElementId titleblockId,
        double sectionBoxPadding,
        NameRegistry viewNames, NameRegistry sheetNames,
        string packageType)
    {
        var bbox = GeometryUtils.GetAssemblyBoundingBox(doc, assembly)
            ?? throw new InvalidOperationException(
                "Could not compute a bounding box for this assembly.");

        var (sizeX, sizeY, sizeZ) = GeometryUtils.GetSize(bbox);
        var modelW = Math.Max(sizeX, sizeY);
        var modelH = sizeZ;

        // Each category is gated by its Step 5 flag. A skipped category creates
        // nothing and touches nothing already in the model -- including the view
        // family type lookups, so a run with 2D off cannot fail on a project that
        // has no Section or Floor Plan type.
        View3D? view3d = null;
        if (setup.Generate3DViews)
        {
            // Standard orthographic View3D, not an assembly 3D view. A section box
            // does the framing, so the assembly has to be isolated explicitly the
            // way the 2D views are.
            var vft3d = ViewUtils.Get3DViewFamilyType(doc)
                ?? throw new InvalidOperationException(
                    "No 3D view family type found in this project.");

            view3d = ViewUtils.CreateOrthographic3D(
                doc, vft3d, viewNames.Reserve($"{assembly.Name} - 3D"),
                setup.V3d!.Id, bbox, sectionBoxPadding);
            ViewUtils.IsolateAssemblyPermanently(doc, view3d, assembly);

            ViewUtils.SetDescription(view3d, ViewTitles["3D"]);
        }

        ViewSchedule? schedule = null;
        if (setup.GenerateSchedules)
        {
            schedule = AssemblyViewUtils.CreatePartList(doc, assembly.Id);
            schedule.Name = viewNames.Reserve($"{assembly.Name} - Part List");
            schedule.ViewTemplateId = setup.Sched!.Id;
        }

        var detailViews = new Dictionary<string, View>(6);

        if (setup.Generate2DViews)
        {
            // Every look direction below is stated in the assembly's own local
            // axes (SideLookDirections/ScreenRightAnchorLocal, both fixed) and
            // mapped into world space through its transform.
            var assemblyFrame = assembly.GetTransform();
            XYZ MapDirection(XYZ local) => assemblyFrame.OfVector(local).Normalize();

            // CropPaddingMm is for the 2D crop regions only.
            var viewPadding = UnitUtils.ConvertToInternalUnits(
                CropPaddingMm, UnitTypeId.Millimeters);

            var sectionVft = ViewUtils.GetViewFamilyType(doc, ViewFamily.Section)
                ?? throw new InvalidOperationException(
                    "No Section view family type found in this project.");
            var floorPlanVft = ViewUtils.GetViewFamilyType(doc, ViewFamily.FloorPlan)
                ?? throw new InvalidOperationException(
                    "No Floor Plan view family type found in this project.");

            var bboxCenter = (bbox.Min + bbox.Max) / 2.0;

            var level = LevelUtils.FindNearestLevel(doc, bboxCenter)
                ?? throw new InvalidOperationException("No levels found in this project.");

            foreach (var (key, lookDirection) in SideLookDirections)
            {
                detailViews[key] = ViewUtils.CreateOrthoSection(
                    doc, sectionVft.Id, bbox, MapDirection(lookDirection), viewPadding);
            }

            // Plan and RCP views are born north-up, which puts them out of step
            // with the sections. Turn them so their screen-right follows the
            // fixed Y-axis anchor -- see ScreenRightAnchorLocal's own comment
            // for why this is a separate constant from SideLookDirections
            // rather than a reuse of ["FRONT"].
            var frontRight = XYZ.BasisZ
                .CrossProduct(MapDirection(ScreenRightAnchorLocal))
                .Normalize();

            // Fit, rotate, fit: the first fit keeps the crop small so the rotation's
            // view-scoped lookup stays cheap, the second re-states the same extents
            // in the turned frame. Both calls are the untouched fitting routine.
            var topView = ViewPlan.Create(doc, floorPlanVft.Id, level.Id);
            ViewUtils.FitViewRangeToBoundingBox(topView, bbox, level, viewPadding);
            ViewUtils.FitCropBoxToBoundingBox(topView, bbox, viewPadding);
            ViewUtils.RotateCropToRightDirection(doc, topView, frontRight, bboxCenter);
            ViewUtils.FitCropBoxToBoundingBox(topView, bbox, viewPadding);
            detailViews["TOP"] = topView;

            // BOTTOM is an upward section, not a Reflected Ceiling Plan: an RCP
            // mirrors what it draws and nothing in the API undoes that. It takes its
            // crop from the creation box, exactly as the four side sections do, so
            // it needs neither a view range nor a crop rotation.
            var bottomView = ViewUtils.CreateUpwardSection(
                doc, sectionVft.Id, bbox, frontRight, viewPadding);
            detailViews["BOTTOM"] = bottomView;

            foreach (var (key, view) in detailViews)
            {
                view.Name = viewNames.Reserve($"{assembly.Name} - {key}");
                TryApplyTemplate(view, setup.V2d!.Id);
                ViewUtils.IsolateAssemblyPermanently(doc, view, assembly);

                if (ViewTitles.TryGetValue(key, out var title))
                    ViewUtils.SetDescription(view, title);

                // ViewKey only -- Vendor's in-place re-run reads this to match
                // an old viewport to the new view that should replace it (see
                // VendorSheetLayout.UpdateSecondSheet). No axis/sign left to
                // stamp once Front/Back is a fixed convention rather than a
                // per-assembly derivation.
                AssemblyOrientationStorage.Stamp(view, assembly.UniqueId, packageType, key);
            }
        }

        return new AssemblySheetContext(
            doc, assembly, titleblockId, sheetNames,
            view3d, schedule, detailViews, modelW, modelH);
    }

    private static void TryApplyTemplate(View view, ElementId templateId)
    {
        try { view.ViewTemplateId = templateId; }
        catch { /* template not valid for this view type; leave view without one */ }
    }
}

/// <summary>Per-command wording and the layout that command draws with.</summary>
internal sealed record AssemblyDrawingProfile(
    string AlertTitle,
    string TransactionName,
    string ReportTitle,
    Func<UIApplication, IAssemblySheetLayout?> CreateLayout,

    /// <summary>
    /// Settings file this command remembers its Window 1 picks in -- the three
    /// view templates and the title block. Vendor and Korea share the window
    /// and the settings type but not the file, so a template chosen in one
    /// never resurfaces as the restored choice in the other.
    /// </summary>
    string TemplateSettingsFile,

    /// <summary>
    /// "Korea" or "Vendor" -- scopes AssemblyOrientationStorage's per-view
    /// key stamp to this command, so Vendor's in-place re-run viewport
    /// matching (VendorSheetLayout.UpdateSecondSheet) can tell Korea's and
    /// Vendor's views of the same assembly apart.
    /// </summary>
    string PackageType);

/// <summary>
/// Drives the whole run: templates, assemblies, title block, settings, the
/// per-assembly transaction loop and the report. Identical for every drawing
/// command -- only the profile and its layout differ.
/// </summary>
internal static class AssemblyDrawingRunner
{
    public static Result Run(
        UIApplication uiApp, AssemblyDrawingProfile profile, out string message)
    {
        message = string.Empty;
        var doc = uiApp.ActiveUIDocument.Document;

        try
        {
            var templates = ViewUtils.GetTemplates(doc);
            if (templates.Count == 0)
            {
                AvControls.ShowAlert(profile.AlertTitle,
                    "No view templates found in this project.", AlertSeverity.Warning);
                return Result.Failed;
            }

            // Loaded up front so the setup window can preselect the title block
            // this command was last run with.
            var saved = SettingsStore
                .LoadOrDefault<VendorDrawingsSettings>(profile.TemplateSettingsFile);

            // Collected before the setup window so its assembly column can offer
            // them, already alphabetical from the shared collector.
            var assemblies = CollectAssemblies(doc);
            if (assemblies.Count == 0)
            {
                AvControls.ShowAlert(profile.AlertTitle,
                    "No assemblies found in this project.", AlertSeverity.Warning);
                return Result.Failed;
            }

            var setup = TemplatePicker.PickSetup(
                templates, doc, saved.TitleBlockName,
                saved.View3DTemplateName, saved.View2DTemplateName,
                saved.ScheduleTemplateName, assemblies,
                profile.AlertTitle, uiApp.MainWindowHandle);
            if (setup is null || !setup.IsComplete) return Result.Cancelled;

            // Persisted before the settings dialog opens, so the dialog loads and
            // re-saves the same choice rather than dropping it.
            saved.TitleBlockName = TitleblockUtils.DisplayName(setup.Titleblock!);
            saved.View3DTemplateName = setup.V3d?.Name;
            saved.View2DTemplateName = setup.V2d?.Name;
            saved.ScheduleTemplateName = setup.Sched?.Name;
            SettingsStore.Save(profile.TemplateSettingsFile, saved);

            if (profile.CreateLayout(uiApp) is not { } layout)
                return Result.Cancelled;

            // Section box padding is owned by the Assembly Drawings settings and
            // read from there, so every command frames its 3D views identically
            // rather than each keeping its own copy of the number.
            var sectionBoxPadding = SettingsStore
                .LoadOrDefault<AssemblyDrawingsSettings>("assembly-drawings.json")
                .SectionBoxPaddingFt();

            var viewNames = BuildViewNameRegistry(doc);
            var sheetNames = BuildSheetNameRegistry(doc);

            var results = new List<(string Name, string? Error, string? Warning)>();

            using var tx = new Transaction(doc, profile.TransactionName);
            tx.Start();
            FailureHandling.SuppressWarnings(tx);

            foreach (var assembly in setup.SelectedAssemblies)
            {
                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    var ctx = AssemblyDrawingBuilder.Build(
                        doc, assembly, setup,
                        setup.Titleblock!.Id, sectionBoxPadding, viewNames, sheetNames,
                        profile.PackageType);

                    layout.PlaceViews(ctx);

                    sub.Commit();
                    var warning = ctx.Warnings.Count > 0
                        ? string.Join("\n", ctx.Warnings)
                        : null;
                    results.Add((assembly.Name, null, warning));
                }
                catch (Exception ex)
                {
                    sub.RollBack();
                    results.Add((assembly.Name, ex.Message, null));
                }
            }

            tx.Commit();

            // A warning (Vendor's sheet-2 matching could not confidently
            // place every viewport -- see VendorSheetLayout.UpdateSecondSheet
            // and AssemblySheetContext.Warnings) surfaces as a "warn" row on
            // the table below via Reporting's three-state ShowResult
            // overload, not a dialog. Korea never populates ctx.Warnings
            // today -- KoreaSheetLayout has no branch that declines to place
            // a view -- so this state is currently Vendor-only in practice,
            // even though both commands go through this same runner.
            Reporting.ShowResult(profile.ReportTitle, results, uiApp.MainWindowHandle);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    /// <summary>
    /// Every assembly in the project, ordered by name so a batch processes them
    /// in the same sequence every run. The collector itself yields Revit's
    /// internal element order, which tracks creation order and is not a
    /// meaningful ordering to a user.
    /// </summary>
    internal static List<AssemblyInstance> CollectAssemblies(Document doc) =>
        new FilteredElementCollector(doc)
            .OfCategory(BuiltInCategory.OST_Assemblies)
            .WhereElementIsNotElementType()
            .OfType<AssemblyInstance>()
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    internal static NameRegistry BuildViewNameRegistry(Document doc) =>
        new(new FilteredElementCollector(doc)
            .OfClass(typeof(View))
            .Cast<View>()
            .Select(v => v.Name));

    internal static NameRegistry BuildSheetNameRegistry(Document doc) =>
        new(new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Select(s => s.Name));
}

/// <summary>
/// The existing two-sheet A3 layout: 3D plus part list on sheet 1, the six
/// orthographic views clustered in first-angle projection on sheet 2.
/// </summary>
internal sealed class VendorSheetLayout(VendorDrawingsSettings settings) : IAssemblySheetLayout
{
    public void PlaceViews(AssemblySheetContext ctx)
    {
        var doc = ctx.Doc;
        var offsetX = settings.OffsetXFt();
        var offsetY = settings.OffsetYFt();

        // The sheets are the durable object; views and schedules on them are
        // disposable. If this assembly already has a Vendor package, both
        // sheets are refreshed in place rather than duplicated.
        var existingSheets = ResolvePackageSheets(doc, ctx.Assembly);
        existingSheets.TryGetValue(VendorPackageStorage.RoleSheet1, out var existingSheet1);
        existingSheets.TryGetValue(VendorPackageStorage.RoleSheet2, out var existingSheet2);

        // Sheet 1 carries the 3D view and the schedule; sheet 2 carries the
        // cluster. A sheet whose only contents were skipped in Step 5 is not
        // created (or updated), so a partial run leaves no empty sheets behind.
        var wantsSheet1 = ctx.View3D is not null || ctx.Schedule is not null;
        var wantsSheet2 = ctx.DetailViews.Count > 0;

        if (ctx.View3D is { } view3d)
        {
            var view3dAvailW = RevitConstants.A3WidthFt * settings.LeftPercent
                               - Math.Abs(settings.View3DOffsetXFt());
            var view3dAvailH = RevitConstants.A3HeightFt
                               - Math.Abs(settings.View3DOffsetYFt());

            view3d.Scale = GeometryUtils.PickScale(
                ctx.ModelWidth, ctx.ModelHeight, view3dAvailW, view3dAvailH);
        }

        if (wantsSheet2)
        {
            var reservedW = RevitConstants.A3WidthFt * settings.BottomRightReservedXPercent;
            var reservedH = RevitConstants.A3HeightFt * settings.BottomRightReservedYPercent;
            var scale2d = GeometryUtils.PickScale(
                ctx.ModelWidth * 3.0, ctx.ModelHeight * 3.0,
                RevitConstants.A3WidthFt - reservedW,
                RevitConstants.A3HeightFt - reservedH);
            foreach (var v in ctx.DetailViews.Values) v.Scale = scale2d;
        }

        var view3dPosition = new XYZ(
            settings.View3DOffsetXFt() + offsetX,
            settings.View3DOffsetYFt() + offsetY,
            0);

        var schedulePosition = new XYZ(
            settings.ScheduleOffsetXFt() + offsetX,
            settings.ScheduleOffsetYFt() + offsetY,
            0);

        if (wantsSheet1)
        {
            if (existingSheet1 is not null)
                UpdateFirstSheet(ctx, existingSheet1, view3dPosition, schedulePosition);
            else
                CreateFirstSheet(ctx, view3dPosition, schedulePosition);
        }

        if (!wantsSheet2) return;

        if (existingSheet2 is not null)
        {
            UpdateSecondSheet(ctx, existingSheet2, offsetX, offsetY);
            return;
        }

        var sheet2 = ViewSheet.Create(doc, ctx.TitleblockId);
        sheet2.Name = ctx.SheetNames.Reserve($"V-ASM_{ctx.Assembly.Name}_02");
        VendorPackageStorage.Stamp(sheet2, ctx.Assembly, VendorPackageStorage.RoleSheet2);

        var spacing = RevitConstants.A3WidthFt * settings.ClusterSpacingFactor;
        var clusterCenter = new XYZ(offsetX, offsetY, 0);

        foreach (var (key, (dx, dy)) in BuildFirstAngleClusterPositions(spacing))
        {
            if (!ctx.DetailViews.TryGetValue(key, out var v)) continue;
            Viewport.Create(doc, sheet2.Id, v.Id,
                new XYZ(clusterCenter.X + dx, clusterCenter.Y + dy, 0));
        }
    }

    /// <summary>
    /// Matches sheet 2's existing viewports to FRONT/BACK/LEFT/RIGHT/TOP/
    /// BOTTOM and swaps each one to its freshly built replacement, in place
    /// of duplicating the sheet.
    /// <para>
    /// Resolution is two steps, in order, no third: (A) read
    /// AssemblyOrientationStorage's ViewKey stamp off the viewport's current
    /// view, requiring AssemblyUniqueId to match this assembly and ViewKey to
    /// be one of the six known keys; (B), for anything Step A did not claim,
    /// strip a NameRegistry " (n)" collision suffix and match a trailing
    /// " - KEY" (case-insensitive) -- the naming every 2D view has carried
    /// since before the ViewKey stamp existed. No geometry-derived fallback:
    /// that shape of thing is exactly what this whole investigation spent
    /// its effort removing.
    /// </para>
    /// <para>
    /// An unresolved key's handling depends on whether ANY viewport on THIS
    /// SHEET actually resolved via the ViewKey stamp (Step A) this run --
    /// not on how the sheet itself was found. Those are different questions:
    /// VendorPackageStorage's sheet-level stamp, once written on a sheet's
    /// first re-run, stays forever regardless of what the individual views on
    /// it carry, so a sheet can be "stamped" at the VendorPackageStorage
    /// level while every one of its views has since lost its ViewKey stamp
    /// (exactly what stripping it for a test produces). The case rule needs
    /// the second question, not the first: on a sheet where Step A is
    /// demonstrably still working (something else on it resolved that way
    /// this run), a missing key means the viewport was genuinely deleted --
    /// Step A would have found a merely-renamed one regardless of its name --
    /// so it is recreated at its standard cluster position. On a sheet where
    /// nothing resolved via Step A this run, "genuinely missing" and "present
    /// but renamed and unstamped" are indistinguishable, so it is left alone
    /// and warned about rather than guessed at. Two viewports claiming the
    /// same key are both left alone and warned about, naming both. A
    /// viewport neither step claims (user-added, or a view renamed on an
    /// already-stamped package that somehow lost its stamp) is left alone
    /// with no warning -- it is still sitting there, visibly untouched,
    /// which is the correct outcome for something this code does not
    /// recognize as its own.
    /// </para>
    /// <para>
    /// Exception: if literally none of the six keys resolve (every viewport
    /// deleted, or every view renamed beyond recognition), the conservative
    /// per-key rule above would warn six times and place nothing -- and since
    /// nothing here ever gets swapped, nothing gets re-stamped either, so a
    /// later run lands in the exact same unresolvable state forever. That
    /// case is handled separately, before the per-key loop: build a fresh
    /// sheet 2 beside the unrecoverable one (today's pre-Stage-3 behavior)
    /// and say so once, rather than leave the package permanently stuck. The
    /// old sheet is left untouched either way.
    /// </para>
    /// <para>
    /// Warnings are appended to ctx.Warnings, not shown directly -- see
    /// AssemblyDrawingRunner.Run, which reads them after PlaceViews returns
    /// and reports them as a "warn" row on the results table rather than a
    /// dialog.
    /// </para>
    /// </summary>
    private void UpdateSecondSheet(
        AssemblySheetContext ctx, ViewSheet sheet2, double offsetX, double offsetY)
    {
        var doc = ctx.Doc;
        var assembly = ctx.Assembly;

        var allViewports = sheet2.GetAllViewports()
            .Select(id => doc.GetElement(id) as Viewport)
            .Where(v => v is not null)
            .Cast<Viewport>()
            .ToList();

        var claimsByKey = new Dictionary<string, List<Viewport>>(StringComparer.OrdinalIgnoreCase);
        var anyResolvedByStamp = false;

        foreach (var vp in allViewports)
        {
            if (doc.GetElement(vp.ViewId) is not View view) continue;

            string? key = null;

            if (AssemblyOrientationStorage.TryRead(
                    view, out var stampedAssemblyId, out _, out var stampedKey)
                && stampedAssemblyId == assembly.UniqueId
                && IsKnownKey(stampedKey))
            {
                key = stampedKey;
                anyResolvedByStamp = true;
            }
            else if (ViewKeyNameMatch.KeyFromViewName(view.Name) is { } nameKey)
            {
                key = nameKey;
            }

            if (key is null) continue;

            if (!claimsByKey.TryGetValue(key, out var claimants))
                claimsByKey[key] = claimants = [];
            claimants.Add(vp);
        }

        var spacing = RevitConstants.A3WidthFt * settings.ClusterSpacingFactor;
        var clusterCenter = new XYZ(offsetX, offsetY, 0);
        var clusterPositions = BuildFirstAngleClusterPositions(spacing);

        if (claimsByKey.Count == 0)
        {
            // Nothing on this sheet resolved to any key -- every viewport was
            // either deleted or renamed beyond recognition. The per-key rule
            // below would warn on all six and place none of them, and since
            // nothing here ever gets swapped, nothing gets re-stamped either
            // -- the next run would land in this exact same state, forever.
            // Rather than leave the package permanently stuck, build a fresh
            // sheet 2 beside the unrecoverable one -- the same thing a run
            // would have done before this stage existed -- and say so once,
            // instead of six warnings naming a problem nothing can act on
            // without opening the model by hand. The old sheet is left
            // completely untouched.
            var freshSheet2 = ViewSheet.Create(doc, ctx.TitleblockId);
            freshSheet2.Name = ctx.SheetNames.Reserve($"V-ASM_{assembly.Name}_02");
            VendorPackageStorage.Stamp(freshSheet2, assembly, VendorPackageStorage.RoleSheet2);

            foreach (var (key, (dx, dy)) in clusterPositions)
            {
                if (!ctx.DetailViews.TryGetValue(key, out var v)) continue;
                Viewport.Create(doc, freshSheet2.Id, v.Id,
                    new XYZ(clusterCenter.X + dx, clusterCenter.Y + dy, 0));
            }

            ctx.Warnings.Add(
                $"Sheet 2: none of the six viewports on the existing sheet " +
                $"(\"{sheet2.SheetNumber} - {sheet2.Name}\") could be matched to FRONT/BACK/" +
                "LEFT/RIGHT/TOP/BOTTOM -- every view was either deleted or renamed beyond " +
                $"recognition. Built a new sheet (\"{freshSheet2.SheetNumber} - " +
                $"{freshSheet2.Name}\") instead of leaving the package with no drawings. " +
                "The old sheet was left untouched -- delete it by hand if it's no longer needed.");

            return;
        }

        foreach (var (key, replacement) in ctx.DetailViews)
        {
            claimsByKey.TryGetValue(key, out var claimants);

            if (claimants is { Count: 1 })
            {
                PackageSheetHelpers.SwapOrRecreateViewport(doc, claimants[0], replacement);
                continue;
            }

            if (claimants is { Count: > 1 })
            {
                var named = string.Join(", ", claimants.Select(vp =>
                {
                    var v = doc.GetElement(vp.ViewId) as View;
                    return $"\"{v?.Name}\" (view Id={vp.ViewId}, viewport Id={vp.Id})";
                }));
                ctx.Warnings.Add($"Sheet 2, {key}: {claimants.Count} viewports claim this key " +
                                  $"-- left untouched: {named}");
                // This run's freshly built replacement for this key is not
                // going anywhere -- delete it, or it sits in the document
                // unplaced, holding the canonical name, and pushes every
                // later attempt at this key into a " (1)", " (2)", ...
                // collision with itself.
                doc.Delete(replacement.Id);
                continue;
            }

            // No existing viewport resolved to this key.
            if (anyResolvedByStamp)
            {
                var (dx, dy) = clusterPositions[key];
                Viewport.Create(doc, sheet2.Id, replacement.Id,
                    new XYZ(clusterCenter.X + dx, clusterCenter.Y + dy, 0));
            }
            else
            {
                ctx.Warnings.Add(
                    $"Sheet 2, {key}: no viewport on this sheet resolves to this key, and " +
                    $"this package has no ViewKey stamp to fall back on for {key} -- if a " +
                    $"view for {key} still exists but was renamed, rename it back to end in " +
                    $"\" - {key}\" and it will be picked up (and permanently protected " +
                    "against this happening again) on the next re-run. Left unplaced rather " +
                    "than guess which viewport it might be.");
                // Same leak as the conflict case above: this run's fresh
                // replacement is unused and would otherwise sit around
                // colliding with itself on every future attempt.
                doc.Delete(replacement.Id);
            }
        }
    }

    private static bool IsKnownKey(string key) =>
        key is "FRONT" or "BACK" or "LEFT" or "RIGHT" or "TOP" or "BOTTOM";

    /// <summary>Creates sheet 1 fresh: no existing package was found for this
    /// assembly, or Step 5's 3D/Schedule categories were never requested
    /// before now.</summary>
    private void CreateFirstSheet(AssemblySheetContext ctx, XYZ view3dPosition, XYZ schedulePosition)
    {
        var doc = ctx.Doc;

        var sheet1 = ViewSheet.Create(doc, ctx.TitleblockId);
        sheet1.Name = ctx.SheetNames.Reserve($"V-ASM_{ctx.Assembly.Name}_01");
        VendorPackageStorage.Stamp(sheet1, ctx.Assembly, VendorPackageStorage.RoleSheet1);

        if (ctx.View3D is { } view3d)
            Viewport.Create(doc, sheet1.Id, view3d.Id, view3dPosition);

        if (ctx.Schedule is { } schedule)
            ScheduleSheetInstance.Create(doc, sheet1.Id, schedule.Id, schedulePosition);
    }

    /// <summary>
    /// Refreshes an existing sheet 1 in place: the 3D viewport is swapped (or
    /// recreated, if Revit refuses the swap) to the freshly built 3D view,
    /// and the schedule is replaced -- both via the same shared helpers
    /// Korea's sheet 1 uses, so the two commands behave identically here.
    /// </summary>
    private static void UpdateFirstSheet(
        AssemblySheetContext ctx, ViewSheet sheet1, XYZ view3dPosition, XYZ schedulePosition)
    {
        var doc = ctx.Doc;

        if (ctx.View3D is { } view3d)
        {
            if (PackageSheetHelpers.TryGetSingleViewport(doc, sheet1) is { } viewport)
                PackageSheetHelpers.SwapOrRecreateViewport(doc, viewport, view3d);
            else
                Viewport.Create(doc, sheet1.Id, view3d.Id, view3dPosition);
        }

        if (ctx.Schedule is { } schedule)
            PackageSheetHelpers.ReplaceScheduleOnSheet(doc, sheet1, schedule, schedulePosition);
    }

    /// <summary>
    /// The package's sheets, keyed by role. Extensible Storage is
    /// authoritative; whichever role a package created before the stamp
    /// existed -- every Vendor package as of this feature shipping -- is
    /// missing gets located once by its exact V-ASM_&lt;name&gt;_01 / _02
    /// name and stamped on the spot, so the next run finds it by storage and
    /// renaming the sheet stops mattering.
    /// <para>
    /// Each role is backfilled independently rather than adopting by name
    /// only when NEITHER role is stamped: a package can legitimately end up
    /// with one role stamped and the other not (its sheet 2 never existed
    /// yet on an earlier run that only touched sheet 1, for one), and
    /// short-circuiting on "any role stamped" would leave that other role
    /// undiscoverable by name forever after, indistinguishable from a
    /// genuinely missing sheet.
    /// </para>
    /// <para>
    /// Simpler than KoreaSheetLayout's version in one respect: Vendor's
    /// sheet numbering has never changed, so there is no legacy-order table
    /// to fall back through.
    /// </para>
    /// </summary>
    private static Dictionary<string, ViewSheet> ResolvePackageSheets(
        Document doc, AssemblyInstance assembly)
    {
        var found = VendorPackageStorage.FindPackageSheets(doc, assembly);

        if (!found.ContainsKey(VendorPackageStorage.RoleSheet1))
            AdoptByName(doc, assembly, found, "_01", VendorPackageStorage.RoleSheet1);

        if (!found.ContainsKey(VendorPackageStorage.RoleSheet2))
            AdoptByName(doc, assembly, found, "_02", VendorPackageStorage.RoleSheet2);

        return found;
    }

    /// <summary>Legacy name lookup, used only to migrate an unstamped package.</summary>
    private static void AdoptByName(
        Document doc, AssemblyInstance assembly,
        Dictionary<string, ViewSheet> into, string sheetSuffix, string role)
    {
        var sheetName = $"V-ASM_{assembly.Name}{sheetSuffix}";
        if (ViewUtils.FindSheetByName(doc, sheetName) is not { } sheet) return;

        VendorPackageStorage.Stamp(sheet, assembly, role);
        into[role] = sheet;
    }

    private static IReadOnlyDictionary<string, (double X, double Y)>
        BuildFirstAngleClusterPositions(double spacing) =>
        new Dictionary<string, (double, double)>
        {
            ["FRONT"] = (0.0, 0.0),
            ["BACK"] = (spacing, 0.0),
            ["RIGHT"] = (-spacing, 0.0),
            ["LEFT"] = (spacing * 2, 0.0),
            ["TOP"] = (0.0, -spacing),
            ["BOTTOM"] = (0.0, spacing),
        };
}

/// <summary>
/// <summary>
/// Marks each sheet of a Korea drawing package with the assembly it documents
/// and the role it plays, so an update can find the package by what a sheet IS
/// rather than by what it is called. Users are then free to rename or renumber
/// sheets without breaking the next run.
/// <para>
/// Packages created before this existed carry no stamp, so the lookup falls back
/// to the old name pattern once and stamps whatever it finds -- after which that
/// package no longer depends on its names either.
/// </para>
/// </summary>
internal static class KoreaPackageStorage
{
    // Fixed for the life of the add-in: changing it orphans every stamped sheet.
    private static readonly Guid SchemaGuid = new("6D6A5C41-9F27-4B8E-A0D3-27C58E1B7A94");

    private const string SchemaName = "AVESKoreaDrawingPackage";
    // Must match <VendorId> in AVES.addin exactly: with Vendor-level write
    // access Revit compares the two and rejects every write when they differ.
    // The manifest is the source of truth, so this follows it.
    private const string VendorVendorId = "Development";

    private const string FieldAssemblyId = "AssemblyId";
    private const string FieldAssemblyUniqueId = "AssemblyUniqueId";
    private const string FieldPackageType = "PackageType";
    private const string FieldSheetRole = "SheetRole";

    public const string PackageTypeKorea = "Korea";

    /// <summary>Role of the 3D / part list sheet.</summary>
    public const string RoleFirstSheet = "Sheet1";

    /// <summary>Role of a per-view sheet: FRONT becomes Front, and so on.</summary>
    public static string RoleFor(string sheetOrderKey) =>
        sheetOrderKey.Length == 0
            ? sheetOrderKey
            : char.ToUpperInvariant(sheetOrderKey[0]) + sheetOrderKey[1..].ToLowerInvariant();

    /// <summary>
    /// The schema, created on first use. Revit keeps schemas per session, so
    /// Lookup succeeds on every later call in the same Revit run and on any
    /// document that already holds stamped sheets.
    /// </summary>
    private static Autodesk.Revit.DB.ExtensibleStorage.Schema GetOrCreateSchema()
    {
        if (Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid) is { } existing)
            return existing;

        using var builder = new Autodesk.Revit.DB.ExtensibleStorage.SchemaBuilder(SchemaGuid);

        builder.SetSchemaName(SchemaName);
        builder.SetVendorId(VendorVendorId);
        builder.SetReadAccessLevel(Autodesk.Revit.DB.ExtensibleStorage.AccessLevel.Public);
        builder.SetWriteAccessLevel(Autodesk.Revit.DB.ExtensibleStorage.AccessLevel.Vendor);

        builder.AddSimpleField(FieldAssemblyId, typeof(ElementId));
        builder.AddSimpleField(FieldAssemblyUniqueId, typeof(string));
        builder.AddSimpleField(FieldPackageType, typeof(string));
        builder.AddSimpleField(FieldSheetRole, typeof(string));

        return builder.Finish();
    }

    /// <summary>Stamps one sheet as part of this assembly's Korea package.</summary>
    public static void Stamp(ViewSheet sheet, AssemblyInstance assembly, string role)
    {
        var schema = GetOrCreateSchema();
        var entity = new Autodesk.Revit.DB.ExtensibleStorage.Entity(schema);

        entity.Set(FieldAssemblyId, assembly.Id);
        entity.Set(FieldAssemblyUniqueId, assembly.UniqueId);
        entity.Set(FieldPackageType, PackageTypeKorea);
        entity.Set(FieldSheetRole, role);

        sheet.SetEntity(entity);
    }

    /// <summary>
    /// Every stamped sheet of this assembly's Korea package, keyed by role.
    /// Empty when the package predates the stamp or does not exist.
    /// <para>
    /// TODO: this walks every sheet in the document once per assembly. A
    /// document-wide cache built once per run would make it O(sheets) instead
    /// of O(assemblies x sheets). Left as-is deliberately -- introduce it only
    /// if profiling on a large model shows it matters.
    /// </para>
    /// </summary>
    public static Dictionary<string, ViewSheet> FindPackageSheets(
        Document doc, AssemblyInstance assembly)
    {
        var found = new Dictionary<string, ViewSheet>(StringComparer.OrdinalIgnoreCase);

        if (Autodesk.Revit.DB.ExtensibleStorage.Schema.Lookup(SchemaGuid) is not { } schema)
            return found;

        var uniqueId = assembly.UniqueId;

        foreach (var sheet in new FilteredElementCollector(doc)
                     .OfClass(typeof(ViewSheet))
                     .Cast<ViewSheet>())
        {
            var entity = sheet.GetEntity(schema);
            if (!entity.IsValid()) continue;

            if (entity.Get<string>(FieldPackageType) != PackageTypeKorea) continue;
            if (entity.Get<string>(FieldAssemblyUniqueId) != uniqueId) continue;

            var role = entity.Get<string>(FieldSheetRole);
            if (!string.IsNullOrEmpty(role))
                found[role] = sheet;
        }

        return found;
    }
}

/// One view per sheet: 3D plus part list on the first sheet, then a sheet each
/// for Front, Back, Left, Right, Top and Bottom. Every 2D view is scaled so its
/// viewport fills roughly <see cref="TargetHeightFraction"/> of the drawable
/// height of its own sheet.
/// </summary>
internal sealed class KoreaSheetLayout(VendorDrawingsSettings settings) : IAssemblySheetLayout
{
    // Allowance for the title block's own borders and name block. The API can
    // measure the sheet but not the frame drawn inside it, so this is the
    // fraction of the sheet treated as drawable.
    public const double UsableSheetFraction = 0.90;

    // How much of that drawable height a placed 2D viewport should fill.
    public const double TargetHeightFraction = 0.85;

    // Order the per-view sheets are created and updated in, after the
    // 3D/schedule sheet. Position in this array also fixes each sheet's name
    // suffix: the first entry becomes _02, the last _07.
    public static readonly string[] SheetOrder =
        ["TOP", "BOTTOM", "FRONT", "BACK", "LEFT", "RIGHT"];

    // The order sheets were named under before SheetOrder was changed. Frozen
    // deliberately: it is used only to migrate a package that predates the
    // Extensible Storage stamp, whose _02.._07 names were assigned under this
    // sequence. Reading those names with the current SheetOrder would stamp the
    // old Front sheet as Top and swap every role.
    private static readonly string[] LegacySheetOrder =
        ["FRONT", "BACK", "LEFT", "RIGHT", "TOP", "BOTTOM"];

    // Sheet 1 is the 3D/schedule sheet, so the per-view sheets start at 2.
    private const int FirstDetailSheetIndex = 2;

    public void PlaceViews(AssemblySheetContext ctx)
    {
        var doc = ctx.Doc;
        var assemblyName = ctx.Assembly.Name;
        var sheetIndex = 1;

        // Same offsets the Vendor layout reads, off the same dialog. Vendor adds
        // them to the sheet origin; here they are added to the measured frame
        // centre, which is the same thing whenever the title block family is
        // centred on its sheet and stays on-sheet when it is not.
        var offsetX = settings.OffsetXFt();
        var offsetY = settings.OffsetYFt();

        // The 2D viewport offset is the same on every per-view sheet, so it is
        // resolved once here and folded into the shared placement below.
        var views2dX = settings.Views2DOffsetXFt();
        var views2dY = settings.Views2DOffsetYFt();

        // The sheets are the durable object; views and schedules on them are
        // disposable. If this assembly already has a Korea package, refresh that
        // one -- replacing only the categories Step 5 asked for, adding only what
        // is missing -- rather than building a second package beside it. Running
        // twice is therefore idempotent.
        var existingSheets = ResolvePackageSheets(doc, ctx.Assembly);
        if (existingSheets.Count > 0)
        {
            UpdateExistingPackage(ctx, existingSheets, offsetX, offsetY, views2dX, views2dY);
            return;
        }

        // Sheet 1 exists only for the 3D view and the schedule; if Step 5 skipped
        // both, it is not created. The per-view sheets below fall away on their
        // own, because DetailViews is empty when 2D Views was unchecked.
        if (ctx.View3D is not null || ctx.Schedule is not null)
        {
            var firstSheet = ViewSheet.Create(doc, ctx.TitleblockId);
            firstSheet.Name = ctx.SheetNames.Reserve($"K-ASM_{assemblyName}_{sheetIndex:00}");
            KoreaPackageStorage.Stamp(firstSheet, ctx.Assembly, KoreaPackageStorage.RoleFirstSheet);

            var firstFrame = SheetFrame.Measure(doc, firstSheet, UsableSheetFraction);

            if (ctx.View3D is { } view3d)
            {
                Scale3DView(ctx, view3d, firstFrame);

                Viewport.Create(doc, firstSheet.Id, view3d.Id,
                    View3DPoint(firstFrame, offsetX, offsetY));
            }

            if (ctx.Schedule is { } schedule)
            {
                ScheduleSheetInstance.Create(doc, firstSheet.Id, schedule.Id,
                    SchedulePoint(firstFrame, offsetX, offsetY));
            }
        }

        foreach (var key in SheetOrder)
        {
            if (!ctx.DetailViews.TryGetValue(key, out var view)) continue;

            sheetIndex++;
            PlaceOnNewSheet(ctx, $"K-ASM_{assemblyName}_{sheetIndex:00}",
                KoreaPackageStorage.RoleFor(key), view,
                offsetX, offsetY, views2dX, views2dY);
        }
    }

    /// <summary>
    /// The package's sheets, keyed by role. Extensible Storage is authoritative;
    /// a package created before the stamp existed is located once by the old name
    /// pattern and stamped on the spot, so the next run finds it by storage and
    /// renaming the sheets stops mattering.
    /// </summary>
    private static Dictionary<string, ViewSheet> ResolvePackageSheets(
        Document doc, AssemblyInstance assembly)
    {
        var stamped = KoreaPackageStorage.FindPackageSheets(doc, assembly);
        if (stamped.Count > 0) return stamped;

        var migrated = new Dictionary<string, ViewSheet>(StringComparer.OrdinalIgnoreCase);

        AdoptByName(doc, assembly, migrated, 1, KoreaPackageStorage.RoleFirstSheet);

        for (var i = 0; i < LegacySheetOrder.Length; i++)
        {
            AdoptByName(doc, assembly, migrated,
                i + FirstDetailSheetIndex,
                KoreaPackageStorage.RoleFor(LegacySheetOrder[i]));
        }

        return migrated;
    }

    /// <summary>Legacy name lookup, used only to migrate an unstamped package.</summary>
    private static void AdoptByName(
        Document doc, AssemblyInstance assembly,
        Dictionary<string, ViewSheet> into, int sheetIndex, string role)
    {
        var sheetName = $"K-ASM_{assembly.Name}_{sheetIndex:00}";
        if (ViewUtils.FindSheetByName(doc, sheetName) is not { } sheet) return;

        KoreaPackageStorage.Stamp(sheet, assembly, role);
        into[role] = sheet;
    }

    /// <summary>
    /// Scale rule for the 3D view: the left share of the drawable width, less
    /// whatever the dialog offset pushes it out by.
    /// </summary>
    private void Scale3DView(AssemblySheetContext ctx, View3D view3d, SheetFrame frame)
    {
        var availW = frame.UsableWidth * settings.LeftPercent
                     - Math.Abs(settings.View3DOffsetXFt());
        var availH = frame.UsableHeight
                     - Math.Abs(settings.View3DOffsetYFt());

        view3d.Scale = GeometryUtils.PickScale(
            ctx.ModelWidth, ctx.ModelHeight, availW, availH);
    }

    private XYZ View3DPoint(SheetFrame frame, double offsetX, double offsetY) =>
        new(frame.Center.X + settings.View3DOffsetXFt() + offsetX,
            frame.Center.Y + settings.View3DOffsetYFt() + offsetY,
            0);

    private XYZ SchedulePoint(SheetFrame frame, double offsetX, double offsetY) =>
        new(frame.Center.X + settings.ScheduleOffsetXFt() + offsetX,
            frame.Center.Y + settings.ScheduleOffsetYFt() + offsetY,
            0);

    /// <summary>
    /// Refreshes an existing package in place. Each category is handled only when
    /// Step 5 asked for it -- the builder leaves View3D, Schedule and DetailViews
    /// empty for anything unchecked -- so an unrequested category is neither
    /// replaced nor deleted.
    /// </summary>
    private void UpdateExistingPackage(
        AssemblySheetContext ctx, Dictionary<string, ViewSheet> existingSheets,
        double offsetX, double offsetY, double views2dX, double views2dY)
    {
        if (ctx.View3D is not null || ctx.Schedule is not null)
            UpdateFirstSheet(ctx, existingSheets, offsetX, offsetY);

        if (ctx.DetailViews.Count > 0)
            UpdateExisting2DPackage(ctx, existingSheets, offsetX, offsetY, views2dX, views2dY);
    }

    /// <summary>
    /// Refreshes sheet 1 -- the 3D view and the part list. Creates the sheet only
    /// when the package is missing it, so a partial package is repaired rather
    /// than duplicated.
    /// </summary>
    private void UpdateFirstSheet(
        AssemblySheetContext ctx, Dictionary<string, ViewSheet> existingSheets,
        double offsetX, double offsetY)
    {
        var doc = ctx.Doc;

        if (!existingSheets.TryGetValue(KoreaPackageStorage.RoleFirstSheet, out var sheet))
        {
            sheet = ViewSheet.Create(doc, ctx.TitleblockId);
            sheet.Name = ctx.SheetNames.Reserve($"K-ASM_{ctx.Assembly.Name}_01");
            KoreaPackageStorage.Stamp(sheet, ctx.Assembly, KoreaPackageStorage.RoleFirstSheet);
        }

        var frame = SheetFrame.Measure(doc, sheet, UsableSheetFraction);

        if (ctx.View3D is { } view3d)
        {
            Scale3DView(ctx, view3d, frame);

            // Sheet 1 carries at most one Viewport -- the schedule sits in a
            // ScheduleSheetInstance, which is not a Viewport -- so the same swap
            // the 2D sheets use applies here unchanged.
            if (PackageSheetHelpers.TryGetSingleViewport(doc, sheet) is { } viewport)
                PackageSheetHelpers.SwapOrRecreateViewport(doc, viewport, view3d);
            else
                Viewport.Create(doc, sheet.Id, view3d.Id, View3DPoint(frame, offsetX, offsetY));
        }

        if (ctx.Schedule is { } schedule)
            PackageSheetHelpers.ReplaceScheduleOnSheet(
                doc, sheet, schedule, SchedulePoint(frame, offsetX, offsetY));
    }

    /// <summary>
    /// Refreshes the 2D half of the package: every sheet that is there has its
    /// view replaced, and every sheet that is missing is created, so the package
    /// comes out complete either way.
    /// <para>
    /// Sheets are found by name; the view on each is found by walking that
    /// sheet's own viewport rather than by view name, because view names are not
    /// unique across the Vendor, Korea and MT commands.
    /// </para>
    /// </summary>
    private void UpdateExisting2DPackage(
        AssemblySheetContext ctx, Dictionary<string, ViewSheet> existingSheets,
        double offsetX, double offsetY, double views2dX, double views2dY)
    {
        var doc = ctx.Doc;
        var assemblyName = ctx.Assembly.Name;

        for (var i = 0; i < SheetOrder.Length; i++)
        {
            var key = SheetOrder[i];
            if (!ctx.DetailViews.TryGetValue(key, out var replacement)) continue;

            var role = KoreaPackageStorage.RoleFor(key);
            var sheetName = $"K-ASM_{assemblyName}_{i + FirstDetailSheetIndex:00}";
            existingSheets.TryGetValue(role, out var sheet);

            if (sheet is null)
            {
                // Missing from the package -- add just this sheet.
                PlaceOnNewSheet(ctx, sheetName, role, replacement,
                    offsetX, offsetY, views2dX, views2dY);
                continue;
            }

            if (PackageSheetHelpers.TryGetSingleViewport(doc, sheet) is { } viewport)
            {
                ScaleToSheet(doc, sheet, replacement);
                PackageSheetHelpers.SwapOrRecreateViewport(doc, viewport, replacement);
            }
            else
            {
                // The sheet is there but carries no viewport we can safely swap,
                // so place the replacement on it rather than leave it unplaced.
                PlaceOnExistingSheet(doc, sheet, replacement,
                    offsetX, offsetY, views2dX, views2dY);
            }
        }
    }

    /// <summary>Creates one per-view sheet and places the view on it.</summary>
    private void PlaceOnNewSheet(
        AssemblySheetContext ctx, string sheetName, string role, View view,
        double offsetX, double offsetY, double views2dX, double views2dY)
    {
        var doc = ctx.Doc;

        var sheet = ViewSheet.Create(doc, ctx.TitleblockId);
        sheet.Name = ctx.SheetNames.Reserve(sheetName);
        KoreaPackageStorage.Stamp(sheet, ctx.Assembly, role);

        PlaceOnExistingSheet(doc, sheet, view, offsetX, offsetY, views2dX, views2dY);
    }

    /// <summary>Scales the view to the sheet and drops a viewport at its centre.</summary>
    private static void PlaceOnExistingSheet(
        Document doc, ViewSheet sheet, View view,
        double offsetX, double offsetY, double views2dX, double views2dY)
    {
        var frame = ScaleToSheet(doc, sheet, view);

        Viewport.Create(doc, sheet.Id, view.Id,
            new XYZ(frame.Center.X + offsetX + views2dX,
                    frame.Center.Y + offsetY + views2dY, 0));
    }

    /// <summary>
    /// Scales the view off its own crop -- what the viewport actually draws,
    /// which differs per view -- against the sheet drawable area.
    /// </summary>
    private static SheetFrame ScaleToSheet(Document doc, ViewSheet sheet, View view)
    {
        var frame = SheetFrame.Measure(doc, sheet, UsableSheetFraction);

        var crop = view.CropBox;
        var viewWidth = Math.Abs(crop.Max.X - crop.Min.X);
        var viewHeight = Math.Abs(crop.Max.Y - crop.Min.Y);

        view.Scale = GeometryUtils.PickScale(
            viewWidth, viewHeight,
            frame.UsableWidth,
            frame.UsableHeight * TargetHeightFraction);

        return frame;
    }
}


// =============================================================================
// 8. BUTTON 1 — Cmd_DOC03c_Vendor (A3, two sheets per assembly)
// =============================================================================

[Transaction(TransactionMode.Manual)]
public sealed class Cmd_DOC03c_Vendor : IExternalCommand
{
    private static readonly VendorLayoutDescriptor Descriptor =
        VendorLayoutDescriptor.Vendor;

    private static readonly AssemblyDrawingProfile Profile = new(
        "Vendor Automation",
        "Vendor Sheets - Batch",
        "Vendor Drawing Automation",
        uiApp => Descriptor.CreateLayoutFrom(uiApp, SettingsGate.VendorOffsetsFile),
        TemplateSettingsFile: SettingsGate.VendorOffsetsFile,
        PackageType: "Vendor");

    public Result Execute(ExternalCommandData commandData, ref string message,
        ElementSet elements)
    {
        var result = AssemblyDrawingRunner.Run(
            commandData.Application, Profile, out var failure);
        if (failure.Length > 0) message = failure;
        return result;
    }

    // Kept as forwarders: Cmd_DOC03b_MT calls these by name.
    internal static List<AssemblyInstance> CollectAssemblies(Document doc) =>
        AssemblyDrawingRunner.CollectAssemblies(doc);

    internal static NameRegistry BuildViewNameRegistry(Document doc) =>
        AssemblyDrawingRunner.BuildViewNameRegistry(doc);

    internal static NameRegistry BuildSheetNameRegistry(Document doc) =>
        AssemblyDrawingRunner.BuildSheetNameRegistry(doc);
}


// =============================================================================
// 9. BUTTON 4 — Cmd_DOC03d_Korea (one view per sheet)
// =============================================================================

[Transaction(TransactionMode.Manual)]
public sealed class Cmd_DOC03d_Korea : IExternalCommand
{
    private static readonly VendorLayoutDescriptor Descriptor =
        VendorLayoutDescriptor.Korea;

    private static readonly AssemblyDrawingProfile Profile = new(
        "Korea Automation",
        "Korea Sheets - Batch",
        "Korea Drawing Automation",
        uiApp => Descriptor.CreateLayoutFrom(uiApp, SettingsGate.KoreaOffsetsFile),
        TemplateSettingsFile: SettingsGate.KoreaOffsetsFile,
        PackageType: "Korea");

    public Result Execute(ExternalCommandData commandData, ref string message,
        ElementSet elements)
    {
        var result = AssemblyDrawingRunner.Run(
            commandData.Application, Profile, out var failure);
        if (failure.Length > 0) message = failure;
        return result;
    }
}


// =============================================================================
// 10. BUTTON 2 — Cmd_DOC03a_Assembly (bolt clustering, one sheet per assembly)
// =============================================================================

[Transaction(TransactionMode.Manual)]
public sealed class Cmd_DOC03a_Assembly : IExternalCommand
{
    private sealed class AssemblyContext(AssemblyInstance assembly)
    {
        public AssemblyInstance Assembly { get; } = assembly;
        public ElementId Id { get; } = assembly.Id;
        public string Name { get; } = assembly.Name;
        public IReadOnlyList<ElementId> MemberIds { get; set; } = [];
        public BoundingBoxXYZ? BoundingBox { get; set; }
        public IReadOnlyList<Element> BoltElements { get; set; } = [];
        public IReadOnlyList<BoltClusterType> BoltClusterTypes { get; set; } = [];
        public View? Template { get; set; }
    }

    private sealed record BoltInfo(string FamilyName, XYZ Point, XYZ Direction);

    private sealed class BoltClusterType(string familyName, string directionKey)
    {
        public string FamilyName { get; } = familyName;
        public string DirectionKey { get; } = directionKey;
        public List<BoltInfo> Bolts { get; } = [];
        public BoundingBoxXYZ? BoundingBox { get; private set; }

        public void Add(BoltInfo bolt) => Bolts.Add(bolt);

        public void ComputeBounds()
        {
            if (Bolts.Count == 0) return;
            var pts = Bolts.Select(b => b.Point).ToList();
            BoundingBox = new BoundingBoxXYZ
            {
                Min = new XYZ(pts.Min(p => p.X), pts.Min(p => p.Y), pts.Min(p => p.Z)),
                Max = new XYZ(pts.Max(p => p.X), pts.Max(p => p.Y), pts.Max(p => p.Z)),
            };
        }
    }

    public Result Execute(ExternalCommandData commandData, ref string message,
        ElementSet elements)
    {
        var uiApp = commandData.Application;
        var doc = uiApp.ActiveUIDocument.Document;

        try
        {
            // Shared collector, so this command processes assemblies in the same
            // alphabetical order as Vendor, Korea and MT rather than keeping its
            // own copy of the query and the sort.
            var assemblies = AssemblyDrawingRunner.CollectAssemblies(doc);

            if (assemblies.Count == 0)
            {
                AvControls.ShowAlert("Assembly Bolt Automation",
                    "No assemblies found in the model.", AlertSeverity.Warning);
                return Result.Failed;
            }

            var contexts = AnalyseAssemblies(doc, assemblies);
            ClusterBolts(contexts);

            var viewFamilyType3d = ViewUtils.Get3DViewFamilyType(doc);
            if (viewFamilyType3d is null)
            {
                AvControls.ShowAlert("Assembly Bolt Automation",
                    "No 3D ViewFamilyType found in this project.", AlertSeverity.Warning);
                return Result.Failed;
            }

            // This command has no setup window to pick a title block in, so it
            // resolves the one stored in its own settings, falling back to the
            // first available if that type has since left the project.
            var titleblocks = TitleblockUtils.CollectSymbols(doc);
            var savedAsm = SettingsStore
                .LoadOrDefault<AssemblyDrawingsSettings>("assembly-drawings.json");

            if (TitleblockUtils.Resolve(titleblocks, savedAsm.TitleBlockName, out var fellBack)
                is not { } titleblock)
            {
                AvControls.ShowAlert("Assembly Bolt Automation",
                    "No title block found in this project.", AlertSeverity.Warning);
                return Result.Failed;
            }

            if (fellBack)
            {
                AvControls.ShowAlert("Assembly Bolt Automation",
                    $"The saved title block \"{savedAsm.TitleBlockName}\" is no longer " +
                    "in this project.\n\n" +
                    $"\"{TitleblockUtils.DisplayName(titleblock)}\" has been used instead.",
                    AlertSeverity.Warning);
            }

            savedAsm.TitleBlockName = TitleblockUtils.DisplayName(titleblock);
            SettingsStore.Save("assembly-drawings.json", savedAsm);

            var missingTemplates = new List<string>();
            foreach (var ctx in contexts)
            {
                ctx.Template = ViewUtils.FindTemplateByPrefix(doc, $"Assembly {ctx.Name}");
                if (ctx.Template is null) missingTemplates.Add(ctx.Name);
            }

            if (missingTemplates.Count > 0)
            {
                AvControls.ShowAlert("Assembly Bolt Automation",
                    "Missing view templates for these assemblies " +
                    "(create one named \"Assembly {name}...\" each):\n\n  • " +
                    string.Join("\n  • ", missingTemplates), AlertSeverity.Warning);
                return Result.Failed;
            }

            var settings = SettingsGate.ShowAssemblyDrawings(uiApp);
            if (settings is null) return Result.Cancelled;

            var results = new List<(string Name, string? Error)>();

            using var tx = new Transaction(doc, "Assembly Bolt Drawings - Batch");
            tx.Start();
            FailureHandling.SuppressWarnings(tx);

            foreach (var ctx in contexts)
            {
                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    CreateViews(doc, ctx, viewFamilyType3d, settings);
                    CreateSheetAndPlaceViews(doc, ctx, titleblock.Id, settings);
                    sub.Commit();
                    results.Add((ctx.Name, null));
                }
                catch (Exception ex)
                {
                    sub.RollBack();
                    results.Add((ctx.Name, ex.Message));
                }
            }

            tx.Commit();

            Reporting.ShowResult("Assembly Bolt Automation", results,
                uiApp.MainWindowHandle);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    private static List<AssemblyContext> AnalyseAssemblies(
        Document doc, List<AssemblyInstance> assemblies)
    {
        var contexts = new List<AssemblyContext>(assemblies.Count);

        foreach (var asm in assemblies)
        {
            var ctx = new AssemblyContext(asm)
            {
                MemberIds = asm.GetMemberIds().ToList(),
            };

            var members = ctx.MemberIds
                .Select(doc.GetElement)
                .Where(el => el is not null)
                .ToList()!;

            ctx.BoundingBox = GeometryUtils.GetCombinedBoundingBox(members);
            ctx.BoltElements = members.Where(IsBoltCandidate).ToList();
            contexts.Add(ctx);
        }

        return contexts;
    }

    private static bool IsBoltCandidate(Element element)
    {
        if (element is not FamilyInstance { Category: { } cat }) return false;
        var catId = cat.Id.Value;
        return catId == (long)BuiltInCategory.OST_StructConnections
            || catId == (long)BuiltInCategory.OST_GenericModel;
    }

    private static void ClusterBolts(List<AssemblyContext> contexts)
    {
        foreach (var ctx in contexts)
        {
            var infos = ctx.BoltElements
                .Select(TryBuildBoltInfo)
                .Where(b => b is not null)
                .Cast<BoltInfo>()
                .ToList();

            ctx.BoltClusterTypes = Cluster(infos);
        }
    }

    private static BoltInfo? TryBuildBoltInfo(Element element)
    {
        if (element.Location is not LocationPoint { Point: { } point }) return null;
        if (element is not FamilyInstance fi) return null;

        XYZ? direction = null;
        var facing = fi.FacingOrientation;
        if (facing is not null && facing.GetLength() > 1e-9)
            direction = facing.Normalize();
        else
        {
            var hand = fi.HandOrientation;
            if (hand is not null && hand.GetLength() > 1e-9)
                direction = hand.Normalize();
        }
        if (direction is null) return null;

        var familyName = fi.Symbol?.Family?.Name ?? element.Name;
        return new BoltInfo(familyName, point, direction);
    }

    private static string DirKey(XYZ v, int precision = 2) =>
        $"{Math.Round(v.X, precision)}_{Math.Round(v.Y, precision)}_{Math.Round(v.Z, precision)}";

    private static List<BoltClusterType> Cluster(IEnumerable<BoltInfo> bolts)
    {
        var buckets = new Dictionary<(string FamilyName, string DirKey), BoltClusterType>();

        foreach (var bolt in bolts)
        {
            var key = (bolt.FamilyName, DirKey(bolt.Direction));
            if (!buckets.TryGetValue(key, out var ct))
            {
                ct = new BoltClusterType(key.FamilyName, key.Item2);
                buckets[key] = ct;
            }
            ct.Add(bolt);
        }

        foreach (var ct in buckets.Values) ct.ComputeBounds();
        return [.. buckets.Values];
    }

    private static void CreateViews(
        Document doc,
        AssemblyContext ctx,
        ViewFamilyType vft3d,
        AssemblyDrawingsSettings settings)
    {
        var padding = settings.SectionBoxPaddingFt();
        var asmName = ctx.Name.Replace(" ", "_");
        var template = ctx.Template!;

        var asmViewName = $"ASM_{asmName}_3D";
        // Reuse an existing view where one is already there, but always refit
        // its section box -- the assembly may have moved or changed since.
        if (ViewUtils.Find3DViewByName(doc, asmViewName) is { } existing)
        {
            ViewUtils.ApplySectionBox(existing, ctx.BoundingBox, padding);
        }
        else
        {
            ViewUtils.CreateOrthographic3D(
                doc, vft3d, asmViewName, template.Id, ctx.BoundingBox, padding);
        }

        foreach (var ct in ctx.BoltClusterTypes)
        {
            var safeName = ct.FamilyName.Replace(" ", "_");
            var boltViewName = $"ASM_{asmName}_BOLT_{safeName}_{ct.DirectionKey}";
            if (ViewUtils.Find3DViewByName(doc, boltViewName) is not null) continue;

            ViewUtils.CreateOrthographic3D(
                doc, vft3d, boltViewName, template.Id, ct.BoundingBox, padding);
        }
    }

    private static void CreateSheetAndPlaceViews(
        Document doc,
        AssemblyContext ctx,
        ElementId titleblockId,
        AssemblyDrawingsSettings settings)
    {
        var asmName = ctx.Name.Replace(" ", "_");
        var asmSheetName = $"A-ASM_{asmName}";

        var sheet = ViewUtils.FindSheetByName(doc, asmSheetName)
                    ?? ViewSheet.Create(doc, titleblockId);
        sheet.Name = asmSheetName;

        var asmPosition = new XYZ(settings.AsmViewXFt(), settings.AsmViewYFt(), 0);
        var asmView = ViewUtils.Find3DViewByName(doc, $"ASM_{asmName}_3D");
        if (asmView is not null)
            ViewUtils.PlaceView(doc, sheet, asmView, asmPosition);

        var boltX = settings.BoltViewXFt();
        var y = settings.BoltViewStartYFt();
        var boltStepY = settings.BoltViewStepYFt();

        foreach (var ct in ctx.BoltClusterTypes)
        {
            var safeName = ct.FamilyName.Replace(" ", "_");
            var name = $"ASM_{asmName}_BOLT_{safeName}_{ct.DirectionKey}";
            if (ViewUtils.Find3DViewByName(doc, name) is { } bv)
            {
                ViewUtils.PlaceView(doc, sheet, bv, new XYZ(boltX, y, 0));
                y += boltStepY;
            }
        }
    }
}


// =============================================================================
// 11. BUTTON 3 — Cmd_DOC03b_MT (A1, single sheet per assembly)
// =============================================================================

[Transaction(TransactionMode.Manual)]
public sealed class Cmd_DOC03b_MT : IExternalCommand
{
    private const double ProjectionSpacingFactor = 0.4;

    private static readonly double LeftHalfWidth = RevitConstants.A1WidthFt * 0.50;
    private static readonly double RightHalfWidth = RevitConstants.A1WidthFt * 0.50;
    private static readonly double Left2DZoneWidth = LeftHalfWidth * 0.30;
    private static readonly double Left3DZoneWidth = LeftHalfWidth * 0.20;

    private static readonly IReadOnlyDictionary<string, string> ViewTitles =
        new Dictionary<string, string>
        {
            ["3D"] = "3D View",
            ["TOP"] = "Top View",
            ["FRONT"] = "Front View",
            ["LEFT"] = "Left View",
            ["RIGHT"] = "Right View",
        };

    private static readonly IReadOnlyDictionary<string, AssemblyDetailViewOrientation> Orientations =
        new Dictionary<string, AssemblyDetailViewOrientation>
        {
            ["FRONT"] = AssemblyDetailViewOrientation.ElevationFront,
            ["LEFT"] = AssemblyDetailViewOrientation.ElevationLeft,
            ["RIGHT"] = AssemblyDetailViewOrientation.ElevationRight,
            ["TOP"] = AssemblyDetailViewOrientation.ElevationTop,
        };

    public Result Execute(ExternalCommandData commandData, ref string message,
        ElementSet elements)
    {
        var uiApp = commandData.Application;
        var doc = uiApp.ActiveUIDocument.Document;

        try
        {
            var templates = ViewUtils.GetTemplates(doc);
            if (templates.Count == 0)
            {
                AvControls.ShowAlert("Vendor Automation (Single Sheet)",
                    "No view templates found in this project.", AlertSeverity.Warning);
                return Result.Failed;
            }

            // Loaded up front so the setup window can preselect the title block
            // this command was last run with.
            var savedMt = SettingsStore
                .LoadOrDefault<MTDrawingsSettings>("mtq-drawings.json");

            // Collected before the setup window so its assembly column can offer
            // them, already alphabetical from the shared collector.
            var assemblies = Cmd_DOC03c_Vendor.CollectAssemblies(doc);
            if (assemblies.Count == 0)
            {
                AvControls.ShowAlert("Vendor Automation (Single Sheet)",
                    "No assemblies found in this project.", AlertSeverity.Warning);
                return Result.Failed;
            }

            var setup = TemplatePicker.PickSetup(
                templates, doc, savedMt.TitleBlockName,
                savedMt.View3DTemplateName, savedMt.View2DTemplateName,
                savedMt.ScheduleTemplateName, assemblies,
                "Vendor Automation (Single Sheet)", uiApp.MainWindowHandle);
            if (setup is null || !setup.IsComplete) return Result.Cancelled;

            savedMt.TitleBlockName = TitleblockUtils.DisplayName(setup.Titleblock!);
            savedMt.View3DTemplateName = setup.V3d?.Name;
            savedMt.View2DTemplateName = setup.V2d?.Name;
            savedMt.ScheduleTemplateName = setup.Sched?.Name;
            SettingsStore.Save("mtq-drawings.json", savedMt);

            var settings = SettingsGate.ShowMTDrawings(uiApp);
            if (settings is null) return Result.Cancelled;

            var viewNames = Cmd_DOC03c_Vendor.BuildViewNameRegistry(doc);
            var sheetNames = Cmd_DOC03c_Vendor.BuildSheetNameRegistry(doc);

            var results = new List<(string Name, string? Error)>();

            using var tx = new Transaction(doc, "Vendor Sheet (MT) - Batch");
            tx.Start();
            FailureHandling.SuppressWarnings(tx);

            foreach (var assembly in setup.SelectedAssemblies)
            {
                using var sub = new SubTransaction(doc);
                sub.Start();
                try
                {
                    ProcessAssembly(doc, assembly, setup,
                        setup.Titleblock!.Id, settings, viewNames, sheetNames);
                    sub.Commit();
                    results.Add((assembly.Name, null));
                }
                catch (Exception ex)
                {
                    sub.RollBack();
                    results.Add((assembly.Name, ex.Message));
                }
            }

            tx.Commit();

            Reporting.ShowResult("MT Drawing Automation", results,
                uiApp.MainWindowHandle);
            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    private static void ProcessAssembly(
        Document doc,
        AssemblyInstance assembly,
        DrawingSetup setup,
        ElementId titleblockId,
        MTDrawingsSettings settings,
        NameRegistry viewNames, NameRegistry sheetNames)
    {
        var bbox = GeometryUtils.GetAssemblyBoundingBox(doc, assembly)
            ?? throw new InvalidOperationException("No bounding box for assembly.");

        var (sizeX, sizeY, sizeZ) = GeometryUtils.GetSize(bbox);
        var modelW = Math.Max(sizeX, sizeY);
        var modelH = sizeZ;

        var offsetX = settings.OffsetXFt();

        var offsetY = settings.OffsetYFt();
        // Each category is gated by its Step 5 flag; a skipped one creates
        // nothing and leaves what is already in the model alone.
        View3D? view3d = null;
        if (setup.Generate3DViews)
        {
            view3d = AssemblyViewUtils.Create3DOrthographic(doc, assembly.Id);
            view3d.Name = viewNames.Reserve($"{assembly.Name} - 3D");
            view3d.ViewTemplateId = setup.V3d!.Id;
        }

        ViewSchedule? schedule = null;
        if (setup.GenerateSchedules)
        {
            schedule = AssemblyViewUtils.CreatePartList(doc, assembly.Id);
            schedule.Name = viewNames.Reserve($"{assembly.Name} - Part List");
            schedule.ViewTemplateId = setup.Sched!.Id;
        }

        var detailViews = setup.Generate2DViews
            ? ViewUtils.CreateDetailViews(
                doc, assembly.Id, setup.V2d!.Id, assembly.Name, viewNames, Orientations)
            : new Dictionary<string, View>();

        var sheet = ViewSheet.Create(doc, titleblockId);
        sheet.Name = sheetNames.Reserve($"MT-{assembly.Name}");

        var scale2d = GeometryUtils.PickScale(
            modelW * 3.0, modelH * 2.0,
            Left2DZoneWidth, RevitConstants.A1HeightFt);
        var scale3d = GeometryUtils.PickScale(
            modelW, modelH,
            Left3DZoneWidth, RevitConstants.A1HeightFt);

        if (view3d is not null)
        {
            view3d.Scale = scale3d;
            ViewUtils.SetDescription(view3d, ViewTitles["3D"]);
        }

        foreach (var v in detailViews.Values) v.Scale = scale2d;

        foreach (var (key, view) in detailViews)
        {
            if (ViewTitles.TryGetValue(key, out var title))
                ViewUtils.SetDescription(view, title);
        }

        var leftHalfCenterX = -(RevitConstants.A1WidthFt / 4.0) + offsetX;
        var quirkX = settings.ApplyOffsetXTwice ? offsetX : 0.0;
        var left2dZoneCenterX = leftHalfCenterX - (LeftHalfWidth * 0.25) + quirkX;
        var left3dZoneCenterX = leftHalfCenterX + (LeftHalfWidth * 0.05) + quirkX;
        var rightHalfCenterX = (RevitConstants.A1WidthFt / 4.0) + offsetX;

        var defaultSpacing = Left2DZoneWidth * ProjectionSpacingFactor;
        var bundleX = settings.BundleSpacingXFt();
        var bundleY = settings.BundleSpacingYFt();
        var spacingX = bundleX != 0.0 ? bundleX : defaultSpacing;
        var spacingY = bundleY != 0.0 ? bundleY : defaultSpacing;

        var clusterCenterX = left2dZoneCenterX + settings.Views2DOffsetXFt();
        var clusterCenterY = offsetY + settings.Views2DOffsetYFt();

        foreach (var (key, pos) in BuildFirstAngleProjectionPositions(
                     clusterCenterX, clusterCenterY, spacingX, spacingY))
        {
            if (detailViews.TryGetValue(key, out var view))
                Viewport.Create(doc, sheet.Id, view.Id, pos);
        }

        if (view3d is not null)
        {
            Viewport.Create(doc, sheet.Id, view3d.Id,
                new XYZ(left3dZoneCenterX + settings.View3DOffsetXFt(),
                        offsetY + settings.View3DOffsetYFt(),
                        0));
        }

        if (schedule is not null)
        {
            var scheduleBaseX = rightHalfCenterX + (RightHalfWidth * -0.10) + offsetX;
            var scheduleBaseY = (RevitConstants.A1HeightFt * 0.40) + offsetY;
            ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id,
                new XYZ(scheduleBaseX + settings.PartListOffsetXFt(),
                        scheduleBaseY + settings.PartListOffsetYFt(),
                        0));
        }
    }

    private static IReadOnlyDictionary<string, XYZ> BuildFirstAngleProjectionPositions(
        double centerX, double centerY, double spacingX, double spacingY) =>
        new Dictionary<string, XYZ>
        {
            ["FRONT"] = new(centerX, centerY, 0),
            ["RIGHT"] = new(centerX - spacingX, centerY, 0),
            ["LEFT"] = new(centerX + spacingX, centerY, 0),
            ["TOP"] = new(centerX, centerY - spacingY, 0),
        };
}