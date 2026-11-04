// -----------------------------------------------------------------------
// Title: Detail Views - setup window
// Summary: The window for the Detail Views button: the sets of elements and
//          their view ticks, the templates and title block, where the views
//          go, and a live to-scale preview of the resulting sheets.
// Key logic: Every change re-runs DetailContext.BuildPlan and repaints the
//            preview from it -- the same plan the real placement uses. Adding
//            or re-picking a set closes the window with an Outcome; the runner
//            picks in the model and re-opens it on the same DetailRequest.
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Autodesk.Revit.DB;
using AVES.Core;
using Canvas = System.Windows.Controls.Canvas;
using CheckBox = System.Windows.Controls.CheckBox;
using Grid = System.Windows.Controls.Grid;
using RadioButton = System.Windows.Controls.RadioButton;
using Rectangle = System.Windows.Shapes.Rectangle;
using TextBox = System.Windows.Controls.TextBox;
using Visibility = System.Windows.Visibility;

namespace AVES.Commands;

internal enum DetailWindowOutcome
{
    Cancel,
    Run,
    AddSet,
    Repick,
}

internal sealed class DetailSetupWindow : Window
{
    private const string Placeholder = "Not selected";
    private const double CanvasW = 640;
    private const double CanvasH = 440;
    private const double CanvasPad = 24;

    private readonly DetailRequest _req;
    private readonly DetailContext _ctx;

    private readonly StackPanel _setsPanel = new();
    private readonly Button _pick3d;
    private readonly Button _pick2d;
    private readonly Button _pickTitleblock;
    private readonly Button _pickSheet;
    private readonly RadioButton _rbShared;
    private readonly RadioButton _rbPerSet;
    private readonly RadioButton _rbExisting;
    private readonly TextBox _nameBox;
    private readonly TextBox _numberBox;
    private readonly TextBox _gapBox;
    private readonly StackPanel _newSheetPanel = new();
    private readonly StackPanel _existingPanel = new();
    private readonly TextBlock _problemText;
    private readonly Button _runBtn;
    private readonly Border _previewHost;
    private readonly Border _banner;
    private readonly TextBlock _bannerText;
    private readonly StackPanel _pager;
    private readonly TextBlock _pageLabel;

    private int _page;
    private bool _loading;

    public DetailWindowOutcome Outcome { get; private set; } = DetailWindowOutcome.Cancel;
    public int RepickIndex { get; private set; }

    public DetailSetupWindow(DetailRequest req, DetailContext ctx)
    {
        _req = req;
        _ctx = ctx;

        Title = "Detail Views";
        Width = 1220;
        MinWidth = 980;
        SizeToContent = SizeToContent.Height;
        MaxHeight = SystemParameters.WorkArea.Height * 0.92;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        FontFamily = AvTokens.Ui;
        Background = AvTokens.Br(AvTokens.Surface);

        if (ctx.RevitHandle != IntPtr.Zero)
            _ = new WindowInteropHelper(this) { Owner = ctx.RevitHandle };

        var root = new DockPanel();

        var header = AvControls.HeaderStrip("Detail Views");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // ── Footer ────────────────────────────────────────────────────────────
        _runBtn = AvControls.PrimaryButtonHoverLoaded("Run");
        _runBtn.IsDefault = true;
        _runBtn.Click += (_, _) => Finish(DetailWindowOutcome.Run);

        var cancelBtn = AvControls.GhostButtonHoverLoaded("Cancel");
        cancelBtn.IsCancel = true;
        cancelBtn.Margin = new Thickness(0, 0, 8, 0);
        cancelBtn.Click += (_, _) => Finish(DetailWindowOutcome.Cancel);

        _problemText = new TextBlock
        {
            FontSize = 12,
            Foreground = AvTokens.Br(AvTokens.TextDim),
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(cancelBtn);
        buttons.Children.Add(_runBtn);

        var footerGrid = new Grid();
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_problemText, 0);
        Grid.SetColumn(buttons, 1);
        footerGrid.Children.Add(_problemText);
        footerGrid.Children.Add(buttons);

        var footer = new Border
        {
            Background = AvTokens.Br(AvTokens.Surface2),
            BorderBrush = AvTokens.Br(AvTokens.Border),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 14, 20, 14),
            Child = footerGrid,
        };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // ── Body ──────────────────────────────────────────────────────────────
        var columns = new Grid { Margin = new Thickness(20, 16, 20, 16) };
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(510) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // Left column
        var left = new StackPanel();

