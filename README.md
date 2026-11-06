# HshVisionLab

`HshVisionLab` یک میزکار WinForms برای تحلیل هم‌زمان چند دوربین است. هر دوربین source، ROI چندضلعی، Motion Gate و مسیرهای پردازش مستقل دارد و می‌تواند Plate، Face، Palm یا ترکیب آن‌ها را اجرا کند.

## مرز قطعی برنامهٔ Windows و UI وب

`HshVisionLab.exe` یک برنامهٔ Windows مستقل است. تمام capture، اتصال مستقیم به دوربین، inference، پردازش ROI، overlay، history و دیتابیس موردنیاز آن در خود برنامه و پروژه‌های داخل همین solution پیاده‌سازی می‌شوند. این برنامه نباید به `HshDetectionService`، API، SignalR، WHEP یا Overlay سرویس وصل شود و تصویر یا نتیجهٔ پردازش را از سرویس دریافت کند.

`DetectionManagerUi` یک UI وب مستقل برای `HshDetectionService` است. این UI فقط از API، SignalR، stream و WHEP/Overlay سرویس استفاده می‌کند؛ مستقیماً به دوربین، engine یا دیتابیس محلی دسترسی ندارد و هیچ ارتباطی با `HshVisionLab` برقرار نمی‌کند.

این دو برنامه از نظر امکانات و تجربهٔ کاربری باید هم‌سطح و تا حد امکان مشابه باشند، اما runtime، مسیر capture، pipeline، state و ارتباطات آن‌ها مستقل است. شباهت قابلیت‌ها به معنی اشتراک process یا اتصال بین دو UI نیست.

برای بازسازی مشابه این پروژه، از [راهنمای مرجع بازسازی](docs/REBUILD_GUIDE.md) شروع کنید. آن سند منبع اصلی رفتار، معماری، تنظیمات، مدل‌ها، لایسنس و معیارهای پذیرش است.

رابط کاربری برنامه دو زبانه است و از دکمهٔ زبان در نوار بالایی برای جابه‌جایی فارسی و انگلیسی استفاده می‌کند. انتخاب زبان کنار فایل اجرایی ذخیره می‌شود و پس از راه‌اندازی مجدد باقی می‌ماند.

## اجزای solution

| جزء | مسئولیت |
| --- | --- |
| `HshVisionLab.exe` | UI چنددوربینه، preview، ROI، history محلی، automation و Identity database |
| `HshDetectionEngin.Abstractions.dll` | قراردادها و تنظیمات مشترک |
| `HshDetectionEngin.dll` | capture، Motion، ROI و runtime processing |
| `HshDetectionEngin.Plate.dll` | تشخیص پلاک ایرانی و مدل‌های package شده |
| `HshDetectionEngin.Face.dll` | YuNet، SFace، track و شناسایی چهره |
| `HshDetectionEngin.Palm.dll` | BlazePalm/RTMDet، CCNet/PPNet و شناسایی کف دست |
| `HshDetectionEngin.Identity.dll` | دیتابیس مرکزی اشخاص، پلاک‌ها، نمونه‌های چهره و کف دست |
| `HshDetectionEngin.Licensing.dll` | لایسنس RSA وابسته به دستگاه |
| `HshDetectionEngin.LicenseRequest.exe` | ساخت درخواست فعال‌سازی روی دستگاه مشتری |
| `HshDetectionEngin.LicenseIssuer.exe` | مدیریت مشتری و صدور/آرشیو لایسنس روی سیستم امن صادرکننده |

## رفتار UI اصلی

