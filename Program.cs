using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    SelfTest.Run();
    return;
}

try
{
    using HttpClient httpClient = new();
    AppSettings settings = EnvironmentParser.Load();

    foreach (DnsProfile profile in settings.Profiles)
    {
        ConsoleLogger.Global($"Setting up profile {profile.Number} (NextDNS)");

        NextDnsClient client = new(httpClient, profile);
        HostsListLoader hostsListLoader = new(httpClient);
        NextDnsRunner runner = new(client, hostsListLoader, settings);

        await runner.RunAsync();

        ConsoleLogger.Global($"Profile {profile.Number} (NextDNS) set up successfully");
    }
}
catch (ProcessException exception)
{
    ConsoleLogger.Fail(exception.Message);
    Environment.ExitCode = 1;
}
catch (Exception exception)
{
    ConsoleLogger.Fail("Unexpected exception");
    Console.WriteLine(exception);
    Environment.ExitCode = 1;
}

internal sealed class NextDnsRunner(NextDnsClient client, HostsListLoader hostsListLoader, AppSettings settings)
{
    public async Task RunAsync()
    {
        if (settings.BlockSources.Count > 0)
        {
            ConsoleLogger.Step($"Obtain block lists from {settings.BlockSources.Count} sources");
            List<string> blocks = await hostsListLoader.FetchBlocksAsync(settings.BlockSources);

            ConsoleLogger.Step("Prepare denylist");
            List<DenyDto> existingDenyList = await client.FetchDenyListAsync();
            HashSet<string> existingActiveDomains = existingDenyList
                .Where(item => item.Active)
                .Select(item => item.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            List<string> newDenyList = blocks
                .Where(domain => !existingActiveDomains.Contains(domain))
                .ToList();

            ConsoleLogger.Common($"Prepared {newDenyList.Count} domains to block");
            ConsoleLogger.Step("Save denylist");
            await NextDnsRateLimitedApiProcessor.CallApiAsync(newDenyList, domain => client.SaveDenyAsync(new CreateDenyRequest(domain)));
        }
        else
        {
            ConsoleLogger.Fail("No block sources provided");
        }

        if (settings.RedirectSources.Count > 0)
        {
            ConsoleLogger.Step($"Obtain rewrite lists from {settings.RedirectSources.Count} sources");
            List<BypassRoute> overrides = await hostsListLoader.FetchOverridesAsync(settings.RedirectSources);

            ConsoleLogger.Step("Prepare rewrites");
            Dictionary<string, CreateRewriteRequest> newRewrites = BuildNewRewrites(overrides);
            List<CreateRewriteRequest> rewritesToCreate = await CleanupOutdatedAndExcludedAsync(newRewrites);

            ConsoleLogger.Step("Save rewrites");
            await NextDnsRateLimitedApiProcessor.CallApiAsync(rewritesToCreate, client.SaveRewriteAsync);
        }
        else
        {
            ConsoleLogger.Fail("No rewrite sources provided");
        }

        if (settings.BlockSources.Count == 0 && settings.RedirectSources.Count == 0)
        {
            ConsoleLogger.Step("Remove settings");
            await RemoveAllAsync();
        }
    }

    private static Dictionary<string, CreateRewriteRequest> BuildNewRewrites(IEnumerable<BypassRoute> overrides)
    {
        Dictionary<string, CreateRewriteRequest> rewriteRequests = new(StringComparer.OrdinalIgnoreCase);

        foreach (BypassRoute route in overrides)
        {
            rewriteRequests.TryAdd(route.Website, new CreateRewriteRequest(route.Website, route.Ip));
        }

        return rewriteRequests;
    }

    private async Task<List<CreateRewriteRequest>> CleanupOutdatedAndExcludedAsync(
        Dictionary<string, CreateRewriteRequest> newRewriteRequests)
    {
        ConsoleLogger.Io("Fetching existing rewrites from NextDNS");
        List<RewriteDto> existingRewrites = await client.FetchRewritesAsync();

        List<string> outdatedIds = [];
        List<string> excludedIds = [];

        foreach (RewriteDto existingRewrite in existingRewrites)
        {
            if (settings.ShouldExcludeRedirect(existingRewrite.Name))
            {
                excludedIds.Add(existingRewrite.Id);
                newRewriteRequests.Remove(existingRewrite.Name);
                continue;
            }

            if (newRewriteRequests.TryGetValue(existingRewrite.Name, out CreateRewriteRequest? request))
            {
                if (!string.Equals(request.Content, existingRewrite.Content, StringComparison.OrdinalIgnoreCase))
                {
                    outdatedIds.Add(existingRewrite.Id);
                }
                else
                {
                    newRewriteRequests.Remove(existingRewrite.Name);
                }
            }
        }

        if (outdatedIds.Count > 0)
        {
            ConsoleLogger.Io($"Removing {outdatedIds.Count} outdated rewrites from NextDNS");
            await NextDnsRateLimitedApiProcessor.CallApiAsync(outdatedIds, client.DeleteRewriteByIdAsync);
        }

        if (excludedIds.Count > 0)
        {
            ConsoleLogger.Io($"Removing {excludedIds.Count} excluded rewrites from NextDNS");
            await NextDnsRateLimitedApiProcessor.CallApiAsync(excludedIds, client.DeleteRewriteByIdAsync);
        }

        foreach (string excludedDomain in newRewriteRequests.Keys.Where(settings.ShouldExcludeRedirect).ToList())
        {
            newRewriteRequests.Remove(excludedDomain);
        }

        return [.. newRewriteRequests.Values];
    }

    private async Task RemoveAllAsync()
    {
        ConsoleLogger.Io("Fetching existing denylist from NextDNS");
        List<DenyDto> denyList = await client.FetchDenyListAsync();
        ConsoleLogger.Io("Removing denylist from NextDNS");
        await NextDnsRateLimitedApiProcessor.CallApiAsync(denyList.Select(item => item.Id).ToList(), client.DeleteDenyByIdAsync);

        ConsoleLogger.Io("Fetching existing rewrites from NextDNS");
        List<RewriteDto> rewrites = await client.FetchRewritesAsync();
        ConsoleLogger.Io("Removing rewrites from NextDNS");
        await NextDnsRateLimitedApiProcessor.CallApiAsync(rewrites.Select(item => item.Id).ToList(), client.DeleteRewriteByIdAsync);
    }
}

internal sealed class NextDnsClient(HttpClient httpClient, DnsProfile profile)
{
    private const int MaxTransientAttempts = 3;
    private static readonly TimeSpan TransientRetryDelay = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private string ApiUrl => $"https://api.nextdns.io/profiles/{profile.ClientId}";

