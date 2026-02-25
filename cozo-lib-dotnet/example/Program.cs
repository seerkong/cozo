using System.Text.Json;
using Cozo.DotNet;

Console.WriteLine("=== CozoDB .NET Example ===");

using var db = new CozoDb(engine: "mem", path: "");

// Run a simple query
using var result = db.Run("?[] <- [[1, 'hello'], [2, 'world']]");

Console.WriteLine("Query result:");
Console.WriteLine(result.RootElement.GetRawText());

// Check the ok field
if (result.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean())
{
    Console.WriteLine("Query succeeded!");

    if (result.RootElement.TryGetProperty("rows", out var rows))
    {
        foreach (var row in rows.EnumerateArray())
        {
            Console.WriteLine($"  Row: {row.GetRawText()}");
        }
    }
}
else
{
    Console.WriteLine("Query failed.");
}
