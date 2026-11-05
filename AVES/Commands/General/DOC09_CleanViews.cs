// -----------------------------------------------------------------------
// Title: Clean Assembly Views
// Summary: Lists the 2D and 3D views on the open sheet, says how many
//          elements will be hidden, then hides everything in the chosen
//          views except the Generic Model elements of the assembly shown
//          and tags.
// Key logic: An assembly view uses its own assembly. A normal view uses the
//            assembly visible in it, offers a dropdown when there are
//            several, and is skipped when there are none. The keep-set is that
//            assembly's Generic Model members plus their nested
//            sub-components, because GetMemberIds() returns direct members
//            only. Nothing is deleted, and the whole run is one transaction,
//            so a single undo reverses it.
// -----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using AVES.Core;
using WpfBorder = System.Windows.Controls.Border;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfClipboard = System.Windows.Clipboard;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfDataGrid = System.Windows.Controls.DataGrid;
using WpfDataGridCell = System.Windows.Controls.DataGridCell;
using WpfDataGridColumnHeader = System.Windows.Controls.Primitives.DataGridColumnHeader;
using WpfDataGridTemplateColumn = System.Windows.Controls.DataGridTemplateColumn;
using WpfDataGridTextColumn = System.Windows.Controls.DataGridTextColumn;
using WpfFontWeights = System.Windows.FontWeights;
using WpfGrid = System.Windows.Controls.Grid;
using WpfHAlign = System.Windows.HorizontalAlignment;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfRowDef = System.Windows.Controls.RowDefinition;
using WpfScrollBarVis = System.Windows.Controls.ScrollBarVisibility;
using WpfScrollViewer = System.Windows.Controls.ScrollViewer;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfThickness = System.Windows.Thickness;
using WpfVisibility = System.Windows.Visibility;

namespace AVES.Commands
{
    // ─────────────────────────────────────────────────────────────────────────
    //  DATA MODEL
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>What one view will change, worked out before anything is touched.</summary>
    internal class CleanPlan
    {
        public View View = null!;
        public string AssemblyName = "";
        public List<ElementId> ToHide = new List<ElementId>();
        public List<ElementId> ToRestore = new List<ElementId>();
        public int MembersToHide;
        public int LinksLeftVisible;
        public string? SkipReason;
        public string? Error;

        public bool HasWork => ToHide.Count > 0 || ToRestore.Count > 0;
    }

    /// <summary>An assembly a view could be cleaned for.</summary>
    internal class AssemblyChoice
    {
        public AssemblyInstance Assembly { get; }
        public string Label { get; }

        public AssemblyChoice(AssemblyInstance assembly, int visibleElements)
        {
            Assembly = assembly;
            Label = $"{assembly.AssemblyTypeName} · {visibleElements} visible · ID {assembly.Id.Value}";
        }
    }

    /// <summary>One line of the preview or results table.</summary>
    internal class CleanRow
    {
        public string ViewName { get; set; } = "";
        public string AssemblyName { get; set; } = "";
        public string Kind { get; set; } = "";
        public int Hide { get; set; }
        public int Restore { get; set; }
        public string Notes { get; set; } = "";
        public string StatusKind { get; set; } = "ok";
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  ENGINE
    // ─────────────────────────────────────────────────────────────────────────

    internal static class ViewCleaner
    {
        private static readonly HashSet<ViewType> CleanableTypes = new HashSet<ViewType>
        {
            ViewType.ThreeD, ViewType.Section, ViewType.Detail, ViewType.Elevation,
            ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.EngineeringPlan,
        };

        /// <summary>
        /// The view's own frame rather than content: hiding these would change
        /// what the view shows, not just remove clutter from it.
        /// </summary>
        private static readonly HashSet<BuiltInCategory> ViewFrameCategories = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_Viewers,
            BuiltInCategory.OST_CropBoundary,
            BuiltInCategory.OST_SectionBox,
        };

        public static bool Is3D(View view) => view.ViewType == ViewType.ThreeD;

        /// <summary>The views placed on a sheet that can hold model elements,
        /// which leaves out legends and drafting views.</summary>
        public static List<View> GetSheetViews(Document doc, ViewSheet sheet)
        {
            return sheet.GetAllPlacedViews()
                .Select(doc.GetElement)
                .OfType<View>()
                .Where(v => CleanableTypes.Contains(v.ViewType))
                .ToList();
        }

