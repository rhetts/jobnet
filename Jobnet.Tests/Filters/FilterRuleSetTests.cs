using System.Collections.Generic;
using System.Linq;
using Jobnet.Models;
using Jobnet.Services.Filters;

namespace Jobnet.Tests.Filters;

public class FilterRuleSetTests
{
    private static FilterRule Rule(string subject, string pattern, string matchType,
                                    string action = FilterAction.Block, string? scope = null)
        => new()
        {
            Id = _nextId++,
            Subject = subject,
            Action = action,
            MatchType = matchType,
            Pattern = pattern,
            Scope = scope,
            IsEnabled = true,
        };

    private static int _nextId = 1;

    private static FilterRuleSet Set(params FilterRule[] rules) => FilterRuleSet.FromRules(rules);

    // ── domain matching ────────────────────────────────────────────────────

    [Fact]
    public void Domain_match_covers_subdomains()
    {
        // The bug that motivated the unification: three of the four legacy lists used exact
        // matching, so ca.linkedin.com passed filters that linkedin.com would have failed.
        var set = Set(Rule(FilterSubject.Host, "linkedin.com", FilterMatchType.Domain));

        Assert.True(set.IsHostBlocked("linkedin.com"));
        Assert.True(set.IsHostBlocked("ca.linkedin.com"));
        Assert.True(set.IsHostBlocked("www.linkedin.com"));
    }

    [Fact]
    public void Domain_match_requires_a_dot_boundary()
    {
        // "mylinkedin.com" must NOT be caught by a "linkedin.com" rule — a naive EndsWith would.
        var set = Set(Rule(FilterSubject.Host, "linkedin.com", FilterMatchType.Domain));

        Assert.False(set.IsHostBlocked("mylinkedin.com"));
        Assert.False(set.IsHostBlocked("linkedin.com.evil.net"));
    }

    [Fact]
    public void Domain_match_does_not_false_match_sibling_multilabel_domains()
    {
        // bcsc.bc.ca is a regulator we block; real BC companies on *.bc.ca must survive.
        var set = Set(Rule(FilterSubject.Host, "bcsc.bc.ca", FilterMatchType.Domain));

        Assert.True(set.IsHostBlocked("bcsc.bc.ca"));
        Assert.False(set.IsHostBlocked("acme.bc.ca"));
    }

    // ── url rules ──────────────────────────────────────────────────────────

    [Fact]
    public void Category_rule_blocks_the_wordpress_archives_that_stalled_blue_ant()
    {
        var set = Set(Rule(FilterSubject.Url, @"/(category|tag|author)/", FilterMatchType.Regex,
                            scope: FilterScope.Crawl));

        Assert.True(set.IsUrlBlocked("https://blueantmedia.com/category/all-news/page/5/", FilterScope.Crawl));
        Assert.True(set.IsUrlBlocked("https://blueantmedia.com/category/channels/bbc-earth/", FilterScope.Crawl));
    }

    [Fact]
    public void Category_rule_leaves_real_careers_pages_alone()
    {
        var set = Set(Rule(FilterSubject.Url, @"/(category|tag|author)/", FilterMatchType.Regex,
                            scope: FilterScope.Crawl));

        // The reason the old skip list ran last: "/about" would have killed this URL. Precise
        // regex means we can run the blocklist first without that collateral damage.
        Assert.False(set.IsUrlBlocked("https://blueantmedia.com/about-us/careers/", FilterScope.Crawl));
        Assert.False(set.IsUrlBlocked("https://acme.com/jobs/", FilterScope.Crawl));
    }

    [Fact]
    public void Host_rules_also_block_urls_on_that_host()
    {
        // A host rule has to reach the crawl path too, otherwise UrlClassifier's "/jobs" check
        // happily files linkedin.com/jobs/view/123 as a job board — which is what used to happen.
        var set = Set(Rule(FilterSubject.Host, "linkedin.com", FilterMatchType.Domain));

        Assert.True(set.IsUrlBlocked("https://www.linkedin.com/jobs/view/12345", FilterScope.Crawl));
    }

    // ── scope ──────────────────────────────────────────────────────────────

    [Fact]
    public void Scoped_rule_applies_only_within_its_scope()
    {
        // Big-tech domains are blocked from discovery but must stay crawlable if the user
        // deliberately added one as a company to track.
        var set = Set(Rule(FilterSubject.Host, "microsoft.com", FilterMatchType.Domain,
                            scope: FilterScope.Discovery));

        Assert.True(set.IsHostBlocked("microsoft.com", FilterScope.Discovery));
        Assert.False(set.IsHostBlocked("microsoft.com", FilterScope.Crawl));
    }

