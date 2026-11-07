// -----------------------------------------------------------------------
// Title: GLB Document
// Summary: The Revit-free half of the GLB + BOM export: the coordinate
//          convention, plain mesh and node data, and the writers for the
//          .glb and .bom.json files.
// Key logic: Mesh vertices are stored in metres on glTF axes but in the
//            subpart's own frame, so subparts of one type can share a mesh.
//            Each node carries the placement, including any mirroring. No
//            Revit types appear here, so this file can be tested outside
//            Revit.
// -----------------------------------------------------------------------

using System.IO;
using System.Numerics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using GltfMeshBuilder = SharpGLTF.Geometry.MeshBuilder<SharpGLTF.Geometry.VertexTypes.VertexPositionNormal>;
using GltfVertex = SharpGLTF.Geometry.VertexTypes.VertexPositionNormal;

namespace AVES.Core.GlbExport
{
    // ─────────────────────────────────────────────────────────────────────────
    //  COORDINATES
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>A plain double vector, so this file needs no Revit XYZ.</summary>
    internal readonly record struct Vec3d(double X, double Y, double Z);

    /// <summary>
    /// The web app's convention: Revit feet to metres, and Revit Z-up to glTF
    /// Y-up, (x, y, z) becomes (x, z, -y).
    /// </summary>
    internal static class GltfAxes
    {
        public const double MetresPerFoot = 0.3048;

        /// <summary>A point in Revit feet as a point in metres on glTF axes.</summary>
        public static Vector3 Point(Vec3d p) => new Vector3(
            (float)(p.X * MetresPerFoot),
            (float)(p.Z * MetresPerFoot),
            (float)(-p.Y * MetresPerFoot));

        /// <summary>A Revit direction as a unit vector on glTF axes.</summary>
        public static Vector3 Direction(Vec3d d)
        {
            var v = new Vector3((float)d.X, (float)d.Z, (float)-d.Y);
            var length = v.Length();
            return length > 1e-12f ? v / length : Vector3.UnitY;
        }

        /// <summary>
        /// The node matrix that places a mesh stored with <see cref="Point"/>.
        /// With Revit transform T, panel origin O and conversion A, it is
        /// A (T - O) A^-1, so the foot-to-metre scale cancels out of the
        /// rotation and only the translation is scaled.
        /// </summary>
        /// <remarks>System.Numerics uses row vectors, so each row is the image
        /// of one glTF axis. Mirrored instances keep a negative determinant,
        /// which glTF renderers handle by flipping the winding.</remarks>
        public static Matrix4x4 NodeMatrix(Vec3d basisX, Vec3d basisY, Vec3d basisZ, Vec3d origin, Vec3d panelOrigin)
        {
            var rowX = Axes(basisX);
            var rowY = Axes(basisZ);
            var rowZ = -Axes(basisY);
            var translation = Point(new Vec3d(
                origin.X - panelOrigin.X,
                origin.Y - panelOrigin.Y,
                origin.Z - panelOrigin.Z));

            return new Matrix4x4(
                rowX.X, rowX.Y, rowX.Z, 0f,
                rowY.X, rowY.Y, rowY.Z, 0f,
                rowZ.X, rowZ.Y, rowZ.Z, 0f,
                translation.X, translation.Y, translation.Z, 1f);
        }

        private static Vector3 Axes(Vec3d v) => new Vector3((float)v.X, (float)v.Z, (float)-v.Y);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  DATA MODEL
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>One triangle: three positions in metres and their unit normals.</summary>
    internal readonly record struct ExportTriangle(
        Vector3 A, Vector3 B, Vector3 C,
        Vector3 NormalA, Vector3 NormalB, Vector3 NormalC);

    /// <summary>One Revit material, reduced to a base colour.</summary>
    /// <param name="Name">The material name.</param>
    /// <param name="Red">Red channel, 0-255, sRGB as Revit stores it.</param>
    /// <param name="Green">Green channel, 0-255, sRGB.</param>
    /// <param name="Blue">Blue channel, 0-255, sRGB.</param>
    /// <param name="Opacity">1 for solid, 0 for fully transparent.</param>
    internal sealed record ExportMaterial(string Name, byte Red, byte Green, byte Blue, float Opacity);

    /// <summary>A glTF mesh: triangles grouped by material key.</summary>
    internal sealed class ExportMesh
    {
        public string Name { get; }
        public Dictionary<long, List<ExportTriangle>> TrianglesByMaterial { get; }
        public int TriangleCount => TrianglesByMaterial.Values.Sum(t => t.Count);

        public ExportMesh(string name, Dictionary<long, List<ExportTriangle>> trianglesByMaterial)
        {
            Name = name;
            TrianglesByMaterial = trianglesByMaterial;
        }
    }

    /// <summary>A glTF node. Node names must be unique within the file.</summary>
    /// <param name="Name">The node name, which starts with the subpart name.</param>
    /// <param name="Mesh">The shared mesh this node places.</param>
    /// <param name="Matrix">Placement from <see cref="GltfAxes.NodeMatrix"/>.</param>
    /// <param name="Subpart">The exact subpart name, or null for the body.</param>
    /// <param name="Role">The extras role, or null for a normal subpart.</param>
    internal sealed record ExportNode(string Name, ExportMesh Mesh, Matrix4x4 Matrix, string? Subpart, string? Role);

    // ─────────────────────────────────────────────────────────────────────────
    //  GLB WRITER
    // ─────────────────────────────────────────────────────────────────────────

