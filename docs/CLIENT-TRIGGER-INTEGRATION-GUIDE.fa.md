# راهنمای اتصال پروژهٔ مشتری به Trigger سرویس تشخیص پلاک

## هدف سند

این سند قرارداد اتصال یک برنامهٔ مستقل مشتری به `HshDetectionService` را تعریف می‌کند. برنامهٔ مشتری خارج از Solution سرویس است و باید بتواند رخدادهای تشخیص را دریافت کند، فقط رخدادهای مربوط به Trigger موردنظر را پردازش کند، پس از قطع ارتباط ادامه دهد و هیچ رخدادی را به‌علت restart یا قطعی کوتاه شبکه دوباره اجرا نکند.

این سند برای مطالعهٔ یک عامل AI و پیاده‌سازی در پروژهٔ مشتری نوشته شده است.

## تصمیم معماری اجباری

در نسخهٔ فعلی سرویس، مسیر قابل اتکا برای اتصال کلاینت این است:

```text
دوربین و inference
        ↓
HshDetectionService
        ↓ ثبت durable در Event Store
SignalR Hub: /hubs/detections
        ↓
پروژهٔ مشتری
        ↓
اکشن مشتری: رله، API، کنترل تردد، ثبت دیتابیس و غیره
```

Trigger در سمت سرویس شرط رخداد را ارزیابی می‌کند و نتیجه را داخل `event.trigger` قرار می‌دهد. کلاینت باید از SignalR رخداد را دریافت کند و بر اساس `trigger.matched` و `matchingTriggerIds` تصمیم بگیرد.

در نسخهٔ فعلی نباید فرض شود که `TriggerAction.Type = Webhook` مستقیماً Webhook قابل‌اعتماد به پروژهٔ مشتری ارسال می‌کند. Webhook مستقل در مستندات طراحی شده، اما dispatcher فعلی سرویس برای آن مسیر وجود ندارد. اگر ارسال HTTP باید توسط خود سرویس انجام شود، از بخش `Invocations` استفاده شود؛ در غیر این صورت کلاینت مشتری رخداد SignalR را دریافت و خودش اکشن را اجرا کند.

## اگر Trigger قبلاً در سرویس تعریف شده باشد

بله، در این حالت کلاینت نباید سناریو، نوع تشخیص، confidence یا شرط پلاک را دوباره تعریف کند. این منطق باید فقط در Trigger سرویس باشد. کلاینت فقط یکی از دو قرارداد زیر را مصرف می‌کند:

### گزینهٔ پیشنهادی برای کلاینت ساده: Invocation سرویس به endpoint کلاینت

یک endpoint در پروژهٔ مشتری بساز، مثلاً:

```text
POST http://customer-client:5100/api/plate-trigger
```

سپس در سرویس یک `Invocation` با این فیلترها تعریف کن:

```json
{
  "id": "send-customer-gate-trigger",
  "name": "Send customer gate trigger",
  "enabled": true,
  "type": "Web",
  "cameraIds": [],
  "eventTypes": [],
  "triggered": true,
  "triggerIds": ["customer-gate-plate"],
  "minimumConfidence": null,
  "plateTextEquals": null,
  "timeoutSeconds": 15,
  "maxRetries": 3,
  "retryDelaySeconds": 30,
  "web": {
    "url": "http://customer-client:5100/api/plate-trigger",
    "method": "POST",
    "contentType": "application/json",
    "authenticationType": "ApiKey",
    "authenticationValue": "customer-api-key",
    "headers": {}
  },
  "mappings": [
    { "target": "eventId", "source": "eventId" },
    { "target": "sequence", "source": "sequence" },
    { "target": "plateText", "source": "components.plate.plateText" },
    { "target": "confidence", "source": "components.plate.confidence" },
    { "target": "cameraId", "source": "source.cameraId" },
    { "target": "cameraName", "source": "source.cameraName" },
    { "target": "occurredAtUtc", "source": "occurredAtUtc" },
    { "target": "triggerIds", "source": "trigger.matchingTriggerIds" }
  ]
}
```

