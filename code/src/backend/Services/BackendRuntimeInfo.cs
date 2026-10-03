using System.Reflection;

namespace Bento.Api.Services;

public sealed record BackendRuntimeInfo(
    DateTimeOffset StartedAtUtc,
    string Version)
{
    public static BackendRuntimeInfo CreateForCurrentProcess()
    {
        return new BackendRuntimeInfo(
            StartedAtUtc: DateTimeOffset.UtcNow,
            Version: ResolveVersion(typeof(Program).Assembly));
    }

    private static string ResolveVersion(Assembly assembly)
    {
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            return informationalVersion.Split('+', 2)[0];
        }

        var version = assembly.GetName().Version;
        if (version is not null)
        {
            return version.Build >= 0
                ? $"{version.Major}.{version.Minor}.{version.Build}"
                : $"{version.Major}.{version.Minor}.0";
        }

        return "dev";
    }
}
