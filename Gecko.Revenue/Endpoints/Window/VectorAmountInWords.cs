using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Gecko.Revenue.Endpoints.Window;

/// <summary>
/// The amount in words on the tax invoice: a line-for-line port of the KPS full tax invoice's
/// <c>Code.ExpandPrice</c> (TMS.Operation.Gate.FullTaxInvoice(KPS).rdl), quirks kept so the printout reads
/// as Vector's did — e.g. 1,230.50 is "One Thousand Two Hundred Thirty  Baht  And Fifty  Satang" and a whole
/// amount ends "Zero Satang". The caller upper-cases it, as the report did.
/// </summary>
internal static partial class VectorAmountInWords
{
    private static readonly string[] Suffixes = ["Thousand ", "Million ", "Billion ", "Trillion ", "Quadrillion ", "Quintillion ", "Sextillion "];
    private static readonly string[] Units = ["", "One ", "Two ", "Three ", "Four ", "Five ", "Six ", "Seven ", "Eight ", "Nine "];
    private static readonly string[] Tens = ["Twenty ", "Thirty ", "Forty ", "Fifty ", "Sixty ", "Seventy ", "Eighty ", "Ninety "];
    private static readonly string[] Digits = ["Ten ", "Eleven ", "Twelve ", "Thirteen ", "Fourteen ", "Fifteen ", "Sixteen ", "Seventeen ", "Eighteen ", "Nineteen"];

    [GeneratedRegex(@"^-?\d+(\.\d{2})?$")]
    private static partial Regex Shape();

    public static string ExpandPrice(decimal price)
    {
        // Vector's VB threw on a negative amount; a receipt never has one, so it prints nothing.
        if (price < 0) return "";
        var text = price.ToString("##############.00", CultureInfo.InvariantCulture);
        var temp = new StringBuilder();
        if (!Shape().IsMatch(text)) return "";

        var parts = text.Split('.');
        var dollars = parts[0];
        var cents = parts[1];
        var whole = decimal.Parse(dollars, CultureInfo.InvariantCulture);
        var fraction = int.Parse(cents, CultureInfo.InvariantCulture);

        if (whole > 1)
        {
            temp.Append(ExpandIntegerNumber(dollars) + " Baht  ");
            if (fraction > 0) temp.Append("And ");
        }
        else if (whole == 0)
        {
            temp.Append(ExpandIntegerNumber(dollars) + "Zero Baht");
            temp.Append("And ");   // VB: If CInt(cents) >= 0 — always.
        }
        else if (whole == 1)
        {
            temp.Append(ExpandIntegerNumber(dollars) + " Baht ");
        }

        if (fraction > 1) temp.Append(ExpandIntegerNumber(cents) + " Satang");
        else if (fraction == 0) temp.Append(ExpandIntegerNumber(cents) + "Zero Satang");
        else if (fraction == 1) temp.Append(ExpandIntegerNumber(cents) + " Satang ");
        return temp.ToString();
    }

    private static string ExpandIntegerNumber(string numberText)
    {
        var temp = new StringBuilder();
        var number = new string('0', 3 - numberText.Length % 3) + numberText;
        var j = -1;
        // VB: For i = Len(number) - 2 To 1 Step -3, Mid(number, i, 3) — 1-based.
        for (var i = number.Length - 2; i >= 1; i -= 3)
        {
            var part = number.Substring(i - 1, 3);
            if (int.Parse(part, CultureInfo.InvariantCulture) > 0 && j > -1) temp.Insert(0, Suffixes[j]);
            temp.Insert(0, Under1000(part));
            j++;
        }
        return temp.ToString();
    }

    private static string Under1000(string part)
    {
        var temp = new StringBuilder();
        if (part.Length == 3 && part[0] != '0') temp.Append(Under100(part[..1]) + "Hundred ");
        temp.Append(Under100(("0" + part)[^2..]));
        return temp.ToString();
    }

    private static string Under100(string part)
    {
        var value = int.Parse(part, CultureInfo.InvariantCulture);
        var last = part[^1] - '0';
        if (value > 19) return Tens[part[0] - '0' - 2] + Units[last];
        if (value >= 10) return Digits[last];
        return Units[last];
    }
}
