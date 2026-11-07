// -----------------------------------------------------------------------
// Pulldown Title: Parameter & Data Tools
// Sub-buttons:
//   - Batch Shared Parameters: Adds a chosen set of shared parameters to
//     families/categories in bulk, letting the user pick a group, the
//     target parameters, binding type, and whether they're instance or
//     type parameters.
//   - Batch Remove Parameters: Removes a chosen set of parameters from
//     families/categories in bulk and reports how many were removed per
//     family.
//   - CSV Importer: Imports a CSV keyed by "Item Number" and writes its
//     columns as parameter values onto the types of the selected
//     elements, logging matched/unmatched item numbers.
// -----------------------------------------------------------------------
#nullable disable

// Autodesk
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using AVES.Core;

// System
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;

// WPF — aliases resolve every collision with Autodesk.Revit.DB / Autodesk.Revit.UI
using System.Windows;
using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WpfGrid = System.Windows.Controls.Grid;
using WpfColor = System.Windows.Media.Color;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfToggleButton = System.Windows.Controls.Primitives.ToggleButton;
using WpfBinding = System.Windows.Data.Binding;
using WpfPoint = System.Windows.Point;
using WpfTextBox = System.Windows.Controls.TextBox;
using WpfVisibility = System.Windows.Visibility;
using WpfBorder = System.Windows.Controls.Border;
using WpfSetter = System.Windows.Setter;
using WpfTrigger = System.Windows.Trigger;

namespace AVES.Commands
{
    // ══════════════════════════════════════════════════════════════════════
    //  Av  —  inline design token helpers (no XAML resource dictionary needed)
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Inline design tokens and composite-widget factories.
    /// Mirrors AvesTheme.xaml + AvesUi.cs from the design handoff so that
    /// all windows share a single source of truth without requiring a
    /// merged ResourceDictionary at add-in startup.
    /// </summary>
    internal static class Av
    {
        // ── Status pill ───────────────────────────────────────────────────
        /// <summary>
        /// Renders a rounded status pill replacing the old emoji column.
        /// kind: "ok" | "skip" | "fail" | "warn" | "info"
        /// Geometry routed to AvControls.StatusPill; "warn" keeps this file's
        /// own "Cache mismatch" wording via labelOverride (reconciliation
        /// decision: unify styling, keep existing per-file wording).
        /// </summary>
        public static WpfBorder StatusPill(string kind) =>
            AvControls.StatusPill(kind, kind == "warn" ? "Cache mismatch" : null);

        public static string PillKind(string glyph) => glyph switch
        {
            "✅" => "ok",
            "⏭" => "skip",
            "❌" => "fail",
            "⚠" => "warn",
            "⚠️" => "warn",
            _ => "info",
        };

