using System.Text.Json;
using Serilog;

namespace IrisAura.Island.Providers.Timers;

/// <summary>
/// A countdown timer (BLUEPRINT §8.6.10). Immutable; also the view payload of the timer activity and what is
/// persisted (durations and times only).
/// </summary>
/// <param name="Duration">Total length (grows with +1 min).</param>
/// <param name="EndsAt">When it ends (running), or <see langword="null"/> while paused.</param>
/// <param name="PausedRemaining">Time left when paused, or <see langword="null"/> while running.</param>
public sealed record TimerState(TimeSpan Duration, DateTimeOffset? EndsAt, TimeSpan? PausedRemaining)
{
    public bool IsPaused => PausedRemaining is not null;

    public static TimerState StartNew(TimeSpan duration, DateTimeOffset now) => new(duration, now + duration, null);

    public TimeSpan Remaining(DateTimeOffset now)
    {
        var left = PausedRemaining ?? (EndsAt is { } end ? end - now : TimeSpan.Zero);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    /// <summary>Fraction remaining (1 → 0), for the ring.</summary>
    public double FractionRemaining(DateTimeOffset now) =>
        Duration <= TimeSpan.Zero ? 0 : Math.Clamp(Remaining(now) / Duration, 0, 1);

    public TimerState Pause(DateTimeOffset now) => IsPaused ? this : new(Duration, null, Remaining(now));

    public TimerState Resume(DateTimeOffset now) => IsPaused ? new(Duration, now + PausedRemaining!.Value, null) : this;

    public TimerState AddTime(TimeSpan extra) => IsPaused
        ? this with { Duration = Duration + extra, PausedRemaining = PausedRemaining + extra }
        : this with { Duration = Duration + extra, EndsAt = EndsAt + extra };

    /// <summary>"mm:ss", or "h:mm:ss" from one hour; rounds up so the last second shows "00:01", not "00:00".</summary>
    public static string Format(TimeSpan remaining)
    {
        var total = (long)Math.Ceiling(Math.Max(0, remaining.TotalSeconds));
        var hours = total / 3600;
        var minutes = total / 60 % 60;
        var seconds = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{seconds:00}" : $"{total / 60:00}:{seconds:00}";
    }

    public static string Describe(TimeSpan duration) => duration.TotalMinutes >= 1 && duration.Seconds == 0
        ? $"{(int)duration.TotalMinutes} min timer"
        : $"{Format(duration)} timer";
}

/// <summary>
/// Persists the running timer in <c>%LocalAppData%\IrisAura\island-timer.json</c> so it survives restarts
/// (BLUEPRINT §8.6.10). Atomic writes; a missing or corrupt file means no timer.
/// </summary>
public sealed class TimerStore(string path)
{
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IrisAura", "island-timer.json");

    public string FilePath => path;

    public TimerState? Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<TimerState>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning("Timer file unreadable ({Error}); ignoring it", ex.GetType().Name);
            return null;
        }
    }

    public void Save(TimerState? state)
    {
        try
        {
            if (state is null)
            {
                File.Delete(path);
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Timer could not be saved ({Error})", ex.GetType().Name);
        }
    }
}
