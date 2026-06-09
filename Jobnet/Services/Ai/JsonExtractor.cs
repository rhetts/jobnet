namespace Jobnet.Services.Ai;

/// <summary>
/// Recovers a strict-JSON object from an LLM response that may include code fences, prose,
/// or both. Used by every AI client that prompts for a JSON answer. Even cloud providers
/// occasionally wrap output in <c>```json ... ```</c> or add a sentence before / after the
/// object despite "JSON only" instructions; local Llama does it constantly.
///
/// Strategy (cheap first, then forgiving):
///   1. If the trimmed text opens with a <c>```</c> fence, drop the opening line (which may
///      carry a language tag like <c>json</c>) and the matching closing fence.
///   2. Locate the first <c>{</c> and walk forward counting brace depth, with awareness of
///      JSON string literals (so a <c>{</c> inside a string value doesn't fool the counter).
///      Return the substring from the opening brace to the balanced close.
///   3. If no balanced object is found, return the trimmed input so the caller's parse step
///      surfaces a meaningful error instead of a silent empty result.
///
/// Cheaper than a real JSON parser, handles every shape we've seen the models emit, and
/// rejects unbalanced output (e.g. truncated responses) instead of returning garbage.
/// </summary>
public static class JsonExtractor
{
    public static string ExtractJsonObject(string response)
    {
        if (string.IsNullOrEmpty(response)) return response;
        var s = response.Trim();

        // (1) Strip a leading ``` fence. Closing fence may legitimately be missing — the
        // model sometimes hits its token budget before emitting it. Take whatever's left.
        if (s.StartsWith("```"))
        {
            var nl = s.IndexOf('\n');
            if (nl > 0) s = s[(nl + 1)..];
            var close = s.LastIndexOf("```", System.StringComparison.Ordinal);
            if (close > 0) s = s[..close];
            s = s.Trim();
        }

        // (2) Walk the first balanced object.
        var start = s.IndexOf('{');
        if (start < 0) return s;
        int depth = 0;
        bool inStr = false;
        bool esc = false;
        for (int i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (inStr)
            {
                if (esc) { esc = false; }
                else if (c == '\\') { esc = true; }
                else if (c == '"') { inStr = false; }
                continue;
            }
            switch (c)
            {
                case '"': inStr = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0) return s.Substring(start, i - start + 1);
                    break;
            }
        }
        // Unbalanced — likely truncated. Hand back the cleaned-but-incomplete text so the
        // caller's JsonDocument.Parse raises a clear error rather than silently returning {}.
        return s;
    }
}
