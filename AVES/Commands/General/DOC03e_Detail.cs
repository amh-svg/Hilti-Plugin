// -----------------------------------------------------------------------
// Title: Detail Views
// Summary: Builds 3D and orthographic detail views of a SELECTION of model
//          elements (not whole assemblies) and puts them on new sheets, or
//          stages them below an existing sheet.
// Key logic: The user builds one or more "sets" of elements, each with its own
//            view ticks. DetailPlanner decides sheets, scales and positions;
//            the setup window previews that same plan, and this file creates
//            the views and viewports from it. Every view shows only its set.
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using AVES.Core;

namespace AVES.Commands;

// =============================================================================
// 1. MODEL
// =============================================================================

/// <summary>One group of selected elements and the views wanted of it.</summary>
internal sealed class DetailSet
{
    public string Name { get; set; } = "Detail A";

    /// <summary>The ticked views, as DetailKeys.Order keys.</summary>
    public HashSet<string> Views { get; } = ["3D", "TOP", "FRONT", "RIGHT"];

    /// <summary>The model elements the views are framed around.</summary>
    public List<ElementId> ElementIds { get; } = [];

    /// <summary>Everything that stays visible in the set's views: the elements,
    /// the assemblies and groups they were picked through, and the nested
    /// components of any family instance.</summary>
    public HashSet<ElementId> KeepIds { get; } = [];

    public BoundingBoxXYZ? BoundingBox { get; private set; }

    /// <summary>The assembly every element belongs to, when there is exactly
    /// one; its axes then orient the 2D views.</summary>
    public AssemblyInstance? ParentAssembly { get; private set; }

    public Transform Frame { get; private set; } = Transform.Identity;

    /// <summary>Picked elements that could not be used (no geometry, not a
    /// model element, linked).</summary>
    public int SkippedCount { get; private set; }

    public string OrientationLabel =>
        ParentAssembly is null ? "world axes" : $"axes of assembly {ParentAssembly.Name}";

    public void SetElements(Document doc, IEnumerable<ElementId> picked)
    {
        ElementIds.Clear();
        KeepIds.Clear();
        BoundingBox = null;
        ParentAssembly = null;
        Frame = Transform.Identity;
        SkippedCount = 0;

        var pending = new Queue<Element>();
        foreach (var id in picked)
            if (doc.GetElement(id) is { } start) pending.Enqueue(start);

        var seen = new HashSet<ElementId>();
        var parents = new HashSet<ElementId>();
        var usable = new List<Element>();

        while (pending.Count > 0)
        {
            var e = pending.Dequeue();
            if (!seen.Add(e.Id)) continue;

            // Containers are expanded to their members; the container itself
            // only needs to stay visible.
            if (e is AssemblyInstance assembly)
            {
                KeepIds.Add(assembly.Id);
                foreach (var m in assembly.GetMemberIds())
                    if (doc.GetElement(m) is { } member) pending.Enqueue(member);
                continue;
            }
            if (e is Group group)
            {
                KeepIds.Add(group.Id);
                foreach (var m in group.GetMemberIds())
                    if (doc.GetElement(m) is { } member) pending.Enqueue(member);
                continue;
            }

            if (!IsUsable(e))
            {
                SkippedCount++;
                continue;
            }

            ElementIds.Add(e.Id);
            KeepIds.Add(e.Id);
            usable.Add(e);
            parents.Add(e.AssemblyInstanceId);

            // Nested shared components are separate elements in a view and would
            // be hidden along with everything else unless they are kept.
            if (e is FamilyInstance instance)
            {
                foreach (var sub in instance.GetSubComponentIds()) KeepIds.Add(sub);
            }
        }

        BoundingBox = GeometryUtils.GetCombinedBoundingBox(usable);

        if (parents.Count == 1 && parents.First() != ElementId.InvalidElementId
            && doc.GetElement(parents.First()) is AssemblyInstance parent)
        {
            ParentAssembly = parent;
            Frame = parent.GetTransform();
        }
    }

    internal static bool IsUsable(Element e)
    {
        if (e is ElementType or RevitLinkInstance) return false;
        if (e.ViewSpecific) return false;
        if (e.Category is not { CategoryType: CategoryType.Model }) return false;

        try { return e.get_BoundingBox(null) is not null; }
        catch { return false; }
    }
}