- برنامه با عنوان `Camera Management` (در فارسی: «مدیریت دوربین‌ها») به‌صورت maximized باز می‌شود و نوار بالایی دکمه‌های آیکونی `Cameras`، `Save`، `Face database`، `Start All`، `Stop All`، `Thumbnails` و تغییر زبان را دارد؛ نام عملکرد هر دکمه در Tooltip نمایش داده می‌شود و `Thumbnails` فقط هنگام نمایش یک دوربین بزرگ فعال/نمایش داده می‌شود. رابط از فارسی و انگلیسی پشتیبانی می‌کند و انتخاب زبان در `ui-language.txt` کنار executable ذخیره می‌شود.
- دکمهٔ `Cameras` فرم جداگانه و غیرمودال مدیریت دوربین‌ها را باز می‌کند. این فرم دکمهٔ `Add camera` و جدول نام، منبع، وضعیت، FPS و کنترل‌های Start/Stop، Edit و Delete را دارد و با تغییرات فرم اصلی همگام می‌شود؛ خود فرم تنظیمات افزودن/ویرایش دوربین به‌صورت modal باز می‌شود.
- نمای چنددوربینه با توجه به تعداد دوربین‌ها شبکهٔ ۱، ۲، ۳ یا ۴ ستونه می‌سازد. بالای هر tile نیز دکمه‌های آیکون‌محور کم‌عرض `Start/Stop`، `Edit` و `Delete` قرار دارد و متن عملکرد آن‌ها در Tooltip نمایش داده می‌شود تا این عملیات بدون بازکردن فهرست دوربین‌ها انجام شود. کلیک روی tile، تصویر یا وضعیت دوربین آن را انتخاب می‌کند و دوبارکلیک روی tile، تصویر یا وضعیت، همان دوربین را بزرگ می‌کند. دوبارکلیک تصویر بزرگ یا دکمهٔ `Thumbnails` به نمای چنددوربینه برمی‌گرداند.
- فضای کاری دو ستون دارد: سمت چپ preview و سمت راست ستونی با عرض ثابت ۳۹۰ پیکسل برای `Detected events` و، در نمای بزرگ، پنل ROI. کارت‌های رخداد نهاییِ Event Store همراه دوربین، برچسب، confidence و زمان نمایش داده می‌شوند و history رابط کاربر حداکثر ۱۰۰ مورد را نگه می‌دارد؛ رکوردهای staging داخلی هرگز کارت جداگانه نمی‌سازند.
- پنل ROI در نمای چنددوربینه مخفی است و در نمای بزرگ بلافاصله به‌صورت کامل زیر `Detected events` دیده می‌شود؛ پنل آیکنِ تنها یا overlay روی تصویر وجود ندارد. کنترل‌های آن شامل افزودن، ویرایش نقاط، تغییر نام، حذف و پاک‌کردن همهٔ ROIهاست. ویرایش نقاط با یک polygon جدید شروع می‌شود و نقاط ثبت‌شده جایگزین نقاط قبلی همان ROI می‌شوند.
- نوار وضعیت پایین صفحه وضعیت دوربین، FPS، زمان inference، resolution و dropped frames را نشان می‌دهد.
- پنجرهٔ `Identity database` از SQLite مرکزی (`identity-database.db`) استفاده می‌کند. جدول `People` مشخصات شخص را نگه می‌دارد و جدول‌های `PersonPlates`، `FaceSamples` و `PalmSamples` اطلاعات modalityها را با `PersonId` مشترک ذخیره می‌کنند. فرم از یک فهرست اشخاص و تب‌های پلاک، نمونه‌های چهره و نمونه‌های کف دست تشکیل شده و افزودن، حذف و rename را مدیریت می‌کند؛ ابزارهای import و similarity چهره نیز در همان فرم در دسترس‌اند.
- در تنظیمات هر دوربین، `CaptureBackend` بین `FFmpeg`، `LibVLC` و `MediaMTX` قابل انتخاب است. `FFmpeg` پیش‌فرض است؛ `LibVLC` برای RTSPهایی است که در VLC پایدارتر هستند و به VLC 3.x x64 نصب‌شده یا `VLC_HOME` نیاز دارد؛ `MediaMTX` برای path مستقل، WHEP خام و پخش کم‌تاخیر مرورگر استفاده می‌شود.
- دکمهٔ `Detection history` یک Event Store محلی برای رخدادهای Plate/Face/Palm باز می‌کند. رخدادها همراه crop، فریم خام، metadata و payload در `desktop-events.json` و `desktop-event-artifacts` ذخیره می‌شوند و جست‌وجو، فیلتر، صفحه‌بندی، preview و حذف همه/بازه‌ای دارند.
- دکمهٔ `Triggers and workflows` یک تب `Application subscription` نیز دارد. subscription به‌صورت چندپروفایلی در `desktop-automation.json` ذخیره می‌شود و برای هر سناریو محور تشخیص، دوربین/ROI، requiredهای Face/Plate/Palm، includeهای Face/Plate/Palm، unknown policy، artifact و cooldown را تعیین می‌کند. دوربینی که در هیچ profileای scope نشده باشد، همهٔ تشخیص‌های معتبر پردازش‌شده‌اش را ثبت می‌کند؛ این تنظیم فیلترِ پس از query نیست، بلکه تعیین می‌کند چه رکورد نهایی در Event Store ساخته و در پنل/تاریخچهٔ Windows دیده شود. اگر profile مثلاً `Mode=Plate` و `FaceRequired=true` و `PlateRequired=true` داشته باشد، اجزای خام تا `WindowMs` staging می‌شوند و فقط association نهایی `PlateFace` نمایش داده می‌شود.
- همان فرم، triggerهای محلی و Invocationهای HTTP/SQLite را مدیریت می‌کند. trigger مسیر مستقل ارسال به کلاینت است؛ هنگام match شدن، subscription داخلی bypass می‌شود و payload کامل موجود شامل Plate، Face و Palm، چه شناخته‌شده و چه ناشناس، با رعایت `includeFace`، `includePlate` و `includePalm` ارسال می‌شود. این بخش هیچ اتصال یا وابستگی به `HshDetectionService` ندارد.
- فرم `Identity database` جست‌وجوی اشخاص، حذف گروهی آبشاری، افزودن چندتصویر، Import folder برای Face/Palm و انتقال نمونهٔ Face/Palm بین اشخاص را فراهم می‌کند.

