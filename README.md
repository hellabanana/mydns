Languages: [English](README.md) | [Русский](README.ru.md)

# DNS Block&Redirect Configurer for NextDNS

A .NET console application for automatically configuring NextDNS denylist entries and DNS rewrites.

The project is ready to run from GitHub Actions: fork the repository, set secrets and environment variables, and the workflow will update your rules daily or on demand with Run workflow.

## Features

- Loads one or more hosts files from URLs.
- Adds domains from `BLOCK` to the NextDNS Denylist.
- Adds `IP -> domain` pairs from `REDIRECT` to NextDNS Rewrites.
- Updates a rewrite when the IP for an existing domain changes.
- Removes rewrites for domains from `EXCLUDE_REDIRECT` and skips them when adding new rules.
- Supports multiple NextDNS profiles through comma-separated `CLIENT_ID` and `AUTH_SECRET` values.
- Handles NextDNS API limits by retrying after `429` or `524` responses.

## Environment Variables

### Required

- `CLIENT_ID` - NextDNS profile ID. You can find it in the profile's Endpoints section.
- `AUTH_SECRET` - API key from https://my.nextdns.io/account.

For multiple profiles, provide comma-separated values without spaces:

```text
CLIENT_ID=profile_1,profile_2
AUTH_SECRET=api_key_1,api_key_2
```

`CLIENT_ID` and `AUTH_SECRET` must contain the same number of values.

### Optional

- `BLOCK` - URLs to hosts files with domains to block.
- `REDIRECT` - URLs to hosts files with DNS rewrites.
- `REQUIRED_REDIRECT` - comma-separated domains or patterns from `REDIRECT` to save first. `*.openai.com` matches `openai.com` and its subdomains at any depth. `*.openai.*` also matches other suffixes, such as `openai.net`. A standalone `*` matches every domain. `EXCLUDE_REDIRECT` still applies. The sync reports an error if a pattern has no eligible matches or if the required rewrites exceed the available slots.
- `EXCLUDE_REDIRECT` - comma-separated exclusions for rewrites. An entry without a dot, such as `rutor`, matches anywhere in the domain name. An entry with a dot matches the end of the name. Matching is case-insensitive.

Multiple sources or domains are separated by commas:

```text
BLOCK=https://first.example/hosts,https://second.example/hosts
REDIRECT=https://example.com/redirect-hosts
REQUIRED_REDIRECT=*.openai.*,*.chatgpt.com
EXCLUDE_REDIRECT=rutor,instagram.com,twitch.com
```

## Hosts File Format

`BLOCK` uses lines with blocking IPs:

```text
0.0.0.0 domain.to.block
127.0.0.1 another.domain.to.block
::1 ipv6.domain.to.block
```

`REDIRECT` uses lines with real IPs:

```text
1.2.3.4 domain.to.redirect
5.6.7.8 another.domain.to.redirect
```

Comment lines and inline comments after `#` are ignored. The `www.` prefix is removed.

## Behavior

When `BLOCK` is provided, the app downloads the sources, keeps only domains from `0.0.0.0`, `127.0.0.1`, and `::1` lines, then adds missing domains to the NextDNS Denylist.

When `REDIRECT` is provided, the app downloads the sources, keeps `IP domain` pairs where IP is not a blocking IP, compares them with existing NextDNS Rewrites, and:

- leaves matching rules untouched;
- removes outdated rules when the IP changed;
- adds new rules;
- removes and skips domains from `EXCLUDE_REDIRECT`.

At the start of each profile sync, the app removes existing rewrites that match `EXCLUDE_REDIRECT`, even when `REDIRECT` is empty. With only `EXCLUDE_REDIRECT` set, the sync performs only that cleanup. If `BLOCK`, `REDIRECT`, and `EXCLUDE_REDIRECT` are all empty, the app removes all Denylist and Rewrites entries in the profile.

## GitHub Actions

1. Fork the repository.
2. Go to Settings -> Environments.
3. Create an environment named `DNS`.
4. Add `CLIENT_ID` and `AUTH_SECRET` to Environment secrets.
5. Add `BLOCK`, `REDIRECT`, `REQUIRED_REDIRECT`, and `EXCLUDE_REDIRECT` as needed to Environment variables.

The workflow runs daily at `01:30 UTC` and can also be started manually from the Actions tab.

To change the schedule, edit the cron expression in `.github/workflows/github_action.yml`.

## Local Run

.NET 10 SDK is required.

```shell
dotnet restore
dotnet build
```

To run against a test NextDNS profile, copy `.env.example` to `.env` and fill in the test profile ID and API key. Run from the project directory. The app loads `.env` from the working directory, and existing environment variables take precedence.

```shell
dotnet run
```

To run the self-test without calling the NextDNS API:

```shell
dotnet run -- --self-test
```