/// <summary>Everything the setup window collects.</summary>
internal sealed class DetailRequest
{
    public List<DetailSet> Sets { get; } = [];
    public DetailTargetMode Mode { get; set; } = DetailTargetMode.NewShared;
    public ViewSheet? ExistingSheet { get; set; }
    public FamilySymbol? Titleblock { get; set; }
    public View? V3d { get; set; }
    public View? V2d { get; set; }
    public string SheetName { get; set; } = "";

    /// <summary>True once the user has typed their own sheet name, so the
    /// suggestion stops overwriting it.</summary>
    public bool SheetNameEdited { get; set; }

    public string SheetNumber { get; set; } = "";
    public double GapMm { get; set; } = 50.0;

    public double GapFt => UnitUtils.ConvertToInternalUnits(GapMm, UnitTypeId.Millimeters);

    /// <summary>The sets that will actually produce views, in order. Their
    /// position in this list is the SetIndex the planner uses.</summary>
    public List<DetailSet> PlannableSets() =>
        Sets.Where(s => s.BoundingBox is not null && s.Views.Count > 0).ToList();

    /// <summary>Why the request cannot run yet, or null when it can.</summary>
    public string? Problem()
    {
        var sets = PlannableSets();
        if (sets.Count == 0) return "Add a set with elements and at least one view ticked.";
        if (sets.Any(s => string.IsNullOrWhiteSpace(s.Name))) return "Give every set a name.";

        if (sets.Any(s => s.Views.Contains("3D")) && V3d is null) return "Pick a 3D view template.";
        if (sets.Any(s => s.Views.Any(k => k != "3D")) && V2d is null) return "Pick a 2D view template.";

        if (Mode == DetailTargetMode.ExistingSheet)
            return ExistingSheet is null ? "Pick the existing sheet." : null;

        if (Titleblock is null) return "Pick a title block.";
        if (string.IsNullOrWhiteSpace(SheetNumber)) return "Enter a sheet number.";
        return null;
    }
}

internal sealed class DetailViewsSettings
{
    public string? TitleBlockName { get; set; }
    public string? View3DTemplateName { get; set; }
    public string? View2DTemplateName { get; set; }
    public string? LastSheetNumber { get; set; }
    public int Mode { get; set; }
    public double GapMm { get; set; } = 50.0;
}


// =============================================================================
// 2. SIZING AND FRAMES
// =============================================================================

/// <summary>A sheet's title block extent: the full size and where its centre
/// is in sheet coordinates.</summary>
internal readonly record struct DetailFrame(double FullWidth, double FullHeight, XYZ Center)
{
    public double UsableWidth => FullWidth * DetailPlanner.UsableFraction;
    public double UsableHeight => FullHeight * DetailPlanner.UsableFraction;
}

internal static class DetailFrames
{
    /// <summary>
    /// Measures a title block type by putting it on a sheet that is rolled back
    /// straight away -- the same measurement SheetFrame.Measure takes on the
    /// real sheets, so the preview and the placement agree.
    /// </summary>
    public static DetailFrame? ForTitleblock(Document doc, FamilySymbol symbol)
    {
        using var tx = new Transaction(doc, "Measure title block");
        tx.Start();
        try
        {
            var sheet = ViewSheet.Create(doc, symbol.Id);
            var frame = SheetFrame.Measure(doc, sheet, 1.0);
            return new DetailFrame(frame.UsableWidth, frame.UsableHeight, frame.Center);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
        }
    }

    /// <summary>The title block extent of a sheet that already exists.</summary>
    public static DetailFrame? ForSheet(Document doc, ViewSheet sheet)
    {
        var titleblock = new FilteredElementCollector(doc, sheet.Id)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType()
            .FirstElement();

        if (titleblock?.get_BoundingBox(sheet) is not { } bb) return null;

        return new DetailFrame(
            Math.Abs(bb.Max.X - bb.Min.X),
            Math.Abs(bb.Max.Y - bb.Min.Y),
            new XYZ((bb.Min.X + bb.Max.X) / 2.0, (bb.Min.Y + bb.Max.Y) / 2.0, 0));
    }
}

internal static class DetailSizing
{
    /// <summary>Breathing room around the set in the 2D views; matches the
    /// assembly drawing commands.</summary>
    public const double CropPaddingMm = 10.0;

