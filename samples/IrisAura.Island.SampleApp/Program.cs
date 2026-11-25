using IrisAura.Island.Sdk;

// Sample: a fake "deploy" that shows its progress on IrisAura Island, with a Cancel button.
//
//   dotnet run --project samples/IrisAura.Island.SampleApp -- [--allow-unsigned-island] [--trust-thumbprint SHA1]
//
// Release builds of IrisAura Island are signed, and the default check accepts only a trusted IrisAura signature.
// For a development build pass --allow-unsigned-island, or --trust-thumbprint with your development certificate.

var options = new IslandClientOptions { AppName = "Sample Deploy" };
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--allow-unsigned-island")
    {
        options.Verification = IslandServerVerification.UnsignedDevelopmentBuild;
    }
    else if (args[i] == "--trust-thumbprint" && i + 1 < args.Length)
    {
        options.TrustedThumbprints.Add(args[++i]);
    }
}

using var island = await IslandClient.ConnectAsync(options);
Console.WriteLine($"Connected to {island.ServerVersion ?? "IrisAura Island"}.");

using var cancelled = new CancellationTokenSource();
island.ActionReceived += (_, e) =>
{
    Console.WriteLine($"Button pressed on the Island: {e.Button}");
    if (e.Button == "cancel")
    {
        cancelled.Cancel();
    }
};

var spec = new IslandActivitySpec("deploy-42", "Deploying API", progress: 0)
{
    Subtitle = "Stage 1 of 5",
    TrailingText = "0%",
    Priority = 40,
    Ttl = TimeSpan.FromMinutes(10),
};
spec.Buttons.Add(new IslandButton("cancel", "Cancel"));
var activity = await island.StartAsync(spec);

try
{
    for (var step = 1; step <= 20; step++)
    {
        await Task.Delay(500, cancelled.Token);
        var progress = step / 20.0;
        await activity.UpdateAsync(new IslandActivityUpdate
        {
            Progress = progress,
            TrailingText = $"{progress:P0}",
            Subtitle = $"Stage {Math.Min(5, 1 + (step / 4))} of 5",
        });
    }

    await activity.EndAsync("Deployed ✓");
    Console.WriteLine("Done.");
}
catch (OperationCanceledException)
{
    await activity.EndAsync("Cancelled");
    Console.WriteLine("Cancelled from the Island.");
}
