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
        ["Motion direction"] = "جهت حرکت",
        ["Motion settings"] = "تنظیمات جهت حرکت",
        ["Requires a stable track ID. Stationary direction = 0."] = "نیازمند شناسهٔ پایدار ردگیری است. جهت جسم ثابت برابر صفر است.",
        ["Motion window (ms)"] = "بازهٔ بررسی حرکت (میلی‌ثانیه)",
        ["Minimum observation time (ms)"] = "حداقل زمان مشاهده (میلی‌ثانیه)",
        ["Minimum displacement (ROI pixels)"] = "حداقل جابه‌جایی (پیکسل در ROI)",
        ["Motion track timeout (ms)"] = "انقضای ردگیری حرکت (میلی‌ثانیه)",
        ["Entry/exit crossing line"] = "خط عبور ورود و خروج",
        ["Line start X (0–1 in ROI)"] = "X ابتدای خط (۰ تا ۱ در ROI)",
        ["Line start Y (0–1 in ROI)"] = "Y ابتدای خط (۰ تا ۱ در ROI)",
        ["Line end X (0–1 in ROI)"] = "X انتهای خط (۰ تا ۱ در ROI)",
        ["Line end Y (0–1 in ROI)"] = "Y انتهای خط (۰ تا ۱ در ROI)",
        ["Positive-side crossing is entry (default: down)"] = "عبور به سمت مثبت، ورود باشد (خط پیش‌فرض: پایین)",
        ["Image axes: X right, Y down. Default line: downward crossing is entry."] = "محورها: X به راست و Y به پایین. در خط پیش‌فرض، عبور رو به پایین ورود است.",
        ["0 = stationary; 1…8 = moving"] = "۰ = ثابت؛ ۱ تا ۸ = جهت حرکت",
        ["Invalid motion observation window, displacement or track timeout."] = "تنظیمات زمان یا جابه‌جایی نامعتبر است؛ حداقل زمان مشاهده نباید از بازه بیشتر باشد و انقضای ردگیری باید از بازه بیشتر باشد.",
        ["The crossing line must have two distinct endpoints inside the ROI."] = "خط عبور باید دو نقطهٔ متفاوت با مختصات بین صفر و یک در ROI داشته باشد.",
        ["Camera recognitions"] = "تشخیص‌های دوربین",
        ["Camera recognition"] = "تشخیص دوربین",
        ["Recognition destinations"] = "تشخیص‌های مقصد",
        ["Recognition routing"] = "مسیر تکمیل تشخیص",
        ["Recognition execution"] = "روش تکمیل تشخیص",
        ["Recognition destination"] = "تشخیص مقصد",
        ["Direct recognition"] = "تشخیص مستقیم",
        ["Tracking and camera buffer"] = "ردیابی و بافر دوربین",
        ["Add recognition"] = "افزودن تشخیص",
        ["Recognition"] = "تشخیص",
        ["Sample buffers by type"] = "بافر مستقل نوع نمونه‌ها",
        ["Sample type"] = "نوع نمونه",
        ["Acceptance threshold"] = "آستانهٔ پذیرش",
        ["Face buffer"] = "بافر چهره",
        ["Plate buffer"] = "بافر پلاک",
        ["Pending sample capacity"] = "ظرفیت نمونه‌های منتظر",
        ["Maximum memory (MB)"] = "حداکثر حافظه (مگابایت)",
        ["Maximum sample age (ms)"] = "حداکثر عمر نمونه (میلی‌ثانیه)",
        ["Minimum track observations"] = "حداقل مشاهدات هر ردگیری",
        ["Minimum enqueue interval (ms)"] = "حداقل فاصلهٔ ورود نمونه (میلی‌ثانیه)",
        ["Retry delay (ms)"] = "فاصلهٔ تلاش مجدد (میلی‌ثانیه)",
        ["Maximum attempts per track"] = "حداکثر تلاش برای هر ردگیری",
        ["Recognition refresh (ms, 0 = once)"] = "بررسی مجدد (میلی‌ثانیه؛ صفر: یک‌بار)",
        ["Add recognition destinations in the General tab."] = "تشخیص‌های مقصد را در زبانهٔ عمومی اضافه کنید.",
        ["Change or disable these ROI tasks before removing this recognition:"] = "پیش از حذف یا غیرفعال‌کردن این تشخیص، مقصد پردازش‌های زیر را تغییر دهید یا آن‌ها را غیرفعال کنید:",
        ["Enter a name and select a recognition model."] = "نام را وارد و مدل تشخیص را انتخاب کنید.",
        ["Retry delay must be shorter than maximum sample age."] = "فاصلهٔ تلاش مجدد باید از حداکثر عمر نمونه کمتر باشد.",
        ["Recognition model is not available."] = "مدل تشخیص در دسترس نیست.",
        ["Sample buffers"] = "وضعیت بافر نمونه‌ها",
        ["Track"] = "ردگیری",
        ["Sample age (ms)"] = "عمر نمونه (میلی‌ثانیه)",
        ["Attempts"] = "تلاش‌ها",
        ["Completed"] = "تکمیل‌شده",
        ["Replaced"] = "جایگزین‌شده",
        ["Dropped"] = "حذف‌شده",
        ["Expired"] = "منقضی‌شده",
        ["Retried"] = "تلاش مجدد",
        ["Pending"] = "در انتظار",
        ["Camera is stopped or no buffered recognitions are active."] = "دوربین متوقف است یا تشخیص مبتنی بر بافر فعال نیست.",
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
        ["ROI processing targets  •  ROIs run in parallel; choose task mode per ROI"] = "اهداف پردازش ROI • ROIها موازی هستند؛ حالت task را برای هر ROI انتخاب کنید",
        ["ROI properties"] = "ویژگی‌های ROI",
        ["ROI name"] = "نام ROI",
        ["Task execution mode"] = "حالت اجرای task",
        ["Sequential"] = "سریالی",
        ["Parallel"] = "همزمان",
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
        ["Detection history"] = "تاریخچه تشخیص",
        ["Triggers and workflows"] = "تریگرها و جریان‌های کاری",
        ["Search"] = "جست‌وجو",
        ["Scenario"] = "سناریو",
        ["All"] = "همه",
        ["Refresh"] = "تازه‌سازی",
        ["Delete all"] = "حذف همه",
        ["Delete range"] = "حذف بازه",
        ["Today"] = "امروز",
        ["Last 7 days"] = "۷ روز اخیر",
        ["Last 30 days"] = "۳۰ روز اخیر",
        ["Before selected date"] = "قبل از تاریخ انتخاب‌شده",
        ["Previous"] = "قبلی",
        ["Next"] = "بعدی",
        ["No event selected"] = "رخدادی انتخاب نشده است",
        ["Triggers"] = "تریگرها",
        ["Application subscription"] = "subscription برنامه",
        ["Application subscription profiles"] = "سناریوهای subscription برنامه",
        ["Detection axis"] = "محور تشخیص",
        ["Camera IDs"] = "شناسه دوربین‌ها",
        ["ROI IDs"] = "شناسه ROIها",
        ["Required"] = "الزامی",
        ["Components"] = "اجزای ثبت",
        ["Record Face"] = "ثبت چهره",
        ["Record Plate"] = "ثبت پلاک",
        ["Record Palm"] = "ثبت کف دست",
        ["Include unknown Face"] = "ثبت چهره ناشناس",
        ["Include unknown Palm"] = "ثبت کف دست ناشناس",
        ["Artifacts"] = "تصاویر و artifacts",
        ["Save artifacts"] = "ذخیره تصاویر",
        ["Association window ms"] = "پنجره association (میلی‌ثانیه)",
        ["History cooldown sec"] = "فاصله ثبت داخلی (ثانیه)",
        ["History scenario"] = "سناریوی تاریخچه",
        ["KnownFace"] = "چهره شناخته‌شده",
        ["KnownPalm"] = "کف دست شناخته‌شده",
        ["These profiles control only what this Windows application records in its local history. The dashboard/history are not query-filtered. A trigger is independent and sends the complete available detection context, including Palm."] = "این سناریوها فقط ثبت داخلی برنامه Windows را تعیین می‌کنند. نمای اصلی و تاریخچه با query فیلتر نمی‌شوند. تریگر مستقل است و context کامل تشخیص، از جمله کف دست، را ارسال می‌کند.",
        ["History scenario 1"] = "سناریوی تاریخچه ۱",
        ["Invocations"] = "فراخوانی‌ها",
        ["Invocation logs"] = "لاگ فراخوانی‌ها",
        ["New"] = "جدید",
        ["Webhook"] = "Webhook",
        ["WindowsNotification"] = "اعلان ویندوز",
        ["Web"] = "وب",
        ["SQLite"] = "SQLite",
        ["HTTP method"] = "متد HTTP",
        ["URL"] = "نشانی URL",
        ["API key"] = "کلید API",
        ["SQLite path"] = "مسیر SQLite",
        ["Command text"] = "متن دستور",
        ["Timeout sec"] = "مهلت (ثانیه)",
        ["Retries"] = "تعداد تلاش مجدد",
        ["Retry delay sec"] = "فاصله تلاش مجدد (ثانیه)",
        ["Event retention (days)"] = "نگهداری رخداد (روز)",
        ["Artifact retention (days)"] = "نگهداری تصاویر (روز)",
        ["Save retention"] = "ذخیره نگهداری",
        ["Delete selected"] = "حذف انتخاب‌شده‌ها",
        ["Assign face to person"] = "انتساب چهره به شخص",
        ["Palm import"] = "واردکردن کف دست",
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
