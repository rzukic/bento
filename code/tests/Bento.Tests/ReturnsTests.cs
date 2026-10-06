using Bento.Api.Formula;

namespace Bento.Tests;

/// <summary>Step 4.3: the return calculations against Excel's documented examples.</summary>
public sealed class ReturnsTests
{
    private static CashFlow F(string date, double amount) => new(DateOnly.Parse(date), amount);

    [Fact]
    public void Xirr_MatchesExcel()
    {
        // Excel's XIRR example: 0.373362535.
        var rate = Returns.Xirr([F("2008-01-01", -10000), F("2008-03-01", 2750), F("2008-10-30", 4250), F("2009-02-15", 3250), F("2009-04-01", 2750)]);
        Assert.InRange(rate!.Value, 0.373362535 - 1e-8, 0.373362535 + 1e-8);

        // Order does not matter; a loss gives a negative rate; flows of one sign give none.
        Assert.Equal(rate.Value, Returns.Xirr([F("2009-04-01", 2750), F("2008-01-01", -10000), F("2009-02-15", 3250), F("2008-03-01", 2750), F("2008-10-30", 4250)])!.Value, 9);
        Assert.Equal(-0.5, Returns.Xirr([F("2025-01-01", -1000), F("2026-01-01", 500)])!.Value, 6);
        Assert.Null(Returns.Xirr([F("2025-01-01", -1000), F("2026-01-01", -500)]));
        Assert.Null(Returns.Xirr([F("2025-01-01", -1000)]));
    }

    [Fact]
    public void Irr_MatchesExcel()
    {
        // Excel's IRR example: 5 years of income for 70,000 → 8.66 %.
        var flows = new[] { -70000.0, 12000, 15000, 18000, 21000, 26000 }.Select((a, i) => F($"202{i}-01-01", a)).ToList();
        Assert.Equal(0.086630948, Returns.Irr(flows)!.Value, 8);
    }

    [Fact]
    public void ParseFlows_ReadsBothProviders()
    {
        Assert.Equal([F("2026-01-31", -45.9), F("2026-02-01", 3000), F("2026-03-01", 0.5)],
            Returns.ParseFlows("-45.9|2026-01-31;3000.0000|2026-02-01;0|2026-02-02;5.0e-01|2026-03-01 00:00:00;bad|x").ToList());
        Assert.Empty(Returns.ParseFlows(null));
    }

    [Fact]
    public void Twr_ChainsPeriodGrowth()
    {
        // Dec: 1000. Jan: +10 % → 1100. Feb: 500 put in at the start, then −10 % → (1100 + 500) × 0.9 = 1440.
        double?[] twr = Returns.Twr([(1000, 0), (1100, 0), (1440, 500)], null);
        Assert.Equal(0, twr[0]!.Value, 10);
        Assert.Equal(0.1, twr[1]!.Value, 10);
        Assert.Equal(1.1 * 0.9 - 1, twr[2]!.Value, 10);
        Assert.Equal(99, Returns.Twr([(1000, 0), (1100, 0), (1440, 500)], 100)[2]!.Value, 10);

        // An empty portfolio before the first purchase: the purchase is the base.
        double?[] fresh = Returns.Twr([(0, 0), (1050, 1000), (1050, 0)], 100);
        Assert.Equal([100, 105, 105], fresh.Select(v => Math.Round(v!.Value, 10)));
    }

    [Fact]
    public void Rebase_StartsAtTheFirstValue()
    {
        Assert.Equal([null, 100, 110, 90], Returns.Rebase([null, 50, 55, 45], 100).Select(v => v is null ? (double?)null : Math.Round(v.Value, 10)));
    }
}
