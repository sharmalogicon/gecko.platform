namespace Gecko.Tos.Domain;

/// <summary>
/// The cut-off rules the database cannot hold (PLAN §4.1 guards, §10.2), as pure
/// functions so they are tested without a database.
///
/// Vector broke every one of these: yard cut-off after the port cut-off on 141
/// schedules, port cut-off after ETD on 495. The defect view counts them after
/// the fact; this refuses them on save.
/// </summary>
public static class CutoffRules
{
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "PORT_DRY", "PORT_REEFER", "PORT_DG", "YARD_DRY", "YARD_REEFER", "YARD_DG", "CFS_DRY", "CFS_REEFER", "VGM", "SI",
    };

    public static bool IsYard(string kind) => kind.StartsWith("YARD_", StringComparison.Ordinal);

    /// <summary>Vector "CFS Cut-off (Dry / Reefer)" (gecko_tos 21): the depot's CFS cut-off.</summary>
    public static bool IsCfs(string kind) => kind.StartsWith("CFS_", StringComparison.Ordinal);

    /// <summary>A DEPOT's own cut-off (yard or CFS): it may differ by branch and must come before the port's.</summary>
    public static bool IsDepot(string kind) => IsYard(kind) || IsCfs(kind);
    public static bool IsPort(string kind) => kind.StartsWith("PORT_", StringComparison.Ordinal);

    /// <summary>YARD_DRY → PORT_DRY, CFS_REEFER → PORT_REEFER. The port cut-off a depot cut-off feeds.</summary>
    public static string PortKindFor(string depotKind) => "PORT_" + depotKind[(depotKind.IndexOf('_') + 1)..];

    /// <summary>One cut-off as the rules see it. <see cref="LineCode"/> / <see cref="BranchId"/> null = everyone.</summary>
    public sealed record Cutoff(string Kind, string? LineCode, Guid? BranchId, DateTimeOffset At);

    /// <summary>
    /// Every problem in the set at once, keyed <c>cutoffs[i]</c>. <paramref name="lineCodes"/>
    /// are the lines on the call (upper-case).
    /// </summary>
    public static IReadOnlyList<(int Index, string Message)> Validate(
        IReadOnlyList<Cutoff> cutoffs, IReadOnlySet<string> lineCodes, DateTimeOffset etd)
    {
        var problems = new List<(int, string)>();

        for (var i = 0; i < cutoffs.Count; i++)
        {
            var c = cutoffs[i];
            if (!Kinds.Contains(c.Kind))
            {
                problems.Add((i, $"'{c.Kind}' is not a cut-off kind. Use one of: {string.Join(", ", Kinds.Order())}."));
                continue;
            }
            // The port closes for everybody at once; only a depot's own cut-off differs by branch.
            if (c.BranchId is not null && !IsDepot(c.Kind))
                problems.Add((i, $"{c.Kind} cannot be branch-specific — only YARD_* and CFS_* cut-offs differ by depot."));
            if (c.LineCode is not null && !lineCodes.Contains(c.LineCode))
                problems.Add((i, $"{c.Kind} is set for line {c.LineCode}, which is not on this call."));
            if (c.At > etd)
                problems.Add((i, $"{c.Kind} at {c.At:u} is after the ETD ({etd:u}) — the ship would already have sailed."));
        }

        var duplicates = cutoffs
            .Select((c, i) => (c, i))
            .GroupBy(x => (x.c.Kind, x.c.LineCode, x.c.BranchId))
            .Where(g => g.Count() > 1);
        foreach (var d in duplicates)
            problems.Add((d.Last().i, $"{d.Key.Kind} is given twice for the same line and branch."));

        // YARD ≤ PORT wherever the port row applies to the yard row's line — the
        // same comparison as vw_vessel_call_defects YARD_AFTER_PORT.
        for (var i = 0; i < cutoffs.Count; i++)
        {
            var y = cutoffs[i];
            if (!IsDepot(y.Kind) || !Kinds.Contains(y.Kind)) continue;
            var portKind = PortKindFor(y.Kind);
            foreach (var p in cutoffs.Where(p => p.Kind == portKind && (p.LineCode is null || p.LineCode == y.LineCode)))
                if (y.At > p.At)
                    problems.Add((i, $"{y.Kind} at {y.At:u} is after {p.Kind}{(p.LineCode is null ? "" : $" for {p.LineCode}")} at {p.At:u} — boxes still have to reach the terminal."));
        }

        return problems;
    }

    /// <summary>
    /// PLAN Q3: a call with a whole-call PORT_x and no whole-call YARD_x gets one
    /// derived as PORT_x minus the lead time, stored as DERIVED so it is visible
    /// and editable. Vector copied a yard cut-off onto every booking instead.
    /// A branch- or line-specific YARD_x does not count: it narrows the rule for
    /// one depot or line, and every other branch would be left with none.
    /// </summary>
    public static IReadOnlyList<Cutoff> Derive(IReadOnlyList<Cutoff> cutoffs, int leadHours)
    {
        var derived = new List<Cutoff>();
        foreach (var port in cutoffs.Where(c => IsPort(c.Kind) && c.LineCode is null && c.BranchId is null))
        {
            var yardKind = "YARD_" + port.Kind["PORT_".Length..];
            if (cutoffs.Any(c => c.Kind == yardKind && c.LineCode is null && c.BranchId is null)) continue;
            derived.Add(new Cutoff(yardKind, null, null, port.At.AddHours(-leadHours)));
        }
        return derived;
    }
}

/// <summary>The derived status of a call — vessel.vw_vessel_call_status, never stored.</summary>
public static class VesselCallStatus
{
    public const string Open = "OPEN";
    public const string ClosedForReceiving = "CLOSED_FOR_RECEIVING";
    public const string Arrived = "ARRIVED";
    public const string Working = "WORKING";
    public const string DepartedUnconfirmed = "DEPARTED_UNCONFIRMED";
    public const string Departed = "DEPARTED";
    public const string Cancelled = "CANCELLED";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        Open, ClosedForReceiving, Arrived, Working, DepartedUnconfirmed, Departed, Cancelled,
    };
}
