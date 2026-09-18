using System.Text.Json;
using System.Text.Json.Serialization;

namespace Openza.Tasks.Core.PersonalCloud;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PersonalCloudConnectionProfile
{
    public required Uri ServiceOrigin { get; init; }
    public required string InstanceId { get; init; }
    public required string DeviceId { get; init; }
    public required string ArtifactVersion { get; init; }
    public required DateTimeOffset ConnectedAt { get; init; }
}

public interface IPersonalCloudConnectionStore
{
    PersonalCloudConnectionProfile? Load();
    Task SaveAsync(PersonalCloudConnectionProfile connection, CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public sealed class JsonPersonalCloudConnectionStore(string path) : IPersonalCloudConnectionStore
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public PersonalCloudConnectionProfile? Load()
    {
        if (!File.Exists(path)) return null;
        try
        {
            var value = JsonSerializer.Deserialize<PersonalCloudConnectionProfile>(File.ReadAllText(path), Json);
            Validate(value);
            return value;
        }
        catch (PersonalCloudK0ContractException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new PersonalCloudK0ContractException("connection_invalid", "The saved Personal Cloud connection is invalid.", exception);
        }
    }

    public async Task SaveAsync(PersonalCloudConnectionProfile connection, CancellationToken cancellationToken = default)
    {
        Validate(connection);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                await JsonSerializer.SerializeAsync(stream, connection, Json, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private static void Validate(PersonalCloudConnectionProfile? value)
    {
        if (value is null || !value.ServiceOrigin.IsAbsoluteUri || value.ServiceOrigin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(value.ServiceOrigin.Query) || !string.IsNullOrEmpty(value.ServiceOrigin.Fragment) ||
            value.ServiceOrigin.AbsolutePath != "/" || string.IsNullOrWhiteSpace(value.ArtifactVersion) ||
            value.ArtifactVersion.Length > 128)
            throw new PersonalCloudK0ContractException("connection_invalid", "The saved Personal Cloud connection is invalid.");
        PersonalCloudK0Codec.ValidateIdentifier(value.InstanceId, nameof(value.InstanceId));
        PersonalCloudK0Codec.ValidateIdentifier(value.DeviceId, nameof(value.DeviceId));
    }
}