در این مدل، کلاینت اصلاً `scenario` یا `mode` ارسال نمی‌کند. سرویس تشخیص می‌دهد، Trigger را match می‌کند، `Triggered=true` و `TriggerIds` را بررسی می‌کند و سپس با retry به endpoint مشتری درخواست می‌فرستد.

نکتهٔ مهم: در implementation فعلی `Invocation` فقط fieldهایی را که در `mappings` آمده‌اند در body می‌سازد. اگر `mappings` خالی باشد، body برابر `{}` است؛ بنابراین برای ارسال payload کامل باید mappingهای لازم را صریحاً تعریف کرد یا در آینده قابلیت `FullEnvelope` به سرویس اضافه شود.

### گزینهٔ SignalR برای کلاینتی که live/replay کامل می‌خواهد

کلاینت می‌تواند با `Subscribe` وصل شود و فقط `lastSequence` و subscription تحویل را ارسال کند. اما Hub فعلی فیلتر مستقیم `TriggerIds` ندارد؛ بنابراین کلاینت باید رخدادهای دریافتی را با این منطق فیلتر کند:

```text
event.trigger.matched == true
و
ExpectedTriggerIds ∩ event.trigger.matchingTriggerIds != ∅
```

در این حالت کلاینت سناریو را برای تصمیم‌گیری ارسال نمی‌کند، اما ممکن است eventهای غیرمرتبط را نیز دریافت کند. برای حجم کم این روش قابل قبول است. برای حجم بالا یا زمانی که کلاینت فقط یک Trigger را لازم دارد، Invocation HTTP گزینهٔ ساده‌تر است.

### انتخاب نهایی

```text
فقط اجرای اکشن در برنامهٔ مشتری و نیاز به retry پایدار
    → Invocation HTTP به endpoint مشتری

نیاز به live stream، replay، چند نوع event و کنترل کامل cursor
    → SignalR + Subscribe + idempotency
```

## اطلاعاتی که پروژهٔ مشتری باید قابل تنظیم داشته باشد

این مقادیر نباید در کد hard-code شوند:

```text
ServiceBaseUrl       مثال: http://192.168.1.20:5080
ServiceApiKey        مقدار Security.ApiKey سرویس
HubPath              /hubs/detections
ExpectedTriggerIds   شناسهٔ Triggerهای مجاز
CameraIds            شناسهٔ دوربین‌های مجاز، اختیاری
LastSequenceStore    مسیر durable برای ذخیرهٔ cursor
ArtifactDownloadMode دریافت lazy یا eager تصاویر
```

URL نهایی Hub برابر است با:

```text
{ServiceBaseUrl}/hubs/detections
```

## احراز هویت

برای درخواست‌های HTTP از header زیر استفاده شود:

```http
X-Hsh-Api-Key: <ServiceApiKey>
```

مسیرهای `/health/live` و `/health/ready` برای health check بدون API key قابل دسترسی هستند. برای استقرار remote، مقدار `AllowLoopbackWithoutApiKey` نباید باعث بازشدن مسیرهای مدیریتی برای شبکهٔ مشتری شود.

کلاینت باید API key را در log، exception، URL، payload یا پیام کاربر چاپ نکند. برای SignalR نیز header احراز هویت هنگام برقراری connection تنظیم شود. اگر پروژهٔ مشتری browser-only است، ابتدا محدودیت ارسال header در WebSocket مرورگر بررسی شود؛ راه‌حل امن در این حالت استفاده از backend/proxy هم‌مبدأ یا اضافه‌کردن روش token رسمی به سرویس است، نه قرار دادن API key در JavaScript عمومی.

## تعریف Trigger در سرویس

مدیریت Trigger از API زیر انجام می‌شود:

```text
GET    /api/v1/triggers
GET    /api/v1/triggers/{triggerId}
POST   /api/v1/triggers
PATCH  /api/v1/triggers/{triggerId}
DELETE /api/v1/triggers/{triggerId}
```

نمونهٔ Trigger برای یک پلاک مشخص:

