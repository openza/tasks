# Cloudflare OAuth-only probe

This maintainer utility proves the registered Openza desktop Authorization Code + S256 PKCE flow without creating or changing D1 databases, Workers, routes, or deployments.

Run from the repository root:

```bash
dotnet run --project scripts/personal-cloud/oauth-probe/Openza.Tasks.PersonalCloud.OAuthProbe.csproj -c Release
```

Open the printed URL in a browser, review the three required permissions, and authorize the intended account. The probe exchanges the code, checks that the exact required scopes were granted, performs a read-only account-list call, and immediately revokes the temporary token. It never prints the token or authorization code.