    public async Task<List<DenyDto>> FetchDenyListAsync()
    {
        NextDnsResponse<List<DenyDto>>? response = await SendAsync<NextDnsResponse<List<DenyDto>>>(HttpMethod.Get, "/denylist");
        return response?.Data ?? [];
    }

    public Task<NextDnsResponse<DenyDto>?> SaveDenyAsync(CreateDenyRequest request)
    {
        return SendAsync<NextDnsResponse<DenyDto>>(HttpMethod.Post, "/denylist", request);
    }

    public Task<NextDnsResponse<DenyDto>?> DeleteDenyByIdAsync(string id)
    {
        return SendAsync<NextDnsResponse<DenyDto>>(HttpMethod.Delete, $"/denylist/{Uri.EscapeDataString(id)}");
    }

    public async Task<List<RewriteDto>> FetchRewritesAsync()
    {
        NextDnsResponse<List<RewriteDto>>? response = await SendAsync<NextDnsResponse<List<RewriteDto>>>(HttpMethod.Get, "/rewrites");
        return response?.Data ?? [];
    }

    public Task<NextDnsResponse<RewriteDto>?> SaveRewriteAsync(CreateRewriteRequest request)
    {
        return SendAsync<NextDnsResponse<RewriteDto>>(HttpMethod.Post, "/rewrites", request);
    }

    public Task<NextDnsResponse<RewriteDto>?> DeleteRewriteByIdAsync(string id)
    {
        return SendAsync<NextDnsResponse<RewriteDto>>(HttpMethod.Delete, $"/rewrites/{Uri.EscapeDataString(id)}");
    }

    private async Task<TResponse?> SendAsync<TResponse>(HttpMethod method, string path, object? body = null)
        where TResponse : NextDnsResponseBase
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using HttpRequestMessage request = CreateRequest(method, path, body);
                using HttpResponseMessage response = await httpClient.SendAsync(request);
                string responseBody = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    NextDnsHttpException httpException = new(response.StatusCode, responseBody);
                    switch (response.StatusCode)
                    {
                        case HttpStatusCode.Unauthorized:
                        case HttpStatusCode.Forbidden:
                            throw new ProcessException("Invalid NextDNS API key");
                        case HttpStatusCode.NotFound:
                            throw new ProcessException("Make sure AUTH_SECRET and CLIENT_ID belong to the same NextDNS account");
                        default:
                            throw httpException;
                    }
                }

                if (string.IsNullOrWhiteSpace(responseBody))
                {
                    return null;
                }

