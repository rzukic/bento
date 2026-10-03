namespace Bento.Api;

public static class ApiRoutes
{
    public const string Prefix = "api/v1";
    public const string Auth = Prefix + "/auth";
    public const string Schema = Prefix + "/schema";
    public const string Admin = Prefix + "/admin";
    public const string Data = Prefix + "/data";
    public const string Query = Prefix + "/query";
    public const string Views = Prefix + "/views";
    public const string Import = Prefix + "/import";
    public const string ExplorerPreferences = Prefix + "/explorer/preferences";
    public const string Setup = Prefix + "/setup";
    public const string SetupStatus = Setup + "/status";
    public const string AdminHealth = Admin + "/health";
    public const string AdminHealthDetails = AdminHealth + "/details";
}
