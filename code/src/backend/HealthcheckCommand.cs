namespace Repere.Api;

internal static class HealthcheckCommand
{
    public static bool IsRequested(string[] args) =>
        args.Length == 1 && string.Equals(args[0], "--healthcheck", StringComparison.Ordinal);

    public static async Task<int> RunAsync(Func<string, string?> getEnvironmentVariable)
    {
        var port = ResolvePort(getEnvironmentVariable("BACKEND_PORT"));
        var url = $"http://127.0.0.1:{port}/api/v1/admin/health";

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(4)
            };
            using var response = await client.GetAsync(url);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }

    private static int ResolvePort(string? rawPort) =>
        int.TryParse(rawPort, out var port) && port > 0
            ? port
            : 5000;
}
