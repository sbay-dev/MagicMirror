using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace MagicMirror.Native.Mirror;

/// <summary>
/// OCR engine that shells out to a Tesseract binary and parses its TSV output into
/// line-level <see cref="OcrTextRegion"/>s. This is the project's primary OCR path and
/// uses the THEORYS portable language data (eng+ara) via <c>--tessdata-dir</c>.
///
/// It is platform-neutral: it works anywhere a Tesseract executable is resolvable
/// (Windows <c>tesseract.exe</c>, or a native binary on Android once bundled). If no
/// binary is found, <see cref="IsAvailableAsync"/> returns false and the pipeline falls
/// back to the OS OCR engine.
///
/// Invocation protocol (reproducible):
///   <c>tesseract "&lt;png&gt;" stdout --tessdata-dir "&lt;tessdata&gt;" -l &lt;langs&gt; --psm 6 tsv</c>
/// TSV columns: level, page, block, par, line, word, left, top, width, height, conf, text.
/// Words (level 5) are grouped by (block,par,line) into line regions so the translator
/// receives whole phrases rather than isolated words.
/// </summary>
public sealed class TesseractOcrEngine : IOcrEngine
{
    public string Name => "Tesseract";

    private string? _resolvedExe;

    public async Task<bool> IsAvailableAsync()
    {
        var exe = await ResolveExeAsync();
        return exe != null;
    }

