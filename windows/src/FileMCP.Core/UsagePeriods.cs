namespace FileMCP.Core;

public enum UsagePeriodPreset
{
    Today,
    Yesterday,
    SevenDays,
    ThirtyDays,
}

public readonly record struct UsagePeriodRange(DateTimeOffset FromUtc, DateTimeOffset ToUtc)
{
    public TimeSpan Duration => ToUtc - FromUtc;
}

public static class UsagePeriodResolver
{
    public static UsagePeriodRange Resolve(UsagePeriodPreset preset, DateTimeOffset nowUtc, TimeZoneInfo timeZone)
    {
        if (timeZone is null) throw new ArgumentNullException(nameof(timeZone));
        var utcNow = nowUtc.ToUniversalTime();
        var localNow = TimeZoneInfo.ConvertTime(utcNow, timeZone);
        var today = localNow.Date;

        return preset switch
        {
            UsagePeriodPreset.Today => new(LocalBoundaryToUtc(today, timeZone), utcNow),
            UsagePeriodPreset.Yesterday => new(LocalBoundaryToUtc(today.AddDays(-1), timeZone), LocalBoundaryToUtc(today, timeZone)),
            UsagePeriodPreset.SevenDays => new(LocalBoundaryToUtc(today.AddDays(-6), timeZone), utcNow),
            UsagePeriodPreset.ThirtyDays => new(LocalBoundaryToUtc(today.AddDays(-29), timeZone), utcNow),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
    }

    internal static DateTimeOffset LocalBoundaryToUtc(DateTime localBoundary, TimeZoneInfo timeZone)
    {
        var local = DateTime.SpecifyKind(localBoundary, DateTimeKind.Unspecified);
        for (var i = 0; i < 180 && timeZone.IsInvalidTime(local); i++) local = local.AddMinutes(1);
        if (timeZone.IsInvalidTime(local)) throw new FileMcpException("Could not resolve local period boundary through timezone transition.");

        var offset = timeZone.IsAmbiguousTime(local)
            ? timeZone.GetAmbiguousTimeOffsets(local).Max()
            : timeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}