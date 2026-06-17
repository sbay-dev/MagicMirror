using System.Net.Http.Json;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MagicMirror.Native.Mirror;

/// <summary>
/// Translates OCR text into the user's main language. Resilient provider chain:
///   1. <b>Sarmad AI gateway</b> (<c>POST /api/sarmad/ask</c>, Cloudflare Workers AI / the model in
///      <see cref="MirrorSettings.AiModel"/> such as <c>@cf/openai/gpt-oss-20b</c>): the user's own
///      <see cref="MirrorSettings.GatewayBaseUrl"/> first, then the optional
///      <see cref="MirrorSettings.FallbackSarmadUrl"/>.
///   2. <b>Free no-key machine translation</b> (Google <c>gtx</c> → MyMemory), only when
///      <see cref="MirrorSettings.ForceMachineTranslationFallback"/> is set for the current run after
///      explicit user confirmation.
///
/// The AI path stays primary so a correctly-deployed gateway uses the configured Sarmad model; the MT
/// fallback is explicit opt-in because it sends captured text to third-party services.
/// </summary>
public sealed class SarmadTranslationService : ITranslationService
{
    private readonly HttpClient _http;
    private readonly GlossaryMemoryStore _glossary;
    private const int BatchSize = 36;
    private const int ProgressiveBatchSize = 1;
    private const int MaxParallel = 4;
    private const int MtParallel = 6;

    public SarmadTranslationService(HttpClient http, GlossaryMemoryStore glossary)
    {
        _http = http;
        _glossary = glossary;
    }

    public async Task<TranslationBatchResult> TranslateBatchAsync(
        IReadOnlyList<string> sources, string targetLanguage, MirrorSettings settings, CancellationToken ct = default)
    {
        var results = new string[sources.Count];
        for (int i = 0; i < sources.Count; i++) results[i] = sources[i]; // default: original
        var sourceKinds = new TranslationSourceKind[sources.Count];
        var sourceLabels = new string[sources.Count];
        Array.Fill(sourceKinds, TranslationSourceKind.OriginalTextFallback);
        Array.Fill(sourceLabels, TranslationSourceLabel(TranslationSourceKind.OriginalTextFallback));

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
                for (int i = 0; i < sl.Count && i < translated.Lines.Length; i++)
                {
                    if (!string.IsNullOrWhiteSpace(translated.Lines[i]))
                        results[sl.Start + i] = translated.Lines[i];
                    sourceKinds[sl.Start + i] = translated.Source;
                    sourceLabels[sl.Start + i] = TranslationSourceLabel(translated.Source, translated.Reason);
                }
            }
            catch { /* keep originals for this slice */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);

