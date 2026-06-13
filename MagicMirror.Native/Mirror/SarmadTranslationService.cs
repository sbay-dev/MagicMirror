using System.Net.Http.Json;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagicMirror.Native.Mirror;

/// <summary>
/// Translates OCR text into the user's main language. Resilient provider chain:
///   1. <b>Sarmad AI gateway</b> (<c>POST /api/sarmad/ask</c>, Cloudflare Workers AI / the model in
///      <see cref="MirrorSettings.AiModel"/> such as <c>@cf/openai/gpt-oss-120b</c>): the user's own
///      <see cref="MirrorSettings.GatewayBaseUrl"/> first, then the canonical
///      <see cref="MirrorSettings.FallbackSarmadUrl"/>.
///   2. <b>Free no-key machine translation</b> (Google <c>gtx</c> → MyMemory) when the AI gateway
///      is unreachable or returns an error — so the mirror keeps translating even if the mesh is
///      down (e.g. a deprecated upstream model).
///
/// The AI path stays primary so a correctly-deployed gateway uses gpt-oss-120b as designed; the MT
/// fallback guarantees the feature works regardless of mesh availability.
/// </summary>
public sealed class SarmadTranslationService : ITranslationService
{
    private readonly HttpClient _http;
    private const int BatchSize = 36;
    private const int MaxParallel = 4;
    private const int MtParallel = 6;