        private static bool HasOwnAssembly(View view) =>
            view.AssociatedAssemblyInstanceId != ElementId.InvalidElementId;

        /// <summary>Picker section: the assembly for an assembly view, the view
        /// type for a normal one.</summary>
        public static string GroupLabel(Document doc, View view)
        {
            if (HasOwnAssembly(view))
                return (doc.GetElement(view.AssociatedAssemblyInstanceId) as AssemblyInstance)?.AssemblyTypeName
                       ?? "Unknown Assembly";

            return view.ViewType switch
            {
                ViewType.ThreeD => "Normal Views · 3D",
                ViewType.FloorPlan => "Normal Views · Floor Plans",
                ViewType.CeilingPlan => "Normal Views · Ceiling Plans",
                ViewType.EngineeringPlan => "Normal Views · Structural Plans",
                ViewType.Section => "Normal Views · Sections",
                ViewType.Elevation => "Normal Views · Elevations",
                ViewType.Detail => "Normal Views · Details",
                _ => "Normal Views · " + view.ViewType,
            };
        }

        /// <summary>The assemblies a view could be cleaned for: its own, or for
        /// a normal view each assembly with members visible in it, most first.</summary>
        public static List<AssemblyChoice> GetAssemblyChoices(Document doc, View view)
        {
            if (HasOwnAssembly(view))
            {
                return doc.GetElement(view.AssociatedAssemblyInstanceId) is AssemblyInstance own
                    ? new List<AssemblyChoice> { new AssemblyChoice(own, 0) }
                    : new List<AssemblyChoice>();
            }

            return new FilteredElementCollector(doc, view.Id)
                .WhereElementIsNotElementType()
                .Where(e => e.AssemblyInstanceId != ElementId.InvalidElementId)
                .GroupBy(e => e.AssemblyInstanceId)
                .Select(g => (Assembly: doc.GetElement(g.Key) as AssemblyInstance, Visible: g.Count()))
                .Where(x => x.Assembly != null)
                .OrderByDescending(x => x.Visible)
                .ThenBy(x => x.Assembly!.AssemblyTypeName, StringComparer.OrdinalIgnoreCase)
                .Select(x => new AssemblyChoice(x.Assembly!, x.Visible))
                .ToList();
        }

        /// <summary>The assembly itself, plus its Generic Model members and
        /// nested sub-components. Hiding the instance would hide every member.</summary>
        private static HashSet<ElementId> BuildKeepSet(Document doc, AssemblyInstance assembly)
        {
            var keep = new HashSet<ElementId> { assembly.Id };

            void Add(ElementId id)
            {
                var element = doc.GetElement(id);
                if (element == null) return;

                if (element.Category?.BuiltInCategory == BuiltInCategory.OST_GenericModel)
                    keep.Add(id);

                if (element is FamilyInstance instance)
                    foreach (var subId in instance.GetSubComponentIds())
                        Add(subId);
            }

            foreach (var memberId in assembly.GetMemberIds())
                Add(memberId);

            return keep;
        }

        /// <summary>Read-only: works out one view's changes without editing the model.</summary>
        public static CleanPlan BuildPlan(Document doc, View view, AssemblyInstance? assembly, bool restoreMembers)
        {
            var plan = new CleanPlan { View = view };

            try
            {
                if (assembly == null)
                {
                    // Without an assembly every element would count as foreign.
                    if (HasOwnAssembly(view)) plan.Error = "Assembly not found";
                    else plan.SkipReason = "No assembly in this view";
                    return plan;
                }

                plan.AssemblyName = assembly.AssemblyTypeName;

                var keep = BuildKeepSet(doc, assembly);
                var members = new HashSet<ElementId>(assembly.GetMemberIds());

                var visible = new FilteredElementCollector(doc, view.Id)
                    .WhereElementIsNotElementType();

                foreach (var element in visible)
                {
                    var category = element.Category;
                    if (category == null || keep.Contains(element.Id)) continue;
                    if (element is IndependentTag || element is SpatialElementTag) continue;
                    if (ViewFrameCategories.Contains(category.BuiltInCategory)) continue;

                    // A link hides only as a whole, so it is reported instead.
                    if (element is RevitLinkInstance)
                    {
                        plan.LinksLeftVisible++;
                        continue;
                    }

                    if (!element.CanBeHidden(view)) continue;

                    plan.ToHide.Add(element.Id);
                    if (members.Contains(element.Id)) plan.MembersToHide++;
                }

                if (restoreMembers)
                {
                    plan.ToRestore = keep
                        .Where(id => id != assembly.Id && doc.GetElement(id)?.IsHidden(view) == true)
                        .ToList();
                }
            }
            catch (Exception ex)
            {
                plan.Error = ex.Message;
            }

            return plan;
        }

