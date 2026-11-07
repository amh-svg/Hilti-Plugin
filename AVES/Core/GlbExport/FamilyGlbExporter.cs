// -----------------------------------------------------------------------
// Title: Family GLB Exporter
// Summary: Exports one or all types of an open family document as a .glb,
//          with each nested subpart as a named node, and a .bom.json with
//          the subpart quantities.
// Key logic: Only the direct nested instances count, so a subpart with its
//            own nested parts stays one BOM line but keeps its full geometry.
//            Subparts with identical geometry share a mesh, keyed by type and
//            a geometry hash, so a cut or parameter-driven instance gets its
//            own. Geometry the family owns directly becomes one "(body)" node
//            outside the BOM. The panel's size and origin come from the
//            exported vertices, leaving out hardware that sticks out of the
//            panel. Nothing here writes to the document.
// -----------------------------------------------------------------------

using System.Globalization;
using System.IO;
using Autodesk.Revit.DB;
using RevitMaterial = Autodesk.Revit.DB.Material;
using RevitMesh = Autodesk.Revit.DB.Mesh;
using Vector3 = System.Numerics.Vector3;

namespace AVES.Core.GlbExport
{
    /// <summary>Settings for one export run.</summary>
    internal sealed class GlbExportOptions
    {
        public string OutputFolder { get; set; } = "";

        /// <summary>Exports every type of the family, not just the current one.</summary>
        public bool AllTypes { get; set; }

        /// <summary>Compresses each .glb with Meshopt when npx is available.</summary>
        public bool Compress { get; set; }

        /// <summary>
        /// Warns about meshes above <see cref="TriangleWarningThreshold"/>.
        /// TODO: replace such plates with their outer boundary, without the holes.
        /// </summary>
        public bool FlagPerforations { get; set; } = true;

        /// <summary>Meshes above this count are treated as likely perforated.</summary>
        public int TriangleWarningThreshold { get; set; } = 20000;

        /// <summary>
        /// Subparts whose name contains one of these, ignoring case, are left
        /// out of the panel's size and origin. They are still exported and
        /// counted in the BOM.
        /// </summary>
        public List<string> FrameExcludedNames { get; set; } = new() { " - Cam", "button fix set" };
    }

    /// <summary>What exporting one type produced.</summary>
    internal sealed class GlbExportResult
    {
        public string TypeName { get; set; } = "";
        public string GlbPath { get; set; } = "";
        public string BomPath { get; set; } = "";
        public BomFile Bom { get; set; } = new();
        public int WarningCount { get; set; }
        public bool Compressed { get; set; }

        /// <summary>Why this type failed, or null if it was exported.</summary>
        public string? Error { get; set; }
    }

    internal sealed class FamilyGlbExporter
    {
        public const string ExporterVersion = "0.2.0";

        private const double MillimetresPerFoot = 304.8;
        private const long NoMaterialKey = -1;

        private readonly Document _doc;
        private readonly GlbExportOptions _options;
        private readonly GlbExportLog _log;
        private readonly Action<int, int, string>? _progress;
        private string _statusPrefix = "";

