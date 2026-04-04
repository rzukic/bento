using Repere.Api;

var builder = WebApplication.CreateBuilder(args);

BackendHost.ConfigureConfiguration(builder);
BackendHost.ConfigureServices(builder.Services, builder.Configuration, builder.Environment);

var app = builder.Build();

BackendHost.ConfigureMiddleware(app);
BackendHost.ConfigureRoutes(app);

app.Run();

public partial class Program;
