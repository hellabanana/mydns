using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

if (args.Contains("--self-test", StringComparer.OrdinalIgnoreCase))
{
    await SelfTest.RunAsync();
    return;
}

try
{
    using HttpClient httpClient = new();
    AppSettings settings = EnvironmentParser.Load();
    const int maxParallelProfiles = 3;
    int parallelProfiles = Math.Min(settings.Profiles.Count, maxParallelProfiles);

    ConsoleLogger.Common($"Processing {settings.Profiles.Count} profile(s), up to {parallelProfiles} in parallel");

    using SemaphoreSlim profileSemaphore = new(parallelProfiles);
    Task[] profileTasks = settings.Profiles
        .Select(profile => RunProfileAsync(profile, httpClient, settings, profileSemaphore))
        .ToArray();

    await Task.WhenAll(profileTasks);
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

static async Task RunProfileAsync(
    DnsProfile profile,
    HttpClient httpClient,
    AppSettings settings,
    SemaphoreSlim profileSemaphore)
{
    await profileSemaphore.WaitAsync();
    try
    {
        ConsoleLogger.Global($"Setting up profile {profile.Number} (NextDNS)");

        NextDnsClient client = new(httpClient, profile);
        HostsListLoader hostsListLoader = new(httpClient);
        NextDnsRunner runner = new(client, hostsListLoader, settings);

        await runner.RunAsync();

        ConsoleLogger.Global($"Profile {profile.Number} (NextDNS) set up successfully");
    }
    finally
    {
        profileSemaphore.Release();
    }
}

internal sealed class NextDnsRunner(NextDnsClient client, HostsListLoader hostsListLoader, AppSettings settings)
{
    private const int RewriteLimit = 500;

    public async Task RunAsync()
    {
        List<RewriteDto>? existingRewrites = null;
        if (settings.ExcludedRedirects.Count > 0)
        {
            ConsoleLogger.Step("Apply rewrite exclusions");
            existingRewrites = await FetchExistingRewritesAsync();
            await RemoveExcludedRewritesAsync(existingRewrites);
        }

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
            HashSet<string> requiredNames = FindRequiredRewriteNames(newRewrites.Keys);
            existingRewrites ??= await FetchExistingRewritesAsync();
            List<CreateRewriteRequest> rewritesToCreate = await PrepareRewritesAsync(newRewrites, requiredNames, existingRewrites);

            ConsoleLogger.Step("Save rewrites");
            List<CreateRewriteRequest> requiredRewrites = rewritesToCreate
                .Where(request => requiredNames.Contains(request.Name))
                .ToList();
            if (requiredRewrites.Count > 0)
            {
                int saved = await NextDnsRateLimitedApiProcessor.CallApiAsync(requiredRewrites, client.SaveRewriteAsync);
                if (saved != requiredRewrites.Count)
                {
                    throw new ProcessException($"Profile {client.ProfileNumber}: NextDNS saved only {saved} of {requiredRewrites.Count} required rewrites");
                }
            }

            await NextDnsRateLimitedApiProcessor.CallApiAsync(
                rewritesToCreate.Skip(requiredRewrites.Count).ToList(), client.SaveRewriteAsync);
        }
        else
        {
            ConsoleLogger.Fail("No rewrite sources provided");
        }

        if (settings.BlockSources.Count == 0 && settings.RedirectSources.Count == 0 && settings.ExcludedRedirects.Count == 0)
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

    private HashSet<string> FindRequiredRewriteNames(IReadOnlyCollection<string> sourceNames)
    {
        HashSet<string> requiredNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string pattern in settings.RequiredRedirects)
        {
            Regex matcher = BuildRequiredRedirectMatcher(pattern);
            List<string> matches = sourceNames
                .Where(name => matcher.IsMatch(name) && !settings.ShouldExcludeRedirect(name))
                .ToList();
            if (matches.Count == 0)
            {
                throw new ProcessException($"Profile {client.ProfileNumber}: REQUIRED_REDIRECT pattern '{pattern}' matched no domains allowed by EXCLUDE_REDIRECT in the REDIRECT sources");
            }

            requiredNames.UnionWith(matches);
        }

        return requiredNames;
    }

    internal static Regex BuildRequiredRedirectMatcher(string pattern)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        if (pattern == "*")
        {
            return new Regex(@".+", options);
        }

        bool anyPrefix = pattern.StartsWith("*.", StringComparison.Ordinal);
        bool anySuffix = pattern.EndsWith(".*", StringComparison.Ordinal);
        int start = anyPrefix ? 2 : 0;
        int end = anySuffix ? pattern.Length - 2 : pattern.Length;
        string domain = end > start ? pattern[start..end] : "";
        if (domain.Length == 0 || domain.Contains('*') || domain.Any(char.IsWhiteSpace))
        {
            throw new ProcessException($"Invalid REQUIRED_REDIRECT pattern '{pattern}'. Use an exact domain, *.example.com, or *.example.*");
        }

        string prefix = anyPrefix ? @"(?:[^.]+\.)*" : "";
        string suffix = anySuffix ? @"(?:\.[^.]+)+" : "";
        return new Regex(@"\A" + prefix + Regex.Escape(domain) + suffix + @"\z", options);
    }

    private Task<List<RewriteDto>> FetchExistingRewritesAsync()
    {
        ConsoleLogger.Io("Fetching existing rewrites from NextDNS");
        return client.FetchRewritesAsync();
    }

    private async Task RemoveExcludedRewritesAsync(List<RewriteDto> existingRewrites)
    {
        List<string> excludedIds = existingRewrites
            .Where(rewrite => settings.ShouldExcludeRedirect(rewrite.Name))
            .Select(rewrite => rewrite.Id)
            .ToList();
        if (excludedIds.Count == 0)
        {
            return;
        }

        ConsoleLogger.Io($"Removing {excludedIds.Count} excluded rewrites from NextDNS");
        int removedCount = await NextDnsRateLimitedApiProcessor.CallApiAsync(excludedIds, client.DeleteRewriteByIdAsync);
        if (removedCount != excludedIds.Count)
        {
            throw new ProcessException($"Profile {client.ProfileNumber}: NextDNS failed to remove one or more excluded rewrites");
        }

        existingRewrites.RemoveAll(rewrite => settings.ShouldExcludeRedirect(rewrite.Name));
    }

    private async Task<List<CreateRewriteRequest>> PrepareRewritesAsync(
        Dictionary<string, CreateRewriteRequest> newRewriteRequests,
        IReadOnlySet<string> requiredNames,
        IReadOnlyCollection<RewriteDto> existingRewrites)
    {
        List<string> outdatedIds = [];
        HashSet<string> outdatedNames = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> matchingNames = new(StringComparer.OrdinalIgnoreCase);

        foreach (RewriteDto existingRewrite in existingRewrites)
        {
            if (newRewriteRequests.TryGetValue(existingRewrite.Name, out CreateRewriteRequest? request))
            {
                if (!string.Equals(request.Content, existingRewrite.Content, StringComparison.OrdinalIgnoreCase))
                {
                    outdatedIds.Add(existingRewrite.Id);
                    outdatedNames.Add(existingRewrite.Name);
                }
                else
                {
                    matchingNames.Add(existingRewrite.Name);
                }
            }
        }

        List<CreateRewriteRequest> pendingRewrites = newRewriteRequests.Values
            .Where(request => !matchingNames.Contains(request.Name) && !settings.ShouldExcludeRedirect(request.Name))
            .OrderByDescending(request => requiredNames.Contains(request.Name))
            .ThenByDescending(request => outdatedNames.Contains(request.Name))
            .ToList();

        int availableSlots = Math.Max(0, RewriteLimit - (existingRewrites.Count - outdatedIds.Count));
        int optionalReplacements = pendingRewrites.Count(request =>
            outdatedNames.Contains(request.Name) && !requiredNames.Contains(request.Name));
        List<string> requiredWithoutSlots = pendingRewrites.Where(request => requiredNames.Contains(request.Name))
            .Skip(Math.Max(0, availableSlots - optionalReplacements))
            .Select(request => request.Name)
            .ToList();
        if (requiredWithoutSlots.Count > 0)
        {
            throw new ProcessException($"Profile {client.ProfileNumber}: Free {requiredWithoutSlots.Count} NextDNS rewrite slot(s) for required domains: " +
                string.Join(", ", requiredWithoutSlots));
        }

        if (outdatedIds.Count > 0)
        {
            ConsoleLogger.Io($"Removing {outdatedIds.Count} outdated rewrites from NextDNS");
            int removedCount = await NextDnsRateLimitedApiProcessor.CallApiAsync(outdatedIds, client.DeleteRewriteByIdAsync);
            if (removedCount != outdatedIds.Count)
            {
                throw new ProcessException($"Profile {client.ProfileNumber}: NextDNS failed to remove one or more outdated rewrites");
            }
        }

        if (pendingRewrites.Count > availableSlots)
        {
            ConsoleLogger.Warn($"Profile {client.ProfileNumber}: NextDNS rewrite limit is {RewriteLimit}. " +
                $"Saving {availableSlots} of {pendingRewrites.Count} pending rewrites and deferring {pendingRewrites.Count - availableSlots}.");
        }

        return pendingRewrites.Take(availableSlots).ToList();
    }

    private async Task RemoveAllAsync()
    {
        ConsoleLogger.Io("Fetching existing denylist from NextDNS");
        List<DenyDto> denyList = await client.FetchDenyListAsync();
        ConsoleLogger.Io("Removing denylist from NextDNS");
        await NextDnsRateLimitedApiProcessor.CallApiAsync(denyList.Select(item => item.Id).ToList(), client.DeleteDenyByIdAsync);

        List<RewriteDto> rewrites = await FetchExistingRewritesAsync();
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
    public int ProfileNumber => profile.Number;

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

    public async Task<NextDnsResponse<RewriteDto>?> SaveRewriteAsync(CreateRewriteRequest request)
    {
        try
        {
            return await SendAsync<NextDnsResponse<RewriteDto>>(HttpMethod.Post, "/rewrites", request);
        }
        catch (NextDnsHttpException exception) when (exception.IsRewriteLimitExceeded())
        {
            throw new ProcessException($"Profile {ProfileNumber}: NextDNS reached its rewrite limit while saving {request.Name}. " +
                "Rerun the sync to recalculate available slots.");
        }
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
                            if (IsTransientHttpStatusCode(response.StatusCode))
                            {
                                if (attempt >= MaxTransientAttempts)
                                {
                                    throw new ProcessException(
                                        $"NextDNS HTTP request failed after {MaxTransientAttempts} attempts: {method} {path}. Last response: {(int)response.StatusCode}: {responseBody}");
                                }

                                ConsoleLogger.Common(
                                    $"NextDNS HTTP error {(int)response.StatusCode}: {method} {path}. Retrying in {TransientRetryDelay.TotalSeconds:N0} seconds ({attempt}/{MaxTransientAttempts})");
                                await Task.Delay(TransientRetryDelay);
                                continue;
                            }

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

    private static bool IsTransientHttpStatusCode(HttpStatusCode statusCode)
    {
        int code = (int)statusCode;
        return code is >= 500 and <= 599 and not 524;
    }
}

internal static class NextDnsRateLimitedApiProcessor
{
    public static async Task<int> CallApiAsync<TRequest, TResponse>(
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
        return successCounter;
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
        Dictionary<string, string> fileSettings = ReadDotEnv();
        string? ReadSetting(string key) =>
            Environment.GetEnvironmentVariable(key) ?? fileSettings.GetValueOrDefault(key);

        List<string> clientIds = ParseRequiredCsv("CLIENT_ID", ReadSetting("CLIENT_ID"));
        List<string> secrets = ParseRequiredCsv("AUTH_SECRET", ReadSetting("AUTH_SECRET"));

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

        List<string> excludedRedirects = ParseCsv(ReadSetting("EXCLUDE_REDIRECT"))
            .Select(domain => HostsListLoader.RemoveWww(domain.Trim().ToLowerInvariant()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        HashSet<string> requiredRedirects = ParseCsv(ReadSetting("REQUIRED_REDIRECT"))
            .Select(pattern => pattern.Contains('*') ? pattern : HostsListLoader.RemoveWww(pattern))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> redirectSources = ParseCsv(ReadSetting("REDIRECT"));
        if (requiredRedirects.Count > 0 && redirectSources.Count == 0)
        {
            throw new ProcessException("REQUIRED_REDIRECT requires at least one REDIRECT source");
        }

        return new AppSettings(
            profiles,
            ParseCsv(ReadSetting("BLOCK")),
            redirectSources,
            excludedRedirects,
            requiredRedirects);
    }

    private static Dictionary<string, string> ReadDotEnv()
    {
        const string fileName = ".env";
        Dictionary<string, string> settings = new(StringComparer.Ordinal);
        if (!File.Exists(fileName))
        {
            return settings;
        }

        foreach (string rawLine in File.ReadLines(fileName))
        {
            string line = rawLine.Trim();
            int separator = line.IndexOf('=');
            if (separator <= 0 || line.StartsWith('#'))
            {
                continue;
            }

            string key = line[..separator].TrimEnd();
            string value = line[(separator + 1)..].Trim();
            if (value.Length >= 2 && (value[0] is '\'' or '"') && value[^1] == value[0])
            {
                value = value[1..^1];
            }

            settings[key] = value;
        }

        return settings;
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

    private static List<string> ParseRequiredCsv(string key, string? value)
    {
        List<string> values = ParseCsv(value);
        if (values.Count == 0)
        {
            throw new ProcessException($"Mandatory environment variable is not provided: {key}");
        }

        return values;
    }
}

internal sealed class ProcessException(string message) : Exception(message);

internal sealed class NextDnsHttpException(HttpStatusCode statusCode, string responseBody)
    : Exception($"NextDNS HTTP error {(int)statusCode}: {responseBody}")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public bool IsRewriteLimitExceeded()
    {
        if (StatusCode != HttpStatusCode.BadRequest)
        {
            return false;
        }

        NextDnsResponse<RewriteDto>? response = JsonSerializer.Deserialize<NextDnsResponse<RewriteDto>>(
            responseBody, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return response?.Errors?.Any(error => error is { Code: "listLimitExceeded", List: "rewrites" }) == true;
    }
}

internal static class ConsoleLogger
{
    public static void Global(string message) => Console.WriteLine($"\n=== {message} ===");

    public static void Step(string message) => Console.WriteLine($"\n> {message}");

    public static void Io(string message) => Console.WriteLine(message);

    public static void Common(string message) => Console.WriteLine(message);

    public static void Progress(string message) => Console.WriteLine(message);

    public static void Warn(string message) => Console.WriteLine($"Warning: {message}");

    public static void Fail(string message) => Console.Error.WriteLine(message);
}

internal static class SelfTest
{
    public static async Task RunAsync()
    {
        HostsLine? block = HostsListLoader.ParseHostsLine("0.0.0.0 www.example.com # comment");
        Assert(block is { Ip: "0.0.0.0", Domain: "example.com" }, "block hosts parsing failed");

        HostsLine? redirect = HostsListLoader.ParseHostsLine("1.2.3.4 www.redirect.test");
        Assert(redirect is { Ip: "1.2.3.4", Domain: "redirect.test" }, "redirect hosts parsing failed");

        HostsLine? comment = HostsListLoader.ParseHostsLine("# comment");
        Assert(comment is null, "comment parsing failed");

        List<string> values = EnvironmentParser.ParseCsv("first, second,,third");
        Assert(values.SequenceEqual(["first", "second", "third"]), "CSV parsing failed");

        AppSettings settings = new([], [], [], ["example.com", "rutor"], new HashSet<string>());
        Assert(settings.ShouldExcludeRedirect("sub.example.com"), "subdomain exclusion failed");
        Assert(settings.ShouldExcludeRedirect("RuTor.info") && settings.ShouldExcludeRedirect("tracker.new-rutor.is"),
            "fragment exclusion failed");
        Assert(!settings.ShouldExcludeRedirect("example.com.other") && !settings.ShouldExcludeRedirect("unrelated.test"),
            "exclusion matched an unrelated domain");

        Regex openAiPattern = NextDnsRunner.BuildRequiredRedirectMatcher("*.openai.*");
        Assert(openAiPattern.IsMatch("OPENAI.com") && openAiPattern.IsMatch("android.chat.openai.com") &&
            openAiPattern.IsMatch("api.openai.net") && !openAiPattern.IsMatch("notopenai.com") &&
            !openAiPattern.IsMatch("openai-example.com"), "required redirect pattern matching failed");
        Regex openAiComPattern = NextDnsRunner.BuildRequiredRedirectMatcher("*.openai.com");
        Assert(openAiComPattern.IsMatch("openai.com") && openAiComPattern.IsMatch("android.chat.openai.com") &&
            !openAiComPattern.IsMatch("openai.com.example"), "required redirect suffix matching failed");

        await TestRewriteLimitAsync();
        await TestExclusionsWithoutSourcesAsync();
        Console.WriteLine("Self-test completed successfully");
    }

    private static async Task TestRewriteLimitAsync()
    {
        List<RewriteDto> existingRewrites = Enumerable.Range(0, 498)
            .Select(index => new RewriteDto($"keep-{index}", $"keep-{index}.test", "1.2.3.4"))
            .ToList();
        existingRewrites.Add(new RewriteDto("update", "update.test", "1.2.3.4"));
        existingRewrites.Add(new RewriteDto("exclude", "exclude.test", "1.2.3.4"));

        DnsProfile profile = new(1, "test", "test");
        AppSettings settings = new([profile], [], ["https://hosts.test/rewrites"], ["exclude.test"],
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "*.openai.*", "android.chat.openai.com" });
        using RewriteTestHandler handler = new(existingRewrites);
        using HttpClient httpClient = new(handler);
        NextDnsRunner runner = new(new NextDnsClient(httpClient, profile), new HostsListLoader(httpClient), settings);

        await runner.RunAsync();
        Assert(handler.SavedNames.SequenceEqual(["android.chat.openai.com", "update.test"]), "rewrite capacity or priority was incorrect");
        Assert(existingRewrites.Count == 500, "expected exactly 500 rewrites after the sync");

        await runner.RunAsync();
        Assert(handler.SavedNames.Count == 2, "full profile should skip pending rewrites without posting");

        NextDnsRunner fullProfileRunner = new(new NextDnsClient(httpClient, profile), new HostsListLoader(httpClient),
            settings with { RequiredRedirects = new HashSet<string> { "first.test" } });
        try
        {
            await fullProfileRunner.RunAsync();
            Assert(false, "a missing required rewrite in a full profile should fail");
        }
        catch (ProcessException exception) when (exception.Message.Contains("Free 1 NextDNS rewrite slot(s) for required domains: first.test"))
        {
            Assert(handler.SavedNames.Count == 2 && existingRewrites.Count == 500, "capacity check should leave rewrites intact");
        }
    }

    private static async Task TestExclusionsWithoutSourcesAsync()
    {
        RewriteDto keptRewrite = new("keep", "keep.test", "1.2.3.4");
        List<RewriteDto> existingRewrites = [new RewriteDto("exclude", "tracker.rutor.is", "1.2.3.4"), keptRewrite];
        DnsProfile profile = new(1, "test", "test");
        AppSettings settings = new([profile], [], [], ["rutor"], new HashSet<string>());
        using RewriteTestHandler handler = new(existingRewrites);
        using HttpClient httpClient = new(handler);
        NextDnsRunner runner = new(new NextDnsClient(httpClient, profile), new HostsListLoader(httpClient), settings);

        await runner.RunAsync();
        Assert(existingRewrites.SequenceEqual([keptRewrite]), "exclusions-only sync should remove only matching rewrites");
    }

    private sealed class RewriteTestHandler(List<RewriteDto> rewrites) : HttpMessageHandler
    {
        public List<string> SavedNames { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri uri = request.RequestUri!;
            if (uri.Host == "hosts.test")
            {
                Assert(rewrites.All(rewrite => rewrite.Name != "exclude.test"), "excluded rewrite was still present when loading sources");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("5.6.7.8 first.test\n5.6.7.8 android.chat.openai.com\n5.6.7.8 update.test\n5.6.7.8 exclude.test")
                };
            }

            if (request.Method == HttpMethod.Get && uri.AbsolutePath.EndsWith("/rewrites", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { data = rewrites }) };
            }

            if (request.Method == HttpMethod.Delete)
            {
                string id = uri.Segments[^1];
                Assert(rewrites.RemoveAll(rewrite => rewrite.Id == id) == 1, "unexpected rewrite deletion");
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }

            Assert(request.Method == HttpMethod.Post && rewrites.Count < 500, "unexpected request or rewrite limit exceeded");
            CreateRewriteRequest rewrite = (await request.Content!.ReadFromJsonAsync<CreateRewriteRequest>(cancellationToken))!;
            rewrites.Add(new RewriteDto(rewrite.Name, rewrite.Name, rewrite.Content));
            SavedNames.Add(rewrite.Name);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new ProcessException(message);
        }
    }
}