        /// <param name="doc">An open family document.</param>
        /// <param name="options">The export settings.</param>
        /// <param name="log">Receives the detailed run log.</param>
        /// <param name="progress">Called with (current, total, status).</param>
        public FamilyGlbExporter(
            Document doc,
            GlbExportOptions options,
            GlbExportLog log,
            Action<int, int, string>? progress = null)
        {
            _doc = doc;
            _options = options;
            _log = log;
            _progress = progress;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  EXPORT
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Exports the current type, or every type if the options say so. One
        /// type failing does not stop the others; its result carries the error.
        /// </summary>
        public List<GlbExportResult> Export()
        {
            var wasModified = _doc.IsModified;

            _log.Line($"Exporter {ExporterVersion}, family \"{FamilyName(_doc)}\"");
            _log.Line($"Document path: {_doc.PathName}");
            _log.Line($"Family category: {_doc.OwnerFamily?.FamilyCategory?.Name}");
            _log.Line($"IsModified before: {wasModified}");

            var types = _doc.FamilyManager.Types
                .Cast<FamilyType>()
                .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Only switching types writes to the document, so a single type
            // needs no transaction at all.
            var results = _options.AllTypes && types.Count > 1
                ? ExportEachType(types)
                : new List<GlbExportResult> { ExportCurrentTypeSafely() };

            _log.Section("RUN");
            _log.Line($"IsModified after: {_doc.IsModified}");
            if (_doc.IsModified != wasModified)
                _log.Warning("The document's modified state changed during the export.");

            return results;
        }

        /// <summary>
        /// Switches through the types inside one transaction, which is always
        /// rolled back, so the family is left as it was.
        /// </summary>
        private List<GlbExportResult> ExportEachType(List<FamilyType> types)
        {
            var results = new List<GlbExportResult>();
            var manager = _doc.FamilyManager;

            using var transaction = new Transaction(_doc, "AVES Export GLB");
            var failureOptions = transaction.GetFailureHandlingOptions();
            failureOptions.SetFailuresPreprocessor(new WarningSwallower());
            failureOptions.SetClearAfterRollback(true);
            transaction.SetFailureHandlingOptions(failureOptions);

            transaction.Start();
            try
            {
                for (var i = 0; i < types.Count; i++)
                {
                    _statusPrefix = $"Type {i + 1} of {types.Count}: ";
                    Report(0, 1, types[i].Name);
                    results.Add(ExportType(manager, types[i]));
                }
            }
            finally
            {
                _statusPrefix = "";
                transaction.RollBack();
            }

            var clashes = results
                .Where(r => r.Error == null)
                .GroupBy(r => r.GlbPath, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1);
            foreach (var clash in clashes)
                _log.Warning($"Types {string.Join(", ", clash.Select(r => $"\"{r.TypeName}\""))} share " +
                             $"the file name {Path.GetFileName(clash.Key)}, so only the last one is on disk.");

