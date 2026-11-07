// -----------------------------------------------------------------------
// Title: Export GLB + BOM
// Summary: Exports the open family as a .glb with one named node per
//          subpart, plus a .bom.json for the web app's catalog, for the
//          current type or for every type.
// Key logic: The export itself lives in FamilyGlbExporter. This file only
//            checks the document, asks for the options, remembers them
//            between runs, shows progress and reports the result.
// -----------------------------------------------------------------------

using System.IO;
using System.Text.Json;
using System.Windows;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using AVES.Core;
using AVES.Core.GlbExport;
using Microsoft.Win32;
using WpfButton = System.Windows.Controls.Button;
using WpfCheckBox = System.Windows.Controls.CheckBox;
using WpfDock = System.Windows.Controls.Dock;
using WpfDockPanel = System.Windows.Controls.DockPanel;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfRadioButton = System.Windows.Controls.RadioButton;
using WpfStackPanel = System.Windows.Controls.StackPanel;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfThickness = System.Windows.Thickness;

namespace AVES.Commands
{
    // ─────────────────────────────────────────────────────────────────────────
    //  SETTINGS
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The remembered options, in %AppData%\AVES\ExportGlb\settings.json. The
    /// threshold and the frame list have no dialog control; edit them there.
    /// </summary>
    internal sealed class ExportGlbSettings
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static string Folder { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVES", "ExportGlb");

        private static string FilePath => Path.Combine(Folder, "settings.json");

        public string OutputFolder { get; set; } = "";
        public bool AllTypes { get; set; }
        public bool FlagPerforations { get; set; } = true;
        public bool Compress { get; set; }
        public int TriangleWarningThreshold { get; set; } = new GlbExportOptions().TriangleWarningThreshold;
        public List<string> FrameExcludedNames { get; set; } = new GlbExportOptions().FrameExcludedNames;

        public static ExportGlbSettings Load()
        {
            try
            {
                return File.Exists(FilePath)
                    ? JsonSerializer.Deserialize<ExportGlbSettings>(File.ReadAllText(FilePath)) ?? new ExportGlbSettings()
                    : new ExportGlbSettings();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return new ExportGlbSettings();
            }
        }

        public void Save()
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }

