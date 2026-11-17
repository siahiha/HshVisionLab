# DetectionManagerUi

پنل مدیریتی React/TypeScript مستقل برای `HshDetectionService`. این UI از نظر امکانات و تجربهٔ کاربری هم‌سطح `HshVisionLab` طراحی شده، اما هیچ ارتباطی با خود برنامهٔ `HshVisionLab` ندارد؛ تمام عملیات فقط از طریق API، SignalR، stream و WHEP/Overlay سرویس انجام می‌شود و UI به فایل‌های تنظیمات، engine یا دیتابیس محلی دسترسی مستقیم ندارد. مشخصات مرجع برای بازسازی دقیق در [WEB-UI-RECONSTRUCTION-SPEC.md](../docs/WEB-UI-RECONSTRUCTION-SPEC.md) است؛ مسیرهای تصویر و latency در [WEB-UI-AND-STREAMING.md](../docs/WEB-UI-AND-STREAMING.md) آمده‌اند.

## اجرا

```powershell
npm install
npm run dev
```

سرویس تشخیص باید روی `http://127.0.0.1:5080` در حال اجرا باشد. Vite در حالت توسعه routeهای `/api`، `/health` و `/hubs` را به سرویس proxy می‌کند؛ در این حالت UI از داخل سرویس host نمی‌شود.

برای دسترسی از سایر دستگاه‌های شبکهٔ محلی، Vite را روی همهٔ interfaceها bind کنید:

```powershell
npx vite --host 0.0.0.0 --port 5081
```

آدرس فعلی UI در این محیط:

```text
http://192.168.10.172:5081/
```

اگر آدرس شبکهٔ سیستم تغییر کرد، مقدار `192.168.10.172` را با IPv4 جدید سیستم جایگزین کنید. سرویس تشخیص همچنان باید روی پورت `5080` فعال باشد و برای پخش WebRTC/MediaMTX از دستگاه دیگر، پورت UDP مربوط به WebRTC نیز باید در Firewall باز باشد.

برای build و host مستقل UI، آدرس سرویس را هنگام build مشخص کنید:

```powershell
$env:VITE_HSH_API_BASE_URL = 'http://127.0.0.1:5080'
$env:VITE_HSH_API_KEY = 'service-api-key'
npm run build
npm run preview -- --host 0.0.0.0 --port 5081
```

اگر UI و سرویس روی یک ماشین نیستند، origin دقیق UI را در `service-settings.json` داخل
`http.corsOrigins` اضافه کنید؛ برای اجرای محلی معمولاً
`http://127.0.0.1:5081` و `http://localhost:5081` لازم هستند. این تنظیم برای API و
SignalR (`/hubs/detections`) مشترک است و بعد از تغییر، سرویس باید restart شود تا
policy جدید در middleware اعمال شود. مسیرهای API، snapshot، WHEP و SignalR همگی از
همین `VITE_HSH_API_BASE_URL` استفاده می‌کنند.

برای سرویس remote می‌توان کلید API را تنظیم کرد:

```powershell
$env:VITE_HSH_DEV_API_URL = 'http://192.168.10.50:5080'
$env:VITE_HSH_API_KEY = 'service-api-key'
npm run dev
```

`VITE_HSH_DEV_API_URL` فقط مقصد proxy توسعه است. برای build production از
`VITE_HSH_API_BASE_URL` استفاده کنید؛ این مقدار باید با یکی از originهای مجاز
سرویس در `http.corsOrigins` هماهنگ باشد.

## قابلیت‌ها

