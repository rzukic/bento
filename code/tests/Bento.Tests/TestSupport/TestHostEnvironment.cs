using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Bento.Tests;

public sealed class TestHostEnvironment : IHostEnvironment
{
    public TestHostEnvironment()
    {
    }

    public TestHostEnvironment(string environmentName)
    {
        EnvironmentName = environmentName;
    }

    public string EnvironmentName { get; set; } = Environments.Development;

    public string ApplicationName { get; set; } = "Bento.Tests";

    public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

    public IFileProvider ContentRootFileProvider { get; set; } = null!;
}
