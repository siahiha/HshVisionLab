# HshDetectionService

سرویس Windows مبتنی بر .NET 8 برای اجرای headless موتور تشخیص، مدیریت دوربین‌ها و taskها، ارائهٔ preview زنده و انتشار رخدادهای پایدار تشخیص است.

## اجرا در توسعه

```powershell
dotnet run --project .\HshDetectionService\HshDetectionService.csproj
```

سرویس فقط API، SignalR، stream و inference را برای `DetectionManagerUi` ارائه می‌کند و UI اصلی را host نمی‌کند. پنل React در پروژهٔ `DetectionManagerUi` جداگانه build/serve می‌شود و با `VITE_HSH_API_BASE_URL` به این سرویس وصل می‌گردد؛ routeهای `/api` و `/hubs` متعلق به سرویس هستند. `HshVisionLab` مشتری این سرویس نیست و باید به‌صورت مستقل با engineهای داخل solution و اتصال مستقیم به دوربین اجرا شود.

دریافت فریم در موتور فقط با FFmpeg مستقیم انجام می‌شود. decoder از یک thread
استفاده می‌کند و پردازش زنده همیشه جدیدترین فریم را برمی‌دارد؛ صف تاریخچهٔ
فریم وجود ندارد. در تنظیمات دوربین فقط transport شبکه (TCP/UDP) و زمان
reconnect قابل تنظیم هستند.

برای build کامل خروجی سرویس:

```powershell
Push-Location .\DetectionManagerUi
npm run build
Pop-Location
dotnet build .\HshDetectionService\HshDetectionService.csproj -c Debug
```

اگر سرویس Windows در حال اجراست، قبل از build باید آن را با دسترسی Administrator متوقف کنید تا فایل executable قفل نباشد، سپس بعد از build دوباره start کنید.

data root سرویس برابر پوشهٔ اجرای `HshDetectionService.exe` است. سرویس هنگام اجرا فایل‌های زیر را در همین پوشه ایجاد می‌کند:

- `config\settings.json`: تنظیمات سرویس با schema سازگار با `AppSettings`؛ فایل مستقل از `HshVisionLab` است
- `config\service-settings.json`: تنظیمات HTTP، امنیت، retention و triggerها
- `database\identity-database.db`: دیتابیس مرکزی SQLite اشخاص، پلاک‌ها، نمونه‌های Face و نمونه‌های Palm
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
POST /api/v1/face/people/bulk-delete      (JSON: { "personIds": ["..."] })
POST /api/v1/face/people/{personId}/samples   (multipart image)
GET  /api/v1/face/samples/{sampleId}/image
POST /api/v1/face/samples/{sampleId}/move

GET  /api/v1/palm/people
GET  /api/v1/palm/people/summary
GET  /api/v1/palm/people/{personId}/samples
GET  /api/v1/palm/database/health
GET  /api/v1/palm/similar?minimumSimilarity=0.40&onlyDifferentPeople=false
POST /api/v1/palm/samples                 (multipart image)
GET  /api/v1/palm/samples/{sampleId}/image
DELETE /api/v1/palm/samples/{sampleId}
POST /api/v1/palm/samples/{sampleId}/move

GET  /api/v1/identity/people/{personId}/plates
POST /api/v1/identity/people/{personId}/plates
DELETE /api/v1/identity/plates/{plateId}

