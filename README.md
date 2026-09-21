# HshVisionLab

`HshVisionLab` یک میزکار WinForms برای تحلیل هم‌زمان چند دوربین است. هر دوربین source، ROI چندضلعی، Motion Gate و مسیرهای پردازش مستقل دارد و می‌تواند Plate، Face یا هر دو را اجرا کند.

برای بازسازی مشابه این پروژه، از [راهنمای مرجع بازسازی](docs/REBUILD_GUIDE.md) شروع کنید. آن سند منبع اصلی رفتار، معماری، تنظیمات، مدل‌ها، لایسنس و معیارهای پذیرش است.

رابط کاربری برنامه دو زبانه است و از دکمهٔ زبان در نوار بالایی برای جابه‌جایی فارسی و انگلیسی استفاده می‌کند. انتخاب زبان کنار فایل اجرایی ذخیره می‌شود و پس از راه‌اندازی مجدد باقی می‌ماند.

## اجزای solution

| جزء | مسئولیت |
| --- | --- |
| `HshVisionLab.exe` | UI چنددوربینه، preview، ROI، history و Face database |
| `HshDetectionEngin.Abstractions.dll` | قراردادها و تنظیمات مشترک |
| `HshDetectionEngin.dll` | capture، Motion، ROI و runtime processing |
| `HshDetectionEngin.Plate.dll` | تشخیص پلاک ایرانی و مدل‌های package شده |
| `HshDetectionEngin.Face.dll` | YuNet، SFace، track و شناسایی چهره |
| `HshDetectionEngin.Licensing.dll` | لایسنس RSA وابسته به دستگاه |
| `HshDetectionEngin.LicenseRequest.exe` | ساخت درخواست فعال‌سازی روی دستگاه مشتری |
| `HshDetectionEngin.LicenseIssuer.exe` | مدیریت مشتری و صدور/آرشیو لایسنس روی سیستم امن صادرکننده |

## رفتار UI اصلی

- برنامه با عنوان `Camera Management` (در فارسی: «مدیریت دوربین‌ها») به‌صورت maximized باز می‌شود و نوار بالایی دکمه‌های آیکونی `Cameras`، `Save`، `Face database`، `Start All`، `Stop All`، `Thumbnails` و تغییر زبان را دارد؛ نام عملکرد هر دکمه در Tooltip نمایش داده می‌شود و `Thumbnails` فقط هنگام نمایش یک دوربین بزرگ فعال/نمایش داده می‌شود. رابط از فارسی و انگلیسی پشتیبانی می‌کند و انتخاب زبان در `ui-language.txt` کنار executable ذخیره می‌شود.
- دکمهٔ `Cameras` فرم جداگانه و غیرمودال مدیریت دوربین‌ها را باز می‌کند. این فرم دکمهٔ `Add camera` و جدول نام، منبع، وضعیت، FPS و کنترل‌های Start/Stop، Edit و Delete را دارد و با تغییرات فرم اصلی همگام می‌شود؛ خود فرم تنظیمات افزودن/ویرایش دوربین به‌صورت modal باز می‌شود.
- نمای چنددوربینه با توجه به تعداد دوربین‌ها شبکهٔ ۱، ۲، ۳ یا ۴ ستونه می‌سازد. بالای هر tile نیز دکمه‌های آیکون‌محور کم‌عرض `Start/Stop`، `Edit` و `Delete` قرار دارد و متن عملکرد آن‌ها در Tooltip نمایش داده می‌شود تا این عملیات بدون بازکردن فهرست دوربین‌ها انجام شود. کلیک روی tile، تصویر یا وضعیت دوربین آن را انتخاب می‌کند و دوبارکلیک روی tile، تصویر یا وضعیت، همان دوربین را بزرگ می‌کند. دوبارکلیک تصویر بزرگ یا دکمهٔ `Thumbnails` به نمای چنددوربینه برمی‌گرداند.
- فضای کاری دو ستون دارد: سمت چپ preview و سمت راست ستونی با عرض ثابت ۳۹۰ پیکسل برای `Detected events` و، در نمای بزرگ، پنل ROI. کارت‌های رخداد همراه دوربین، برچسب، confidence و زمان نمایش داده می‌شوند و history رابط کاربر حداکثر ۱۰۰ مورد را نگه می‌دارد.
- پنل ROI در نمای چنددوربینه مخفی است و در نمای بزرگ بلافاصله به‌صورت کامل زیر `Detected events` دیده می‌شود؛ پنل آیکنِ تنها یا overlay روی تصویر وجود ندارد. کنترل‌های آن شامل افزودن، ویرایش نقاط، تغییر نام، حذف و پاک‌کردن همهٔ ROIهاست. ویرایش نقاط با یک polygon جدید شروع می‌شود و نقاط ثبت‌شده جایگزین نقاط قبلی همان ROI می‌شوند.
- نوار وضعیت پایین صفحه وضعیت دوربین، FPS، زمان inference، resolution و dropped frames را نشان می‌دهد.
- پنجرهٔ مدیریت Face database از SQLite (`face-database.db`) استفاده می‌کند؛ گرید ستون‌های crop، شماره و نام شخص، نوع، شماره نمونه، confidence تشخیص، تاریخ، فایل منبع و وضعیت تصویر دارد. نام شخص با دوبارکلیک یا `F2` مستقیماً در گرید قابل تغییر است و پس از اعتبارسنجی در database ذخیره می‌شود. گزینهٔ گروه‌بندی برای هر شخص یک ردیف سرگروه با علامت `-`/`+` می‌سازد و با کلیک، نمونه‌های آن شخص را جمع یا باز می‌کند. برای نمونهٔ موجود در گرید (مثلاً ناشناس) می‌توان `Move selected sample` را زد و شخص مقصد را انتخاب کرد؛ تصویر، embedding و confidence همان نمونه حفظ می‌شود. برای فایل تصویر جدید نیز `Add to selected person` وجود دارد. ورود پوشه‌ای، حذف نمونه/شخص، تغییر نام و بررسی تشابه نیز در همین پنجره هستند؛ خروجی بررسی تشابه شامل تصاویر جفت‌ها و `similarity-report.csv` است.
- در تنظیمات هر دوربین، `CaptureBackend` بین `FFmpeg`، `LibVLC` و `MediaMTX` قابل انتخاب است. `FFmpeg` پیش‌فرض است؛ `LibVLC` برای RTSPهایی است که در VLC پایدارتر هستند و به VLC 3.x x64 نصب‌شده یا `VLC_HOME` نیاز دارد؛ `MediaMTX` برای path مستقل، WHEP خام و پخش کم‌تاخیر مرورگر استفاده می‌شود.

