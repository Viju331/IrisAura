using IrisAura.Island.Activities;

namespace IrisAura.Island.Providers.Privacy;

/// <summary>
/// Microphone and camera indicators (BLUEPRINT §8.6.7). Publishes one <see cref="ActivityKind.PrivacyIndicator"/>
/// activity per device while any app uses it; the Island draws these as dots at the trailing edge of whatever it
/// shows and lists the apps in its menu. Colours come from the brand palette (mic = Aura.Violet, camera = Aura.Cyan),
/// not the orange/green convention of other platforms.
/// </summary>
public sealed class PrivacyProvider(ICapabilityUsageSource source) : IActivityProvider
{
    public const string ProviderId = "privacy";
    public const string MicrophoneId = "privacy:microphone";
    public const string CameraId = "privacy:camera";

    private readonly object _gate = new();
    private IActivitySink? _sink;
    private CapabilitySnapshot _shown = CapabilitySnapshot.Empty;

    public string Id => ProviderId;

    public string DisplayName => "Microphone and camera indicators";

    public void Start(IActivitySink sink)
    {
        lock (_gate)
        {
            _sink = sink;
            _shown = CapabilitySnapshot.Empty;
        }

        source.Changed += OnChanged;
        source.Acquire();
        Publish(source.Current);
    }

    public void Stop()
    {
        source.Changed -= OnChanged;
        source.Release();
        lock (_gate)
        {
            _sink = null;
        }
    }

    public bool OnAction(string activityId, string actionId) => false;

    /// <summary>The indicator activity for one device, or <see langword="null"/> when nobody uses it.</summary>
    public static ActivitySpec? Indicator(Capability capability, IReadOnlyList<CapabilityUser> users)
    {
        if (users.Count == 0)
        {
            return null;
        }

        var device = capability == Capability.Microphone ? "Microphone" : "Camera";
        var apps = string.Join(", ", users.Select(u => u.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase));
        return new ActivitySpec(
            capability == Capability.Microphone ? MicrophoneId : CameraId,
            ActivityKind.PrivacyIndicator,
            new CompactContent(LeadingGlyph: capability == Capability.Microphone ? "Aura.Glyph.Mic" : "Aura.Glyph.Camera", LeadingText: device),
            new ExpandedContent($"{device} in use", apps),
            Priority: 0,
            IsTransient: false,
            AccessibleText: $"{device} in use by {apps}");
    }

    private void OnChanged(object? sender, CapabilitySnapshot snapshot) => Publish(snapshot);

    private void Publish(CapabilitySnapshot snapshot)
    {
        IActivitySink? sink;
        CapabilitySnapshot previous;
        lock (_gate)
        {
            if (_sink is null)
            {
                return;
            }

            sink = _sink;
            previous = _shown;
            _shown = snapshot;
        }

        Apply(sink, Capability.Microphone, previous.Microphone, snapshot.Microphone);
        Apply(sink, Capability.Camera, previous.Camera, snapshot.Camera);
    }

    private static void Apply(IActivitySink sink, Capability capability, IReadOnlyList<CapabilityUser> before, IReadOnlyList<CapabilityUser> now)
    {
        var id = capability == Capability.Microphone ? MicrophoneId : CameraId;
        if (Indicator(capability, now) is { } spec)
        {
            if (!before.Select(u => u.Key).SequenceEqual(now.Select(u => u.Key), StringComparer.OrdinalIgnoreCase))
            {
                sink.Start(spec);
            }
        }
        else if (before.Count > 0)
        {
            sink.End(id);
        }
    }
}