GET /api/v1/events?afterSequence=0&limit=200
DELETE /api/v1/events?fromUtc=...&toUtc=...  (هر دو خالی = حذف همه)
GET /api/v1/events/{eventId}/artifacts/{artifactId}
GET/POST/PATCH/DELETE /api/v1/invocations
GET  /api/v1/invocations/logs
POST /api/v1/invocations/{invocationId}/test
POST /api/v1/invocations/jobs/{jobId}/retry
GET /api/v1/triggers
POST /api/v1/triggers
GET /api/v1/streams/{cameraId}/overlay
POST /api/v1/streams/{cameraId}/webrtc/offer
PATCH/DELETE /api/v1/streams/webrtc/{sessionId}
GET /api/v1/streams/diagnostics
POST/PATCH/DELETE /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
GET /api/v1/streams/{cameraId}/snapshot
```

برای حذف تاریخچه، `DELETE /api/v1/events` با `fromUtc` و `toUtc` به‌صورت ISO-8601 استفاده می‌شود؛ حذف بدون بازه تمام eventها و artifactهای تصویری آن‌ها را پاک می‌کند. برای routeهای مدیریتی از `X-Hsh-Api-Key` استفاده می‌شود. به‌صورت پیش‌فرض دسترسی loopback بدون کلید برای ابزار تنظیمات محلی مجاز است و باید برای استقرار remote غیرفعال شود. UI وب صفحهٔ `مدیریت افراد` را برای مدیریت مشترک Face/Palm/Plate مصرف می‌کند.

## قرارداد رخداد

هر event قبل از ارسال live در `events.db` ذخیره می‌شود و شامل `EventId`، `Sequence`، source، trigger state، componentهای `plate`/`face`/`palm` و artifact descriptorهاست. artifactهای تصویری به‌جای Base64 با URL سرویس ارائه می‌شوند. برای face و palm، person id/number/name، unknown state، similarity، matched sample id و crop مربوطه ذخیره می‌شود. برای plate، متن، validation، threshold و characterهای OCR با bounds و confidence ارائه می‌شود.

کلاینت SignalR به `/hubs/detections` وصل می‌شود و متد `Subscribe(lastSequence)` را صدا می‌زند. اگر cursor در retention موجود نباشد، پیام `cursorExpired` دریافت می‌کند و باید resync کامل انجام دهد.
برای اتصال مرورگر، origin دقیق UI باید در `http.corsOrigins` تنظیمات HTTP سرویس
وجود داشته باشد؛ originهای محلی UI فعلی `http://127.0.0.1:5081` و
`http://localhost:5081` هستند. این CORS policy برای REST و SignalR مشترک است و
پس از تغییر تنظیمات باید سرویس restart شود.

## WebRTC

هر دوربینِ دارای بیننده، یک worker مستقل و یک encoder مشترک بین بیننده‌های
خود دارد. هر worker فقط یک فریم در حال encode و یک فریم منتظر نگه می‌دارد؛
فریم منتظر با تصویر جدید جایگزین می‌شود. فریم تکراری دوباره encode نمی‌شود.
Bitmap مقصد و بافر I420 تا تغییر اندازه دوباره استفاده می‌شوند. timestamp
ویدئو از زمان یکنواخت دریافت فریم می‌آید.

در offer می‌توان `maxWidth` و `maxHeight` فرستاد. UI اندازهٔ محل نمایش را
خودکار اعلام می‌کند؛ سرویس بزرگ‌ترین نیاز بیننده‌های همان دوربین را تا سقف
`1280×720` اعمال و ابعاد encoder را به مضرب ۱۶ تنظیم می‌کند. اندازهٔ فریم
تشخیص تغییر نمی‌کند. پاسخ جدید `supportsViewportUpdates: true` دارد؛ سپس
`PATCH /api/v1/streams/webrtc/{sessionId}` اندازه و heartbeat را به‌روزرسانی
می‌کند. UI هر ۱۰ ثانیه heartbeat می‌فرستد؛ session جدید پس از ۴۵ ثانیه
بی‌خبری منقضی می‌شود. مذاکرهٔ ناتمام پس از ۳۰ ثانیه پاک می‌شود. کلاینت قدیمی
بدون فیلدهای اندازه، مشمول lease اتصال فعال نیست.

نمای مخفی، خارج از دید یا بسته‌شده session را آزاد می‌کند. سرویس بدون تقاضای
ویدئو یا snapshot، preview نمی‌سازد و دریافت/تشخیص ادامه دارند. تقاضای snapshot
سه ثانیه اعتبار دارد و به‌تنهایی حداکثر ۵ FPS می‌خواهد؛ `runtime.previewFps`
سقف ویدئوی سرویس را تا ۱۵ FPS کنترل می‌کند. درخواست اولین snapshot پس از
بیکاری تا ۷۵۰ میلی‌ثانیه برای فریم تازه صبر می‌کند.

