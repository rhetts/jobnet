using System;
using System.Text.RegularExpressions;

namespace Jobnet.Services.Profiling;

/// <summary>Lightweight HTML → text extractor. Removes scripts, styles, tags; collapses whitespace.
/// Good enough for sending company homepage content to a summarization LLM.</summary>
internal static class HtmlTextExtractor
{
    private static readonly Regex ScriptBlock = new(@"<script[\s\S]*?</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StyleBlock  = new(@"<style[\s\S]*?</style>",   RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CommentRe   = new(@"<!--[\s\S]*?-->",          RegexOptions.Compiled);
    private static readonly Regex SvgBlock    = new(@"<svg[\s\S]*?</svg>",       RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex NoscriptRe  = new(@"<noscript[\s\S]*?</noscript>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TagRe       = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRe = new(@"\s+", RegexOptions.Compiled);

    /// <summary>Strip HTML to plain text. Truncates to maxChars (after collapsing whitespace).</summary>
    public static string Extract(string html, int maxChars = 6000)
    {
        if (string.IsNullOrEmpty(html)) return "";
        var s = html;
        s = ScriptBlock.Replace(s, " ");
        s = StyleBlock.Replace(s, " ");
        s = CommentRe.Replace(s, " ");
        s = SvgBlock.Replace(s, " ");
        s = NoscriptRe.Replace(s, " ");
        s = TagRe.Replace(s, " ");
        s = System.Net.WebUtility.HtmlDecode(s);
        s = WhitespaceRe.Replace(s, " ").Trim();
        if (s.Length > maxChars) s = s.Substring(0, maxChars);
        return s;
    }
}
