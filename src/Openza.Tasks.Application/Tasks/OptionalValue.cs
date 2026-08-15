namespace Openza.Tasks.Application.Tasks;

public readonly record struct OptionalValue<T>(bool IsSpecified, T? Value)
{
    public static OptionalValue<T> Unspecified => default;
    public static OptionalValue<T> Set(T? value) => new(true, value);
}
