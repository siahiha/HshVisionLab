# طراحی سرویس تشخیص HSH

این پوشه معماری، قراردادها و راهنمای اجرایی `HshDetectionService` را نگه می‌دارد. پروژهٔ سرویس اکنون در Solution ایجاد شده و هستهٔ اجرایی آن شامل API، ذخیره‌سازی event، SignalR replay، WebRTC و اتصال به موتور تشخیص است.

## هدف

سرویس باید موتور تشخیص فعلی را بدون وابستگی به رابط کاربری اجرا کند، چند دوربین و چند وظیفهٔ تشخیص را مدیریت کند، تصویر زندهٔ دارای Drawing را به کلاینت‌ها بدهد و رخدادها را حتی در زمان قطع بودن UI از دست ندهد.

## تصمیم‌های اصلی

1. سرویس مالک اصلی تنظیمات فعال، Face Database و Event Store است.
2. `HshVisionLab` در حالت Service Manager از API سرویس استفاده می‌کند و فایل‌های سرویس را مستقیم باز نمی‌کند.
3. هر `CameraSettings.Rois[].Processing[]` در نسخهٔ اول یک Detection Task است؛ مدل جدید و تکراری برای Task ایجاد نمی‌شود.
4. نرخ Rendering مستقل از نرخ inference است.
5. مسیر MediaMTX در وب WHEP خام را ارسال می‌کند و Overlay تشخیص جداگانه روی کلاینت رسم می‌شود؛ مسیر composited WebRTC برای clientهای legacy باقی می‌ماند و inference هیچ‌وقت منتظر encoder یا کلاینت نمی‌ماند.
6. رخداد قبل از broadcast در Event Store ثبت می‌شود و کلاینت با cursor ترتیبی replay می‌کند.
7. SignalR/WebSocket برای کنترل، رخداد و signaling و WebRTC برای ویدئوی زنده استفاده می‌شود.
8. رخدادهای عملیاتی در `events.db` جدا از `face-database.db` ذخیره می‌شوند.

## اسناد

| سند | موضوع |
| --- | --- |
| [01-ARCHITECTURE.md](01-ARCHITECTURE.md) | مرز پروژه، اجزا و جریان کلی داده |
| [02-RENDERING-WEBRTC.md](02-RENDERING-WEBRTC.md) | فریم، overlay، compositing و WebRTC |
| [../WEB-UI-RECONSTRUCTION-SPEC.md](../WEB-UI-RECONSTRUCTION-SPEC.md) | قرارداد canonical بازسازی UI وب: routeها، componentها، layout و رفتار |
| [../WEB-UI-AND-STREAMING.md](../WEB-UI-AND-STREAMING.md) | مسیر WHEP خام MediaMTX، SVG Overlay کلاینت و LibVLC |
| [03-EVENTS-TRIGGERS.md](03-EVENTS-TRIGGERS.md) | رخداد پایدار، replay، trigger و webhook |
| [04-API.md](04-API.md) | قرارداد کامل API برای تنظیمات، دیتابیس و کلاینت‌ها |
| [05-CONFIGURATION-DATA.md](05-CONFIGURATION-DATA.md) | محل فایل‌ها، مالکیت داده، migration و concurrency |
| [06-IMPLEMENTATION-ROADMAP.md](06-IMPLEMENTATION-ROADMAP.md) | مراحل پیشنهادی پیاده‌سازی و معیار پذیرش |
| [07-DETECTION-EVENT-CONTRACT.md](07-DETECTION-EVENT-CONTRACT.md) | قرارداد کامل رخداد، تصاویر و ارتباط پلاک/چهره |

## وضعیت پیاده‌سازی

- پروژهٔ مستقل `HshDetectionService` با `net8.0-windows` به Solution اضافه شده است.
- فایل‌های `settings.json`، `service-settings.json`، `face-database.db`، `events.db` و artifactها در data root سرویس نگهداری می‌شوند.
- endpointهای مدیریت تنظیمات، دوربین، ROI، task، Face Database، trigger، event، snapshot و Overlay زنده فعال هستند.
- endpoint offer و فریم composited برای clientهای legacy فعال‌اند؛ مسیر UI وب برای
  دوربین MediaMTX از WHEP خام استفاده می‌کند و SVG Overlay را جداگانه می‌گیرد.
- WHEP proxy برای دوربین‌های MediaMTX فعال است؛ UI وب ویدئوی خام را از WHEP می‌گیرد و `/api/v1/streams/{cameraId}/overlay` را جداگانه مصرف می‌کند.
- SignalR قبل از broadcast، event را در SQLite ثبت می‌کند و reconnect با sequence replay انجام می‌شود.
- association پلاک/چهره در runtime فعال است: componentهای Plate و Face در همان فریم یا پنجرهٔ زمانی محدود به هم متصل می‌شوند و event مستقل با `associationType`های `SameFrame`، `TemporalAssociation` یا `Standalone` ساخته می‌شود. policy هر اتصال نیز از طریق `ClientSubscription` جداگانه فیلتر می‌شود.

برای راه‌اندازی و API واقعی به [راهنمای پروژهٔ سرویس](../../HshDetectionService/README.md) مراجعه کنید.
