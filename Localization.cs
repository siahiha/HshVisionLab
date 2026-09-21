using System.Globalization;
using System.Text;

namespace HshVisionLab;

internal enum UiLanguage
{
    English,
    Persian
}

internal static class UiLocalization
{
    private static readonly string SettingsPath = Path.Combine(AppContext.BaseDirectory, "ui-language.txt");
    private static readonly Dictionary<string, string> Persian = new(StringComparer.Ordinal)
    {
        ["Camera Management"] = "مدیریت دوربین‌ها",
        ["Plate Detector • Multi Camera"] = "تشخیص پلاک • چند دوربینه",
        ["Plate Detector  /  Multi Camera"] = "تشخیص پلاک  /  چند دوربینه",
        ["+ Add camera"] = "+ افزودن دوربین",
        ["Cameras"] = "دوربین‌ها",
        ["Manage cameras"] = "مدیریت دوربین‌ها",
        ["Camera manager"] = "مدیریت دوربین‌ها",
        ["Add camera"] = "افزودن دوربین",
        ["Start / Stop"] = "شروع / توقف",
        ["Start"] = "شروع",
        ["Stop"] = "توقف",
        ["Edit"] = "ویرایش",
        ["Delete"] = "حذف",
        ["Face database"] = "بانک اطلاعات چهره",
        ["Start All"] = "شروع همه",
        ["Stop All"] = "توقف همه",
        ["Thumbnails"] = "تصاویر کوچک",
        ["Cameras  •  hover to open  •  double-click preview to maximize"] = "دوربین‌ها • برای بازکردن مکث کنید • دوبارکلیک برای بزرگ‌نمایی",
        ["Detected events • All cameras"] = "رخدادهای شناسایی‌شده • همه دوربین‌ها",
        ["Camera thumbnails"] = "تصاویر کوچک دوربین‌ها",
        ["Camera thumbnails  •  double-click a camera to maximize"] = "تصاویر کوچک دوربین‌ها • برای بزرگ‌نمایی دوبارکلیک کنید",
        ["ROI manager"] = "مدیریت ROI",
        ["Ready"] = "آماده",
        ["No cameras configured\r\nClick '+ Add camera' to create the first camera."] = "دوربینی تنظیم نشده است\r\nبرای افزودن اولین دوربین روی «+ افزودن دوربین» کلیک کنید.",
        ["Add Camera"] = "افزودن دوربین",
        ["Edit Camera"] = "ویرایش دوربین",
        ["General"] = "عمومی",
        ["Processing"] = "پردازش",
        ["Camera connection"] = "اتصال دوربین",
        ["Camera name"] = "نام دوربین",
        ["RTSP / camera source"] = "منبع RTSP / دوربین",
        ["RTSP transport"] = "انتقال RTSP",
        ["RTSP receiver"] = "گیرنده RTSP",
        ["Reconnect delay (seconds)"] = "تأخیر اتصال مجدد (ثانیه)",
        ["Performance"] = "کارایی",
        ["Draw boxes and labels"] = "نمایش کادر و برچسب",
        ["Motion gate"] = "فیلتر حرکت",
        ["Enable motion gate"] = "فعال‌سازی فیلتر حرکت",
        ["Motion sampling FPS"] = "نرخ نمونه‌برداری حرکت",
        ["Motion threshold"] = "آستانه حرکت",
        ["Changed percent"] = "درصد تغییر",
        ["Motion ROI scale (%)"] = "مقیاس ROI حرکت (%)",
        ["Motion hold (ms)"] = "مدت نگهداری حرکت (میلی‌ثانیه)",
        ["Scheduling"] = "زمان‌بندی",
        ["Active detection FPS"] = "نرخ تشخیص فعال",
        ["Idle detection FPS"] = "نرخ تشخیص در حالت بی‌کار",
        ["ROI processing targets  •  execution order is the tree order"] = "اهداف پردازش ROI • ترتیب اجرا همان ترتیب درخت است",
        ["ROI properties"] = "ویژگی‌های ROI",
        ["ROI name"] = "نام ROI",
        ["Status"] = "وضعیت",
        ["Detection properties"] = "ویژگی‌های تشخیص",
        ["Enabled"] = "فعال",
        ["Model"] = "مدل",
        ["Input size"] = "اندازه ورودی",
        ["Preprocessing"] = "پیش‌پردازش",
        ["Confidence"] = "اطمینان",
        ["NMS IoU"] = "هم‌پوشانی NMS",
        ["Max processing FPS"] = "حداکثر نرخ پردازش",
        ["Threads (ONNX Runtime IntraOp)"] = "رشته‌ها (ONNX Runtime IntraOp)",
        ["Buffer count (0 = newest only)"] = "تعداد بافر (۰ = فقط جدیدترین)",
        ["Track max misses"] = "حداکثر عدم مشاهده در ردیابی",
        ["FACE DETECTION  •  YuNet finds face boxes"] = "تشخیص چهره • YuNet کادر چهره را پیدا می‌کند",
        ["Detection model"] = "مدل تشخیص",
        ["Detection confidence"] = "اطمینان تشخیص",
        ["Restore Defaults"] = "بازگردانی پیش‌فرض‌ها",
        ["Weak / virtual 6-core"] = "سیستم ضعیف / مجازی ۶ هسته‌ای",
        ["Balanced / normal system"] = "سیستم معمولی / متعادل",
        ["High performance / realtime"] = "سیستم قوی / تقریباً بلادرنگ",
        ["Performance preset applied. Press Save to keep it."] = "پروفایل کارایی اعمال شد؛ برای ذخیره روی ذخیره کلیک کنید.",
        ["Camera settings"] = "تنظیمات دوربین",
        ["Max candidate faces"] = "حداکثر چهره‌های کاندید",
        ["FACE IDENTIFICATION  •  SFace compares identity"] = "شناسایی هویت • SFace هویت را مقایسه می‌کند",
        ["Enable identification"] = "فعال‌سازی شناسایی هویت",
        ["Recognition model (SFace)"] = "مدل شناسایی (SFace)",
        ["Known-person threshold"] = "آستانه فرد شناخته‌شده",
        ["Unknown-person match threshold"] = "آستانه تطبیق فرد ناشناس",
        ["FACE TRACKING AND RECORDING"] = "ردیابی و ثبت چهره",
        ["History record confidence"] = "اطمینان ثبت در تاریخچه",
        ["History event cooldown (seconds)"] = "فاصله ثبت رخداد (ثانیه)",
        ["Tracking IoU"] = "هم‌پوشانی ردیابی",
        ["MODULE OPTIONS  •  custom processing"] = "گزینه‌های ماژول • پردازش سفارشی",
        ["Module settings"] = "تنظیمات ماژول",
        ["Plate detection"] = "تشخیص پلاک",
        ["Face detection"] = "تشخیص چهره",
        ["Save"] = "ذخیره",
        ["Cancel"] = "انصراف",
        ["Close"] = "بستن",
        ["Face database manager"] = "مدیریت بانک اطلاعات چهره",
        ["Face crop"] = "برش چهره",
        ["Person #"] = "شخص #",
        ["Name"] = "نام",
        ["Type"] = "نوع",
        ["Sample #"] = "نمونه #",
        ["Created"] = "ایجادشده",
        ["Source file"] = "فایل منبع",
        ["Image missing"] = "تصویر موجود نیست",
        ["Unknown"] = "ناشناس",
        ["Named"] = "نام‌گذاری‌شده",
        ["Add image"] = "افزودن تصویر",
        ["Add to selected person"] = "افزودن به شخص انتخاب‌شده",
        ["Import folder"] = "واردکردن پوشه",
        ["Group by person"] = "گروه‌بندی بر اساس شخص",
        ["Detection confidence"] = "اطمینان تشخیص",
        ["Delete person"] = "حذف شخص",
        ["Delete sample"] = "حذف نمونه",
        ["Move selected sample"] = "انتقال نمونه انتخاب‌شده",
        ["Select target person"] = "انتخاب شخص مقصد",
        ["Rename person"] = "تغییر نام شخص",
        ["Check similarity"] = "بررسی شباهت",
        ["Similar face samples"] = "نمونه‌های چهره مشابه",
        ["Minimum similarity:"] = "حداقل شباهت:",
        ["Only different people"] = "فقط افراد متفاوت",
        ["Export to folder"] = "خروجی در پوشه",
        ["Merge into first person"] = "ادغام در شخص اول",
        ["Choose a threshold and check for similar samples."] = "یک آستانه انتخاب و شباهت نمونه‌ها را بررسی کنید.",
        ["No similar samples were found."] = "نمونه مشابهی پیدا نشد.",
        ["Run similarity check first."] = "ابتدا بررسی شباهت را اجرا کنید.",
        ["Export completed:"] = "خروجی با موفقیت ایجاد شد:",
        ["Merge people"] = "ادغام افراد",
        ["Export"] = "خروجی",
        ["Folder import"] = "واردکردن پوشه",
        ["Image files|*.jpg;*.jpeg;*.png;*.bmp;*.webp"] = "فایل‌های تصویر|*.jpg;*.jpeg;*.png;*.bmp;*.webp",
        ["Issue a license"] = "صدور مجوز",
        ["English"] = "انگلیسی",
        ["فارسی"] = "فارسی",
        ["Switch language"] = "تغییر زبان",
        ["Start camera"] = "شروع دوربین",
        ["Stop camera"] = "توقف دوربین",
        ["Edit camera"] = "ویرایش دوربین",
        ["Delete camera"] = "حذف دوربین",
        ["Double-click or press F2 to rename"] = "برای تغییر نام دوبارکلیک کنید یا F2 را بزنید",
        ["Name cannot be empty."] = "نام نمی‌تواند خالی باشد.",
        ["This name is already in use."] = "این نام قبلاً استفاده شده است.",
        ["The person could not be found or deleted."] = "شخص پیدا نشد یا حذف نشد.",
        ["Language changed. The application will restart to apply it."] = "زبان تغییر کرد. برنامه برای اعمال تغییرات دوباره راه‌اندازی می‌شود."
    };

