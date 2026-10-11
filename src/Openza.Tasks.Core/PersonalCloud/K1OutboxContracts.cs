namespace Openza.Tasks.Core.PersonalCloud;

public sealed record PersonalCloudK1OutboxEntry
{
    public required long LocalSequence { get; init; }
    public required string MutationId { get; init; }
    public required string Product { get; init; }
    public required string InstanceId { get; init; }
    public required string DeviceId { get; init; }
    public required long DeviceSequence { get; init; }
    public required string Operation { get; init; }
    public required string EntityId { get; init; }
    public required string CanonicalJson { get; init; }
    public required string CanonicalHash { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
