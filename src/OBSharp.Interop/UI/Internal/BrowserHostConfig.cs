namespace OBSharp.Interop.UI.Internal;

/// <summary>
/// 浏览器实例解析后的宿主配置。
/// </summary>
internal sealed class BrowserHostConfig
{
    public string Host = "";
    public string Scheme = "https";
    public string Domain = "example.com";
    public Endpoint? Endpoint;
    public string Url = "";

    /// <summary>
    /// 是否注册 Scheme Handler 并建立 IPC 连接。
    /// </summary>
    public bool HasEndpoint;
}

internal static class BrowserHostConfigResolver
{
    public static bool TryResolve(
        string? url,
        string? scheme,
        string? domain,
        string? endpoint,
        OBSharpEndpointKind endpointKind,
        string? host,
        out BrowserHostConfig config,
        out string? error)
    {
        domain ??= "example.com";
        scheme = string.IsNullOrEmpty(scheme) ? "https" : scheme;
        url = string.IsNullOrEmpty(url) ? null : url;
        endpoint = string.IsNullOrEmpty(endpoint) ? null : endpoint;
        host ??= "";

        config = new() { Scheme = scheme };
        error = null;

        string urlHost = "", urlRest = "";

        if (url is not null && UrlMapping.HasObsharpScheme(url) &&
            !UrlMapping.TrySplitObsharpUrl(url, out urlHost, out urlRest))
        {
            error = "malformed obsharp URL: " + url;
            return false;
        }

        if (host.Length is 0)
            host = urlHost;
        else if (urlHost.Length is not 0 &&
                 !string.Equals(host, urlHost, StringComparison.Ordinal))
        {
            error = $"url host \"{urlHost}\" does not match options->host \"{host}\"";
            return false;
        }

        if (host.Length is not 0)
        {
            if (!UrlMapping.IsValidHostName(host))
            {
                error = $"invalid host name \"{host}\": expected ^[a-z0-9]([a-z0-9-]{{0,46}}[a-z0-9])?$";
                return false;
            }

            config.Host = host;
            config.Domain = UrlMapping.HostDomain(host);
        }
        else
        {
            config.Domain = domain;
        }

        if (endpoint is not null)
        {
            config.Endpoint = new() { Kind = endpointKind, Address = endpoint };
            config.HasEndpoint = true;
        }
        else if (config.Host.Length is not 0)
        {
            config.Endpoint = new()
            {
                Kind = OBSharpEndpointKind.Auto,
                Address = EndpointResolver.DefaultEndpointForHost(config.Host)
            };
            config.HasEndpoint = true;
        }

        if (url is null)
        {
            if (config.Host.Length is 0)
                config.Url = $"{config.Scheme}://{config.Domain}/";
            else
                config.Url = UrlMapping.MapToCefUrl(config.Scheme, config.Host, "");
        }
        else if (UrlMapping.HasObsharpScheme(url))
        {
            config.Url = UrlMapping.MapToCefUrl(config.Scheme, config.Host, urlRest);
        }
        else
        {
            config.Url = url;
        }

        return true;
    }
}
