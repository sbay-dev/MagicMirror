using System.Text.Json;
using System.Text.RegularExpressions;

namespace MagicMirror.Native.Mirror;

/// <summary>
/// Persists user-approved dictionary choices as local glossary rules.
/// These rules are not remote model fine-tuning; they are a compact, auditable
/// "learning layer" injected into future prompts and applied as safe post-edits.
/// </summary>
public sealed class GlossaryMemoryStore
{
    private const int MaxRules = 400;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<GlossaryMemoryRule> _rules;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public GlossaryMemoryStore()
    {
        _path = Path.Combine(FileSystem.AppDataDirectory, "magicmirror-glossary-memory.json");
        _rules = Load();
    }

    public IReadOnlyList<GlossaryMemoryRule> Rules
    {
        get
        {
            lock (_gate)
                return _rules.Select(rule => rule.Clone()).ToList();
        }
    }

    /// <summary>
    /// Stores a user's accepted dictionary proposal.
    /// Parameters:
    /// - <paramref name="sourceText"/>: source/OCR line that produced the displayed translation.
    /// - <paramref name="selectedText"/>: current displayed word/phrase/sentence selected by the user.
    /// - <paramref name="replacementText"/>: dictionary proposal approved by the user.
    /// - <paramref name="targetLanguage"/>: target language for scoping the rule.
    /// - <paramref name="context"/>: nearby document context, trimmed for future prompt grounding.
    /// Protocol: rules are merged by target language + selected/replacement/source fingerprint,
    /// then usage count and timestamps are updated. The newest/frequently used rules are kept.
    /// </summary>
    public GlossaryMemoryRule RememberSelection(
        string sourceText,
        string selectedText,
        string replacementText,
        string targetLanguage,
        string context)
    {
        var source = CleanLine(sourceText);
        var selected = CleanLine(selectedText);
        var replacement = CleanLine(replacementText);
        if (selected.Length == 0)
            throw new ArgumentException("Selected glossary text is required.", nameof(selectedText));
        if (replacement.Length == 0)
            throw new ArgumentException("Replacement glossary text is required.", nameof(replacementText));

        var language = NormalizeLanguage(targetLanguage);
        var now = DateTimeOffset.UtcNow;
        GlossaryMemoryRule saved;
        List<GlossaryMemoryRule> snapshot;
        lock (_gate)
        {
            var existing = _rules.FirstOrDefault(rule =>
                string.Equals(rule.TargetLanguage, language, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeSearch(rule.SelectedText), NormalizeSearch(selected), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeSearch(rule.ReplacementText), NormalizeSearch(replacement), StringComparison.OrdinalIgnoreCase) &&
                SameSourceFingerprint(rule.SourceText, source));

            if (existing == null)
            {
                existing = new GlossaryMemoryRule
                {
                    Id = Guid.NewGuid().ToString("N"),
                    TargetLanguage = language,
                    SourceText = source,
                    SelectedText = selected,
                    ReplacementText = replacement,
                    Context = TrimForStorage(context, 900),
                    CreatedAt = now,
                    UpdatedAt = now,
                    UseCount = 1,
                };
                _rules.Add(existing);
            }
            else
            {
                existing.SourceText = PreferLonger(existing.SourceText, source, 260);
                existing.Context = PreferLonger(existing.Context, context, 900);
                existing.UseCount++;
                existing.UpdatedAt = now;
            }

            TrimRuleList();
            saved = existing.Clone();
            snapshot = _rules.Select(rule => rule.Clone()).ToList();
        }

        SaveSnapshot(snapshot);
        return saved;
    }

