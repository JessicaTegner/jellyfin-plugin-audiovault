using System.Text;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>Runs ahead of Jellyfin's own pipeline (including its base URL mapping and static web files).</summary>
public sealed class AccountMenuLinkStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.UseMiddleware<AccountMenuLinkMiddleware>();
        next(app);
    };
}

/// <summary>
/// Serves the web client's config.json with an "AudioVault account" side-menu link added, so users who can't see
/// plugin pages can still reach the account page. Anything unexpected falls through to the real file.
/// </summary>
public sealed class AccountMenuLinkMiddleware(
    RequestDelegate next,
    IServerConfigurationManager config,
    ILogger<AccountMenuLinkMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        HookSignals.MiddlewareRan();
        var body = (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
            && Plugin.Instance?.Configuration.ShowAccountMenuLink == true
                ? await TryBuildAsync(context).ConfigureAwait(false)
                : null;
        if (body is null)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.ContentLength = bytes.Length;
        if (!HttpMethods.IsHead(context.Request.Method))
        {
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted).ConfigureAwait(false);
        }
    }

    private async Task<string?> TryBuildAsync(HttpContext context)
    {
        try
        {
            var basePath = WebConfigMenuLink.NormalizeBasePath(config.GetNetworkConfiguration().BaseUrl);
            var path = (context.Request.PathBase + context.Request.Path).Value ?? string.Empty;
            if (!WebConfigMenuLink.IsConfigJsonPath(path, basePath))
            {
                return null;
            }

            var file = Path.Combine(config.ApplicationPaths.WebPath, "config.json");
            if (!File.Exists(file))
            {
                return null;
            }

            var original = await File.ReadAllTextAsync(file, context.RequestAborted).ConfigureAwait(false);
            var result = WebConfigMenuLink.AddLink(original, WebConfigMenuLink.AccountUrl(basePath));
            if (result is null)
            {
                logger.LogWarning("The web client's config.json isn't in the expected format; not adding the AudioVault account menu link");
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not add the AudioVault account menu link to the web client's config.json");
            return null;
        }
    }
}