        left.Children.Add(AvControls.FieldLabel(1, "Sets"));
        left.Children.Add(_setsPanel);

        var addSet = AvControls.SecondaryBtn("+ Add set", () => Finish(DetailWindowOutcome.AddSet));
        addSet.HorizontalAlignment = HorizontalAlignment.Left;
        addSet.Margin = new Thickness(0, 2, 0, 16);
        left.Children.Add(addSet);

        _pick3d = AvControls.PickerButton(Placeholder, showChevron: true);
        _pick3d.Click += (_, _) => PickTemplate(_ctx.Templates3D, "Select 3D View Template", _pick3d, v => _req.V3d = v);
        AddStep(left, 2, "3D View Template", _pick3d);

        _pick2d = AvControls.PickerButton(Placeholder, showChevron: true);
        _pick2d.Click += (_, _) => PickTemplate(_ctx.Templates2D, "Select 2D View Template", _pick2d, v => _req.V2d = v);
        AddStep(left, 3, "2D View Template", _pick2d);

        left.Children.Add(AvControls.FieldLabel(4, "Place on"));
        _rbShared = NewRadio("New sheet - all sets together", DetailTargetMode.NewShared);
        _rbPerSet = NewRadio("New sheet per set", DetailTargetMode.NewPerSet);
        _rbExisting = NewRadio("Existing sheet (views are placed below its frame)", DetailTargetMode.ExistingSheet);
        left.Children.Add(_rbShared);
        left.Children.Add(_rbPerSet);
        left.Children.Add(_rbExisting);
        left.Children.Add(new Border { Height = 14 });

        // New-sheet fields
        _pickTitleblock = AvControls.PickerButton(Placeholder, showChevron: true);
        _pickTitleblock.Click += (_, _) => PickTitleblock();
        _newSheetPanel.Children.Add(AvControls.FieldLabel(5, "Title Block"));
        _pickTitleblock.Margin = new Thickness(0, 0, 0, 16);
        _newSheetPanel.Children.Add(_pickTitleblock);

        _newSheetPanel.Children.Add(AvControls.FieldLabel(6, "Sheet name and number"));
        _nameBox = NewTextBox();
        _nameBox.TextChanged += (_, _) => OnNameChanged();
        _numberBox = NewTextBox();
        _numberBox.TextChanged += (_, _) => OnNumberChanged();

        var nameRow = new Grid();
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        Grid.SetColumn(_nameBox, 0);
        Grid.SetColumn(_numberBox, 2);
        nameRow.Children.Add(_nameBox);
        nameRow.Children.Add(_numberBox);
        _newSheetPanel.Children.Add(nameRow);
        _newSheetPanel.Children.Add(Hint(
            "The name is a suggestion - edit it freely. Numbers count up from the one " +
            "entered and skip any already in the project."));
        left.Children.Add(_newSheetPanel);

        // Existing-sheet fields
        _pickSheet = AvControls.PickerButton(Placeholder, showChevron: true);
        _pickSheet.Click += (_, _) => PickSheet();
        _existingPanel.Children.Add(AvControls.FieldLabel(5, "Existing sheet"));
        _pickSheet.Margin = new Thickness(0, 0, 0, 16);
        _existingPanel.Children.Add(_pickSheet);