```json
{
  "id": "customer-gate-plate",
  "name": "Customer gate plate",
  "enabled": true,
  "cameraIds": ["camera-entrance"],
  "taskIds": [],
  "kinds": ["Plate"],
  "labelEquals": null,
  "identityId": null,
  "plateTextEquals": "12الف34567",
  "minimumConfidence": 0.70,
  "cooldownSeconds": 5,
  "actions": [
    {
      "type": "LiveEvent",
      "target": null,
      "enabled": true
    }
  ]
}
```

نکات:

- `cameraIds` خالی یعنی همهٔ دوربین‌ها.
- `taskIds` خالی یعنی همهٔ taskها.
- `kinds` نوع component تشخیص است، نه الزاماً مقدار `eventType`. برای پلاک معمولاً `Plate` یا نوع‌های پشتیبانی‌شدهٔ پلاک استفاده می‌شود.
- `plateTextEquals` برای پلاک خاص است.
- `minimumConfidence` بین صفر و یک است.
- `cooldownSeconds` جلوی اجرای تکراری Trigger برای یک کلید تشخیص را می‌گیرد؛ کلاینت همچنان باید idempotent باشد.
- برای رخداد ترکیبی، مقدارهایی مانند `PlateFaceAssociation` یا `PlatePalmAssociation` طبق قرارداد سرویس استفاده می‌شوند.

در صورت عدم نیاز پروژهٔ مشتری به مدیریت Trigger، این بخش فقط توسط administrator یا پنل سرویس انجام شود و کلاینت فقط `ExpectedTriggerIds` را مصرف کند.

## قرارداد رخداد

مدل اصلی رخداد `DetectionEventEnvelope` است:

```json
{
  "eventId": "event-guid",
  "sequence": 18420,
  "payloadVersion": 1,
  "eventType": "PlateDetected",
  "scenario": "PlateOnly",
  "occurredAtUtc": "2026-10-12T10:20:30.125Z",
  "receivedAtUtc": "2026-10-12T10:20:30.180Z",
  "source": {
    "serviceNodeId": "node-01",
    "cameraId": "camera-entrance",
    "cameraName": "Entrance",
    "taskId": "task-plate",
    "taskName": "Plate recognition",
    "roiId": "roi-gate",
    "roiName": "Main gate",
    "sourceFrameSequence": 991238,
    "associationType": "Standalone",
    "associationAgeMs": 0,
    "frameWidth": 1920,
    "frameHeight": 1080
  },
  "trigger": {
    "matched": true,
    "cooldownApplied": false,
    "matchingTriggerIds": ["customer-gate-plate"],
    "suppressedTriggerIds": []
  },
  "components": {
    "plate": {
      "kind": "Plate",
      "label": "12الف34567",
      "plateText": "12الف34567",
      "confidence": 0.91
    }
  },
  "artifacts": []
}
```

رخدادهای فعلی سرویس شامل این نوع‌ها هستند:

```text
PlateDetected
FaceRecognized
FaceUnknown
PalmRecognized
PalmUnknown
PlateFaceMatched
PlatePalmMatched
```

نام `eventType` و ساختار componentها باید از payload واقعی خوانده شود. عامل AI نباید با `label` یا جایگاه آرایه‌ها منطق تشخیص بنویسد؛ برای پلاک از `components.plate.plateText` و برای Trigger از `trigger.matchingTriggerIds` استفاده شود.

## فیلتر دریافت کلاینت

متد SignalR به شکل زیر فراخوانی می‌شود:

```text
Subscribe(lastSequence, subscription)
```

نمونهٔ subscription برای دریافت رخدادهای پلاک یک دوربین:

```json
{
  "mode": "Plate",
  "cameraIds": ["camera-entrance"],
  "roiIds": [],
  "faceRequired": false,
  "plateRequired": true,
  "palmRequired": false,
  "includeFace": false,
  "includePlate": true,
  "includePalm": false,
  "includeUnknownFace": true,
  "includeUnknownPalm": true,
  "includeArtifacts": true,
  "windowMs": 1500,
  "cooldownSeconds": 0
}
```

این subscription فقط فیلتر تحویل همین connection است و Trigger سرویس را تغییر نمی‌دهد. برای دریافت فقط Triggerهای خاص:

