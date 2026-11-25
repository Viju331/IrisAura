using System.Globalization;
using IrisAura.Island.Sdk;

namespace IrisAura.Island.Cli;

/// <summary>
/// <c>irisaura-island</c>: show activities on IrisAura Island from scripts in any language (BLUEPRINT §8.6.14).
/// Exit codes: 0 done, 1 usage error, 2 IrisAura Island not running (or SDK switched off), 3 the pipe server is not a
/// trusted IrisAura Island, 4 the Island refused the request (the message says why).
/// </summary>
internal static class Program
{
    private const string Usage = """
        irisaura-island — show your script's progress on IrisAura Island

          irisaura-island push   --id ID --title TEXT [options]   start (or replace) an activity
          irisaura-island update --id ID [options]                change only what you pass
          irisaura-island end    --id ID [--final-text TEXT] [--dismiss-after SECONDS]
          irisaura-island list                                    your app's live activity ids
          irisaura-island watch                                   print button presses ("ID BUTTON") until Ctrl+C

        Options for push / update:
          --title TEXT  --subtitle TEXT  --progress 0..1
          --text TEXT (right of the pill, e.g. 37%)  --leading-text TEXT  --ring 0..1
          --icon FILE.png (≤ 128×128)  --button ID=Label (up to 3)  --open-uri URI
          --priority 10..60  --ttl SECONDS (default 3600)

        Common options:
          --app NAME                  your app name (default "Command line"); activities are per app
          --trust-thumbprint SHA1     trust IrisAura.Island.exe signed with this certificate (repeatable)
          --allow-unsigned-island     development builds only: skip the signature check

        Example:
          irisaura-island push --app Backup --id nightly --title "Backing up photos" --progress 0.3 --text 30%
        """;

    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return args.Length == 0 ? 1 : 0;
        }

        Arguments a;
        try
        {
            a = Arguments.Parse(args.Skip(1));
        }
        catch (FormatException ex)
        {
            Console.Error.WriteLine($"irisaura-island: {ex.Message}");
            return 1;
        }

        var options = new IslandClientOptions
        {
            AppName = a.Value("app") ?? "Command line",
            Verification = a.Has("allow-unsigned-island") ? IslandServerVerification.UnsignedDevelopmentBuild : IslandServerVerification.SignedIsland,
            PipeName = a.Value("pipe"),
        };
        foreach (var thumbprint in a.Values("trust-thumbprint"))
        {
            options.TrustedThumbprints.Add(thumbprint);
        }

        try
        {
            using var island = await IslandClient.ConnectAsync(options);
            switch (args[0])
            {
                case "push":
                    var spec = new IslandActivitySpec(a.Required("id"), a.Required("title"), a.Number("progress"))
                    {
                        Subtitle = a.Value("subtitle"),
                        TrailingText = a.Value("text"),
                        LeadingText = a.Value("leading-text"),
                        TrailingProgress = a.Number("ring"),
                        Icon = a.Value("icon") is { } icon ? await File.ReadAllBytesAsync(icon) : null,
                        OpenUri = a.Value("open-uri"),
                        Priority = a.Integer("priority"),
                        Ttl = a.Number("ttl") is { } ttl ? TimeSpan.FromSeconds(ttl) : null,
                    };
                    foreach (var button in a.Buttons())
                    {
                        spec.Buttons.Add(button);
                    }

                    await island.StartAsync(spec);
                    break;
                case "update":
                    var buttons = a.Buttons().ToList();
                    await island.UpdateAsync(a.Required("id"), new IslandActivityUpdate
                    {
                        Title = a.Value("title"),
                        Subtitle = a.Value("subtitle"),
                        Progress = a.Number("progress"),
                        TrailingText = a.Value("text"),
                        LeadingText = a.Value("leading-text"),
                        TrailingProgress = a.Number("ring"),
                        Icon = a.Value("icon") is { } updateIcon ? await File.ReadAllBytesAsync(updateIcon) : null,
                        OpenUri = a.Value("open-uri"),
                        Priority = a.Integer("priority"),
                        Ttl = a.Number("ttl") is { } updateTtl ? TimeSpan.FromSeconds(updateTtl) : null,
                        Buttons = buttons.Count > 0 ? buttons : null,
                    });
                    break;
                case "end":
                    await island.EndAsync(a.Required("id"), a.Value("final-text"), a.Number("dismiss-after") is { } d ? TimeSpan.FromSeconds(d) : null);
                    break;
                case "list":
                    foreach (var id in await island.QueryAsync())
                    {
                        Console.WriteLine(id);
                    }

                    break;
                case "watch":
                    return await WatchAsync(island);
                default:
                    Console.Error.WriteLine($"irisaura-island: unknown command '{args[0]}'. Run 'irisaura-island --help'.");
                    return 1;
            }

            return 0;
        }
        catch (TimeoutException)
        {
            Console.Error.WriteLine("irisaura-island: IrisAura Island is not running, or 'Apps using the IrisAura SDK' is switched off in its Activities menu.");
            return 2;
        }
        catch (IslandServerVerificationException ex)
        {
            Console.Error.WriteLine($"irisaura-island: refusing to send: {ex.Message}");
            return 3;
        }
        catch (IslandSdkException ex)
        {
            Console.Error.WriteLine($"irisaura-island: {ex.Code}: {ex.Message}");
            return 4;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"irisaura-island: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> WatchAsync(IslandClient island)
    {
        var done = new TaskCompletionSource<int>();
        island.ActionReceived += (_, e) => Console.WriteLine($"{e.ActivityId} {e.Button}");
        island.Disconnected += (_, _) => done.TrySetResult(2);
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            done.TrySetResult(0);
        };
        await island.QueryAsync(); // registers this connection for the app's button presses
        return await done.Task;
    }

    /// <summary>--name value pairs; repeated names keep every value.</summary>
    private sealed class Arguments
    {
        private static readonly HashSet<string> Flags = ["allow-unsigned-island"];
        private readonly List<(string Name, string? Value)> _items = [];

        public static Arguments Parse(IEnumerable<string> args)
        {
            var result = new Arguments();
            using var e = args.GetEnumerator();
            while (e.MoveNext())
            {
                var arg = e.Current;
                if (!arg.StartsWith("--", StringComparison.Ordinal) || arg.Length < 3)
                {
                    throw new FormatException($"unexpected '{arg}' (options start with --)");
                }

                var name = arg[2..];
                if (Flags.Contains(name))
                {
                    result._items.Add((name, null));
                    continue;
                }

                if (!e.MoveNext())
                {
                    throw new FormatException($"--{name} needs a value");
                }

                result._items.Add((name, e.Current));
            }

            return result;
        }

        public bool Has(string name) => _items.Any(i => i.Name == name);

        public string? Value(string name) => _items.LastOrDefault(i => i.Name == name).Value;

        public IEnumerable<string> Values(string name) => _items.Where(i => i.Name == name && i.Value is not null).Select(i => i.Value!);

        public string Required(string name) => Value(name) ?? throw new FormatException($"--{name} is required");

        public double? Number(string name) => Value(name) is { } v
            ? double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new FormatException($"--{name} must be a number")
            : null;

        public int? Integer(string name) => Value(name) is { } v
            ? int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : throw new FormatException($"--{name} must be a whole number")
            : null;

        public IEnumerable<IslandButton> Buttons() => Values("button").Select(b =>
        {
            var eq = b.IndexOf('=');
            return eq > 0 ? new IslandButton(b[..eq], b[(eq + 1)..]) : new IslandButton(b, b);
        });
    }
}
