using Depa.Cozo;

using var database = new CozoDb("mem");
var result = database.RunRaw("?[value] <- [[42]]");
if (!result.Contains("42"))
{
    throw new InvalidOperationException($"Unexpected Cozo response: {result}");
}

Console.WriteLine(result);