        public GlbExportOptions ToOptions() => new GlbExportOptions
        {
            OutputFolder = OutputFolder,
            AllTypes = AllTypes,
            FlagPerforations = FlagPerforations,
            Compress = Compress,
            TriangleWarningThreshold = TriangleWarningThreshold,
            FrameExcludedNames = FrameExcludedNames,
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  OPTIONS DIALOG
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>Asks for the output folder and export options.</summary>
    internal sealed class ExportGlbOptionsWindow : Window
    {
        private const string FolderPlaceholder = "Choose a folder…";

        private readonly ExportGlbSettings _settings;
        private readonly WpfButton _folderButton;
        private readonly WpfRadioButton _allTypes;
        private readonly WpfCheckBox _flagPerforations;
        private readonly WpfCheckBox _compress;
        private string _folder;

        /// <summary>True when the user chose to export.</summary>
        public bool Confirmed { get; private set; }

        public ExportGlbOptionsWindow(ExportGlbSettings settings, string familyName, string currentType, int typeCount)
        {
            _settings = settings;
            _folder = settings.OutputFolder;

            Title = "Export GLB + BOM";
            Width = 480;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStyle = WindowStyle.ToolWindow;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = AvTokens.B(AvTokens.Surface);
            FontFamily = AvTokens.Ui;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            var shell = new WpfDockPanel { LastChildFill = true };
            Content = shell;

            var titleBar = AvControls.TitleBar("Export GLB + BOM", familyName);
            WpfDockPanel.SetDock(titleBar, WpfDock.Top);
            shell.Children.Add(titleBar);

            var footerInner = new WpfDockPanel { LastChildFill = false };
            var export = AvControls.PrimaryBtn("Export", OnExport);
            var cancel = AvControls.GhostBtn("Cancel", Close);
            WpfDockPanel.SetDock(export, WpfDock.Right);
            WpfDockPanel.SetDock(cancel, WpfDock.Right);
            footerInner.Children.Add(export);
            footerInner.Children.Add(cancel);
            var footer = AvControls.FooterStrip(footerInner);
            WpfDockPanel.SetDock(footer, WpfDock.Bottom);
            shell.Children.Add(footer);

            var body = new WpfStackPanel { Margin = new WpfThickness(20, 18, 20, 18) };
            shell.Children.Add(body);

            body.Children.Add(AvControls.FieldLabel(1, "Output folder"));
            _folderButton = AvControls.PickerButton(FolderPlaceholder);
            AvControls.SetPickerValue(_folderButton, _folder, FolderPlaceholder);
            _folderButton.Margin = new WpfThickness(0, 6, 0, 18);
            _folderButton.Click += (_, _) => PickFolder();
            body.Children.Add(_folderButton);

            body.Children.Add(AvControls.FieldLabel(2, "Types to export"));
            var typeRow = new WpfStackPanel
            {
                Orientation = WpfOrientation.Vertical,
                Margin = new WpfThickness(0, 6, 0, 18),
            };
            var currentLabel = string.IsNullOrWhiteSpace(currentType) ? "Current type" : $"Current type ({currentType})";
            var current = Radio(currentLabel);
            _allTypes = Radio($"All types ({typeCount})");
            current.IsChecked = !settings.AllTypes;
            _allTypes.IsChecked = settings.AllTypes;
            typeRow.Children.Add(current);
            typeRow.Children.Add(_allTypes);
            body.Children.Add(typeRow);

            body.Children.Add(AvControls.FieldLabel(3, "Options"));

            _flagPerforations = Check("Flag perforated plates (warning only)");
            _flagPerforations.IsChecked = settings.FlagPerforations;
            _flagPerforations.Margin = new WpfThickness(0, 6, 0, 0);
            _flagPerforations.ToolTip =
                $"Warns about meshes above {settings.TriangleWarningThreshold} triangles. " +
                "The holes are still exported: replacing them with a plain plate is not implemented yet.";
            body.Children.Add(_flagPerforations);

            var canCompress = GlbCompressor.IsAvailable();
            _compress = Check(canCompress ? "Compress after export (Meshopt)" : "Compress after export (npx not found)");
            _compress.IsEnabled = canCompress;
            _compress.IsChecked = canCompress && settings.Compress;
            _compress.Margin = new WpfThickness(0, 8, 0, 0);
            _compress.ToolTip = "The web app needs a Meshopt decoder to load compressed files.";
            body.Children.Add(_compress);
        }

        private static WpfRadioButton Radio(string label) => new WpfRadioButton
        {
            Content = new WpfTextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis },
            FontFamily = AvTokens.Ui,
            FontSize = 12,
            Foreground = AvTokens.B(AvTokens.TextDim),
            Margin = new WpfThickness(0, 0, 0, 6),
        };

        private static WpfCheckBox Check(string label) => new WpfCheckBox
        {
            Content = label,
            FontFamily = AvTokens.Ui,
            FontSize = 12,
            Foreground = AvTokens.B(AvTokens.TextDim),
        };

        private void PickFolder()
        {
            var dialog = new OpenFolderDialog { Title = "Choose the output folder" };
            if (Directory.Exists(_folder)) dialog.InitialDirectory = _folder;
            if (dialog.ShowDialog(this) != true) return;

            _folder = dialog.FolderName;
            AvControls.SetPickerValue(_folderButton, _folder, FolderPlaceholder);
        }

        private void OnExport()
        {
            if (string.IsNullOrWhiteSpace(_folder))
            {
                AvControls.ShowAlert("Export GLB + BOM", "Choose an output folder first.", AlertSeverity.Warning);
                return;
            }

            _settings.OutputFolder = _folder;
            _settings.AllTypes = _allTypes.IsChecked == true;
            _settings.FlagPerforations = _flagPerforations.IsChecked == true;

            // A disabled box says nothing about what the user wants next time.
            if (_compress.IsEnabled) _settings.Compress = _compress.IsChecked == true;

            Confirmed = true;
            Close();
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  COMMAND
    // ─────────────────────────────────────────────────────────────────────────

    [Transaction(TransactionMode.Manual)]
    public class Cmd_FAM04_ExportGlb : IExternalCommand
    {
        private const string ToolName = "Export GLB + BOM";

        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var doc = commandData.Application.ActiveUIDocument?.Document;

            if (doc is not { IsFamilyDocument: true })
            {
                AvControls.ShowAlert(ToolName,
                    "Open a family in the Family Editor first. This tool exports the active family, not a project.",
                    AlertSeverity.Warning);
                return Result.Cancelled;
            }

            GlbExportLog? log = null;
            BatchProgressWindow? progress = null;
            try
            {
                var familyName = FamilyGlbExporter.FamilyName(doc);
                var settings = ExportGlbSettings.Load();

                var window = new ExportGlbOptionsWindow(
                    settings,
                    familyName,
                    doc.FamilyManager.CurrentType?.Name ?? "",
                    doc.FamilyManager.Types.Size);
                window.ShowDialog();
                if (!window.Confirmed) return Result.Cancelled;

                settings.Save();

                log = new GlbExportLog(ExportGlbSettings.Folder, ExportFileNames.BaseName(familyName, ""));
                progress = new BatchProgressWindow(ToolName, 1, "part", "parts");
                progress.Show();

                var bar = progress;
                var results = new FamilyGlbExporter(
                    doc,
                    settings.ToOptions(),
                    log,
                    (current, total, status) => bar.Update(current, total, status)).Export();

                progress.Close();
                progress = null;

                Report(results, settings.OutputFolder, log);
                return results.Any(r => r.Error == null) ? Result.Succeeded : Result.Failed;
            }
            catch (Exception ex)
            {
                log?.Line($"FAILED: {ex}");
                progress?.Close();
                AvControls.ShowAlert($"{ToolName} – Error",
                    log == null ? ex.Message : $"{ex.Message}\n\nLog: {log.Path}",
                    AlertSeverity.Error);
                return Result.Failed;
            }
            finally
            {
                log?.Dispose();
            }
        }

        private static void Report(List<GlbExportResult> results, string folder, GlbExportLog log)
        {
            var exported = results.Where(r => r.Error == null).ToList();
            var failed = results.Where(r => r.Error != null).ToList();

            var lines = new List<string>();
            if (results.Count == 1 && exported.Count == 1)
            {
                var bom = exported[0].Bom;
                lines.Add($"{bom.Subparts.Count} subparts, {bom.Subparts.Sum(s => s.Qty)} pieces, " +
                          $"{bom.Stats.Nodes} nodes, {bom.Stats.Meshes} meshes.");
                lines.Add($"Size: {bom.WidthMm} x {bom.HeightMm} x {bom.ThicknessMm} mm.");
            }
            else
            {
                lines.Add($"{exported.Count} of {results.Count} types exported.");
            }

            foreach (var failure in failed)
                lines.Add($"Failed: \"{failure.TypeName}\": {failure.Error}");

            if (exported.Any(r => r.Compressed))
                lines.Add($"Compressed: {exported.Count(r => r.Compressed)} of {exported.Count}.");

            lines.Add($"Warnings: {log.WarningCount}.");
            lines.Add("");
            lines.Add($"Files: {folder}");
            lines.Add("");
            lines.Add($"Log: {log.Path}");

            var severity = exported.Count == 0 ? AlertSeverity.Error
                : failed.Count > 0 || log.WarningCount > 0 ? AlertSeverity.Warning
                : AlertSeverity.Success;

            AvControls.ShowAlert(ToolName, string.Join("\n", lines), severity);
        }
    }
}
