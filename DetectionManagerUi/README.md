# DetectionManagerUi

پنل مدیریتی React/TypeScript برای `HshDetectionService`. این UI مشابه جریان کاری HshVisionLab طراحی شده، اما تمام عملیات را از طریق API سرویس انجام می‌دهد و به فایل‌های تنظیمات یا دیتابیس دسترسی مستقیم ندارد. مشخصات مرجع برای بازسازی دقیق در [WEB-UI-RECONSTRUCTION-SPEC.md](../docs/WEB-UI-RECONSTRUCTION-SPEC.md) است؛ مسیرهای تصویر و latency در [WEB-UI-AND-STREAMING.md](../docs/WEB-UI-AND-STREAMING.md) آمده‌اند.

## اجرا

```powershell
npm install
npm run dev
```

سرویس تشخیص باید روی `http://127.0.0.1:5080` در حال اجرا باشد. Vite در حالت توسعه routeهای `/api`، `/health` و `/hubs` را به سرویس proxy می‌کند؛ در این حالت UI از داخل سرویس host نمی‌شود.

برای دسترسی از سایر دستگاه‌های شبکهٔ محلی، Vite را روی همهٔ interfaceها bind کنید:

```powershell
npx vite --host 0.0.0.0 --port 5173
```

آدرس فعلی UI در این محیط:

```text
http://192.168.10.172:5173/
```

اگر آدرس شبکهٔ سیستم تغییر کرد، مقدار `192.168.10.172` را با IPv4 جدید سیستم جایگزین کنید. سرویس تشخیص همچنان باید روی پورت `5080` فعال باشد و برای پخش WebRTC/MediaMTX از دستگاه دیگر، پورت UDP مربوط به WebRTC نیز باید در Firewall باز باشد.

برای build و host مستقل UI، آدرس سرویس را هنگام build مشخص کنید:

```powershell
$env:VITE_HSH_API_BASE_URL = 'http://127.0.0.1:5080'
$env:VITE_HSH_API_KEY = 'service-api-key'
npm run build
npm run preview -- --host 0.0.0.0 --port 5173
```

اگر UI و سرویس روی یک ماشین نیستند، origin UI را در `service-settings.json` داخل
`http.corsOrigins` اضافه کنید. مسیرهای API، snapshot، WHEP و SignalR همگی از همین
`VITE_HSH_API_BASE_URL` استفاده می‌کنند.

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
- thumbnailهای داشبورد با نرخ کنترل‌شدهٔ snapshot/overlay به‌روزرسانی می‌شوند و هنگام مخفی‌شدن تب مرورگر polling و WebRTC متوقف می‌شود تا با افزایش تعداد دوربین‌ها بار UI و API محدود بماند
- نوار اکشن بالایی و tileهای دوربین با کنترل‌های Start/Stop، Edit و نمای کامل؛ زیر layout اصلی داشبورد نیز پنل واقعی `وضعیت runtime` برای پنج دوربین اول وجود دارد
- پنل `Detected events` در کنار تصویر با crop تشخیص، نام دوربین، label، confidence و زمان؛ کارت‌های صرفاً متنی جایگزین این پنل نیستند
- صفحهٔ تاریخچهٔ تشخیص امکان حذف همهٔ eventها، بازه‌های آماده و بازهٔ سفارشی را با تأیید کاربر دارد؛ artifactهای تصویری حذف‌شده نیز پاک می‌شوند
- ادیتور تصویری ROI چندضلعی با نقاط نرمال‌شده، نام/فعال‌بودن ROI، اجرای موازی ROIها و انتخاب اجرای سریالی/همزمان taskهای هر ROI
- UI به‌صورت دوزبانهٔ فارسی/انگلیسی ارائه می‌شود؛ کلید `EN`/`فا` در نوار بالایی زبان و جهت چیدمان را تغییر می‌دهد و انتخاب در مرورگر ذخیره می‌شود
- نمای متمرکز دوربین تصویر همان دوربین را در workspace میانی نشان می‌دهد و به‌صورت خودکار وارد edit نمی‌شود؛ ابزارهای ویرایش، ROI جدید، حذف، ذخیره، لغو و بازگشت مستقل‌اند
- تنظیمات کامل General/Capture، FFmpeg، LibVLC یا MediaMTX، TCP/UDP، reconnect، buffer و Motion Gate
- پروفایل‌های Weak، Balanced و High مطابق فرم ویندوزی، بدون تغییر thresholdهای تشخیص
- تنظیمات مستقل Plate و Face برای هر ROI در چهار گروه Plate detection، Face detection، Face identification و Tracking/recording
- مدل‌ها در ComboBox از catalog سرویس (`/api/v1/service/models`) بارگذاری می‌شوند؛ UI برای model file ورودی متنی ندارد
- MediaMTX با WHEP خام و کم‌تاخیر در `<video>` نمایش داده می‌شود و ROI، کادر تشخیص، متن و primitiveهای پردازشی از `/api/v1/streams/{cameraId}/overlay` به‌صورت SVG Overlay سمت کلاینت رسم می‌شوند
- snapshot برای backendهای غیر MediaMTX و endpoint WebRTC کامپوزیت‌شده برای مصرف‌کننده‌های legacy باقی می‌مانند؛ مسیر اصلی MediaMTX از encode مجدد ویدئو استفاده نمی‌کند
- Face Database کامل: افراد نام‌دار/Unknown، rename، حذف sample/person، enrollment چندتصویری، انتقال sample و Similarity/Merge
- مشاهدهٔ eventهای پایدار، فریم کامل، ROI/Plate/Face crop، metadata جزئی و payload کامل trigger
- اتصال SignalR با نگهداری sequence و replay پس از reconnect؛ polling فقط fallback است
- تنظیم `History event cooldown (sec)` مستقل برای هر آیتم Plate/Face زیر ROI؛ در نبود trigger، ثبت تکراری canonical history برای همان دوربین/ROI/component را محدود می‌کند
- فیلد `History event cooldown (sec)` در «آزمایش subscription کلاینت»؛ فقط history و replay/live همان اتصال وب را فیلتر می‌کند و database مشترک یا triggerها را تغییر نمی‌دهد
- triggerهای سناریویی Plate، Face و Plate+Face با camera scope، identity، confidence و `History event cooldown (sec)`؛ cooldown هر تریگر از Event Store بررسی می‌شود و برای کلید همان سناریو اعمال می‌شود
- مدیریت سرویس، listener، API key، retention، runtime reload، capability registry و inventory مدل‌ها

## ساخت production

```powershell
npm run build
npm run preview
```

خروجی production در پوشهٔ `dist` ایجاد می‌شود و باید توسط IIS، Nginx یا هر
static host مستقلی سرو شود. سرویس تشخیص عمداً UI را host نمی‌کند.