        _existingPanel.Children.Add(AvControls.FieldLabel(6, "Gap below the title block (mm)"));
        _gapBox = NewTextBox();
        _gapBox.Width = 130;
        _gapBox.HorizontalAlignment = HorizontalAlignment.Left;
        _gapBox.TextChanged += (_, _) => OnGapChanged();
        _existingPanel.Children.Add(_gapBox);
        _existingPanel.Children.Add(Hint(
            "The views are laid out as they would be on a blank sheet and placed " +
            "below the frame. Move them into the free space by hand."));
        left.Children.Add(_existingPanel);

        var leftScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = left,
        };
        Grid.SetColumn(leftScroll, 0);
        columns.Children.Add(leftScroll);

        // Right column - preview
        var right = new DockPanel();

        _pageLabel = new TextBlock
        {
            FontSize = 12,
            Foreground = AvTokens.Br(AvTokens.TextDim),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 0),
        };
        var prev = AvControls.SecondaryBtn("◀", () => { _page--; RefreshPreview(); });
        var next = AvControls.SecondaryBtn("▶", () => { _page++; RefreshPreview(); });
        _pager = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
        _pager.Children.Add(prev);
        _pager.Children.Add(_pageLabel);
        _pager.Children.Add(next);

        var previewTitle = new TextBlock
        {
            Text = "PREVIEW",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = AvTokens.Br(AvTokens.TextFaint),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var previewHeader = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        DockPanel.SetDock(_pager, Dock.Right);
        previewHeader.Children.Add(_pager);
        previewHeader.Children.Add(previewTitle);
        DockPanel.SetDock(previewHeader, Dock.Top);
        right.Children.Add(previewHeader);

        _bannerText = new TextBlock
        {
            FontSize = 12,
            Foreground = AvTokens.Br(AvTokens.WarnFg),
            TextWrapping = TextWrapping.Wrap,
        };
        _banner = new Border
        {
            Background = AvTokens.Br(AvTokens.WarnBg),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 8),
            Visibility = Visibility.Collapsed,
            Child = _bannerText,
        };
        DockPanel.SetDock(_banner, Dock.Top);
        right.Children.Add(_banner);

        _previewHost = new Border
        {
            Background = AvTokens.Br(AvTokens.Zebra),
            BorderBrush = AvTokens.Br(AvTokens.Border),
            BorderThickness = new Thickness(1),
            MinHeight = 440,
        };
        right.Children.Add(_previewHost);

        Grid.SetColumn(right, 2);
        columns.Children.Add(right);

        root.Children.Add(columns);
        Content = root;

        // ── Initial state ─────────────────────────────────────────────────────
        _loading = true;
        _numberBox.Text = _req.SheetNumber;
        _nameBox.Text = _req.SheetName;
        _gapBox.Text = _req.GapMm.ToString("0.##", CultureInfo.InvariantCulture);
        _loading = false;

        AvControls.SetPickerValue(_pick3d, _req.V3d?.Name, Placeholder);
        AvControls.SetPickerValue(_pick2d, _req.V2d?.Name, Placeholder);
        AvControls.SetPickerValue(_pickTitleblock,
            _req.Titleblock is null ? null : TitleblockUtils.DisplayName(_req.Titleblock), Placeholder);
        AvControls.SetPickerValue(_pickSheet,
            _req.ExistingSheet is null ? null : $"{_req.ExistingSheet.SheetNumber} - {_req.ExistingSheet.Name}",
            Placeholder);

        _loading = true;
        _rbShared.IsChecked = _req.Mode == DetailTargetMode.NewShared;
        _rbPerSet.IsChecked = _req.Mode == DetailTargetMode.NewPerSet;
        _rbExisting.IsChecked = _req.Mode == DetailTargetMode.ExistingSheet;
        _loading = false;

        RebuildSets();
        UpdateTargetPanels();
        RefreshAll();
    }

    // ── Plumbing ─────────────────────────────────────────────────────────────

    private void Finish(DetailWindowOutcome outcome)
    {
        Outcome = outcome;
        DialogResult = outcome == DetailWindowOutcome.Run;
        Close();
    }

    private void RefreshAll()
    {
        UpdateSuggestedName();
        RefreshPreview();
        UpdateRunState();
    }

    private void UpdateRunState()
    {
        var problem = _req.Problem();
        _runBtn.IsEnabled = problem is null;
        _problemText.Text = problem ?? "";
    }

    private void UpdateTargetPanels()
    {
        var existing = _req.Mode == DetailTargetMode.ExistingSheet;
        _newSheetPanel.Visibility = existing ? Visibility.Collapsed : Visibility.Visible;
        _existingPanel.Visibility = existing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateSuggestedName()
    {
        if (_req.SheetNameEdited) return;

        var names = _req.PlannableSets().Select(s => s.Name).ToList();
        var suggestion = names.Count == 0 ? "Detail Views" : DetailNaming.SuggestBaseName(names);

        _req.SheetName = suggestion;
        _loading = true;
        _nameBox.Text = suggestion;
        _loading = false;
    }

    private void OnNameChanged()
    {
        if (_loading) return;

        _req.SheetName = _nameBox.Text;
        _req.SheetNameEdited = !string.IsNullOrWhiteSpace(_nameBox.Text);
        RefreshPreview();
    }

    private void OnNumberChanged()
    {
        if (_loading) return;

        _req.SheetNumber = _numberBox.Text.Trim();
        RefreshPreview();
        UpdateRunState();
    }

    private void OnGapChanged()
    {
        if (_loading) return;

        if (double.TryParse(_gapBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm)
            || double.TryParse(_gapBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out mm))
        {
            _req.GapMm = Math.Max(0, mm);
            RefreshPreview();
        }
    }

    private void PickTemplate(
        IReadOnlyDictionary<string, View> source, string title, Button box, Action<View> assign)
    {
        var picked = TemplatePicker.Pick(source, title, _ctx.RevitHandle);
        if (picked is null) return;

        assign(picked);
        AvControls.SetPickerValue(box, picked.Name, Placeholder);
        RefreshAll();
    }

    private void PickTitleblock()
    {
        var picked = TemplatePicker.PickName(_ctx.Titleblocks.Keys, "Select Title Block", _ctx.RevitHandle);
        if (picked is null) return;

        _req.Titleblock = _ctx.Titleblocks[picked];
        AvControls.SetPickerValue(_pickTitleblock, picked, Placeholder);
        RefreshAll();
    }

    private void PickSheet()
    {
        var picked = TemplatePicker.PickName(_ctx.SheetsByLabel.Keys, "Select Sheet", _ctx.RevitHandle);
        if (picked is null) return;

        var sheet = _ctx.SheetsByLabel[picked];
        if (_ctx.FrameOf(sheet) is null)
        {
            AvControls.ShowAlert("Detail Views",
                "That sheet has no title block, so there is no frame to place the views below.\n\n" +
                "Pick another sheet.", AlertSeverity.Warning);
            return;
        }

        _req.ExistingSheet = sheet;
        AvControls.SetPickerValue(_pickSheet, picked, Placeholder);
        RefreshAll();
    }

    // ── Sets ─────────────────────────────────────────────────────────────────

    private void RebuildSets()
    {
        _setsPanel.Children.Clear();

        if (_req.Sets.Count == 0)
        {
            _setsPanel.Children.Add(Hint(
                "No sets yet. Select elements in the model, then click Add set."));
            return;
        }

        for (var i = 0; i < _req.Sets.Count; i++)
            _setsPanel.Children.Add(BuildSetCard(i, _req.Sets[i]));
    }

    private Border BuildSetCard(int index, DetailSet set)
    {
        var nameBox = NewTextBox();
        nameBox.Text = set.Name;
        nameBox.Width = 150;
        nameBox.FontWeight = FontWeights.SemiBold;
        nameBox.TextChanged += (_, _) =>
        {
            set.Name = nameBox.Text;
            RefreshAll();
        };

        var count = set.ElementIds.Count;
        var info = new TextBlock
        {
            Text = $"{count} element{(count == 1 ? "" : "s")} · {set.OrientationLabel}",
            FontSize = 11,
            Foreground = AvTokens.Br(AvTokens.TextFaint),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(10, 0, 8, 0),
        };

        var repick = AvControls.SecondaryBtn("Re-pick", () =>
        {
            RepickIndex = index;
            Finish(DetailWindowOutcome.Repick);
        });
        repick.Margin = new Thickness(0, 0, 6, 0);

        var remove = AvControls.SecondaryBtn("✕", () =>
        {
            _req.Sets.RemoveAt(index);
            RebuildSets();
            RefreshAll();
        });

        var top = new Grid();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(nameBox, 0);
        Grid.SetColumn(info, 1);
        Grid.SetColumn(repick, 2);
        Grid.SetColumn(remove, 3);
        top.Children.Add(nameBox);
        top.Children.Add(info);
        top.Children.Add(repick);
        top.Children.Add(remove);

        // One row: a StackPanel never wraps, and the column is wide enough for all seven.
        var ticks = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 10, 0, 2),
        };
        for (var k = 0; k < DetailKeys.Order.Length; k++)
        {
            var key = DetailKeys.Order[k];
            var box = new CheckBox
            {
                Content = DetailKeys.Labels[k],
                IsChecked = set.Views.Contains(key),
                FontSize = 12,
                Foreground = AvTokens.Br(AvTokens.Text),
                Margin = new Thickness(0, 0, 12, 0),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            if (DetailControlStyles.Check() is { } checkTemplate) box.Template = checkTemplate;
            box.Checked += (_, _) => { set.Views.Add(key); RefreshAll(); };
            box.Unchecked += (_, _) => { set.Views.Remove(key); RefreshAll(); };
            ticks.Children.Add(box);
        }

        var body = new StackPanel();
        body.Children.Add(top);
        body.Children.Add(ticks);

        if (set.SkippedCount > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"{set.SkippedCount} picked element{(set.SkippedCount == 1 ? "" : "s")} " +
                       "skipped (no geometry or not a model element).",
                FontSize = 11,
                Foreground = AvTokens.Br(AvTokens.WarnFg),
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        return new Border
        {
            Background = AvTokens.Br(AvTokens.Surface2),
            BorderBrush = AvTokens.Br(AvTokens.Border),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = body,
        };
    }

    // ── Preview ──────────────────────────────────────────────────────────────

    private (DetailPlan Plan, DetailFrame Frame)? SafeBuildPlan()
    {
        try { return _ctx.BuildPlan(_req); }
        catch { return null; }
    }

    private void ShowPreviewMessage(string text)
    {
        _pager.Visibility = Visibility.Collapsed;
        _banner.Visibility = Visibility.Collapsed;
        _previewHost.Child = new TextBlock
        {
            Text = text,
            FontSize = 13,
            Foreground = AvTokens.Br(AvTokens.TextFaint),
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MaxWidth = 380,
        };
    }

    private void RefreshPreview()
    {
        var existing = _req.Mode == DetailTargetMode.ExistingSheet;

        if (_req.PlannableSets().Count == 0)
        {
            ShowPreviewMessage("Add a set and tick at least one view to see the layout.");
            return;
        }

        if (SafeBuildPlan() is not { } built)
        {
            ShowPreviewMessage(existing
                ? "Pick an existing sheet to see the layout."
                : "Pick a title block to see the layout.");
            return;
        }

        var (plan, frame) = built;

        // Pager - one sheet at a time for new sheets; an existing sheet shows
        // all of its staged pages at once.
        var multi = !existing && plan.Sheets.Count > 1;
        _page = Math.Clamp(_page, 0, Math.Max(0, plan.Sheets.Count - 1));
        _pager.Visibility = multi ? Visibility.Visible : Visibility.Collapsed;
        _pageLabel.Text = $"{_page + 1} / {plan.Sheets.Count}";

        // Notes
        var notes = new List<string>(plan.Warnings);
        if (plan.NeedsExtraSheets)
        {
            notes.Add($"These sets need {plan.Sheets.Count} sheets. You will be asked whether to " +
                      "add extra sheets, or you can go back and untick views.");
        }
        _bannerText.Text = string.Join("\n", notes);
        _banner.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        _previewHost.Child = new Viewbox
        {
            Stretch = Stretch.Uniform,
            Margin = new Thickness(8),
            Child = DrawPlan(plan, frame, existing),
        };
    }

    private LayoutPreviewCanvas DrawPlan(DetailPlan plan, DetailFrame frame, bool existing)
    {
        var canvas = new LayoutPreviewCanvas { Width = CanvasW, Height = CanvasH };
        var sets = _req.PlannableSets();
        var gap = _req.GapFt;

        var contentW = frame.FullWidth;
        var contentH = existing
            ? frame.FullHeight + plan.Sheets.Count * (gap + frame.FullHeight)
            : frame.FullHeight;

        var px = Math.Min(
            (CanvasW - 2 * CanvasPad) / contentW,
            (CanvasH - 2 * CanvasPad) / contentH);

        var centreX = CanvasW / 2.0;
        var sheetW = frame.FullWidth * px;
        var sheetH = frame.FullHeight * px;
        var sheetCentreY = existing
            ? (CanvasH - contentH * px) / 2.0 + sheetH / 2.0
            : CanvasH / 2.0;

        string sheetLabel;
        if (existing)
        {
            sheetLabel = _req.ExistingSheet is null
                ? "Existing sheet"
                : $"{_req.ExistingSheet.SheetNumber} · {_req.ExistingSheet.Name}";
        }
        else
        {
            sheetLabel = plan.Sheets.Count > 0
                ? $"{plan.Sheets[_page].Number} · {plan.Sheets[_page].Name}"
                : "New sheet";
        }

        canvas.DrawSheetOutline(
            centreX - sheetW / 2.0, sheetCentreY - sheetH / 2.0, sheetW, sheetH, sheetLabel);

        var accent = AvTokens.Br(AvTokens.AccentDeep);

        foreach (var page in plan.Sheets)
        {
            if (!existing && page.PageIndex != _page) continue;

            var pageCentreY = existing
                ? sheetCentreY - DetailPlanner.StagingOffsetY(
                    page.PageIndex, frame.FullHeight, gap) * px
                : sheetCentreY;

            if (existing)
            {
                var staging = new Rectangle
                {
                    Width = sheetW,
                    Height = sheetH,
                    Stroke = AvTokens.Br(AvTokens.BorderStrong),
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                };
                Canvas.SetLeft(staging, centreX - sheetW / 2.0);
                Canvas.SetTop(staging, pageCentreY - sheetH / 2.0);
                canvas.Children.Add(staging);

                canvas.DrawCaption(
                    "Placed here - move into the sheet above",
                    centreX - sheetW / 2.0 + 4, pageCentreY - sheetH / 2.0 + 2);
            }

            foreach (var pv in page.Views)
            {
                var is3d = DetailKeys.Is3D(pv.Key);
                canvas.DrawElement(
                    centreX + pv.CenterX * px,
                    pageCentreY - pv.CenterY * px,
                    pv.DrawnWidth * px,
                    pv.DrawnHeight * px,
                    is3d ? LayoutPreviewCanvas.View3DFill : LayoutPreviewCanvas.View2DFill,
                    is3d ? LayoutPreviewCanvas.View3DStroke : LayoutPreviewCanvas.View2DStroke,
                    $"{pv.Key} 1:{pv.Scale}");
            }

            foreach (var (setIndex, top) in page.Bands)
            {
                canvas.DrawCaption(
                    sets[setIndex].Name,
                    centreX - frame.UsableWidth * px / 2.0 + 3,
                    pageCentreY - top * px + 1,
                    accent);
            }
        }

        return canvas;
    }

    // ── Small builders ───────────────────────────────────────────────────────

    private RadioButton NewRadio(string text, DetailTargetMode mode)
    {
        var rb = new RadioButton
        {
            Content = text,
            GroupName = "DetailTarget",
            FontSize = 12.5,
            Foreground = AvTokens.Br(AvTokens.Text),
            Margin = new Thickness(0, 0, 0, 8),
        };
        if (DetailControlStyles.Radio() is { } radioTemplate) rb.Template = radioTemplate;
        rb.Checked += (_, _) =>
        {
            if (_loading) return;

            _req.Mode = mode;
            _page = 0;
            UpdateTargetPanels();
            RefreshAll();
        };
        return rb;
    }

    private static TextBox NewTextBox() => new()
    {
        FontFamily = AvTokens.Ui,
        FontSize = 13,
        Foreground = AvTokens.Br(AvTokens.Text),
        Background = AvTokens.Br(AvTokens.Surface2),
        BorderBrush = AvTokens.Br(AvTokens.BorderStrong),
        BorderThickness = new Thickness(1),
        Padding = new Thickness(8, 0, 8, 0),
        Height = 34,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private static TextBlock Hint(string text) => new()
    {
        Text = text,
        FontSize = 11,
        Foreground = AvTokens.Br(AvTokens.TextFaint),
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 6, 0, 4),
    };

    private static void AddStep(StackPanel stack, int step, string title, Button box)
    {
        stack.Children.Add(AvControls.FieldLabel(step, title));
        box.Margin = new Thickness(0, 0, 0, 16);
        stack.Children.Add(box);
    }
}


