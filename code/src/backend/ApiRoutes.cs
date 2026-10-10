namespace Bento.Api;

public static class ApiRoutes
{
    public const string Prefix = "api/v1";
    public const string Auth = Prefix + "/auth";
    public const string Schema = Prefix + "/schema";
    public const string Admin = Prefix + "/admin";
    public const string Data = Prefix + "/data";
    public const string Query = Prefix + "/query";
    public const string SavedQueries = Prefix + "/queries";
    public const string Formulas = Prefix + "/formulas";
    public const string Functions = Prefix + "/functions";
    public const string Dashboards = Prefix + "/dashboards";
    public const string Forms = Prefix + "/forms";
    public const string Views = Prefix + "/views";
    public const string Import = Prefix + "/import";
    public const string ExplorerPreferences = Prefix + "/explorer/preferences";
    public const string Setup = Prefix + "/setup";
    public const string SetupStatus = Setup + "/status";
    public const string AdminHealth = Admin + "/health";
    public const string ApiKeys = Admin + "/api-keys";
    public const string Credentials = Admin + "/credentials";
    public const string Problems = Admin + "/problems";
    public const string PriceSources = Prefix + "/price-sources";
    public const string OpenApi = Prefix + "/openapi.json";
    public const string OAuth = Prefix + "/oauth";
    public const string OAuthRegister = OAuth + "/register";
    public const string OAuthToken = OAuth + "/token";
    public const string Mcp = "mcp";
    public const string McpConnections = Prefix + "/mcp";
    public const string McpBundle = McpConnections + "/bento.mcpb";
    public const string Ai = Prefix + "/ai";
    public const string Categorise = Prefix + "/categorise";
    public const string AdminHealthDetails = AdminHealth + "/details";
}