1. subscription را برای modality/camera مناسب تنظیم کن.
2. در handler رخداد، `event.trigger.matched` را بررسی کن.
3. اگر `ExpectedTriggerIds` مقدار دارد، اشتراک آن با `event.trigger.matchingTriggerIds` را بررسی کن.
4. رخدادهای بدون match را برای اکشن Trigger اجرا نکن.

## پروتکل SignalR و reconnect

نام eventهایی که کلاینت باید ثبت کند:

```text
replayStarted
detection
replayCompleted
cursorExpired
```

ترتیب صحیح اتصال:

```text
ساخت connection
ثبت handlerها
StartAsync
InvokeAsync("Subscribe", lastSequence, subscription)
دریافت replay
دریافت live event
```

سرویس هنگام Subscribe ابتدا یک watermark می‌گیرد، رخدادهای بعد از `lastSequence` تا watermark را replay می‌کند و سپس live delivery را ادامه می‌دهد. کلاینت نباید هنگام reconnect فقط به live event متکی باشد.

منطق reconnect الزامی:

1. آخرین sequence پردازش‌شده را روی دیسک ذخیره کن.
2. پس از هر رخداد، ابتدا منطق کسب‌وکار را با موفقیت اجرا کن.
3. سپس cursor را ذخیره کن.
4. در صورت قطع connection، با backoff دوباره وصل شو.
5. بعد از اتصال مجدد، دوباره `Subscribe(lastSequence, subscription)` را صدا بزن.
6. اگر رخداد قبل از ذخیرهٔ cursor دوباره رسید، با `eventId` آن را duplicate تشخیص بده.

تحویل رخداد از نظر شبکه `at-least-once` است؛ exactly-once قابل فرض نیست.

## رفتار `cursorExpired`

اگر sequence ذخیره‌شده قدیمی‌تر از retention سرویس باشد، Hub پیام زیر را می‌فرستد و replay عادی را ادامه نمی‌دهد:

```json
{
  "requestedAfter": 10,
  "availableFrom": 9000,
  "requiresResync": true
}
```

در این حالت عامل AI باید یکی از policyهای زیر را که در تنظیمات محصول تعیین شده اجرا کند:

- `ResyncLatest`: دریافت آخرین رخدادها با `GET /api/v1/events?limit=2000` و شروع cursor از آخرین sequence.
- `ResyncFromAvailable`: دریافت با `GET /api/v1/events?afterSequence={availableFrom - 1}&limit=2000` و صفحه‌بندی تا پایان.
- `StopAndAlert`: توقف اکشن‌ها و اعلام نیاز به resync دستی.

برای محصول کنترل تردد، policy پیشنهادی `ResyncFromAvailable` است؛ رخدادهایی که خارج از retention هستند دیگر از سرویس قابل بازیابی نیستند و باید به‌عنوان gap در log ثبت شوند.

## APIهای replay و artifact

برای همگام‌سازی و بررسی رخدادها:

```text
GET /api/v1/events?afterSequence=0&limit=2000
GET /api/v1/events/{eventId}
GET /api/v1/events/{eventId}/artifacts
GET /api/v1/events/{eventId}/artifacts/{artifactId}
```

برای دانلود artifact، مقدار `downloadUrl` ممکن است relative باشد؛ آن را با `ServiceBaseUrl` ترکیب کن و header `X-Hsh-Api-Key` را اضافه کن.

تصاویر معمولاً داخل پیام live به‌صورت Base64 نیستند. ابتدا metadata رخداد را ذخیره کن و فقط در صورت نیاز artifact را lazy دانلود کن.

## نمونهٔ پیاده‌سازی C# برای کلاینت مشتری

پکیج پیشنهادی:

```text
Microsoft.AspNetCore.SignalR.Client
```

نمونهٔ اسکلت اتصال:

