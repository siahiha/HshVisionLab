# HshDetectionService

سرویس Windows مبتنی بر .NET 8 برای اجرای headless موتور تشخیص، مدیریت دوربین‌ها و taskها، ارائهٔ preview زنده و انتشار رخدادهای پایدار تشخیص است.

## اجرا در توسعه

```powershell
dotnet run --project .\HshDetectionService\HshDetectionService.csproj
```

سرویس فقط API، SignalR، stream و inference را ارائه می‌کند و UI در آن host نمی‌شود. پنل React در پروژهٔ `DetectionManagerUi` جداگانه build/serve می‌شود و با `VITE_HSH_API_BASE_URL` به این سرویس وصل می‌گردد؛ routeهای `/api` و `/hubs` متعلق به سرویس هستند.

برای build کامل خروجی سرویس:

```powershell
Push-Location .\DetectionManagerUi
npm run build
Pop-Location
dotnet build .\HshDetectionService\HshDetectionService.csproj -c Debug
```

اگر سرویس Windows در حال اجراست، قبل از build باید آن را با دسترسی Administrator متوقف کنید تا فایل executable قفل نباشد، سپس بعد از build دوباره start کنید.

data root سرویس برابر پوشهٔ اجرای `HshDetectionService.exe` است. سرویس هنگام اجرا فایل‌های زیر را در همین پوشه ایجاد می‌کند:

- `config\settings.json`: همان `AppSettings` برنامهٔ HshVisionLab
- `config\service-settings.json`: تنظیمات HTTP، امنیت، retention و triggerها
- `database\face-database.db`: دیتابیس SQLite چهره و sampleهای aligned
- `database\events.db`: event log ترتیبی برای replay
- `media\event-artifacts`: فریم، ROI، crop تشخیص و aligned face

## APIهای اصلی

```text
GET  /health/live
GET  /health/ready
GET  /api/v1/service/status
GET  /api/v1/service/capabilities
GET  /api/v1/settings
PUT  /api/v1/settings

GET/POST/PUT/DELETE /api/v1/cameras
POST /api/v1/cameras/{cameraId}/start|stop|restart
GET/POST/PUT/DELETE /api/v1/cameras/{cameraId}/rois
POST/PUT/DELETE /api/v1/cameras/{cameraId}/rois/{roiId}/tasks

GET/POST/PATCH/DELETE /api/v1/face/people
POST /api/v1/face/people/{personId}/samples   (multipart image)
GET  /api/v1/face/samples/{sampleId}/image
POST /api/v1/face/samples/{sampleId}/move

GET /api/v1/events?afterSequence=0&limit=200
DELETE /api/v1/events?fromUtc=...&toUtc=...  (هر دو خالی = حذف همه)
GET /api/v1/events/{eventId}/artifacts/{artifactId}
GET /api/v1/triggers
POST /api/v1/triggers
GET /api/v1/streams/{cameraId}/overlay
POST /api/v1/streams/{cameraId}/webrtc/offer
POST/PATCH/DELETE /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
GET /api/v1/streams/{cameraId}/snapshot
```

برای حذف تاریخچه، `DELETE /api/v1/events` با `fromUtc` و `toUtc` به‌صورت ISO-8601 استفاده می‌شود؛ حذف بدون بازه تمام eventها و artifactهای تصویری آن‌ها را پاک می‌کند. برای routeهای مدیریتی از `X-Hsh-Api-Key` استفاده می‌شود. به‌صورت پیش‌فرض دسترسی loopback بدون کلید برای ابزار تنظیمات محلی مجاز است و باید برای استقرار remote غیرفعال شود.

## قرارداد رخداد

هر event قبل از ارسال live در `events.db` ذخیره می‌شود و شامل `EventId`، `Sequence`، source، trigger state، componentهای `plate`/`face` و artifact descriptorهاست. artifactهای تصویری به‌جای Base64 با URL سرویس ارائه می‌شوند. برای face، person id/number/name، unknown state، similarity، matched sample id و aligned crop ذخیره می‌شود. برای plate، متن، validation، threshold و characterهای OCR با bounds و confidence ارائه می‌شود.

کلاینت SignalR به `/hubs/detections` وصل می‌شود و متد `Subscribe(lastSequence)` را صدا می‌زند. اگر cursor در retention موجود نباشد، پیام `cursorExpired` دریافت می‌کند و باید resync کامل انجام دهد.

## WebRTC

درخواست offer اختصاصی شامل `{ "type": "offer", "sdp": "..." }` است و پاسخ
شامل `sessionId` و answer SDP خواهد بود. این مسیر آخرین فریم composited را
encode می‌کند و برای کلاینت‌های legacy باقی مانده است؛ session با
`DELETE /api/v1/streams/webrtc/{sessionId}` بسته می‌شود.

برای دوربین‌های `MediaMTX`، مسیر اصلی وب از WHEP خام MediaMTX استفاده می‌کند:

```text
Camera RTSP → MediaMTX path → WHEP → Browser <video>
                           └→ local RTSP → Engine/Detection
GET /api/v1/streams/{cameraId}/overlay → Browser SVG overlay (`LiveOverlaySvg`)
```

`overlay` فقط دادهٔ سبک ROI، bounds و مشخصات detection/processing overlay را
برمی‌گرداند؛ ویدئو در این مسیر از `FrameReady`، تبدیل Bitmap یا `WebRtcGateway`
عبور نمی‌کند. برای جلوگیری از cache شدن وضعیت، کلاینت query timestamp کوتاه
اضافه می‌کند. Snapshot برای backendهای غیر MediaMTX و مصرف‌کننده‌هایی که آخرین
فریم کامپوزیت‌شده را می‌خواهند همچنان فعال است.

## نصب Windows Service

پس از publish self-contained یا framework-dependent، فایل خروجی را در مسیر deployment قرار دهید و با `sc.exe create` یا ابزار نصب سازمانی ثبت کنید. اجرای سرویس باید با حسابی انجام شود که به streamهای RTSP، مدل‌ها، license و data root دسترسی داشته باشد.
