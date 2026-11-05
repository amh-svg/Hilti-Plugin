// -----------------------------------------------------------------------
// Pulldown Title: Auto Tag
// Sub-buttons:
//   - MT Tag: Lets the user pick a Generic Model tag family and one or
//     more 3D views on sheets, then auto-places tags on view elements
//     with collision-avoiding placement (edge clearance, spacing, and
//     tall/wide element handling).
//   - Vendor Tag: Same batch view/tag picker and auto-placement logic as
//     MT Tag, also targeting Generic Model tag families.
// Both windows have a Tag Density slider (10-100%, steps of 10) that sets
// what share of the elements get tagged, largest first. The last value is
// remembered per button. Defaults: MT 100%, Vendor 30%.
// -----------------------------------------------------------------------
// Autodesk
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using AVES.Core;
// System
using System;
using System.Collections.Generic;
using System.Linq;
// WPF
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using WpfBrush = System.Windows.Media.SolidColorBrush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfColor = System.Windows.Media.Color;
using WpfColorConv = System.Windows.Media.ColorConverter;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfFontFamily = System.Windows.Media.FontFamily;
using WpfGrid = System.Windows.Controls.Grid;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfVisibility = System.Windows.Visibility;

namespace AVES.Commands
{
    // ============================================================
    //  SHARED UI HELPERS
    // ============================================================
    internal static class W
    {
        // Geometry routed to AvControls.StatusPill; "warn" keeps this file's
        // own "Warn" wording via labelOverride (reconciliation decision:
        // unify styling, keep existing per-file wording). Also fixes the
        // previously-flagged missing VerticalAlignment on the outer Border,
        // for free, since Core's method sets it.
        public static Border StatusPill(string kind) =>
            AvControls.StatusPill(kind, kind == "warn" ? "Warn" : null);
    }

    // ============================================================
    //  TAG FAMILY MEMORY
    //  Remembers the last tag family picked per category (MT vs
    //  Vendor use different BuiltInCategory values) so returning
    //  users get it pre-selected next time. Stored per-Windows-user
    //  in %AppData%\AVES\last_tag_family.txt as "Category<TAB>Label" lines.
    // ============================================================
    internal static class TagFamilyMemory
    {
        private static readonly string ConfigDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVES");

        private static readonly string ConfigFile =
            System.IO.Path.Combine(ConfigDir, "last_tag_family.txt");

        internal static string? TryGet(BuiltInCategory cat)
        {
            try
            {
                if (!System.IO.File.Exists(ConfigFile)) return null;
                string prefix = cat.ToString() + "\t";
                foreach (string line in System.IO.File.ReadAllLines(ConfigFile))
                    if (line.StartsWith(prefix)) return line.Substring(prefix.Length);
            }
            catch { }
            return null;
        }

        internal static void Save(BuiltInCategory cat, string label)
        {
            try
            {
                System.IO.Directory.CreateDirectory(ConfigDir);
                string prefix = cat.ToString() + "\t";
                var lines = System.IO.File.Exists(ConfigFile)
                    ? System.IO.File.ReadAllLines(ConfigFile).Where(l => !l.StartsWith(prefix)).ToList()
                    : new List<string>();
                lines.Add(prefix + label);
                System.IO.File.WriteAllLines(ConfigFile, lines);
            }
            catch { /* Non-fatal — worst case the user just picks again next run. */ }
        }
    }

    // ============================================================
    //  TAG DENSITY
    //  The "% of elements to tag" slider shared by MT and Vendor.
    //  Memory is keyed per button ("MT" / "Vendor") and stored per-user in
    //  %AppData%\AVES\last_tag_percent.txt as "Key<TAB>Percent" lines.
    // ============================================================
    internal static class TagDensity
    {
        public const int Min = 10, Max = 100, Step = 10;

        public static int Clamp(int pct) => Math.Max(Min, Math.Min(Max, pct));

        /// <summary>
        /// How many of <paramref name="total"/> elements to tag. Groups of 1-2 are
        /// always tagged in full; otherwise ceil(total * pct / 100), at least 1.
        /// Integer arithmetic on purpose: ceil(10 * 0.30) can land on 4 in doubles.
        /// </summary>
        public static int PickCount(int total, int pct)
        {
            if (total <= 2) return total;
            int n = (total * Clamp(pct) + 99) / 100;
            return Math.Max(1, Math.Min(total, n));
        }

        /// <summary>The PickCount largest items by <paramref name="area"/>.</summary>
        public static List<T> SelectLargest<T>(IList<T> items, int pct, Func<T, double> area) =>
            items.OrderByDescending(area).Take(PickCount(items.Count, pct)).ToList();

        private static readonly string ConfigFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AVES", "last_tag_percent.txt");

        internal static int? TryGet(string key)
        {
            try
            {
                if (!System.IO.File.Exists(ConfigFile)) return null;
                string prefix = key + "\t";
                foreach (string line in System.IO.File.ReadAllLines(ConfigFile))
                    if (line.StartsWith(prefix) && int.TryParse(line.Substring(prefix.Length), out int v))
                        return Clamp(v);
            }
            catch { }
            return null;
        }

