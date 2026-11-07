// -----------------------------------------------------------------------
// Title: Export GLB + BOM
// Summary: Exports the open family as a .glb with one named node per
//          subpart, plus a .bom.json for the web app's catalog.
// Key logic: SPIKE: exports the current type to a fixed Desktop folder with
//            a verbose log, and has no options dialog yet. The export itself
//            lives in FamilyGlbExporter; this class only checks the document
//            and reports the result.
// -----------------------------------------------------------------------

using System.IO;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using AVES.Core;
using AVES.Core.GlbExport;

namespace AVES.Commands
{
    [Transaction(TransactionMode.Manual)]
    public class Cmd_FAM04_ExportGlb : IExternalCommand
    {
        private const string ToolName = "Export GLB + BOM";
        private const string SpikeFolderName = "AVES_GLB_Spike";

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

            var logFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AVES", "ExportGlb");

            GlbExportLog? log = null;
            try
            {
                log = new GlbExportLog(logFolder, ExportFileNames.BaseName(FamilyGlbExporter.FamilyName(doc), ""));

                var options = new GlbExportOptions
                {
                    OutputFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), SpikeFolderName),
                };

                var result = new FamilyGlbExporter(doc, options, log).ExportCurrentType();

                var pieces = result.Bom.Subparts.Sum(s => s.Qty);
                AvControls.ShowAlert(ToolName,
                    $"{result.Bom.Subparts.Count} subparts, {pieces} pieces, {result.Bom.Stats.Nodes} nodes, " +
                    $"{result.Bom.Stats.Meshes} meshes.\n" +
                    $"Size: {result.Bom.WidthMm} x {result.Bom.HeightMm} x {result.Bom.ThicknessMm} mm.\n" +
                    $"Warnings: {result.WarningCount}.\n\n" +
                    $"Files: {options.OutputFolder}\n\nLog: {log.Path}",
                    result.WarningCount > 0 ? AlertSeverity.Warning : AlertSeverity.Success);

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                log?.Line($"FAILED: {ex}");
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
    }
}
