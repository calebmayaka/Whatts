namespace Whatts;

/// <summary>Rolling window of power samples for the graph.</summary>
sealed class PowerHistory(TimeSpan window)
{
    readonly Queue<(DateTime Time, double Watts)> samples = new();

    public TimeSpan Window { get; } = window;
    public IReadOnlyCollection<(DateTime Time, double Watts)> Samples => samples;

    public void Add(DateTime now, double? watts)
    {
        if (watts is double w) samples.Enqueue((now, w));
        while (samples.Count > 0 && now - samples.Peek().Time > Window)
            samples.Dequeue();
    }
}

/// <summary>
/// Tracks the current plugged-in or on-battery stretch: when it began, energy moved, peak power.
/// Energy and average come from the fuel gauge's capacity delta, which is steadier than
/// integrating the rate (many firmwares refresh the rate only every few seconds).
/// </summary>
sealed class ChargeSession
{
    bool started;
    uint? startCapacity;

    public bool OnAc { get; private set; }
    public DateTime Start { get; private set; }
    /// <summary>True when the session began at app launch rather than at a plug/unplug.</summary>
    public bool FromLaunch { get; private set; }
    public double? StartPercent { get; private set; }
    /// <summary>Largest power magnitude seen in the session's direction (charging or draining).</summary>
    public double? PeakMagnitude { get; private set; }

    public static readonly TimeSpan MinimumForAverage = TimeSpan.FromMinutes(2);

    public void Update(BatteryReading b, DateTime now)
    {
        bool onAc = b.State.HasFlag(PowerState.OnLine);
        if (!started || onAc != OnAc)
        {
            FromLaunch = !started;
            started = true;
            OnAc = onAc;
            Start = now;
            startCapacity = b.Capacity;
            StartPercent = b.Percent;
            PeakMagnitude = null;
        }

        if (b.Watts is double w)
        {
            double magnitude = onAc ? w : -w;
            if (magnitude > 0 && (PeakMagnitude is null || magnitude > PeakMagnitude))
                PeakMagnitude = magnitude;
        }
    }

    public TimeSpan Elapsed(DateTime now) => now - Start;

    /// <summary>Signed Wh since the session began: + stored, − used.</summary>
    public double? EnergyWh(BatteryReading b) =>
        b.Capacity is uint c && startCapacity is uint s ? ((double)c - s) / 1000.0 : null;

    /// <summary>Signed average watts into the battery; null until enough time has passed to be meaningful.</summary>
    public double? AverageWatts(BatteryReading b, DateTime now)
    {
        var elapsed = Elapsed(now);
        return elapsed >= MinimumForAverage && EnergyWh(b) is double wh ? wh / elapsed.TotalHours : null;
    }
}
