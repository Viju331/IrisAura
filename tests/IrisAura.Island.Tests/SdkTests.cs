using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using IrisAura.Contracts;
using IrisAura.Island.Providers.Sdk;
using IrisAura.Island.Sdk;

namespace IrisAura.Island.Tests;

/// <summary>
/// Activity SDK (BLUEPRINT §8.6.14, §10 Phase 5 acceptance): protocol rules, untrusted local processes,
/// malformed/oversized messages, rate limits, reconnects, pipe security and client-side server verification.
/// </summary>
public sealed class SdkTests
{
    private static readonly SdkClient AppA = new(@"C:\Apps\builder.exe", "builder", "Builder");
    private static readonly SdkClient AppB = new(@"C:\Apps\other.exe", "other", "Other");

    private static IslandSdkMessage Start(string id, string title = "Deploying", int? priority = null) => new()
    {
        V = 1,
        Op = IslandSdkOps.Start,
        Id = id,
        Priority = priority,
        Expanded = new IslandSdkExpanded { Title = title },
    };

    private static IslandSdkMessage Op(string op, string id) => new() { V = 1, Op = op, Id = id };

    // ---------------------------------------------------------------- text, ids, links, images

    [Fact]
    public void Text_is_plain_bounded_and_free_of_invisible_tricks()
    {
        SdkText.Clean("  Deploy\u202Egnp.exe  \n\n now\t", 80).Should().Be("Deploygnp.exe now", "bidi overrides and control characters are removed");
        SdkText.Clean("a\u200Bb\uFEFFc", 80).Should().Be("abc");
        SdkText.Clean("<b>bold</b>", 80).Should().Be("<b>bold</b>", "shown as text, never interpreted as markup");
        SdkText.Clean(new string('x', 100), 10).Should().Be("xxxxxxxxx…");
        SdkText.Clean(" \u0007 ", 10).Should().BeNull();
    }

    [Theory]
    [InlineData("build-42", true)]
    [InlineData("a.b_c-1", true)]
    [InlineData("", false)]
    [InlineData("has space", false)]
    [InlineData("../etc", false)]
    [InlineData("ünï", false)]
    public void Ids_use_a_safe_alphabet(string id, bool valid) => SdkText.IsValidId(id, 64).Should().Be(valid);