                return JsonSerializer.Deserialize<TResponse>(responseBody, JsonOptions);
            }
            catch (Exception exception) when (IsTransientNetworkException(exception))
            {
                if (attempt >= MaxTransientAttempts)
                {
                    throw new ProcessException(
                        $"NextDNS request failed after {MaxTransientAttempts} attempts: {method} {path}. {exception.Message}");
                }

                ConsoleLogger.Common(
                    $"NextDNS request failed: {method} {path}. Retrying in {TransientRetryDelay.TotalSeconds:N0} seconds ({attempt}/{MaxTransientAttempts})");
                await Task.Delay(TransientRetryDelay);
            }
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path, object? body)
    {
        Uri uri = new(ApiUrl + path);
        HttpRequestMessage request = new(method, uri);
        request.Headers.Add("X-Api-Key", profile.AuthSecret);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        return request;
    }

    private static bool IsTransientNetworkException(Exception exception)
    {
        return exception is HttpRequestException or TaskCanceledException;
    }
}

internal static class NextDnsRateLimitedApiProcessor
{
    public static async Task CallApiAsync<TRequest, TResponse>(
        IReadOnlyCollection<TRequest> requests,
        Func<TRequest, Task<TResponse?>> callApi)
        where TResponse : NextDnsResponseBase
    {
        const int waitSeconds = 60;
        Queue<TRequest> queue = new(requests);
        int successCounter = 0;
        int waveCounter = 0;

        while (queue.Count > 0)
        {
            TRequest request = queue.Dequeue();

            try
            {
                NextDnsResponseBase? response = await callApi(request);
                if (response?.Errors is { Count: > 0 })
                {
                    ConsoleLogger.Fail("Failed request: " + string.Join(", ", response.Errors.Select(error => error.Code)));
                    continue;
                }

                successCounter++;
                waveCounter++;
                ConsoleLogger.Progress($"Current success progress: {successCounter}/{requests.Count}");
            }
            catch (NextDnsHttpException exception) when (exception.StatusCode is HttpStatusCode.TooManyRequests or (HttpStatusCode)524)
            {
                queue.Enqueue(request);
                ConsoleLogger.Common($"Sending speed: {waveCounter / 60.0:F2} requests per second");
                ConsoleLogger.Common($"Code {(int)exception.StatusCode}. API rate limit has reached");
                await RunResetWaitTimerAsync(waitSeconds);
                ConsoleLogger.Io("Continue...");
                waveCounter = 0;
            }
        }

        ConsoleLogger.Common("Completed");
    }

    private static async Task RunResetWaitTimerAsync(int seconds)
    {
        for (int timer = seconds; timer > 0; timer--)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            ConsoleLogger.Progress($"Waiting for reset: {timer} seconds");
        }
    }
}

internal sealed class HostsListLoader(HttpClient httpClient)
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly HashSet<string> BlockIps = new(StringComparer.OrdinalIgnoreCase)
    {
        "0.0.0.0",
        "127.0.0.1",
        "::1"
    };

    private static readonly HashSet<string> LocalhostNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "localhost",
        "ip6-localhost"
    };

    public async Task<List<string>> FetchBlocksAsync(IReadOnlyCollection<string> urls)
    {
        List<HostsLine> lines = await FetchHostsLinesAsync(urls, "Block");
        HashSet<string> seenDomains = new(StringComparer.OrdinalIgnoreCase);
        List<string> domains = [];

        foreach (HostsLine line in lines)
        {
            if (IsBlockIp(line.Ip) &&
                line.Domain is not null &&
                !LocalhostNames.Contains(line.Domain) &&
                seenDomains.Add(line.Domain))
            {
                domains.Add(line.Domain);
            }
        }

        return domains;
    }

    public async Task<List<BypassRoute>> FetchOverridesAsync(IReadOnlyCollection<string> urls)
    {
        List<HostsLine> lines = await FetchHostsLinesAsync(urls, "Override");
        HashSet<string> seenDomains = new(StringComparer.OrdinalIgnoreCase);
        List<BypassRoute> routes = [];

        foreach (HostsLine line in lines)
        {
            if (line.Ip is not null &&
                !IsBlockIp(line.Ip) &&
                line.Domain is not null &&
                seenDomains.Add(line.Domain))
            {
                routes.Add(new BypassRoute(line.Ip, line.Domain));
            }
        }

        return routes;
    }

    private async Task<List<HostsLine>> FetchHostsLinesAsync(IReadOnlyCollection<string> urls, string listType)
    {
        Task<string>[] tasks = urls.Select(url => FetchListAsync(url, listType)).ToArray();
        string[] rawLists = await Task.WhenAll(tasks);

        return rawLists
            .SelectMany(SplitByEol)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Where(line => !line.StartsWith('#'))
            .Select(line => ParseHostsLine(line.ToLowerInvariant()))
            .OfType<HostsLine>()
            .ToList();
    }

    private async Task<string> FetchListAsync(string url, string listType)
    {
        ConsoleLogger.Io($"Loading {listType} list from url: {url}");
        return await httpClient.GetStringAsync(url);
    }

    public static HostsLine? ParseHostsLine(string line)
    {
        string sanitizedLine = StripInlineComment(line);
        if (string.IsNullOrWhiteSpace(sanitizedLine))
        {
            return null;
        }

        string[] columns = Whitespace.Split(sanitizedLine.Trim(), 3);
        if (columns.Length == 1)
        {
            string value = columns[0];
            return IsValidIp(value) ? new HostsLine(value, null) : new HostsLine(null, RemoveWww(value));
        }

        if (columns.Length == 2)
        {
            string ip = columns[0];
            string domain = RemoveWww(columns[1]);
            return IsValidIp(ip) ? new HostsLine(ip, domain) : null;
        }

        ConsoleLogger.Fail("Failed to parse hosts line: " + line);
        return null;
    }

    public static string RemoveWww(string domain)
    {
        return domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? domain["www.".Length..] : domain;
    }

    public static bool IsBlockIp(string? ip)
    {
        return ip is not null && BlockIps.Contains(ip);
    }

    private static bool IsValidIp(string ip)
    {
        return IPAddress.TryParse(ip, out _);
    }

    private static string StripInlineComment(string line)
    {
        int commentIndex = line.IndexOf('#', StringComparison.Ordinal);
        return commentIndex >= 0 ? line[..commentIndex].Trim() : line;
    }

    private static IEnumerable<string> SplitByEol(string data)
    {
        return data.Split(["\r\n", "\n"], StringSplitOptions.None);
    }
}