- dashboard چنددوربینه با نمای tile، وضعیت FPS/inference، start/stop و آخرین رخدادها
- دیوار زندهٔ داشبورد صفحه‌بندی‌شده است و در هر صفحه حداکثر ۶ thumbnail mount می‌کند؛ tileهای صفحات دیگر stream یا polling فعال ندارند
- نمای زندهٔ tileها، نمای متمرکز و نمای کامل از WebRTC کامپوزیت‌شدهٔ سرویس استفاده می‌کند؛ تب پیش‌نمایش تنظیمات برای منابع غیر RTSP از snapshot با نرخ کنترل‌شده استفاده می‌کند
- ابعاد stream به‌صورت خودکار از فضای واقعی تصویر در صفحه گرفته می‌شود؛ tile کوچک و نمای بزرگ تنظیم دستی جدا ندارند. تغییر اندازه پس از ۲۰۰ میلی‌ثانیه به همان session فرستاده می‌شود و نسبت ابعاد، هم‌ترازی encoder و سقف اندازه را سرویس اعمال می‌کند
- stream دوربین غیرفعال، خارج از viewport یا داخل تب مخفی بسته می‌شود؛ برگشت به تصویر، اتصال را دوباره برقرار می‌کند. پاسخ دیررس handshake و session قبلی هنگام خروج پاک می‌شوند؛ retry فقط یک timer با فاصله‌های محدود ۲، ۵، ۱۰ و ۳۰ ثانیه دارد
- نوار اکشن بالایی و tileهای دوربین با کنترل‌های Start/Stop، Edit و نمای کامل؛ زیر layout اصلی داشبورد نیز پنل واقعی `وضعیت runtime` برای پنج دوربین اول وجود دارد
- پنل `Detected events` در کنار تصویر با crop تشخیص، نام دوربین، label، confidence و زمان؛ کارت‌های صرفاً متنی جایگزین این پنل نیستند
- صفحهٔ تاریخچهٔ تشخیص امکان حذف همهٔ eventها، بازه‌های آماده و بازهٔ سفارشی را با تأیید کاربر دارد؛ artifactهای تصویری حذف‌شده نیز پاک می‌شوند
- ادیتور تصویری ROI چندضلعی با نقاط نرمال‌شده، نام/فعال‌بودن ROI، اجرای موازی ROIها و انتخاب اجرای سریالی/همزمان taskهای هر ROI
- UI به‌صورت دوزبانهٔ فارسی/انگلیسی ارائه می‌شود؛ کلید `EN`/`فا` در نوار بالایی زبان و جهت چیدمان را تغییر می‌دهد و انتخاب در مرورگر ذخیره می‌شود
- نمای متمرکز دوربین تصویر همان دوربین را در workspace میانی نشان می‌دهد و به‌صورت خودکار وارد edit نمی‌شود؛ ابزارهای ویرایش، ROI جدید، حذف، ذخیره، لغو و بازگشت مستقل‌اند
- دریافت مستقیم FFmpeg با decoder تک‌thread و نگهداری جدیدترین فریم؛ تنظیمات General/Capture شامل TCP/UDP، reconnect و Motion Gate هستند
- پروفایل‌های Weak، Balanced و High مطابق فرم ویندوزی، بدون تغییر thresholdهای تشخیص
- تنظیمات مستقل Plate، Face و Palm برای هر ROI؛ پردازش‌های هر ROI در تب‌های detection، recognition/identification و tracking/recording در دسترس‌اند و کارت‌های detailed با کلیک باز و بسته می‌شوند
- مدل‌ها در ComboBox از catalog سرویس (`/api/v1/service/models`) بارگذاری می‌شوند؛ هر ComboBox فقط مدل‌های همان قابلیت را نشان می‌دهد (Plate، Face یا Palm) و UI برای model file ورودی متنی ندارد
- وضعیت `canWhep: true` قابلیت منبع RTSP فعال را برای API مستقل WHEP مشخص می‌کند؛ این API و endpoint سبک `/api/v1/streams/{cameraId}/overlay` برای مصرف‌کننده‌های ویدئوی خام و SVG Overlay در دسترس‌اند
- MediaMTX فقط gateway سرویس برای API مرورگر است؛ موتور تصویر را مستقیماً از FFmpeg می‌گیرد. مسیر اصلی فعلی UI، endpoint WebRTC کامپوزیت‌شدهٔ سرویس است
- صفحهٔ `مدیریت افراد` (`/faces`) به‌جای Face Database: افراد نام‌دار، Unknown و `Unknown Palm #…` را نشان می‌دهد و برای هر فرد سه تب مستقل `چهره`، `پالم` و `پلاک` دارد؛ تصاویر crop‌شدهٔ Face/Palm، افزودن چندتصویری، Import folder، انتقال/حذف sample، rename و Similarity/Merge از همین صفحه انجام می‌شوند
- حذف گروهی افراد با checkbox کنار نام (سمت راست ردیف) و تأیید کاربر انجام می‌شود؛ endpoint حذف، متعلقات همان افراد شامل FaceSamples، PalmSamples و PersonPlates را نیز با حذف آبشاری پاک می‌کند
- مشاهدهٔ eventهای پایدار، فریم کامل، ROI/Plate/Face crop، metadata جزئی و payload کامل trigger
- صفحهٔ تاریخچهٔ رخدادها آخرین ۵۰۰ رکورد را بارگذاری می‌کند و گرید آن با اندازهٔ ۵۰ (پیش‌فرض)، ۱۰۰ یا ۲۰۰ رکورد در صفحه، داخل خود گرید صفحه‌بندی می‌شود
- preview رخداد انتخاب‌شده در بالای صفحه sticky است؛ فقط گرید اسکرول عمودی دارد و header آن ثابت می‌ماند، و ارتفاع ردیف‌ها برای نمایش رکوردهای بیشتر فشرده شده است
- اتصال SignalR با نگهداری sequence و replay پس از reconnect؛ polling فقط fallback است
- تنظیم `History event cooldown (sec)` مستقل برای هر آیتم Plate/Face زیر ROI؛ در نبود trigger، ثبت تکراری canonical history برای همان دوربین/ROI/component را محدود می‌کند
- تنظیم subscription چندپروفایلی برنامه با محور تشخیص، دوربین/ROI، requiredهای Face/Plate/Palm، `includeFace`، `includePlate`، `includePalm`، unknown policy، artifact و cooldown؛ این تنظیم فقط روی event canonical ثبت‌شده اثر دارد و پنل تشخیص/تاریخچهٔ عمومی را فیلتر نمی‌کند. trigger مسیر مستقل ارسال به کلاینت است و payload کامل موجود، از جمله Palm، را تحویل می‌دهد.
- triggerهای سناریویی Plate، Face و Plate+Face با camera scope، identity، confidence و `History event cooldown (sec)`؛ cooldown هر تریگر از Event Store بررسی می‌شود و برای کلید همان سناریو اعمال می‌شود
- مدیریت سرویس، listener، API key، retention، runtime reload، capability registry و inventory مدل‌ها

