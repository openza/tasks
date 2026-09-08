using Openza.Tasks.Application.Runtime;

if (args.Length is < 1 or > 2 || !Enum.TryParse<OpenzaChannel>(args[0], true, out var target) || target == OpenzaChannel.Production)
{
    Console.Error.WriteLine("Usage: seed-linux-data.sh dev|preview [--replace]");
    return 2;
}

var replace = args.Contains("--replace", StringComparer.Ordinal);
var context = OpenzaRuntimeContext.Create(target);
if (File.Exists(context.DatabasePath))
{
    if (!replace)
    {
        Console.Error.WriteLine($"{context.DisplayName} already has data. Re-run with --replace to confirm replacement.");
        return 5;
    }
    Console.Write($"Type {target.ToString().ToLowerInvariant()} to replace {context.DatabasePath}: ");
    if (!string.Equals(Console.ReadLine()?.Trim(), target.ToString(), StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine("Seed cancelled.");
        return 5;
    }
}

try
{
    await DevelopmentDataSeeder.SeedFromProductionAsync(target, replace);
    Console.WriteLine($"Seeded {context.DisplayName} with an isolated, sync-disabled Production snapshot.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
