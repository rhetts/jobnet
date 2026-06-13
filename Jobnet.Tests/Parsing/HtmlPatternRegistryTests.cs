using System;
using System.Collections.Generic;
using Jobnet.Services.JobSources;
using Jobnet.Services.Parsing.HtmlPatternParsers;

namespace Jobnet.Tests.Parsing;

public class HtmlPatternRegistryTests
{
    [Fact]
    public void ResolveFor_returns_null_when_no_parser_matches()
    {
        var reg = new HtmlPatternRegistry(new IHtmlPatternParser[]
        {
            new FakeParser("a", canHandle: false),
            new FakeParser("b", canHandle: false),
        });
        Assert.Null(reg.ResolveFor("https://example.com/", "<html/>"));
    }

    [Fact]
    public void ResolveFor_returns_first_match_in_registration_order()
    {
        // Two parsers both claim the page. The earlier-registered one must win — registration
        // order is contractually the priority order.
        var first  = new FakeParser("priority_winner", canHandle: true);
        var second = new FakeParser("would_also_match", canHandle: true);
        var reg = new HtmlPatternRegistry(new IHtmlPatternParser[] { first, second });

        var resolved = reg.ResolveFor("https://example.com/", "<html/>");
        Assert.Same(first, resolved);
    }

    [Fact]
    public void ResolveFor_skips_parsers_that_say_no_until_one_says_yes()
    {
        var loser  = new FakeParser("loser", canHandle: false);
        var winner = new FakeParser("winner", canHandle: true);
        var reg = new HtmlPatternRegistry(new IHtmlPatternParser[] { loser, winner });

        var resolved = reg.ResolveFor("https://example.com/", "<html/>");
        Assert.Same(winner, resolved);
    }

    [Fact]
    public void ResolveFor_returns_null_on_empty_html()
    {
        // Fast bail-out — we don't even probe parsers when there's nothing to look at.
        var probed = false;
        var probe = new FakeParser("probe", canHandle: true, onCanHandle: () => probed = true);
        var reg = new HtmlPatternRegistry(new IHtmlPatternParser[] { probe });

        Assert.Null(reg.ResolveFor("https://example.com/", ""));
        Assert.False(probed, "registry should not probe parsers when html is empty");
    }

    [Fact]
    public void ResolveFor_keeps_going_when_a_parser_throws_in_CanHandle()
    {
        // A buggy parser shouldn't poison the whole pipeline — the next one still gets a shot.
        var thrower = new FakeParser("thrower", canHandle: false, throwInCanHandle: true);
        var winner  = new FakeParser("winner", canHandle: true);
        var reg = new HtmlPatternRegistry(new IHtmlPatternParser[] { thrower, winner });

        var resolved = reg.ResolveFor("https://example.com/", "<html/>");
        Assert.Same(winner, resolved);
    }

    [Fact]
    public void Names_reflects_registration_order()
    {
        var reg = new HtmlPatternRegistry(new IHtmlPatternParser[]
        {
            new FakeParser("alpha", canHandle: false),
            new FakeParser("bravo", canHandle: false),
            new FakeParser("charlie", canHandle: false),
        });
        Assert.Equal(new[] { "alpha", "bravo", "charlie" }, reg.Names);
    }

    [Fact]
    public void Real_registered_parsers_compose_correctly_through_registry()
    {
        // End-to-end: feed the two real fixtures through a registry holding the two real
        // parsers. Each should route to exactly one parser. Catches the case where two parsers
        // both claim a page (priority order matters then).
        var reg = new HtmlPatternRegistry(new IHtmlPatternParser[]
        {
            new LeverShortcodeParser(),
            new GreenhouseLinkParser(),
        });

        var blackbird = Fixtures.FixtureLoader.Load("blackbird-interactive.html");
        var sevenShifts = Fixtures.FixtureLoader.Load("7shifts.html");

        var bbResolved = reg.ResolveFor("https://blackbirdinteractive.com/careers-2/", blackbird);
        Assert.NotNull(bbResolved);
        Assert.Equal("lever_shortcode", bbResolved!.Name);

        var s7Resolved = reg.ResolveFor("https://www.7shifts.com/careers", sevenShifts);
        Assert.NotNull(s7Resolved);
        Assert.Equal("greenhouse_link", s7Resolved!.Name);
    }

    /// <summary>Test double: matches or not based on the constructor flag, can be configured
    /// to throw to exercise the registry's defensive try/catch.</summary>
    private sealed class FakeParser : IHtmlPatternParser
    {
        private readonly bool _canHandle;
        private readonly bool _throw;
        private readonly Action? _onCanHandle;

        public FakeParser(string name, bool canHandle, bool throwInCanHandle = false, Action? onCanHandle = null)
        {
            Name = name;
            _canHandle = canHandle;
            _throw = throwInCanHandle;
            _onCanHandle = onCanHandle;
        }

        public string Name { get; }

        public bool CanHandle(string url, string html)
        {
            _onCanHandle?.Invoke();
            if (_throw) throw new InvalidOperationException("simulated parser failure");
            return _canHandle;
        }

        public IReadOnlyList<RawJobPosting> Parse(string html, string baseUrl) => Array.Empty<RawJobPosting>();
    }
}
