using System.Text.RegularExpressions;

namespace HshDetectionEngin.Plate;

/// <summary>
/// Class mapping for the Iranian plate character model.
/// Index = class id predicted by the network (class 30 = whole plate box).
/// </summary>
internal static class PersianPlate
{
    /// <summary>The 'plate' class id.</summary>
    public const int PlateClassId = 30;

    /// <summary>English class names (index = class id).</summary>
    public static readonly string[] ClassNames =
    [
        "0", "1", "2", "3", "4", "5", "6", "7", "8", "9",
        "Alef", "Be", "Te", "Se", "Jim", "Dal", "Sin", "Shin",
        "Sad", "Ta", "Za", "Eyn", "Ghaf", "Lam", "Mim", "Nun",
        "He", "Vav", "Pe", "Zhe",
        "plate",
        "Ye", "Ze"
    ];

    private static readonly Dictionary<int, string> CharMap = new()
    {
        [0] = "\u06F0", // ۰
        [1] = "\u06F1", // ۱
        [2] = "\u06F2", // ۲
        [3] = "\u06F3", // ۳
        [4] = "\u06F4", // ۴
        [5] = "\u06F5", // ۵
        [6] = "\u06F6", // ۶
        [7] = "\u06F7", // ۷
        [8] = "\u06F8", // ۸
        [9] = "\u06F9", // ۹
        [10] = "\u0627", // الف
        [11] = "\u0628", // ب
        [12] = "\u062A", // ت
        [13] = "\u062B", // ث
        [14] = "\u062C", // ج
        [15] = "\u062F", // د
        [16] = "\u0633", // س
        [17] = "\u0634", // ش
        [18] = "\u0635", // ص
        [19] = "\u0637", // ط
        [20] = "\u0638", // ظ
        [21] = "\u0639", // ع
        [22] = "\u0642", // ق
        [23] = "\u0644", // ل
        [24] = "\u0645", // م
        [25] = "\u0646", // ن
        [26] = "\u0647", // ه
        [27] = "\u0648", // و
        [28] = "\u067E", // پ
        [29] = "\u0698", // ژ
        // 30 = 'plate' -> not a character
        [31] = "\u06CC", // ی
        [32] = "\u0632", // ز
    };

    // Iranian private plates are eight characters in the OCR output:
    // number-number-letter-number-number-number-number-number.
    private static readonly Regex IranianPlatePattern = new(
        @"^\d{2}[آ-ی]\d{5}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Persian glyph for a class id, or empty string.</summary>
    public static string CharOf(int classId) =>
        CharMap.TryGetValue(classId, out var ch) ? ch : "";

    /// <summary>Display name (English) for a class id.</summary>
    public static string Name(int classId) =>
        classId >= 0 && classId < ClassNames.Length ? ClassNames[classId] : classId.ToString();

    public static bool IsPlate(int classId) => classId == PlateClassId;

    public static bool IsValidIranianPlate(string plate)
    {
        if (string.IsNullOrWhiteSpace(plate)) return false;
        plate = plate.Trim()
            .Replace('۰', '0').Replace('۱', '1').Replace('۲', '2')
            .Replace('۳', '3').Replace('۴', '4').Replace('۵', '5')
            .Replace('۶', '6').Replace('۷', '7').Replace('۸', '8').Replace('۹', '9');
        return IranianPlatePattern.IsMatch(plate);
    }
}