    [Fact]
    public void Null_scope_applies_everywhere()
    {
        var set = Set(Rule(FilterSubject.Host, "indeed.com", FilterMatchType.Domain, scope: null));

        Assert.True(set.IsHostBlocked("indeed.com", FilterScope.Discovery));
        Assert.True(set.IsHostBlocked("indeed.com", FilterScope.Crawl));
    }

    // ── allow beats block ──────────────────────────────────────────────────

    [Fact]
    public void Allow_rule_overrides_a_matching_block_rule()
    {
        // Phase 4 depends on this: LocationMatcher's "any Canada signal wins outright" is an
        // allow rule sitting on top of the US/other-city block rules.
        var set = Set(
            Rule(FilterSubject.Host, "github.com", FilterMatchType.Domain),
            Rule(FilterSubject.Host, "pages.github.com", FilterMatchType.Domain, FilterAction.Allow));

        Assert.True(set.IsHostBlocked("github.com"));
        Assert.False(set.IsHostBlocked("pages.github.com"));
    }

    // ── match types ────────────────────────────────────────────────────────

    [Fact]
    public void Word_match_respects_word_boundaries()
    {
        // The greylist semantics phase 2 needs: "designer" must not fire on "design".
        var set = Set(Rule(FilterSubject.JobTitle, "designer", FilterMatchType.Word));

        Assert.True(set.Matches(FilterSubject.JobTitle, FilterAction.Block, "Senior Product Designer"));
        Assert.False(set.Matches(FilterSubject.JobTitle, FilterAction.Block, "Design Systems Engineer"));
    }

    [Fact]
    public void Substring_and_exact_differ()
    {
        var sub = Set(Rule(FilterSubject.JobTitle, "intern", FilterMatchType.Substring));
        var exact = Set(Rule(FilterSubject.JobTitle, "intern", FilterMatchType.Exact));

        Assert.True(sub.Matches(FilterSubject.JobTitle, FilterAction.Block, "Marketing Intern"));
        Assert.False(exact.Matches(FilterSubject.JobTitle, FilterAction.Block, "Marketing Intern"));
        Assert.True(exact.Matches(FilterSubject.JobTitle, FilterAction.Block, "Intern"));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var set = Set(Rule(FilterSubject.Url, "/CATEGORY/", FilterMatchType.Regex, scope: FilterScope.Crawl));
        Assert.True(set.IsUrlBlocked("https://acme.com/category/news/", FilterScope.Crawl));
    }

    // ── robustness ─────────────────────────────────────────────────────────

    [Fact]
    public void Bad_regex_is_reported_not_swallowed()
    {
        // GreylistMatcher can silently drop a bad token because its input is Regex.Escape'd.
        // These patterns are user-authored, so a rule that looks active but never fires is a
        // trap — it has to surface.
        var set = Set(
            Rule(FilterSubject.Url, "[unclosed", FilterMatchType.Regex),
            Rule(FilterSubject.Host, "indeed.com", FilterMatchType.Domain));

        Assert.Single(set.CompileErrors);
        Assert.Contains("[unclosed", set.CompileErrors[0]);
        // The valid rule alongside it still works.
        Assert.True(set.IsHostBlocked("indeed.com"));
    }

    [Fact]
    public void Disabled_rules_are_ignored()
    {
        var disabled = Rule(FilterSubject.Host, "indeed.com", FilterMatchType.Domain);
        disabled.IsEnabled = false;

        Assert.False(Set(disabled).IsHostBlocked("indeed.com"));
    }

    [Fact]
    public void Empty_set_blocks_nothing()
    {
        var set = FilterRuleSet.Empty();

        Assert.False(set.IsUrlBlocked("https://anything.com/category/x/"));
        Assert.False(set.IsHostBlocked("linkedin.com"));
    }

    [Fact]
    public void Null_and_malformed_input_is_safe()
    {
        var set = Set(Rule(FilterSubject.Host, "linkedin.com", FilterMatchType.Domain));

        Assert.False(set.IsUrlBlocked(null));
        Assert.False(set.IsUrlBlocked(""));
        Assert.False(set.IsUrlBlocked("not-a-url"));
        Assert.False(set.IsHostBlocked(null));
    }

    [Fact]
    public void ExplainUrl_names_the_rule_that_blocked_it()
    {
        var set = Set(Rule(FilterSubject.Url, @"/category/", FilterMatchType.Regex, scope: FilterScope.Crawl));

        var hit = set.ExplainUrl("https://blueantmedia.com/category/all-news/page/5/", FilterScope.Crawl);

        Assert.NotNull(hit);
        Assert.Equal(@"/category/", hit!.Pattern);
    }
}
