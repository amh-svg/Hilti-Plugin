// -----------------------------------------------------------------------
// Title: GLB Compressor
// Summary: Compresses a .glb with Meshopt through the gltf-transform CLI,
//          when npx is available.
// Key logic: Uses the "meshopt" command, not "optimize": optimize also
//            flattens, joins, instances and simplifies, which would destroy
//            the named subpart nodes. The file is only replaced if every node
//            survives with its name and extras. Any failure leaves the
//            original in place, so compression can never fail an export.
// -----------------------------------------------------------------------

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AVES.Core.GlbExport
{
    /// <summary>What a compression attempt did.</summary>
    /// <param name="Compressed">True when the file was replaced.</param>
    /// <param name="Message">A line for the log.</param>
    internal sealed record GlbCompressionOutcome(bool Compressed, string Message);

    internal static class GlbCompressor
    {
        private const int TimeoutMilliseconds = 180000;
        private const int PollMilliseconds = 250;
        private const int JsonChunkOffset = 20;
        private const string InputName = "in.glb";
        private const string OutputName = "out.glb";

        /// <summary>True when npx is on the PATH.</summary>
        public static bool IsAvailable()
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
                .Any(folder => File.Exists(Combine(folder, "npx.cmd")) || File.Exists(Combine(folder, "npx.exe")));
        }

        private static string Combine(string folder, string file)
        {
            try { return Path.Combine(folder.Trim('"'), file); }
            catch (ArgumentException) { return ""; }
        }

        /// <summary>Compresses a .glb in place, if that is possible and safe.</summary>
        /// <param name="glbPath">The file to compress.</param>
        /// <param name="onWait">Called regularly while the CLI runs.</param>
        public static GlbCompressionOutcome Compress(string glbPath, Action? onWait = null)
        {
            if (!IsAvailable())
                return new GlbCompressionOutcome(false, "Compression skipped: npx was not found on the PATH.");

            // The CLI works on fixed names in a temp folder, so nothing from the
            // family name, such as % or &, ever reaches the command line.
            var workFolder = Path.Combine(Path.GetTempPath(), "AVES-glb-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(workFolder);
                var input = Path.Combine(workFolder, InputName);
                var output = Path.Combine(workFolder, OutputName);
                File.Copy(glbPath, input);

                var failure = RunCli(workFolder, onWait);
                if (failure != null)
                    return new GlbCompressionOutcome(false, $"Compression skipped: {failure}");

                if (!File.Exists(output))
                    return new GlbCompressionOutcome(false, "Compression skipped: the CLI wrote no file.");

                if (!SameNodes(input, output))
                    return new GlbCompressionOutcome(false,
                        "Compression skipped: it changed the nodes, so the uncompressed file was kept.");

                var before = new FileInfo(input).Length;
                var after = new FileInfo(output).Length;
                File.Copy(output, glbPath, true);
                return new GlbCompressionOutcome(true, $"Compressed from {before} to {after} bytes.");
            }
            catch (Exception ex)
            {
                return new GlbCompressionOutcome(false, $"Compression skipped: {ex.Message}");
            }
            finally
            {
                TryDelete(workFolder);
            }
        }

        /// <summary>Runs the CLI and returns why it failed, or null on success.</summary>
        private static string? RunCli(string workFolder, Action? onWait)
        {
            var startInfo = new ProcessStartInfo
            {
                // npx is a .cmd file on Windows, which only cmd.exe can start.
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/c npx --yes @gltf-transform/cli meshopt {InputName} {OutputName}",
                WorkingDirectory = workFolder,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var process = new Process { StartInfo = startInfo };
            var errors = new StringBuilder();
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errors) errors.AppendLine(e.Data); };
            process.OutputDataReceived += (_, _) => { };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var waited = Stopwatch.StartNew();
            while (!process.WaitForExit(PollMilliseconds))
            {
                onWait?.Invoke();
                if (waited.ElapsedMilliseconds <= TimeoutMilliseconds) continue;

                process.Kill(true);
                return $"the CLI took longer than {TimeoutMilliseconds / 1000} seconds.";
            }

            process.WaitForExit();
            if (process.ExitCode == 0) return null;

            lock (errors)
                return $"the CLI exited with code {process.ExitCode}. {errors.ToString().Trim()}";
        }

        /// <summary>
        /// True when both files hold the same nodes, by name and extras. The JSON
        /// is read directly, because the geometry is not decoded here.
        /// </summary>
        public static bool SameNodes(string firstGlb, string secondGlb) =>
            NodeSignatures(firstGlb).SequenceEqual(NodeSignatures(secondGlb), StringComparer.Ordinal);

        private static List<string> NodeSignatures(string glbPath)
        {
            using var stream = File.OpenRead(glbPath);
            using var reader = new BinaryReader(stream);

            stream.Position = JsonChunkOffset - 8;
            var json = new byte[reader.ReadUInt32()];
            stream.Position = JsonChunkOffset;
            stream.ReadExactly(json);

            using var document = JsonDocument.Parse(json);
            var signatures = new List<string>();
            if (!document.RootElement.TryGetProperty("nodes", out var nodes)) return signatures;

            foreach (var node in nodes.EnumerateArray())
            {
                if (!node.TryGetProperty("mesh", out _)) continue;

                var name = node.TryGetProperty("name", out var n) ? n.GetString() : "";
                var extras = node.TryGetProperty("extras", out var e) ? e.GetRawText() : "";
                signatures.Add($"{name}|{extras}");
            }

            signatures.Sort(StringComparer.Ordinal);
            return signatures;
        }

        private static void TryDelete(string folder)
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
            catch (IOException)
            {
                // A leftover temp folder is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // A leftover temp folder is harmless.
            }
        }
    }
}