```csharp
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;

var connection = new HubConnectionBuilder()
    .WithUrl($"{serviceBaseUrl.TrimEnd('/')}/hubs/detections", options =>
    {
        options.Headers["X-Hsh-Api-Key"] = serviceApiKey;
    })
    .WithAutomaticReconnect(new[]
    {
        TimeSpan.Zero,
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15)
    })
    .Build();

connection.On<JsonElement>("detection", async envelope =>
{
    string eventId = envelope.GetProperty("eventId").GetString()!;
    long sequence = envelope.GetProperty("sequence").GetInt64();

    if (await eventStore.IsProcessedAsync(eventId))
    {
        await cursorStore.SaveIfGreaterAsync(sequence);
        return;
    }

    if (IsExpectedTrigger(envelope))
    {
        await customerActionDispatcher.DispatchAsync(envelope);
        await eventStore.MarkProcessedAsync(eventId, sequence, "Succeeded");
    }
    else
    {
        // رخداد معتبر است، اما برای این کلاینت اکشن ندارد؛ cursor باید جلو برود.
        await eventStore.MarkProcessedAsync(eventId, sequence, "Ignored");
    }

    await cursorStore.SaveIfGreaterAsync(sequence);
});

connection.On<JsonElement>("cursorExpired", payload =>
{
    // اجرای policy resync محصول مشتری
    return Task.CompletedTask;
});

await connection.StartAsync();

long lastSequence = await cursorStore.LoadAsync();
await connection.InvokeAsync("Subscribe", lastSequence, new
{
    mode = "Plate",
    cameraIds = Array.Empty<string>(),
    roiIds = Array.Empty<string>(),
    plateRequired = true,
    includePlate = true,
    includeFace = false,
    includePalm = false,
    includeArtifacts = true,
    windowMs = 1500,
    cooldownSeconds = 0
});
```

این کد فقط اسکلت است. عامل AI باید `eventStore`، `cursorStore` و `customerActionDispatcher` را با storage و نیاز واقعی پروژهٔ مشتری پیاده‌سازی کند.

## قواعد idempotency

کلید اصلی جلوگیری از اجرای دوباره:

```text
eventId
```

`sequence` برای ترتیب و replay است، اما به‌تنهایی جایگزین `eventId` نیست. storage مشتری باید حداقل این جدول یا معادل آن را داشته باشد:

```text
ProcessedEvents
----------------
EventId           UNIQUE
Sequence
ProcessedAtUtc
ActionStatus
Error
```

ثبت رخداد و اجرای اکشن باید با یکی از این policyها انجام شود:

- اگر اکشن transactionally قابل ثبت است: event را ثبت کن، اکشن را اجرا کن و status را atomically تکمیل کن.
- اگر اکشن سخت‌افزاری است: قبل از ارسال، وضعیت `Processing` و بعد از نتیجه `Succeeded/Failed` ذخیره شود.
- اگر crash بین اجرای رله و ذخیرهٔ status رخ دهد، ممکن است تکرار اجتناب‌ناپذیر باشد؛ اکشن باید با cooldown و idempotency داخلی تا حد ممکن تکرار را بی‌خطر کند.

## اجرای اکشن مشتری

لایهٔ اتصال SignalR نباید مستقیماً به GPIO، Serial یا PLC وابسته شود. این abstraction را ایجاد کن:

```text
ITriggerActionDispatcher
    ├── HttpCustomerDispatcher
    ├── RelayDispatcher
    ├── ModbusDispatcher
    └── SqlDispatcher
```

Dispatcher باید این اطلاعات را دریافت کند:

```text
EventId
Sequence
TriggerIds
CameraId
PlateText
Identity
OccurredAtUtc
Confidence
Artifact references
```

اگر مقصد فقط HTTP است، header زیر برای idempotency ارسال شود:

```http
X-Event-Id: <eventId>
X-Trigger-Id: <triggerId>
```

اگر مقصد چنین headerهایی را نمی‌پذیرد، `eventId` در body قرار بگیرد.

## تست اتصال

### 1. تست دسترسی سرویس

```http
GET http://server:5080/health/ready
```

باید پاسخ `200` و `status = ready` دریافت شود.

### 2. تست وضعیت و cursor

```http
GET http://server:5080/api/v1/service/status
X-Hsh-Api-Key: <key>
```

مقدارهای مهم:

```text
ready
eventSequence
droppedEventCount
cameras
```

