using System.Text.RegularExpressions;

namespace Gecko.SharedKernel;

/// <summary>
/// ISO 6346 container numbers — owner code, equipment category, serial and the
/// check digit that proves the other ten characters were read correctly.
///
/// WHY THIS IS IN C# WHEN dbo.fn_container_check_digit ALREADY EXISTS:
/// the gate has a 3-second budget for the whole transaction (ADR-007), and a
/// round trip to SQL to find out that an OCR camera misread one character is
/// three hundred milliseconds spent learning nothing. The SQL function stays —
/// it is what lets a fixture script or an ETL validate in bulk — and the two are
/// checked against each other on the 30 real production numbers in dev_04.
///
/// Pure: no EF, no ASP.NET, no database. Lives in SharedKernel because MDM
/// (registry) and TOS (assignment, and the gate in Phase 5) both need the one
/// definition; two copies of a checksum drift.
/// </summary>
public static partial class ContainerNumber
{
    /// <summary>
    /// Owner code (3 letters) + equipment category + 6-digit serial + check digit.
    /// The category is U (freight container), J (detachable freight-related
    /// equipment) or Z (trailer/chassis) — no other letter is valid there.
    /// </summary>
    [GeneratedRegex("^[A-Z]{3}[UJZ][0-9]{7}$")]
    private static partial Regex IsoFormed { get; }

    /// <summary>Any box number the depot may meet: 4–11 letters or digits (gecko_tos 28).</summary>
    [GeneratedRegex("^[A-Z0-9]{4,11}$")]
    private static partial Regex AnyFormed { get; }

    /// <summary>
    /// Owner 2026-10-08: "some containers don't follow ISO container specs" (TMSKORAKIT: ANVY6399011,
    /// PLD9256601, RBI051, 4043535…). A number is accepted when it is 4–11 letters or digits; whether it
    /// is ISO 6346 only matters where a depot sets gate.enforce_check_digit (<see cref="IsValid"/>).
    /// </summary>
    public static bool IsWellFormed(string? containerNo) =>
        !string.IsNullOrWhiteSpace(containerNo) && AnyFormed.IsMatch(containerNo);

    /// <summary>Owner code + U/J/Z + 7 digits: the ISO 6346 shape.</summary>
    public static bool IsIsoFormed(string? containerNo) =>
        !string.IsNullOrWhiteSpace(containerNo) && IsoFormed.IsMatch(containerNo);

    /// <summary>Normalises what a human or an OCR camera produced: trim, strip spaces and dashes, upper-case.</summary>
    public static string Normalise(string containerNo) =>
        containerNo.Replace(" ", "").Replace("-", "").Trim().ToUpperInvariant();

    /// <summary>
    /// The ISO 6346 check digit for the first ten characters.
    /// Each character gets a value, weighted by 2^position, summed, mod 11 —
    /// and a remainder of 10 is written as 0, which is the rule everyone
    /// re-implementing this forgets. Returns null if the input is not usable.
    /// </summary>
    public static int? CheckDigitOf(string? containerNo)
    {
        if (containerNo is null) return null;
        var value = Normalise(containerNo);
        if (value.Length < 10) return null;

        var sum = 0;
        for (var i = 0; i < 10; i++)
        {
            var c = value[i];
            int characterValue;

            if (c is >= 'A' and <= 'Z') characterValue = LetterValue(c);
            else if (c is >= '0' and <= '9') characterValue = c - '0';
            else return null;

            sum += characterValue << i;   // 2^i
        }

        var remainder = sum % 11;
        return remainder == 10 ? 0 : remainder;
    }

    /// <summary>True when the number is ISO 6346 shaped AND its eleventh character is the correct check digit.</summary>
    public static bool IsValid(string? containerNo)
    {
        if (!IsIsoFormed(containerNo)) return false;
        var value = Normalise(containerNo!);
        return CheckDigitOf(value) is { } expected && value[10] - '0' == expected;
    }

    /// <summary>The owner code and equipment category — 'MSKU' of 'MSKU1234567'. Used to resolve the owning line.</summary>
    public static string? PrefixOf(string? containerNo) =>
        IsIsoFormed(containerNo) ? Normalise(containerNo!)[..4] : null;

    /// <summary>
    /// Letter values run 10..38 but SKIP every multiple of 11, because a value
    /// divisible by 11 would be invisible to a mod-11 checksum: A=10, B=12 … K=21,
    /// L=23 … U=32, V=34 … Z=38.
    /// </summary>
    private static int LetterValue(char letter)
    {
        var value = 10 + (letter - 'A');
        if (value >= 11) value++;
        if (value >= 22) value++;
        if (value >= 33) value++;
        return value;
    }
}