    public IReadOnlyList<GlossaryMemoryRule> GetRelevantRules(
        IEnumerable<string> contexts,
        string targetLanguage,
        int maxRules = 10)
    {
        var language = NormalizeLanguage(targetLanguage);
        var haystack = NormalizeSearch(string.Join(" ", contexts.Select(CleanLine)));
        lock (_gate)
        {
            return _rules
                .Where(rule => string.Equals(rule.TargetLanguage, language, StringComparison.OrdinalIgnoreCase))
                .Select(rule => (Rule: rule, Score: ScoreRule(rule, haystack)))
                .Where(item => item.Score > 0)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Rule.UseCount)
                .ThenByDescending(item => item.Rule.UpdatedAt)
                .Take(Math.Clamp(maxRules, 1, 24))
                .Select(item => item.Rule.Clone())
                .ToList();
        }
    }

    public string BuildPromptGuidance(IReadOnlyList<GlossaryMemoryRule> rules)
    {
        if (rules.Count == 0)
            return "";

        var lines = rules.Select((rule, index) =>
        {
            var source = TrimForStorage(rule.SourceText, 120);
            var context = TrimForStorage(rule.Context, 160);
            var when = source.Length > 0 ? $" عندما يشبه المصدر: \"{source}\"" : "";
            var nearby = context.Length > 0 ? $" | سياق محفوظ: \"{context}\"" : "";
            return $"{index + 1}. استبدل/فضّل \"{rule.ReplacementText}\" بدل \"{rule.SelectedText}\"{when}.{nearby}";
        });

        return "User-approved glossary memory (highest priority; apply only when context matches):\n" +
               string.Join('\n', lines);
    }

    public string ApplyPostEdits(string sourceText, string translatedText, string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(translatedText))
            return translatedText;

        var rules = GetRelevantRules(new[] { sourceText, translatedText }, targetLanguage, maxRules: 12);
        var output = translatedText;
        foreach (var rule in rules)
        {
            if (string.Equals(rule.SelectedText, rule.ReplacementText, StringComparison.Ordinal))
                continue;
            if (!SourceMatches(rule, sourceText) && !ContainsNormalized(output, rule.SelectedText))
                continue;

            output = ReplaceTerm(output, rule.SelectedText, rule.ReplacementText);
        }

        return output;
    }

    private List<GlossaryMemoryRule> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new List<GlossaryMemoryRule>();

            var state = JsonSerializer.Deserialize<GlossaryMemoryState>(File.ReadAllText(_path), Json);
            return state?.Rules?
                .Where(rule => !string.IsNullOrWhiteSpace(rule.ReplacementText))
                .Take(MaxRules)
                .ToList() ?? new List<GlossaryMemoryRule>();
        }
        catch (Exception ex)
        {
            MirrorLog.Error("Unable to load glossary memory; starting with an empty memory.", ex);
            return new List<GlossaryMemoryRule>();
        }
    }

    private void SaveSnapshot(IReadOnlyList<GlossaryMemoryRule> rules)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(new GlossaryMemoryState { Rules = rules.ToList() }, Json));
        }
        catch (Exception ex)
        {
            MirrorLog.Error("Unable to save glossary memory.", ex);
        }
    }

    private void TrimRuleList()
    {
        if (_rules.Count <= MaxRules)
            return;

        var keep = _rules
            .OrderByDescending(rule => rule.UseCount)
            .ThenByDescending(rule => rule.UpdatedAt)
            .Take(MaxRules)
            .ToHashSet();
        _rules.RemoveAll(rule => !keep.Contains(rule));
    }

    private static int ScoreRule(GlossaryMemoryRule rule, string haystack)
    {
        if (haystack.Length == 0)
            return 0;

        var score = 0;
        if (ContainsNormalized(haystack, rule.SourceText)) score += 12;
        if (ContainsNormalized(haystack, rule.SelectedText)) score += 8;
        if (ContainsNormalized(haystack, rule.ReplacementText)) score += 5;
        score += SourceTokenOverlap(rule.SourceText, haystack);
        return score;
    }

    private static bool SourceMatches(GlossaryMemoryRule rule, string sourceText)
    {
        var source = NormalizeSearch(sourceText);
        if (source.Length == 0)
            return false;
        if (ContainsNormalized(source, rule.SourceText))
            return true;
        return SourceTokenOverlap(rule.SourceText, source) >= 3;
    }

    private static int SourceTokenOverlap(string sourceText, string haystack)
    {
        var tokens = NormalizeSearch(sourceText)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12);
        return tokens.Count(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameSourceFingerprint(string a, string b)
    {
        var left = Fingerprint(a);
        var right = Fingerprint(b);
        return left.Length > 0 && right.Length > 0 && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    private static string Fingerprint(string text)
        => string.Join(" ", NormalizeSearch(text)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3)
            .Take(8));

    private static bool ContainsNormalized(string haystack, string needle)
    {
        var normalizedNeedle = NormalizeSearch(needle);
        if (normalizedNeedle.Length == 0)
            return false;
        return NormalizeSearch(haystack).Contains(normalizedNeedle, StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplaceTerm(string text, string selected, string replacement)
    {
        if (string.IsNullOrWhiteSpace(selected) || string.IsNullOrWhiteSpace(replacement))
            return text;

        var exact = text.IndexOf(selected, StringComparison.Ordinal);
        if (exact >= 0)
            return text[..exact] + replacement + text[(exact + selected.Length)..];

        exact = text.IndexOf(selected, StringComparison.OrdinalIgnoreCase);
        if (exact >= 0)
            return text[..exact] + replacement + text[(exact + selected.Length)..];

        var escaped = Regex.Escape(selected.Trim());
        return Regex.Replace(text, escaped, replacement, RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(60));
    }

    private static string CleanLine(string text)
        => Regex.Replace((text ?? "").Replace('\r', ' ').Replace('\n', ' '), @"\s+", " ").Trim();

    private static string NormalizeSearch(string text)
        => Regex.Replace(CleanLine(text).ToLowerInvariant(), @"[\p{P}\p{S}]+", " ").Trim();

    private static string NormalizeLanguage(string targetLanguage)
        => string.IsNullOrWhiteSpace(targetLanguage) ? "ar" : targetLanguage.Trim().ToLowerInvariant();

    private static string PreferLonger(string current, string candidate, int max)
        => TrimForStorage(candidate, max).Length > TrimForStorage(current, max).Length
            ? TrimForStorage(candidate, max)
            : TrimForStorage(current, max);

    private static string TrimForStorage(string text, int max)
    {
        var clean = CleanLine(text);
        return clean.Length <= max ? clean : clean.Substring(0, max - 1).TrimEnd() + "…";
    }

    private sealed class GlossaryMemoryState
    {
        public int Version { get; set; } = 1;
        public List<GlossaryMemoryRule> Rules { get; set; } = new();
    }
}

public sealed class GlossaryMemoryRule
{
    public string Id { get; set; } = "";
    public string TargetLanguage { get; set; } = "ar";
    public string SourceText { get; set; } = "";
    public string SelectedText { get; set; } = "";
    public string ReplacementText { get; set; } = "";
    public string Context { get; set; } = "";
    public int UseCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public GlossaryMemoryRule Clone() => new()
    {
        Id = Id,
        TargetLanguage = TargetLanguage,
        SourceText = SourceText,
        SelectedText = SelectedText,
        ReplacementText = ReplacementText,
        Context = Context,
        UseCount = UseCount,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt,
    };
}
