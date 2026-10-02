using Gecko.Tos.Domain;

namespace Gecko.Tos.Tests.Domain;

/// <summary>
/// Vector's trip type (GateIn.cs:2191) and its mandatory-field matrix
/// (SetMandatoryByTrip, GateIn.cs:146-197), as pure rules.
/// </summary>
public sealed class GateTripTypeRuleTests
{
    [Fact]
    public void A_drop_off_is_an_IN_and_a_pick_up_an_OUT()
    {
        Assert.Null(GateRules.TripTypeContradiction(GateRules.DropOffContainer, GateRules.In));
        Assert.Null(GateRules.TripTypeContradiction(GateRules.PickUpContainer, GateRules.Out));
        Assert.NotNull(GateRules.TripTypeContradiction(GateRules.DropOffContainer, GateRules.Out));
        Assert.NotNull(GateRules.TripTypeContradiction(GateRules.PickUpContainer, GateRules.In));
    }

    [Fact]
    public void A_drop_off_needs_tare_and_max_gross_a_full_one_its_cargo_weight_and_seal_and_a_full_export_its_permit()
    {
        Assert.Equal(["tareWeightKg", "maxGrossWeightKg"],
            GateRules.MissingForTrip(GateRules.DropOffContainer, GateRules.Empty, isExport: true, null, null, null, null, 0).Keys);
        Assert.Equal(["tareWeightKg", "maxGrossWeightKg", "cargoWeightKg", "seals"],
            GateRules.MissingForTrip(GateRules.DropOffContainer, GateRules.Full, isExport: false, null, null, null, null, 0).Keys);
        Assert.Equal(["cargoWeightKg", "customsPermitNo"],
            GateRules.MissingForTrip(GateRules.DropOffContainer, GateRules.Full, isExport: true, 2200m, 30480m, null, " ", 1).Keys);
        Assert.Empty(GateRules.MissingForTrip(GateRules.DropOffContainer, GateRules.Full, isExport: true, 2200m, 30480m, 18000m, "A0011234", 1));
        Assert.Empty(GateRules.MissingForTrip(GateRules.DropOffContainer, GateRules.Empty, isExport: false, 2200m, 30480m, null, null, 0));
    }

    [Fact]
    public void A_visits_mode_is_what_its_moves_add_up_to()
    {
        Assert.Equal("DROPOFF", GateRules.VisitMode(movesIn: 2, movesOut: 0));
        Assert.Equal("PICKUP", GateRules.VisitMode(movesIn: 0, movesOut: 1));
        Assert.Equal("PICKUP_DROPOFF", GateRules.VisitMode(movesIn: 1, movesOut: 1));
        Assert.Equal("NONE", GateRules.VisitMode(movesIn: 0, movesOut: 0));
    }

    [Fact]
    public void A_pick_up_needs_nothing_extra()
    {
        Assert.Empty(GateRules.MissingForTrip(GateRules.PickUpContainer, GateRules.Full, isExport: true, null, null, null, null, 0));
    }
}
