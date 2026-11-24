namespace IrisAura.Contracts;

/// <summary>
/// Named-pipe names from BLUEPRINT §0.2. Values are the pipe name only (what
/// <c>NamedPipeServerStream</c> expects), without the <c>\\.\pipe\</c> prefix.
/// </summary>
public static class PipeNames
{
    /// <summary>Prefix of the per-user Island Activity SDK pipe; the user's SID is appended.</summary>
    public const string IslandActivitiesPrefix = "IrisAura.Island.Activities.";

    /// <summary>Face authentication pipe (conditional sign-in track only).</summary>
    public const string FaceAuth = "IrisAura.Face.Auth";

    /// <summary>Face administration pipe (conditional sign-in track only).</summary>
    public const string FaceAdmin = "IrisAura.Face.Admin";

    /// <summary>Read-only Face events pipe consumed by the Island's FaceEventsProvider.</summary>
    public const string FaceEvents = "IrisAura.Face.Events";

    /// <summary>
    /// Returns the Island Activity SDK pipe name for one user, for example
    /// <c>IrisAura.Island.Activities.S-1-5-21-1-2-3-1001</c>.
    /// </summary>
    /// <param name="userSid">The user's SID in string form (<c>S-1-...</c>).</param>
    /// <exception cref="ArgumentException">The value is not a well-formed SID string.</exception>
    public static string IslandActivities(string userSid)
    {
        if (!IsWellFormedSid(userSid))
        {
            throw new ArgumentException("Expected a SID string such as S-1-5-21-...", nameof(userSid));
        }

        return IslandActivitiesPrefix + userSid;
    }

    /// <summary>
    /// Checks the textual SID shape: <c>S-1-</c>, an identifier authority, then one or more
    /// numeric sub-authorities. Only digits and dashes are allowed, so the value cannot inject
    /// path separators or other characters into a pipe name.
    /// </summary>
    internal static bool IsWellFormedSid(string? value)
    {
        if (value is null || value.Length < 5 || value.Length > 184 || !value.StartsWith("S-1-", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = value.Split('-');

        // "S", "1", authority, then at least one sub-authority.
        if (parts.Length < 4)
        {
            return false;
        }

        for (var i = 1; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || part.Length > 10)
            {
                return false;
            }

            foreach (var c in part)
            {
                if (c is < '0' or > '9')
                {
                    return false;
                }
            }
        }

        return true;
    }
}