        /// <summary>Applies one plan. Call inside an open transaction. Returns
        /// how many elements were hidden and restored.</summary>
        public static (int Hidden, int Restored) Apply(CleanPlan plan)
        {
            int restored = ApplyToView(plan.ToRestore, plan.View.UnhideElements);
            int hidden = ApplyToView(plan.ToHide, plan.View.HideElements);
            return (hidden, restored);
        }

        /// <summary>The bulk call is all-or-nothing, so a failure falls back to
        /// one element at a time rather than losing the whole view.</summary>
        private static int ApplyToView(List<ElementId> ids, Action<ICollection<ElementId>> action)
        {
            if (ids.Count == 0) return 0;

            try
            {
                action(ids);
                return ids.Count;
            }
            catch
            {
                int done = 0;
                foreach (var id in ids)
                {
                    try
                    {
                        action(new List<ElementId> { id });
                        done++;
                    }
                    catch { /* this element refuses the change; the report shows the shortfall */ }
                }
                return done;
            }
        }

        public static string BaseNotes(CleanPlan plan)
        {
            var notes = new List<string>();
            if (plan.MembersToHide > 0)
                notes.Add($"{plan.MembersToHide} non-Generic Model assembly member(s)");
            if (plan.LinksLeftVisible > 0)
                notes.Add($"{plan.LinksLeftVisible} link(s) left visible");
            return string.Join(" · ", notes);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  VIEW PICKER
    // ─────────────────────────────────────────────────────────────────────────

    internal class CleanViewsPickerWindow : Window
    {
        private enum KindFilter { All, TwoD, ThreeD }

        /// <summary>One view in the list. Combo exists only when the view shows
        /// several assemblies; otherwise Assembly is the only candidate.</summary>
        private class PickerRow
        {
            public View View = null!;
            public string SearchKey = "";
            public WpfBorder Visual = null!;
            public WpfCheckBox Box = null!;
            public WpfComboBox? Combo;
            public AssemblyInstance? OnlyAssembly;

            public AssemblyInstance? Assembly =>
                Combo == null ? OnlyAssembly : (Combo.SelectedItem as AssemblyChoice)?.Assembly;
        }

        public List<CleanPlan> SelectedPlans { get; private set; } = new List<CleanPlan>();

        private readonly Document _doc;
        private readonly Dictionary<(ElementId View, ElementId Assembly, bool Restore), CleanPlan> _planCache
            = new Dictionary<(ElementId, ElementId, bool), CleanPlan>();
        private readonly List<PickerRow> _rows = new List<PickerRow>();
        private readonly List<(WpfBorder Header, List<WpfBorder> ChildRows)> _sections
            = new List<(WpfBorder, List<WpfBorder>)>();
        private readonly List<(KindFilter Kind, WpfButton Button)> _kindButtons
            = new List<(KindFilter, WpfButton)>();
        private readonly WpfTextBox _searchBox;
        private readonly WpfTextBlock _countLabel;
        private readonly WpfTextBlock _summary;
        private readonly WpfCheckBox _restoreBox;
        private KindFilter _kind = KindFilter.All;

        public CleanViewsPickerWindow(Document doc, ViewSheet sheet, List<View> views)
        {
            _doc = doc;

            var ordered = views
                .Select(v => new
                {
                    View = v,
                    Group = ViewCleaner.GroupLabel(doc, v)
                })
                .OrderBy(x => x.View.IsAssemblyView ? 0 : 1)
                .ThenBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.View.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Title = "Clean Views";
            Width = 520;
            Height = 660;
            MinWidth = 460;
            MinHeight = 420;
            ResizeMode = ResizeMode.CanResize;
            WindowStyle = WindowStyle.ToolWindow;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = AvTokens.B(AvTokens.Surface);
            FontFamily = AvTokens.Ui;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            var shell = new WpfGrid();
            shell.RowDefinitions.Add(new WpfRowDef { Height = new GridLength(44) });
            shell.RowDefinitions.Add(new WpfRowDef { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new WpfRowDef { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new WpfRowDef { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new WpfRowDef { Height = new GridLength(1, GridUnitType.Star) });
            shell.RowDefinitions.Add(new WpfRowDef { Height = GridLength.Auto });
            shell.RowDefinitions.Add(new WpfRowDef { Height = GridLength.Auto });
            Content = shell;

            var titleBar = AvControls.TitleBar("Clean Views", $"{sheet.SheetNumber} - {sheet.Name}");
            WpfGrid.SetRow(titleBar, 0);
            shell.Children.Add(titleBar);

            // Search box
            var searchWrap = new WpfBorder
            {
                Background = AvTokens.B(AvTokens.Surface2),
                BorderBrush = AvTokens.B(AvTokens.BorderStrong),
                BorderThickness = new WpfThickness(1),
                CornerRadius = new CornerRadius(5),
                Margin = new WpfThickness(20, 14, 20, 10),
                Padding = new WpfThickness(10, 0, 10, 0),
                Height = 34
            };
            var searchRow = new WpfStackPanel { Orientation = WpfOrientation.Horizontal };
            searchRow.Children.Add(new WpfTextBlock
            {
                Text = "⌕",
                FontSize = 15,
                Foreground = AvTokens.B(AvTokens.TextFaint),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new WpfThickness(0, 0, 7, 0)
            });
            _searchBox = new WpfTextBox
            {
                FontFamily = AvTokens.Ui,
                FontSize = 13,
                Foreground = AvTokens.B(AvTokens.Text),
                Background = WpfBrushes.Transparent,
                BorderThickness = new WpfThickness(0),
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 200,
                ToolTip = "Filter by assembly or view name"
            };
            _searchBox.TextChanged += (s, e) => ApplyFilter();
            searchRow.Children.Add(_searchBox);
            searchWrap.Child = searchRow;
            WpfGrid.SetRow(searchWrap, 1);
            shell.Children.Add(searchWrap);

            // 2D / 3D filter
            var kindRow = new WpfStackPanel
            {
                Orientation = WpfOrientation.Horizontal,
                Margin = new WpfThickness(20, 0, 20, 8)
            };
            foreach (var (kind, label) in new[]
            {
                (KindFilter.All, "All Views"),
                (KindFilter.TwoD, "2D Only"),
                (KindFilter.ThreeD, "3D Only"),
            })
            {
                var button = SmallButton(label, () => SetKind(kind));
                button.Margin = new WpfThickness(0, 0, 6, 0);
                kindRow.Children.Add(button);
                _kindButtons.Add((kind, button));
            }
            WpfGrid.SetRow(kindRow, 2);
            shell.Children.Add(kindRow);

            // Select all / none row
            var selRow = new WpfGrid { Margin = new WpfThickness(20, 0, 20, 8) };
            selRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            selRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            selRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var btnAll = SmallButton("Select All", () => SetAllChecked(true));
            btnAll.Margin = new WpfThickness(0, 0, 6, 0);
            btnAll.Foreground = AvTokens.B(AvTokens.Accent);
            var btnNone = SmallButton("Deselect All", () => SetAllChecked(false));
            _countLabel = new WpfTextBlock
            {
                FontFamily = AvTokens.Ui,
                FontSize = 11,
                Foreground = AvTokens.B(AvTokens.TextFaint),
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = WpfHAlign.Right
            };
            WpfGrid.SetColumn(btnAll, 0);
            WpfGrid.SetColumn(btnNone, 1);
            WpfGrid.SetColumn(_countLabel, 2);
            selRow.Children.Add(btnAll);
            selRow.Children.Add(btnNone);
            selRow.Children.Add(_countLabel);
            WpfGrid.SetRow(selRow, 3);
            shell.Children.Add(selRow);

            // List, grouped by assembly
            var list = new WpfStackPanel { Margin = new WpfThickness(20, 0, 20, 10) };
            foreach (var group in ordered.GroupBy(e => e.Group))
            {
                var header = AvControls.SectionHeader(group.Key);
                list.Children.Add(header);
                var childRows = new List<WpfBorder>();

                foreach (var entry in group)
                {
                    var (visual, box) = AvControls.CheckRow(entry.View.Name, false);
                    box.IsChecked = true;
                    visual.MouseLeftButtonUp += (s, e) => { box.IsChecked = !box.IsChecked; };
                    box.Checked += (s, e) => UpdateCount();
                    box.Unchecked += (s, e) => UpdateCount();
                    list.Children.Add(visual);

                    var row = new PickerRow
                    {
                        View = entry.View,
                        SearchKey = $"{group.Key} {entry.View.Name}",
                        Visual = visual,
                        Box = box,
                    };

                    var choices = ViewCleaner.GetAssemblyChoices(doc, entry.View);
                    if (choices.Count > 1)
                    {
                        row.Combo = new WpfComboBox
                        {
                            ItemsSource = choices,
                            DisplayMemberPath = nameof(AssemblyChoice.Label),
                            SelectedIndex = 0,
                            FontFamily = AvTokens.Ui,
                            FontSize = 12.5,
                            Height = 30,
                            VerticalContentAlignment = VerticalAlignment.Center,
                            Margin = new WpfThickness(28, 2, 0, 8),
                            ToolTip = "This view shows several assemblies. Choose the one to keep."
                        };
                        row.Combo.SelectionChanged += (s, e) => UpdateCount();
                        list.Children.Add(row.Combo);
                    }
                    else
                    {
                        row.OnlyAssembly = choices.FirstOrDefault()?.Assembly;
                    }

                    _rows.Add(row);
                    childRows.Add(visual);
                }

                _sections.Add((header, childRows));
            }
            var scroll = new WpfScrollViewer
            {
                VerticalScrollBarVisibility = WpfScrollBarVis.Auto,
                HorizontalScrollBarVisibility = WpfScrollBarVis.Disabled,
                Content = list
            };
            WpfGrid.SetRow(scroll, 4);
            shell.Children.Add(scroll);

            // How many elements the current selection will hide
            _summary = new WpfTextBlock
            {
                FontFamily = AvTokens.Ui,
                FontSize = 13,
                FontWeight = WpfFontWeights.SemiBold,
                Foreground = AvTokens.B(AvTokens.Text),
                TextWrapping = TextWrapping.Wrap,
                Margin = new WpfThickness(20, 4, 20, 12)
            };
            WpfGrid.SetRow(_summary, 5);
            shell.Children.Add(_summary);

            // Footer
            var footerInner = new DockPanel { LastChildFill = true };
            var footerRow = new WpfStackPanel { Orientation = WpfOrientation.Horizontal };
            var btnCancel = AvControls.GhostBtn("Cancel", Close);
            btnCancel.Margin = new WpfThickness(0, 0, 8, 0);
            footerRow.Children.Add(btnCancel);
            footerRow.Children.Add(AvControls.PrimaryBtn("▶  Clean Views", OnOk));
            DockPanel.SetDock(footerRow, Dock.Right);
            footerInner.Children.Add(footerRow);
            _restoreBox = new WpfCheckBox
            {
                Content = "Show hidden assembly members",
                FontFamily = AvTokens.Ui,
                FontSize = 12,
                Foreground = AvTokens.B(AvTokens.TextDim),
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Also shows the Generic Model elements of the assembly in each view that are currently hidden."
            };
            _restoreBox.Checked += (s, e) => UpdateCount();
            _restoreBox.Unchecked += (s, e) => UpdateCount();
            footerInner.Children.Add(_restoreBox);
            var footer = AvControls.FooterStrip(footerInner);
            WpfGrid.SetRow(footer, 6);
            shell.Children.Add(footer);

            SetKind(KindFilter.All);
            UpdateCount();
        }

        private static WpfButton SmallButton(string label, Action onClick)
        {
            var button = AvControls.SecondaryBtn(label, onClick);
            button.FontSize = 12.5;
            button.Padding = new WpfThickness(10, 5, 10, 5);
            return button;
        }

        private void SetKind(KindFilter kind)
        {
            _kind = kind;
            foreach (var (buttonKind, button) in _kindButtons)
                button.Foreground = AvTokens.B(buttonKind == kind ? AvTokens.Accent : AvTokens.TextDim);
            ApplyFilter();
        }

        private bool MatchesKind(View view) => _kind switch
        {
            KindFilter.TwoD => !ViewCleaner.Is3D(view),
            KindFilter.ThreeD => ViewCleaner.Is3D(view),
            _ => true,
        };

        private void ApplyFilter()
        {
            var filter = _searchBox.Text?.Trim() ?? "";
            foreach (var row in _rows)
            {
                bool match = MatchesKind(row.View)
                             && (filter.Length == 0 || row.SearchKey.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0);
                var visibility = match ? WpfVisibility.Visible : WpfVisibility.Collapsed;
                row.Visual.Visibility = visibility;
                if (row.Combo != null) row.Combo.Visibility = visibility;
            }

            foreach (var (header, childRows) in _sections)
            {
                bool anyVisible = childRows.Any(r => r.Visibility == WpfVisibility.Visible);
                header.Visibility = anyVisible ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            }
        }

        private void SetAllChecked(bool value)
        {
            foreach (var row in _rows)
            {
                if (row.Visual.Visibility == WpfVisibility.Visible)
                    row.Box.IsChecked = value;
            }
            UpdateCount();
        }

        /// <summary>Plans are cached because every tick and dropdown change
        /// recounts the whole selection.</summary>
        private CleanPlan PlanFor(PickerRow row)
        {
            var assembly = row.Assembly;
            bool restore = _restoreBox.IsChecked == true;
            var key = (row.View.Id, assembly?.Id ?? ElementId.InvalidElementId, restore);

            if (!_planCache.TryGetValue(key, out var plan))
                _planCache[key] = plan = ViewCleaner.BuildPlan(_doc, row.View, assembly, restore);

            return plan;
        }

        private List<CleanPlan> CheckedPlans() =>
            _rows.Where(r => r.Box.IsChecked == true).Select(PlanFor).ToList();

        private void UpdateCount()
        {
            var plans = CheckedPlans();
            _countLabel.Text = $"{plans.Count} of {_rows.Count} selected";

            int hide = plans.Sum(p => p.ToHide.Count);
            int show = plans.Sum(p => p.ToRestore.Count);

            string text = hide == 1 ? "1 element will be hidden" : $"{hide} elements will be hidden";
            if (show > 0) text += $" and {show} shown";
            _summary.Text = plans.Count == 0 ? "Select a view to clean." : text + ".";
        }

        private void OnOk()
        {
            SelectedPlans = CheckedPlans();
            Close();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  RESULTS WINDOW
    // ─────────────────────────────────────────────────────────────────────────

    internal class CleanViewsResultsWindow : Window
    {
        private const string WindowTitle = "Clean Views – Results";

        private readonly List<CleanRow> _rows;

        public CleanViewsResultsWindow(
            List<CleanRow> rows,
            (string Value, string Label, string Accent)[] stats,
            string docTitle)
        {
            _rows = rows;

            Title = WindowTitle;
            Width = 960;
            Height = 620;
            MinWidth = 720;
            MinHeight = 420;
            ResizeMode = ResizeMode.CanResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = AvTokens.B(AvTokens.Surface);
            FontFamily = AvTokens.Ui;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            var root = new WpfGrid();
            root.RowDefinitions.Add(new WpfRowDef { Height = GridLength.Auto });
            root.RowDefinitions.Add(new WpfRowDef { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new WpfRowDef { Height = GridLength.Auto });
            Content = root;

            var header = AvControls.ReportHeaderBar(WindowTitle);
            WpfGrid.SetRow(header, 0);
            root.Children.Add(header);

            var body = new WpfStackPanel { Margin = new WpfThickness(20) };
            var bodyScroll = new WpfScrollViewer
            {
                VerticalScrollBarVisibility = WpfScrollBarVis.Auto,
                HorizontalScrollBarVisibility = WpfScrollBarVis.Disabled,
                Content = body
            };
            WpfGrid.SetRow(bodyScroll, 1);
            root.Children.Add(bodyScroll);

            var statGrid = new WpfGrid { Margin = new WpfThickness(0, 0, 0, 20) };
            for (int i = 0; i < stats.Length; i++)
            {
                statGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var tile = AvControls.StatTile(stats[i].Value, stats[i].Label, stats[i].Accent);
                tile.Margin = new WpfThickness(i == 0 ? 0 : 6, 0, i == stats.Length - 1 ? 0 : 6, 0);
                WpfGrid.SetColumn(tile, i);
                statGrid.Children.Add(tile);
            }
            body.Children.Add(statGrid);

            body.Children.Add(new WpfBorder
            {
                BorderBrush = AvTokens.B(AvTokens.Border),
                BorderThickness = new WpfThickness(1),
                CornerRadius = new CornerRadius(5),
                ClipToBounds = true,
                Background = AvTokens.B(AvTokens.Surface),
                Child = BuildGrid()
            });

            var footerInner = new DockPanel { LastChildFill = true };
            var footerRow = new WpfStackPanel { Orientation = WpfOrientation.Horizontal, HorizontalAlignment = WpfHAlign.Right };
            var copy = AvControls.SecondaryBtn("⧉  Copy to clipboard", CopyToClipboard);
            copy.Margin = new WpfThickness(0, 0, 10, 0);
            footerRow.Children.Add(copy);
            footerRow.Children.Add(AvControls.PrimaryBtn("Close", Close));
            DockPanel.SetDock(footerRow, Dock.Right);
            footerInner.Children.Add(footerRow);
            footerInner.Children.Add(AvControls.FooterCaption($"Source · {docTitle}"));
            var footer = AvControls.FooterStrip(footerInner);
            WpfGrid.SetRow(footer, 2);
            root.Children.Add(footer);
        }

        private WpfDataGrid BuildGrid()
        {
            var grid = new WpfDataGrid
            {
                ItemsSource = _rows,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                IsReadOnly = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = AvTokens.B(AvTokens.Border),
                RowHeight = 40,
                FontFamily = AvTokens.Ui,
                FontSize = 12.5,
                Background = AvTokens.B(AvTokens.Surface),
                BorderThickness = new WpfThickness(0),
                RowBackground = AvTokens.B(AvTokens.Surface),
                AlternatingRowBackground = AvTokens.B(AvTokens.Zebra),
                AlternationCount = 2,
                ColumnHeaderHeight = 34,
                MaxHeight = 400
            };

            var headerStyle = new Style(typeof(WpfDataGridColumnHeader));
            headerStyle.Setters.Add(new Setter(WpfDataGridColumnHeader.BackgroundProperty, AvTokens.B(AvTokens.Surface2)));
            headerStyle.Setters.Add(new Setter(WpfDataGridColumnHeader.ForegroundProperty, AvTokens.B(AvTokens.TextFaint)));
            headerStyle.Setters.Add(new Setter(WpfDataGridColumnHeader.FontFamilyProperty, AvTokens.Ui));
            headerStyle.Setters.Add(new Setter(WpfDataGridColumnHeader.FontSizeProperty, 10.5));
            headerStyle.Setters.Add(new Setter(WpfDataGridColumnHeader.FontWeightProperty, WpfFontWeights.Bold));
            headerStyle.Setters.Add(new Setter(WpfDataGridColumnHeader.PaddingProperty, new WpfThickness(12, 9, 12, 9)));
            grid.ColumnHeaderStyle = headerStyle;

            var cellStyle = new Style(typeof(WpfDataGridCell));
            cellStyle.Setters.Add(new Setter(WpfDataGridCell.BorderThicknessProperty, new WpfThickness(0)));
            cellStyle.Setters.Add(new Setter(WpfDataGridCell.PaddingProperty, new WpfThickness(12, 8, 12, 8)));
            cellStyle.Setters.Add(new Setter(WpfDataGridCell.BackgroundProperty, WpfBrushes.Transparent));
            cellStyle.Setters.Add(new Setter(WpfDataGridCell.ForegroundProperty, AvTokens.B(AvTokens.Text)));
            cellStyle.Setters.Add(new Setter(WpfDataGridCell.TemplateProperty, AvControls.DataGridCellTemplate()));
            grid.CellStyle = cellStyle;

            void AddText(string header, string path, double width) =>
                grid.Columns.Add(new WpfDataGridTextColumn
                {
                    Header = header,
                    Binding = new System.Windows.Data.Binding(path),
                    Width = new DataGridLength(width, DataGridLengthUnitType.Star)
                });

            AddText("VIEW", nameof(CleanRow.ViewName), 1.4);
            AddText("ASSEMBLY", nameof(CleanRow.AssemblyName), 1.2);
            AddText("TYPE", nameof(CleanRow.Kind), 0.4);
            AddText("HIDDEN", nameof(CleanRow.Hide), 0.6);
            AddText("SHOWN", nameof(CleanRow.Restore), 0.6);
            AddText("NOTES", nameof(CleanRow.Notes), 1.8);

            grid.Columns.Add(new WpfDataGridTemplateColumn
            {
                Header = "STATUS",
                Width = DataGridLength.Auto,
                MinWidth = 90,
                CellTemplate = AvControls.StatusPillTemplate()
            });

            return grid;
        }

        private void CopyToClipboard()
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("View\tAssembly\tType\tHidden\tShown\tNotes");
            foreach (var r in _rows)
                sb.AppendLine(string.Join("\t", r.ViewName, r.AssemblyName, r.Kind, r.Hide, r.Restore, r.Notes));
            try { WpfClipboard.SetText(sb.ToString()); }
            catch { /* the clipboard can be held by another process; nothing to recover */ }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  COMMAND
    // ─────────────────────────────────────────────────────────────────────────

    [Transaction(TransactionMode.Manual)]
    public class Cmd_DOC09_CleanViews : IExternalCommand
    {
        private const string ToolName = "Clean Views";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            try
            {
                if (uiDoc.ActiveView is not ViewSheet sheet)
                {
                    AvControls.ShowAlert(ToolName,
                        "Open a sheet first. This tool cleans the views placed on the open sheet.",
                        AlertSeverity.Warning);
                    return Result.Cancelled;
                }

                var views = ViewCleaner.GetSheetViews(doc, sheet);
                if (views.Count == 0)
                {
                    AvControls.ShowAlert(ToolName,
                        "This sheet has no 2D or 3D views to clean.",
                        AlertSeverity.Warning);
                    return Result.Cancelled;
                }

                var picker = new CleanViewsPickerWindow(doc, sheet, views);
                picker.ShowDialog();
                if (picker.SelectedPlans.Count == 0) return Result.Cancelled;

                var plans = picker.SelectedPlans
                    .OrderBy(p => p.AssemblyName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p.View.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var rows = new List<CleanRow>();
                using (var transaction = new Transaction(doc, "AVES Clean Views"))
                {
                    transaction.Start();
                    foreach (var plan in plans)
                        rows.Add(ApplyPlan(plan));
                    transaction.Commit();
                }

                new CleanViewsResultsWindow(
                    rows,
                    new[]
                    {
                        (rows.Count(r => r.StatusKind == "ok" || r.StatusKind == "warn").ToString(), "Views cleaned", "ok"),
                        (rows.Count(r => r.StatusKind == "skip").ToString(), "Skipped", ""),
                        (rows.Sum(r => r.Hide).ToString(), "Elements hidden", ""),
                        (rows.Count(r => r.StatusKind == "fail").ToString(), "Failed",
                            rows.Any(r => r.StatusKind == "fail") ? "fail" : ""),
                    },
                    doc.Title).ShowDialog();

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                AvControls.ShowAlert($"{ToolName} – Error", ex.Message, AlertSeverity.Error);
                return Result.Failed;
            }
        }

        private static CleanRow NewRow(CleanPlan plan) => new CleanRow
        {
            ViewName = plan.View.Name,
            AssemblyName = plan.AssemblyName,
            Kind = ViewCleaner.Is3D(plan.View) ? "3D" : "2D",
        };

        private static CleanRow ApplyPlan(CleanPlan plan)
        {
            var row = NewRow(plan);

            if (plan.Error != null)
            {
                row.Notes = plan.Error;
                row.StatusKind = "fail";
                return row;
            }

            row.Notes = ViewCleaner.BaseNotes(plan);

            if (!plan.HasWork)
            {
                row.Notes = JoinNotes(plan.SkipReason ?? "Already clean", row.Notes);
                row.StatusKind = "skip";
                return row;
            }

            try
            {
                (row.Hide, row.Restore) = ViewCleaner.Apply(plan);

                int missed = plan.ToHide.Count + plan.ToRestore.Count - row.Hide - row.Restore;
                if (missed > 0)
                {
                    row.Notes = JoinNotes($"{missed} element(s) could not be changed", row.Notes);
                    row.StatusKind = "warn";
                }
            }
            catch (Exception ex)
            {
                row.Notes = ex.Message;
                row.StatusKind = "fail";
            }

            return row;
        }

        private static string JoinNotes(string first, string second) =>
            second.Length == 0 ? first : $"{first} · {second}";
    }
}