## پنل وب و مسیر تصویر

`DetectionManagerUi` رابط وب سرویس است و رفتارهای اصلی برنامهٔ Windows را در
یک dashboard ارائه می‌کند: نوار اکشن بالایی، tileهای دوربین با کنترل‌های
`Start/Stop`، `Edit` و نمای کامل، و پنل `Detected events` در کنار تصویر که crop
و جزئیات متنی هر تشخیص را نشان می‌دهد. زیر این layout نیز پنل `وضعیت runtime`
برای پنج دوربین اول وجود دارد. دکمهٔ Delete در `CameraTile` وب وجود ندارد.

در نمای متمرکز، تصویر یک دوربین workspace میانی را می‌گیرد و ابزارهای ROI در
حالت view قابل دسترسی‌اند؛ انتخاب `ویرایش`، `ROI جدید` یا `حذف` حالت مربوط را
فعال می‌کند و edit/new دکمه‌های `ذخیره ROI` و `لغو` دارند. ورود به این نما
خودکار edit نیست.

برای دوربین‌های `MediaMTX`، ویدئو با WHEP خام MediaMTX مستقیماً به مرورگر
می‌رود و ROI، کادرهای تشخیص، متن و primitiveهای پردازشی از endpoint سبک
`/api/v1/streams/{cameraId}/overlay` روی آن رسم می‌شوند. بنابراین ویدئوی زنده
برای نمایش Drawing دوباره از FFmpeg، Bitmap و encoder اختصاصی عبور نمی‌کند.
در حالت `LibVLC`، capture مستقیماً از `VlcFrameSource`/LibVLC انجام می‌شود و
خروجی‌های UI غیر MediaMTX از latest snapshot سرویس استفاده می‌کنند.

جزئیات کامل routeها و ظاهر UI در [مشخصات مرجع بازسازی UI وب](docs/WEB-UI-RECONSTRUCTION-SPEC.md)
و مسیرهای تصویر در [راهنمای وب و streaming](docs/WEB-UI-AND-STREAMING.md)
و [راهنمای MediaMTX/WebRTC](docs/MediaMTX-WebRTC.md) آمده است.

## Build سریع

```powershell
dotnet restore HshVisionLab.sln
dotnet build HshVisionLab.sln -m:1
dotnet run --project HshVisionLab.csproj --no-build
```

پیش از build Debug، برنامه‌های درحال اجرا را ببندید تا exe و DLLهای خروجی قفل نباشند.

## نقشهٔ مستندات

- [راهنمای بازسازی](docs/REBUILD_GUIDE.md): سند واحد برای بازسازی رفتار مشابه.
- [پیکربندی](docs/CONFIGURATION.md): `settings.json` و پیش‌فرض‌ها.
- [معماری](docs/ARCHITECTURE.md): مسئولیت‌ها و قرارداد pipeline.
- [مدل و عملکرد](docs/OPTIMIZATION.md): مدل‌ها، latency و محدودیت امنیتی packageها.
- آیکون‌های دکمه‌های UI از فونت رسمی Material Icons استفاده می‌کنند؛ resource آن در `Assets/MaterialIcons/MaterialIcons-Regular.ttf` به‌صورت embedded قرار دارد.
- قانون فونت UI: فارسی با `Tahoma` اندازهٔ پیش‌فرض ۸ و انگلیسی با `Segoe UI` و اندازهٔ فعلی نمایش داده می‌شود.
- [عملیات لایسنس](docs/LICENSING.md): درخواست، صدور، archive و rotation کلید.
- [وضعیت](docs/STATUS.md): قابلیت‌های کامل‌شده و backlog آگاهانه.
- [وب و streaming](docs/WEB-UI-AND-STREAMING.md): چیدمان UI، ROI، MediaMTX/WHEP، LibVLC و Overlay.
- [مشخصات بازسازی UI وب](docs/WEB-UI-RECONSTRUCTION-SPEC.md): قرارداد canonical برای routeها، componentها، متن‌ها، اندازه‌ها و رفتار UI.
- [SDK Engine](HshDetectionEngin/README.md)، [Face module](HshDetectionEngin.Face/README.md)، [Plate module](HshDetectionEngin.Plate/README.md) و [امنیت](HshDetectionEngin/SECURITY.md).

مدل خام، ابزار Python و private key صادرکننده بخشی از محصول مشتری نیستند.
