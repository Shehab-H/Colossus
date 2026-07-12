using Colossus.Domain.Measures;
using Xunit;

namespace Colossus.Tests;

public class MeasurePartialsTests
{
    private static IReadOnlyList<string> Names(params string[] exprs) =>
        MeasurePartials.For(exprs.Select(MeasureParser.Parse)).Select(p => p.Name).ToList();

    [Fact]
    public void Flagship_UnionsAndDeduplicates()
    {
        // sum(tests), wavg(download_mbps, tests), share(sum(tests)) where …, argmax(operator, sum(tests))
        // all reduce to sum__tests (+ the wavg cross-product); the shared sum__tests appears once.
        var names = Names(
            "sum(tests)",
            "wavg(download_mbps, tests)",
            "share(sum(tests)) where operator = 'apex'",
            "argmax(operator, sum(tests))");
        Assert.Equal(new HashSet<string> { "sum__tests", "swp__download_mbps__tests" }, names.ToHashSet());
    }

    [Fact]
    public void EachVerb_ExpandsToItsPartials()
    {
        Assert.Equal(new[] { "cnt" }, Names("count()"));
        Assert.Equal(new HashSet<string> { "sum__v", "cnt" }, Names("avg(v)").ToHashSet());
        Assert.Equal(new[] { "min__v" }, Names("min(v)"));
        Assert.Equal(new[] { "max__v" }, Names("max(v)"));
        Assert.Equal(new HashSet<string> { "swp__a__b", "sum__b" }, Names("wavg(a, b)").ToHashSet());
    }
}
