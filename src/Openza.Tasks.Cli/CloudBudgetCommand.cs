using System.CommandLine;
using Openza.Tasks.Core.Data;
using Openza.Tasks.Core.PersonalCloud;

internal static partial class OpenzaCli
{
    private static Command BuildCloudBudgetCommand(Option<string> format)
    {
        var parent = new Command("cloud-budget", "Inspect a held upload or authorize one exact pending attempt; no network request is made.");
        foreach (var authorize in new[] { false, true })
        {
            var command = new Command(authorize ? "allow-once" : "status", authorize
                ? "Authorize one attempt of the FIFO head, expiring in 15 minutes. Does not bypass cloud quota, request limits or storage failures."
                : "Inspect this channel's saved connection, local budget and exact pending FIFO head.");
            var mutation = new Option<string>("--mutation-id") { Required = true };
            var digest = new Option<string>("--sha256") { Required = true };
            var yes = new Option<bool>("--yes") { Description = "Confirm one upload attempt with its full daily write-credit charge." };
            if (authorize) { command.Options.Add(mutation); command.Options.Add(digest); command.Options.Add(yes); }
            command.SetAction(async (parsed, cancellationToken) => await ExecuteAsync(parsed, format, async context =>
            {
                var connection = new JsonPersonalCloudConnectionStore(
                    Path.Combine(context.Runtime.DataDirectory, "personal-cloud-connection.json")).Load();
                if (connection is null)
                {
                    context.Output.WriteError(OperationRestricted, "not_connected", "This channel has no saved Personal Cloud connection.");
                    return OperationRestricted;
                }
                var store = (SqliteTaskStore)context.Store;
                var pending = await store.GetPendingPersonalCloudOutboxAsync(connection.InstanceId, connection.DeviceId, cancellationToken);
                var head = pending.FirstOrDefault();
                var policy = new PersonalCloudDispatchPolicy(Path.Combine(context.Runtime.DataDirectory, "personal-cloud-dispatch"));
                var status = await policy.GetStatusAsync(connection.ServiceOrigin, cancellationToken);
                var exactDigest = head is null ? null : PersonalCloudDispatchPolicy.ExactEnvelopeDigest(head.CanonicalJson);
                if (authorize)
                {
                    if (!parsed.GetValue(yes))
                    {
                        context.Output.WriteError(ConfirmationRequired, "confirmation_required", "A one-attempt exemption requires --yes. An uncertain outcome consumes it.");
                        return ConfirmationRequired;
                    }
                    if (head is null || head.MutationId != parsed.GetValue(mutation) || exactDigest != parsed.GetValue(digest))
                    {
                        context.Output.WriteError(Conflict, "pending_entry_changed", "The FIFO head or SHA256 changed. Inspect cloud-budget status again; no permission was saved.");
                        return Conflict;
                    }
                    await policy.AuthorizeSingleAttemptAsync(connection, head, exactDigest!, cancellationToken);
                }
                var permitted = head is not null && policy.HasSingleAttemptPermission(connection.ServiceOrigin, head.CanonicalJson);
                var recoveryCommand = head is null ? null :
                    $"openza cloud-budget allow-once --mutation-id {head.MutationId} --sha256 {exactDigest} --yes";
                var message = authorize
                    ? "One exact attempt is authorized for 15 minutes. Use Sync in the app. No upload was sent; uncertain outcomes consume the permission."
                    : head is null ? "No pending upload." : "Inspect the pending operation before explicitly authorizing one attempt. Downloads remain available within global limits.";
                var row = new { channel = context.Runtime.Channel.ToString(), origin = connection.ServiceOrigin.AbsoluteUri,
                    status.Code, blocksReads = status.BlocksReads, mutationId = head?.MutationId, operation = head?.Operation,
                    sha256 = exactDigest, attemptAuthorized = permitted, recoveryCommand, message };
                context.Output.Write(row,
                    [["Channel", row.channel], ["Origin", row.origin], ["Budget", status.Code ?? "available"],
                     ["Mutation", head?.MutationId ?? ""], ["Operation", head?.Operation ?? ""], ["SHA256", exactDigest ?? ""],
                     ["Attempt authorized", permitted.ToString()], ["Recovery command", recoveryCommand ?? ""], ["Result", message]],
                    ["channel", "origin", "code", "mutation_id", "operation", "sha256", "attempt_authorized", "recovery_command", "message"],
                    [[row.channel, row.origin, status.Code ?? "", head?.MutationId ?? "", head?.Operation ?? "", exactDigest ?? "",
                      permitted.ToString().ToLowerInvariant(), recoveryCommand ?? "", message]]);
                return Success;
            }, cancellationToken, readOnlyOverride: true));
            parent.Subcommands.Add(command);
        }
        return parent;
    }
}