        var source = SummarizeSources(sourceKinds);
        return new TranslationBatchResult(
            results,
            source,
            TranslationSourceSummary(sourceKinds),
            sourceKinds,
            sourceLabels);
    }

    public async IAsyncEnumerable<TranslationBatchProgress> TranslateBatchProgressiveAsync(
        IReadOnlyList<string> sources,
        string targetLanguage,
        MirrorSettings settings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (sources.Count == 0)
            yield break;

        var completed = 0;
        for (var start = 0; start < sources.Count; start += ProgressiveBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(ProgressiveBatchSize, sources.Count - start);
            var slice = new List<string>(count);
            for (var i = 0; i < count; i++)
                slice.Add(sources[start + i]);

            SliceTranslationResult translated;
            try
            {
                translated = await TranslateSliceAsync(slice, targetLanguage, settings, ct);
            }
            catch (Exception ex)
            {
                MirrorLog.Info($"Progressive translation slice failed ({ex.GetType().Name}); keeping originals.");
                translated = new SliceTranslationResult(
                    slice.ToArray(),
                    TranslationSourceKind.OriginalTextFallback,
                    $"Progressive slice failed ({ex.GetType().Name}); kept original text.");
            }

            completed += count;
            yield return new TranslationBatchProgress(
                start,
                translated.Lines,
                translated.Source,
                TranslationSourceLabel(translated.Source, translated.Reason),
                Enumerable.Repeat(translated.Source, translated.Lines.Length).ToArray(),
                Enumerable.Repeat(TranslationSourceLabel(translated.Source, translated.Reason), translated.Lines.Length).ToArray(),
                completed,
                sources.Count);
        }
    }

    public async Task<string> ExplainDictionaryAsync(
        string selectedText, string documentContext, string targetLanguage, MirrorSettings settings, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
            throw new ArgumentException("No selected text for dictionary explanation.", nameof(selectedText));

        var selected = TrimForPrompt(selectedText.Trim(), 480);
        if (string.IsNullOrWhiteSpace(settings.GatewayBaseUrl) && string.IsNullOrWhiteSpace(settings.FallbackSarmadUrl))
            return BuildDictionaryNotConfiguredMessage(selected);

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
                var rules = _glossary.GetRelevantRules(new[] { selected, context }, targetLanguage, maxRules: 8);
                var prompt = BuildDictionaryPrompt(
                    selected,
                    context,
                    targetName,
                    attempt.Compact,
                    _glossary.BuildPromptGuidance(rules));
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

    private static string BuildDictionaryPrompt(
        string selectedText,
        string documentContext,
        string targetName,
        bool compact,
        string glossaryGuidance)
    {
        var context = (documentContext ?? "").Trim();

        var sb = new StringBuilder();
        sb.Append("Target: ").AppendLine(targetName)
          .AppendLine("Analyze the selected term or sentence lexically. Reply only in the target language.")
          .AppendLine("Required: domain/context; at least five alternatives with fit/non-fit notes when a term is selected; final decisive recommendation.")
          .AppendLine("Act as a provenance auditor, not as a mixed translator: bind the term to the provided OCR original, translated sentence, and proof path when present.")
          .AppendLine("Return both a literal glossary reading and a sentence-level glossary reading; if the source is MT/original fallback, mark the path as not Sarmad-verified.")
          .AppendLine("Preserve technical identifiers (LCNS, CNS, CubiCrypt, GPT, QKV); do not invent unsupported meanings.")
          .Append("Selected: ").AppendLine(selectedText);
        if (!string.IsNullOrWhiteSpace(glossaryGuidance))
        {
            sb.AppendLine()
              .AppendLine(glossaryGuidance);
        }
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
            "تمت إعادة المحاولة بطلبات أصغر حتى طلب الكلمة فقط، لكن البوابة لم تقبل الطلب. لا يمكن توليد خمسة بدائل سياقية موثوقة دون نموذج سرمد عامل؛ اضبط GatewayBaseUrl على نشر Cloudflare عامل يستخدم نموذجًا مدعومًا مثل @cf/openai/gpt-oss-20b.";
    }

    private static string BuildDictionaryNotConfiguredMessage(string selectedText)
    {
        return
            "بوابة المعجم غير مضبوطة.\n" +
            $"النص المحدد: {selectedText}\n\n" +
            "تم تعطيل fallback القديم لأنه كان يشير إلى بوابة وثائق تستخدم نموذجًا منتهيًا. للمعجم السياقي اضبط GatewayBaseUrl على نشر MagicMirror/Cloudflare الخاص بك، وتأكد أن النموذج هو @cf/openai/gpt-oss-20b أو نموذج Cloudflare مدعوم.";
    }

    private async Task<SliceTranslationResult> TranslateSliceAsync(
        List<string> slice, string targetLanguage, MirrorSettings settings, CancellationToken ct)
    {
        string targetName = LanguageName(targetLanguage);
        var promptSlice = slice.Select(NormalizeOcrNoise).ToList();
        var rules = _glossary.GetRelevantRules(promptSlice, targetLanguage);
        var prompt = BuildTranslationPrompt(promptSlice, targetName, _glossary.BuildPromptGuidance(rules));

        if (settings.ForceMachineTranslationFallback && settings.AllowMachineTranslationFallback)
        {
            MirrorLog.Info("User explicitly selected MT fallback for this translation run.");
            return new SliceTranslationResult(
                await TranslateSliceViaMtAsync(slice, targetLanguage, settings, ct),
                TranslationSourceKind.MachineTranslationFallback,
                "User explicitly selected MT fallback for the whole capture.");
        }

        string answer;
        try
        {
            answer = await AskAsync(prompt, targetLanguage, settings, ct);
        }
        catch (Exception ex)
        {
            MirrorLog.Info($"AI gateway failed ({ex.GetType().Name}); MT fallback disabled.");
            return new SliceTranslationResult(
                slice.ToArray(),
                TranslationSourceKind.OriginalTextFallback,
                $"Sarmad gateway failed ({ex.GetType().Name}) and MT fallback is disabled.");
        }
        var parsedLines = CountNumberedTranslations(answer, slice.Count);
        var viaAi = ParseNumbered(answer, slice.Count, slice);
        for (int i = 0; i < viaAi.Length; i++)
            viaAi[i] = ApplyLearnedAndBuiltInPostEdits(promptSlice[i], viaAi[i], targetLanguage);

        if (LooksLikeEnglishInsteadOfArabic(targetLanguage, viaAi))
        {
            MirrorLog.Info("AI translation appears to be English/original while target is Arabic; waiting for explicit MT approval.");
            return new SliceTranslationResult(
                slice.ToArray(),
                TranslationSourceKind.OriginalTextFallback,
                "Sarmad response looked like English/original text while target is Arabic; explicit MT approval is required.");
        }

        if (parsedLines > 0)
            return new SliceTranslationResult(
                viaAi,
                TranslationSourceKind.SarmadGateway,
                "Sarmad AI returned a numbered, context-aware aligned translation.");

        MirrorLog.Info("AI translation response was not a numbered aligned list; keeping AI output until explicit MT approval.");
        return new SliceTranslationResult(
            viaAi,
            TranslationSourceKind.SarmadGateway,
            "Sarmad AI returned unnumbered output; explicit MT approval is required before any fallback switch.");
    }

    private static TranslationSourceKind SummarizeSources(IReadOnlyList<TranslationSourceKind> sources)
    {
        var distinct = sources.Distinct().ToList();
        return distinct.Count == 1 ? distinct[0] : TranslationSourceKind.Mixed;
    }

    private static string TranslationSourceLabel(TranslationSourceKind source, string reason = "") => source switch
    {
        TranslationSourceKind.SarmadGateway => AppendReason("Sarmad AI", reason),
        TranslationSourceKind.MachineTranslationFallback => AppendReason("MT fallback (non-academic)", reason),
        TranslationSourceKind.Mixed => AppendReason("mixed sources", reason),
        _ => AppendReason("original fallback", reason),
    };

    private static string AppendReason(string label, string reason)
        => string.IsNullOrWhiteSpace(reason) ? label : $"{label}: {reason}";

    private static string TranslationSourceSummary(IReadOnlyList<TranslationSourceKind> sources)
    {
        var counts = sources
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{ShortSourceLabel(g.Key)}={g.Count()}")
            .ToList();
        var source = SummarizeSources(sources);
        return source == TranslationSourceKind.Mixed
            ? "mixed sources: " + string.Join(", ", counts)
            : TranslationSourceLabel(source);
    }

    private static string ShortSourceLabel(TranslationSourceKind source) => source switch
    {
        TranslationSourceKind.SarmadGateway => "Sarmad",
        TranslationSourceKind.MachineTranslationFallback => "MT",
        TranslationSourceKind.OriginalTextFallback => "Original",
        _ => "Mixed",
    };

    private static string BuildTranslationPrompt(List<string> slice, string targetName, string glossaryGuidance)
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
          .AppendLine("If user-approved glossary memory is provided below, treat it as the highest-priority terminology policy when the context matches.")
          .AppendLine("OCR handling: the input may contain recognition noise. Correct obvious OCR confusions only when context is unambiguous (for example: fonnal->formal, The01y->Theory, govemed->governed, mixmg->mixing, stmctured->structured, Ca1l->can, IIO->no). If uncertain, preserve the source token.")
          .AppendLine("Line alignment: output exactly one translated line for each input line, in the same order, with the same line number. Do not merge lines, split lines, summarize, explain, or add notes.")
          .AppendLine("Output ONLY the numbered translations, one per line, each prefixed with its number, a period, and a space (e.g. \"1. ...\").")
          .AppendLine();
        if (!string.IsNullOrWhiteSpace(glossaryGuidance))
        {
            sb.AppendLine(glossaryGuidance)
              .AppendLine();
        }

        for (int i = 0; i < slice.Count; i++)
            sb.Append(i + 1).Append(". ").AppendLine(slice[i].Replace("\r", " ").Replace("\n", " "));

        return sb.ToString();
    }

    // ── Free no-key MT fallback (Google gtx → MyMemory), per line, bounded parallelism ──
    private async Task<string[]> TranslateSliceViaMtAsync(
        List<string> slice, string targetLanguage, MirrorSettings settings, CancellationToken ct)
    {
        var outArr = new string[slice.Count];
        for (int i = 0; i < slice.Count; i++) outArr[i] = slice[i];

        using var gate = new SemaphoreSlim(MtParallel);
        var tasks = Enumerable.Range(0, slice.Count).Select(async i =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var t = await TranslateLineMtAsync(slice[i], targetLanguage, settings, ct);
                if (!string.IsNullOrWhiteSpace(t)) outArr[i] = t;
            }
            catch { /* keep original */ }
            finally { gate.Release(); }
        });
        await Task.WhenAll(tasks);
        return outArr;
    }

    private async Task<string?> TranslateLineMtAsync(
        string text, string targetLanguage, MirrorSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var source = NormalizeOcrNoise(text);
        string tl = (targetLanguage ?? "ar").Split('-')[0];
        string sl = ResolveMtSourceLanguage(source, settings.SourceLanguageHint, tl);
        var translated = await TranslateGoogleGtxAsync(source, sl, tl, ct) ?? await TranslateMyMemoryAsync(source, sl, tl, ct);
        return IsProviderErrorText(translated)
            ? null
            : ApplyLearnedAndBuiltInPostEdits(source, translated!, targetLanguage ?? "ar");
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

    private string ApplyLearnedAndBuiltInPostEdits(string source, string translated, string targetLanguage)
    {
        var learned = _glossary.ApplyPostEdits(source, translated, targetLanguage);
        return ApplyTerminologyPostEdits(source, learned, targetLanguage);
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

    private static string ResolveMtSourceLanguage(string text, string sourceLanguageHint, string targetLanguage)
    {
        var hint = (sourceLanguageHint ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(hint) &&
            !string.Equals(hint, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return hint.Split('|')[0].Trim();
        }

        var arabic = 0;
        var latin = 0;
        foreach (var ch in text)
        {
            if (ch is >= '\u0600' and <= '\u06FF')
                arabic++;
            else if ((ch is >= 'A' and <= 'Z') || (ch is >= 'a' and <= 'z'))
                latin++;
        }

        if (latin > arabic)
            return "en";
        if (arabic > latin)
            return "ar";
        return string.Equals(targetLanguage, "en", StringComparison.OrdinalIgnoreCase) ? "ar" : "en";
    }

    private static bool IsProviderErrorText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return true;

        var upper = text.Trim().ToUpperInvariant();
        return upper.Contains("INVALID SOURCE LANGUAGE", StringComparison.Ordinal) ||
               upper.Contains("LANGPAIR=", StringComparison.Ordinal) ||
               upper.Contains("NO CONTENT", StringComparison.Ordinal) ||
               upper.Contains("QUERY PARAM", StringComparison.Ordinal) ||
               upper.Contains("MYMEMORY WARNING", StringComparison.Ordinal);
    }

    private static bool LooksLikeEnglishInsteadOfArabic(string targetLanguage, IReadOnlyList<string> lines)
    {
        if (!IsArabic(targetLanguage))
            return false;

        var text = string.Join(" ", lines);
        var arabic = 0;
        var latin = 0;
        foreach (var ch in text)
        {
            if (ch is >= '\u0600' and <= '\u06FF')
                arabic++;
            else if ((ch is >= 'A' and <= 'Z') || (ch is >= 'a' and <= 'z'))
                latin++;
        }

        return latin >= 12 && arabic < 3;
    }

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
    private async Task<string?> TranslateGoogleGtxAsync(string text, string sl, string tl, CancellationToken ct)
    {
        try
        {
            var googleSource = string.IsNullOrWhiteSpace(sl) ? "auto" : sl;
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl="
                + Uri.EscapeDataString(googleSource) + "&tl=" + Uri.EscapeDataString(tl)
                + "&dt=t&q=" + Uri.EscapeDataString(text);
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
            return IsProviderErrorText(outText) ? null : outText;
        }
        catch { return null; }
    }

    /// <summary>MyMemory free endpoint (no key): {responseData:{translatedText}}.</summary>
    private async Task<string?> TranslateMyMemoryAsync(string text, string sl, string tl, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sl) ||
                string.Equals(sl, "auto", StringComparison.OrdinalIgnoreCase))
            {
                sl = "en";
            }

            var url = "https://api.mymemory.translated.net/get?langpair="
                + Uri.EscapeDataString(sl + "|" + tl) + "&q=" + Uri.EscapeDataString(text);
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("responseData", out var rd) &&
                rd.TryGetProperty("translatedText", out var tt) && tt.ValueKind == JsonValueKind.String)
            {
                var outText = (tt.GetString() ?? "").Trim();
                return IsProviderErrorText(outText) ? null : outText;
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

    private static int CountNumberedTranslations(string answer, int count)
    {
        if (string.IsNullOrWhiteSpace(answer)) return 0;

        var seen = new HashSet<int>();
        foreach (var rawLine in answer.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            var m = System.Text.RegularExpressions.Regex.Match(line, @"^(\d+)[\.\)\:\-\t]\s*(.+)$");
            if (!m.Success) continue;
            if (int.TryParse(m.Groups[1].Value, out int idx) && idx >= 1 && idx <= count)
                seen.Add(idx);
        }

        return seen.Count;
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
        [JsonPropertyName("model")] public string Model { get; set; } = "@cf/openai/gpt-oss-20b";
    }

    private static class SarmadJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
    }

    private sealed record SliceTranslationResult(string[] Lines, TranslationSourceKind Source, string Reason);
}
