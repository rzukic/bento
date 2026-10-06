using System.Text.Json;
using Bento.Api.Formula;
using Bento.Api.Formula.Sandbox;

namespace Bento.Tests;

/// <summary>Step 4.2: the shipped templates compile, and (opt-in) work against the real APIs.</summary>
public sealed class FunctionTemplateTests
{
    private static CustomFunctionDef Def(FunctionTemplate t) =>
        new(t.Name, t.Description, t.Arguments, t.ResultType, t.Code, 1) { AllowedHosts = t.AllowedHosts, Credentials = t.Credentials };

    [Fact]
    public void Templates_Compile()
    {
        foreach (var template in FunctionTemplates.All)
        {
            Assert.Null(JsFunction.CheckSyntax(Def(template)));
            Assert.All(template.AllowedHosts, h => Assert.True(CustomFunctionRules.IsValidHost(h), h));
            Assert.True(CustomFunctionRules.IsValidName(template.Name));
        }
    }

    private static JsonElement Call(string id, params object?[] values)
    {
        var template = FunctionTemplates.All.Single(t => t.Id == id);
        using var network = new JsNetwork(new JsNetworkOptions());
        var (function, error) = JsFunction.Create(Def(template), host: new JsHost(network, new Dictionary<string, string>()));
        Assert.True(function is not null, error);
        using (function)
        {
            var result = function!.Call(values);
            Assert.True(result.Ok, $"{result.Error} {string.Join(" | ", result.Logs)}");
            return (JsonElement)result.Value!;
        }
    }

    [InternetFact]
    public void Yahoo_LatestAndHistory()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var latest = Call("yahoo-prices", JsonSerializer.SerializeToElement(new[] { "EUNL.DE", "VWCE.DE", "EUDF.DE", "NOPE.XX" }),
            today.AddDays(-7).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"));
        var symbols = latest.EnumerateArray().Select(r => r.GetProperty("symbol").GetString()).Distinct().Order().ToList();
        Assert.Equal(["EUDF.DE", "EUNL.DE", "VWCE.DE"], symbols);
        Assert.Contains(latest.EnumerateArray(), r => r.TryGetProperty("time", out _));

        var history = Call("yahoo-prices", JsonSerializer.SerializeToElement(new[] { "VWCE.DE" }),
            today.AddYears(-1).ToString("yyyy-MM-dd"), today.ToString("yyyy-MM-dd"));
        Assert.InRange(history.GetArrayLength(), 240, 270);
        Assert.All(history.EnumerateArray(), r => Assert.True(r.GetProperty("price").GetDecimal() > 0));
    }

    [InternetFact]
    public void OpenFigi_SuggestsXetraSymbols()
    {
        var map = Call("openfigi-xetra", JsonSerializer.SerializeToElement(new[] { "IE00BK5BQT80", "IE00B4L5Y983", "IE0002Y8CX98" }));
        Assert.Equal("VWCE.DE", map.GetProperty("IE00BK5BQT80").GetString());
        Assert.Equal("EUNL.DE", map.GetProperty("IE00B4L5Y983").GetString());
        Assert.Equal("EUDF.DE", map.GetProperty("IE0002Y8CX98").GetString());
    }
}