`droppedEventCount` باید صفر باشد؛ غیرصفر بودن آن باید در تست و مانیتورینگ گزارش شود.

### 3. تست Trigger

یک Trigger با `cooldownSeconds = 0`، دوربین مشخص و پلاک تست بساز. سپس یک رخداد واقعی ایجاد کن و بررسی کن:

- `event.trigger.matched == true`
- شناسهٔ Trigger در `matchingTriggerIds` وجود دارد.
- `components.plate.plateText` مقدار مورد انتظار دارد.
- `sequence` افزایش یافته است.
- رخداد فقط یک‌بار اکشن مشتری را اجرا می‌کند.

### 4. تست قطع شبکه

در حین اجرا connection را قطع کن، چند رخداد تولید کن، سپس connection را برگردان. کلاینت باید رخدادهای بعد از آخرین cursor را replay کند.

### 5. تست duplicate

یک payload یکسان را دوبار به handler بده. اجرای اکشن باید فقط یک‌بار انجام شود.

### 6. تست cursorExpired

cursor را عمداً قدیمی تنظیم کن یا یک event قدیمی‌تر از retention درخواست کن. کلاینت باید `cursorExpired` را مدیریت کند و وارد loop بی‌نهایت reconnect نشود.

## معیار پذیرش پیاده‌سازی عامل AI

پیاده‌سازی زمانی پذیرفته است که:

- URL، API key، Trigger ID و مسیر cursor از configuration خوانده شوند.
- اتصال SignalR به `/hubs/detections` برقرار شود.
- handlerهای `replayStarted`، `detection`، `replayCompleted` و `cursorExpired` ثبت شوند.
- `Subscribe(lastSequence, subscription)` بعد از start و هر reconnect فراخوانی شود.
- cursor فقط بعد از پردازش موفق، یا بعد از ثبت صریح رخداد به‌عنوان `Ignored`، ذخیره شود.
- duplicate بر اساس `eventId` حذف شود.
- فقط رخدادهای `trigger.matched` و Trigger IDهای مجاز اکشن را اجرا کنند.
- `cursorExpired`، timeout، HTTP 401، قطع شبکه و `droppedEventCount` در log و health وضعیت مشخص داشته باشند.
- artifactها با URL نسبی و API key صحیح دانلود شوند.
- API key و تصاویر حساس در log چاپ نشوند.
- تست واحد برای filter، idempotency، reconnect و cursorExpired وجود داشته باشد.
- تست integration با یک سرویس واقعی یا fake SignalR server انجام شود.

## مواردی که عامل AI نباید فرض کند

- Trigger به‌تنهایی به معنای ارسال Webhook به پروژهٔ مشتری نیست.
- اتصال زنده بدون replay برای محصول قابل قبول نیست.
- دریافت یک رخداد به معنای اجرای exactly-once نیست.
- `sequence` جایگزین `eventId` نیست.
- `label` همیشه متن پلاک نیست؛ متن پلاک از component پلاک خوانده شود.
- تصاویر الزاماً داخل payload نیستند؛ از artifact endpoint استفاده شود.
- eventهای association ممکن است چند component داشته باشند.
- endpointهای طراحی‌شده در documentation که در runtime فعلی route ندارند نباید بدون بررسی فراخوانی شوند.

## منابع داخل پروژهٔ سرویس

این فایل بر اساس implementation فعلی استخراج شده است. برای تغییر قرارداد، ابتدا این بخش‌ها بررسی شوند:

- مدل رخداد و Trigger: `HshDetectionService/ServiceModels.cs`
- Hub و replay: `HshDetectionService/ServiceApi.cs`، کلاس `DetectionHub`
- ارزیابی Trigger: `HshDetectionService/DetectionRuntimeHost.cs`
- routeهای API: `HshDetectionService/ServiceApi.cs`
- احراز هویت: `HshDetectionService/Program.cs`
- قرارداد رخداد: `docs/service/07-DETECTION-EVENT-CONTRACT.md`
- قرارداد event و replay: `docs/service/03-EVENTS-TRIGGERS.md`
- ارسال durable HTTP از سرویس: `docs/service/08-INVOCATIONS.md`