    public static PlanSet ToPlanSet(DetailSet set, double sectionBoxPadding)
    {
        var views = new List<PlanView>();
        if (set.BoundingBox is { } bb)
        {
            foreach (var key in DetailKeys.Order.Where(set.Views.Contains))
            {
                var (w, h) = DetailKeys.Is3D(key)
                    ? Estimate3D(bb, sectionBoxPadding)
                    : Size2D(set, bb, key);
                views.Add(new PlanView(key, w, h));
            }
        }
        return new PlanSet(set.Name, views);
    }

    /// <summary>Drawn size of a true isometric of the box, section-box padding
    /// included. An estimate: the real 3D view is only known once it exists.</summary>
    public static (double W, double H) Estimate3D(BoundingBoxXYZ bb, double sectionBoxPadding)
    {
        var (sx, sy, sz) = GeometryUtils.GetSize(bb);
        sx += 2 * sectionBoxPadding;
        sy += 2 * sectionBoxPadding;
        sz += 2 * sectionBoxPadding;

        return ((sx + sy) * 0.7071, 0.8165 * sz + 0.4082 * (sx + sy));
    }

    public static (double W, double H) Size2D(DetailSet set, BoundingBoxXYZ bb, string key)
    {
        var padding = UnitUtils.ConvertToInternalUnits(CropPaddingMm, UnitTypeId.Millimeters);
        var (min, max) = ViewUtils.LocalExtents(ViewFrame(set, bb, key), bb, padding);
        return (max.X - min.X, max.Y - min.Y);
    }

    /// <summary>The frame a 2D view of <paramref name="key"/> is drawn in: the
    /// same bases the view creators use.</summary>
    private static Transform ViewFrame(DetailSet set, BoundingBoxXYZ bb, string key)
    {
        XYZ Map(XYZ local) => set.Frame.OfVector(local).Normalize();

        XYZ bx, by, bz;
        if (AssemblyDrawingBuilder.SideLookDirections.TryGetValue(key, out var look))
        {
            bz = Map(look);
            bx = XYZ.BasisZ.CrossProduct(bz).Normalize();
            by = bz.CrossProduct(bx).Normalize();
        }
        else
        {
            var right = XYZ.BasisZ.CrossProduct(
                Map(AssemblyDrawingBuilder.ScreenRightAnchorLocal)).Normalize();

            bz = XYZ.BasisZ;
            bx = key == "BOTTOM" ? right.Negate() : right;
            by = XYZ.BasisZ.CrossProduct(bx).Normalize();
        }

        var t = Transform.Identity;
        t.Origin = (bb.Min + bb.Max) / 2.0;
        t.BasisX = bx;
        t.BasisY = by;
        t.BasisZ = bz;
        return t;
    }
}

/// <summary>
/// What the setup window and the runner both need from the open document, and
/// the one place a request is turned into a plan.
/// </summary>
internal sealed class DetailContext
{
    private readonly Dictionary<ElementId, DetailFrame?> _titleblockFrames = [];
    private readonly Dictionary<ElementId, DetailFrame?> _sheetFrames = [];

    public Document Doc { get; init; } = null!;
    public IntPtr RevitHandle { get; init; }
    public IReadOnlyDictionary<string, View> Templates3D { get; init; } = new Dictionary<string, View>();
    public IReadOnlyDictionary<string, View> Templates2D { get; init; } = new Dictionary<string, View>();
    public IReadOnlyDictionary<string, FamilySymbol> Titleblocks { get; init; } = new Dictionary<string, FamilySymbol>();
    public IReadOnlyDictionary<string, ViewSheet> SheetsByLabel { get; init; } = new Dictionary<string, ViewSheet>();
    public HashSet<string> UsedSheetNumbers { get; init; } = [];
    public double SectionBoxPaddingFt { get; init; }

    public DetailFrame? FrameOf(FamilySymbol symbol)
    {
        if (!_titleblockFrames.TryGetValue(symbol.Id, out var frame))
        {
            frame = DetailFrames.ForTitleblock(Doc, symbol);
            _titleblockFrames[symbol.Id] = frame;
        }
        return frame;
    }

    public DetailFrame? FrameOf(ViewSheet sheet)
    {
        if (!_sheetFrames.TryGetValue(sheet.Id, out var frame))
        {
            frame = DetailFrames.ForSheet(Doc, sheet);
            _sheetFrames[sheet.Id] = frame;
        }
        return frame;
    }