        // ── Chip ──────────────────────────────────────────────────────────
        /// <summary>A selected-parameter token chip for inline settings.</summary>
        public static WpfBorder Chip(string text)
        {
            var lbl = new TextBlock
            {
                Text = text,
                FontFamily = AvTokens.Mono,
                FontSize = 11.5,
                Foreground = AvTokens.Brush(AvTokens.AccentDeep),
                VerticalAlignment = VerticalAlignment.Center
            };
            var x = new TextBlock
            {
                Text = "✕",
                FontFamily = AvTokens.Ui,
                FontSize = 10,
                Opacity = 0.55,
                Foreground = AvTokens.Brush(AvTokens.AccentDeep),
                Margin = new Thickness(7, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(lbl);
            row.Children.Add(x);
            return new WpfBorder
            {
                Background = AvTokens.Brush(AvTokens.AccentTint),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(11, 6, 8, 6),
                Margin = new Thickness(0, 0, 7, 7),
                Child = row
            };
        }

        // ── Brand tile — same logo bytes as AvTokens.LogoBase64, decoded here
        // with DecodePixelHeight + bicubic scaling (kept local per Phase 3) ──
        public static System.Windows.Controls.Image BrandTile(double size = 28)
        {
            var bytes = Convert.FromBase64String(AvTokens.LogoBase64);
            var ms = new System.IO.MemoryStream(bytes);
            var bmp = new System.Windows.Media.Imaging.BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = ms;
            bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            // Decode directly at the target height (x2 for high-DPI sharpness) so
            // WPF never has to downscale a full-resolution bitmap at render time.
            bmp.DecodePixelHeight = (int)(size * 2);
            bmp.EndInit();
            bmp.Freeze();

            var img = new System.Windows.Controls.Image
            {
                Source = bmp,
                Height = size,
                Width = double.NaN,
                Stretch = System.Windows.Media.Stretch.Uniform,
                VerticalAlignment = System.Windows.VerticalAlignment.Center,
                Margin = new System.Windows.Thickness(0, 0, 0, 0)
            };
            // Use bicubic interpolation instead of the default nearest-neighbour
            // to get a clean smooth result at small display sizes.
            System.Windows.Media.RenderOptions.SetBitmapScalingMode(
                img, System.Windows.Media.BitmapScalingMode.HighQuality);
            return img;
        }

        // ── Picker button + field label ───────────────────────────────────
        //  Moved to AvControls: PickerButton, PickerButtonTemplate,
        //  SetPickerValue and FieldLabel. Promoted unchanged, and the call sites
        //  in this file now go straight to AvControls rather than through a
        //  forwarder here, so there is one definition rather than two names for
        //  the same widget.
        //
        //  AvControls.PickerButton gained an opt-in chevron for a new caller in
        //  DOC03_CreateSheets.cs; it defaults to off, so these windows are unaffected.

        // ── Styled DataGrid ───────────────────────────────────────────────
        public static DataGrid StyledDataGrid()
        {
            var dg = new DataGrid
            {
                AutoGenerateColumns = false,
                IsReadOnly = true,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                CanUserSortColumns = true,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
                HorizontalGridLinesBrush = AvTokens.Brush(AvTokens.Border),
                BorderBrush = AvTokens.Brush(AvTokens.Border),
                BorderThickness = new Thickness(1),
                Background = AvTokens.Brush(AvTokens.Surface),
                RowBackground = AvTokens.Brush(AvTokens.Surface),
                AlternatingRowBackground = AvTokens.Brush(AvTokens.Zebra),
                AlternationCount = 2,
                RowHeight = 34,
                FontFamily = AvTokens.Ui,
                FontSize = 12.5,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };

            var headerStyle = new Style(typeof(DataGridColumnHeader));
            headerStyle.Setters.Add(new WpfSetter(DataGridColumnHeader.FontFamilyProperty, AvTokens.Ui));
            headerStyle.Setters.Add(new WpfSetter(DataGridColumnHeader.FontSizeProperty, 10.5));
            headerStyle.Setters.Add(new WpfSetter(DataGridColumnHeader.FontWeightProperty, FontWeights.Bold));
            headerStyle.Setters.Add(new WpfSetter(DataGridColumnHeader.ForegroundProperty, AvTokens.Brush(AvTokens.TextFaint)));
            headerStyle.Setters.Add(new WpfSetter(DataGridColumnHeader.BackgroundProperty, AvTokens.Brush(AvTokens.Surface2)));
            headerStyle.Setters.Add(new WpfSetter(DataGridColumnHeader.PaddingProperty, new Thickness(12, 9, 12, 9)));
            headerStyle.Setters.Add(new WpfSetter(DataGridColumnHeader.BorderBrushProperty, AvTokens.Brush(AvTokens.BorderStrong)));
            headerStyle.Setters.Add(new WpfSetter(DataGridColumnHeader.BorderThicknessProperty, new Thickness(0, 0, 0, 1.5)));
            dg.ColumnHeaderStyle = headerStyle;

            var cellStyle = new Style(typeof(DataGridCell));
            // Zero vertical padding, matching FAM01_Rename.cs and DOC01_FillSchedules.cs.
            // The 7px this used to declare was inert until the cell template below
            // started honouring Padding, at which point it consumed 14 of the row's
            // 34px and left roughly 3px of slack around the text -- against ~7px in
            // Button10/Button11 and ~25px in Button6/Button7. These are the shortest
            // rows in the plugin, so they can afford no vertical padding at all.
            cellStyle.Setters.Add(new WpfSetter(DataGridCell.PaddingProperty, new Thickness(12, 0, 12, 0)));
            cellStyle.Setters.Add(new WpfSetter(DataGridCell.BorderThicknessProperty, new Thickness(0)));
            cellStyle.Setters.Add(new WpfSetter(DataGridCell.ForegroundProperty, AvTokens.Brush(AvTokens.Text)));
            // The stock DataGridCell template consumes VerticalContentAlignment
            // nowhere, so setting it here did nothing and the TextColumn helper
            // below attaches no ElementStyle to compensate. This factory feeds
            // both of UnifiedReportWindow's grids -- results and warnings --
            // across all three of its callers, so this one setter covers Batch
            // Shared Parameters, Batch Remove Parameters and the CSV Importer.
            cellStyle.Setters.Add(new WpfSetter(DataGridCell.TemplateProperty, AvControls.DataGridCellTemplate()));
            dg.CellStyle = cellStyle;

            return dg;
        }

        // ── Text column helper ────────────────────────────────────────────
        public static DataGridTextColumn TextColumn(string header, string bindingPath, double width)
            => new DataGridTextColumn
            {
                Header = header,
                Binding = new WpfBinding(bindingPath),
                Width = new DataGridLength(width)
            };

        // ── Pill template column ──────────────────────────────────────────
        public static DataGridTemplateColumn PillColumn(string bindingPath, double width)
        {
            var col = new DataGridTemplateColumn
            {
                Header = "Status",
                Width = new DataGridLength(width)
            };

            var dt = new DataTemplate();
            var factory = new FrameworkElementFactory(typeof(ContentControl));
            factory.SetBinding(ContentControl.TagProperty, new WpfBinding(bindingPath));
            factory.AddHandler(FrameworkElement.LoadedEvent, new RoutedEventHandler((s, e) =>
            {
                if (s is ContentControl cc && cc.Tag is string glyph)
                    cc.Content = StatusPill(PillKind(glyph));
            }));
            dt.VisualTree = factory;
            col.CellTemplate = dt;
            return col;
        }

        // ── Footer strip ─────────────────────────────────────────────────
        // Promoted to AvControls: this class's values were already the canonical
        // ones, so all eight report windows now build their footer from it. A
        // forwarder rather than a second definition, matching PickerButton.
        public static WpfBorder FooterStrip(UIElement content)
            => AvControls.FooterStrip(content);

        // ── Segmented toggle (Instance / Type) ────────────────────────────
        public static (WpfBorder container, WpfToggleButton left, WpfToggleButton right)
            SegmentedToggle(string leftLabel, string rightLabel, bool leftSelected)
        {
            var leftBtn = MakeSegment(leftLabel, leftSelected);
            var rightBtn = MakeSegment(rightLabel, !leftSelected);

            leftBtn.Checked += (s, e) => rightBtn.IsChecked = false;
            rightBtn.Checked += (s, e) => leftBtn.IsChecked = false;

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(leftBtn);
            row.Children.Add(rightBtn);

            var container = new WpfBorder
            {
                BorderBrush = AvTokens.Brush(AvTokens.BorderStrong),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Background = AvTokens.Brush(AvTokens.Surface2),
                Padding = new Thickness(3),
                Child = row
            };
            return (container, leftBtn, rightBtn);
        }

        private static WpfToggleButton MakeSegment(string label, bool selected)
        {
            var tb = new WpfToggleButton
            {
                Content = label,
                IsChecked = selected,
                FontFamily = AvTokens.Ui,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
                Padding = new Thickness(14, 7, 14, 7),
                Template = SegmentTemplate()
            };
            return tb;
        }

        private static ControlTemplate SegmentTemplate()
        {
            var t = new ControlTemplate(typeof(WpfToggleButton));
            var bd = new FrameworkElementFactory(typeof(WpfBorder));
            bd.Name = "bd";
            bd.SetValue(WpfBorder.CornerRadiusProperty, new CornerRadius(3));
            bd.SetValue(WpfBorder.BackgroundProperty, new SolidColorBrush(Colors.Transparent));
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            cp.SetValue(ContentPresenter.MarginProperty, new Thickness(14, 7, 14, 7));
            bd.AppendChild(cp);
            t.VisualTree = bd;

            var checkedTrigger = new WpfTrigger { Property = WpfToggleButton.IsCheckedProperty, Value = true };
            checkedTrigger.Setters.Add(new WpfSetter(WpfBorder.BackgroundProperty, AvTokens.Brush(AvTokens.Surface), "bd"));
            checkedTrigger.Setters.Add(new WpfSetter(WpfBorder.BorderBrushProperty, AvTokens.Brush(AvTokens.Border), "bd"));
            checkedTrigger.Setters.Add(new WpfSetter(WpfBorder.BorderThicknessProperty, new Thickness(1), "bd"));
            t.Triggers.Add(checkedTrigger);

            return t;
        }

        // ── Rounded progress bar ──────────────────────────────────────────
        public static ProgressBar RoundedProgressBar(int total)
        {
            var pb = new ProgressBar
            {
                Minimum = 0,
                Maximum = total,
                Height = 10,
                Template = ProgressTemplate()
            };
            return pb;
        }

        private static ControlTemplate ProgressTemplate()
        {
            var t = new ControlTemplate(typeof(ProgressBar));

            var track = new FrameworkElementFactory(typeof(WpfBorder));
            track.SetValue(WpfBorder.CornerRadiusProperty, new CornerRadius(999));
            track.SetValue(WpfBorder.BackgroundProperty, AvTokens.Brush(AvTokens.Border));
            track.SetValue(WpfBorder.ClipToBoundsProperty, true);

            var grid = new FrameworkElementFactory(typeof(WpfGrid));
            grid.Name = "PART_Track";

            var indicator = new FrameworkElementFactory(typeof(WpfBorder));
            indicator.Name = "PART_Indicator";
            indicator.SetValue(WpfBorder.CornerRadiusProperty, new CornerRadius(999));
            indicator.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Left);

            var gradient = new LinearGradientBrush
            {
                StartPoint = new WpfPoint(0, 0),
                EndPoint = new WpfPoint(1, 0)
            };
            gradient.GradientStops.Add(new GradientStop(AvTokens.Hex(AvTokens.Accent), 0.0));
            gradient.GradientStops.Add(new GradientStop(AvTokens.Hex("#FF9E7A"), 1.0));
            indicator.SetValue(WpfBorder.BackgroundProperty, gradient);

            grid.AppendChild(indicator);
            track.AppendChild(grid);
            t.VisualTree = track;
            return t;
        }

        // ── Separator ─────────────────────────────────────────────────────
        public static WpfBorder HairlineSeparator() => new WpfBorder
        {
            Height = 1,
            Background = AvTokens.Brush(AvTokens.Border),
            Margin = new Thickness(0, 16, 0, 16)
        };

        // ── Config panel key/value row ─────────────────────────────────────
        public static WpfBorder ConfigRow(string key, string value, bool lastRow = false)
        {
            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var k = new TextBlock
            {
                Text = key,
                FontFamily = AvTokens.Ui,
                FontSize = 11.5,
                Foreground = AvTokens.Brush(AvTokens.TextDim),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            var v = new TextBlock
            {
                Text = value,
                FontFamily = AvTokens.Mono,
                FontSize = 11.5,
                Foreground = AvTokens.Brush(AvTokens.Text),
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            };
            WpfGrid.SetColumn(k, 0);
            WpfGrid.SetColumn(v, 1);
            grid.Children.Add(k);
            grid.Children.Add(v);

            return new WpfBorder
            {
                Padding = new Thickness(0, 9, 0, 9),
                BorderBrush = lastRow ? Brushes.Transparent : AvTokens.Brush(AvTokens.Border),
                BorderThickness = new Thickness(0, 0, 0, lastRow ? 0 : 1),
                Child = grid
            };
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  AddInConstants
    // ══════════════════════════════════════════════════════════════════════

    internal static class AddInConstants
    {
        internal const string SharedParameterFilePath =
            @"C:\Users\anghi\Desktop\Noruegos 2.0\07-JUL 15 Familias Hilti\MEKANOS.txt";

        internal const int FamilyScanCap = 16;
        internal const string SelectionMode = "Use Current Selection";

        internal static readonly IReadOnlyList<string> ModeChoices = new[]
        {
            SelectionMode, "Generic Models", "Furniture",
            "Mechanical Equipment", "Pipe Fittings", "Duct Fittings", "Specialty Equipment"
        };

        internal static readonly IReadOnlyList<string> RevitGroupChoices = new[]
        {
            "Identity Data", "Text", "Dimensions", "Graphics",
            "Constraints", "Data", "Analysis Results", "General"
        };

        internal static readonly IReadOnlyDictionary<string, ForgeTypeId> GroupForgeIds =
            new Dictionary<string, ForgeTypeId>
            {
                { "Identity Data",    GroupTypeId.IdentityData    },
                { "Text",             GroupTypeId.Text            },
                { "Dimensions",       GroupTypeId.Geometry        },
                { "Graphics",         GroupTypeId.Graphics        },
                { "Constraints",      GroupTypeId.Constraints     },
                { "Data",             GroupTypeId.Data            },
                { "Analysis Results", GroupTypeId.AnalysisResults },
                { "General",          GroupTypeId.General         },
            };
    }

    // ══════════════════════════════════════════════════════════════════════
    //  FamilyResolver
    // ══════════════════════════════════════════════════════════════════════

    internal static class FamilyResolver
    {
        internal static List<Family> Resolve(string mode, UIDocument uiDoc, Document doc)
        {
            if (mode == AddInConstants.SelectionMode)
                return ResolveFromSelection(uiDoc, doc);

            return new FilteredElementCollector(doc)
                .OfClass(typeof(Family))
                .Cast<Family>()
                .Where(f => f.FamilyCategory?.Name == mode)
                .ToList();
        }

        private static List<Family> ResolveFromSelection(UIDocument uiDoc, Document doc)
        {
            var result = new List<Family>();
            var seenIds = new HashSet<ElementId>();
            foreach (ElementId id in uiDoc.Selection.GetElementIds())
            {
                Family family = ExtractFamily(doc.GetElement(id));
                if (family != null && seenIds.Add(family.Id))
                    result.Add(family);
            }
            return result;
        }

        private static Family ExtractFamily(Element element)
        {
            if (element is Family f) return f;
            if (element is FamilyInstance fi) return fi.Symbol?.Family;
            if (element is FamilySymbol fs) return fs.Family;
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  DefaultFamilyLoadOptions
    // ══════════════════════════════════════════════════════════════════════

    internal class DefaultFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        { overwriteParameterValues = true; return true; }

        public bool OnSharedFamilyFound(
            Family sharedFamily, bool familyInUse,
            out FamilySource source, out bool overwriteParameterValues)
        { source = FamilySource.Family; overwriteParameterValues = true; return true; }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  AvesBaseWindow
    // ══════════════════════════════════════════════════════════════════════

    internal class AvesBaseWindow : Window
    {
        protected readonly StackPanel BodyPanel;
        private readonly WpfGrid _rootGrid;
        private readonly ScrollViewer _bodyScroll;

        protected AvesBaseWindow(string windowTitle)
        {
            // windowTitle only ever reached the in-window header strip, so every
            // window built on this base showed an empty OS title bar and an
            // unlabelled taskbar entry. All five subclasses were affected, the
            // three UnifiedReportWindow callers among them.
            Title = windowTitle;
            FontFamily = AvTokens.Ui;
            Background = AvTokens.Brush(AvTokens.Surface);
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            _rootGrid = new WpfGrid();
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _rootGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Header strip ─────────────────────────────────────────────
            // Was hand-built here at the same 52px on Surface, so geometry is
            // unchanged; the logo goes 20px to 24px and picks up the 2x decode,
            // and its left inset 16px to 20px. That reaches all five subclasses,
            // the two settings windows and both pickers included, not only
            // UnifiedReportWindow.
            var header = AvControls.ReportHeaderBar(windowTitle);
            WpfGrid.SetRow(header, 0);
            _rootGrid.Children.Add(header);

            // ── Body scroll area ──────────────────────────────────────────
            BodyPanel = new StackPanel { Margin = new Thickness(20) };
            _bodyScroll = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = BodyPanel
            };
            WpfGrid.SetRow(_bodyScroll, 1);
            _rootGrid.Children.Add(_bodyScroll);

            base.Content = _rootGrid;
        }

        /// <summary>
        /// Grows the window by however much this body ScrollViewer is currently
        /// short, and does nothing when it already fits.
        ///
        /// The body sits in the Star row of a 3-row Grid, so its viewport is
        /// whatever the header and footer leave over. A window height that
        /// under-provisions that row by even a few pixels puts a scrollbar down
        /// the side of the body -- visually adjacent to any list inside it, which
        /// is how SinglePickerWindow's outer scrollbar was mistaken for its
        /// ListBox's through three rounds of fixes aimed at the wrong element.
        ///
        /// Asking the layout how short it is beats predicting it: the chrome is
        /// an aggregate of non-client frame, header, footer, margins and whatever
        /// each window puts above its list, and any of those changing silently
        /// reintroduces the overflow.
        ///
        /// Call from Loaded at ContextIdle, once layout has settled. Converges in
        /// one pass -- growing the window grows the viewport while the content
        /// stays put.
        /// </summary>
        protected void TopUpResidualHeight()
        {
            double residual = _bodyScroll.ScrollableHeight;
            if (residual > 1) Height += residual;
        }

        protected void SetFooter(UIElement footerContent)
        {
            var footer = Av.FooterStrip(footerContent);
            WpfGrid.SetRow(footer, 2);
            _rootGrid.Children.Add(footer);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  SinglePickerWindow
    //  FIX: Added PassWheelToSelf handler on the ListBox so the outer
    //  ScrollViewer in AvesBaseWindow no longer intercepts mouse wheel events.
    // ══════════════════════════════════════════════════════════════════════

    internal class SinglePickerWindow : AvesBaseWindow
    {
        private readonly ListBox _listBox;
        private readonly List<string> _allItems;
        public string SelectedValue { get; private set; }

        /// <summary>
        /// False until Loaded has measured the list and pinned the window height.
        /// The SizeChanged handler is gated on it: that handler assigns an
        /// explicit ListBox height, and the layout passes that run while the
        /// window is opening would otherwise do so BEFORE the measurement, which
        /// is precisely what would destroy the auto-sizing it needs to read.
        /// </summary>
        private bool _measured;

        /// <summary>
        /// Everything in the window that is not the list: non-client chrome, the
        /// 52px header, the footer, the 20px body margins and the search row.
        /// AvesBaseWindow owns most of that and is deliberately not restructured
        /// here, so this stands in for the lot.
        ///
        /// Calibrated, not estimated. At 214 the body overflowed AvesBaseWindow's
        /// own ScrollViewer by a measured 30.4px -- constant across list lengths,
        /// because the shortfall is in the chrome rather than the rows. That
        /// produced an OUTER scrollbar sitting where the list's own would be, so
        /// three rounds of adjusting the ListBox chased a list that was already
        /// sized correctly. 214 + 30.4 + ~1.6 for DPI rounding = 246.
        ///
        /// Both the opening height and the resize handler are expressed in terms
        /// of this one value, so correcting it fixes them together and they cannot
        /// drift apart. ApplyMeasuredHeight also tops up any residual at runtime,
        /// so a future change to the header, footer or search row self-corrects
        /// rather than silently reintroducing the overflow.
        /// </summary>
        private const double ChromeAllowance = 246;

        /// <summary>
        /// Opening-size hint only, NOT the final height. Two rounds of estimating
        /// a row's height from padding and line metrics both came out short and
        /// left the scrollbar showing, so the real height is measured from the
        /// laid-out ListBox in Loaded instead. These constants only pick a
        /// starting size so the corrective resize is a few pixels rather than a
        /// jarring jump.
        /// </summary>
        private const double RowHeightHint = 40;
        private const double ListBoxChromeHint = 6;

        /// <summary>
        /// Floor for the list, about three rows. Shared with the resize handler
        /// so opening and dragging cannot disagree about the minimum. A one- or
        /// two-item list therefore keeps a little slack rather than collapsing to
        /// a sliver.
        /// </summary>
        private const double MinListHeight = 120;

        /// <summary>Ceiling for the list; longer lists stop here and scroll.</summary>
        private const double MaxListHeight = 400;

        public static string Show(
            string title, IEnumerable<string> items, string current, Window owner)
        {
            var w = new SinglePickerWindow(title, items, current) { Owner = owner };
            return w.ShowDialog() == true ? w.SelectedValue : null;
        }

        private SinglePickerWindow(string title, IEnumerable<string> items, string current)
            : base(title)
        {
            _allItems = items.ToList();

            // Opening size only. The real height comes from measuring the
            // laid-out list in Loaded -- see ApplyMeasuredHeight.
            //
            // SizeToContent is deliberately NOT used: it would feed back into the
            // SizeChanged handler below, each deriving its size from the other.
            double hintHeight = Math.Clamp(
                _allItems.Count * RowHeightHint + ListBoxChromeHint,
                MinListHeight, MaxListHeight);

            Width = 360;
            Height = hintHeight + ChromeAllowance;
            MinWidth = 320;
            MinHeight = MinListHeight + ChromeAllowance;
            // Never taller than the display, however long the list.
            MaxHeight = SystemParameters.WorkArea.Height * 0.9;

            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            // ToolWindow gives a close-only title bar while KEEPING resize.
            // ResizeMode.NoResize also hides minimize/maximize but takes resizing
            // with it, which a long list needs.
            WindowStyle = WindowStyle.ToolWindow;
            ResizeMode = ResizeMode.CanResize;

            BodyPanel.Children.Add(BuildSearchBox());

            _listBox = new ListBox
            {
                // No explicit Height: the list sizes itself to its items, which
                // is the only way to learn their true height. MaxHeight still
                // bounds it, so it keeps owning its own scroll region inside the
                // outer BodyPanel ScrollViewer rather than growing without limit
                // (see the wheel-routing fixes below). Loaded then pins the height
                // and SizeChanged drives it from there.
                MinHeight = MinListHeight,
                MaxHeight = MaxListHeight,
                Margin = new Thickness(0, 0, 0, 0),
                FontFamily = AvTokens.Ui,
                FontSize = 13,
                Background = AvTokens.Brush(AvTokens.Surface),
                BorderBrush = AvTokens.Brush(AvTokens.Border),
                BorderThickness = new Thickness(1)
            };
            _listBox.ItemContainerStyle = AvControls.PickerListItemStyle();

            // Auto is already the ListBox default; set explicitly so the intent
            // is on the record. TemplatePickerWindow deliberately forces Visible
            // to keep the wheel from bubbling to the window -- this window does
            // not need that, because PassWheelToSelf below re-raises the wheel on
            // the ListBox whether or not a scrollbar is showing.
            _listBox.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty,
                ScrollBarVisibility.Auto);
            _listBox.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty,
                ScrollBarVisibility.Disabled);

            _listBox.ItemsSource = _allItems;
            if (current != null) _listBox.SelectedItem = current;
            _listBox.MouseDoubleClick += (_, __) => Confirm();

            // FIX: Intercept the bubbling MouseWheel event and re-fire it
            // directly on the ListBox so it scrolls instead of the outer ScrollViewer.
            _listBox.PreviewMouseWheel += PassWheelToSelf;

            BodyPanel.Children.Add(_listBox);

            // Manual resize only, and only once the list has been measured. The
            // list is NOT re-sized when the search filter shrinks it: the window
            // would jump while the user is typing, moving the footer buttons
            // under the cursor, and it would discard any size the user had chosen.
            SizeChanged += (_, __) =>
            {
                if (!_measured) return;
                _listBox.Height = Math.Max(MinListHeight, ActualHeight - ChromeAllowance);
            };

            Loaded += (_, __) => ApplyMeasuredHeight();

            // Footer
            var cancelBtn = AvControls.GhostButton("Cancel");
            cancelBtn.Click += (_, __) => { DialogResult = false; };
            var okBtn = AvControls.PrimaryButton("OK");
            okBtn.Click += (_, __) => Confirm();

            var footerRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            footerRow.Children.Add(cancelBtn);
            footerRow.Children.Add(new WpfBorder { Width = 10 });
            footerRow.Children.Add(okBtn);
            SetFooter(footerRow);
        }

        /// <summary>
        /// Sizes the window from the list's REAL height, once WPF has laid it out.
        ///
        /// Estimating a row's height from padding and line metrics was tried twice
        /// and came out short both times, leaving a scrollbar on a list that fitted
        /// with room to spare. Asking the laid-out control what it actually
        /// measures removes the guess entirely.
        ///
        /// The list is auto-sized and MaxHeight-bounded, so ActualHeight is the
        /// content height for short lists and the cap for long ones -- exactly the
        /// two cases that need different treatment. Pinning it afterwards hands
        /// control to SizeChanged for manual resizing.
        ///
        /// Cost: a brief resize as the window corrects itself on open, kept small
        /// by opening near the estimated size.
        /// </summary>
        private void ApplyMeasuredHeight()
        {
            // Loaded can fire before the list has been arranged, so settle layout
            // first rather than trusting ActualHeight to be populated.
            _listBox.UpdateLayout();

            double measured = _listBox.ActualHeight;
            if (measured <= 0) measured = _listBox.DesiredSize.Height;
            if (measured <= 0) measured = MinListHeight;

            measured = Math.Clamp(measured, MinListHeight, MaxListHeight);

            // Pin the measured height, then let SizeChanged drive it from here.
            _listBox.Height = measured;
            Height = measured + ChromeAllowance;
            _measured = true;

            // Safety net for ChromeAllowance. That constant stands in for the
            // header, footer, body margins and search row all at once, so a change
            // to any of them would silently leave the body overflowing
            // AvesBaseWindow's ScrollViewer again -- which is what produced the
            // outer scrollbar this whole fix chased.
            //
            // Ask the layout how short it still is and add exactly that. With the
            // constant correct this measures ~0 and never fires, so there is no
            // second resize in the normal case. It converges in one pass: growing
            // the window grows the viewport while the content stays put.
            Dispatcher.BeginInvoke(new Action(TopUpResidualHeight),
                DispatcherPriority.ContextIdle);
        }

        // TopUpResidualHeight now lives on AvesBaseWindow, which owns the body
        // ScrollViewer directly and so needs no visual-tree walk to reach it.
        // MultiPickerWindow needs the same safeguard, and a second private copy
        // here is exactly the drift this file has spent the session removing.

        /// <summary>
        /// Search row matching the other AVES pickers: Surface-2 field, 1px
        /// BorderStrong, radius 5, magnifier glyph, live case-insensitive filter.
        /// </summary>
        private WpfBorder BuildSearchBox()
        {
            var grid = new WpfGrid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var magnifier = new TextBlock
            {
                Text = "⌕",
                FontSize = 14,
                Foreground = AvTokens.Brush(AvTokens.TextFaint),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };

            var searchBox = new WpfTextBox
            {
                FontFamily = AvTokens.Ui,
                FontSize = 13,
                Foreground = AvTokens.Brush(AvTokens.Text),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            searchBox.TextChanged += (_, __) => FilterList(searchBox.Text);

            WpfGrid.SetColumn(magnifier, 0);
            WpfGrid.SetColumn(searchBox, 1);
            grid.Children.Add(magnifier);
            grid.Children.Add(searchBox);

            Loaded += (_, __) => searchBox.Focus();

            return new WpfBorder
            {
                Background = AvTokens.Brush(AvTokens.Surface2),
                BorderBrush = AvTokens.Brush(AvTokens.BorderStrong),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 9, 10, 9),
                Margin = new Thickness(0, 0, 0, 12),
                Child = grid
            };
        }

        /// <summary>
        /// Live filter. Re-selects the first match when the previous selection
        /// drops out of the filtered set, so Confirm always has something to
        /// return -- the same rule the other AVES pickers use.
        /// </summary>
        private void FilterList(string query)
        {
            List<string> filtered = string.IsNullOrWhiteSpace(query)
                ? _allItems
                : _allItems
                    .Where(n => n.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();

            _listBox.ItemsSource = filtered;

            if (_listBox.SelectedItem == null && filtered.Count > 0)
                _listBox.SelectedIndex = 0;
        }

        private void Confirm()
        {
            if (_listBox.SelectedItem == null) return;
            SelectedValue = (string)_listBox.SelectedItem;
            DialogResult = true;
        }

        // FIX: Marks the bubbling event as handled, then re-raises it as a
        // direct event on the ListBox itself so the ListBox scrolls normally.
        private static void PassWheelToSelf(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            e.Handled = true;
            var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };
            ((UIElement)sender).RaiseEvent(args);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  MultiPickerWindow
    //  FIX: Added PassWheelToSelf handler on the inner ScrollViewer so the
    //  outer ScrollViewer in AvesBaseWindow no longer intercepts mouse wheel events.
    // ══════════════════════════════════════════════════════════════════════

    internal class MultiPickerWindow : AvesBaseWindow
    {
        /// <summary>
        /// One entry per item. AvControls.CheckRow returns the visible Border and
        /// an invisible CheckBox that carries the state, so both are needed: the
        /// Border is what gets shown, hidden by the filter and clicked; the
        /// CheckBox is what is counted and read back.
        /// </summary>
        private readonly List<(Border Visual, CheckBox Box, string Item)> _rows = new();
        private readonly TextBlock _selectionCount;
        private List<string> _allItems;

        public List<string> SelectedValues { get; private set; }

        public static List<string> Show(
            string title, IEnumerable<string> items, IEnumerable<string> current, Window owner)
        {
            var w = new MultiPickerWindow(title, items, current) { Owner = owner };
            return w.ShowDialog() == true ? w.SelectedValues : null;
        }

        private MultiPickerWindow(
            string title, IEnumerable<string> items, IEnumerable<string> current)
            : base(title)
        {
            // 688 = the 440 list below plus 248 of chrome. Calibrated, not
            // guessed: at 660 the body still overflowed AvesBaseWindow's
            // ScrollViewer by a measured 26.4px, so the window grew visibly a
            // moment after opening. 660 + 26.4 + ~1.6 for DPI rounding = 688.
            //
            // The 220 this replaces was inherited from the old 520/300 pair and
            // had never been calibrated. SinglePickerWindow's equivalent measured
            // 246 by the same method -- two windows arriving within 2px of each
            // other, both about 30 short of their real chrome.
            //
            // TopUpResidualHeight in Loaded stays as the safety net, and with
            // this value it measures ~0 and never visibly fires.
            Width = 420; Height = 688;
            // Never taller than the display: 688 is comfortable at 1080p but
            // would overflow a 768p laptop without this.
            MaxHeight = SystemParameters.WorkArea.Height * 0.9;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.CanResize;

            // No gating needed, unlike SinglePickerWindow: this window has no
            // SizeChanged handler, so nothing can overwrite the corrected height
            // between layout passes.
            Loaded += (_, __) => Dispatcher.BeginInvoke(
                new Action(TopUpResidualHeight), DispatcherPriority.ContextIdle);

            _allItems = items.ToList();
            var currentSet = current != null
                ? new HashSet<string>(current)
                : new HashSet<string>();

            // ── Search box ────────────────────────────────────────────────
            var searchRow = new WpfBorder
            {
                Background = AvTokens.Brush(AvTokens.Surface2),
                BorderBrush = AvTokens.Brush(AvTokens.BorderStrong),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(10, 9, 10, 9),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var searchGrid = new WpfGrid();
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            searchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var magnifier = new TextBlock
            {
                Text = "⌕",
                FontSize = 14,
                Foreground = AvTokens.Brush(AvTokens.TextFaint),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var searchBox = new WpfTextBox
            {
                FontFamily = AvTokens.Ui,
                FontSize = 13,
                Foreground = AvTokens.Brush(AvTokens.Text),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                VerticalAlignment = VerticalAlignment.Center
            };
            searchBox.GotFocus += (_, __) => { };
            searchBox.TextChanged += (s, e) => FilterItems(searchBox.Text);

            WpfGrid.SetColumn(magnifier, 0);
            WpfGrid.SetColumn(searchBox, 1);
            searchGrid.Children.Add(magnifier);
            searchGrid.Children.Add(searchBox);
            searchRow.Child = searchGrid;
            BodyPanel.Children.Add(searchRow);

            // ── Checkbox list in a bordered scroll ─────────────────────────
            var itemStack = new StackPanel();
            foreach (string item in _allItems)
            {
                var (visual, box) = AvControls.CheckRow(item, false);

                // CheckRow's CheckBox is deliberately invisible -- the visible
                // control is its custom square -- so the row is only toggleable
                // if the caller wires the Border. Without this the rows are dead.
                visual.MouseLeftButtonUp += (_, __) => box.IsChecked = box.IsChecked != true;

                // Set after wiring, so CheckRow's own Checked handler paints the
                // pre-checked rows rather than leaving them visually unchecked.
                box.IsChecked = currentSet.Contains(item);

                box.Checked += (_, __) => UpdateCount();
                box.Unchecked += (_, __) => UpdateCount();

                _rows.Add((visual, box, item));
                itemStack.Children.Add(visual);
            }

            // FIX: Extract the inner ScrollViewer so we can attach the
            // wheel handler to it before wrapping it in the border.
            var innerScroll = new ScrollViewer
            {
                // FIX: Fixed height ensures this ScrollViewer owns the scroll
                // region rather than deferring to the outer AvesBaseWindow ScrollViewer.
                //
                // 440 = ten CheckRow rows at 44px. Was 300, which showed under
                // seven rows once the rows grew from 28px to 44px in the CheckRow
                // migration. Ten rather than twelve keeps the window off the
                // bottom of a 768p screen.
                Height = 440,
                Content = itemStack,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };

            // FIX: Intercept the bubbling MouseWheel event and re-fire it
            // directly on the inner ScrollViewer so it scrolls instead of the outer one.
            innerScroll.PreviewMouseWheel += PassWheelToSelf;

            var listBorder = new WpfBorder
            {
                BorderBrush = AvTokens.Brush(AvTokens.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                ClipToBounds = true,
                Child = innerScroll
            };
            BodyPanel.Children.Add(listBorder);

            // ── Footer ─────────────────────────────────────────────────────
            _selectionCount = new TextBlock
            {
                FontFamily = AvTokens.Ui,
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                Foreground = AvTokens.Brush(AvTokens.TextDim),
                VerticalAlignment = VerticalAlignment.Center
            };
            UpdateCount();

            var cancelBtn = AvControls.GhostButton("Cancel");
            cancelBtn.Click += (_, __) => { DialogResult = false; };
            var addBtn = AvControls.PrimaryButton("Add parameters");
            addBtn.Click += (_, __) =>
            {
                SelectedValues = _rows
                    .Where(r => r.Box.IsChecked == true && r.Visual.IsVisible)
                    .Select(r => r.Item)
                    .ToList();
                DialogResult = true;
            };

            var footerRow = new WpfGrid();
            footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            WpfGrid.SetColumn(_selectionCount, 0);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            btnRow.Children.Add(cancelBtn);
            btnRow.Children.Add(new WpfBorder { Width = 10 });
            btnRow.Children.Add(addBtn);
            WpfGrid.SetColumn(btnRow, 1);

            footerRow.Children.Add(_selectionCount);
            footerRow.Children.Add(btnRow);
            SetFooter(footerRow);
        }

        // MakeCheckRow is gone: AvControls.CheckRow supplies the row now, with the
        // custom 17px square, the Ui-13 label, and the checked-state highlight
        // (Accent square, Accent SemiBold label, AccentTint row) that the old
        // Mono-12 checkbox had no equivalent of.

        private void FilterItems(string query)
        {
            foreach (var r in _rows)
            {
                r.Visual.Visibility = string.IsNullOrEmpty(query) ||
                                      r.Item.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                    ? WpfVisibility.Visible
                    : WpfVisibility.Collapsed;
            }
            UpdateCount();
        }

        private void UpdateCount()
        {
            int n = _rows.Count(r => r.Box.IsChecked == true);
            _selectionCount.Text = $"{n} selected";
        }

        // FIX: Marks the bubbling event as handled, then re-raises it as a
        // direct event on the inner ScrollViewer so it scrolls normally.
        private static void PassWheelToSelf(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled) return;
            e.Handled = true;
            var args = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = sender
            };
            ((UIElement)sender).RaiseEvent(args);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  BatchProgressWindow
    // ══════════════════════════════════════════════════════════════════════

    internal class BatchProgressWindow : Window
    {
        private readonly ProgressBar _progressBar;
        private readonly TextBlock _familyLabel;
        private readonly TextBlock _countLabel;
        private readonly TextBlock _captionLabel;
        private int _total;
        private readonly string _itemName;
        private readonly string _itemNamePlural;

        public bool IsCancelled { get; private set; }

        public BatchProgressWindow(string windowTitle, int total, string itemName = "family", string itemNamePlural = "families")
        {
            _itemName = itemName;
            _itemNamePlural = itemNamePlural;
            _total = total;
            Width = 480;
            Height = 220;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            FontFamily = AvTokens.Ui;
            Background = Brushes.White;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            var root = new WpfGrid { Background = Brushes.White };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var brand = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(16, 0, 0, 0)
            };
            var tile = Av.BrandTile(20);
            tile.Margin = new Thickness(0, 0, 10, 0);
            brand.Children.Add(tile);
            brand.Children.Add(new TextBlock
            {
                Text = windowTitle,
                FontFamily = AvTokens.Ui,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = AvTokens.Brush(AvTokens.Text),
                VerticalAlignment = VerticalAlignment.Center
            });
            var header = new WpfBorder
            {
                Height = 52,
                Background = AvTokens.Brush(AvTokens.Surface),
                BorderBrush = AvTokens.Brush(AvTokens.Border),
                BorderThickness = new Thickness(0, 0, 0, 1),
                Child = brand
            };
            WpfGrid.SetRow(header, 0);
            root.Children.Add(header);

            var body = new StackPanel { Margin = new Thickness(20, 18, 20, 20) };

            var statusGrid = new WpfGrid { Margin = new Thickness(0, 0, 0, 12) };
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _familyLabel = new TextBlock
            {
                Text = "Starting…",
                FontFamily = AvTokens.Ui,
                FontSize = 13,
                Foreground = AvTokens.Brush(AvTokens.Text),
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            WpfGrid.SetColumn(_familyLabel, 0);

            _countLabel = new TextBlock
            {
                Text = $"0 / {total}",
                FontFamily = AvTokens.Mono,
                FontSize = 12,
                Foreground = AvTokens.Brush(AvTokens.TextDim),
                VerticalAlignment = VerticalAlignment.Center
            };
            WpfGrid.SetColumn(_countLabel, 1);

            statusGrid.Children.Add(_familyLabel);
            statusGrid.Children.Add(_countLabel);
            body.Children.Add(statusGrid);

            _progressBar = Av.RoundedProgressBar(total);
            _progressBar.Margin = new Thickness(0, 0, 0, 10);
            body.Children.Add(_progressBar);

            _captionLabel = new TextBlock
            {
                FontFamily = AvTokens.Ui,
                FontSize = 11.5,
                Foreground = AvTokens.Brush(AvTokens.TextFaint)
            };
            body.Children.Add(_captionLabel);

            WpfGrid.SetRow(body, 1);
            root.Children.Add(body);

            Content = root;
        }

        public void Update(int current, int total, string status)
        {
            _total = total;
            _familyLabel.Text = status;
            _countLabel.Text = $"{current} / {total}";
            _progressBar.Maximum = total;
            _progressBar.Value = current;

            int remaining = total - current;
            _captionLabel.Text = remaining > 0
                ? $"{remaining} {(remaining == 1 ? _itemName : _itemNamePlural)} remaining"
                : "Finishing up…";

            Dispatcher.Invoke(new Action(() => { }), DispatcherPriority.Render);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Shared report row types
    // ══════════════════════════════════════════════════════════════════════

    public class ReportSummaryRow { public string Setting { get; set; } public string Value { get; set; } }
    public class ReportWarningRow { public string Item { get; set; } public string Reason { get; set; } }

    public class AddParamReportRow
    {
        public string FamilyName { get; set; }
        public string Status { get; set; }
        public string Added { get; set; }
        public string AlreadyExisted { get; set; }
        public string AddedParameters { get; set; }
        public string Notes { get; set; }
    }

    public class RemoveParamReportRow
    {
        public string FamilyName { get; set; }
        public string Status { get; set; }
        public string Removed { get; set; }
        public string NotFound { get; set; }
        public string RemovedParameters { get; set; }
        public string Notes { get; set; }
    }

    public class CsvItemReportRow
    {
        public string ItemNumber { get; set; }
        public string Status { get; set; }
        public string SetCount { get; set; }
        public string FailedCount { get; set; }
        public string SkippedCount { get; set; }
        public string SetParameters { get; set; }
        public string FailedParameters { get; set; }
        public string SkippedParameters { get; set; }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  UnifiedReportWindow
    // ══════════════════════════════════════════════════════════════════════

    internal class UnifiedReportWindow : AvesBaseWindow
    {
        private readonly string _clipboardText;

        public UnifiedReportWindow(
            string title,
            IReadOnlyList<(string setting, string value)> summary,
            IReadOnlyList<(string header, double width, string bindingPath)> resultColumns,
            IEnumerable<object> resultRows,
            IReadOnlyList<(string item, string reason)> warnings = null,
            string footerText = null)
            : base(title)
        {
            Width = 900; Height = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            var resultList = resultRows.ToList();

            BodyPanel.Children.Add(AvControls.SectionLabel("Run Summary"));

            var summaryGrid = new WpfGrid { Margin = new Thickness(0, 0, 0, 20) };
            summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            summaryGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });

            var tilesPanel = BuildStatTiles(summary);
            WpfGrid.SetColumn(tilesPanel, 0);
            summaryGrid.Children.Add(tilesPanel);

            var configPanel = BuildConfigPanel(summary);
            WpfGrid.SetColumn(configPanel, 1);
            summaryGrid.Children.Add(configPanel);

            BodyPanel.Children.Add(summaryGrid);

            var sectionRow = new WpfGrid { Margin = new Thickness(0, 0, 0, 8) };
            sectionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            sectionRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var sectionLbl = AvControls.SectionLabel("Results per Family");
            sectionLbl.Margin = new Thickness(0);
            WpfGrid.SetColumn(sectionLbl, 0);
            sectionRow.Children.Add(sectionLbl);

            var countLbl = new TextBlock
            {
                Text = $"showing {resultList.Count} {(resultList.Count == 1 ? "family" : "families")}",
                FontFamily = AvTokens.Ui,
                FontSize = 11.5,
                Foreground = AvTokens.Brush(AvTokens.TextFaint),
                VerticalAlignment = VerticalAlignment.Center
            };
            WpfGrid.SetColumn(countLbl, 1);
            sectionRow.Children.Add(countLbl);
            BodyPanel.Children.Add(sectionRow);

            var resultGrid = Av.StyledDataGrid();
            foreach (var (header, width, bindingPath) in resultColumns)
            {
                if (bindingPath == "Status")
                    resultGrid.Columns.Add(Av.PillColumn("Status", width));
                else
                    resultGrid.Columns.Add(Av.TextColumn(header, bindingPath, width));
            }
            resultGrid.ItemsSource = resultList;
            resultGrid.Margin = new Thickness(0, 0, 0, 16);
            BodyPanel.Children.Add(resultGrid);

            if (warnings != null && warnings.Count > 0)
            {
                BodyPanel.Children.Add(AvControls.SectionLabel("Warnings"));
                var warnGrid = Av.StyledDataGrid();
                warnGrid.Columns.Add(Av.TextColumn("Item", "Item", 240));
                warnGrid.Columns.Add(Av.TextColumn("Reason", "Reason", 500));
                warnGrid.ItemsSource = warnings
                    .Select(w => new ReportWarningRow { Item = w.item, Reason = w.reason })
                    .ToList();
                warnGrid.Margin = new Thickness(0, 0, 0, 16);
                BodyPanel.Children.Add(warnGrid);
            }

            _clipboardText = BuildClipboardText(title, summary, resultColumns, resultList, warnings);

            // Rendered blank whenever footerText was null, which was two of the
            // three callers: only the CSV importer passed anything. Both now pass
            // their document title, so the caption says what the other seven
            // report windows say. The fallback is a backstop for a future caller
            // that forgets, not something that fires today.
            var sourceCaption = AvControls.FooterCaption(
                !string.IsNullOrEmpty(footerText)
                    ? $"Source · {System.IO.Path.GetFileName(footerText)}"
                    : "Source · (unknown)");

            var copyBtn = AvControls.SecondaryButton("⧉  Copy to clipboard");
            copyBtn.Click += (_, __) =>
            {
                Clipboard.SetText(_clipboardText);
                copyBtn.Content = "✓  Copied!";
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                timer.Tick += (s, e) => { copyBtn.Content = "⧉  Copy to clipboard"; timer.Stop(); };
                timer.Start();
            };

            var closeBtn = AvControls.PrimaryButton("Close");
            closeBtn.Click += (_, __) => Close();

            var footerGrid = new WpfGrid();
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            WpfGrid.SetColumn(sourceCaption, 0);
            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            btnRow.Children.Add(copyBtn);
            btnRow.Children.Add(new WpfBorder { Width = 10 });
            btnRow.Children.Add(closeBtn);
            WpfGrid.SetColumn(btnRow, 1);

            footerGrid.Children.Add(sourceCaption);
            footerGrid.Children.Add(btnRow);
            SetFooter(footerGrid);
        }

        private static UniformGrid BuildStatTiles(IReadOnlyList<(string setting, string value)> summary)
        {
            var grid = new UniformGrid { Columns = 3, Margin = new Thickness(0, 0, 12, 0) };

            foreach (var (setting, value) in summary)
            {
                string accent = "";
                if (setting.Contains("✅") || setting.ToLower().Contains("processed")) accent = "ok";
                else if (setting.Contains("❌") || setting.ToLower().Contains("failed")) accent = "fail";
                else if (setting.Contains("⚠") || setting.ToLower().Contains("mismatch")) accent = "warn";
                else if (setting.ToLower().Contains("added") && !setting.ToLower().Contains("already")) accent = "accent";

                bool looksNumeric = int.TryParse(value, out _);
                if (!looksNumeric) continue;

                string label = setting.Replace("✅", "").Replace("⏭", "").Replace("❌", "").Replace("⚠", "").Trim();
                var tile = AvControls.StatTile(value, label, accent);
                tile.Margin = new Thickness(0, 0, 8, 8);
                grid.Children.Add(tile);
            }

            return grid;
        }

        private static WpfBorder BuildConfigPanel(IReadOnlyList<(string setting, string value)> summary)
        {
            var configRows = summary
                .Where(r => !int.TryParse(r.value, out _))
                .ToList();

            var stack = new StackPanel();
            for (int i = 0; i < configRows.Count; i++)
            {
                bool last = i == configRows.Count - 1;
                string key = configRows[i].setting;
                string value = configRows[i].value;
                stack.Children.Add(Av.ConfigRow(key, value, last));
            }

            return new WpfBorder
            {
                Background = AvTokens.Brush(AvTokens.Surface2),
                BorderBrush = AvTokens.Brush(AvTokens.Border),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(14, 6, 14, 6),
                Child = stack,
                VerticalAlignment = VerticalAlignment.Top
            };
        }

        private static string BuildClipboardText(
            string title,
            IReadOnlyList<(string setting, string value)> summary,
            IReadOnlyList<(string header, double width, string bindingPath)> resultColumns,
            List<object> rows,
            IReadOnlyList<(string item, string reason)> warnings)
        {
            var sb = new StringBuilder();
            sb.AppendLine(title);
            sb.AppendLine(new string('=', title.Length));
            sb.AppendLine();

            sb.AppendLine("RUN SUMMARY");
            sb.AppendLine("Setting\tValue");
            foreach (var (setting, value) in summary)
                sb.AppendLine($"{setting}\t{value}");
            sb.AppendLine();

            sb.AppendLine("RESULTS");
            sb.AppendLine(string.Join("\t", resultColumns.Select(c => c.header)));
            foreach (object row in rows)
            {
                Type rowType = row.GetType();
                var cells = resultColumns.Select(col =>
                {
                    PropertyInfo prop = rowType.GetProperty(col.bindingPath);
                    return prop?.GetValue(row)?.ToString() ?? string.Empty;
                });
                sb.AppendLine(string.Join("\t", cells));
            }

            if (warnings != null && warnings.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("WARNINGS");
                sb.AppendLine("Item\tReason");
                foreach (var (item, reason) in warnings)
                    sb.AppendLine($"{item}\t{reason}");
            }

            return sb.ToString();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  SharedParamFileManager
    //  Per-user remembered path to a shared parameters .txt file that each
    //  user downloads manually from the company Google Drive. No network
    //  calls — this only manages a local file picker + a remembered path
    //  cached in %AppData%\AVES\shared_param_path.txt.
    // ══════════════════════════════════════════════════════════════════════

    internal static class SharedParamFileManager
    {
        private static readonly string ConfigDir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVES");

        private static readonly string ConfigFile =
            System.IO.Path.Combine(ConfigDir, "shared_param_path.txt");

        /// <summary>
        /// Returns the remembered path if it exists and is still valid on disk;
        /// otherwise returns null.
        /// </summary>
        internal static string TryGetSavedPath()
        {
            try
            {
                if (!System.IO.File.Exists(ConfigFile)) return null;
                string saved = System.IO.File.ReadAllText(ConfigFile).Trim();
                return (!string.IsNullOrEmpty(saved) && System.IO.File.Exists(saved)) ? saved : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// Persists the given path so it's remembered for this Windows user next time.
        /// </summary>
        internal static void SavePath(string path)
        {
            try
            {
                System.IO.Directory.CreateDirectory(ConfigDir);
                System.IO.File.WriteAllText(ConfigFile, path ?? string.Empty);
            }
            catch { /* Non-fatal — worst case the user is asked to pick again next run. */ }
        }

        /// <summary>
        /// Shows an OpenFileDialog for the user to select a shared parameters .txt file
        /// (typically one they've manually downloaded from the company Google Drive folder).
        /// Returns the chosen path, or null if the user cancelled.
        /// </summary>
        internal static string PromptForFile(Window owner = null)
        {
            var openDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Shared Parameters File (downloaded from Google Drive)",
                Filter = "Shared Parameter Files (*.txt)|*.txt|All Files (*.*)|*.*",
                DefaultExt = ".txt"
            };
            bool? result = owner != null ? openDialog.ShowDialog(owner) : openDialog.ShowDialog();
            return result == true ? openDialog.FileName : null;
        }

        /// <summary>
        /// Resolves the shared parameters file path for the current run: uses the
        /// remembered path if still valid, otherwise prompts the user and saves the choice.
        /// Holding Shift while launching the command forces the picker even if a
        /// saved path already exists, so users can re-point the add-in after
        /// re-downloading an updated file from Google Drive.
        /// </summary>
        internal static string Resolve(Window owner = null)
        {
            bool forceRePick = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);

            if (!forceRePick)
            {
                string saved = TryGetSavedPath();
                if (saved != null) return saved;
            }

            string picked = PromptForFile(owner);
            if (picked == null) return null;

            SavePath(picked);
            return picked;
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Cmd_FAM03a_SharedParams
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.Manual)]
    public class Cmd_FAM03a_SharedParams : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiApp = commandData.Application;
            var uiDoc = uiApp.ActiveUIDocument;
            var doc = uiDoc.Document;
            var app = uiApp.Application;

            string sharedParamPath = SharedParamFileManager.Resolve();
            if (sharedParamPath == null) return Result.Cancelled;

            app.SharedParametersFilename = sharedParamPath;
            DefinitionFile sharedParamFile = app.OpenSharedParameterFile();

            if (sharedParamFile == null)
            {
                AvControls.ShowAlert("Error",
                    $"Could not open the shared parameter file.\n\nPath: {sharedParamPath}", AlertSeverity.Error);
                return Result.Cancelled;
            }

            var groupsByName = new Dictionary<string, DefinitionGroup>();
            foreach (DefinitionGroup group in sharedParamFile.Groups)
                groupsByName[group.Name] = group;

            var sortedGroupNames = groupsByName.Keys.OrderBy(n => n).ToList();

            if (sortedGroupNames.Count == 0)
            {
                AvControls.ShowAlert("Error", "The shared parameter file contains no groups.", AlertSeverity.Error);
                return Result.Cancelled;
            }

            string defaultMode = uiDoc.Selection.GetElementIds().Count > 0
                ? AddInConstants.SelectionMode : "Generic Models";

            var settings = new SharedParamSettings
            {
                GroupName = sortedGroupNames[0],
                SelectedParams = new List<string>(),
                RevitGroupName = AddInConstants.RevitGroupChoices[0],
                Mode = defaultMode,
                IsInstance = true
            };

            while (true)
            {
                var win = new SharedParamSettingsWindow(settings, sortedGroupNames, groupsByName);
                if (win.ShowDialog() != true) return Result.Cancelled;
                if (settings.SelectedParams.Count > 0) break;
                AvControls.ShowAlert("Validation", "You must select at least one parameter before running.", AlertSeverity.Warning);
            }

            ForgeTypeId targetGroup = ResolveGroupForgeTypeId(settings.RevitGroupName);

            var definitionsToAdd = groupsByName[settings.GroupName].Definitions
                .Cast<Definition>().OfType<ExternalDefinition>()
                .Where(d => settings.SelectedParams.Contains(d.Name)).ToList();

            var familiesToProcess = FamilyResolver.Resolve(settings.Mode, uiDoc, doc);
            if (familiesToProcess.Count == 0)
            {
                AvControls.ShowAlert("No Families Found",
                    $"No families matched the selected mode / category:\n\"{settings.Mode}\"", AlertSeverity.Warning);
                return Result.Cancelled;
            }

            var familyResults = new List<AddParamReportRow>();
            int processedCount = 0, skippedCount = 0, failedCount = 0;
            int totalAdded = 0, totalAlreadyHad = 0, totalGroupMismatches = 0;

            var familyIds = familiesToProcess.Select(f => f.Id).ToList();
            var familyNames = familiesToProcess.Select(f => f.Name).ToList();
            int familyCount = familyIds.Count;

            var loadOptions = new DefaultFamilyLoadOptions();
            var progressWindow = new BatchProgressWindow("Processing Families…", familyCount);
            progressWindow.Show();

            for (int i = 0; i < familyCount; i++)
            {
                if (progressWindow.IsCancelled) break;

                string familyName = familyNames[i];
                progressWindow.Update(i + 1, familyCount, $"Processing: {familyName}");

                Family family = doc.GetElement(familyIds[i]) as Family;
                if (family == null)
                {
                    failedCount++;
                    familyResults.Add(new AddParamReportRow
                    {
                        FamilyName = familyName,
                        Status = "❌",
                        Added = "—",
                        AlreadyExisted = "—",
                        AddedParameters = "—",
                        Notes = "Family element is no longer accessible."
                    });
                    continue;
                }

                if (!family.IsEditable)
                {
                    skippedCount++;
                    familyResults.Add(new AddParamReportRow
                    {
                        FamilyName = familyName,
                        Status = "⏭",
                        Added = "0",
                        AlreadyExisted = "—",
                        AddedParameters = "—",
                        Notes = "System / Non-Editable"
                    });
                    continue;
                }

                Document familyDocument = null;
                try { familyDocument = doc.EditFamily(family); }
                catch (Exception ex)
                {
                    failedCount++;
                    familyResults.Add(new AddParamReportRow
                    {
                        FamilyName = familyName,
                        Status = "❌",
                        Added = "—",
                        AlreadyExisted = "—",
                        AddedParameters = "—",
                        Notes = $"Cannot open for editing — {ex.Message}"
                    });
                    continue;
                }

                app.SharedParametersFilename = sharedParamPath;
                DefinitionFile refreshedSpFile = app.OpenSharedParameterFile();
                List<ExternalDefinition> currentDefinitions = definitionsToAdd;

                if (refreshedSpFile != null)
                {
                    DefinitionGroup refreshedGroup = null;
                    foreach (DefinitionGroup g in refreshedSpFile.Groups)
                        if (g.Name == settings.GroupName) { refreshedGroup = g; break; }

                    if (refreshedGroup != null)
                    {
                        var freshDefs = refreshedGroup.Definitions
                            .Cast<Definition>().OfType<ExternalDefinition>()
                            .Where(d => settings.SelectedParams.Contains(d.Name)).ToList();
                        if (freshDefs.Count > 0) currentDefinitions = freshDefs;
                    }
                }

                try
                {
                    FamilyManager familyManager = familyDocument.FamilyManager;
                    var existingNames = new HashSet<string>(
                        familyManager.Parameters.Cast<FamilyParameter>().Select(p => p.Definition.Name));

                    var toAdd = currentDefinitions.Where(d => !existingNames.Contains(d.Name)).ToList();
                    var toSkip = currentDefinitions.Where(d => existingNames.Contains(d.Name))
                                                   .Select(d => d.Name).ToList();
                    List<string> groupMismatches = new List<string>();

                    if (toAdd.Count > 0)
                    {
                        using (var transaction = new Transaction(familyDocument, "Add Shared Parameters"))
                        {
                            transaction.Start();
                            foreach (ExternalDefinition definition in toAdd)
                                familyManager.AddParameter(definition, targetGroup, settings.IsInstance);
                            transaction.Commit();
                        }

                        familyDocument.LoadFamily(doc, loadOptions);
                        FamilyNameGuard.RestoreIfTruncated(doc, familyIds[i], familyName);

                        groupMismatches = DetectGroupCacheMismatches(
                            doc, familyIds[i], toAdd, targetGroup, settings.IsInstance);

                        totalAdded += toAdd.Count;
                        totalGroupMismatches += groupMismatches.Count;
                        processedCount++;
                    }
                    else
                    {
                        skippedCount++;
                    }

                    totalAlreadyHad += toSkip.Count;

                    string statusIcon = toAdd.Count > 0
                        ? (groupMismatches.Count > 0 ? "⚠" : "✅") : "⏭";
                    string notes = groupMismatches.Count == 0 ? string.Empty
                        : "Project cache forced different group: " + string.Join("; ", groupMismatches);

                    familyResults.Add(new AddParamReportRow
                    {
                        FamilyName = familyName,
                        Status = statusIcon,
                        Added = toAdd.Count.ToString(),
                        AlreadyExisted = toSkip.Count.ToString(),
                        AddedParameters = toAdd.Count > 0 ? string.Join(", ", toAdd.Select(d => d.Name)) : "—",
                        Notes = notes
                    });
                    familyDocument.Close(false);
                }
                catch (Exception ex)
                {
                    SafeClose(familyDocument);
                    failedCount++;
                    familyResults.Add(new AddParamReportRow
                    {
                        FamilyName = familyName,
                        Status = "❌",
                        Added = "—",
                        AlreadyExisted = "—",
                        AddedParameters = "—",
                        Notes = ex.Message
                    });
                }
            }

            progressWindow.Close();

            var summary = new (string, string)[]
            {
                ("Shared Parameter Group",     settings.GroupName),
                ("Mode / Category",            settings.Mode),
                ("Parameter Scope",            settings.IsInstance ? "Instance" : "Type"),
                ("Families Found",             familyCount.ToString()),
                ("Families Processed  ✅",     processedCount.ToString()),
                ("Families Skipped    ⏭",     skippedCount.ToString()),
                ("Families Failed     ❌",     failedCount.ToString()),
                ("Parameters Added",           totalAdded.ToString()),
                ("Parameters Already Existed", totalAlreadyHad.ToString()),
                ("Group Cache Mismatches  ⚠", totalGroupMismatches.ToString()),
            };

            var resultColumns = new (string, double, string)[]
            {
                ("Family Name",      220, "FamilyName"),
                ("Status",            95, "Status"),
                ("Added",             55, "Added"),
                ("Already Existed",  110, "AlreadyExisted"),
                ("Added Parameters", 350, "AddedParameters"),
                ("Notes",            280, "Notes"),
            };

            new UnifiedReportWindow(
                "Batch Parameter Injection",
                summary, resultColumns,
                familyResults.Cast<object>(),
                warnings: null,
                footerText: doc.Title).ShowDialog();

            return Result.Succeeded;
        }

        private static ForgeTypeId ResolveGroupForgeTypeId(string groupName)
        {
            return AddInConstants.GroupForgeIds.TryGetValue(groupName, out ForgeTypeId id)
                ? id : GroupTypeId.General;
        }

        private static List<string> DetectGroupCacheMismatches(
            Document doc, ElementId familyId,
            List<ExternalDefinition> addedDefinitions,
            ForgeTypeId requestedGroup, bool isInstance)
        {
            var mismatches = new List<string>();
            if (addedDefinitions.Count == 0) return mismatches;
            try
            {
                Family reloaded = doc.GetElement(familyId) as Family;
                if (reloaded == null) return mismatches;

                Element probe = null;
                if (isInstance)
                {
                    probe = new FilteredElementCollector(doc)
                        .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                        .FirstOrDefault(fi => fi.Symbol?.Family?.Id == familyId);
                }
                if (probe == null)
                {
                    ICollection<ElementId> symbolIds = reloaded.GetFamilySymbolIds();
                    if (symbolIds.Count > 0)
                        probe = doc.GetElement(symbolIds.First()) as FamilySymbol;
                }
                if (probe == null) return mismatches;

                foreach (ExternalDefinition def in addedDefinitions)
                {
                    Parameter p = probe.LookupParameter(def.Name);
                    if (p == null) continue;
                    if (!(p.Definition is InternalDefinition internalDef)) continue;
                    ForgeTypeId actualGroup;
                    try { actualGroup = internalDef.GetGroupTypeId(); } catch { continue; }
                    if (actualGroup == null || actualGroup.Equals(requestedGroup)) continue;
                    string actualLabel;
                    try { actualLabel = LabelUtils.GetLabelForGroup(actualGroup); } catch { actualLabel = actualGroup.TypeId; }
                    mismatches.Add($"{def.Name} → {actualLabel}");
                }
            }
            catch { }
            return mismatches;
        }

        private static void SafeClose(Document fd) { try { fd?.Close(false); } catch { } }

        private class SharedParamSettings
        {
            public string GroupName { get; set; }
            public List<string> SelectedParams { get; set; }
            public string RevitGroupName { get; set; }
            public string Mode { get; set; }
            public bool IsInstance { get; set; }
        }

        private class SharedParamSettingsWindow : AvesBaseWindow
        {
            private readonly SharedParamSettings _settings;
            private readonly List<string> _availableGroups;
            private readonly Dictionary<string, DefinitionGroup> _groupsByName;
            private readonly Button _groupBtn;
            private readonly WrapPanel _chipsPanel;
            private readonly Button _revitGroupBtn;
            private readonly Button _modeBtn;
            private readonly WpfToggleButton _instanceBtn;
            private readonly WpfToggleButton _typeBtn;

            public SharedParamSettingsWindow(
                SharedParamSettings settings,
                List<string> availableGroups,
                Dictionary<string, DefinitionGroup> groupsByName)
                : base("Batch Shared Parameters")
            {
                _settings = settings;
                _availableGroups = availableGroups;
                _groupsByName = groupsByName;

                Width = 520; Height = 620;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ResizeMode = ResizeMode.NoResize;

                BodyPanel.Children.Add(AvControls.FieldLabel(1, "Shared parameter group"));
                _groupBtn = AvControls.PickerButton(_settings.GroupName);
                _groupBtn.Margin = new Thickness(0, 0, 0, 16);
                _groupBtn.Click += (s, e) =>
                {
                    string picked = SinglePickerWindow.Show(
                        "Select Shared Parameter Group", _availableGroups, _settings.GroupName, this);
                    if (picked != null && picked != _settings.GroupName)
                    {
                        _settings.GroupName = picked;
                        _settings.SelectedParams.Clear();
                        AvControls.SetPickerValue(_groupBtn, picked);
                        RefreshChips();
                    }
                };
                BodyPanel.Children.Add(_groupBtn);

                BodyPanel.Children.Add(AvControls.FieldLabel(2, "Parameters to inject"));
                _chipsPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                BodyPanel.Children.Add(_chipsPanel);

                var addChipBtn = new Button
                {
                    Cursor = Cursors.Hand,
                    FontFamily = AvTokens.Ui,
                    FontSize = 12,
                    Foreground = AvTokens.Brush(AvTokens.TextDim),
                    Margin = new Thickness(0, 0, 0, 16),
                    Template = DashedChipTemplate()
                };
                addChipBtn.Content = "+ Add parameters";
                addChipBtn.Click += (s, e) =>
                {
                    if (!_groupsByName.TryGetValue(_settings.GroupName, out DefinitionGroup group)) return;
                    var names = group.Definitions.Cast<Definition>().Select(d => d.Name).OrderBy(n => n).ToList();
                    var picked = MultiPickerWindow.Show("Select Parameters to Inject", names, _settings.SelectedParams, this);
                    if (picked != null)
                    {
                        _settings.SelectedParams = picked;
                        RefreshChips();
                    }
                };
                BodyPanel.Children.Add(addChipBtn);

                var twoCol = new WpfGrid { Margin = new Thickness(0, 0, 0, 16) };
                twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
                twoCol.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var leftStack = new StackPanel();
                leftStack.Children.Add(AvControls.FieldLabel(3, "Target Revit group"));
                _revitGroupBtn = AvControls.PickerButton(_settings.RevitGroupName);
                _revitGroupBtn.Click += (s, e) =>
                {
                    string picked = SinglePickerWindow.Show(
                        "Select Target Parameter Group",
                        AddInConstants.RevitGroupChoices, _settings.RevitGroupName, this);
                    if (picked != null)
                    {
                        _settings.RevitGroupName = picked;
                        AvControls.SetPickerValue(_revitGroupBtn, picked);
                    }
                };
                leftStack.Children.Add(_revitGroupBtn);
                WpfGrid.SetColumn(leftStack, 0);
                twoCol.Children.Add(leftStack);

                var rightStack = new StackPanel();
                rightStack.Children.Add(AvControls.FieldLabel(4, "Mode / category"));
                _modeBtn = AvControls.PickerButton(_settings.Mode);
                _modeBtn.Click += (s, e) =>
                {
                    string picked = SinglePickerWindow.Show(
                        "Select Mode / Category",
                        AddInConstants.ModeChoices, _settings.Mode, this);
                    if (picked != null)
                    {
                        _settings.Mode = picked;
                        AvControls.SetPickerValue(_modeBtn, picked);
                    }
                };
                rightStack.Children.Add(_modeBtn);
                WpfGrid.SetColumn(rightStack, 2);
                twoCol.Children.Add(rightStack);

                BodyPanel.Children.Add(twoCol);

                BodyPanel.Children.Add(AvControls.FieldLabel(5, "Parameter scope"));
                var (toggleContainer, instanceBtn, typeBtn) =
                    Av.SegmentedToggle("Instance", "Type", _settings.IsInstance);
                _instanceBtn = instanceBtn;
                _typeBtn = typeBtn;
                _instanceBtn.Checked += (s, e) => _settings.IsInstance = true;
                _typeBtn.Checked += (s, e) => _settings.IsInstance = false;
                toggleContainer.Margin = new Thickness(0, 0, 0, 0);
                BodyPanel.Children.Add(toggleContainer);

                var changeFileBtn = AvControls.GhostButton("Change Shared Parameter File");
                changeFileBtn.Click += (_, __) =>
                {
                    string picked = SharedParamFileManager.PromptForFile(this);
                    if (picked == null) return;
                    SharedParamFileManager.SavePath(picked);
                    AvControls.ShowAlert("Shared Parameter File Updated",
                        $"New file saved:\n{picked}\n\n" +
                        "This will be used the next time you run this command.", AlertSeverity.Success);
                };
                var changeFileBorder = new WpfBorder
                {
                    BorderBrush = AvTokens.Brush(AvTokens.BorderStrong),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Child = changeFileBtn
                };

                var cancelBtn = AvControls.GhostButton("Cancel");
                cancelBtn.Click += (_, __) => { DialogResult = false; };
                var runBtn = AvControls.PrimaryButton("▶  Run Script");
                runBtn.Click += (_, __) => { DialogResult = true; };

                var footerRow = new WpfGrid();
                footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                footerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                WpfGrid.SetColumn(changeFileBorder, 0);
                changeFileBorder.HorizontalAlignment = HorizontalAlignment.Left;
                footerRow.Children.Add(changeFileBorder);

                var rightButtons = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                rightButtons.Children.Add(cancelBtn);
                rightButtons.Children.Add(new WpfBorder { Width = 10 });
                rightButtons.Children.Add(runBtn);
                WpfGrid.SetColumn(rightButtons, 1);
                footerRow.Children.Add(rightButtons);

                SetFooter(footerRow);

                RefreshChips();
            }

            private void RefreshChips()
            {
                _chipsPanel.Children.Clear();
                foreach (string p in _settings.SelectedParams)
                    _chipsPanel.Children.Add(Av.Chip(p));
                if (_settings.SelectedParams.Count == 0)
                {
                    _chipsPanel.Children.Add(new TextBlock
                    {
                        Text = "None selected",
                        FontFamily = AvTokens.Ui,
                        FontSize = 12,
                        Foreground = AvTokens.Brush(AvTokens.TextFaint),
                        Margin = new Thickness(0, 0, 0, 8)
                    });
                }
            }

            private static ControlTemplate DashedChipTemplate()
            {
                var t = new ControlTemplate(typeof(Button));
                var bd = new FrameworkElementFactory(typeof(WpfBorder));
                bd.Name = "bd";
                bd.SetValue(WpfBorder.CornerRadiusProperty, new CornerRadius(6));
                bd.SetValue(WpfBorder.BackgroundProperty, new SolidColorBrush(Colors.Transparent));
                bd.SetValue(WpfBorder.PaddingProperty, new Thickness(11, 6, 11, 6));
                bd.SetValue(WpfBorder.BorderBrushProperty, AvTokens.Brush(AvTokens.BorderStrong));
                bd.SetValue(WpfBorder.BorderThicknessProperty, new Thickness(1));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                bd.AppendChild(cp);
                t.VisualTree = bd;
                var hover = new WpfTrigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hover.Setters.Add(new WpfSetter(WpfBorder.BackgroundProperty, AvTokens.Brush(AvTokens.Surface2), "bd"));
                t.Triggers.Add(hover);
                return t;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Cmd_FAM03b_RemoveParams
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.Manual)]
    public class Cmd_FAM03b_RemoveParams : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            string defaultMode = uiDoc.Selection.GetElementIds().Count > 0
                ? AddInConstants.SelectionMode : "Generic Models";

            var settings = new RemoveSettings
            {
                SelectedParams = new List<string>(),
                Mode = defaultMode
            };

            while (true)
            {
                var win = new RemoveSettingsWindow(settings, uiDoc, doc);
                if (win.ShowDialog() != true) return Result.Cancelled;
                if (settings.SelectedParams.Count > 0) break;
                AvControls.ShowAlert("Validation", "You must select at least one parameter before running.", AlertSeverity.Warning);
            }

            var familiesToProcess = FamilyResolver.Resolve(settings.Mode, uiDoc, doc);
            if (familiesToProcess.Count == 0)
            {
                AvControls.ShowAlert("No Families Found",
                    $"No families matched the selected mode / category:\n\"{settings.Mode}\"", AlertSeverity.Warning);
                return Result.Cancelled;
            }

            var familyResults = new List<RemoveParamReportRow>();
            int processedCount = 0, skippedCount = 0, failedCount = 0, totalRemoved = 0;

            var familyIds = familiesToProcess.Select(f => f.Id).ToList();
            var familyNames = familiesToProcess.Select(f => f.Name).ToList();
            int familyCount = familyIds.Count;

            var loadOptions = new DefaultFamilyLoadOptions();
            var progressWindow = new BatchProgressWindow("Purging Parameters…", familyCount);
            progressWindow.Show();

            for (int i = 0; i < familyCount; i++)
            {
                if (progressWindow.IsCancelled) break;

                string familyName = familyNames[i];
                progressWindow.Update(i + 1, familyCount, $"Processing: {familyName}");

                Family family = doc.GetElement(familyIds[i]) as Family;
                if (family == null)
                {
                    failedCount++;
                    familyResults.Add(new RemoveParamReportRow
                    {
                        FamilyName = familyName,
                        Status = "❌",
                        Removed = "—",
                        NotFound = "—",
                        RemovedParameters = "—",
                        Notes = "Family element is no longer accessible."
                    });
                    continue;
                }

                if (!family.IsEditable)
                {
                    skippedCount++;
                    familyResults.Add(new RemoveParamReportRow
                    {
                        FamilyName = familyName,
                        Status = "⏭",
                        Removed = "0",
                        NotFound = "—",
                        RemovedParameters = "—",
                        Notes = "System / Non-Editable"
                    });
                    continue;
                }

                Document familyDocument = null;
                try { familyDocument = doc.EditFamily(family); }
                catch (Exception ex)
                {
                    failedCount++;
                    familyResults.Add(new RemoveParamReportRow
                    {
                        FamilyName = familyName,
                        Status = "❌",
                        Removed = "—",
                        NotFound = "—",
                        RemovedParameters = "—",
                        Notes = $"Cannot open for editing — {ex.Message}"
                    });
                    continue;
                }

                try
                {
                    FamilyManager familyManager = familyDocument.FamilyManager;
                    var parametersToRemove = familyManager.Parameters
                        .Cast<FamilyParameter>()
                        .Where(p => settings.SelectedParams.Contains(p.Definition.Name))
                        .ToList();

                    int notFoundCount = settings.SelectedParams.Count - parametersToRemove.Count;

                    if (parametersToRemove.Count == 0)
                    {
                        skippedCount++;
                        familyResults.Add(new RemoveParamReportRow
                        {
                            FamilyName = familyName,
                            Status = "⏭",
                            Removed = "0",
                            NotFound = notFoundCount.ToString(),
                            RemovedParameters = "—",
                            Notes = "No matching parameters found"
                        });
                        familyDocument.Close(false);
                        continue;
                    }

                    var targetNames = parametersToRemove
                        .Select(p => p.Definition?.Name ?? "(unknown)").ToList();

                    int removedThisFamily = 0;
                    using (var transaction = new Transaction(familyDocument, "Purge Parameters"))
                    {
                        transaction.Start();
                        foreach (FamilyParameter parameter in parametersToRemove)
                        {
                            try { familyManager.RemoveParameter(parameter); removedThisFamily++; }
                            catch { }
                        }
                        transaction.Commit();
                    }

                    familyDocument.LoadFamily(doc, loadOptions);
                    FamilyNameGuard.RestoreIfTruncated(doc, familyIds[i], familyName);

                    totalRemoved += removedThisFamily;
                    processedCount++;
                    familyResults.Add(new RemoveParamReportRow
                    {
                        FamilyName = familyName,
                        Status = removedThisFamily > 0 ? "✅" : "⏭",
                        Removed = removedThisFamily.ToString(),
                        NotFound = notFoundCount.ToString(),
                        RemovedParameters = removedThisFamily > 0 ? string.Join(", ", targetNames) : "—",
                        Notes = string.Empty
                    });
                    familyDocument.Close(false);
                }
                catch (Exception ex)
                {
                    SafeClose(familyDocument);
                    failedCount++;
                    familyResults.Add(new RemoveParamReportRow
                    {
                        FamilyName = familyName,
                        Status = "❌",
                        Removed = "—",
                        NotFound = "—",
                        RemovedParameters = "—",
                        Notes = ex.Message
                    });
                }
            }

            progressWindow.Close();

            var summary = new (string, string)[]
            {
                ("Mode / Category",        settings.Mode),
                ("Families Found",         familiesToProcess.Count.ToString()),
                ("Families Processed  ✅", processedCount.ToString()),
                ("Families Skipped    ⏭", skippedCount.ToString()),
                ("Families Failed     ❌", failedCount.ToString()),
                ("Parameters Removed",     totalRemoved.ToString()),
            };

            var resultColumns = new (string, double, string)[]
            {
                ("Family Name",        220, "FamilyName"),
                ("Status",              95, "Status"),
                ("Removed",             70, "Removed"),
                ("Not Found",           80, "NotFound"),
                ("Removed Parameters", 350, "RemovedParameters"),
                ("Notes",              170, "Notes"),
            };

            new UnifiedReportWindow(
                "Batch Parameter Removal",
                summary, resultColumns,
                familyResults.Cast<object>(),
                warnings: null,
                footerText: doc.Title).ShowDialog();

            return Result.Succeeded;
        }

        private static void SafeClose(Document fd) { try { fd?.Close(false); } catch { } }

        private class RemoveSettings
        {
            public List<string> SelectedParams { get; set; }
            public string Mode { get; set; }
        }

        private class RemoveSettingsWindow : AvesBaseWindow
        {
            private readonly RemoveSettings _settings;
            private readonly UIDocument _uiDoc;
            private readonly Document _doc;
            private readonly WrapPanel _chipsPanel;
            private readonly Button _modeBtn;

            public RemoveSettingsWindow(RemoveSettings settings, UIDocument uiDoc, Document doc)
                : base("Universal Parameter Remover")
            {
                _settings = settings;
                _uiDoc = uiDoc;
                _doc = doc;

                Width = 480; Height = 460;
                WindowStartupLocation = WindowStartupLocation.CenterScreen;
                ResizeMode = ResizeMode.NoResize;

                BodyPanel.Children.Add(AvControls.FieldLabel(1, "Parameters to remove"));
                _chipsPanel = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
                BodyPanel.Children.Add(_chipsPanel);

                var addChipBtn = new Button
                {
                    Cursor = Cursors.Hand,
                    FontFamily = AvTokens.Ui,
                    FontSize = 12,
                    Foreground = AvTokens.Brush(AvTokens.TextDim),
                    Margin = new Thickness(0, 0, 0, 16),
                    Template = DashedChipTemplate()
                };
                addChipBtn.Content = "+ Select parameters";
                addChipBtn.Click += (s, e) => ScanAndPickParameters();
                BodyPanel.Children.Add(addChipBtn);

                BodyPanel.Children.Add(AvControls.FieldLabel(2, "Mode / category"));
                _modeBtn = AvControls.PickerButton(_settings.Mode);
                _modeBtn.Margin = new Thickness(0, 0, 0, 16);
                _modeBtn.Click += (s, e) =>
                {
                    string picked = SinglePickerWindow.Show(
                        "Select Mode / Category",
                        AddInConstants.ModeChoices, _settings.Mode, this);
                    if (picked != null && picked != _settings.Mode)
                    {
                        _settings.Mode = picked;
                        _settings.SelectedParams.Clear();
                        AvControls.SetPickerValue(_modeBtn, picked);
                        RefreshChips();
                    }
                };
                BodyPanel.Children.Add(_modeBtn);

                var cancelBtn = AvControls.GhostButton("Cancel");
                cancelBtn.Click += (_, __) => { DialogResult = false; };
                var runBtn = AvControls.PrimaryButton("▶  Run Script");
                runBtn.Click += (_, __) => { DialogResult = true; };

                var footerRow = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                footerRow.Children.Add(cancelBtn);
                footerRow.Children.Add(new WpfBorder { Width = 10 });
                footerRow.Children.Add(runBtn);
                SetFooter(footerRow);

                RefreshChips();
            }

            private void RefreshChips()
            {
                _chipsPanel.Children.Clear();
                foreach (string p in _settings.SelectedParams)
                    _chipsPanel.Children.Add(Av.Chip(p));
                if (_settings.SelectedParams.Count == 0)
                {
                    _chipsPanel.Children.Add(new TextBlock
                    {
                        Text = "None selected",
                        FontFamily = AvTokens.Ui,
                        FontSize = 12,
                        Foreground = AvTokens.Brush(AvTokens.TextFaint),
                        Margin = new Thickness(0, 0, 0, 8)
                    });
                }
            }

            private void ScanAndPickParameters()
            {
                var families = FamilyResolver.Resolve(_settings.Mode, _uiDoc, _doc);
                if (families.Count == 0)
                {
                    AvControls.ShowAlert("No Families",
                        "No families found to scan for the current mode / category.", AlertSeverity.Warning);
                    return;
                }

                int scanCap = Math.Min(families.Count, AddInConstants.FamilyScanCap);
                var foundNames = new HashSet<string>();
                var scanWindow = new BatchProgressWindow("Scanning families for parameters…", scanCap);
                scanWindow.Show();

                for (int i = 0; i < scanCap; i++)
                {
                    if (scanWindow.IsCancelled) break;
                    Family family = families[i];
                    if (!family.IsEditable) continue;
                    scanWindow.Update(i + 1, scanCap, $"Scanning: {family.Name}");

                    Document familyDocument = null;
                    try
                    {
                        familyDocument = _doc.EditFamily(family);
                        foreach (FamilyParameter p in familyDocument.FamilyManager.Parameters)
                        {
                            bool isUserDefined = p.IsShared ||
                                (p.Definition is InternalDefinition internalDef &&
                                 internalDef.BuiltInParameter == BuiltInParameter.INVALID);
                            if (isUserDefined) foundNames.Add(p.Definition.Name);
                        }
                        familyDocument.Close(false);
                    }
                    catch { try { familyDocument?.Close(false); } catch { } }
                }

                scanWindow.Close();

                if (foundNames.Count == 0)
                {
                    AvControls.ShowAlert("No Parameters",
                        "No user-defined parameters were found in the scanned families.", AlertSeverity.Warning);
                    return;
                }

                var picked = MultiPickerWindow.Show(
                    "Select Parameters to Purge",
                    foundNames.OrderBy(n => n).ToList(),
                    _settings.SelectedParams, this);

                if (picked != null)
                {
                    _settings.SelectedParams = picked;
                    RefreshChips();
                }
            }

            private static ControlTemplate DashedChipTemplate()
            {
                var t = new ControlTemplate(typeof(Button));
                var bd = new FrameworkElementFactory(typeof(WpfBorder));
                bd.Name = "bd";
                bd.SetValue(WpfBorder.CornerRadiusProperty, new CornerRadius(6));
                bd.SetValue(WpfBorder.BackgroundProperty, new SolidColorBrush(Colors.Transparent));
                bd.SetValue(WpfBorder.PaddingProperty, new Thickness(11, 6, 11, 6));
                bd.SetValue(WpfBorder.BorderBrushProperty, AvTokens.Brush(AvTokens.BorderStrong));
                bd.SetValue(WpfBorder.BorderThicknessProperty, new Thickness(1));
                var cp = new FrameworkElementFactory(typeof(ContentPresenter));
                cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
                cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
                bd.AppendChild(cp);
                t.VisualTree = bd;
                var hover = new WpfTrigger { Property = UIElement.IsMouseOverProperty, Value = true };
                hover.Setters.Add(new WpfSetter(WpfBorder.BackgroundProperty, AvTokens.Brush(AvTokens.Surface2), "bd"));
                t.Triggers.Add(hover);
                return t;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Cmd_FAM03c_CsvImporter
    // ══════════════════════════════════════════════════════════════════════

    [Transaction(TransactionMode.Manual)]
    public class Cmd_FAM03c_CsvImporter : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var uiDoc = commandData.Application.ActiveUIDocument;
            var doc = uiDoc.Document;

            var openDialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Data CSV File",
                Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*",
                DefaultExt = ".csv"
            };
            if (openDialog.ShowDialog() != true) return Result.Cancelled;
            string csvPath = openDialog.FileName;

            Dictionary<string, Dictionary<string, string>> dataMap;
            try { dataMap = LoadDataMap(csvPath); }
            catch (Exception ex)
            {
                string hint = ex.Message.ToLower().Contains("encoding") ||
                              ex is System.Text.DecoderFallbackException
                    ? "\n\nTip: Re-export from Excel as 'CSV UTF-8 (Comma delimited)'." : string.Empty;
                AvControls.ShowAlert("CSV Error", $"Error reading CSV:\n{ex.Message}{hint}", AlertSeverity.Error);
                return Result.Cancelled;
            }

            if (dataMap.Count == 0)
            {
                AvControls.ShowAlert("CSV Error",
                    "The CSV file is empty or does not contain an 'Item Number' column.", AlertSeverity.Error);
                return Result.Cancelled;
            }

            var selectionIds = uiDoc.Selection.GetElementIds();
            if (selectionIds.Count == 0)
            {
                AvControls.ShowAlert("No Selection",
                    "Select one or more elements in the model before running this command.", AlertSeverity.Warning);
                return Result.Cancelled;
            }

            var materialCache = BuildMaterialCache(doc);
            Material defaultMaterial;
            try { defaultMaterial = GetDefaultMaterial(materialCache); }
            catch (Exception ex) { AvControls.ShowAlert("Error", ex.Message, AlertSeverity.Error); return Result.Cancelled; }

            var parameterLog = new List<CsvParamLogEntry>();
            var foundItemNumbers = new HashSet<string>();
            var missingItemNumbers = new HashSet<string>();
            var processedTypeIds = new HashSet<ElementId>();

            try
            {
                using (var transaction = new Transaction(doc, "Import CSV Data"))
                {
                    transaction.Start();
                    foreach (ElementId elementId in selectionIds)
                    {
                        Element element = doc.GetElement(elementId);
                        if (element == null) continue;

                        ElementId typeId; Element elementType;
                        try
                        {
                            typeId = element.GetTypeId();
                            if (typeId == ElementId.InvalidElementId) continue;
                            elementType = doc.GetElement(typeId);
                            if (elementType == null) continue;
                        }
                        catch { continue; }

                        string itemNumber = GetItemNumber(element, elementType);
                        if (string.IsNullOrEmpty(itemNumber)) continue;

                        if (!dataMap.TryGetValue(itemNumber, out var rowData))
                        { missingItemNumbers.Add(itemNumber); continue; }

                        foundItemNumbers.Add(itemNumber);
                        bool alreadyProcessedType = processedTypeIds.Contains(typeId);
                        processedTypeIds.Add(typeId);

                        foreach (var kvp in rowData)
                        {
                            string paramName = kvp.Key, paramValue = kvp.Value;
                            if (string.IsNullOrEmpty(paramName)) continue;

                            Parameter typeParam = elementType?.LookupParameter(paramName);
                            Parameter instanceParam = element.LookupParameter(paramName);
                            bool isTypeLevel = typeParam != null &&
                                (instanceParam == null || typeParam.Id != instanceParam.Id);

                            if (isTypeLevel && alreadyProcessedType) continue;

                            var (success, scope, status, detail) = ResolveAndSetParameter(
                                element, elementType, paramName, paramValue,
                                doc, materialCache, defaultMaterial);

                            parameterLog.Add(new CsvParamLogEntry
                            {
                                ItemNumber = itemNumber,
                                ParamName = paramName,
                                Value = paramValue,
                                Scope = scope,
                                Status = status,
                                Detail = detail
                            });
                        }
                    }
                    transaction.Commit();
                }
            }
            catch (Exception ex)
            {
                AvControls.ShowAlert("Transaction Error", $"An error occurred during processing:\n{ex.Message}", AlertSeverity.Error);
                return Result.Cancelled;
            }

            var aggregated = AggregateResults(parameterLog);
            int totalSet = aggregated.Values.Sum(v => v.SetParams.Count);
            int totalFailed = aggregated.Values.Sum(v => v.FailedParams.Count);
            int totalSkipped = aggregated.Values.Sum(v => v.SkippedParams.Count);

            var resultRows = aggregated.Values
                .OrderBy(v => v.ItemNumber)
                .Select(v =>
                {
                    string status = v.FailedParams.Count == 0 && v.SetParams.Count > 0 ? "✅"
                                  : v.FailedParams.Count > 0 && v.SetParams.Count > 0 ? "⚠️"
                                  : v.FailedParams.Count > 0 && v.SetParams.Count == 0 ? "❌" : "⏭";
                    return (object)new CsvItemReportRow
                    {
                        ItemNumber = v.ItemNumber,
                        Status = status,
                        SetCount = v.SetParams.Count.ToString(),
                        FailedCount = v.FailedParams.Count.ToString(),
                        SkippedCount = v.SkippedParams.Count.ToString(),
                        SetParameters = string.Join(", ", v.SetParams.OrderBy(s => s)),
                        FailedParameters = string.Join(", ", v.FailedParams.OrderBy(s => s)),
                        SkippedParameters = string.Join(", ", v.SkippedParams.OrderBy(s => s))
                    };
                }).ToList();

            var summary = new (string, string)[]
            {
                ("CSV Source",            System.IO.Path.GetFileName(csvPath)),
                ("Elements Selected",     selectionIds.Count.ToString()),
                ("Types Processed",       processedTypeIds.Count.ToString()),
                ("Matched in CSV",        foundItemNumbers.Count.ToString()),
                ("Missing from CSV",      missingItemNumbers.Count.ToString()),
                ("Parameters SET    ✅",  totalSet.ToString()),
                ("Parameters FAILED ❌",  totalFailed.ToString()),
                ("Parameters SKIPPED ⏭", totalSkipped.ToString()),
            };

            var resultColumns = new (string, double, string)[]
            {
                ("Item Number",         110, "ItemNumber"),
                ("Status",               95, "Status"),
                ("SET",                  45, "SetCount"),
                ("FAILED",               55, "FailedCount"),
                ("SKIPPED",              65, "SkippedCount"),
                ("Set Parameters",      160, "SetParameters"),
                ("Failed Parameters",   160, "FailedParameters"),
                ("Skipped Parameters",  160, "SkippedParameters"),
            };

            var warnings = missingItemNumbers.Count > 0
                ? missingItemNumbers.OrderBy(s => s)
                    .Select(s => (s, "Item Number not found in CSV")).ToList()
                : null;

            new UnifiedReportWindow(
                "CSV Import",
                summary, resultColumns, resultRows,
                warnings, csvPath).ShowDialog();

            return Result.Succeeded;
        }

        private static Dictionary<string, CsvAggregatedEntry> AggregateResults(
            List<CsvParamLogEntry> log)
        {
            var aggregated = new Dictionary<string, CsvAggregatedEntry>();
            foreach (var entry in log)
            {
                if (!aggregated.TryGetValue(entry.ItemNumber, out var agg))
                {
                    agg = new CsvAggregatedEntry { ItemNumber = entry.ItemNumber };
                    aggregated[entry.ItemNumber] = agg;
                }
                string label = string.IsNullOrEmpty(entry.Detail)
                    ? entry.ParamName : $"{entry.ParamName} ({entry.Detail})";

                if (entry.Status == "SET") agg.SetParams.Add(entry.ParamName);
                else if (entry.Status == "FAILED") agg.FailedParams.Add(label);
                else if (entry.Status == "SKIPPED") agg.SkippedParams.Add(label);
            }
            return aggregated;
        }

        private static Dictionary<string, Material> BuildMaterialCache(Document doc) =>
            new FilteredElementCollector(doc).OfClass(typeof(Material)).Cast<Material>()
                .GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

        private static Material GetDefaultMaterial(Dictionary<string, Material> cache)
        {
            if (cache.TryGetValue("Default", out Material m)) return m;
            Material first = cache.Values.FirstOrDefault();
            if (first != null) return first;
            throw new InvalidOperationException("No materials found in the project.");
        }

        private static (Material material, string status) GetOrCreateMaterial(
            Document doc, string name, Dictionary<string, Material> cache, Material defaultMaterial)
        {
            if (string.IsNullOrWhiteSpace(name)) return (null, "NO_VALUE");
            name = name.Trim();
            if (cache.TryGetValue(name, out Material existing)) return (existing, "FOUND");
            if (defaultMaterial.Duplicate(name) is not Material newMat) return (null, "FAILED");
            cache[name] = newMat;
            return (newMat, "CREATED");
        }

        private static bool IsMaterialParameter(Parameter p)
        {
            if (p.StorageType != StorageType.ElementId) return false;
            try { return p.Definition.GetDataType().Equals(SpecTypeId.Reference.Material); }
            catch { return p.Definition.Name.IndexOf("material", StringComparison.OrdinalIgnoreCase) >= 0; }
        }

        private static string GetItemNumber(Element element, Element elementType)
        {
            foreach (Element src in new[] { element, elementType })
            {
                if (src == null) continue;
                Parameter p = src.LookupParameter("Item Number");
                if (p?.StorageType == StorageType.String)
                {
                    string v = p.AsString();
                    if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                }
            }
            return null;
        }

        private static (bool success, string status, string detail) TrySetParameter(
            Parameter p, string value, Document doc,
            Dictionary<string, Material> materialCache, Material defaultMaterial)
        {
            if (p.IsReadOnly) return (false, "Read-Only", string.Empty);
            try
            {
                if (IsMaterialParameter(p))
                {
                    var (mat, matStatus) = GetOrCreateMaterial(doc, value, materialCache, defaultMaterial);
                    if (mat != null) { p.Set(mat.Id); return (true, "SET", $"Material {matStatus}"); }
                    return (false, "FAILED", "No Material Value");
                }
                switch (p.StorageType)
                {
                    case StorageType.String: p.Set(value); break;
                    case StorageType.Double:
                        p.Set(double.Parse(value, System.Globalization.CultureInfo.InvariantCulture)); break;
                    case StorageType.Integer: p.Set(int.Parse(value)); break;
                    case StorageType.ElementId: return (false, "SKIPPED", "ElementId — not a Material parameter");
                    default: return (false, "SKIPPED", "Unsupported StorageType");
                }
                return (true, "SET", string.Empty);
            }
            catch (Exception ex)
            {
                return (false, "FAILED", $"Format Error: {ex.Message.Replace("\t", " ").Replace("\n", " ")}");
            }
        }

        private static (bool success, string scope, string status, string detail)
            ResolveAndSetParameter(
                Element element, Element elementType, string name, string value,
                Document doc, Dictionary<string, Material> materialCache, Material defaultMaterial)
        {
            foreach (var (source, scope) in new[] { (elementType, "Type"), (element, "Instance") })
            {
                if (source == null) continue;
                Parameter p = source.LookupParameter(name);
                if (p == null || p.IsReadOnly) continue;
                var (success, status, detail) =
                    TrySetParameter(p, value, doc, materialCache, defaultMaterial);
                return (success, scope, status, detail);
            }
            Parameter fallback = element.LookupParameter(name);
            return fallback == null
                ? (false, string.Empty, "FAILED", "Parameter not found on Type or Instance")
                : (false, string.Empty, "FAILED", "Read-Only on both Type and Instance");
        }

        private static Dictionary<string, Dictionary<string, string>> LoadDataMap(string path)
        {
            var data = new Dictionary<string, Dictionary<string, string>>();
            using (var reader = new System.IO.StreamReader(path, System.Text.Encoding.UTF8, true))
            {
                string headerLine = reader.ReadLine();
                if (headerLine == null) return data;
                if (headerLine.Length > 0 && headerLine[0] == '\uFEFF')
                    headerLine = headerLine.Substring(1);

                List<string> headers = ParseCsvLine(headerLine);
                int itemNumberIdx = headers.IndexOf("Item Number");
                if (itemNumberIdx < 0) return data;

                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    List<string> values = ParseCsvLine(line);
                    if (itemNumberIdx >= values.Count) continue;
                    string key = values[itemNumberIdx].Trim();
                    if (string.IsNullOrEmpty(key)) continue;
                    var row = new Dictionary<string, string>();
                    for (int i = 0; i < headers.Count; i++)
                    {
                        if (i == itemNumberIdx || i >= values.Count) continue;
                        if (!string.IsNullOrEmpty(headers[i])) row[headers[i]] = values[i];
                    }
                    data[key] = row;
                }
            }
            return data;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            var buffer = new StringBuilder();
            bool inQuotes = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"') { buffer.Append('"'); i++; }
                        else inQuotes = false;
                    }
                    else buffer.Append(c);
                }
                else
                {
                    if (c == '"') inQuotes = true;
                    else if (c == ',') { fields.Add(buffer.ToString()); buffer.Clear(); }
                    else buffer.Append(c);
                }
            }
            fields.Add(buffer.ToString());
            return fields;
        }

        private class CsvParamLogEntry
        {
            public string ItemNumber { get; set; }
            public string ParamName { get; set; }
            public string Value { get; set; }
            public string Scope { get; set; }
            public string Status { get; set; }
            public string Detail { get; set; }
        }

        private class CsvAggregatedEntry
        {
            public string ItemNumber { get; set; }
            public List<string> SetParams = new List<string>();
            public List<string> FailedParams = new List<string>();
            public List<string> SkippedParams = new List<string>();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  UiHelpers
    // ══════════════════════════════════════════════════════════════════════

    internal static class UiHelpers
    {
        internal static TextBlock MakeHint(string text, int row)
        {
            var hint = new TextBlock
            {
                Text = text,
                Margin = new Thickness(10, 8, 10, 2),
                FontStyle = FontStyles.Italic,
                Foreground = AvTokens.Brush(AvTokens.TextFaint),
                FontSize = 11
            };
            WpfGrid.SetRow(hint, row);
            return hint;
        }

    }

    // ══════════════════════════════════════════════════════════════════════
    //  FamilyNameGuard
    // ══════════════════════════════════════════════════════════════════════

    internal static class FamilyNameGuard
    {
        internal static void RestoreIfTruncated(Document doc, ElementId familyId, string originalName)
        {
            if (!originalName.Contains('/')) return;
            if (!(doc.GetElement(familyId) is Family reloadedFamily)) return;
            if (reloadedFamily.Name == originalName) return;

            using (var tx = new Transaction(doc, $"Restore family name: {originalName}"))
            {
                try
                {
                    tx.Start();
                    reloadedFamily.Name = originalName;
                    tx.Commit();
                }
                catch (Exception ex)
                {
                    tx.RollBack();
                    AvControls.ShowAlert("Family Name Warning",
                        $"Parameter changes were applied successfully, but the family name\n" +
                        $"could not be restored to \"{originalName}\" after reload.\n\n" +
                        $"Current name: \"{reloadedFamily.Name}\"\n\n" +
                        $"Reason: {ex.Message}\n\n" +
                        $"Please rename the family manually in the Project Browser.", AlertSeverity.Warning);
                }
            }
        }
    }
}