### Tracking و تکمیل تشخیص با بافر دوربین در Windows

در تنظیمات دوربین، زبانهٔ `General`، بخش `Camera recognitions` با دکمهٔ `Add recognition` تعریف Face یا Plate بسازید و مدل SFace/OCR و آستانهٔ پذیرش آن را انتخاب کنید. در بخش `Sample buffers by type` ظرفیت و حافظهٔ چهره و پلاک جدا تنظیم می‌شوند؛ پیش‌فرض ظرفیت‌ها به‌ترتیب ۳۲ و ۶۴ نمونهٔ منتظر و سقف حافظهٔ هر نوع ۱۲۸ MiB است.

سپس در زبانهٔ `Processing`، پردازش Face/Plate زیر ROI را انتخاب کنید، `Recognition execution` را روی `Tracking and camera buffer` بگذارید و `Recognition destination` هم‌نوع را تعیین کنید. پس از Save و Start، ROI فقط کادر و track را تولید می‌کند؛ یک worker مستقل همان دوربین نمونه‌ها را با مدل مقصد تکمیل می‌کند و فقط نتیجهٔ پذیرفته‌شده وارد تاریخچه و تریگر می‌شود. یک track تکمیل‌شده تا موعد refresh دوباره شناسایی نمی‌شود. پیش‌فرض فایل‌های قدیمی `Direct recognition` است.

دکمهٔ `Sample buffers` روی tile دوربین، تعداد منتظر/در حال پردازش، مصرف حافظه، حذف/انقضا و جزئیات نمونه‌ها را جدا برای Face/Plate نشان می‌دهد. شماره، زمان و تصویر رخداد نهایی مربوط به همان نمونهٔ بافر است. این حالت فعلاً در Windows فعال است و Service/Web هنوز اتصال آن را ندارند. جزئیات و پیش‌فرض‌ها در [پیکربندی](docs/CONFIGURATION.md) آمده‌اند.

## پنل وب و مسیر تصویر

`DetectionManagerUi` رابط وب مستقل سرویس است و همان سطح قابلیت‌های عملیاتی برنامهٔ Windows را در
یک dashboard ارائه می‌کند؛ اما به `HshVisionLab` وصل نمی‌شود: نوار اکشن بالایی، tileهای دوربین با کنترل‌های
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

## جهت حرکت

در تنظیمات دوربین، پردازش یک ROI را انتخاب کنید و «تنظیمات جهت حرکت» را بزنید. جهت برای پلاک، چهره، کف دست و ماژول‌های دارای شناسهٔ پایدار Track محاسبه می‌شود؛ جسم ثابت `Dir: 0` و جهت نامشخص `Dir: ?` دارد. خط ورود/خروج نیز به‌صورت اختیاری برای هر پردازش قابل تنظیم است. جزئیات کد جهت‌ها، آستانهٔ لرزش و خروجی metadata در [تنظیمات](docs/CONFIGURATION.md) آمده است.
