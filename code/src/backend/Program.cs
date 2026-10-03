using Bento.Api;

if (HealthcheckCommand.IsRequested(args))
{
    Environment.ExitCode = await HealthcheckCommand.RunAsync(Environment.GetEnvironmentVariable);
    return;
}

var builder = WebApplication.CreateBuilder(args);

BackendHost.ConfigureConfiguration(builder);
BackendHost.ConfigureServices(builder.Services, builder.Configuration, builder.Environment);

var app = builder.Build();

BackendHost.ConfigureMiddleware(app);
BackendHost.ConfigureRoutes(app);

app.Run();

public partial class Program;