    public async Task<IReadOnlyList<OcrTextRegion>> RecognizeAsync(
        CaptureResult capture, MirrorSettings settings, CancellationToken ct = default)
    {
        var exe = await ResolveExeAsync(settings);
        if (exe == null || capture.Png == null) return Array.Empty<OcrTextRegion>();

        string tmp = Path.Combine(Path.GetTempPath(), $"mm_{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(tmp, capture.Png, ct);
        try
        {
            var args = new StringBuilder();
            args.Append('"').Append(tmp).Append("\" stdout ");
            var tessData = ResolveTessDataDir(settings, exe);
            if (!string.IsNullOrWhiteSpace(tessData))
                args.Append("--tessdata-dir \"").Append(tessData).Append("\" ");
            args.Append("-l ").Append(string.IsNullOrWhiteSpace(settings.TesseractLanguages) ? "eng" : settings.TesseractLanguages);
            args.Append(" --psm 6 tsv");

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args.ToString(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var proc = Process.Start(psi);
            if (proc == null) return Array.Empty<OcrTextRegion>();
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            string tsv = await stdout;
            if (proc.ExitCode != 0)
            {
                var error = (await stderr).Trim();
                MirrorLog.Info($"Tesseract exited {proc.ExitCode}" + (error.Length > 0 ? $": {error}" : ""));
                return Array.Empty<OcrTextRegion>();
            }
            return ParseTsvIntoLines(tsv);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Groups Tesseract word rows into line regions (union bbox, joined text, mean conf).</summary>
    private static List<OcrTextRegion> ParseTsvIntoLines(string tsv)
    {
        var lines = new Dictionary<(int, int, int), LineAcc>();
        var rows = tsv.Split('\n');
        for (int r = 1; r < rows.Length; r++) // skip header
        {
            var cols = rows[r].Split('\t');
            if (cols.Length < 12) continue;
            if (!int.TryParse(cols[0], out int level) || level != 5) continue; // words only
            string text = cols[11].Trim();
            if (text.Length == 0) continue;

            if (!int.TryParse(cols[2], out int block) ||
                !int.TryParse(cols[3], out int par) ||
                !int.TryParse(cols[4], out int line) ||
                !int.TryParse(cols[6], out int left) ||
                !int.TryParse(cols[7], out int top) ||
                !int.TryParse(cols[8], out int w) ||
                !int.TryParse(cols[9], out int h)) continue;
            float conf = float.TryParse(cols[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var cf) ? cf : -1f;

            var key = (block, par, line);
            if (!lines.TryGetValue(key, out var acc))
            {
                acc = new LineAcc { Left = left, Top = top, Right = left + w, Bottom = top + h };
                lines[key] = acc;
            }
            acc.Left = Math.Min(acc.Left, left);
            acc.Top = Math.Min(acc.Top, top);
            acc.Right = Math.Max(acc.Right, left + w);
            acc.Bottom = Math.Max(acc.Bottom, top + h);
            if (acc.Words.Length > 0) acc.Words.Append(' ');
            acc.Words.Append(text);
            if (conf >= 0) { acc.ConfSum += conf; acc.ConfCount++; }
        }

        var result = new List<OcrTextRegion>(lines.Count);
        foreach (var acc in lines.Values)
        {
            var text = acc.Words.ToString().Trim();
            if (text.Length == 0) continue;
            result.Add(new OcrTextRegion
            {
                Text = text,
                X = acc.Left,
                Y = acc.Top,
                Width = Math.Max(1, acc.Right - acc.Left),
                Height = Math.Max(1, acc.Bottom - acc.Top),
                LineHeightHint = Math.Max(1, acc.Bottom - acc.Top),
                Confidence = acc.ConfCount > 0 ? acc.ConfSum / acc.ConfCount : -1f,
            });
        }
        return result;
    }

    private sealed class LineAcc
    {
        public int Left, Top, Right, Bottom;
        public readonly StringBuilder Words = new();
        public float ConfSum;
        public int ConfCount;
    }

    private async Task<string?> ResolveExeAsync(MirrorSettings? settings = null)
    {
        if (_resolvedExe != null && File.Exists(_resolvedExe)) return _resolvedExe;

        // 1. Explicit setting
        if (settings != null && !string.IsNullOrWhiteSpace(settings.TesseractExePath) && File.Exists(settings.TesseractExePath))
            return _resolvedExe = settings.TesseractExePath;

        // 2. Common Windows install locations
        foreach (var candidate in new[]
        {
            @"C:\Program Files\Tesseract-OCR\tesseract.exe",
            @"C:\Program Files (x86)\Tesseract-OCR\tesseract.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Tesseract-OCR", "tesseract.exe"),
        })
        {
            if (File.Exists(candidate)) return _resolvedExe = candidate;
        }

        // 3. On PATH (where/which)
        try
        {
            string finder = OperatingSystem.IsWindows() ? "where" : "which";
            var psi = new ProcessStartInfo
            {
                FileName = finder, Arguments = "tesseract",
                RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p != null)
            {
                string outp = await p.StandardOutput.ReadToEndAsync();
                await p.WaitForExitAsync();
                var first = outp.Split('\n').Select(s => s.Trim()).FirstOrDefault(s => s.Length > 0);
                if (first != null && File.Exists(first)) return _resolvedExe = first;
            }
        }
        catch { }

        return null;
    }

    private static string? ResolveTessDataDir(MirrorSettings settings, string exe)
    {
        if (!string.IsNullOrWhiteSpace(settings.TessDataPath) && Directory.Exists(settings.TessDataPath))
            return settings.TessDataPath;

        var candidates = new List<string>();
        AddTessDataCandidate(candidates, Environment.GetEnvironmentVariable("TESSDATA_PREFIX"));
        AddTessDataCandidate(candidates, Path.Combine(AppContext.BaseDirectory, "tessdata"));
        AddTessDataCandidate(candidates, Path.Combine(AppContext.BaseDirectory, "tesseract", "tessdata"));

        var exeDir = Path.GetDirectoryName(exe);
        if (!string.IsNullOrWhiteSpace(exeDir))
        {
            AddTessDataCandidate(candidates, Path.Combine(exeDir, "tessdata"));
            AddTessDataCandidate(candidates, Path.Combine(exeDir, "..", "tessdata"));
        }

        AddTessDataCandidate(candidates, @"C:\Program Files\Tesseract-OCR\tessdata");
        AddTessDataCandidate(candidates, @"C:\Program Files (x86)\Tesseract-OCR\tessdata");
        AddTessDataCandidate(candidates, Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Tesseract-OCR", "tessdata"));

        return candidates.FirstOrDefault(IsUsableTessDataDirectory);
    }

    private static void AddTessDataCandidate(List<string> candidates, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            var normalized = Path.GetFullPath(path.Trim().Trim('"'));
            candidates.Add(normalized);
            if (!normalized.EndsWith("tessdata", StringComparison.OrdinalIgnoreCase))
                candidates.Add(Path.Combine(normalized, "tessdata"));
        }
        catch (Exception ex)
        {
            MirrorLog.Info($"Ignoring invalid tessdata candidate '{path}': {ex.GetType().Name}");
        }
    }

    private static bool IsUsableTessDataDirectory(string path)
        => Directory.Exists(path) &&
           (File.Exists(Path.Combine(path, "eng.traineddata")) ||
            File.Exists(Path.Combine(path, "ara.traineddata")));
}
