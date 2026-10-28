# NuGet packaging and trusted publishing

The project targets `net472;net10.0`. A single package contains both assemblies;
NuGet selects the appropriate one. The Framework build is the intended production
target. The modern build supports development/testing without Mono and still uses
a platform-specific HttpListener implementation.

```sh
dotnet pack MiniHttpHost.csproj -c Release -o artifacts
```

The default version is `0.1.0-alpha.1`. This prepares a package locally; it does
not publish it. The reference-assembly package is build-only and is excluded from
runtime dependencies. License metadata has not been selected for this project.

## Configured trusted-publisher identity

| Field | Value |
| --- | --- |
| NuGet profile | `vivainio` |
| GitHub owner | `vivainio` |
| Repository | `MiniHttpHost` |
| Workflow filename | `publish.yml` |
| GitHub environment | `nuget` |

On NuGet.org, open your profile's Trusted Publishing settings and create a GitHub
policy with these values. The workflow field is just `publish.yml`, not the full
`.github/workflows/` path. Scope it to `MiniHttpHost`; allow creating a new package
for the first release and new versions for subsequent releases. Choose the user
or organization that should own the package.

The workflow uses `NuGet/login@v1` to exchange GitHub's OIDC identity for a temporary
publishing key. No long-lived NuGet API key secret is required. The `publish` job
has `id-token: write` and runs in the matching `nuget` environment.

## Build versus publish

- Run **NuGet package** manually in GitHub Actions to build downloadable `.nupkg`
  artifacts. Manual runs do not publish to NuGet.org.
- Publish a GitHub Release with a version tag such as `v0.1.0-alpha.1` to build and
  publish that version through the trusted-publishing job. This is the publication
  trigger, so create the release only when you intend to publish the package.

The package is prepared by this repository; configuring a policy does not itself
publish anything. See [Microsoft's trusted-publishing guide](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing).