    public SarmadTranslationService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
    }

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(
        IReadOnlyList<string> sources, string targetLanguage, MirrorSettings settings, CancellationToken ct = default)
    {
        var results = new string[sources.Count];
        for (int i = 0; i < sources.Count; i++) results[i] = sources[i]; // default: original

        var slices = new List<(int Start, int Count)>();
        for (int start = 0; start < sources.Count; start += BatchSize)
            slices.Add((start, Math.Min(BatchSize, sources.Count - start)));

        using var gate = new SemaphoreSlim(MaxParallel);
        var tasks = slices.Select(async sl =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var slice = new List<string>(sl.Count);
                for (int i = 0; i < sl.Count; i++) slice.Add(sources[sl.Start + i]);
                var translated = await TranslateSliceAsync(slice, targetLanguage, settings, ct);
                for (int i = 0; i < sl.Count && i < translated.Length; i++)
                    if (!string.IsNullOrWhiteSpace(translated[i]))
                        results[sl.Start + i] = translated[i];
            }
            catch { /* keep originals for this slice */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);

        return results;
    }

    public async Task<string> ExplainDictionaryAsync(
        string selectedText, string documentContext, string targetLanguage, MirrorSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
            throw new ArgumentException("No selected text for dictionary explanation.", nameof(selectedText));

        var selected = TrimForPrompt(selectedText.Trim(), 240);
        var targetName = LanguageName(targetLanguage);

        Exception? last = null;
        foreach (var attempt in new[]
        {
            (ContextChars: 900, Compact: false),
            (ContextChars: 360, Compact: true),
            (ContextChars: 0, Compact: true),
        })
        {
            try
            {
                var context = attempt.ContextChars <= 0
                    ? ""
                    : CompactDictionaryContext(documentContext, attempt.ContextChars);
                var prompt = BuildDictionaryPrompt(selected, context, targetName, attempt.Compact);
                return await AskAsync(prompt, targetLanguage, settings, ct,
                    contextOverride: "Magic Mirror dictionary lookup. Keep payload small; use selected term plus nearby context only.");
            }
            catch (Exception ex)
            {
                last = ex;
                if (ex is HttpRequestException http && http.StatusCode == HttpStatusCode.RequestEntityTooLarge)
                    MirrorLog.Info("Dictionary prompt too large (413); retrying with smaller context.");
                else
                    MirrorLog.Info($"Dictionary gateway attempt failed ({ex.GetType().Name}); retrying compact request.");
            }
        }

        return BuildDictionaryUnavailableMessage(selected, last ?? new InvalidOperationException("Dictionary gateway rejected all attempts."));
    }

    private static string BuildDictionaryPrompt(string selectedText, string documentContext, string targetName, bool compact)
    {
        var context = (documentContext ?? "").Trim();

        var sb = new StringBuilder();
        sb.Append("Target: ").AppendLine(targetName)
          .AppendLine("Analyze the selected term lexically. Reply only in the target language.")
          .AppendLine("Required: domain/context; at least five alternatives with fit/non-fit notes; final decisive recommendation.")
          .AppendLine("Preserve technical identifiers (LCNS, CNS, CubiCrypt, GPT, QKV); do not invent unsupported meanings.")
          .Append("Selected: ").AppendLine(selectedText);
        if (context.Length > 0)
        {
            sb.AppendLine("Nearby context:")
              .AppendLine(context);
        }
        if (compact)
            sb.AppendLine("Be concise; this payload was minimized for gateway limits.");
        return sb.ToString();
    }

    private static string CompactDictionaryContext(string documentContext, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(documentContext))
            return "";

        var lines = documentContext
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => TrimForPrompt(line, 160))
            .Where(line => line.Length > 0)
            .ToList();

        var sb = new StringBuilder();
        foreach (var line in lines)
        {
            if (sb.Length + line.Length + 1 > maxChars)
                break;
            sb.AppendLine(line);
        }

        return sb.ToString().Trim();
    }

    private static string TrimForPrompt(string text, int maxChars)
    {
        var normalized = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        if (normalized.Length <= maxChars)
            return normalized;
        return normalized.Substring(0, Math.Max(0, maxChars - 1)).TrimEnd() + "…";
    }

    private static string BuildDictionaryUnavailableMessage(string selectedText, Exception ex)
    {
        var reason = ex is HttpRequestException http && http.StatusCode.HasValue
            ? $"HTTP {(int)http.StatusCode.Value} {http.StatusCode.Value}"
            : ex.GetType().Name;

        return
            "بوابة المعجم غير متاحة الآن.\n" +
            $"السبب التقني: {reason}\n" +
            $"النص المحدد: {selectedText}\n\n" +
            "تمت إعادة المحاولة بطلبات أصغر حتى طلب الكلمة فقط، لكن البوابة لم تقبل الطلب. لا يمكن توليد خمسة بدائل سياقية موثوقة دون gpt-oss-120b؛ اضبط GatewayBaseUrl على نشر Cloudflare عامل أو أعد المحاولة عند عودة البوابة.";
    }

    private async Task<string[]> TranslateSliceAsync(
        List<string> slice, string targetLanguage, MirrorSettings settings, CancellationToken ct)
    {
        string targetName = LanguageName(targetLanguage);
        var promptSlice = slice.Select(NormalizeOcrNoise).ToList();
        var prompt = BuildTranslationPrompt(promptSlice, targetName);

        string answer;
        try
        {
            answer = await AskAsync(prompt, targetLanguage, settings, ct);
        }
        catch (Exception ex)
        {
            MirrorLog.Info($"AI gateway failed ({ex.GetType().Name}) — using MT fallback");
            return await TranslateSliceViaMtAsync(slice, targetLanguage, ct);
        }
        var viaAi = ParseNumbered(answer, slice.Count, slice);
        for (int i = 0; i < viaAi.Length; i++)
            viaAi[i] = ApplyTerminologyPostEdits(promptSlice[i], viaAi[i], targetLanguage);

        // If the AI gateway left most lines unchanged (mesh down / parsing miss), use free MT.
        int changed = 0;
        for (int i = 0; i < slice.Count; i++)
            if (!string.Equals(viaAi[i], slice[i], StringComparison.Ordinal)) changed++;
        if (changed >= Math.Max(1, slice.Count / 2)) return viaAi;

        MirrorLog.Info($"AI translation weak ({changed}/{slice.Count} changed) — using MT fallback");
        return await TranslateSliceViaMtAsync(slice, targetLanguage, ct);
    }

    private static string BuildTranslationPrompt(List<string> slice, string targetName)
    {
        var sb = new StringBuilder();
        sb.Append("You are the Magic Mirror domain-aware translation engine. Translate each numbered line into ")
          .Append(targetName)
          .AppendLine(" with maximum fidelity, terminology control, and publication-quality style.")
          .AppendLine()
          .AppendLine("Before translating, silently classify the text domain: scientific/academic, religious/sacred, legal, medical, UI, literary, or general. Do not print the classification; only use it to choose the correct register.")
          .AppendLine("Scientific/academic texts: use higher-education formal terminology, preserve conceptual precision, render theory/framework/operator/model terms consistently, and avoid colloquial wording.")
          .AppendLine("Religious/sacred texts: translate faithfully and reverently; preserve doctrinal and legal meaning; do not add interpretation or sectarian wording; preserve proper names, references, and formulaic terms. If the source is scripture or hadith, render it as a translation of meaning, not as a replacement for the original.")
          .AppendLine("Legal, medical, or safety-critical texts: preserve exact meaning, scope, negation, quantities, conditions, and uncertainty. Do not simplify away technical risk.")
          .AppendLine("Terminology: preserve acronyms, model names, project names, equations, identifiers, citations, and proper nouns unless the source explicitly expands them. Examples: LCNS, CNS, CubiCrypt, Cubic Neural Statistics, GPT, QKV remain as terms; do not translate CNS as central nervous system unless the source is explicitly anatomical/medical.")
          .AppendLine("OCR handling: the input may contain recognition noise. Correct obvious OCR confusions only when context is unambiguous (for example: fonnal->formal, The01y->Theory, govemed->governed, mixmg->mixing, stmctured->structured, Ca1l->can, IIO->no). If uncertain, preserve the source token.")
          .AppendLine("Line alignment: output exactly one translated line for each input line, in the same order, with the same line number. Do not merge lines, split lines, summarize, explain, or add notes.")
          .AppendLine("Output ONLY the numbered translations, one per line, each prefixed with its number, a period, and a space (e.g. \"1. ...\").")
          .AppendLine();

        for (int i = 0; i < slice.Count; i++)
            sb.Append(i + 1).Append(". ").AppendLine(slice[i].Replace("\r", " ").Replace("\n", " "));

        return sb.ToString();
    }

    // ── Free no-key MT fallback (Google gtx → MyMemory), per line, bounded parallelism ──
    private async Task<string[]> TranslateSliceViaMtAsync(List<string> slice, string targetLanguage, CancellationToken ct)
    {
        var outArr = new string[slice.Count];
        for (int i = 0; i < slice.Count; i++) outArr[i] = slice[i];

        using var gate = new SemaphoreSlim(MtParallel);
        var tasks = Enumerable.Range(0, slice.Count).Select(async i =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var t = await TranslateLineMtAsync(slice[i], targetLanguage, ct);
                if (!string.IsNullOrWhiteSpace(t)) outArr[i] = t;
            }
            catch { /* keep original */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return outArr;
    }

    private async Task<string?> TranslateLineMtAsync(string text, string targetLanguage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var source = NormalizeOcrNoise(text);
        string tl = (targetLanguage ?? "ar").Split('-')[0];
        var translated = await TranslateGoogleGtxAsync(source, tl, ct) ?? await TranslateMyMemoryAsync(source, tl, ct);
        return translated == null ? null : ApplyTerminologyPostEdits(source, translated, targetLanguage ?? "ar");
    }

    private static string NormalizeOcrNoise(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        var s = text;
        foreach (var (pattern, replacement) in OcrCorrections)
            s = System.Text.RegularExpressions.Regex.Replace(
                s, pattern, replacement,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return s;
    }

    private static string ApplyTerminologyPostEdits(string source, string translated, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(translated)) return translated;
        if (!IsArabic(targetLanguage)) return translated;

        var t = translated;
        if (ContainsToken(source, "CNS"))
        {
            t = t.Replace("الجهاز العصبي المركزي", "CNS", StringComparison.OrdinalIgnoreCase)
                 .Replace("نظام إدارة CNS", "CNS", StringComparison.OrdinalIgnoreCase)
                 .Replace("إدارة CNS", "CNS", StringComparison.OrdinalIgnoreCase)
                 .Replace("المركز العصبي", "CNS", StringComparison.OrdinalIgnoreCase);
        }
        if (ContainsToken(source, "LCNS"))
            t = t.Replace("إل سي إن إس", "LCNS", StringComparison.OrdinalIgnoreCase)
                 .Replace("ال سي ان اس", "LCNS", StringComparison.OrdinalIgnoreCase);
        if (ContainsToken(source, "CubiCrypt"))
            t = t.Replace("كوبيكريبت", "CubiCrypt", StringComparison.OrdinalIgnoreCase)
                 .Replace("كيوبكريبت", "CubiCrypt", StringComparison.OrdinalIgnoreCase);
        if (ContainsToken(source, "Cubic Neural Statistics"))
            t = t.Replace("إحصاءات عصبية مكعبة", "Cubic Neural Statistics", StringComparison.OrdinalIgnoreCase)
                 .Replace("الإحصاءات العصبية المكعبة", "Cubic Neural Statistics", StringComparison.OrdinalIgnoreCase);
        return t;
    }

    private static bool ContainsToken(string source, string token) =>
        source.Contains(token, StringComparison.OrdinalIgnoreCase);

    private static bool IsArabic(string targetLanguage) =>
        (targetLanguage ?? "").StartsWith("ar", StringComparison.OrdinalIgnoreCase);

    private static readonly (string Pattern, string Replacement)[] OcrCorrections =
    {
        (@"\bfonnal\b", "formal"),
        (@"\bfommlates\b", "formulates"),
        (@"\bThe01y\b", "Theory"),
        (@"\bgovemed\b", "governed"),
        (@"\bmixmg\b", "mixing"),
        (@"\bstmctured\b", "structured"),
        (@"\bCubiC1ypt\b", "CubiCrypt"),
        (@"\bCa1l\b", "can"),
        (@"\baS\b", "as"),
        (@"\bpanwise\b", "pairwise"),
        (@"[,،]\s*pace\b", " space"),
        (@"[,،]\s*oordinates\b", " coordinates"),
        (@"[,،]\s*isplacement\b", " displacement"),
        (@"[,،]\s*interactions\b", " interactions"),
        (@"[,،]\s*hannels\b", " channels"),
        (@"[,،]\s*eatures\b", " features"),
        (@"\.\s*ignals\b", " signals"),
        (@"\bquew-key-value\b", "query-key-value"),
        (@"[,،]\s*rojections\b", " projections"),
        (@"[,،]\s*ttention\b", " attention"),
        (@"\.\s*elf-attention\b", " self-attention"),
        (@"\btheow\b", "theory"),
        (@"\bperfonnance\b", "performance"),
        (@"[,،]\s*eorem\b", " theorem"),
        (@"\bdetemmine\b", "determine"),
        (@"\bgoveming\b", "governing"),
        (@"\.\s*bias\b", " bias"),
        (@"\bIIO\b", "no"),
    };

    /// <summary>Google's public <c>gtx</c> endpoint (no key). Response: nested array; root[0][n][0] = segment.</summary>
    private async Task<string?> TranslateGoogleGtxAsync(string text, string tl, CancellationToken ct)
    {
        try
        {
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl="
                + Uri.EscapeDataString(tl) + "&dt=t&q=" + Uri.EscapeDataString(text);
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() == 0) return null;
            var seg = root[0];
            if (seg.ValueKind != JsonValueKind.Array) return null;
            var sb = new StringBuilder();
            foreach (var s in seg.EnumerateArray())
                if (s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0 && s[0].ValueKind == JsonValueKind.String)
                    sb.Append(s[0].GetString());
            var outText = sb.ToString().Trim();
            return outText.Length > 0 ? outText : null;
        }
        catch { return null; }
    }

    /// <summary>MyMemory free endpoint (no key): {responseData:{translatedText}}.</summary>
    private async Task<string?> TranslateMyMemoryAsync(string text, string tl, CancellationToken ct)
    {
        try
        {
            var url = "https://api.mymemory.translated.net/get?langpair="
                + Uri.EscapeDataString("auto|" + tl) + "&q=" + Uri.EscapeDataString(text);
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("responseData", out var rd) &&
                rd.TryGetProperty("translatedText", out var tt) && tt.ValueKind == JsonValueKind.String)
            {
                var outText = (tt.GetString() ?? "").Trim();
                return outText.Length > 0 ? outText : null;
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>Sends one prompt to the mesh, trying the primary gateway then the fallback.</summary>
    private async Task<string> AskAsync(
        string prompt,
        string targetLanguage,
        MirrorSettings settings,
        CancellationToken ct,
        string? contextOverride = null)
    {
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.GatewayBaseUrl))
            urls.Add(settings.GatewayBaseUrl.TrimEnd('/') + "/api/sarmad/ask");
        if (!string.IsNullOrWhiteSpace(settings.FallbackSarmadUrl))
            urls.Add(settings.FallbackSarmadUrl);

        var payload = new SarmadRequest
        {
            Mode = "docs-assistant",
            Surface = "magic-mirror",
            Prompt = prompt,
            Context = contextOverride ??
                "On-screen text captured by the Magic Mirror overlay. Follow the domain-aware translation policy in the prompt: classify the domain silently, use higher-education scientific Arabic for academic texts, use faithful reverent translation for religious texts, preserve acronyms/terms, and repair only unambiguous OCR noise.",
            Language = targetLanguage,
            Model = settings.AiModel,
        };

        Exception? last = null;
        foreach (var url in urls)
        {
            try
            {
                using var resp = await _http.PostAsJsonAsync(url, payload, SarmadJson.Options, ct);
                resp.EnsureSuccessStatusCode();
                var body = await resp.Content.ReadAsStringAsync(ct);
                return ExtractAnswer(body);
            }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new InvalidOperationException("No translation endpoint configured.");
    }

    /// <summary>Reads the <c>answer</c> field from a Sarmad JSON response; tolerates plain-text bodies.</summary>
    private static string ExtractAnswer(string body)
    {
        body = body.Trim();
        if (body.StartsWith("{"))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                // Explicit upstream error (e.g. deprecated model) → treat as failure to trigger fallback.
                if (doc.RootElement.TryGetProperty("error", out var err))
                    throw new InvalidOperationException("Sarmad error: " +
                        (doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : err.ToString()));
                if (doc.RootElement.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String)
                    return a.GetString() ?? "";
                // Some deployments wrap the answer in {result:{answer}} or {content}.
                if (doc.RootElement.TryGetProperty("result", out var r) &&
                    r.TryGetProperty("answer", out var ra) && ra.ValueKind == JsonValueKind.String)
                    return ra.GetString() ?? "";
                if (doc.RootElement.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    return c.GetString() ?? "";
            }
            catch (InvalidOperationException) { throw; }
            catch { /* fall through to raw */ }
        }
        return body;
    }

    /// <summary>
    /// Parses a numbered translation list back into an array aligned with the inputs.
    /// Falls back to the original line when a number is missing.
    /// </summary>
    private static string[] ParseNumbered(string answer, int count, List<string> originals)
    {
        var outArr = new string[count];
        for (int i = 0; i < count; i++) outArr[i] = originals[i];
        if (string.IsNullOrWhiteSpace(answer)) return outArr;

        foreach (var rawLine in answer.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\d+)[\.\)\:\-\t]\s*(.+)$");
            if (!m.Success) continue;
            if (int.TryParse(m.Groups[1].Value, out int idx) && idx >= 1 && idx <= count)
                outArr[idx - 1] = m.Groups[2].Value.Trim();
        }
        return outArr;
    }

    private static string LanguageName(string code) => (code ?? "").ToLowerInvariant() switch
    {
        "ar" or "ar-sa" => "Arabic",
        "en" or "en-us" => "English",
        "fr" => "French",
        "es" => "Spanish",
        "de" => "German",
        "tr" => "Turkish",
        "ur" => "Urdu",
        "fa" => "Persian",
        "zh" => "Chinese",
        "ja" => "Japanese",
        "ru" => "Russian",
        _ => code ?? "the target language",
    };

    private sealed class SarmadRequest
    {
        [JsonPropertyName("mode")] public string Mode { get; set; } = "docs-assistant";
        [JsonPropertyName("surface")] public string Surface { get; set; } = "magic-mirror";
        [JsonPropertyName("prompt")] public string Prompt { get; set; } = "";
        [JsonPropertyName("context")] public string Context { get; set; } = "";
        [JsonPropertyName("language")] public string Language { get; set; } = "ar";
        [JsonPropertyName("model")] public string Model { get; set; } = "@cf/openai/gpt-oss-120b";
    }

    private static class SarmadJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }
}
