namespace Repere.Api;

public static class ApiRoutes
{
    public const string Prefix = "api/v1";
    public const string Auth = Prefix + "/auth";
    public const string Admin = Prefix + "/admin";
    public const string Data = Prefix + "/data";
    public const string Query = Prefix + "/query";
    public const string AdminHealth = Admin + "/health";
}