    /// <summary>The frame the request will be laid out in, or null while the
    /// title block / sheet is not chosen or cannot be measured.</summary>
    public DetailFrame? FrameFor(DetailRequest req) =>
        req.Mode == DetailTargetMode.ExistingSheet
            ? (req.ExistingSheet is null ? null : FrameOf(req.ExistingSheet))
            : (req.Titleblock is null ? null : FrameOf(req.Titleblock));

    /// <summary>The plan the preview shows (sizes are estimates for the 3D
    /// views; the real run re-plans with the measured views).</summary>
    public (DetailPlan Plan, DetailFrame Frame)? BuildPlan(DetailRequest req)
    {
        if (FrameFor(req) is not { } frame) return null;

        var sets = req.PlannableSets();
        var planSets = sets.Select(s => DetailSizing.ToPlanSet(s, SectionBoxPaddingFt)).ToList();

        var plan = DetailPlanner.Plan(planSets, frame.UsableWidth, frame.UsableHeight, req.Mode);
        DetailNaming.Apply(
            plan, sets.Select(s => s.Name).ToList(), req.Mode,
            req.SheetName, req.SheetNumber, UsedSheetNumbers);

        return (plan, frame);
    }
}


// =============================================================================
// 3. VIEW BUILDER
// =============================================================================

/// <summary>
/// Creates the views for one set. Mirrors AssemblyDrawingBuilder.Build, but
/// frames the set's elements instead of an assembly and isolates exactly those.
/// The Front/Back/Left/Right look directions are the assembly drawing
/// commands' own fixed convention (AssemblyDrawingBuilder.SideLookDirections),
/// applied in the parent assembly's frame when there is one.
/// </summary>
internal static class DetailViewBuilder
{
    public static IReadOnlyDictionary<string, View> Build(
        Document doc, DetailSet set, DetailRequest req,
        double sectionBoxPadding, NameRegistry viewNames)
    {
        var bbox = set.BoundingBox
            ?? throw new InvalidOperationException("The set has no usable elements.");

        var views = new Dictionary<string, View>();

        if (set.Views.Contains("3D"))
        {
            var vft3d = ViewUtils.Get3DViewFamilyType(doc)
                ?? throw new InvalidOperationException(
                    "No 3D view family type found in this project.");

            var view3d = ViewUtils.CreateOrthographic3D(
                doc, vft3d, viewNames.Reserve($"{set.Name} - 3D"),
                req.V3d!.Id, bbox, sectionBoxPadding);
            IsolateElementsPermanently(doc, view3d, set.KeepIds);
            ViewUtils.SetDescription(view3d, AssemblyDrawingBuilder.ViewTitles["3D"]);
            views["3D"] = view3d;
        }

        var keys2d = DetailKeys.Order.Where(k => !DetailKeys.Is3D(k) && set.Views.Contains(k)).ToList();
        if (keys2d.Count == 0) return views;

        XYZ Map(XYZ local) => set.Frame.OfVector(local).Normalize();

        var viewPadding = UnitUtils.ConvertToInternalUnits(
            DetailSizing.CropPaddingMm, UnitTypeId.Millimeters);

        var sectionVft = ViewUtils.GetViewFamilyType(doc, ViewFamily.Section)
            ?? throw new InvalidOperationException(
                "No Section view family type found in this project.");

        var bboxCenter = (bbox.Min + bbox.Max) / 2.0;
        var frontRight = XYZ.BasisZ
            .CrossProduct(Map(AssemblyDrawingBuilder.ScreenRightAnchorLocal))
            .Normalize();

        var created = new Dictionary<string, View>();

        foreach (var key in keys2d)
        {
            if (AssemblyDrawingBuilder.SideLookDirections.TryGetValue(key, out var look))
            {
                created[key] = ViewUtils.CreateOrthoSection(
                    doc, sectionVft.Id, bbox, Map(look), viewPadding);
            }
            else if (key == "TOP")
            {
                var floorPlanVft = ViewUtils.GetViewFamilyType(doc, ViewFamily.FloorPlan)
                    ?? throw new InvalidOperationException(
                        "No Floor Plan view family type found in this project.");
                var level = LevelUtils.FindNearestLevel(doc, bboxCenter)
                    ?? throw new InvalidOperationException("No levels found in this project.");

                // Fit, rotate, fit -- see AssemblyDrawingBuilder.Build.
                var top = ViewPlan.Create(doc, floorPlanVft.Id, level.Id);
                ViewUtils.FitViewRangeToBoundingBox(top, bbox, level, viewPadding);
                ViewUtils.FitCropBoxToBoundingBox(top, bbox, viewPadding);
                ViewUtils.RotateCropToRightDirection(doc, top, frontRight, bboxCenter);
                ViewUtils.FitCropBoxToBoundingBox(top, bbox, viewPadding);
                created[key] = top;
            }
            else if (key == "BOTTOM")
            {
                created[key] = ViewUtils.CreateUpwardSection(
                    doc, sectionVft.Id, bbox, frontRight, viewPadding);
            }
        }

        foreach (var (key, view) in created)
        {
            view.Name = viewNames.Reserve($"{set.Name} - {key}");
            try { view.ViewTemplateId = req.V2d!.Id; }
            catch { /* template not valid for this view type; leave the view without one */ }

            IsolateElementsPermanently(doc, view, set.KeepIds);

            if (AssemblyDrawingBuilder.ViewTitles.TryGetValue(key, out var title))
                ViewUtils.SetDescription(view, title);
        }

        foreach (var key in keys2d)
            if (created.TryGetValue(key, out var view)) views[key] = view;

        return views;
    }

