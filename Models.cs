internal sealed record AppSettings(
    IReadOnlyList<DnsProfile> Profiles,
    IReadOnlyList<string> BlockSources,
    IReadOnlyList<string> RedirectSources,
    IReadOnlyList<string> ExcludedRedirects)
{
    public bool ShouldExcludeRedirect(string domain)
    {
        return ExcludedRedirects.Any(excluded => domain.EndsWith(excluded, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed record DnsProfile(int Number, string ClientId, string AuthSecret);

internal sealed record HostsLine(string? Ip, string? Domain);

internal sealed record BypassRoute(string Ip, string Website);

internal sealed record CreateDenyRequest(string Id)
{
    public bool Active { get; init; } = true;
}

internal sealed record CreateRewriteRequest(string Name, string Content);

internal sealed record DenyDto(string Id, bool Active);

internal sealed record RewriteDto(string Id, string Name, string Content);

internal sealed record NextDnsApiError(string Code, string? List);

internal abstract class NextDnsResponseBase
{
    public List<NextDnsApiError>? Errors { get; set; }
}

internal sealed class NextDnsResponse<T> : NextDnsResponseBase
{
    public T? Data { get; set; }
}