// =============================================================================
// Brand-coloured radio buttons and tick boxes
// =============================================================================

/// <summary>
/// Templates that give the window's radio buttons and tick boxes the brand red
/// when selected, drawn like AvControls.CheckRow's box (17 px, white tick on an
/// Accent fill). A template that fails to parse yields null and the control
/// keeps its default look rather than stopping the window opening.
/// </summary>
internal static class DetailControlStyles
{
    private const string Ns =
        "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
        "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'";

    private static readonly Lazy<ControlTemplate?> RadioTemplate = new(() => Parse(
        $@"<ControlTemplate {Ns} TargetType='RadioButton'>
  <StackPanel x:Name='Root' Orientation='Horizontal' Background='Transparent' Cursor='Hand'>
    <Grid Width='17' Height='17' VerticalAlignment='Center'>
      <Ellipse x:Name='Ring' Stroke='{AvTokens.BorderStrong}' StrokeThickness='1.5' Fill='{AvTokens.Surface}'/>
      <Ellipse x:Name='Dot' Width='9' Height='9' Fill='{AvTokens.Accent}' Visibility='Hidden'/>
    </Grid>
    <ContentPresenter Margin='9,0,0,0' VerticalAlignment='Center' RecognizesAccessKey='True'/>
  </StackPanel>
  <ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'>
      <Setter TargetName='Ring' Property='Stroke' Value='{AvTokens.Accent}'/>
    </Trigger>
    <Trigger Property='IsChecked' Value='True'>
      <Setter TargetName='Ring' Property='Stroke' Value='{AvTokens.Accent}'/>
      <Setter TargetName='Dot' Property='Visibility' Value='Visible'/>
    </Trigger>
    <Trigger Property='IsEnabled' Value='False'>
      <Setter TargetName='Root' Property='Opacity' Value='0.5'/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>"));

    private static readonly Lazy<ControlTemplate?> CheckTemplate = new(() => Parse(
        $@"<ControlTemplate {Ns} TargetType='CheckBox'>
  <StackPanel x:Name='Root' Orientation='Horizontal' Background='Transparent' Cursor='Hand'>
    <Border x:Name='Box' Width='17' Height='17' CornerRadius='4' BorderThickness='1.5'
            BorderBrush='{AvTokens.BorderStrong}' Background='{AvTokens.Surface}' VerticalAlignment='Center'>
      <TextBlock x:Name='Mark' Text='✓' FontSize='11' FontWeight='Bold' Foreground='White'
                 HorizontalAlignment='Center' VerticalAlignment='Center' Visibility='Hidden'/>
    </Border>
    <ContentPresenter Margin='8,0,0,0' VerticalAlignment='Center' RecognizesAccessKey='True'/>
  </StackPanel>
  <ControlTemplate.Triggers>
    <Trigger Property='IsMouseOver' Value='True'>
      <Setter TargetName='Box' Property='BorderBrush' Value='{AvTokens.Accent}'/>
    </Trigger>
    <Trigger Property='IsChecked' Value='True'>
      <Setter TargetName='Box' Property='Background' Value='{AvTokens.Accent}'/>
      <Setter TargetName='Box' Property='BorderBrush' Value='{AvTokens.Accent}'/>
      <Setter TargetName='Mark' Property='Visibility' Value='Visible'/>
      <Setter TargetName='Root' Property='TextElement.Foreground' Value='{AvTokens.Accent}'/>
      <Setter TargetName='Root' Property='TextElement.FontWeight' Value='SemiBold'/>
    </Trigger>
    <Trigger Property='IsEnabled' Value='False'>
      <Setter TargetName='Root' Property='Opacity' Value='0.5'/>
    </Trigger>
  </ControlTemplate.Triggers>
</ControlTemplate>"));

    public static ControlTemplate? Radio() => RadioTemplate.Value;

    public static ControlTemplate? Check() => CheckTemplate.Value;

    private static ControlTemplate? Parse(string xaml)
    {
        try { return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml); }
        catch { return null; }
    }
}


// =============================================================================
// Extra-sheets prompt
// =============================================================================

internal static class DetailPrompt
{
    /// <summary>True to add extra sheets, false to go back and untick views.</summary>
    public static bool AskExtraSheets(IntPtr revitHandle, int sheetCount)
    {
        var window = new DetailChoiceWindow(
            "Detail Views",
            $"These sets do not fit on one sheet - {sheetCount} sheets are needed.\n\n" +
            "Add extra sheets, or go back and untick some views?",
            "Add extra sheets", "Go back", revitHandle);
        window.ShowDialog();
        return window.Accepted;
    }
}

internal sealed class DetailChoiceWindow : Window
{
    public bool Accepted { get; private set; }

    public DetailChoiceWindow(
        string title, string message, string acceptText, string declineText, IntPtr revitHandle)
    {
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        MaxWidth = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow;
        ShowInTaskbar = false;
        UseLayoutRounding = true;
        FontFamily = AvTokens.Ui;
        Background = AvTokens.Br(AvTokens.Surface);

        if (revitHandle != IntPtr.Zero)
            _ = new WindowInteropHelper(this) { Owner = revitHandle };

        var icon = new Border
        {
            Width = 36,
            Height = 36,
            Background = AvTokens.B(AvTokens.WarnBg),
            CornerRadius = new CornerRadius(18),
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = "!",
                FontSize = 17,
                FontWeight = FontWeights.Bold,
                Foreground = AvTokens.B(AvTokens.WarnFg),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };

        var text = new TextBlock
        {
            Text = message,
            FontSize = 13,
            Foreground = AvTokens.B(AvTokens.Text),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            MaxWidth = 400,
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(20, 20, 20, 16),
        };
        content.Children.Add(icon);
        content.Children.Add(text);

        var decline = AvControls.GhostBtn(declineText, () => { Accepted = false; Close(); });
        decline.Margin = new Thickness(0, 0, 8, 0);
        var accept = AvControls.PrimaryBtn(acceptText, () => { Accepted = true; Close(); });

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(decline);
        buttons.Children.Add(accept);

        var footer = new Border
        {
            Background = AvTokens.B(AvTokens.Surface2),
            BorderBrush = AvTokens.B(AvTokens.Border),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 12, 20, 12),
            Child = buttons,
        };

        var root = new StackPanel();
        root.Children.Add(content);
        root.Children.Add(footer);
        Content = root;
    }
}