            return results;
        }

        private GlbExportResult ExportType(FamilyManager manager, FamilyType type)
        {
            try
            {
                manager.CurrentType = type;
                _doc.Regenerate();
            }
            catch (Exception ex)
            {
                return Failed(type.Name, ex);
            }

            return ExportCurrentTypeSafely();
        }

        private GlbExportResult ExportCurrentTypeSafely()
        {
            var typeName = _doc.FamilyManager.CurrentType?.Name ?? "";
            try
            {
                return ExportCurrentType(typeName);
            }
            catch (Exception ex)
            {
                return Failed(typeName, ex);
            }
        }

        private GlbExportResult Failed(string typeName, Exception ex)
        {
            _log.Section($"FAILED: TYPE \"{typeName}\"");
            _log.Line(ex.ToString());
            return new GlbExportResult { TypeName = typeName, Error = ex.Message };
        }

        private GlbExportResult ExportCurrentType(string typeName)
        {
            var familyName = FamilyName(_doc);
            var warningsBefore = _log.WarningCount;

            _log.Section($"TYPE \"{typeName}\"");

            var subparts = CollectSubparts();
            var body = CollectBody();

            var placed = subparts.Where(s => s.Geometry.TriangleCount > 0).ToList();
            var bounds = PanelBounds(placed, body);
            if (bounds.IsEmpty)
                throw new InvalidOperationException("This family has no exportable geometry.");

            var panelOrigin = new Vec3d(bounds.Min.X, (bounds.Min.Y + bounds.Max.Y) / 2, bounds.Min.Z);
            LogBounds(bounds, panelOrigin);

            var materials = BuildMaterials(placed, body);
            var nodes = BuildNodes(subparts, body, familyName, panelOrigin);
            var meshes = nodes.Select(n => n.Mesh).Distinct().ToList();

            Directory.CreateDirectory(_options.OutputFolder);
            var baseName = ExportFileNames.BaseName(familyName, typeName);
            var glbPath = Path.Combine(_options.OutputFolder, baseName + ".glb");
            var bomPath = Path.Combine(_options.OutputFolder, baseName + ".bom.json");

            Report(subparts.Count, subparts.Count, "Writing GLB…");
            GlbFileWriter.Write(glbPath, baseName, nodes, materials);

            var compressed = _options.Compress && Compress(glbPath, subparts.Count);

            var bom = new BomFile
            {
                FamilyName = familyName,
                TypeName = typeName,
                WidthMm = RoundMm(bounds.Max.X - bounds.Min.X),
                HeightMm = RoundMm(bounds.Max.Z - bounds.Min.Z),
                ThicknessMm = RoundMm(bounds.Max.Y - bounds.Min.Y),
                ExportedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
                ExporterVersion = ExporterVersion,
                Subparts = BuildBomLines(subparts),
                Stats = new BomStats
                {
                    Nodes = nodes.Count,
                    Meshes = meshes.Count,
                    Triangles = meshes.Sum(m => m.TriangleCount),
                    FileSizeBytes = new FileInfo(glbPath).Length,
                },
            };
            bom.Write(bomPath);

            LogSummary(subparts, body, nodes, meshes, bom);

            return new GlbExportResult
            {
                TypeName = typeName,
                GlbPath = glbPath,
                BomPath = bomPath,
                Bom = bom,
                WarningCount = _log.WarningCount - warningsBefore,
                Compressed = compressed,
            };
        }

        /// <summary>Compresses the file; a failure only costs a warning.</summary>
        private bool Compress(string glbPath, int partCount)
        {
            Report(partCount, partCount, "Compressing GLB…");
            var outcome = GlbCompressor.Compress(
                glbPath,
                () => Report(partCount, partCount, "Compressing GLB…"));

            if (outcome.Compressed) _log.Line(outcome.Message);
            else _log.Warning(outcome.Message);

            return outcome.Compressed;
        }

        private void Report(int current, int total, string status) =>
            _progress?.Invoke(current, total, _statusPrefix + status);

        /// <summary>Stops Revit warnings from opening dialogs during a type switch.</summary>
        private sealed class WarningSwallower : IFailuresPreprocessor
        {
            public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
            {
                failuresAccessor.DeleteAllWarnings();
                return FailureProcessingResult.Continue;
            }
        }

        /// <summary>The family's name, from the document title without ".rfa".</summary>
        internal static string FamilyName(Document doc)
        {
            var title = doc.Title;
            return title.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)
                ? title.Substring(0, title.Length - 4)
                : title;
        }

        /// <summary>
        /// The panel's extents without the excluded hardware. If that would
        /// leave nothing, all the geometry is used instead.
        /// </summary>
        private Bounds PanelBounds(List<Subpart> placed, GeometryBuffer? body)
        {
            var frameParts = placed.Where(p => !IsFrameExcluded(p.Name)).ToList();
            if (frameParts.Count == 0 && body == null)
            {
                if (placed.Count > 0)
                    _log.Warning("Every subpart is excluded from the panel frame, so all of them are used.");
                frameParts = placed;
            }

            var bounds = new Bounds();
            foreach (var part in frameParts) bounds.Include(part.Geometry.Bounds);
            if (body != null) bounds.Include(body.Bounds);

            _log.Section("PANEL FRAME");
            _log.Line($"Subparts left out of the frame: {placed.Count - frameParts.Count} of {placed.Count}");
            return bounds;
        }

        private bool IsFrameExcluded(string subpartName) =>
            _options.FrameExcludedNames.Any(n => subpartName.Contains(n, StringComparison.OrdinalIgnoreCase));

        private static int RoundMm(double feet) =>
            (int)Math.Round(feet * MillimetresPerFoot, MidpointRounding.AwayFromZero);

        // ─────────────────────────────────────────────────────────────────────
        //  COLLECTION
        // ─────────────────────────────────────────────────────────────────────

        private sealed class Subpart
        {
            public FamilyInstance Instance = null!;
            public string Name = "";
            public bool Hidden;
            public GeometryBuffer Geometry = null!;
            public Transform Placement = Transform.Identity;
            public bool IsTypeGeometry;
        }

        private static Options GeometryOptions() => new Options
        {
            DetailLevel = ViewDetailLevel.Fine,
            ComputeReferences = false,
        };

        /// <summary>
        /// Collects the model instances placed directly in this family. A
        /// family document only holds its own direct children; the parts
        /// nested inside them live in their own family definitions.
        /// </summary>
        private List<Subpart> CollectSubparts()
        {
            var instances = new FilteredElementCollector(_doc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .OrderBy(i => i.Id.Value)
                .ToList();

            _log.Section($"NESTED INSTANCES ({instances.Count})");

            var subparts = new List<Subpart>();
            var index = 0;
            foreach (var instance in instances)
            {
                index++;
                Report(index, instances.Count, instance.Symbol.Name);

                LogInstanceIdentity(instance);

                if (ExclusionReason(instance) is string reason)
                {
                    _log.Line($"  EXCLUDED: {reason}");
                    continue;
                }

                var subpart = new Subpart
                {
                    Instance = instance,
                    Name = SubpartNaming.BuildName(instance),
                    Hidden = IsHidden(instance),
                };
                ReadGeometry(subpart);

                _log.Line($"  subpart name: \"{subpart.Name}\"");
                _log.Line($"  triangles: {subpart.Geometry.TriangleCount}, " +
                          $"type geometry: {subpart.IsTypeGeometry}, " +
                          $"bounds mm: {subpart.Geometry.Bounds.ToMmString()}");

                if (subpart.Hidden)
                    _log.Warning($"Hidden instance {instance.Id.Value} \"{subpart.Name}\" is exported and counted.");
                if (subpart.Geometry.TriangleCount == 0)
                    _log.Warning($"Instance {instance.Id.Value} \"{subpart.Name}\" has no geometry, so it gets no node.");

                subparts.Add(subpart);
            }

            return subparts;
        }

        private static string? ExclusionReason(FamilyInstance instance)
        {
            if (instance.Category?.CategoryType != CategoryType.Model)
                return $"not a model category ({instance.Category?.Name ?? "none"})";
            if (instance.ViewSpecific)
                return "view-specific";
            if (instance.SuperComponent != null)
                return "not a direct child";
            return null;
        }

        private static bool IsHidden(FamilyInstance instance)
        {
            var visible = instance.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
            return visible != null && visible.StorageType == StorageType.Integer && visible.AsInteger() == 0;
        }

        /// <summary>
        /// Reads an instance's geometry. The normal form is one GeometryInstance
        /// holding the type's geometry, which can be shared, plus a transform.
        /// Anything else, such as an instance cut by a void, comes back already
        /// in family coordinates and is read with an identity placement.
        /// </summary>
        private void ReadGeometry(Subpart subpart)
        {
            var instance = subpart.Instance;
            var fallbackMaterial = instance.Category?.Material?.Id.Value ?? NoMaterialKey;
            var geometry = instance.get_Geometry(GeometryOptions());
            var objects = geometry?.ToList() ?? new List<GeometryObject>();

            var soleInstance = objects.OfType<GeometryInstance>().Count() == 1 && !objects.Any(HasSurface)
                ? objects.OfType<GeometryInstance>().Single()
                : null;

            if (soleInstance != null)
            {
                subpart.IsTypeGeometry = true;
                subpart.Placement = soleInstance.Transform;
                subpart.Geometry = new GeometryBuffer(_doc, fallbackMaterial, subpart.Placement, _log);
                subpart.Geometry.Add(soleInstance.GetSymbolGeometry(), Transform.Identity);
            }
            else
            {
                subpart.Placement = Transform.Identity;
                subpart.Geometry = new GeometryBuffer(_doc, fallbackMaterial, Transform.Identity, _log);
                if (geometry != null) subpart.Geometry.Add(geometry, Transform.Identity);
            }
        }

        private static bool HasSurface(GeometryObject obj) =>
            obj is RevitMesh || (obj is Solid solid && solid.Faces.Size > 0);

        /// <summary>
        /// Collects model geometry the family owns directly, such as extrusions.
        /// Voids are skipped: their effect already shows in what they cut.
        /// </summary>
        private GeometryBuffer? CollectBody()
        {
            _log.Section("FAMILY-OWNED GEOMETRY (BODY)");

            var elements = new FilteredElementCollector(_doc)
                .WhereElementIsNotElementType()
                .Where(e => e is not FamilyInstance and not Group
                            && e.Category?.CategoryType == CategoryType.Model)
                .OrderBy(e => e.Id.Value)
                .ToList();

            var body = new GeometryBuffer(_doc, NoMaterialKey, Transform.Identity, _log);
            foreach (var element in elements)
            {
                if (element is GenericForm { IsSolid: false }) continue;

                try
                {
                    var geometry = element.get_Geometry(GeometryOptions());
                    if (geometry == null) continue;

                    var before = body.TriangleCount;
                    body.Add(geometry, Transform.Identity);
                    var added = body.TriangleCount - before;
                    if (added == 0) continue;

                    var visible = element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
                    _log.Line($"{element.GetType().Name} {element.Id.Value} \"{element.Name}\" " +
                              $"category \"{element.Category?.Name}\", visible param: " +
                              $"{(visible == null ? "none" : visible.AsInteger().ToString())}, triangles: {added}");
                }
                catch (Exception ex)
                {
                    _log.Warning($"Could not read {element.GetType().Name} {element.Id.Value}: {ex.Message}");
                }
            }

            _log.Line(body.TriangleCount == 0
                ? "None."
                : $"Body total: {body.TriangleCount} triangles, bounds mm: {body.Bounds.ToMmString()}");
            return body.TriangleCount == 0 ? null : body;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  SCENE
        // ─────────────────────────────────────────────────────────────────────

        private List<ExportNode> BuildNodes(
            List<Subpart> subparts,
            GeometryBuffer? body,
            string familyName,
            Vec3d panelOrigin)
        {
            var nameCounts = subparts
                .GroupBy(s => s.Name, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

            var meshes = new Dictionary<string, ExportMesh>(StringComparer.Ordinal);
            var nodes = new List<ExportNode>();

            foreach (var part in subparts.Where(s => s.Geometry.TriangleCount > 0))
            {
                var key = $"{part.Instance.Symbol.Id.Value}:{part.Geometry.ContentHash():x16}";
                if (!meshes.TryGetValue(key, out var mesh))
                {
                    mesh = new ExportMesh(part.Name, part.Geometry.Triangles);
                    meshes[key] = mesh;
                }

                var nodeName = nameCounts[part.Name] > 1
                    ? $"{part.Name} [{part.Instance.Id.Value}]"
                    : part.Name;

                nodes.Add(new ExportNode(nodeName, mesh, ToNodeMatrix(part.Placement, panelOrigin), part.Name, null));
            }

            if (body != null)
            {
                var name = $"{familyName} (body)";
                nodes.Add(new ExportNode(
                    name,
                    new ExportMesh(name, body.Triangles),
                    ToNodeMatrix(Transform.Identity, panelOrigin),
                    null,
                    "body"));
            }

            return nodes;
        }

        private static System.Numerics.Matrix4x4 ToNodeMatrix(Transform t, Vec3d panelOrigin) =>
            GltfAxes.NodeMatrix(ToVec(t.BasisX), ToVec(t.BasisY), ToVec(t.BasisZ), ToVec(t.Origin), panelOrigin);

        internal static Vec3d ToVec(XYZ p) => new Vec3d(p.X, p.Y, p.Z);

        /// <summary>
        /// One material per key in use. Geometry with no material, or one that
        /// does not resolve in this document, uses a neutral grey.
        /// </summary>
        private Dictionary<long, ExportMaterial> BuildMaterials(List<Subpart> placed, GeometryBuffer? body)
        {
            var keys = placed.SelectMany(p => p.Geometry.Triangles.Keys)
                .Concat(body?.Triangles.Keys ?? Enumerable.Empty<long>())
                .Distinct()
                .OrderBy(k => k);

            _log.Section("MATERIALS");

            var materials = new Dictionary<long, ExportMaterial>();
            foreach (var key in keys)
            {
                var material = key == NoMaterialKey ? null : _doc.GetElement(new ElementId(key)) as RevitMaterial;
                materials[key] = material is { Color.IsValid: true }
                    ? new ExportMaterial(
                        material.Name,
                        material.Color.Red,
                        material.Color.Green,
                        material.Color.Blue,
                        1f - material.Transparency / 100f)
                    : new ExportMaterial("Default", 160, 160, 165, 1f);

                var m = materials[key];
                _log.Line($"{key}: \"{m.Name}\" rgb({m.Red},{m.Green},{m.Blue}) opacity {m.Opacity:0.00}");
            }

            return materials;
        }

        private static List<BomSubpart> BuildBomLines(IEnumerable<Subpart> subparts) => subparts
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new BomSubpart { Name = g.Key, Qty = g.Count() })
            .ToList();

        // ─────────────────────────────────────────────────────────────────────
        //  LOGGING
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Logs what identifies an instance. Values are quoted and unmodified so
        /// double spaces stay visible.
        /// </summary>
        private void LogInstanceIdentity(FamilyInstance instance)
        {
            var symbol = instance.Symbol;
            var visible = instance.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);

            _log.Line("");
            _log.Line($"Instance {instance.Id.Value}");
            _log.Line($"  family name: \"{symbol.FamilyName}\"");
            _log.Line($"  type name: \"{symbol.Name}\"");
            _log.Line($"  visible param: {(visible == null ? "none" : visible.AsInteger().ToString())}");
            _log.Line($"  mirrored: {instance.Mirrored}");
        }

        private void LogBounds(Bounds bounds, Vec3d panelOrigin)
        {
            _log.Line($"Bounds in family coordinates, mm: {bounds.ToMmString()}");
            _log.Line($"Applied offset, mm: X {-panelOrigin.X * MillimetresPerFoot:F3}, " +
                      $"Y {-panelOrigin.Y * MillimetresPerFoot:F3}, Z {-panelOrigin.Z * MillimetresPerFoot:F3}");
            _log.Line($"Family origin to thickness centre, mm: {panelOrigin.Y * MillimetresPerFoot:F3}");
        }

        private void LogSummary(
            List<Subpart> subparts,
            GeometryBuffer? body,
            List<ExportNode> nodes,
            List<ExportMesh> meshes,
            BomFile bom)
        {
            _log.Section("MESHES (largest first)");
            foreach (var mesh in meshes.OrderByDescending(m => m.TriangleCount))
            {
                var count = nodes.Count(n => ReferenceEquals(n.Mesh, mesh));
                _log.Line($"{mesh.TriangleCount,9} triangles x {count,3} nodes  \"{mesh.Name}\"");
                if (_options.FlagPerforations && mesh.TriangleCount > _options.TriangleWarningThreshold)
                    _log.Warning($"\"{mesh.Name}\" has {mesh.TriangleCount} triangles, above the " +
                                 $"{_options.TriangleWarningThreshold} threshold: likely perforated.");
            }

            _log.Section("BOM");
            foreach (var line in bom.Subparts)
                _log.Line($"{line.Qty,4} x \"{line.Name}\"");
            _log.Line($"{bom.Subparts.Count} subparts, {bom.Subparts.Sum(s => s.Qty)} pieces");

            var hidden = subparts.Count(s => s.Hidden);
            if (hidden > 0)
            {
                _log.Line("");
                _log.Line($"Same BOM with the {hidden} hidden instances left out:");
                var visibleLines = BuildBomLines(subparts.Where(s => !s.Hidden));
                foreach (var line in visibleLines)
                    _log.Line($"{line.Qty,4} x \"{line.Name}\"");
                _log.Line($"{visibleLines.Count} subparts, {visibleLines.Sum(s => s.Qty)} pieces");
            }

            _log.Section("RESULT");
            _log.Line($"Size mm: {bom.WidthMm} x {bom.HeightMm} x {bom.ThicknessMm} (width, height, thickness)");
            _log.Line($"Nodes: {nodes.Count}{(body != null ? ", including the body node" : "")}");
            _log.Line($"Meshes: {meshes.Count}");
            _log.Line($"Triangles stored: {meshes.Sum(m => m.TriangleCount)}, " +
                      $"drawn: {nodes.Sum(n => n.Mesh.TriangleCount)}");
            _log.Line($"GLB size: {bom.Stats.FileSizeBytes} bytes");
            _log.Line($"Warnings: {_log.WarningCount}");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  GEOMETRY BUFFER
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>An axis-aligned box in Revit feet.</summary>
    internal sealed class Bounds
    {
        public Vec3d Min { get; private set; } = new(double.MaxValue, double.MaxValue, double.MaxValue);
        public Vec3d Max { get; private set; } = new(double.MinValue, double.MinValue, double.MinValue);
        public bool IsEmpty => Min.X > Max.X;

        public void Include(Vec3d p)
        {
            Min = new Vec3d(Math.Min(Min.X, p.X), Math.Min(Min.Y, p.Y), Math.Min(Min.Z, p.Z));
            Max = new Vec3d(Math.Max(Max.X, p.X), Math.Max(Max.Y, p.Y), Math.Max(Max.Z, p.Z));
        }

        public void Include(Bounds other)
        {
            if (other.IsEmpty) return;
            Include(other.Min);
            Include(other.Max);
        }

        public string ToMmString()
        {
            if (IsEmpty) return "empty";
            const double k = 304.8;
            return $"X {Min.X * k:F2}..{Max.X * k:F2}, Y {Min.Y * k:F2}..{Max.Y * k:F2}, Z {Min.Z * k:F2}..{Max.Z * k:F2}";
        }
    }

    /// <summary>
    /// Triangulates Revit geometry into triangles grouped by material, in the
    /// local frame of its placement, and tracks the bounds in family coordinates.
    /// </summary>
    internal sealed class GeometryBuffer
    {
        private readonly Document _doc;
        private readonly long _fallbackMaterial;
        private readonly Transform _placement;
        private readonly GlbExportLog _log;

        public Dictionary<long, List<ExportTriangle>> Triangles { get; } = new();
        public Bounds Bounds { get; } = new();
        public int TriangleCount => Triangles.Values.Sum(t => t.Count);

        /// <param name="doc">The document the geometry came from.</param>
        /// <param name="fallbackMaterial">Material key for unassigned geometry.</param>
        /// <param name="placement">Local frame to family coordinates.</param>
        /// <param name="log">The run log.</param>
        public GeometryBuffer(Document doc, long fallbackMaterial, Transform placement, GlbExportLog log)
        {
            _doc = doc;
            _fallbackMaterial = fallbackMaterial;
            _placement = placement;
            _log = log;
        }

        /// <summary>Adds geometry, descending into nested instances.</summary>
        /// <param name="geometry">The geometry to add.</param>
        /// <param name="toLocal">Geometry coordinates to the local frame.</param>
        public void Add(GeometryElement geometry, Transform toLocal)
        {
            foreach (var obj in geometry)
            {
                switch (obj)
                {
                    case Solid solid:
                        foreach (Face face in solid.Faces)
                            AddFace(face, solid.GraphicsStyleId, toLocal);
                        break;
                    case RevitMesh mesh:
                        AddMesh(mesh, MaterialKey(mesh.MaterialElementId, mesh.GraphicsStyleId), toLocal);
                        break;
                    case GeometryInstance instance:
                        Add(instance.GetSymbolGeometry(), toLocal.Multiply(instance.Transform));
                        break;
                }
            }
        }

        private void AddFace(Face face, ElementId styleId, Transform toLocal)
        {
            try
            {
                var mesh = face.Triangulate();
                if (mesh != null) AddMesh(mesh, MaterialKey(face.MaterialElementId, styleId), toLocal);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                _log.Warning($"Skipped a face that could not be triangulated: {ex.Message}");
            }
        }

        /// <summary>
        /// The material key: the face's own material, then its subcategory's, then
        /// the fallback.
        /// </summary>
        private long MaterialKey(ElementId materialId, ElementId styleId)
        {
            if (materialId != ElementId.InvalidElementId) return materialId.Value;

            var style = _doc.GetElement(styleId) as GraphicsStyle;
            return style?.GraphicsStyleCategory?.Material?.Id.Value ?? _fallbackMaterial;
        }

        private void AddMesh(RevitMesh mesh, long key, Transform toLocal)
        {
            if (!Triangles.TryGetValue(key, out var triangles))
            {
                triangles = new List<ExportTriangle>();
                Triangles[key] = triangles;
            }

            for (var i = 0; i < mesh.NumTriangles; i++)
            {
                var triangle = mesh.get_Triangle(i);
                var a = toLocal.OfPoint(triangle.get_Vertex(0));
                var b = toLocal.OfPoint(triangle.get_Vertex(1));
                var c = toLocal.OfPoint(triangle.get_Vertex(2));

                Bounds.Include(FamilyGlbExporter.ToVec(_placement.OfPoint(a)));
                Bounds.Include(FamilyGlbExporter.ToVec(_placement.OfPoint(b)));
                Bounds.Include(FamilyGlbExporter.ToVec(_placement.OfPoint(c)));

                var pointA = GltfAxes.Point(FamilyGlbExporter.ToVec(a));
                var pointB = GltfAxes.Point(FamilyGlbExporter.ToVec(b));
                var pointC = GltfAxes.Point(FamilyGlbExporter.ToVec(c));

                var flat = Vector3.Cross(pointB - pointA, pointC - pointA);
                if (flat.LengthSquared() < 1e-18f) continue;
                flat = Vector3.Normalize(flat);

                var normalA = CornerNormal(mesh, i, triangle, 0, toLocal) ?? flat;
                var normalB = CornerNormal(mesh, i, triangle, 1, toLocal) ?? flat;
                var normalC = CornerNormal(mesh, i, triangle, 2, toLocal) ?? flat;

                // Revit's triangle order is not a dependable outward winding,
                // and a mirrored transform reverses it, so the normals decide.
                if (Vector3.Dot(flat, normalA + normalB + normalC) < 0f)
                    triangles.Add(new ExportTriangle(pointA, pointC, pointB, normalA, normalC, normalB));
                else
                    triangles.Add(new ExportTriangle(pointA, pointB, pointC, normalA, normalB, normalC));
            }
        }

        private static Vector3? CornerNormal(RevitMesh mesh, int triangleIndex, MeshTriangle triangle, int corner, Transform toLocal)
        {
            if (mesh.NumberOfNormals == 0) return null;

            var normal = mesh.DistributionOfNormals switch
            {
                DistributionOfNormals.AtEachPoint => mesh.GetNormal((int)triangle.get_Index(corner)),
                DistributionOfNormals.OnEachFacet => mesh.GetNormal(triangleIndex),
                DistributionOfNormals.OnePerFace => mesh.GetNormal(0),
                _ => null,
            };

            return normal == null || normal.IsZeroLength()
                ? null
                : GltfAxes.Direction(FamilyGlbExporter.ToVec(toLocal.OfVector(normal)));
        }

        /// <summary>
        /// FNV-1a hash of the triangles, with positions rounded to a micrometre.
        /// Equal hashes mean the same shape in the same local frame.
        /// </summary>
        public ulong ContentHash()
        {
            const ulong prime = 1099511628211UL;
            var hash = 14695981039346656037UL;

            void Mix(long value) => hash = (hash ^ (ulong)value) * prime;
            void MixPoint(Vector3 p)
            {
                Mix((long)MathF.Round(p.X * 1e6f));
                Mix((long)MathF.Round(p.Y * 1e6f));
                Mix((long)MathF.Round(p.Z * 1e6f));
            }

            foreach (var (material, triangles) in Triangles.OrderBy(t => t.Key))
            {
                Mix(material);
                foreach (var t in triangles)
                {
                    MixPoint(t.A);
                    MixPoint(t.B);
                    MixPoint(t.C);
                }
            }

            return hash;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  LOG
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The run log. Each line is flushed as it is written, so a crash in the
    /// middle of an export still leaves everything up to that point on disk.
    /// </summary>
    internal sealed class GlbExportLog : IDisposable
    {
        private readonly StreamWriter _writer;

        public string Path { get; }
        public int WarningCount { get; private set; }

        /// <summary>Starts a new log file in the given folder.</summary>
        public GlbExportLog(string folder, string baseName)
        {
            Directory.CreateDirectory(folder);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            Path = System.IO.Path.Combine(folder, $"{baseName}-{stamp}.log");
            _writer = new StreamWriter(Path, false, System.Text.Encoding.UTF8) { AutoFlush = true };
        }

        public void Line(string text) => _writer.WriteLine(text);

        public void Section(string title)
        {
            _writer.WriteLine();
            _writer.WriteLine($"===== {title} =====");
        }

        public void Warning(string text)
        {
            WarningCount++;
            _writer.WriteLine($"WARNING: {text}");
        }

        public void Dispose() => _writer.Dispose();
    }
}
