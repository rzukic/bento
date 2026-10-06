namespace Bento.Tests;

/// <summary>Runs only with BENTO_TEST_INTERNET=1: tests that call real public APIs (not in CI).</summary>
public sealed class InternetFactAttribute : FactAttribute
{
    public InternetFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("BENTO_TEST_INTERNET") != "1")
            Skip = "Set BENTO_TEST_INTERNET=1 to call real APIs.";
    }
}
