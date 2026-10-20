using Knowledge.Indexer;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await KnowledgeCli.RunAsync(args, Console.Out, Console.Error, cancellation.Token);