    /// <summary>
    /// Permanently hides everything in <paramref name="view"/> that is not in
    /// <paramref name="keep"/>. Same method as ViewUtils.IsolateAssemblyPermanently,
    /// with the keep-set supplied by the caller.
    /// </summary>
    private static void IsolateElementsPermanently(
        Document doc, View view, IReadOnlySet<ElementId> keep)
    {
        // A view-based collector only sees what the view has resolved, and these
        // views were created moments ago in this same transaction.
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
            // Bulk hide is all-or-nothing; fall back to one at a time so a
            // single stubborn element does not cost the whole isolation.
            foreach (var id in toHide)
            {
                try { view.HideElements(new List<ElementId> { id }); }
                catch { /* element refuses to hide in this view; skip it */ }
            }
        }
    }

    /// <summary>The size a built view will draw at 1:1, for re-planning with
    /// real numbers once the views exist.</summary>
    public static (double W, double H) MeasuredSize(
        DetailSet set, string key, View view, double sectionBoxPadding)
    {
        if (DetailKeys.Is3D(key))
            return DetailSizing.Estimate3D(set.BoundingBox!, sectionBoxPadding);

        var crop = view.CropBox;
        return (Math.Abs(crop.Max.X - crop.Min.X), Math.Abs(crop.Max.Y - crop.Min.Y));
    }
}


// =============================================================================
// 4. SELECTION
// =============================================================================

internal sealed class DetailSelectionFilter : ISelectionFilter
{
    public bool AllowElement(Element elem) =>
        elem is AssemblyInstance || elem is Group || DetailSet.IsUsable(elem);

    public bool AllowReference(Reference reference, XYZ position) => false;
}


// =============================================================================
// 5. THE COMMAND
// =============================================================================

[Transaction(TransactionMode.Manual)]
public sealed class Cmd_DOC03e_Detail : IExternalCommand
{
    private const string AlertTitle = "Detail Views";
    private const string ReportTitle = "Detail Views";
    private const string SettingsFile = "detail-views.json";