internal static class EnvironmentParser
{
    public static AppSettings Load()
    {
        List<string> clientIds = ParseRequiredCsv("CLIENT_ID");
        List<string> secrets = ParseRequiredCsv("AUTH_SECRET");

        if (clientIds.Count != secrets.Count)
        {
            throw new ProcessException(
                $"CLIENT_ID values amount and AUTH_SECRET values amount must be equal, but were {clientIds.Count} and {secrets.Count}");
        }

        List<DnsProfile> profiles = [];
        for (int i = 0; i < clientIds.Count; i++)
        {
            profiles.Add(new DnsProfile(i + 1, clientIds[i], secrets[i]));
        }

        List<string> excludedRedirects = ParseCsv(Environment.GetEnvironmentVariable("EXCLUDE_REDIRECT"))
            .Select(domain => HostsListLoader.RemoveWww(domain.Trim().ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new AppSettings(
            profiles,
            ParseCsv(Environment.GetEnvironmentVariable("BLOCK")),
            ParseCsv(Environment.GetEnvironmentVariable("REDIRECT")),
            excludedRedirects);
    }

    public static List<string> ParseCsv(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => item.Length > 0)
            .ToList();
    }

    private static List<string> ParseRequiredCsv(string key)
    {
        List<string> values = ParseCsv(Environment.GetEnvironmentVariable(key));
        if (values.Count == 0)
        {
            throw new ProcessException($"Mandatory environment variable is not provided: {key}");
        }

        return values;
    }
}

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

internal abstract class NextDnsResponseBase
{
    public List<NextDnsApiError>? Errors { get; set; }
}

internal sealed class NextDnsResponse<T> : NextDnsResponseBase
{
    public T? Data { get; set; }
}

internal sealed record NextDnsApiError(string Code);

internal sealed class ProcessException(string message) : Exception(message);

internal sealed class NextDnsHttpException(HttpStatusCode statusCode, string responseBody)
    : Exception($"NextDNS HTTP error {(int)statusCode}: {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

internal static class ConsoleLogger
{
    public static void Global(string message) => Console.WriteLine($"\n=== {message} ===");

    public static void Step(string message) => Console.WriteLine($"\n> {message}");

    public static void Io(string message) => Console.WriteLine(message);

    public static void Common(string message) => Console.WriteLine(message);

    public static void Progress(string message) => Console.WriteLine(message);

    public static void Fail(string message) => Console.Error.WriteLine(message);
}

internal static class SelfTest
{
    public static void Run()
    {
        HostsLine? block = HostsListLoader.ParseHostsLine("0.0.0.0 www.example.com # comment");
        Assert(block is { Ip: "0.0.0.0", Domain: "example.com" }, "block hosts parsing failed");

        HostsLine? redirect = HostsListLoader.ParseHostsLine("1.2.3.4 www.redirect.test");
        Assert(redirect is { Ip: "1.2.3.4", Domain: "redirect.test" }, "redirect hosts parsing failed");

        HostsLine? comment = HostsListLoader.ParseHostsLine("# comment");
        Assert(comment is null, "comment parsing failed");

        List<string> values = EnvironmentParser.ParseCsv("first, second,,third");
        Assert(values.SequenceEqual(["first", "second", "third"]), "CSV parsing failed");

        AppSettings settings = new([], [], [], ["example.com"]);
        Assert(settings.ShouldExcludeRedirect("sub.example.com"), "subdomain exclusion failed");

        Console.WriteLine("Self-test completed successfully");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new ProcessException(message);
        }
    }
}