    public static UiLanguage Current { get; private set; } = Load();
    public static bool IsPersian => Current == UiLanguage.Persian;

    public static string FontFamily => IsPersian ? "Tahoma" : "Segoe UI";
    public static float DefaultFontSize => IsPersian ? 8F : 9F;

    public static string T(string text)
        => IsPersian && Persian.TryGetValue(text, out string? translated) ? translated : text;

    public static string TFormat(string format, params object[] args)
        => string.Format(CultureInfo.CurrentCulture, T(format), args);

    public static void Apply(Control root)
    {
        root.Text = T(root.Text);
        Font currentFont = root.Font;
        float fontSize = IsPersian && Math.Abs(currentFont.Size - 9F) < 0.1F
            ? DefaultFontSize
            : currentFont.Size;
        root.Font = new Font(FontFamily, fontSize, currentFont.Style, currentFont.Unit);
        if (root is Form form)
        {
            form.RightToLeft = IsPersian ? RightToLeft.Yes : RightToLeft.No;
            form.RightToLeftLayout = IsPersian;
        }

        if (root is TabControl tabs)
            foreach (TabPage page in tabs.TabPages) page.Text = T(page.Text);
        if (root is DataGridView grid)
            foreach (DataGridViewColumn column in grid.Columns) column.HeaderText = T(column.HeaderText);
        if (root is ToolStrip strip)
            foreach (ToolStripItem item in strip.Items) item.Text = T(item.Text ?? string.Empty);

        foreach (Control child in root.Controls) Apply(child);
    }

    public static void ToggleAndRestart(Form owner)
    {
        Current = IsPersian ? UiLanguage.English : UiLanguage.Persian;
        File.WriteAllText(SettingsPath, Current.ToString(), Encoding.UTF8);
        MessageBox.Show(owner,
            T("Language changed. The application will restart to apply it."),
            T("Switch language"), MessageBoxButtons.OK, MessageBoxIcon.Information);
        Application.Restart();
        owner.Close();
    }

    private static UiLanguage Load()
    {
        try
        {
            string value = File.Exists(SettingsPath) ? File.ReadAllText(SettingsPath).Trim() : string.Empty;
            return value.Equals(nameof(UiLanguage.Persian), StringComparison.OrdinalIgnoreCase)
                ? UiLanguage.Persian : UiLanguage.English;
        }
        catch { return UiLanguage.English; }
    }
}