    public Result Execute(ExternalCommandData commandData, ref string message,
        ElementSet elements)
    {
        try
        {
            return Run(commandData.Application, out message);
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            message = string.Empty;
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    private static Result Run(UIApplication uiApp, out string message)
    {
        message = string.Empty;
        var uiDoc = uiApp.ActiveUIDocument;
        var doc = uiDoc.Document;

        // ── What the project offers ─────────────────────────────────────────
        var templates = ViewUtils.GetTemplates(doc);

        var templates3d = FilterByType(templates, ViewType.ThreeD);
        if (templates3d.Count == 0)
        {
            AvControls.ShowAlert(AlertTitle,
                "No 3D view templates found in this project.", AlertSeverity.Warning);
            return Result.Failed;
        }

        // Elevation, Section and Detail are one interchangeable group for the
        // section views the API creates (see TemplatePicker.View2dTemplatesOf).
        var templates2d = FilterByType(
            templates, ViewType.Elevation, ViewType.Section, ViewType.Detail);
        if (templates2d.Count == 0)
        {
            AvControls.ShowAlert(AlertTitle,
                "No 2D view templates found in this project.\n\n" +
                "An elevation, section or detail view template is required.",
                AlertSeverity.Warning);
            return Result.Failed;
        }

        var titleblocks = TitleblockUtils.CollectSymbols(doc);
        if (titleblocks.Count == 0)
        {
            AvControls.ShowAlert(AlertTitle,
                "No title block types found in this project.", AlertSeverity.Warning);
            return Result.Failed;
        }

        var sheets = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(s => !s.IsPlaceholder)
            .OrderBy(s => s.SheetNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var ctx = new DetailContext
        {
            Doc = doc,
            RevitHandle = uiApp.MainWindowHandle,
            Templates3D = templates3d,
            Templates2D = templates2d,
            Titleblocks = titleblocks,
            SheetsByLabel = sheets
                .GroupBy(s => $"{s.SheetNumber} - {s.Name}")
                .ToDictionary(g => g.Key, g => g.First()),
            UsedSheetNumbers = new HashSet<string>(
                sheets.Select(s => s.SheetNumber), StringComparer.OrdinalIgnoreCase),
            SectionBoxPaddingFt = SettingsStore
                .LoadOrDefault<AssemblyDrawingsSettings>("assembly-drawings.json")
                .SectionBoxPaddingFt(),
        };

        // ── Starting request, restored from the last run ────────────────────
        var saved = SettingsStore.LoadOrDefault<DetailViewsSettings>(SettingsFile);

        var req = new DetailRequest
        {
            Titleblock = TitleblockUtils.Resolve(titleblocks, saved.TitleBlockName, out _),
            Mode = Enum.IsDefined(typeof(DetailTargetMode), saved.Mode)
                ? (DetailTargetMode)saved.Mode
                : DetailTargetMode.NewShared,
            GapMm = saved.GapMm > 0 ? saved.GapMm : 50.0,
        };
        if (req.Mode == DetailTargetMode.ExistingSheet) req.Mode = DetailTargetMode.NewShared;

        templates3d.TryGetValue(saved.View3DTemplateName ?? string.Empty, out var preset3d);
        templates2d.TryGetValue(saved.View2DTemplateName ?? string.Empty, out var preset2d);
        req.V3d = preset3d;
        req.V2d = preset2d;
        req.SheetNumber = DetailNaming.Allocate(
            saved.LastSheetNumber ?? string.Empty, ctx.UsedSheetNumbers, 1)[0];

        // A selection made before the click becomes the first set.
        var preselected = uiDoc.Selection.GetElementIds();
        if (preselected.Count > 0)
        {
            var first = new DetailSet { Name = NextSetName(req) };
            first.SetElements(doc, preselected);
            if (first.ElementIds.Count > 0) req.Sets.Add(first);
        }

        // ── Window loop ─────────────────────────────────────────────────────
        // Adding or re-picking a set closes the window so the user can click in
        // the model; the same request goes back in, so nothing is lost.
        while (true)
        {
            var window = new DetailSetupWindow(req, ctx);
            window.ShowDialog();

            switch (window.Outcome)
            {
                case DetailWindowOutcome.AddSet:
                {
                    var ids = PickElements(uiDoc,
                        "Select the elements for this detail, then click Finish.");
                    if (ids is null) break;

                    var set = new DetailSet { Name = NextSetName(req) };
                    set.SetElements(doc, ids);
                    if (set.ElementIds.Count == 0)
                    {
                        AvControls.ShowAlert(AlertTitle,
                            "None of the picked elements could be used. Pick model " +
                            "elements that have geometry.", AlertSeverity.Warning);
                        break;
                    }
                    req.Sets.Add(set);
                    break;
                }

                case DetailWindowOutcome.Repick:
                {
                    var ids = PickElements(uiDoc,
                        "Select the elements for this detail, then click Finish.");
                    if (ids is null) break;

                    var set = req.Sets[window.RepickIndex];
                    set.SetElements(doc, ids);
                    if (set.ElementIds.Count == 0)
                    {
                        AvControls.ShowAlert(AlertTitle,
                            "None of the picked elements could be used.",
                            AlertSeverity.Warning);
                    }
                    break;
                }

                case DetailWindowOutcome.Run:
                {
                    if (req.Problem() is { } problem)
                    {
                        AvControls.ShowAlert(AlertTitle, problem, AlertSeverity.Warning);
                        break;
                    }

                    if (ctx.BuildPlan(req) is not { } preview)
                    {
                        AvControls.ShowAlert(AlertTitle,
                            "Could not measure the title block.", AlertSeverity.Warning);
                        break;
                    }

                    if (preview.Plan.NeedsExtraSheets
                        && !DetailPrompt.AskExtraSheets(
                            uiApp.MainWindowHandle, preview.Plan.Sheets.Count))
                    {
                        break;      // back to the window to untick views
                    }

                    return Generate(uiApp, doc, req, ctx, saved, out message);
                }

                default:
                    return Result.Cancelled;
            }
        }
    }

    private static string NextSetName(DetailRequest req)
    {
        var n = req.Sets.Count;
        var name = n < 26 ? $"Detail {(char)('A' + n)}" : $"Detail {n + 1}";
        while (req.Sets.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            name += "'";
        return name;
    }

    private static IList<ElementId>? PickElements(UIDocument uiDoc, string prompt)
    {
        try
        {
            return uiDoc.Selection
                .PickObjects(ObjectType.Element, new DetailSelectionFilter(), prompt)
                .Select(r => r.ElementId)
                .ToList();
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, View> FilterByType(
        IReadOnlyDictionary<string, View> templates, params ViewType[] kinds) =>
        templates
            .Where(kv => Array.IndexOf(kinds, kv.Value.ViewType) >= 0)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

    // ── Generation ──────────────────────────────────────────────────────────

    private static Result Generate(
        UIApplication uiApp, Document doc, DetailRequest req, DetailContext ctx,
        DetailViewsSettings saved, out string message)
    {
        message = string.Empty;

        var sets = req.PlannableSets();
        var viewNames = AssemblyDrawingRunner.BuildViewNameRegistry(doc);

        var built = new Dictionary<int, IReadOnlyDictionary<string, View>>();
        var failures = new Dictionary<int, string>();

        using var tx = new Transaction(doc, "Detail Views");
        tx.Start();
        FailureHandling.SuppressWarnings(tx);

        // 1. Views, one sub-transaction per set so a failing set rolls back alone.
        for (var i = 0; i < sets.Count; i++)
        {
            using var sub = new SubTransaction(doc);
            sub.Start();
            try
            {
                built[i] = DetailViewBuilder.Build(
                    doc, sets[i], req, ctx.SectionBoxPaddingFt, viewNames);
                sub.Commit();
            }
            catch (Exception ex)
            {
                sub.RollBack();
                failures[i] = ex.Message;
            }
        }

        // 2. Re-plan with the measured views. Position i in planSets is
        //    sets[okIndexes[i]].
        var okIndexes = built.Keys.OrderBy(k => k).ToList();
        var placements = new Dictionary<int, List<string>>();     // set -> where it went
        var warnings = new Dictionary<int, List<string>>();

        List<string> WarningsOf(int setIndex) =>
            warnings.TryGetValue(setIndex, out var list) ? list : warnings[setIndex] = [];

        if (okIndexes.Count > 0)
        {
            var frame = ctx.FrameFor(req);
            if (frame is null)
            {
                tx.RollBack();
                message = "Could not measure the title block.";
                return Result.Failed;
            }

            var planSets = okIndexes
                .Select(i => new PlanSet(
                    sets[i].Name,
                    DetailKeys.Order
                        .Where(built[i].ContainsKey)
                        .Select(k =>
                        {
                            var (w, h) = DetailViewBuilder.MeasuredSize(
                                sets[i], k, built[i][k], ctx.SectionBoxPaddingFt);
                            return new PlanView(k, w, h);
                        })
                        .ToList()))
                .ToList();

            var plan = DetailPlanner.Plan(
                planSets, frame.Value.UsableWidth, frame.Value.UsableHeight, req.Mode);
            DetailNaming.Apply(
                plan, planSets.Select(s => s.Name).ToList(), req.Mode,
                req.SheetName, req.SheetNumber, ctx.UsedSheetNumbers);

            // 3. Sheets and viewports, one sub-transaction per page.
            foreach (var page in plan.Sheets)
            {
                using var sub = new SubTransaction(doc);
                sub.Start();

                var pageSets = page.SetIndexes.Select(p => okIndexes[p]).Distinct().ToList();
                try
                {
                    ViewSheet sheet;
                    XYZ centre;

                    if (req.Mode == DetailTargetMode.ExistingSheet)
                    {
                        sheet = req.ExistingSheet!;
                        centre = frame.Value.Center + new XYZ(
                            0, DetailPlanner.StagingOffsetY(
                                page.PageIndex, frame.Value.FullHeight, req.GapFt), 0);
                    }
                    else
                    {
                        sheet = ViewSheet.Create(doc, req.Titleblock!.Id);
                        sheet.SheetNumber = page.Number;
                        sheet.Name = page.Name;
                        centre = SheetFrame.Measure(
                            doc, sheet, DetailPlanner.UsableFraction).Center;
                    }

                    foreach (var pv in page.Views)
                    {
                        var setIndex = okIndexes[pv.SetIndex];
                        var view = built[setIndex][pv.Key];

                        try { view.Scale = pv.Scale; }
                        catch
                        {
                            WarningsOf(setIndex).Add(
                                $"{view.Name}: the view template stops the scale being set; " +
                                $"it stays at 1:{view.Scale}.");
                        }

                        var point = new XYZ(centre.X + pv.CenterX, centre.Y + pv.CenterY, 0);
                        if (ViewUtils.PlaceView(doc, sheet, view, point) is null)
                        {
                            throw new InvalidOperationException(
                                $"{view.Name} cannot be added to sheet {sheet.SheetNumber}.");
                        }

                        if (pv.Scale > DetailPlanner.ScaleWarnAbove)
                        {
                            var note = DetailKeys.Is3D(pv.Key)
                                ? $"3D comes out at 1:{pv.Scale}."
                                : $"The 2D views come out at 1:{pv.Scale}.";
                            var notes = WarningsOf(setIndex);
                            if (!notes.Contains(note)) notes.Add(note);
                        }
                    }

                    sub.Commit();

                    var label = req.Mode == DetailTargetMode.ExistingSheet
                        ? $"{sheet.SheetNumber} (below the frame)"
                        : sheet.SheetNumber;

                    foreach (var i in pageSets)
                    {
                        if (!placements.TryGetValue(i, out var where)) placements[i] = where = [];
                        where.Add(label);
                    }
                }
                catch (Exception ex)
                {
                    sub.RollBack();
                    foreach (var i in pageSets)
                        failures[i] = $"Views were created but could not be placed: {ex.Message}";
                }
            }

            if (req.Mode == DetailTargetMode.ExistingSheet)
            {
                foreach (var (i, _) in placements)
                {
                    var count = built[i].Count;
                    WarningsOf(i).Add(
                        $"{count} view{(count == 1 ? "" : "s")} placed below sheet " +
                        $"{req.ExistingSheet!.SheetNumber}. Move them into position.");
                }
            }

            if (req.Mode != DetailTargetMode.ExistingSheet && plan.Sheets.Count > 0)
                saved.LastSheetNumber = plan.Sheets[^1].Number;
        }

        tx.Commit();

        // 4. Remember the choices, then report.
        saved.TitleBlockName = req.Titleblock is null ? saved.TitleBlockName
            : TitleblockUtils.DisplayName(req.Titleblock);
        saved.View3DTemplateName = req.V3d?.Name ?? saved.View3DTemplateName;
        saved.View2DTemplateName = req.V2d?.Name ?? saved.View2DTemplateName;
        saved.Mode = (int)req.Mode;
        saved.GapMm = req.GapMm;
        SettingsStore.Save(SettingsFile, saved);

        var results = new List<(string Name, string? Error, string? Warning)>();
        for (var i = 0; i < sets.Count; i++)
        {
            var set = sets[i];

            if (failures.TryGetValue(i, out var error))
            {
                results.Add((set.Name, error, null));
                continue;
            }

            var notes = warnings.TryGetValue(i, out var w) ? new List<string>(w) : [];
            if (set.SkippedCount > 0)
            {
                notes.Add($"{set.SkippedCount} picked element" +
                          $"{(set.SkippedCount == 1 ? " was" : "s were")} skipped " +
                          "(no geometry or not a model element).");
            }

            var where = placements.TryGetValue(i, out var p) ? string.Join(", ", p) : "";
            results.Add((
                where.Length > 0 ? $"{set.Name}  →  {where}" : set.Name,
                null,
                notes.Count > 0 ? string.Join("\n", notes) : null));
        }

        Reporting.ShowResult(ReportTitle, results, uiApp.MainWindowHandle);
        return Result.Succeeded;
    }
}
