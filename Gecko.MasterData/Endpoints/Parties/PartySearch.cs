using System.Text.RegularExpressions;

namespace Gecko.MasterData.Endpoints.Parties;

/// <summary>
/// Turns what a user typed in a party search into the words to look for.
/// People type company names the way they write them on paper: "บ.ประสิทธิ์ เอ็นเตอร์ไพรส์",
/// "บจก. เอชพีเอ็ม", "PRASIT ENTERPRISE CO.,LTD". The stored name may say "บริษัท …", "… จำกัด"
/// or nothing at all, and may space words differently. So the company-type words are dropped
/// and every remaining word must appear somewhere in the party (code, either name, short name,
/// tax id or alias), in any order.
/// </summary>
public static partial class PartySearch
{
    // Company-type words at the start of a word: "บ.ประสิทธิ์" -> "ประสิทธิ์"
    [GeneratedRegex(@"^(ห้างหุ้นส่วนจำกัด|ห้างหุ้นส่วนสามัญ|ห้างหุ้นส่วน|บริษัท|บจก\.?|บมจ\.?|หจก\.?|บ\.)")]
    private static partial Regex ThaiPrefix();

    // Whole words that only say what kind of company it is
    private static readonly HashSet<string> CompanyWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "จำกัด", "จํากัด", "มหาชน", "สำนักงานใหญ่", "CO", "CO.", "CO.,", "CO.,LTD", "CO.,LTD.", "CO.LTD", "CO.LTD.", "CO,LTD", "LTD", "LTD.",
        "LIMITED", "COMPANY", "CORP", "CORP.", "INC", "INC.", "PCL", "PCL.", "LP", "L.P.", "PART.", "&", ",", ".", "-",
    };

    private static readonly char[] EdgePunctuation = { '(', ')', '[', ']', ',', '.', ';', ':', '"', '\'', '-', '/' };

    /// <summary>The words every match must contain. Never empty for a non-blank search.</summary>
    public static IReadOnlyList<string> Terms(string search)
    {
        var raw = search.Trim();
        var words = new List<string>();
        foreach (var part in raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var w = part;
            w = ThaiPrefix().Replace(w, "");
            if (w.EndsWith("จำกัด", StringComparison.Ordinal) && w.Length > "จำกัด".Length) w = w[..^"จำกัด".Length];
            if (CompanyWords.Contains(w)) continue;
            w = w.Trim(EdgePunctuation);
            if (w.Length == 0 || CompanyWords.Contains(w)) continue;
            if (!words.Contains(w, StringComparer.OrdinalIgnoreCase)) words.Add(w);
        }
        // Only company words typed ("บริษัท"): search for what was typed
        return words.Count > 0 ? words : new[] { raw };
    }
}
