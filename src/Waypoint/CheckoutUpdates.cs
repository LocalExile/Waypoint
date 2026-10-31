namespace Waypoint;

/// <summary>The update Play made last: what to go back to.</summary>
sealed record CheckoutUpdate(string Commit, string? Backup, DateTimeOffset At);

/// <summary>
/// When Simple mode's Play may update the checkout. LocalExile has no releases, so an update is its default branch as it is
/// now. Only a plain copy of LocalExile is ever moved: anything of the player's own (another branch, a fork as origin,
/// uncommitted changes, commits of their own) keeps the checkout as it is, in either mode.
/// </summary>
static class CheckoutUpdates
{
    /// <summary>How often Play looks for an update: LocalExile changes many times a day.</summary>
    public static readonly TimeSpan Every = TimeSpan.FromDays(1);
    /// <summary>How long the player is offered the way back, and how long going back pauses updates.</summary>
    public static readonly TimeSpan Week = TimeSpan.FromDays(7);

    public static bool Due(bool keepUpToDate, DateTimeOffset? lastCheck, DateTimeOffset? pausedUntil, DateTimeOffset now) =>
        keepUpToDate && (pausedUntil is null || now >= pausedUntil) && (lastCheck is null || now - lastCheck >= Every);

    /// <summary>A plain copy of LocalExile: on its default branch, from LocalExile itself, with nothing of the player's.</summary>
    public static bool PlainCopy(string? branch, string defaultBranch, string? origin, int changes, (int Ahead, int Behind)? compared) =>
        branch == defaultBranch && Git.IsUpstream(origin) && changes == 0 && compared is (0, _);

    /// <summary>The way back stays offered for a week after the update.</summary>
    public static bool CanGoBack(CheckoutUpdate? update, DateTimeOffset now) => update is not null && now - update.At < Week;
}