`GET /api/v1/streams/diagnostics` تعداد بیننده، اندازه، نرخ و مدت encode،
تعداد فریم‌های جایگزین/تکراری و وضعیت حافظهٔ منتظر را به تفکیک دوربین می‌دهد.
این آمار شامل URL یا رمز دوربین نیست. `fps` در وضعیت عمومی دوربین نرخ پردازش
تشخیص است و نرخ ویدئوی مرورگر محسوب نمی‌شود.

درخواست offer اختصاصی شامل `{ "type": "offer", "sdp": "..." }` است و پاسخ
شامل `sessionId` و answer SDP خواهد بود. این مسیر آخرین فریم composited را
encode می‌کند و تصویر زندهٔ فعلی پنل وب و نتایج پردازش را ارائه می‌دهد؛ session با
`DELETE /api/v1/streams/webrtc/{sessionId}` بسته می‌شود.

برای منبع RTSP معتبر و فعال، `canWhep` در وضعیت دوربین `true` است. API مستقل
WHEP برای این منابع از gateway اختصاصی MediaMTX در خود سرویس استفاده می‌کند:

```text
Camera RTSP → FFmpeg → Engine/Detection
Camera RTSP → MediaMTX browser gateway → WHEP → Browser <video>
GET /api/v1/streams/{cameraId}/overlay → Browser SVG overlay (`LiveOverlaySvg`)
```

`overlay` فقط دادهٔ سبک ROI، bounds و مشخصات detection/processing overlay را
برمی‌گرداند؛ ویدئو در این مسیر از `FrameReady`، تبدیل Bitmap یا `WebRtcGateway`
عبور نمی‌کند. برای جلوگیری از cache شدن وضعیت، کلاینت query timestamp کوتاه
اضافه می‌کند. پنل وب فعلی برای نمای زنده از stream کامپوزیت‌شده استفاده می‌کند.
Snapshot برای webcam، فایل و سایر منابع غیر RTSP در تب پیش‌نمایش تنظیمات و برای
مصرف‌کننده‌هایی که آخرین فریم کامپوزیت‌شده را می‌خواهند فعال است. gateway فقط
هنگام وجود بیننده به دوربین متصل می‌شود؛ همهٔ بیننده‌ها همان اتصال اضافی را
به اشتراک می‌گذارند. دریافت درگاه برای پخش مرورگر با TCP انجام می‌شود؛ تنظیم
TCP/UDP دوربین برای دریافت مستقل FFmpeg در موتور است.

MediaMTX فقط وابستگی سرویس برای پخش مرورگر است؛ فایل اجرایی آن در خروجی
سرویس قرار می‌گیرد. تنظیمات مدیریت‌شدهٔ آن listenerهای API و WebRTC را فعال
می‌کند. متغیرهای محیطی `HSH_MEDIAMTX_PATH`، `HSH_MEDIAMTX_CONFIG`،
`HSH_MEDIAMTX_API_PORT`، `HSH_MEDIAMTX_WEBRTC_PORT`،
`HSH_MEDIAMTX_WEBRTC_UDP_PORT` و `HSH_MEDIAMTX_ADDITIONAL_HOSTS` برای استقرار
gateway در دسترس‌اند.

## نصب Windows Service

پس از publish self-contained یا framework-dependent، فایل خروجی را در مسیر deployment قرار دهید و با `sc.exe create` یا ابزار نصب سازمانی ثبت کنید. اجرای سرویس باید با حسابی انجام شود که به streamهای RTSP، مدل‌ها، license و data root دسترسی داشته باشد.
### استقلال مدل‌های Plate

در event، تشخیص کادر و OCR دو مرحلهٔ مستقل هستند. `ModelFile` فقط مدل تشخیص
کادر را تعیین می‌کند و `CharacterModelFile` مدل OCR را روی crop هر کادر تعیین
می‌کند. مدل‌های OCR از طریق manifest هم‌نام `.ocr.json` و decoderهای ثبت‌شده
در catalog سرویس شناسایی می‌شوند؛ خروجی CRNN، CNN و YOLO character به قرارداد
استاندارد مشترک تبدیل می‌شود و characterها با confidence و bounds قابل ارائه
هستند.
