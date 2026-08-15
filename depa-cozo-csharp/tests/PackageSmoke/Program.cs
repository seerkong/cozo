using Depa.Cozo;

using var database = new CozoDb("mem");
var result = database.RunRaw("?[value] <- [[42]]");
if (!result.Contains("42"))
{
    throw new InvalidOperationException($"Unexpected Cozo response: {result}");
}

Console.WriteLine(result);

using (var transaction = database.BeginTransaction())
{
    var create = transaction.RunRaw(":create package_tx {value}");
    if (!create.Contains("\"ok\":true"))
    {
        throw new InvalidOperationException($"Could not create relation in transaction: {create}");
    }

    var commit = transaction.CommitRaw();
    if (!commit.Contains("\"ok\":true"))
    {
        throw new InvalidOperationException($"Could not commit transaction: {commit}");
    }
}

using (var transaction = database.BeginTransaction())
{
    var insert = transaction.RunRaw("?[value] <- [[7]] :put package_tx {value}");
    if (!insert.Contains("\"ok\":true"))
    {
        throw new InvalidOperationException($"Could not write in transaction: {insert}");
    }

    var abort = transaction.AbortRaw();
    if (!abort.Contains("\"ok\":true"))
    {
        throw new InvalidOperationException($"Could not abort transaction: {abort}");
    }
}

var afterAbort = database.RunRaw("?[value] := *package_tx[value]");
if (afterAbort.Contains("7"))
{
    throw new InvalidOperationException($"Aborted transaction persisted data: {afterAbort}");
}
