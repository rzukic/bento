using System.IO.Compression;
using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Bento.Api.Configuration;
using Bento.Api.Services.Mcp;

namespace Bento.Api.Controllers;

/// <summary>
/// The Claude Desktop extension: a <c>.mcpb</c> bundle with a small stdio bridge that
/// forwards to <c>/mcp</c> with an API key, its address set to this installation.
/// Public on purpose (a plain download link): it holds no secret; the key is entered
/// in Claude Desktop's extension settings.
/// </summary>
[ApiController]
[Route(ApiRoutes.McpBundle)]
public sealed class McpBundleController : ControllerBase
{
    private const string ResourcePrefix = "Bento.Api.McpBundle.";

    private readonly BackendRuntimeOptions _runtimeOptions;

    public McpBundleController(BackendRuntimeOptions runtimeOptions)
    {
        _runtimeOptions = runtimeOptions;
    }

    [HttpGet]
    public IActionResult Download()
    {
        var baseUrl = PublicUrl.For(Request, _runtimeOptions);
        var version = typeof(McpBundleController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "1.0.0";
        var manifest = ReadText("manifest.json")
            .Replace("{{VERSION}}", version)
            .Replace("{{BASE_URL}}", baseUrl)
            .Replace("{{MCP_URL}}", PublicUrl.McpEndpoint(Request, _runtimeOptions));

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "manifest.json", System.Text.Encoding.UTF8.GetBytes(manifest));
            Add(zip, "icon.png", ReadBytes("icon.png"));
            Add(zip, "server/index.js", ReadBytes("server.index.js"));
        }

        Response.Headers.CacheControl = "no-cache";
        return File(buffer.ToArray(), "application/zip", "bento.mcpb");
    }

    private static void Add(ZipArchive zip, string path, byte[] content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(content);
    }

    private static byte[] ReadBytes(string name)
    {
        using var stream = typeof(McpBundleController).Assembly.GetManifestResourceStream(ResourcePrefix + name)
            ?? throw new InvalidOperationException($"Bundle file {name} is missing from the build.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static string ReadText(string name) => System.Text.Encoding.UTF8.GetString(ReadBytes(name));
}
