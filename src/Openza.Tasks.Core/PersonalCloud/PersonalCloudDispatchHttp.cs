using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Openza.Tasks.Core.PersonalCloud;

public static class PersonalCloudDispatchHttp
{
    private static readonly ConditionalWeakTable<HttpClient, PersonalCloudDispatchPolicy> Policies = new();
    public static HttpClient Create(PersonalCloudDispatchPolicy policy, HttpMessageHandler? inner = null)
    {
        var client = new HttpClient(new DispatchHandler(policy, inner ?? new HttpClientHandler { AllowAutoRedirect = false }));
        Policies.Add(client, policy);
        return client;
    }
    public static PersonalCloudDispatchPolicy? PolicyFor(HttpClient client) => Policies.TryGetValue(client, out var policy) ? policy : null;

    // Classify cancellation outside HttpClient: the handler receives a linked
    // timeout/caller token and cannot tell which source cancelled it.
    public static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request,
        CancellationToken cancellationToken, HttpCompletionOption completion = HttpCompletionOption.ResponseHeadersRead)
    {
        try { return await client.SendAsync(request, completion, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested &&
            exception.InnerException is TimeoutException && request.RequestUri?.AbsolutePath != "/sync/initial-status" &&
            PolicyFor(client) is { } policy)
        {
            throw (await policy.RecordFailureAsync(request.RequestUri!, CancellationToken.None).ConfigureAwait(false)).ToException();
        }
    }

    private sealed class DispatchHandler(PersonalCloudDispatchPolicy policy, HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var origin = request.RequestUri ?? throw new InvalidOperationException("A cloud request needs an origin.");
            var optionalReport = origin.AbsolutePath is "/sync/initial-status" or "/sync/catchup-status";
            var envelopes = 0;
            long writeUnits = 0;
            string? singleMutation = null;
            long? fanoutBound = request.Options.TryGetValue(PersonalCloudMutationCost.GuardedFanoutBound, out var bound) &&
                request.Headers.TryGetValues(PersonalCloudMutationCost.FanoutHeader, out var values) &&
                values.SequenceEqual([bound.ToString(CultureInfo.InvariantCulture)]) ? bound : null;
            var budget = (await policy.GetStatusAsync(origin, cancellationToken).ConfigureAwait(false)).WriteUnitLimit;
            if (request.Method == HttpMethod.Post && origin.AbsolutePath == "/v1/tasks/mutations")
            {
                envelopes = 1;
                singleMutation = await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var payload = JsonDocument.Parse(singleMutation);
                writeUnits = PersonalCloudMutationCost.ForBudget(payload.RootElement, budget, fanoutBound);
            }
            else if (request.Method == HttpMethod.Post && origin.AbsolutePath == "/v1/tasks/mutations/batch")
            {
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
                envelopes = payload.RootElement.GetProperty("mutations").GetArrayLength();
                if (envelopes is < 1 or > 2) throw new PersonalCloudK0ContractException("invalid_request", "Invalid mutation batch size.");
                writeUnits = payload.RootElement.GetProperty("mutations").EnumerateArray().Sum(mutation => PersonalCloudMutationCost.ForBudget(mutation, budget, fanoutBound));
            }
            using var lease = await policy.BeginAsync(origin, envelopes, writeUnits, cancellationToken, singleMutation).ConfigureAwait(false);
            HttpResponseMessage? response = null;
            try
            {
                response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
                // Keep the lease through bounded body transfer, not just response headers.
                // Callers then parse the buffered response without holding the dispatch lease.
                await response.Content.LoadIntoBufferAsync(2 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                var batch = response.IsSuccessStatusCode && origin.AbsolutePath == "/v1/tasks/mutations/batch";
                if (!response.IsSuccessStatusCode || batch)
                {
                    var body = await ReadBoundedAsync(response, cancellationToken).ConfigureAwait(false);
                    var reference = PersonalCloudQuotaResponse.ReferenceTime(response.Headers.Date, policy.UtcNow);
                    if (body is not null && PersonalCloudQuotaResponse.TryRead(response, body, reference, out var quota))
                    {
                        var observedAt = policy.UtcNow;
                        // Translate trusted server time into a local duration. A fast local
                        // clock must not make a recognized daily quota immediately expire.
                        var status = lease.Pause("daily_quota_exceeded", quota.Resource, observedAt,
                            observedAt + (quota.ResetAt - reference), quota.ResetAt);
                        // The sync client must validate and ACK a 200 batch's confirmed prefix first.
                        if (!response.IsSuccessStatusCode) throw status.ToException();
                        return response;
                    }
                    var media = response.Content.Headers.ContentType?.MediaType;
                    var unsupported = (origin.AbsolutePath is "/health" or "/v1/tasks/mutations/batch") &&
                        response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented;
                    var edgeErrorPage = PersonalCloudQuotaResponse.HasEdgeContext(response) &&
                        (string.Equals(media, "text/html", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(media, "text/plain", StringComparison.OrdinalIgnoreCase) &&
                            body?.TrimStart().StartsWith("error code:", StringComparison.OrdinalIgnoreCase) == true);
                    // Preserve explicitly unsupported legacy endpoints after quota classification.
                    if (unsupported && !edgeErrorPage) return response;
                    if (!optionalReport && !response.IsSuccessStatusCode && ((int)response.StatusCode is >= 500 or 408 or 429 ||
                        string.Equals(media, "text/html", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(media, "text/plain", StringComparison.OrdinalIgnoreCase) &&
                            (response.StatusCode == HttpStatusCode.Forbidden || PersonalCloudQuotaResponse.HasEdgeContext(response))))
                        throw lease.Backoff().ToException();
                }
                return response;
            }
            catch (HttpRequestException) when (!optionalReport)
            {
                response?.Dispose();
                throw lease.Backoff().ToException();
            }
            catch
            {
                response?.Dispose();
                throw;
            }
        }

        private static async Task<string?> ReadBoundedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
        {
            if (response.Content.Headers.ContentLength > PersonalCloudQuotaResponse.MaximumBodyBytes) return null;
            try
            {
                await response.Content.LoadIntoBufferAsync(PersonalCloudQuotaResponse.MaximumBodyBytes, cancellationToken).ConfigureAwait(false);
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException) { return null; } // Unrecognized/oversized body never grants a daily pause.
        }
    }
}

internal sealed record PersonalCloudQuota(string Resource, DateTimeOffset ResetAt);

internal static class PersonalCloudQuotaResponse
{
    internal const int MaximumBodyBytes = 65536;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    internal static DateTimeOffset ReferenceTime(DateTimeOffset? responseDate, DateTimeOffset now) =>
        responseDate is { } date && (date - now).Duration() <= TimeSpan.FromDays(2) ? date : now;
    internal static bool HasEdgeContext(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Server", out var servers) && servers.Any(value => value.Trim().Equals("cloudflare", StringComparison.OrdinalIgnoreCase)) ||
        response.Headers.TryGetValues("cf-ray", out var rays) && rays.Any(value => !string.IsNullOrWhiteSpace(value));

    internal static bool TryRead(HttpResponseMessage response, string body, DateTimeOffset reference, out PersonalCloudQuota quota)
    {
        quota = null!;
        if (System.Text.Encoding.UTF8.GetByteCount(body) > MaximumBodyBytes) return false;
        var media = response.Content.Headers.ContentType?.MediaType;
        if (string.Equals(media, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var json = JsonDocument.Parse(body);
                if (json.RootElement.ValueKind != JsonValueKind.Object ||
                    !json.RootElement.TryGetProperty("protocolVersion", out var protocol) || protocol.ValueKind != JsonValueKind.Number || !protocol.TryGetInt32(out var number) || number != 1 ||
                    !json.RootElement.TryGetProperty("product", out var product) || product.ValueKind != JsonValueKind.String || product.GetString() != "tasks" ||
                    !json.RootElement.TryGetProperty("error", out var error) || !TryReadError(error, reference, out quota)) return false;
                return !response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.OK;
            }
            catch (JsonException) { return false; }
        }
        if ((int)response.StatusCode is < 400 or > 599 || !HasEdgeContext(response)) return false;
        var plain = string.Equals(media, "text/plain", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(body.Trim(), @"^error code:\s*1027$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
        var html = string.Equals(media, "text/html", StringComparison.OrdinalIgnoreCase) && IsError1027Html(body);
        if (!plain && !html) return false;
        quota = new("workers_requests", PersonalCloudDispatchPolicy.NextMidnight(reference));
        return true;
    }

    internal static bool TryReadError(JsonElement error, DateTimeOffset reference, out PersonalCloudQuota quota)
    {
        quota = null!;
        if (error.ValueKind != JsonValueKind.Object || error.EnumerateObject().Count() != 5 ||
            !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String ||
            !error.TryGetProperty("requestId", out var requestId) || requestId.ValueKind != JsonValueKind.String ||
            !error.TryGetProperty("code", out var code) ||
            code.ValueKind != JsonValueKind.String || code.GetString() != "daily_quota_exceeded" ||
            !error.TryGetProperty("retryable", out var retry) || retry.ValueKind != JsonValueKind.True ||
            !error.TryGetProperty("quota", out var value) || value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 3 ||
            !value.TryGetProperty("protocolVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1 ||
            !value.TryGetProperty("resource", out var resource) || resource.ValueKind != JsonValueKind.String ||
            resource.GetString() is not ("d1_rows_read" or "d1_rows_written") ||
            !value.TryGetProperty("resetAt", out var reset)) return false;
        var expected = PersonalCloudDispatchPolicy.NextMidnight(reference);
        var resetAt = expected;
        var validHint = reset.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParseExact(reset.GetString(), "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out resetAt) && resetAt == expected;
        // Recognition rests on the strict typed code/resource envelope. A bad time
        // hint cannot turn a known daily limit into repeated transient requests.
        quota = new(resource.GetString()!, validHint ? resetAt : expected);
        return true;
    }

    private static bool IsError1027Html(string body)
    {
        try
        {
            var title = Regex.Match(body, @"<title\b[^>]{0,256}>(?<title>[^<]{0,1024})</title\s*>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout).Groups["title"].Value;
            if (!title.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ||
                !(title.Contains("error", StringComparison.OrdinalIgnoreCase) || title.Contains("worker exceeded resource limits", StringComparison.OrdinalIgnoreCase))) return false;
            foreach (Match span in Regex.Matches(body, @"<span\s+(?<attrs>[^>]{0,2048})>\s*1027\s*</span\s*>",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
            {
                if (HasErrorClass(span.Groups["attrs"].Value)) return true;
            }
        }
        catch (RegexMatchTimeoutException) { }
        return false;
    }

    private static bool HasErrorClass(string attributes)
    {
        // Consume complete attributes from left to right. A word-boundary search
        // would accept data-class or class-looking text inside another value.
        var text = " " + attributes;
        var attribute = new Regex("\\G\\s+(?<name>[A-Za-z_:][\\w:.-]*)(?:\\s*=\\s*(?:\"(?<double>[^\"]*)\"|'(?<single>[^']*)'|(?<bare>[^\\s\"'=<>`]+)))?",
            RegexOptions.CultureInvariant, RegexTimeout);
        string? classes = null;
        var at = 0;
        while (at < text.Length)
        {
            if (string.IsNullOrWhiteSpace(text[at..])) break;
            var parsed = attribute.Match(text, at);
            if (!parsed.Success || parsed.Index != at) return false;
            at = parsed.Index + parsed.Length;
            if (!parsed.Groups["name"].Value.Equals("class", StringComparison.OrdinalIgnoreCase)) continue;
            if (classes is not null) return false;
            classes = parsed.Groups["double"].Success ? parsed.Groups["double"].Value
                : parsed.Groups["single"].Success ? parsed.Groups["single"].Value : parsed.Groups["bare"].Value;
        }
        return classes is not null && Regex.Split(classes, @"\s+").Contains("cf-error-code", StringComparer.Ordinal);
    }
}