    internal static class GlbFileWriter
    {
        /// <summary>Writes the nodes to a binary glTF file.</summary>
        /// <param name="path">The .glb path to write.</param>
        /// <param name="sceneName">The glTF scene name.</param>
        /// <param name="nodes">Nodes with unique names.</param>
        /// <param name="materials">Materials by the keys the meshes use.</param>
        public static void Write(
            string path,
            string sceneName,
            IReadOnlyList<ExportNode> nodes,
            IReadOnlyDictionary<long, ExportMaterial> materials)
        {
            var materialBuilders = materials.ToDictionary(m => m.Key, m => ToBuilder(m.Value));
            var meshBuilders = new Dictionary<ExportMesh, GltfMeshBuilder>();
            var scene = new SceneBuilder(sceneName);

            foreach (var node in nodes)
            {
                if (!meshBuilders.TryGetValue(node.Mesh, out var meshBuilder))
                {
                    meshBuilder = ToBuilder(node.Mesh, materialBuilders);
                    meshBuilders[node.Mesh] = meshBuilder;
                }

                scene.AddRigidMesh(meshBuilder, node.Matrix).WithName(node.Name);
            }

            var model = scene.ToGltf2();

            // Extras go on after the build: the names are unique, and this way
            // they land on the node itself whatever the builder does internally.
            var nodesByName = nodes.ToDictionary(n => n.Name, StringComparer.Ordinal);
            foreach (var logicalNode in model.LogicalNodes)
            {
                if (logicalNode.Name is string name && nodesByName.TryGetValue(name, out var node))
                    logicalNode.Extras = ToExtras(node);
            }

            model.SaveGLB(path);
        }

        private static JsonObject ToExtras(ExportNode node)
        {
            var extras = new JsonObject { ["subpart"] = node.Subpart };
            if (node.Role != null) extras["role"] = node.Role;
            return extras;
        }

        private static GltfMeshBuilder ToBuilder(
            ExportMesh mesh,
            IReadOnlyDictionary<long, MaterialBuilder> materials)
        {
            var builder = new GltfMeshBuilder(mesh.Name);

            foreach (var (materialKey, triangles) in mesh.TrianglesByMaterial)
            {
                if (triangles.Count == 0) continue;

                var primitive = builder.UsePrimitive(materials[materialKey]);
                foreach (var t in triangles)
                {
                    primitive.AddTriangle(
                        new GltfVertex(t.A, t.NormalA),
                        new GltfVertex(t.B, t.NormalB),
                        new GltfVertex(t.C, t.NormalC));
                }
            }

            return builder;
        }

        private static MaterialBuilder ToBuilder(ExportMaterial material)
        {
            // Metallic defaults to 1 in glTF, which renders near-black without
            // an environment map, so it is set off.
            var builder = new MaterialBuilder(material.Name)
                .WithMetallicRoughnessShader()
                .WithMetallicRoughness(0f, 0.7f)
                .WithBaseColor(new Vector4(
                    SrgbToLinear(material.Red),
                    SrgbToLinear(material.Green),
                    SrgbToLinear(material.Blue),
                    material.Opacity));

            return material.Opacity < 1f ? builder.WithAlpha(AlphaMode.BLEND) : builder;
        }

        /// <summary>glTF base colours are linear; Revit's are sRGB.</summary>
        private static float SrgbToLinear(byte channel)
        {
            var c = channel / 255.0;
            return (float)(c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4));
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  BOM WRITER
    // ─────────────────────────────────────────────────────────────────────────

    internal sealed class BomSubpart
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("qty")] public int Qty { get; set; }
    }

    internal sealed class BomStats
    {
        [JsonPropertyName("nodes")] public int Nodes { get; set; }
        [JsonPropertyName("meshes")] public int Meshes { get; set; }
        [JsonPropertyName("triangles")] public int Triangles { get; set; }
        [JsonPropertyName("file_size_bytes")] public long FileSizeBytes { get; set; }
    }

    /// <summary>The .bom.json file, in the web app's catalog format.</summary>
    internal sealed class BomFile
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            // Keeps names such as "OM LC4-ASM-0038_001 - Cam" readable and
            // byte-exact rather than \u-escaped.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        [JsonPropertyName("family_name")] public string FamilyName { get; set; } = "";
        [JsonPropertyName("type_name")] public string TypeName { get; set; } = "";
        [JsonPropertyName("width_mm")] public int WidthMm { get; set; }
        [JsonPropertyName("height_mm")] public int HeightMm { get; set; }
        [JsonPropertyName("thickness_mm")] public int ThicknessMm { get; set; }
        [JsonPropertyName("exported_at")] public string ExportedAt { get; set; } = "";
        [JsonPropertyName("exporter_version")] public string ExporterVersion { get; set; } = "";
        [JsonPropertyName("subparts")] public List<BomSubpart> Subparts { get; set; } = new();
        [JsonPropertyName("stats")] public BomStats Stats { get; set; } = new();

        public void Write(string path) =>
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  FILE NAMES
    // ─────────────────────────────────────────────────────────────────────────

    internal static class ExportFileNames
    {
        /// <summary>
        /// "&lt;FamilyName&gt;-&lt;TypeName&gt;" with spaces replaced by "-" and
        /// characters invalid on Windows removed.
        /// </summary>
        public static string BaseName(string familyName, string typeName)
        {
            var name = string.IsNullOrWhiteSpace(typeName) ? familyName : $"{familyName}-{typeName}";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(name.Replace(' ', '-').Where(c => !invalid.Contains(c)).ToArray());
        }
    }
}