        internal static void Save(string key, int pct)
        {
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ConfigFile)!);
                string prefix = key + "\t";
                var lines = System.IO.File.Exists(ConfigFile)
                    ? System.IO.File.ReadAllLines(ConfigFile).Where(l => !l.StartsWith(prefix)).ToList()
                    : new List<string>();
                lines.Add(prefix + pct);
                System.IO.File.WriteAllLines(ConfigFile, lines);
            }
            catch { /* Non-fatal — worst case the slider resets to its default. */ }
        }
    }

    // ============================================================
    //  WINDOW 1 — BATCH MULTI-SELECT 3D VIEW PICKER
    //  Lists every 3D view that lives on any sheet, grouped by
    //  sheet. Mouse-wheel scrollable. Click a row to toggle its
    //  checkbox. Live search filters by view name or sheet number.
    //  "Select all" / "Deselect all" operate on the current filter.
    //  Real-time counter + confirm button in footer.
    // ============================================================
    // ============================================================
    //  TAG FAMILY PICKER WINDOW (modal popup, replaces the old
    //  button-anchored dropdown for aesthetic/behavior consistency
    //  with the rest of the AVES picker windows)
    // ============================================================
    internal sealed class TagFamilyPickerWindow : Window
    {
        public string? SelectedLabel { get; private set; }

        private readonly List<string> _allLabels;
        private readonly string? _currentLabel;
        private WpfTextBox _searchBox = null!;
        private ListBox _listBox = null!;
        private Button _selectBtn = null!;

        public TagFamilyPickerWindow(string title, List<string> labels, string? currentLabel)
        {
            _allLabels = labels;
            _currentLabel = currentLabel;

            Title = title;
            Width = 480; Height = 560;
            // ToolWindow gives a close-only title bar while KEEPING resize.
            // ResizeMode.NoResize also hides minimize/maximize but takes resizing
            // with it, which a long scrolling list needs.
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Background = AvTokens.B(AvTokens.Surface); FontFamily = AvTokens.Ui;
            UseLayoutRounding = true; SnapsToDevicePixels = true;

            var root = new DockPanel();

            // HeaderStrip, not TitleBar: matches TemplatePickerWindow, which is
            // the reference for this popup's styling.
            var tb = AvControls.HeaderStrip(title);
            DockPanel.SetDock(tb, Dock.Top);
            root.Children.Add(tb);

            var footer = BuildFooter();
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var body = new DockPanel { Margin = new Thickness(20, 16, 20, 0) };

            var search = BuildSearchBox();
            DockPanel.SetDock(search, Dock.Top);
            body.Children.Add(search);

            _listBox = new ListBox
            {
                FontFamily = AvTokens.Ui,
                FontSize = 13,
                BorderThickness = new Thickness(0),
                Background = WpfBrushes.Transparent,
                Focusable = true
            };
            // Was a local copy, byte-identical to DOC04_AutoDimension.cs's. Now the
            // shared Core style — same padding, row template and selected
            // treatment as before, plus a hover tint that neither copy had.
            _listBox.ItemContainerStyle = AvControls.PickerListItemStyle();
            _listBox.SelectionChanged += (s, e) => _selectBtn.IsEnabled = _listBox.SelectedItem != null;
            _listBox.MouseDoubleClick += (s, e) => { if (_listBox.SelectedItem != null) OnSelect(); };

            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _listBox,
                Focusable = false
            };
            scroll.PreviewMouseWheel += (s, e) =>
            {
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta * 0.5);
                e.Handled = true;
            };

            var listBorder = new Border
            {
                Background = AvTokens.B(AvTokens.Surface),
                BorderBrush = AvTokens.B(AvTokens.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 10, 0, 16),
                Child = scroll
            };
            body.Children.Add(listBorder);
            root.Children.Add(body);
            Content = root;

            RefreshList();

            Loaded += (s, e) => _searchBox.Focus();
            KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape) DialogResult = false;
                if (e.Key == System.Windows.Input.Key.Enter && _selectBtn.IsEnabled) OnSelect();
            };
        }

        private Border BuildSearchBox()
        {
            var g = new WpfGrid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new TextBlock
            {
                Text = "\u2315",
                FontSize = 15,
                Foreground = AvTokens.B(AvTokens.TextFaint),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            WpfGrid.SetColumn(icon, 0);

            _searchBox = new WpfTextBox
            {
                FontFamily = AvTokens.Ui,
                FontSize = 13,
                Foreground = AvTokens.B(AvTokens.Text),
                Background = WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0, 8, 10, 8),
                ToolTip = "Filter tag families by name"
            };
            _searchBox.TextChanged += (s, e) => RefreshList();
            WpfGrid.SetColumn(_searchBox, 1);

            g.Children.Add(icon); g.Children.Add(_searchBox);
            return new Border
            {
                Background = AvTokens.B(AvTokens.Surface2),
                BorderBrush = AvTokens.B(AvTokens.BorderStrong),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Child = g
            };
        }

        private UIElement BuildFooter()
        {
            var footerBorder = new Border
            {
                Background = AvTokens.B(AvTokens.Surface2),
                BorderBrush = AvTokens.B(AvTokens.Border),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(20, 14, 20, 14)
            };

            var inner = new DockPanel { LastChildFill = false };

            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(btns, Dock.Right);

            var cancelBtn = AvControls.GhostButton("Cancel");
            cancelBtn.Margin = new Thickness(0, 0, 10, 0);
            cancelBtn.Click += (s, e) => DialogResult = false;

            _selectBtn = AvControls.PrimaryButton("Select");
            _selectBtn.IsEnabled = false;
            _selectBtn.Click += (s, e) => OnSelect();

            btns.Children.Add(cancelBtn); btns.Children.Add(_selectBtn);
            inner.Children.Add(btns);
            footerBorder.Child = inner;
            return footerBorder;
        }

        private bool _initialSelectionApplied;

        private void RefreshList()
        {
            string q = (_searchBox.Text ?? "").Trim().ToLowerInvariant();
            string? previouslySelected = _listBox.SelectedItem as string;

            var filtered = string.IsNullOrEmpty(q)
                ? _allLabels
                : _allLabels.Where(l => l.ToLowerInvariant().Contains(q)).ToList();

            _listBox.Items.Clear();
            foreach (var label in filtered) _listBox.Items.Add(label);

            string? toSelect = previouslySelected;
            if (!_initialSelectionApplied)
            {
                _initialSelectionApplied = true;
                toSelect = _currentLabel;
            }

            if (toSelect != null && filtered.Contains(toSelect))
            {
                _listBox.SelectedItem = toSelect;
                _listBox.ScrollIntoView(toSelect);
            }
            _selectBtn.IsEnabled = _listBox.SelectedItem != null;
        }

        private void OnSelect()
        {
            if (_listBox.SelectedItem is string label)
            {
                SelectedLabel = label;
                DialogResult = true;
            }
        }
    }


    internal sealed class BatchViewPickerWindow : Window
    {
        public List<View3D> SelectedViews { get; } = new List<View3D>();

        public struct ViewEntry
        {
            public View3D View;
            public string SheetNum;
            public string SheetName;
            public string BadgeBg, BadgeFg, BadgeLabel;
        }

        private readonly List<ViewEntry> _all;
        private readonly List<(Border Row, WpfCheckBox Box, View3D View)> _visible
            = new List<(Border, WpfCheckBox, View3D)>();

        private WpfTextBox _searchBox = null!;
        private StackPanel _listPanel = null!;
        private TextBlock _counterLbl = null!;
        private Button _confirmBtn = null!;
        private int _checkedCount;

        // Tag family section (moved in from the former TagFamilyPickerWindow popup)
        private List<(string Label, ElementId Id)> _tagFamilies = null!;
        private Button _tagFamilyBtn = null!;
        private ElementId _selectedTagFamilyId = ElementId.InvalidElementId;
        public ElementId SelectedTagFamilyId => _selectedTagFamilyId;

        // Tag density section (% of elements tagged per assembly)
        private readonly string _densityKey;
        private Slider _densitySlider = null!;
        private TextBlock _densityLbl = null!;
        public int SelectedPercent => TagDensity.Clamp((int)Math.Round(_densitySlider.Value));

        // confirmLabel is injected so both MT and Vendor can share this window
        // with their own footer button text. tagCat/tagSectionLabel let each
        // button load its own tag family list (MultiCategoryTags vs GenericModelTags)
        // into the same in-window picker. densityKey/defaultPercent give each
        // button its own remembered slider value and default.
        public BatchViewPickerWindow(
            List<ViewEntry> entries, Document doc, BuiltInCategory tagCat, string tagSectionLabel,
            string densityKey, int defaultPercent,
            string confirmLabel = "▶  Run Script")
        {
            _all = entries;
            _densityKey = densityKey;

            Title = "Batch Tag";
            Width = 520; Height = 720;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = AvTokens.B(AvTokens.Surface); FontFamily = AvTokens.Ui;
            UseLayoutRounding = true; SnapsToDevicePixels = true;

            var root = new DockPanel();

            var tb = AvControls.TitleBar("Batch Tag", "");
            DockPanel.SetDock(tb, Dock.Top);
            root.Children.Add(tb);

            var footer = BuildFooter(confirmLabel);
            DockPanel.SetDock(footer, Dock.Bottom);
            root.Children.Add(footer);

            var body = new DockPanel { Margin = new Thickness(20, 16, 20, 0) };

            var tagSection = BuildTagFamilySection(doc, tagCat, tagSectionLabel);
            DockPanel.SetDock(tagSection, Dock.Top);
            body.Children.Add(tagSection);

            var densitySection = BuildDensitySection(defaultPercent);
            DockPanel.SetDock(densitySection, Dock.Top);
            body.Children.Add(densitySection);

            var viewsSectionTitle = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 10) };
            viewsSectionTitle.Children.Add(AvControls.NumberBadge(3));
            viewsSectionTitle.Children.Add(new TextBlock
            {
                Text = "Select Views",
                FontFamily = AvTokens.Ui,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = AvTokens.B(AvTokens.TextFaint),
                VerticalAlignment = VerticalAlignment.Center
            });
            DockPanel.SetDock(viewsSectionTitle, Dock.Top);
            body.Children.Add(viewsSectionTitle);

            var search = BuildSearchBox();
            DockPanel.SetDock(search, Dock.Top);
            body.Children.Add(search);

            var toolbar = BuildToolbar();
            DockPanel.SetDock(toolbar, Dock.Top);
            body.Children.Add(toolbar);

            _listPanel = new StackPanel();
            var scroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = _listPanel,
                Focusable = true
            };
            scroll.PreviewMouseWheel += (s, e) =>
            {
                scroll.ScrollToVerticalOffset(scroll.VerticalOffset - e.Delta * 0.5);
                e.Handled = true;
            };

            var listBorder = new Border
            {
                Background = AvTokens.B(AvTokens.Surface),
                BorderBrush = AvTokens.B(AvTokens.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 0, 0, 16),
                Child = scroll
            };
            body.Children.Add(listBorder);
            root.Children.Add(body);
            Content = root;

            Loaded += (s, e) => { ApplyFilter(); _searchBox.Focus(); };
            KeyDown += (s, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Escape) DialogResult = false;
                if (e.Key == System.Windows.Input.Key.Enter && _confirmBtn.IsEnabled) OnConfirm();
            };
        }

        private Border BuildTagFamilySection(Document doc, BuiltInCategory tagCat, string sectionLabel)
        {
            _tagFamilies = new FilteredElementCollector(doc)
                .OfClass(typeof(FamilySymbol))
                .OfCategory(tagCat)
                .Cast<FamilySymbol>()
                .OrderBy(fs => fs.FamilyName)
                .ThenBy(fs => fs.Name)
                .Select(fs => (Label: fs.FamilyName + "  ·  " + fs.Name, Id: fs.Id))
                .ToList();

            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            titleRow.Children.Add(AvControls.NumberBadge(1));
            titleRow.Children.Add(new TextBlock
            {
                Text = sectionLabel,
                FontFamily = AvTokens.Ui,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = AvTokens.B(AvTokens.TextFaint),
                VerticalAlignment = VerticalAlignment.Center
            });
            stack.Children.Add(titleRow);

            bool hasFamilies = _tagFamilies.Count > 0;

            _tagFamilyBtn = new Button
            {
                FontFamily = AvTokens.Ui,
                FontSize = 13,
                Foreground = AvTokens.B(AvTokens.TextFaint),
                Background = AvTokens.B(AvTokens.Surface),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(10, 8, 10, 8),
                Cursor = System.Windows.Input.Cursors.Hand,
                Content = hasFamilies ? "Select Tag Family\u2026" : "No tag families of this type are loaded in the project.",
                IsEnabled = hasFamilies,
                // Left, explicitly: the template's ContentPresenter alignment
                // overrides the button's own HorizontalContentAlignment above, so
                // taking the Center default here silently centred the selection.
                Template = AvControls.FieldButtonTemplate(HorizontalAlignment.Left),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // Pre-select the last family remembered for this tag category, if it
            // still exists in this project.
            if (hasFamilies)
            {
                string? rememberedLabel = TagFamilyMemory.TryGet(tagCat);
                int rememberedIdx = rememberedLabel == null
                    ? -1 : _tagFamilies.FindIndex(f => f.Label == rememberedLabel);
                if (rememberedIdx >= 0)
                {
                    var remembered = _tagFamilies[rememberedIdx];
                    _selectedTagFamilyId = remembered.Id;
                    _tagFamilyBtn.Content = remembered.Label;
                    _tagFamilyBtn.Foreground = AvTokens.B(AvTokens.Text);
                }
            }

            _tagFamilyBtn.Click += (s, e) =>
            {
                var picker = new TagFamilyPickerWindow(
                    sectionLabel,
                    _tagFamilies.Select(f => f.Label).ToList(),
                    _tagFamilyBtn.Content as string == "Select Tag Family\u2026" ? null : _tagFamilyBtn.Content as string)
                { Owner = this };

                if (picker.ShowDialog() == true && picker.SelectedLabel != null)
                {
                    var picked = _tagFamilies.First(f => f.Label == picker.SelectedLabel);
                    _selectedTagFamilyId = picked.Id;
                    _tagFamilyBtn.Content = picked.Label;
                    _tagFamilyBtn.Foreground = AvTokens.B(AvTokens.Text);
                    TagFamilyMemory.Save(tagCat, picked.Label);
                    UpdateCounter();
                }
            };

            stack.Children.Add(_tagFamilyBtn);
            return new Border { Child = stack };
        }

        private Border BuildDensitySection(int defaultPercent)
        {
            int initial = TagDensity.Clamp(TagDensity.TryGet(_densityKey) ?? defaultPercent);

            var stack = new StackPanel { Margin = new Thickness(0, 0, 0, 4) };

            // Title row: badge + label on the left, live value on the right.
            var titleRow = new DockPanel { Margin = new Thickness(0, 0, 0, 6), LastChildFill = true };
            _densityLbl = new TextBlock
            {
                Text = initial + "%",
                FontFamily = AvTokens.Ui,
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = AvTokens.B(AvTokens.Accent),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(_densityLbl, Dock.Right);
            titleRow.Children.Add(_densityLbl);

            var left = new StackPanel { Orientation = Orientation.Horizontal };
            left.Children.Add(AvControls.NumberBadge(2));
            left.Children.Add(new TextBlock
            {
                Text = "Tag Density",
                FontFamily = AvTokens.Ui,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = AvTokens.B(AvTokens.TextFaint),
                VerticalAlignment = VerticalAlignment.Center
            });
            titleRow.Children.Add(left);
            stack.Children.Add(titleRow);

            _densitySlider = BuildSlider(initial);
            _densitySlider.ValueChanged += (s, e) =>
                _densityLbl.Text = TagDensity.Clamp((int)Math.Round(_densitySlider.Value)) + "%";
            stack.Children.Add(_densitySlider);

            var ends = new DockPanel { Margin = new Thickness(2, 0, 2, 4) };
            var maxLbl = new TextBlock
            {
                Text = "more tags",
                FontFamily = AvTokens.Ui, FontSize = 11,
                Foreground = AvTokens.B(AvTokens.TextFaint)
            };
            DockPanel.SetDock(maxLbl, Dock.Right);
            ends.Children.Add(maxLbl);
            ends.Children.Add(new TextBlock
            {
                Text = "fewer tags",
                FontFamily = AvTokens.Ui, FontSize = 11,
                Foreground = AvTokens.B(AvTokens.TextFaint)
            });
            stack.Children.Add(ends);
            return new Border { Child = stack };
        }

        // Accent-coloured slider: filled track left of the thumb, grey right of it.
        private static Slider BuildSlider(int value)
        {
            string xaml =
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
                "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type Slider}'>" +
                "<Grid Height='24' Background='Transparent'>" +
                "<Track x:Name='PART_Track'>" +
                "<Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge' Focusable='False' Background='" + AvTokens.Accent + "'>" +
                "<RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'>" +
                "<Border Background='{TemplateBinding Background}' Height='4' CornerRadius='2' VerticalAlignment='Center'/>" +
                "</ControlTemplate></RepeatButton.Template></RepeatButton></Track.DecreaseRepeatButton>" +
                "<Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge' Focusable='False' Background='" + AvTokens.BorderStrong + "'>" +
                "<RepeatButton.Template><ControlTemplate TargetType='{x:Type RepeatButton}'>" +
                "<Border Background='{TemplateBinding Background}' Height='4' CornerRadius='2' VerticalAlignment='Center'/>" +
                "</ControlTemplate></RepeatButton.Template></RepeatButton></Track.IncreaseRepeatButton>" +
                "<Track.Thumb><Thumb Width='18' Height='18' Cursor='Hand' Focusable='False'>" +
                "<Thumb.Template><ControlTemplate TargetType='{x:Type Thumb}'>" +
                "<Ellipse Fill='White' Stroke='" + AvTokens.Accent + "' StrokeThickness='2.5'/>" +
                "</ControlTemplate></Thumb.Template></Thumb></Track.Thumb>" +
                "</Track></Grid></ControlTemplate>";

            return new Slider
            {
                Minimum = TagDensity.Min,
                Maximum = TagDensity.Max,
                TickFrequency = TagDensity.Step,
                IsSnapToTickEnabled = true,
                SmallChange = TagDensity.Step,
                LargeChange = TagDensity.Step,
                Value = value,
                Focusable = true,
                Margin = new Thickness(0, 0, 0, 2),
                Template = (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml)
            };
        }

        private Border BuildSearchBox()
        {
            var g = new WpfGrid();
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var icon = new TextBlock
            {
                Text = "\u2315",
                FontSize = 15,
                Foreground = AvTokens.B(AvTokens.TextFaint),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            WpfGrid.SetColumn(icon, 0);

            _searchBox = new WpfTextBox
            {
                FontFamily = AvTokens.Ui,
                FontSize = 13,
                Foreground = AvTokens.B(AvTokens.TextFaint),
                Background = WpfBrushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                Padding = new Thickness(0, 8, 10, 8),
                Text = "Search by view or sheet\u2026"
            };
            _searchBox.GotFocus += (s, e) =>
            {
                if (_searchBox.Text == "Search by view or sheet\u2026")
                { _searchBox.Text = ""; _searchBox.Foreground = AvTokens.B(AvTokens.Text); }
            };
            _searchBox.LostFocus += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(_searchBox.Text))
                { _searchBox.Text = "Search by view or sheet\u2026"; _searchBox.Foreground = AvTokens.B(AvTokens.TextFaint); }
            };
            _searchBox.TextChanged += (s, e) => ApplyFilter();
            WpfGrid.SetColumn(_searchBox, 1);

            g.Children.Add(icon); g.Children.Add(_searchBox);
            return new Border
            {
                Background = AvTokens.B(AvTokens.Surface2),
                BorderBrush = AvTokens.B(AvTokens.BorderStrong),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 0, 0, 8),
                Child = g
            };
        }

        private UIElement BuildToolbar()
        {
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8), LastChildFill = false };

            var left = new StackPanel { Orientation = Orientation.Horizontal };

            var selAllBtn = AvControls.SecondaryButton("Select All");
            selAllBtn.FontSize = 12;
            selAllBtn.Foreground = AvTokens.B(AvTokens.Accent);
            selAllBtn.Padding = new Thickness(12, 5, 12, 5);
            selAllBtn.Click += (s, e) => SetAllVisible(true);

            var deselAllBtn = AvControls.SecondaryButton("Deselect All");
            deselAllBtn.FontSize = 12;
            deselAllBtn.Foreground = AvTokens.B(AvTokens.TextDim);
            deselAllBtn.Padding = new Thickness(12, 5, 12, 5);
            deselAllBtn.Margin = new Thickness(8, 0, 0, 0);
            deselAllBtn.Click += (s, e) => SetAllVisible(false);

            left.Children.Add(selAllBtn); left.Children.Add(deselAllBtn);
            DockPanel.SetDock(left, Dock.Left);
            bar.Children.Add(left);

            _counterLbl = new TextBlock
            {
                Text = "0 views selected",
                FontFamily = AvTokens.Ui,
                FontSize = 12,
                Foreground = AvTokens.B(AvTokens.TextDim),
                VerticalAlignment = VerticalAlignment.Center
            };
            DockPanel.SetDock(_counterLbl, Dock.Right);
            bar.Children.Add(_counterLbl);

            return bar;
        }

        private UIElement BuildFooter(string confirmLabel)
        {
            var footerBorder = new Border
            {
                Background = AvTokens.B(AvTokens.Surface2),
                BorderBrush = AvTokens.B(AvTokens.Border),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(20, 14, 20, 14)
            };

            var inner = new DockPanel { LastChildFill = false };

            var btns = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(btns, Dock.Right);

            var cancelBtn = AvControls.GhostButton("Cancel");
            cancelBtn.Margin = new Thickness(0, 0, 10, 0);
            cancelBtn.Click += (s, e) => DialogResult = false;

            _confirmBtn = AvControls.PrimaryButton(confirmLabel);
            _confirmBtn.IsEnabled = false;
            _confirmBtn.Click += (s, e) => OnConfirm();

            btns.Children.Add(cancelBtn); btns.Children.Add(_confirmBtn);
            inner.Children.Add(btns);
            footerBorder.Child = inner;
            return footerBorder;
        }

        private void ApplyFilter()
        {
            bool placeholder = _searchBox.Text == "Search by view or sheet\u2026";
            string q = placeholder ? "" : (_searchBox.Text ?? "").Trim().ToLowerInvariant();

            var filtered = string.IsNullOrEmpty(q)
                ? _all
                : _all.Where(e =>
                    e.View.Name.ToLowerInvariant().Contains(q) ||
                    e.SheetNum.ToLowerInvariant().Contains(q) ||
                    e.SheetName.ToLowerInvariant().Contains(q))
                  .ToList();

            var checkedIds = new HashSet<ElementId>(
                _visible.Where(v => v.Box.IsChecked == true).Select(v => v.View.Id));

            _visible.Clear();
            _listPanel.Children.Clear();

            if (filtered.Count == 0)
            {
                _listPanel.Children.Add(new TextBlock
                {
                    Text = "No views match.",
                    FontFamily = AvTokens.Ui,
                    FontSize = 12.5,
                    Foreground = AvTokens.B(AvTokens.TextFaint),
                    Margin = new Thickness(16, 14, 16, 14),
                    HorizontalAlignment = HorizontalAlignment.Center
                });
                return;
            }

            var sorted = filtered.OrderBy(e => e.View.Name).ToList();

            bool firstRow = true;
            foreach (var entry in sorted)
            {
                var (rowBorder, box) = AvControls.CheckRow(entry.View.Name, firstRow);
                firstRow = false;

                if (checkedIds.Contains(entry.View.Id)) box.IsChecked = true;

                bool isChecked = box.IsChecked == true;
                box.Checked += (s, e) => { if (!isChecked) { isChecked = true; _checkedCount++; UpdateCounter(); } };
                box.Unchecked += (s, e) => { if (isChecked) { isChecked = false; _checkedCount--; UpdateCounter(); } };

                var capturedView = entry.View;
                rowBorder.MouseLeftButtonUp += (s, e) => { box.IsChecked = !box.IsChecked; };

                _listPanel.Children.Add(rowBorder);
                _visible.Add((rowBorder, box, capturedView));
            }

            _checkedCount = _visible.Count(v => v.Box.IsChecked == true);
            UpdateCounter();
        }

        private void SetAllVisible(bool check)
        {
            foreach (var (_, box, _) in _visible) box.IsChecked = check;
            _checkedCount = check ? _visible.Count : 0;
            UpdateCounter();
        }

        private void UpdateCounter()
        {
            _counterLbl.Text = _checkedCount == 1 ? "1 view selected"
                                                   : _checkedCount + " views selected";
            _confirmBtn.IsEnabled = _checkedCount > 0 && _selectedTagFamilyId != ElementId.InvalidElementId;
        }

        private void OnConfirm()
        {
            SelectedViews.Clear();
            foreach (var (_, box, view) in _visible)
                if (box.IsChecked == true) SelectedViews.Add(view);
            TagDensity.Save(_densityKey, SelectedPercent);
            DialogResult = true;
        }

        // Collect 3D views that live on at least one sheet
        public static List<ViewEntry> CollectEntries(Document doc)
        {
            var sheetByView = new Dictionary<ElementId, (string Num, string Name)>();
            foreach (ViewSheet vs in new FilteredElementCollector(doc)
                                         .OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
                foreach (ElementId vpId in vs.GetAllViewports())
                    if (doc.GetElement(vpId) is Viewport vp)
                        sheetByView[vp.ViewId] = (vs.SheetNumber, vs.Name);

            var allViews = new FilteredElementCollector(doc)
                .OfClass(typeof(View3D)).Cast<View3D>()
                .Where(v => !v.IsTemplate && sheetByView.ContainsKey(v.Id))
                .ToList();

            var entries = new List<ViewEntry>();
            foreach (var v in allViews)
            {
                var si = sheetByView[v.Id];
                string bbg, bfg, blbl;
                if (v.IsAssemblyView) { bbg = AvTokens.InfoBg; bfg = AvTokens.InfoFg; blbl = "Assembly"; }
                else { bbg = AvTokens.SkipBg; bfg = AvTokens.SkipFg; blbl = "3D"; }
                entries.Add(new ViewEntry
                {
                    View = v,
                    SheetNum = si.Num,
                    SheetName = si.Name,
                    BadgeBg = bbg,
                    BadgeFg = bfg,
                    BadgeLabel = blbl
                });
            }
            return entries.OrderBy(e => e.SheetNum).ThenBy(e => e.View.Name).ToList();
        }
    }

    // ============================================================
    //  VIEW LOG ROW
    // ============================================================
    internal sealed class ViewLogRow
    {
        public string ViewName { get; set; } = "";
        public string SheetNum { get; set; } = "";
        public string Status { get; set; } = "";
        public int Created { get; set; }
        public int Skipped { get; set; }
        public string Notes { get; set; } = "";

        /// <summary>
        /// Owning assembly's instance name, or empty for a 3D view that is not an
        /// assembly view. Captured while the command loop still has the Document
        /// in scope: this row keeps only the view's NAME, so there is no way back
        /// to the View element later to ask.
        /// </summary>
        public string AssemblyName { get; set; } = "";
    }

    /// <summary>
    /// One grid row per assembly, aggregated from that assembly's ViewLogRows --
    /// or one row for a single view that belongs to no assembly, in which case
    /// AssemblyName carries the view's own name and nothing is aggregated.
    ///
    /// Display model only. ViewLogRow stays the source of truth, and the
    /// clipboard payload is still built from the per-view list, so nothing is
    /// lost -- it is just not all visible in the grid at once.
    /// </summary>
    internal sealed class AssemblyLogRow
    {
        public string AssemblyName { get; set; } = "";
        public string Status { get; set; } = "";
        public int ViewCount { get; set; }
        public int Created { get; set; }
        public int Skipped { get; set; }
        public string Notes { get; set; } = "";
    }

    // ============================================================
    //  WINDOW 2 — RESULTS REPORT
    // ============================================================
    internal sealed class AutoTagResultsWindow : Window
    {
        public AutoTagResultsWindow(
            int totalCreated, int totalSkipped, int viewsProcessed,
            List<ViewLogRow> rows, string windowTitle = "Auto Tag \u2014 Results")
        {
            string tsvText = BuildTsv(rows);

            Title = windowTitle;
            Width = 680; MaxHeight = 660;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = AvTokens.B(AvTokens.Surface); FontFamily = AvTokens.Ui;
            UseLayoutRounding = true; SnapsToDevicePixels = true;

            var root = new StackPanel();
            root.Children.Add(AvControls.TitleBar(windowTitle));

            var body = new StackPanel { Margin = new Thickness(20, 20, 20, 20) };
            root.Children.Add(new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = body
            });

            body.Children.Add(AvControls.SectionLabel("Run Summary"));
            var tileGrid = new UniformGrid { Columns = 3, Rows = 1, Margin = new Thickness(0, 0, 0, 20) };
            foreach (var tile in new[]
            {
                AvControls.StatTile(totalCreated.ToString(),   "Tags created",    totalCreated  > 0 ? "ok"   : ""),
                AvControls.StatTile(totalSkipped.ToString(),   "Skipped",         totalSkipped  > 0 ? "warn" : ""),
                AvControls.StatTile(viewsProcessed.ToString(), "Views processed", ""),
            })
            { tile.Margin = new Thickness(0, 0, 8, 8); tileGrid.Children.Add(tile); }
            body.Children.Add(tileGrid);

            body.Children.Add(AvControls.SectionLabel("Results per view"));
            body.Children.Add(BuildResultsTable(rows));

            root.Children.Add(AvControls.HDivider());
            var footer = new DockPanel { Background = AvTokens.B(AvTokens.Surface2), LastChildFill = false };

            var caption = new TextBlock
            {
                Text = string.Format("{0} view(s) processed", viewsProcessed),
                FontFamily = AvTokens.Ui,
                FontSize = 11.5,
                Foreground = AvTokens.B(AvTokens.TextFaint),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(20, 14, 10, 14)
            };
            DockPanel.SetDock(caption, Dock.Left);
            footer.Children.Add(caption);

            var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 14, 20, 14) };
            DockPanel.SetDock(btnPanel, Dock.Right);

            var copyBtn = AvControls.SecondaryButton("\u29c9  Copy to clipboard");
            copyBtn.Margin = new Thickness(0, 0, 10, 0);
            DispatcherTimer? copyTimer = null;
            copyBtn.Click += (s, e) =>
            {
                try { Clipboard.SetText(tsvText); } catch { }
                copyBtn.Content = "\u2713  Copied!"; copyBtn.IsEnabled = false;
                copyTimer?.Stop();
                copyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                copyTimer.Tick += (ts, te) =>
                { copyTimer.Stop(); copyBtn.Content = "\u29c9  Copy to clipboard"; copyBtn.IsEnabled = true; };
                copyTimer.Start();
            };
            var closeBtn = AvControls.PrimaryButton("Close");
            closeBtn.Click += (s, e) => Close();
            btnPanel.Children.Add(copyBtn); btnPanel.Children.Add(closeBtn);
            footer.Children.Add(btnPanel);
            root.Children.Add(footer);
            Content = root;
        }

        /// <summary>
        /// Collapses the per-view rows into one row per assembly.
        ///
        /// Status takes the worst case, where only "warn" demotes: any warn makes
        /// the assembly warn; otherwise an assembly whose views ALL skipped is
        /// skip; otherwise ok. A single empty view therefore does not drag down
        /// an otherwise clean assembly -- "skip" means nothing to do, not
        /// something failed -- and the Notes column records it either way.
        ///
        /// Created and Skipped are summed, matching how the stat tiles above the
        /// grid already compute their totals. The per-view exception text cannot
        /// survive that aggregation, which is why the clipboard payload is
        /// deliberately left ungrouped.
        ///
        /// Views belonging to no assembly keep a row each, named by the view.
        /// They have no grouping key, so merging them would be an arbitrary
        /// collapse rather than a grouping -- and it would hide exactly the
        /// detail that grouping cannot justify losing. They carry their own
        /// status, counts and notes through unchanged, and sort after the
        /// assemblies.
        /// </summary>
        private static List<AssemblyLogRow> GroupByAssembly(List<ViewLogRow> rows)
        {
            var assemblyRows = rows
                .Where(r => !string.IsNullOrWhiteSpace(r.AssemblyName))
                .GroupBy(r => r.AssemblyName)
                .Select(g =>
                {
                    int warnCount = g.Count(r => r.Status == "warn");
                    bool allSkipped = g.All(r => r.Status == "skip");

                    string status = warnCount > 0 ? "warn"
                                  : allSkipped ? "skip"
                                  : "ok";

                    string notes;
                    if (warnCount > 0)
                        notes = string.Format("{0} of {1} views had skips", warnCount, g.Count());
                    else if (allSkipped)
                        notes = g.Select(r => r.Notes)
                                 .FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "";
                    else
                        notes = "";

                    return new AssemblyLogRow
                    {
                        AssemblyName = g.Key,
                        Status = status,
                        ViewCount = g.Count(),
                        Created = g.Sum(r => r.Created),
                        Skipped = g.Sum(r => r.Skipped),
                        Notes = notes,
                    };
                })
                .OrderBy(r => r.AssemblyName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Straight pass-through: one row in, one row out, nothing aggregated.
            var looseViewRows = rows
                .Where(r => string.IsNullOrWhiteSpace(r.AssemblyName))
                .Select(r => new AssemblyLogRow
                {
                    AssemblyName = r.ViewName,
                    Status = r.Status,
                    ViewCount = 1,
                    Created = r.Created,
                    Skipped = r.Skipped,
                    Notes = r.Notes,
                })
                .OrderBy(r => r.AssemblyName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            assemblyRows.AddRange(looseViewRows);
            return assemblyRows;
        }

        /// <summary>
        /// Resolves a view's owning assembly instance name, or empty when the view
        /// is not an assembly view.
        /// </summary>
        internal static string ResolveAssemblyName(Document doc, View view)
        {
            try
            {
                ElementId id = view.AssemblyInstanceId;
                if (id == null || id == ElementId.InvalidElementId) return "";
                return doc.GetElement(id) is AssemblyInstance asm ? asm.Name : "";
            }
            catch { return ""; }
        }

        private static Border BuildResultsTable(List<ViewLogRow> viewRows)
        {
            var rows = GroupByAssembly(viewRows);

            var headerGrid = new WpfGrid { Background = AvTokens.B(AvTokens.Surface2) };
            DefineTableColumns(headerGrid);

            string[] headers = { "Status", "Assembly", "Views", "Created", "Skipped", "Notes" };
            for (int i = 0; i < headers.Length; i++)
            {
                var hdr = new TextBlock
                {
                    Text = headers[i],
                    FontFamily = AvTokens.Ui,
                    FontSize = 10.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = AvTokens.B(AvTokens.TextFaint),
                    Padding = new Thickness(i == 0 ? 14 : 12, 9, 12, 9)
                };
                WpfGrid.SetColumn(hdr, i);
                headerGrid.Children.Add(hdr);
            }

            var rowsPanel = new StackPanel();
            bool odd = false;
            foreach (var r in rows)
            {
                var rowGrid = new WpfGrid { Background = AvTokens.B(odd ? "#F7F8FA" : AvTokens.Surface) };
                DefineTableColumns(rowGrid);

                var pill = W.StatusPill(r.Status);
                pill.Margin = new Thickness(14, 0, 0, 0);
                pill.VerticalAlignment = VerticalAlignment.Center;
                WpfGrid.SetColumn(pill, 0); rowGrid.Children.Add(pill);

                var nameCell = MakeCell(r.AssemblyName, AvTokens.Ui, 12.5, AvTokens.Text, bold: true);
                WpfGrid.SetColumn(nameCell, 1); rowGrid.Children.Add(nameCell);

                var viewsCell = MakeCell(r.ViewCount.ToString(), AvTokens.Mono, 11.5, AvTokens.TextDim, centre: true);
                WpfGrid.SetColumn(viewsCell, 2); rowGrid.Children.Add(viewsCell);

                string createdFg = r.Created > 0 ? AvTokens.OkFg : AvTokens.TextFaint;
                var createdCell = MakeCell(r.Created > 0 ? r.Created.ToString() : "\u2014", AvTokens.Mono, 11.5, createdFg, centre: true);
                WpfGrid.SetColumn(createdCell, 3); rowGrid.Children.Add(createdCell);

                string skippedFg = r.Skipped > 0 ? AvTokens.WarnFg : AvTokens.TextFaint;
                var skippedCell = MakeCell(r.Skipped > 0 ? r.Skipped.ToString() : "\u2014", AvTokens.Mono, 11.5, skippedFg, centre: true);
                WpfGrid.SetColumn(skippedCell, 4); rowGrid.Children.Add(skippedCell);

                var notesCell = MakeCell(r.Notes, AvTokens.Ui, 11.5, AvTokens.TextFaint);
                WpfGrid.SetColumn(notesCell, 5); rowGrid.Children.Add(notesCell);

                rowsPanel.Children.Add(new Border
                {
                    BorderBrush = AvTokens.B(AvTokens.Border),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = rowGrid,
                    MinHeight = 42
                });
                odd = !odd;
            }

            var tableStack = new StackPanel();
            tableStack.Children.Add(new Border
            {
                BorderBrush = AvTokens.B(AvTokens.BorderStrong),
                BorderThickness = new Thickness(0, 0, 0, 1.5),
                Child = headerGrid
            });

            var dataScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 320,
                Content = rowsPanel
            };
            dataScroll.PreviewMouseWheel += (s, e) =>
            {
                dataScroll.ScrollToVerticalOffset(dataScroll.VerticalOffset - e.Delta * 0.5);
                e.Handled = true;
            };
            tableStack.Children.Add(dataScroll);

            return new Border
            {
                Background = AvTokens.B(AvTokens.Surface),
                BorderBrush = AvTokens.B(AvTokens.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Margin = new Thickness(0, 0, 0, 0),
                Child = tableStack
            };
        }

        private static void DefineTableColumns(WpfGrid g)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(68) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        }

        private static TextBlock MakeCell(
            string text, WpfFontFamily font, double size, string fg,
            bool bold = false, bool centre = false) => new TextBlock
            {
                Text = text,
                FontFamily = font,
                FontSize = size,
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = AvTokens.B(fg),
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = text,
                Padding = new Thickness(12, 11, 12, 11),
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = centre ? TextAlignment.Center : TextAlignment.Left
            };

        private static string BuildTsv(List<ViewLogRow> rows)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Status\tView\tSheet\tCreated\tSkipped\tNotes");
            foreach (var r in rows)
                sb.AppendLine(string.Format("{0}\t{1}\t{2}\t{3}\t{4}\t{5}",
                    r.Status, r.ViewName, r.SheetNum, r.Created, r.Skipped, r.Notes));
            return sb.ToString().TrimEnd();
        }
    }

    // ============================================================
    //  BUTTON 1 — CMD_MTTAG  (MT Drawings)
    // ============================================================
    [Transaction(TransactionMode.Manual)]
    public class Cmd_DOC05a_MT : IExternalCommand
    {
        private const double EdgeClearanceFt = 1.0;
        private const double MinTagSpacingFt = 0.40;
        private const bool LeaderEnabled = true;
        private const double TallRatio = 1.4;
        private const double TallTopBottomFraction = 0.05;
        private const double TallTopBottomMaxHeightFraction = 0.10;
        private const double WideTopFraction = 0.30;
        private const double WideTopMaxHeightFraction = 0.50;
        private const double WideBottomFraction = 0.05;
        private const double WideBottomMaxHeightFraction = 0.10;
        private const double WideElementFraction = 0.55;
        private const double ClearThreshold = 0.08;
        private const double ColumnVerticalPaddingFt = 0.5;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            // 1. Collect all 3D views on sheets
            var entries = BatchViewPickerWindow.CollectEntries(doc);
            if (entries.Count == 0)
            {
                AvControls.ShowAlert("Auto Tag", "No 3D views found on any sheet in this project.", AlertSeverity.Warning);
                return Result.Failed;
            }

            // 2. Single window: pick tag family (in-window section) + multi-select views
            var picker = new BatchViewPickerWindow(
                entries, doc, BuiltInCategory.OST_GenericModelTags,
                "MT Tag Family", "MT", 100, "▶  Run Script");
            if (picker.ShowDialog() != true
                || picker.SelectedViews.Count == 0
                || picker.SelectedTagFamilyId == ElementId.InvalidElementId)
                return Result.Cancelled;

            ElementId tagTypeId = picker.SelectedTagFamilyId;
            int tagPercent = picker.SelectedPercent;
            List<View3D> selectedViews = picker.SelectedViews;

            // 3. Tag each selected view, collect structured log rows
            int totalCreated = 0, totalSkipped = 0;
            var logRows = new List<ViewLogRow>();

            var sheetByViewId = new Dictionary<ElementId, string>();
            foreach (ViewSheet vs in new FilteredElementCollector(doc)
                                         .OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
                foreach (ElementId vpId in vs.GetAllViewports())
                    if (doc.GetElement(vpId) is Viewport vp)
                        sheetByViewId[vp.ViewId] = vs.SheetNumber;

            foreach (View3D view in selectedViews)
            {
                sheetByViewId.TryGetValue(view.Id, out string? sheetNum);
                var row = new ViewLogRow
                {
                    ViewName = view.Name,
                    SheetNum = sheetNum ?? "\u2014",
                    // Captured here because this is the last point at which the
                    // Document is in scope; the row itself keeps only the name.
                    AssemblyName = AutoTagResultsWindow.ResolveAssemblyName(doc, view)
                };

                List<Element> modelElements = CollectModelElements(doc, view);
                if (modelElements.Count == 0)
                {
                    row.Status = "skip"; row.Notes = "No taggable elements";
                    logRows.Add(row); continue;
                }

                GetViewAxes(view, out XYZ right, out XYZ up, out XYZ fwd);

                var elDataAll = new List<(Element El,
                    (double UMin, double UMax, double VMin, double VMax) Rect,
                    double Depth)>();
                foreach (Element el in modelElements)
                {
                    if (!TryGetScreenRect(el, view, right, up, out var rect)) continue;
                    elDataAll.Add((el, rect, GetElementDepth(el, view, fwd)));
                }

                if (elDataAll.Count == 0)
                {
                    row.Status = "skip"; row.Notes = "Could not project elements";
                    logRows.Add(row); continue;
                }

                // Tag Density slider: tag only the largest N% of the view's
                // elements. Assembly bounds still come from ALL elements so the
                // slot columns/rows sit at the true assembly edges. At 100% this
                // is the full set, i.e. the previous behaviour.
                var elData = TagDensity.SelectLargest(elDataAll, tagPercent,
                    e => (e.Rect.UMax - e.Rect.UMin) * (e.Rect.VMax - e.Rect.VMin));

                double asmUMin = elDataAll.Min(e => e.Rect.UMin);
                double asmUMax = elDataAll.Max(e => e.Rect.UMax);
                double asmVMin = elDataAll.Min(e => e.Rect.VMin);
                double asmVMax = elDataAll.Max(e => e.Rect.VMax);
                double medDepth = elDataAll.Select(e => e.Depth).OrderBy(d => d)
                                           .ElementAt(elDataAll.Count / 2);

                double colLeft = asmUMin - EdgeClearanceFt;
                double colRight = asmUMax + EdgeClearanceFt;
                double rowTop = asmVMax + EdgeClearanceFt;
                double rowBottom = asmVMin - EdgeClearanceFt;

                var (topFrac, topHFrac, botFrac, botHFrac, _) =
                    GetThresholds(asmUMin, asmUMax, asmVMin, asmVMax);

                var zoneMap = AssignZonesTwoPass(elData,
                    asmUMin, asmUMax, asmVMin, asmVMax,
                    topFrac, topHFrac, botFrac, botHFrac);

                var zones = new Dictionary<string, List<(Element El,
                    (double UMin, double UMax, double VMin, double VMax) Rect,
                    double Depth)>>
                {
                    ["LEFT"] = new List<(Element, (double, double, double, double), double)>(),
                    ["RIGHT"] = new List<(Element, (double, double, double, double), double)>(),
                    ["TOP"] = new List<(Element, (double, double, double, double), double)>(),
                    ["BOTTOM"] = new List<(Element, (double, double, double, double), double)>(),
                };
                foreach (var entry in elData) zones[zoneMap[entry.El.Id.Value]].Add(entry);

                var leaderMap = PrecomputeLeaderEndpoints(zones, right, up, fwd, view);
                var slotMap = BuildSlotMap(zones, colLeft, colRight, rowTop, rowBottom,
                                             asmUMin, asmUMax, asmVMin, asmVMax);
                var allOrdered = zones["RIGHT"].Concat(zones["LEFT"])
                                              .Concat(zones["TOP"])
                                              .Concat(zones["BOTTOM"]).ToList();

                int created = 0;
                var warnMsgs = new List<string>();
                var skippedIds = new List<long>();

                // Tx 1: lock the view (tags require a locked 3D view)
                using (var tLock = new Transaction(doc, "Lock view: " + view.Name))
                {
                    tLock.Start();
                    try { view.SaveOrientationAndLock(); } catch { }
                    tLock.Commit();
                }

                // Tx 2: place tags into the now-locked view
                using (var t = new Transaction(doc, "Auto Tag: " + view.Name))
                {
                    t.Start();
                    foreach (var entry in allOrdered)
                    {
                        Element el = entry.El;
                        try
                        {
                            long elKey = el.Id.Value;
                            var slot = slotMap[elKey];
                            XYZ head = FromScreen(slot.U, slot.V, medDepth, right, up, fwd);

                            if (!leaderMap.TryGetValue(elKey, out XYZ? lEnd) || lEnd == null)
                            { skippedIds.Add(elKey); continue; }

                            IndependentTag tag = IndependentTag.Create(
                                doc, tagTypeId, view.Id,
                                new Reference(el), LeaderEnabled,
                                TagOrientation.Horizontal, head);

                            if (tag == null) { skippedIds.Add(elKey); continue; }
                            try { tag.TagHeadPosition = head; } catch { }

                            if (LeaderEnabled)
                            {
                                try
                                {
                                    tag.LeaderEndCondition = LeaderEndCondition.Free;
                                    tag.SetLeaderEnd(new Reference(el), lEnd);
                                }
                                catch { }
                            }
                            created++;
                        }
                        catch (Exception ex)
                        {
                            skippedIds.Add(el.Id.Value);
                            warnMsgs.Add(ex.Message);
                        }
                    }
                    t.Commit();
                }

                row.Created = created;
                row.Skipped = skippedIds.Count;
                row.Status = skippedIds.Count == 0 ? "ok" : "warn";
                row.Notes = warnMsgs.Count > 0
                    ? warnMsgs[0] + (warnMsgs.Count > 1
                        ? string.Format(" (+{0} more)", warnMsgs.Count - 1) : "")
                    : "";
                logRows.Add(row);
                totalCreated += created;
                totalSkipped += skippedIds.Count;
            }

            new AutoTagResultsWindow(
                totalCreated, totalSkipped, selectedViews.Count, logRows,
                "Auto Tag \u2014 Results")
                .ShowDialog();

            return Result.Succeeded;
        }

        // ── Projection helpers ────────────────────────────────────────────────
        private static double Dot(XYZ a, XYZ b) =>
            a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        private static void GetViewAxes(View view, out XYZ right, out XYZ up, out XYZ fwd)
        { right = view.RightDirection; up = view.UpDirection; fwd = up.CrossProduct(right); }

        private static XYZ FromScreen(double u, double v, double depth, XYZ right, XYZ up, XYZ fwd) =>
            new XYZ(u * right.X + v * up.X + depth * fwd.X,
                    u * right.Y + v * up.Y + depth * fwd.Y,
                    u * right.Z + v * up.Z + depth * fwd.Z);

        private static bool TryGetScreenRect(
            Element element, View view, XYZ right, XYZ up,
            out (double UMin, double UMax, double VMin, double VMax) rect)
        {
            rect = default;
            BoundingBoxXYZ? bb = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            if (bb == null) return false;
            XYZ mn = bb.Min, mx = bb.Max;
            XYZ[] pts =
            {
                new XYZ(mn.X,mn.Y,mn.Z), new XYZ(mx.X,mn.Y,mn.Z),
                new XYZ(mn.X,mx.Y,mn.Z), new XYZ(mx.X,mx.Y,mn.Z),
                new XYZ(mn.X,mn.Y,mx.Z), new XYZ(mx.X,mn.Y,mx.Z),
                new XYZ(mn.X,mx.Y,mx.Z), new XYZ(mx.X,mx.Y,mx.Z),
            };
            double[] us = pts.Select(p => Dot(p, right)).ToArray();
            double[] vs = pts.Select(p => Dot(p, up)).ToArray();
            rect = (us.Min(), us.Max(), vs.Min(), vs.Max());
            return true;
        }

        private static double GetElementDepth(Element element, View view, XYZ fwd)
        {
            BoundingBoxXYZ? bb = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            if (bb == null) return 0.0;
            return (Dot(bb.Min, fwd) + Dot(bb.Max, fwd)) / 2.0;
        }

        // ── Zone assignment ───────────────────────────────────────────────────
        private static (double TopFrac, double TopHFrac, double BotFrac, double BotHFrac, double ElFrac)
            GetThresholds(double uMin, double uMax, double vMin, double vMax)
        {
            double w = uMax - uMin, h = vMax - vMin;
            bool tall = h > 0 && (w / h) < (1.0 / TallRatio);
            return tall
                ? (TallTopBottomFraction, TallTopBottomMaxHeightFraction,
                   TallTopBottomFraction, TallTopBottomMaxHeightFraction, WideElementFraction)
                : (WideTopFraction, WideTopMaxHeightFraction,
                   WideBottomFraction, WideBottomMaxHeightFraction, WideElementFraction);
        }

        private static Dictionary<long, string> AssignZonesTwoPass(
            List<(Element El, (double UMin, double UMax, double VMin, double VMax) Rect, double Depth)> elData,
            double asmUMin, double asmUMax, double asmVMin, double asmVMax,
            double topFrac, double topHFrac, double botFrac, double botHFrac)
        {
            double w = asmUMax - asmUMin, h = asmVMax - asmVMin;
            double topThresh = asmVMax - topFrac * h;
            double botThresh = asmVMin + botFrac * h;
            double topMaxH = topHFrac * h;
            double botMaxH = botHFrac * h;

            var result = new Dictionary<long, string>();
            var unassigned = new List<(Element El, (double UMin, double UMax, double VMin, double VMax) Rect, double Depth)>();

            foreach (var item in elData)
            {
                double vc = (item.Rect.VMin + item.Rect.VMax) / 2.0;
                double elH = item.Rect.VMax - item.Rect.VMin;
                double elW = item.Rect.UMax - item.Rect.UMin;

                if (vc >= topThresh && elH <= topMaxH)
                    result[item.El.Id.Value] = "TOP";
                else if (vc <= botThresh && elH <= botMaxH)
                    result[item.El.Id.Value] = "BOTTOM";
                else
                    unassigned.Add(item);
            }

            foreach (var item in unassigned)
            {
                double uc = (item.Rect.UMin + item.Rect.UMax) / 2.0;
                double relU = w > 0 ? (uc - asmUMin) / w : 0.5;
                result[item.El.Id.Value] = relU <= 0.5 ? "LEFT" : "RIGHT";
            }

            return result;
        }

        // ── Slot layout ───────────────────────────────────────────────────────
        private struct SlotUV { public double U, V; }

        private static List<SlotUV> EvenSlotsLR(int count, double colU, double vMin, double vMax)
        {
            if (count == 0) return new List<SlotUV>();
            var slots = new List<SlotUV>();
            double step = count == 1 ? 0 : (vMax - vMin) / (count - 1);
            for (int i = 0; i < count; i++)
                slots.Add(new SlotUV { U = colU, V = count == 1 ? (vMin + vMax) / 2.0 : vMax - i * step });
            return slots;
        }

        private static List<SlotUV> EvenSlotsTB(int count, double rowV, double uMin, double uMax)
        {
            if (count == 0) return new List<SlotUV>();
            var slots = new List<SlotUV>();
            double step = count == 1 ? 0 : (uMax - uMin) / (count - 1);
            for (int i = 0; i < count; i++)
                slots.Add(new SlotUV { U = uMin + i * step, V = rowV });
            return slots;
        }

        private static double BoundaryInterceptV(
            (double UMin, double UMax, double VMin, double VMax) rect,
            string zone, double colU, double asmEdge)
        {
            bool isLeft = zone == "LEFT";
            double relX = isLeft ? (rect.UMax - colU) : (rect.UMin - colU);
            if (Math.Abs(relX) < 1e-9) return (rect.VMin + rect.VMax) / 2.0;
            double slope = (rect.VMax - rect.VMin) / (2.0 * relX);
            return (rect.VMin + rect.VMax) / 2.0 + slope * (asmEdge - colU);
        }

        private static Dictionary<long, SlotUV> BuildSlotMap(
            Dictionary<string, List<(Element El,
                (double UMin, double UMax, double VMin, double VMax) Rect,
                double Depth)>> zones,
            double colLeft, double colRight, double rowTop, double rowBottom,
            double asmUMin, double asmUMax, double asmVMin, double asmVMax)
        {
            var sm = new Dictionary<long, SlotUV>();
            foreach (var col in new[]
            {
                (Zname: "LEFT",  ColU: colLeft,  AsmEdge: asmUMin),
                (Zname: "RIGHT", ColU: colRight, AsmEdge: asmUMax)
            })
            {
                var ord = zones[col.Zname]
                    .OrderByDescending(x => BoundaryInterceptV(x.Rect, col.Zname, col.ColU, col.AsmEdge))
                    .ThenBy(x => x.Depth).ToList();
                var sl = EvenSlotsLR(ord.Count, col.ColU, asmVMin, asmVMax);
                for (int i = 0; i < ord.Count; i++) sm[ord[i].El.Id.Value] = sl[i];
            }
            foreach (var row in new[]
            {
                (RowV: rowTop,    Zname: "TOP"),
                (RowV: rowBottom, Zname: "BOTTOM")
            })
            {
                var ord = zones[row.Zname].OrderBy(x => (x.Rect.UMin + x.Rect.UMax) / 2.0).ToList();
                var sl = EvenSlotsTB(ord.Count, row.RowV, asmUMin, asmUMax);
                for (int i = 0; i < ord.Count; i++) sm[ord[i].El.Id.Value] = sl[i];
            }
            return sm;
        }

        // ── Geometry / leader endpoint ────────────────────────────────────────
        private static Options GetGeometryOptions(View view) => new Options
        { View = view, ComputeReferences = true, IncludeNonVisibleObjects = false };

        private static double XyzDist(XYZ a, XYZ b)
        { double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z; return Math.Sqrt(dx * dx + dy * dy + dz * dz); }

        private static XYZ? ClosestOnFace(Face face, XYZ pt)
        { try { return face.Project(pt)?.XYZPoint; } catch { return null; } }

        private static XYZ? GetClosestGeometryPoint(Element element, View view, XYZ probe)
        {
            GeometryElement? geom;
            try { geom = element.get_Geometry(GetGeometryOptions(view)); } catch { return null; }
            if (geom == null) return null;
            XYZ? best = null; double bestDist = double.MaxValue;
            void PS(Solid s)
            {
                if (s == null || s.Volume <= 0) return;
                try { foreach (Face f in s.Faces) { var p = ClosestOnFace(f, probe); if (p == null) continue; double d = XyzDist(p, probe); if (d < bestDist) { bestDist = d; best = p; } } } catch { }
            }
            void Walk(GeometryObject o)
            {
                try { if (o is GeometryInstance gi) foreach (var g in gi.GetInstanceGeometry()) Walk(g); else if (o is Solid sl) PS(sl); } catch { }
            }
            try { foreach (GeometryObject o in geom) Walk(o); } catch { }
            return best;
        }

        private static XYZ? GetElementCenter(Element element, View view)
        {
            BoundingBoxXYZ? bb = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            if (bb == null) return null;
            return new XYZ((bb.Min.X + bb.Max.X) / 2.0, (bb.Min.Y + bb.Max.Y) / 2.0, (bb.Min.Z + bb.Max.Z) / 2.0);
        }

        private static Dictionary<long, XYZ> PrecomputeLeaderEndpoints(
            Dictionary<string, List<(Element El,
                (double UMin, double UMax, double VMin, double VMax) Rect,
                double Depth)>> zones,
            XYZ right, XYZ up, XYZ fwd, View view)
        {
            var lm = new Dictionary<long, XYZ>();
            foreach (var zl in zones.Values)
                foreach (var item in zl)
                {
                    double uc = (item.Rect.UMin + item.Rect.UMax) / 2.0;
                    double vc = (item.Rect.VMin + item.Rect.VMax) / 2.0;
                    XYZ probe = FromScreen(uc, vc, item.Depth, right, up, fwd);
                    lm[item.El.Id.Value] =
                        GetClosestGeometryPoint(item.El, view, probe)
                        ?? GetElementCenter(item.El, view)
                        ?? probe;
                }
            return lm;
        }

        // ── Revit helpers ─────────────────────────────────────────────────────
        private static List<Element> CollectModelElements(Document doc, View view)
        {
            var skip = new HashSet<int>
            {
                (int)BuiltInCategory.OST_Cameras,   (int)BuiltInCategory.OST_Grids,
                (int)BuiltInCategory.OST_Levels,    (int)BuiltInCategory.OST_SectionBox,
                (int)BuiltInCategory.OST_Views,     (int)BuiltInCategory.OST_RvtLinks,
                (int)BuiltInCategory.OST_Assemblies,
            };
            var seen = new HashSet<long>(); var res = new List<Element>();
            foreach (Element el in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                if (seen.Contains(el.Id.Value)) continue;
                Category? cat = el.Category;
                if (cat == null || !cat.HasMaterialQuantities) continue;
                try { if (skip.Contains((int)cat.BuiltInCategory)) continue; } catch { continue; }
                seen.Add(el.Id.Value); res.Add(el);
            }
            return res;
        }

    }

    // ============================================================
    //  BUTTON 2 — CMD_VENDORTAG  (Vendor Drawings)
    // ============================================================
    [Transaction(TransactionMode.Manual)]
    public class Cmd_DOC05b_Vendor : IExternalCommand
    {
        private const double EdgeClearanceFt = 1.0;
        private const double MinTagSpacingFt = 0.40;
        private const bool LeaderEnabled = true;
        private const double TallRatio = 1.4;
        private const double TallTopBottomFraction = 0.05;
        private const double TallTopBottomMaxHeightFraction = 0.10;
        private const double WideTopFraction = 0.30;
        private const double WideTopMaxHeightFraction = 0.50;
        private const double WideBottomFraction = 0.05;
        private const double WideBottomMaxHeightFraction = 0.10;
        private const double WideElementFraction = 0.55;
        private const double ClearThreshold = 0.08;
        private const double ColumnVerticalPaddingFt = 0.5;

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            // 1. Collect all 3D views on sheets
            var entries = BatchViewPickerWindow.CollectEntries(doc);
            if (entries.Count == 0)
            {
                AvControls.ShowAlert("Vendor Auto Tag", "No 3D views found on any sheet in this project.", AlertSeverity.Warning);
                return Result.Failed;
            }

            // 2. Single window: pick tag family (in-window section) + multi-select views
            var picker = new BatchViewPickerWindow(
                entries, doc, BuiltInCategory.OST_GenericModelTags,
                "Vendor Tag Family", "Vendor", 30, "▶  Run Script");
            if (picker.ShowDialog() != true
                || picker.SelectedViews.Count == 0
                || picker.SelectedTagFamilyId == ElementId.InvalidElementId)
                return Result.Cancelled;

            ElementId tagTypeId = picker.SelectedTagFamilyId;
            int tagPercent = picker.SelectedPercent;
            List<View3D> selectedViews = picker.SelectedViews;

            // 3. Tag each selected view, collect structured log rows
            int totalCreated = 0, totalSkipped = 0;
            var logRows = new List<ViewLogRow>();

            var sheetByViewId = new Dictionary<ElementId, string>();
            foreach (ViewSheet vs in new FilteredElementCollector(doc)
                                         .OfClass(typeof(ViewSheet)).Cast<ViewSheet>())
                foreach (ElementId vpId in vs.GetAllViewports())
                    if (doc.GetElement(vpId) is Viewport vp)
                        sheetByViewId[vp.ViewId] = vs.SheetNumber;

            foreach (View3D view in selectedViews)
            {
                sheetByViewId.TryGetValue(view.Id, out string? sheetNum);
                var row = new ViewLogRow
                {
                    ViewName = view.Name,
                    SheetNum = sheetNum ?? "\u2014",
                    // Captured here because this is the last point at which the
                    // Document is in scope; the row itself keeps only the name.
                    AssemblyName = AutoTagResultsWindow.ResolveAssemblyName(doc, view)
                };

                List<Element> modelElements = CollectModelElements(doc, view);
                if (modelElements.Count == 0)
                {
                    row.Status = "skip"; row.Notes = "No taggable elements";
                    logRows.Add(row); continue;
                }

                GetViewAxes(view, out XYZ right, out XYZ up, out XYZ fwd);

                // Build full elData for ALL elements (needed for assembly bounds)
                var elDataAll = new List<(Element El,
                    (double UMin, double UMax, double VMin, double VMax) Rect,
                    double Depth)>();
                foreach (Element el in modelElements)
                {
                    if (!TryGetScreenRect(el, view, right, up, out var rect)) continue;
                    elDataAll.Add((el, rect, GetElementDepth(el, view, fwd)));
                }

                if (elDataAll.Count == 0)
                {
                    row.Status = "skip"; row.Notes = "Could not project elements";
                    logRows.Add(row); continue;
                }

                // Select representatives: group by Comments, pick top N by screen area
                var elData = SelectRepresentativesByComments(elDataAll, tagPercent);

                if (elData.Count == 0)
                {
                    row.Status = "skip"; row.Notes = "No representatives selected";
                    logRows.Add(row); continue;
                }

                // Assembly bounds come from ALL elements so slot columns/rows
                // sit at the true assembly edges
                double asmUMin = elDataAll.Min(e => e.Rect.UMin);
                double asmUMax = elDataAll.Max(e => e.Rect.UMax);
                double asmVMin = elDataAll.Min(e => e.Rect.VMin);
                double asmVMax = elDataAll.Max(e => e.Rect.VMax);
                double medDepth = elDataAll.Select(e => e.Depth).OrderBy(d => d)
                                           .ElementAt(elDataAll.Count / 2);

                double colLeft = asmUMin - EdgeClearanceFt;
                double colRight = asmUMax + EdgeClearanceFt;
                double rowTop = asmVMax + EdgeClearanceFt;
                double rowBottom = asmVMin - EdgeClearanceFt;

                var (topFrac, topHFrac, botFrac, botHFrac, _) =
                    GetThresholds(asmUMin, asmUMax, asmVMin, asmVMax);

                // Zone assignment and slot layout use only the representatives
                var zoneMap = AssignZonesTwoPass(elData,
                    asmUMin, asmUMax, asmVMin, asmVMax,
                    topFrac, topHFrac, botFrac, botHFrac);

                var zones = new Dictionary<string, List<(Element El,
                    (double UMin, double UMax, double VMin, double VMax) Rect,
                    double Depth)>>
                {
                    ["LEFT"] = new List<(Element, (double, double, double, double), double)>(),
                    ["RIGHT"] = new List<(Element, (double, double, double, double), double)>(),
                    ["TOP"] = new List<(Element, (double, double, double, double), double)>(),
                    ["BOTTOM"] = new List<(Element, (double, double, double, double), double)>(),
                };
                foreach (var entry in elData) zones[zoneMap[entry.El.Id.Value]].Add(entry);

                var leaderMap = PrecomputeLeaderEndpoints(zones, right, up, fwd, view);
                var slotMap = BuildSlotMap(zones, colLeft, colRight, rowTop, rowBottom,
                                             asmUMin, asmUMax, asmVMin, asmVMax);
                var allOrdered = zones["RIGHT"].Concat(zones["LEFT"])
                                              .Concat(zones["TOP"])
                                              .Concat(zones["BOTTOM"]).ToList();

                int created = 0;
                var warnMsgs = new List<string>();
                var skippedIds = new List<long>();

                // Tx 1: lock the view (tags require a locked 3D view)
                using (var tLock = new Transaction(doc, "Lock view: " + view.Name))
                {
                    tLock.Start();
                    try { view.SaveOrientationAndLock(); } catch { }
                    tLock.Commit();
                }

                // Tx 2: place tags into the now-locked view
                using (var t = new Transaction(doc, "Vendor Auto Tag: " + view.Name))
                {
                    t.Start();
                    foreach (var entry in allOrdered)
                    {
                        Element el = entry.El;
                        try
                        {
                            long elKey = el.Id.Value;
                            var slot = slotMap[elKey];
                            XYZ head = FromScreen(slot.U, slot.V, medDepth, right, up, fwd);

                            if (!leaderMap.TryGetValue(elKey, out XYZ? lEnd) || lEnd == null)
                            { skippedIds.Add(elKey); continue; }

                            IndependentTag tag = IndependentTag.Create(
                                doc, tagTypeId, view.Id,
                                new Reference(el), LeaderEnabled,
                                TagOrientation.Horizontal, head);

                            if (tag == null) { skippedIds.Add(elKey); continue; }
                            try { tag.TagHeadPosition = head; } catch { }

                            if (LeaderEnabled)
                            {
                                try
                                {
                                    tag.LeaderEndCondition = LeaderEndCondition.Free;
                                    tag.SetLeaderEnd(new Reference(el), lEnd);
                                }
                                catch { }
                            }
                            created++;
                        }
                        catch (Exception ex)
                        {
                            skippedIds.Add(el.Id.Value);
                            warnMsgs.Add(ex.Message);
                        }
                    }
                    t.Commit();
                }

                row.Created = created;
                row.Skipped = skippedIds.Count;
                row.Status = skippedIds.Count == 0 ? "ok" : "warn";
                row.Notes = warnMsgs.Count > 0
                    ? warnMsgs[0] + (warnMsgs.Count > 1
                        ? string.Format(" (+{0} more)", warnMsgs.Count - 1) : "")
                    : "";
                logRows.Add(row);
                totalCreated += created;
                totalSkipped += skippedIds.Count;
            }

            new AutoTagResultsWindow(
                totalCreated, totalSkipped, selectedViews.Count, logRows,
                "Vendor Auto Tag \u2014 Results")
                .ShowDialog();

            return Result.Succeeded;
        }

        // ── Comments-group representative selection ───────────────────────────
        // Rules (percent comes from the Tag Density slider; default 30):
        //   group size 1 → tag 1
        //   group size 2 → tag 2
        //   group size 3+ → tag ceil(count * percent / 100), pick by largest screen area
        private static List<(Element El,
            (double UMin, double UMax, double VMin, double VMax) Rect,
            double Depth)>
            SelectRepresentativesByComments(
                List<(Element El,
                    (double UMin, double UMax, double VMin, double VMax) Rect,
                    double Depth)> all, int percent)
        {
            // Group by Comments parameter value; elements with null/empty Comments
            // each form their own singleton group so they are still tagged.
            var groups = all.GroupBy(e =>
            {
                Parameter? p = e.El.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                string v = p?.AsString() ?? "";
                // Use element id as fallback key so blank-comment elements don't merge
                return string.IsNullOrWhiteSpace(v) ? "__NO_COMMENT_" + e.El.Id.Value : v;
            });

            var result = new List<(Element El,
                (double UMin, double UMax, double VMin, double VMax) Rect,
                double Depth)>();

            foreach (var group in groups)
            {
                var members = group.ToList();
                // Largest screen-projected area first, top N per the slider.
                result.AddRange(TagDensity.SelectLargest(members, percent,
                    e => (e.Rect.UMax - e.Rect.UMin) * (e.Rect.VMax - e.Rect.VMin)));
            }

            return result;
        }

        // ── Projection helpers (copy — shared with Cmd_DOC05a_MT) ────────────────
        private static double Dot(XYZ a, XYZ b) =>
            a.X * b.X + a.Y * b.Y + a.Z * b.Z;

        private static void GetViewAxes(View view, out XYZ right, out XYZ up, out XYZ fwd)
        { right = view.RightDirection; up = view.UpDirection; fwd = up.CrossProduct(right); }

        private static XYZ FromScreen(double u, double v, double depth, XYZ right, XYZ up, XYZ fwd) =>
            new XYZ(u * right.X + v * up.X + depth * fwd.X,
                    u * right.Y + v * up.Y + depth * fwd.Y,
                    u * right.Z + v * up.Z + depth * fwd.Z);

        private static bool TryGetScreenRect(
            Element element, View view, XYZ right, XYZ up,
            out (double UMin, double UMax, double VMin, double VMax) rect)
        {
            rect = default;
            BoundingBoxXYZ? bb = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            if (bb == null) return false;
            XYZ mn = bb.Min, mx = bb.Max;
            XYZ[] pts =
            {
                new XYZ(mn.X,mn.Y,mn.Z), new XYZ(mx.X,mn.Y,mn.Z),
                new XYZ(mn.X,mx.Y,mn.Z), new XYZ(mx.X,mx.Y,mn.Z),
                new XYZ(mn.X,mn.Y,mx.Z), new XYZ(mx.X,mn.Y,mx.Z),
                new XYZ(mn.X,mx.Y,mx.Z), new XYZ(mx.X,mx.Y,mx.Z),
            };
            double[] us = pts.Select(p => Dot(p, right)).ToArray();
            double[] vs = pts.Select(p => Dot(p, up)).ToArray();
            rect = (us.Min(), us.Max(), vs.Min(), vs.Max());
            return true;
        }

        private static double GetElementDepth(Element element, View view, XYZ fwd)
        {
            BoundingBoxXYZ? bb = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            if (bb == null) return 0.0;
            return (Dot(bb.Min, fwd) + Dot(bb.Max, fwd)) / 2.0;
        }

        private static (double TopFrac, double TopHFrac, double BotFrac, double BotHFrac, double ElFrac)
            GetThresholds(double uMin, double uMax, double vMin, double vMax)
        {
            double w = uMax - uMin, h = vMax - vMin;
            bool tall = h > 0 && (w / h) < (1.0 / TallRatio);
            return tall
                ? (TallTopBottomFraction, TallTopBottomMaxHeightFraction,
                   TallTopBottomFraction, TallTopBottomMaxHeightFraction, WideElementFraction)
                : (WideTopFraction, WideTopMaxHeightFraction,
                   WideBottomFraction, WideBottomMaxHeightFraction, WideElementFraction);
        }

        private static Dictionary<long, string> AssignZonesTwoPass(
            List<(Element El, (double UMin, double UMax, double VMin, double VMax) Rect, double Depth)> elData,
            double asmUMin, double asmUMax, double asmVMin, double asmVMax,
            double topFrac, double topHFrac, double botFrac, double botHFrac)
        {
            double h = asmVMax - asmVMin;
            double topThresh = asmVMax - topFrac * h;
            double botThresh = asmVMin + botFrac * h;
            double topMaxH = topHFrac * h;
            double botMaxH = botHFrac * h;

            var result = new Dictionary<long, string>();
            var unassigned = new List<(Element El, (double UMin, double UMax, double VMin, double VMax) Rect, double Depth)>();

            foreach (var item in elData)
            {
                double vc = (item.Rect.VMin + item.Rect.VMax) / 2.0;
                double elH = item.Rect.VMax - item.Rect.VMin;

                if (vc >= topThresh && elH <= topMaxH)
                    result[item.El.Id.Value] = "TOP";
                else if (vc <= botThresh && elH <= botMaxH)
                    result[item.El.Id.Value] = "BOTTOM";
                else
                    unassigned.Add(item);
            }

            foreach (var item in unassigned)
            {
                double w = asmUMax - asmUMin;
                double uc = (item.Rect.UMin + item.Rect.UMax) / 2.0;
                double relU = w > 0 ? (uc - asmUMin) / w : 0.5;
                result[item.El.Id.Value] = relU <= 0.5 ? "LEFT" : "RIGHT";
            }

            return result;
        }

        private struct SlotUV { public double U, V; }

        private static List<SlotUV> EvenSlotsLR(int count, double colU, double vMin, double vMax)
        {
            if (count == 0) return new List<SlotUV>();
            var slots = new List<SlotUV>();
            double step = count == 1 ? 0 : (vMax - vMin) / (count - 1);
            for (int i = 0; i < count; i++)
                slots.Add(new SlotUV { U = colU, V = count == 1 ? (vMin + vMax) / 2.0 : vMax - i * step });
            return slots;
        }

        private static List<SlotUV> EvenSlotsTB(int count, double rowV, double uMin, double uMax)
        {
            if (count == 0) return new List<SlotUV>();
            var slots = new List<SlotUV>();
            double step = count == 1 ? 0 : (uMax - uMin) / (count - 1);
            for (int i = 0; i < count; i++)
                slots.Add(new SlotUV { U = uMin + i * step, V = rowV });
            return slots;
        }

        private static double BoundaryInterceptV(
            (double UMin, double UMax, double VMin, double VMax) rect,
            string zone, double colU, double asmEdge)
        {
            bool isLeft = zone == "LEFT";
            double relX = isLeft ? (rect.UMax - colU) : (rect.UMin - colU);
            if (Math.Abs(relX) < 1e-9) return (rect.VMin + rect.VMax) / 2.0;
            double slope = (rect.VMax - rect.VMin) / (2.0 * relX);
            return (rect.VMin + rect.VMax) / 2.0 + slope * (asmEdge - colU);
        }

        private static Dictionary<long, SlotUV> BuildSlotMap(
            Dictionary<string, List<(Element El,
                (double UMin, double UMax, double VMin, double VMax) Rect,
                double Depth)>> zones,
            double colLeft, double colRight, double rowTop, double rowBottom,
            double asmUMin, double asmUMax, double asmVMin, double asmVMax)
        {
            var sm = new Dictionary<long, SlotUV>();
            foreach (var col in new[]
            {
                (Zname: "LEFT",  ColU: colLeft,  AsmEdge: asmUMin),
                (Zname: "RIGHT", ColU: colRight, AsmEdge: asmUMax)
            })
            {
                var ord = zones[col.Zname]
                    .OrderByDescending(x => BoundaryInterceptV(x.Rect, col.Zname, col.ColU, col.AsmEdge))
                    .ThenBy(x => x.Depth).ToList();
                var sl = EvenSlotsLR(ord.Count, col.ColU, asmVMin, asmVMax);
                for (int i = 0; i < ord.Count; i++) sm[ord[i].El.Id.Value] = sl[i];
            }
            foreach (var row in new[]
            {
                (RowV: rowTop,    Zname: "TOP"),
                (RowV: rowBottom, Zname: "BOTTOM")
            })
            {
                var ord = zones[row.Zname].OrderBy(x => (x.Rect.UMin + x.Rect.UMax) / 2.0).ToList();
                var sl = EvenSlotsTB(ord.Count, row.RowV, asmUMin, asmUMax);
                for (int i = 0; i < ord.Count; i++) sm[ord[i].El.Id.Value] = sl[i];
            }
            return sm;
        }

        private static Options GetGeometryOptions(View view) => new Options
        { View = view, ComputeReferences = true, IncludeNonVisibleObjects = false };

        private static double XyzDist(XYZ a, XYZ b)
        { double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z; return Math.Sqrt(dx * dx + dy * dy + dz * dz); }

        private static XYZ? ClosestOnFace(Face face, XYZ pt)
        { try { return face.Project(pt)?.XYZPoint; } catch { return null; } }

        private static XYZ? GetClosestGeometryPoint(Element element, View view, XYZ probe)
        {
            GeometryElement? geom;
            try { geom = element.get_Geometry(GetGeometryOptions(view)); } catch { return null; }
            if (geom == null) return null;
            XYZ? best = null; double bestDist = double.MaxValue;
            void PS(Solid s)
            {
                if (s == null || s.Volume <= 0) return;
                try { foreach (Face f in s.Faces) { var p = ClosestOnFace(f, probe); if (p == null) continue; double d = XyzDist(p, probe); if (d < bestDist) { bestDist = d; best = p; } } } catch { }
            }
            void Walk(GeometryObject o)
            {
                try { if (o is GeometryInstance gi) foreach (var g in gi.GetInstanceGeometry()) Walk(g); else if (o is Solid sl) PS(sl); } catch { }
            }
            try { foreach (GeometryObject o in geom) Walk(o); } catch { }
            return best;
        }

        private static XYZ? GetElementCenter(Element element, View view)
        {
            BoundingBoxXYZ? bb = element.get_BoundingBox(view) ?? element.get_BoundingBox(null);
            if (bb == null) return null;
            return new XYZ((bb.Min.X + bb.Max.X) / 2.0, (bb.Min.Y + bb.Max.Y) / 2.0, (bb.Min.Z + bb.Max.Z) / 2.0);
        }

        private static Dictionary<long, XYZ> PrecomputeLeaderEndpoints(
            Dictionary<string, List<(Element El,
                (double UMin, double UMax, double VMin, double VMax) Rect,
                double Depth)>> zones,
            XYZ right, XYZ up, XYZ fwd, View view)
        {
            var lm = new Dictionary<long, XYZ>();
            foreach (var zl in zones.Values)
                foreach (var item in zl)
                {
                    double uc = (item.Rect.UMin + item.Rect.UMax) / 2.0;
                    double vc = (item.Rect.VMin + item.Rect.VMax) / 2.0;
                    XYZ probe = FromScreen(uc, vc, item.Depth, right, up, fwd);
                    lm[item.El.Id.Value] =
                        GetClosestGeometryPoint(item.El, view, probe)
                        ?? GetElementCenter(item.El, view)
                        ?? probe;
                }
            return lm;
        }

        private static List<Element> CollectModelElements(Document doc, View view)
        {
            var skip = new HashSet<int>
            {
                (int)BuiltInCategory.OST_Cameras,   (int)BuiltInCategory.OST_Grids,
                (int)BuiltInCategory.OST_Levels,    (int)BuiltInCategory.OST_SectionBox,
                (int)BuiltInCategory.OST_Views,     (int)BuiltInCategory.OST_RvtLinks,
                (int)BuiltInCategory.OST_Assemblies,
            };
            var seen = new HashSet<long>(); var res = new List<Element>();
            foreach (Element el in new FilteredElementCollector(doc, view.Id).WhereElementIsNotElementType())
            {
                if (seen.Contains(el.Id.Value)) continue;
                Category? cat = el.Category;
                if (cat == null) continue;

                // Always include Generic Model families, even if HasMaterialQuantities is false
                bool isGenericModel = false;
                try { isGenericModel = cat.BuiltInCategory == BuiltInCategory.OST_GenericModel; } catch { }

                if (!isGenericModel)
                {
                    if (!cat.HasMaterialQuantities) continue;
                    try { if (skip.Contains((int)cat.BuiltInCategory)) continue; } catch { continue; }
                }

                seen.Add(el.Id.Value); res.Add(el);
            }
            return res;
        }

    }
}