    [Theory]
    [InlineData("https://example.com/builds/42", true)]
    [InlineData("http://localhost:8080/", true)]
    [InlineData("myapp://builds/42", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData(@"\\server\share\run.exe", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("ms-msdt:/id PCWDiagnostic", false)]
    [InlineData("search-ms:query=x", false)]
    [InlineData("relative/path", false)]
    [InlineData("C:\\Windows\\notepad.exe", false)]
    public void Only_safe_links_can_be_opened(string uri, bool allowed) => SdkUriPolicy.IsAllowed(uri).Should().Be(allowed);

    [Fact]
    public void Icons_are_png_or_jpeg_up_to_128_pixels()
    {
        SdkImage.FromDataUri(DataUri(Png(128, 128))).Error.Should().BeNull();
        SdkImage.FromDataUri(DataUri(Png(129, 16))).Error.Should().Contain("larger than 128");
        SdkImage.FromDataUri("data:image/png;base64,@@@").Error.Should().Contain("base64");
        SdkImage.FromDataUri("data:image/svg+xml;base64,PHN2Zz4=").Error.Should().Contain("png");
        SdkImage.FromDataUri("data:image/png;base64," + Convert.ToBase64String(Encoding.ASCII.GetBytes("not an image"))).Error.Should().Contain("readable");
        SdkImage.Size(Jpeg(96, 64)).Should().Be((96, 64));
    }

    [Fact]
    public void Rate_limit_is_10_per_second_per_activity()
    {
        var limiter = new SdkRateLimiter();
        var t = DateTimeOffset.UnixEpoch;
        Enumerable.Range(0, 10).Select(_ => limiter.TryTake("a", t)).ToList().Should().AllBeEquivalentTo(true);
        limiter.TryTake("a", t).Should().BeFalse();
        limiter.TryTake("b", t).Should().BeTrue("per activity");
        limiter.TryTake("a", t.AddMilliseconds(100)).Should().BeTrue("refills at 10 per second");
    }

    // ---------------------------------------------------------------- protocol rules

    [Fact]
    public void Wrong_version_and_unknown_ops_are_refused()
    {
        var handler = new SdkRequestHandler(new ManualTimeProvider(), _ => false);
        handler.Handle(AppA, Start("x")).Reply.Should().BeNull();

        var bad = Start("y");
        bad.V = 2;
        handler.Handle(AppA, bad).Reply!.Code.Should().Be(IslandSdkErrors.BadVersion);
        handler.Handle(AppA, Op("explode", "y")).Reply!.Code.Should().Be(IslandSdkErrors.BadRequest);
        handler.Handle(AppA, Start("bad id")).Reply!.Code.Should().Be(IslandSdkErrors.BadRequest);
    }

    [Fact]
    public void Start_publishes_a_custom_activity_with_capped_priority_and_clean_text()
    {
        var handler = new SdkRequestHandler(new ManualTimeProvider(), _ => false);
        var high = handler.Handle(AppA, Start("one", "Deploy\u202E now", priority: 99)).Start!;
        high.Kind.Should().Be(ActivityKind.Custom);
        high.Priority.Should().Be(IslandSdkLimits.MaxPriority);
        high.Expanded!.Title.Should().Be("Deploy now");
        high.Ttl.Should().Be(TimeSpan.FromSeconds(IslandSdkLimits.DefaultTtlSeconds));
        high.SourceAppId.Should().Be("builder", "no open link: a click brings the app forward");
        high.View.Should().BeOfType<SdkVisuals>();

        handler.Handle(AppA, Start("two", priority: 1)).Start!.Priority.Should().Be(IslandSdkLimits.MinPriority);

        var withLink = Start("three");
        withLink.OpenUri = "https://example.com";
        handler.Handle(AppA, withLink).Start!.SourceAppId.Should().BeEmpty("the link opens instead");
    }

    [Fact]
    public void An_untrusted_app_cannot_see_update_or_end_another_apps_activity()
    {
        var handler = new SdkRequestHandler(new ManualTimeProvider(), _ => false);
        var mine = handler.Handle(AppA, Start("build-42")).Start!;

        SdkRequestHandler.EngineId(AppB, "build-42").Should().NotBe(mine.Id, "ids are namespaced per app");
        var update = Op(IslandSdkOps.Update, "build-42");
        update.Expanded = new IslandSdkExpanded { Title = "Hijacked" };
        handler.Handle(AppB, update).Reply!.Code.Should().Be(IslandSdkErrors.NotFound);
        handler.Handle(AppB, Op(IslandSdkOps.End, "build-42")).Reply!.Code.Should().Be(IslandSdkErrors.NotFound);
        handler.Handle(AppB, Op(IslandSdkOps.Query, "")).Reply!.Ids.Should().BeEmpty();

        var sameExeOtherName = AppA with { AppName = "Pretender" };
        handler.Handle(sameExeOtherName, Op(IslandSdkOps.End, "build-42")).Reply!.Code.Should().Be(IslandSdkErrors.NotFound);
        handler.Handle(AppA, Op(IslandSdkOps.Query, "")).Reply!.Ids.Should().Equal("build-42");
    }

    [Fact]
    public void At_most_five_activities_per_app_and_expired_ones_free_their_slot()
    {
        var time = new ManualTimeProvider();
        var handler = new SdkRequestHandler(time, _ => false);
        for (var i = 0; i < IslandSdkLimits.MaxActivitiesPerApp; i++)
        {
            handler.Handle(AppA, Start($"a{i}")).Start.Should().NotBeNull();
        }

        handler.Handle(AppA, Start("a5")).Reply!.Code.Should().Be(IslandSdkErrors.TooManyActivities);
        handler.Handle(AppA, Start("a0", "replaced")).Start.Should().NotBeNull("replacing an existing id is not a new activity");
        handler.Handle(AppB, Start("b0")).Start.Should().NotBeNull("the limit is per app");

        time.Advance(TimeSpan.FromSeconds(IslandSdkLimits.DefaultTtlSeconds + 1));
        handler.Handle(AppA, Start("a5")).Start.Should().NotBeNull("expired activities no longer count");
    }

    [Fact]
    public void Updates_beyond_10_per_second_are_dropped_with_an_error()
    {
        var time = new ManualTimeProvider();
        var handler = new SdkRequestHandler(time, _ => false);
        handler.Handle(AppA, Start("fast")).Start.Should().NotBeNull();
        var update = Op(IslandSdkOps.Update, "fast");
        update.Expanded = new IslandSdkExpanded { Progress = 0.5 };

        var outcomes = Enumerable.Range(0, 12).Select(_ => handler.Handle(AppA, update)).ToList();
        outcomes.Count(o => o.Start is not null).Should().Be(9, "the start used one of the 10 tokens");
        outcomes.Last().Reply!.Code.Should().Be(IslandSdkErrors.RateLimited);

        time.Advance(TimeSpan.FromSeconds(1));
        handler.Handle(AppA, update).Start.Should().NotBeNull();
    }

    [Fact]
    public void Update_changes_only_what_it_sends_and_a_rejected_update_changes_nothing()
    {
        var handler = new SdkRequestHandler(new ManualTimeProvider(), _ => false);
        var start = Start("merge", "Deploying");
        start.Expanded!.Subtitle = "Stage 1";
        handler.Handle(AppA, start);

        var progress = Op(IslandSdkOps.Update, "merge");
        progress.Expanded = new IslandSdkExpanded { Progress = 0.4 };
        var merged = handler.Handle(AppA, progress).Start!;
        merged.Expanded!.Title.Should().Be("Deploying");
        merged.Expanded.Subtitle.Should().Be("Stage 1");
        merged.Expanded.Progress.Should().Be(0.4);

        var bad = Op(IslandSdkOps.Update, "merge");
        bad.Expanded = new IslandSdkExpanded { Title = "Changed", Progress = double.NaN };
        handler.Handle(AppA, bad).Reply!.Code.Should().Be(IslandSdkErrors.BadRequest);
        var after = Op(IslandSdkOps.Update, "merge");
        after.Compact = new IslandSdkCompact { TrailingText = "40%" };
        handler.Handle(AppA, after).Start!.Expanded!.Title.Should().Be("Deploying", "the rejected update was not half-applied");
    }

    [Fact]
    public void Buttons_links_and_end_are_validated()
    {
        var handler = new SdkRequestHandler(new ManualTimeProvider(), _ => false);
        var tooMany = Start("b");
        tooMany.Expanded!.Buttons = [.. Enumerable.Range(0, 4).Select(i => new IslandSdkButton { Id = $"b{i}", Label = "x" })];
        handler.Handle(AppA, tooMany).Reply!.Code.Should().Be(IslandSdkErrors.BadRequest);

        var reserved = Start("b");
        reserved.Expanded!.Buttons = [new IslandSdkButton { Id = "open", Label = "Open" }];
        handler.Handle(AppA, reserved).Reply!.Code.Should().Be(IslandSdkErrors.BadRequest);

        var link = Start("b");
        link.OpenUri = "file:///C:/Windows/System32/calc.exe";
        handler.Handle(AppA, link).Reply!.Code.Should().Be(IslandSdkErrors.BadRequest);

        var ok = Start("b");
        ok.Expanded!.Buttons = [new IslandSdkButton { Id = "cancel", Label = "Cancel" }];
        handler.Handle(AppA, ok).Start!.Expanded!.Buttons!.Single().Id.Should().Be("cancel");
        handler.Find(SdkRequestHandler.EngineId(AppA, "b"))!.Value.Buttons.Should().Equal("cancel");

        var end = Op(IslandSdkOps.End, "b");
        end.FinalText = "Done ✓";
        end.DismissAfterSeconds = 99;
        var ended = handler.Handle(AppA, end);
        ended.EndId.Should().Be(SdkRequestHandler.EngineId(AppA, "b"));
        ended.DismissAfter.Should().Be(TimeSpan.FromSeconds(IslandSdkLimits.MaxDismissSeconds));
        handler.Handle(AppA, Op(IslandSdkOps.End, "b")).Reply!.Code.Should().Be(IslandSdkErrors.NotFound);
    }

    [Fact]
    public void Blocked_apps_are_refused_and_their_activities_removed()
    {
        var blocked = new HashSet<string>();
        var handler = new SdkRequestHandler(new ManualTimeProvider(), blocked.Contains);
        handler.Handle(AppA, Start("x"));

        blocked.Add(AppA.Key);
        handler.RemoveClient(AppA.Key).Should().Equal(SdkRequestHandler.EngineId(AppA, "x"));
        handler.Handle(AppA, Start("y")).Reply!.Code.Should().Be(IslandSdkErrors.Blocked);
        handler.Handle(AppB, Start("y")).Start.Should().NotBeNull();
    }

    // ---------------------------------------------------------------- pipe: security, end to end, malformed, oversized, reconnect

    [Fact]
    public void Pipe_is_owned_by_and_open_only_to_the_current_user()
    {
        var security = SdkPipeServer.CreateSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.GetOwner(typeof(SecurityIdentifier)).Should().Be(user);
        security.AreAccessRulesProtected.Should().BeTrue("no inherited access");
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        rules.Where(r => r.AccessControlType == AccessControlType.Allow).Select(r => r.IdentityReference).Should().Equal(user);
        rules.Should().Contain(r => r.AccessControlType == AccessControlType.Deny
            && r.IdentityReference.Equals(new SecurityIdentifier(WellKnownSidType.NetworkSid, null)));
    }

    [Fact]
    public async Task Client_and_Island_talk_end_to_end_including_button_presses()
    {
        using var fixture = SdkFixture.Start();
        using var client = await fixture.ConnectAsync("Builder");
        var pressed = new TaskCompletionSource<IslandActionEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.ActionReceived += (_, e) => pressed.TrySetResult(e);

        var spec = new IslandActivitySpec("deploy", "Deploying", progress: 0.1) { TrailingText = "10%", Icon = Png(64, 64) };
        spec.Buttons.Add(new IslandButton("cancel", "Cancel"));
        var activity = await client.StartAsync(spec, Ct);
        client.ServerVersion.Should().StartWith("IrisAura.Island/");
        await activity.UpdateAsync(progress: 0.5, cancellationToken: Ct);
        (await client.QueryAsync(Ct)).Should().Equal("deploy");

        var engineId = fixture.Sink.Started.Last().Id;
        fixture.Sink.Started.Last().Expanded!.Progress.Should().Be(0.5);
        ((SdkVisuals)fixture.Sink.Started.Last().View!).Icon.Should().NotBeNull();
        fixture.Provider.OnAction(engineId, "cancel").Should().BeTrue();
        (await pressed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Button.Should().Be("cancel");
        fixture.Provider.OnAction(engineId, "not-a-button").Should().BeFalse();

        await activity.EndAsync("Deployed ✓", cancellationToken: Ct);
        fixture.Sink.Ended.Should().Contain(engineId);
    }

    [Fact]
    public async Task Refusals_come_back_as_exceptions()
    {
        using var fixture = SdkFixture.Start();
        using var client = await fixture.ConnectAsync("Builder");
        var act = () => client.UpdateAsync("never-started", new IslandActivityUpdate { Progress = 0.2 }, Ct);
        (await act.Should().ThrowAsync<IslandSdkException>()).Which.Code.Should().Be(IslandSdkErrors.NotFound);
    }

    [Fact]
    public async Task Malformed_and_wrong_version_lines_get_errors_and_a_flood_of_errors_disconnects()
    {
        using var fixture = SdkFixture.Start();
        using var raw = await fixture.ConnectRawAsync();
        (await raw.ReadAsync()).Op.Should().Be(IslandSdkOps.Hello);

        await raw.WriteAsync("{ not json");
        (await raw.ReadAsync()).Code.Should().Be(IslandSdkErrors.BadJson);
        await raw.WriteAsync("""{"v":7,"op":"start","id":"x"}""");
        (await raw.ReadAsync()).Code.Should().Be(IslandSdkErrors.BadVersion);
        await raw.WriteAsync("[1,2,3]");
        (await raw.ReadAsync()).Code.Should().Be(IslandSdkErrors.BadJson);

        for (var i = 0; i < SdkActivityProvider.MaxConsecutiveErrors; i++)
        {
            await raw.WriteAsync("nope");
        }

        (await raw.ReadUntilClosedAsync()).Should().BeTrue("the Island hangs up after 20 errors in a row");
        fixture.Sink.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task A_message_over_64_KB_closes_the_connection()
    {
        using var fixture = SdkFixture.Start();
        using var raw = await fixture.ConnectRawAsync();
        await raw.ReadAsync(); // hello
        var huge = """{"v":1,"op":"start","id":"x","expanded":{"title":" """ + new string('a', IslandSdkLimits.MaxMessageBytes) + "\"}}";
        await raw.WriteAsync(huge, newline: false);
        (await raw.ReadUntilClosedAsync()).Should().BeTrue();
        fixture.Sink.Started.Should().BeEmpty();

        using var next = await fixture.ConnectAsync("Builder");
        await next.StartAsync(new IslandActivitySpec("after", "Still works"), Ct);
        fixture.Sink.Started.Should().ContainSingle("other clients are unaffected");
    }

    [Fact]
    public async Task Activities_outlive_the_connection_and_a_reconnected_client_takes_over_actions()
    {
        using var fixture = SdkFixture.Start();
        using (var first = await fixture.ConnectAsync("Builder"))
        {
            var spec = new IslandActivitySpec("job", "Working");
            spec.Buttons.Add(new IslandButton("stop", "Stop"));
            await first.StartAsync(spec, Ct);
        }

        await Task.Delay(200, Ct);
        fixture.Sink.Ended.Should().BeEmpty("a script can push and exit");
        var engineId = fixture.Sink.Started.Single().Id;
        fixture.Provider.OnAction(engineId, "stop").Should().BeFalse("nobody is connected to receive it");

        using var second = await fixture.ConnectAsync("Builder");
        await second.UpdateAsync("job", new IslandActivityUpdate { Progress = 0.9 }, Ct);
        var pressed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        second.ActionReceived += (_, e) => pressed.TrySetResult(e.Button);
        fixture.Provider.OnAction(engineId, "stop").Should().BeTrue();
        (await pressed.Task.WaitAsync(TimeSpan.FromSeconds(5), Ct)).Should().Be("stop");

        using var stranger = await fixture.ConnectAsync("Someone else");
        var hijack = () => stranger.EndAsync("job", cancellationToken: Ct);
        await hijack.Should().ThrowAsync<IslandSdkException>();
        fixture.Sink.Ended.Should().BeEmpty();
    }

    [Fact]
    public async Task Rate_limit_is_enforced_over_the_pipe()
    {
        using var fixture = SdkFixture.Start();
        using var client = await fixture.ConnectAsync("Builder");
        await client.StartAsync(new IslandActivitySpec("fast", "Fast"), Ct);
        var refused = 0;
        for (var i = 0; i < 15; i++)
        {
            try
            {
                await client.UpdateAsync("fast", new IslandActivityUpdate { Progress = i / 15.0 }, Ct);
            }
            catch (IslandSdkException ex) when (ex.Code == IslandSdkErrors.RateLimited)
            {
                refused++;
            }
        }

        refused.Should().BeGreaterThan(0);
        fixture.Sink.Started.Count.Should().BeLessThanOrEqualTo(IslandSdkLimits.MaxUpdatesPerSecond + 2);
    }

    [Fact]
    public async Task Blocking_an_app_ends_its_activities_and_refuses_it()
    {
        using var fixture = SdkFixture.Start();
        using var client = await fixture.ConnectAsync("Builder");
        await client.StartAsync(new IslandActivitySpec("x", "X"), Ct);
        var app = fixture.Provider.Apps.Single();
        app.Connected.Should().BeTrue();
        app.Activities.Should().Be(1);

        fixture.Provider.SetBlocked(app.Key, true);
        fixture.Sink.Ended.Should().ContainSingle();
        var again = () => client.StartAsync(new IslandActivitySpec("y", "Y"), Ct);
        (await again.Should().ThrowAsync<IslandSdkException>()).Which.Code.Should().Be(IslandSdkErrors.Blocked);
        fixture.Provider.BlockedApps.Should().Contain(app.Key);
    }

    [Fact]
    public void A_squatted_pipe_name_makes_the_provider_unavailable_instead_of_sharing_it()
    {
        var name = $"IrisAura.Island.Test.{Guid.NewGuid():N}";
        using var squatter = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var sink = new RecordingSink();
        var provider = new SdkActivityProvider(pipeName: () => name);

        provider.Start(sink);

        sink.Unavailable.Should().Contain("in use");
        provider.Stop();
    }

    [Fact]
    public async Task Client_refuses_a_server_that_is_not_IrisAura_Island()
    {
        var name = $"IrisAura.Island.Test.{Guid.NewGuid():N}";
        using var fake = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var waiting = fake.WaitForConnectionAsync(Ct);

        foreach (var mode in new[] { IslandServerVerification.SignedIsland, IslandServerVerification.UnsignedDevelopmentBuild })
        {
            var connect = () => IslandClient.ConnectAsync(new IslandClientOptions { PipeName = name, Verification = mode }, Ct);
            (await connect.Should().ThrowAsync<IslandServerVerificationException>()).Which.Message.Should().Contain("not IrisAura.Island.exe");
            await waiting;
            fake.Disconnect();
            waiting = fake.WaitForConnectionAsync(Ct);
        }
    }

    [Fact]
    public void Unsigned_files_fail_the_signature_check()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "IrisAura.Island.Tests.dll");
        var check = () => ServerVerifier.VerifySignature(path, new List<string>());
        check.Should().Throw<IslandServerVerificationException>().WithMessage("*not signed*");
    }

    // ---------------------------------------------------------------- helpers

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string DataUri(byte[] png) => "data:image/png;base64," + Convert.ToBase64String(png);

    /// <summary>PNG signature + IHDR with the given size (enough for the header check and WPF's decoder is not involved).</summary>
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 13);
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20), height);
        bytes[24] = 8;
        bytes[25] = 6;
        return bytes;
    }

    private static byte[] Jpeg(int width, int height)
    {
        var bytes = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x04, 0x00, 0x00, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0, 0, 0, 0, 0x03 };
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(13), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(15), (ushort)width);
        return [.. bytes, .. new byte[16]];
    }

    private sealed class SdkFixture : IDisposable
    {
        private SdkFixture(string name, SdkActivityProvider provider, RecordingSink sink)
        {
            Name = name;
            Provider = provider;
            Sink = sink;
        }

        public string Name { get; }

        public SdkActivityProvider Provider { get; }

        public RecordingSink Sink { get; }

        public static SdkFixture Start()
        {
            var name = $"IrisAura.Island.Test.{Guid.NewGuid():N}";
            var sink = new RecordingSink();
            var provider = new SdkActivityProvider(pipeName: () => name);
            provider.Start(sink);
            sink.Unavailable.Should().BeNull();
            return new SdkFixture(name, provider, sink);
        }

        public Task<IslandClient> ConnectAsync(string app) =>
            IslandClient.ConnectAsync(new IslandClientOptions { PipeName = Name, AppName = app, Verification = IslandServerVerification.None }, Ct);

        public async Task<RawClient> ConnectRawAsync()
        {
            var pipe = new NamedPipeClientStream(".", Name, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(2000, Ct);
            return new RawClient(pipe);
        }

        public void Dispose() => Provider.Stop();
    }

    private sealed class RawClient(NamedPipeClientStream pipe) : IDisposable
    {
        private readonly StreamReader _reader = new(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        public async Task WriteAsync(string line, bool newline = true)
        {
            var bytes = Encoding.UTF8.GetBytes(newline ? line + "\n" : line);
            try
            {
                await pipe.WriteAsync(bytes, Ct);
                await pipe.FlushAsync(Ct);
            }
            catch (IOException)
            {
                // The Island may hang up mid-write (oversized message).
            }
        }

        public async Task<IslandSdkMessage> ReadAsync()
        {
            var line = await _reader.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct);
            return JsonSerializer.Deserialize<IslandSdkMessage>(line!, SdkJson.Options)!;
        }

        /// <summary>Reads until the Island closes the pipe; true if it did within 5 s.</summary>
        public async Task<bool> ReadUntilClosedAsync()
        {
            try
            {
                while (await _reader.ReadLineAsync(Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(5), Ct) is not null)
                {
                }

                return true;
            }
            catch (IOException)
            {
                return true;
            }
            catch (TimeoutException)
            {
                return false;
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            pipe.Dispose();
        }
    }

    private sealed class RecordingSink : IActivitySink
    {
        private readonly object _gate = new();
        private readonly List<ActivitySpec> _started = [];
        private readonly List<string> _ended = [];

        public List<ActivitySpec> Started
        {
            get
            {
                lock (_gate)
                {
                    return [.. _started];
                }
            }
        }

        public List<string> Ended
        {
            get
            {
                lock (_gate)
                {
                    return [.. _ended];
                }
            }
        }

        public string? Unavailable { get; private set; }

        public void Start(ActivitySpec spec)
        {
            lock (_gate)
            {
                _started.Add(spec);
            }
        }

        public void Update(ActivityUpdate update)
        {
        }

        public void End(string id, string? finalText = null, TimeSpan? dismissAfter = null)
        {
            lock (_gate)
            {
                _ended.Add(id);
            }
        }

        public void ReportFailure(Exception error) => Unavailable = "failed: " + error.Message;

        public void ReportUnavailable(string reason) => Unavailable = reason;
    }
}
