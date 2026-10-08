namespace Gecko.Tos.Contracts;

/// <summary>
/// The trucks a charge was raised for (Revenue's charge.truck_visit_id), as the gate recorded them: the plate and the
/// haulier. The KPS tax invoice prints the haulier on its "ขนส่งโดย" line, as Vector's did — a fact only TOS has.
/// </summary>
public interface ITosTruckVisits
{
    /// <summary>By truck visit id; an id TOS does not know (or another tenant's) is absent.</summary>
    Task<IReadOnlyDictionary<Guid, TosTruckVisit>> VisitsAsync(IReadOnlyCollection<Guid> truckVisitIds, CancellationToken ct);
}

/// <param name="HaulierCode">The haulier on the visit, else the haulier named on a booking it carried; null when neither has one.</param>
public sealed record TosTruckVisit(Guid TruckVisitId, string VisitNo, string TruckPlate, string? HaulierCode);
