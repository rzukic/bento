using System.Globalization;

namespace Bento.Api.Formula;

/// <summary>A cash flow: money paid out is negative, money received positive.</summary>
public readonly record struct CashFlow(DateOnly Date, double Amount);

/// <summary>
/// Returns (Step 4.3), computed after the database: the internal rate of return
/// of dated cash flows (XIRR, as in Excel), of periodic ones (IRR), the
/// time-weighted return of values at period ends, and rebasing to a first value.
/// </summary>
public static class Returns
{
    private const double Tolerance = 1e-10;

    /// <summary>"amount|date;amount|date…" as the database concatenates FLOWS(…).</summary>
    public static IEnumerable<CashFlow> ParseFlows(string? text)
    {
        if (string.IsNullOrEmpty(text))
            yield break;
        foreach (var item in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var bar = item.IndexOf('|');
            if (bar <= 0 || item.Length < bar + 11)
                continue;
            if (!double.TryParse(item[..bar], NumberStyles.Float, CultureInfo.InvariantCulture, out var amount))
                continue;
            if (!DateOnly.TryParseExact(item.Substring(bar + 1, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                continue;
            if (amount != 0)
                yield return new CashFlow(date, amount);
        }
    }

    /// <summary>
    /// The annual rate at which the flows' present value is zero, counting
    /// days/365 from the first flow (Excel's XIRR). Empty without a flow of
    /// each sign, or when no rate is found.
    /// </summary>
    public static double? Xirr(IReadOnlyList<CashFlow> flows)
    {
        if (flows.Count < 2 || !flows.Any(f => f.Amount > 0) || !flows.Any(f => f.Amount < 0))
            return null;
        var start = flows.Min(f => f.Date);
        var years = flows.Select(f => (f.Date.DayNumber - start.DayNumber) / 365.0).ToArray();
        var amounts = flows.Select(f => f.Amount).ToArray();
        return Solve(rate =>
        {
            double value = 0, slope = 0;
            for (var i = 0; i < amounts.Length; i++)
            {
                var factor = Math.Pow(1 + rate, years[i]);
                value += amounts[i] / factor;
                slope -= years[i] * amounts[i] / (factor * (1 + rate));
            }
            return (value, slope);
        });
    }

    /// <summary>The rate per period of flows at equal intervals, in date order (Excel's IRR).</summary>
    public static double? Irr(IReadOnlyList<CashFlow> flows)
    {
        if (flows.Count < 2 || !flows.Any(f => f.Amount > 0) || !flows.Any(f => f.Amount < 0))
            return null;
        var amounts = flows.OrderBy(f => f.Date).Select(f => f.Amount).ToArray();
        return Solve(rate =>
        {
            double value = 0, slope = 0;
            for (var i = 0; i < amounts.Length; i++)
            {
                var factor = Math.Pow(1 + rate, i);
                value += amounts[i] / factor;
                slope -= i * amounts[i] / (factor * (1 + rate));
            }
            return (value, slope);
        });
    }

    /// <summary>Newton's method from 10 %, then bisection between -99.99 % and 10000 % when it does not converge.</summary>
    private static double? Solve(Func<double, (double Value, double Slope)> npv)
    {
        var rate = 0.1;
        for (var i = 0; i < 100; i++)
        {
            var (value, slope) = npv(rate);
            if (Math.Abs(value) < Tolerance)
                return rate;
            if (slope == 0 || double.IsNaN(slope))
                break;
            var next = rate - value / slope;
            if (next <= -1 || double.IsNaN(next) || double.IsInfinity(next))
                break;
            if (Math.Abs(next - rate) < Tolerance)
                return next;
            rate = next;
        }

        double low = -0.9999, high = 100;
        var lowValue = npv(low).Value;
        if (double.IsNaN(lowValue) || Math.Sign(lowValue) == Math.Sign(npv(high).Value))
            return null;
        for (var i = 0; i < 300; i++)
        {
            var mid = (low + high) / 2;
            var midValue = npv(mid).Value;
            if (Math.Abs(midValue) < Tolerance || (high - low) / 2 < Tolerance)
                return mid;
            if (Math.Sign(midValue) == Math.Sign(lowValue))
            {
                low = mid;
                lowValue = midValue;
            }
            else
            {
                high = mid;
            }
        }
        return (low + high) / 2;
    }

    /// <summary>
    /// The time-weighted return up to each period: the product of the period
    /// growths (V_t − F_t + W_t) / (V_t−1 + W_t), where F_t is the money put in
    /// during the period (taken out negative) and W_t the same weighted by the
    /// share of the period it was in (Modified Dietz). Without dates W_t = F_t:
    /// V_t / (V_t−1 + F_t), the flow counting from the period's start. The first
    /// period is the base: 0, or the index's start. A period whose base is not
    /// positive (an empty portfolio before the first purchase) does not move it.
    /// </summary>
    public static double?[] Twr(IReadOnlyList<(double? Value, double? Flow, double? Weighted)> periods, double? index)
    {
        var result = new double?[periods.Count];
        double growth = 1;
        double? previous = null;
        for (var i = 0; i < periods.Count; i++)
        {
            var (value, flow, weighted) = periods[i];
            if (value is null)
                continue;
            var f = flow ?? 0;
            var w = weighted ?? f;
            if (previous is { } start && start + w is var invested && invested > 0)
                growth *= (value.Value - f + w) / invested;
            previous = value;
            result[i] = index is { } first ? first * growth : growth - 1;
        }
        return result;
    }

    /// <summary>The flows within a period (start and end inclusive): their sum, and their sum weighted by the days they were in.</summary>
    public static (double Flow, double Weighted) InPeriod(IEnumerable<CashFlow> flows, DateOnly start, DateOnly end)
    {
        var days = end.DayNumber - start.DayNumber + 1;
        double flow = 0, weighted = 0;
        foreach (var f in flows.Where(f => f.Date >= start && f.Date <= end))
        {
            flow += f.Amount;
            weighted += f.Amount * (end.DayNumber - f.Date.DayNumber + 1) / days;
        }
        return (flow, weighted);
    }

    /// <summary>Each value relative to the first non-empty one, which becomes <paramref name="start"/>.</summary>
    public static double?[] Rebase(IReadOnlyList<double?> values, double start)
    {
        var first = values.FirstOrDefault(v => v is not null && v != 0);
        return values.Select(v => v is null || first is null ? null : v / first * start).ToArray();
    }
}
