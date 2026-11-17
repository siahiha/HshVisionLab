# طراحی سرویس تشخیص HSH

این پوشه معماری، قراردادها و راهنمای اجرایی `HshDetectionService` را نگه می‌دارد. پروژهٔ سرویس اکنون در Solution ایجاد شده و هستهٔ اجرایی آن شامل API، ذخیره‌سازی event، SignalR replay، WebRTC و اتصال به موتور تشخیص است.

## هدف

سرویس باید موتور تشخیص فعلی را بدون وابستگی به رابط کاربری اجرا کند، چند دوربین و چند وظیفهٔ تشخیص را مدیریت کند، تصویر زندهٔ دارای Drawing را به کلاینت‌ها بدهد و رخدادها را حتی در زمان قطع بودن UI از دست ندهد.

## تصمیم‌های اصلی

1. سرویس مالک اصلی تنظیمات فعال، Identity Database و Event Store است.
2. `HshVisionLab` بخشی از این سرویس نیست و هیچ حالت `Service Manager` برای اتصال آن به سرویس تعریف نمی‌شود؛ برنامهٔ Windows مستقل و مستقیم اجرا می‌شود. کلاینت UI این سرویس `DetectionManagerUi` است.
3. هر `CameraSettings.Rois[].Processing[]` در نسخهٔ اول یک Detection Task است؛ مدل جدید و تکراری برای Task ایجاد نمی‌شود.
4. نرخ Rendering مستقل از نرخ inference است.
5. UI وب فعلی تصویر کامپوزیت‌شده را با WebRTC می‌گیرد و inference منتظر encoder یا کلاینت نمی‌ماند. درگاه مستقل MediaMTX/WHEP نیز برای sourceهای فعال RTSP، تصویر خام و Overlay جداگانه ارائه می‌کند.

6. رخداد قبل از broadcast در Event Store ثبت می‌شود و کلاینت با cursor ترتیبی replay می‌کند.
7. SignalR/WebSocket برای کنترل، رخداد و signaling و WebRTC برای ویدئوی زنده استفاده می‌شود.
8. رخدادهای عملیاتی در `events.db` جدا از `identity-database.db` ذخیره می‌شوند؛ Identity Database شامل People، Plate، Face و Palm است.

`HshVisionLab` و `DetectionManagerUi` دو برنامهٔ مستقل هستند. آن‌ها باید امکانات مشابه داشته باشند، اما هیچ ارتباط runtime، API، SignalR، WHEP، دیتابیس مشترک یا اشتراک process با یکدیگر ندارند. این سند فقط معماری `HshDetectionService` و کلاینت وب آن را تعریف می‌کند.

## اسناد

| سند | موضوع |
| --- | --- |
| [01-ARCHITECTURE.md](01-ARCHITECTURE.md) | مرز پروژه، اجزا و جریان کلی داده |
| [02-RENDERING-WEBRTC.md](02-RENDERING-WEBRTC.md) | فریم، overlay، compositing و WebRTC |
| [../WEB-UI-RECONSTRUCTION-SPEC.md](../WEB-UI-RECONSTRUCTION-SPEC.md) | قرارداد canonical بازسازی UI وب: routeها، componentها، layout و رفتار |
| [../WEB-UI-AND-STREAMING.md](../WEB-UI-AND-STREAMING.md) | دریافت مستقیم FFmpeg، نمایش کامپوزیت‌شدهٔ وب و درگاه مستقل WHEP |
| [03-EVENTS-TRIGGERS.md](03-EVENTS-TRIGGERS.md) | رخداد پایدار، replay، trigger و webhook |
| [04-API.md](04-API.md) | قرارداد کامل API برای تنظیمات، دیتابیس و کلاینت‌ها |
| [05-CONFIGURATION-DATA.md](05-CONFIGURATION-DATA.md) | محل فایل‌ها، مالکیت داده، migration و concurrency |
| [06-IMPLEMENTATION-ROADMAP.md](06-IMPLEMENTATION-ROADMAP.md) | مراحل پیشنهادی پیاده‌سازی و معیار پذیرش |
| [07-DETECTION-EVENT-CONTRACT.md](07-DETECTION-EVENT-CONTRACT.md) | قرارداد کامل رخداد، تصاویر و ارتباط پلاک/چهره |
| [08-INVOCATIONS.md](08-INVOCATIONS.md) | تعریف فراخوانی‌های Web/SQL، Mapping، تصویر، زنجیره، retry، log و تست |
| [09-DETECTOR-COMPLETION-CHECKLIST.md](09-DETECTOR-COMPLETION-CHECKLIST.md) | چک‌لیست و الزام parity برای هر تشخیص جدید |

## وضعیت پیاده‌سازی

- پروژهٔ مستقل `HshDetectionService` با `net8.0-windows` به Solution اضافه شده است.
- فایل‌های `settings.json`، `service-settings.json`، `identity-database.db`، `events.db` و artifactها در data root سرویس نگهداری می‌شوند.
- endpointهای مدیریت تنظیمات، دوربین، ROI، task، Identity Database (Face/Palm/Plate)، trigger، event، snapshot و Overlay زنده فعال هستند.
- Windows و سرویس RTSP را مستقیماً با FFmpeg، یک thread decoder و فقط جدیدترین فریم دریافت می‌کنند.
- endpoint offer و فریم composited مسیر فعال tile، focus و ROI زندهٔ UI وب هستند.
- WHEP proxy برای source معتبر RTSP و فعال، مستقل از دریافت موتور تشخیص در دسترس است؛ مصرف‌کنندهٔ خام می‌تواند `/api/v1/streams/{cameraId}/overlay` را جداگانه مصرف کند. `MediaMtxRuntime` فقط متعلق به پروژهٔ سرویس است.
- SignalR قبل از broadcast، event را در SQLite ثبت می‌کند و reconnect با sequence replay انجام می‌شود.
- association پلاک/چهره/کف‌دست در runtime فعال است و componentهای همان دوربین در همان فریم یا پنجرهٔ زمانی محدود به هم متصل می‌شوند. `ServiceSettingsDocument.HistorySubscription` فقط سناریوی چندپروفایلی ثبت داخلی را تعیین می‌کند؛ پنل تشخیص و تاریخچهٔ عمومی همهٔ رخدادهای ثبت‌شدهٔ دوربین‌ها را نشان می‌دهند. `ClientSubscription` صرفاً policy تحویل هر connection است و `includePalm` را مانند `includeFace` و `includePlate` پشتیبانی می‌کند. `TriggerDefinition` مسیر مستقل ارسال به کلاینت است و رخداد match‌شده با context کامل موجود برای کلاینت ارسال می‌شود.

برای راه‌اندازی و API واقعی به [راهنمای پروژهٔ سرویس](../../HshDetectionService/README.md) مراجعه کنید.