### اندازهٔ خودکار و طول عمر stream

`CompositeWebRtcStream` اندازهٔ viewport را با `ResizeObserver` و پیکسل CSS
می‌گیرد و بدون بزرگ‌کردن آن با ضریب DPI در offer می‌فرستد:

```text
POST /api/v1/streams/{cameraId}/webrtc/offer
{ "type": "offer", "sdp": "...", "maxWidth": 480, "maxHeight": 184 }

PATCH /api/v1/streams/webrtc/{sessionId}
{ "maxWidth": 960, "maxHeight": 540 }

DELETE /api/v1/streams/webrtc/{sessionId}
```

PATCH اندازهٔ درخواستی را به‌روزرسانی و lease را تمدید می‌کند؛ پاسخ موفق `204`
است و `404` باعث برقراری مجدد اتصال می‌شود. viewer فعال هر ۱۰ ثانیه lease را
تمدید می‌کند؛ sessionهای دارای این قرارداد در سرویس lease برابر ۴۵ ثانیه دارند.
پاسخ offer با `supportsViewportUpdates: true` این قرارداد را اعلام می‌کند؛
برای سازگاری هنگام ارتقای سرویس، پاسخ قدیمی فاقد این فیلد باعث ارسال PATCH
نمی‌شود و همان اتصال موجود را حفظ می‌کند.
این اندازه فقط خروجی نمایش را کنترل می‌کند و اندازهٔ فریم اصلی، ROI و inference
را تغییر نمی‌دهد. peer هنگام resize دوباره ساخته نمی‌شود.

برای بررسی روانی تصویر، هر video فعال آخرین نمونهٔ آمار مرورگر را هر دو ثانیه
در `data-preview-metrics` نگه می‌دارد: نرخ decode از اختلاف شمارنده‌ها، میانگین
jitter buffer در همان فاصله، شمار فریم‌های حذف‌شده، freeze و ابعاد ویدئو.
`data-preview-state` وضعیت اتصال و `data-preview-demand-width/height` اندازهٔ
درخواستی را نشان می‌دهند. این داده فقط آخرین نمونه است؛ تاریخچهٔ نامحدود، log
حاوی URL دوربین و تغییر state React برای هر فریم تولید نمی‌شود. این سنجه‌ها
اندازه‌گیری تأخیر کامل دوربین تا نمایش نیستند.

### API هویت در UI وب

صفحهٔ `مدیریت افراد` از دیتابیس مرکزی سرویس استفاده می‌کند و به SQLite به‌صورت
مستقیم دسترسی ندارد. مسیرهای اصلی آن عبارت‌اند از:

```text
GET  /api/v1/face/people
POST /api/v1/face/people/bulk-delete       { "personIds": ["..."] }
GET  /api/v1/face/people/{id}/samples
GET  /api/v1/palm/people/summary
GET  /api/v1/palm/people/{id}/samples
GET  /api/v1/palm/samples/{id}/image
GET  /api/v1/identity/people/{id}/plates
```

حذف گروهی برای بدنهٔ خالی `400` و برای درخواست معتبر `200` با
`{ "deletedCount": number }` برمی‌گرداند. سرویس با حذف آبشاری، نمونه‌های چهره،
کف‌دست و پلاک‌های همان افراد را نیز حذف می‌کند.

## ساخت production

```powershell
npm run build
npm run preview
```

خروجی production در پوشهٔ `dist` ایجاد می‌شود و باید توسط IIS، Nginx یا هر
static host مستقلی سرو شود. سرویس تشخیص عمداً UI را host نمی‌کند.
