# رخدادها، Triggerها و جبران قطع اتصال

## 1. اصل طراحی

> **مرز implementation فعلی:** `EventStore` و replay ترتیبی SignalR فعال هستند و event قبل از broadcast در SQLite ثبت می‌شود. outbox مستقل، dispatcher webhook و API gap جداگانه در نسخهٔ فعلی وجود ندارند و بخش‌های مربوط به آن‌ها در این سند roadmap محسوب می‌شوند.

هر connection در Hub subscription مستقل دارد. `Subscribe(lastSequence, ClientSubscription)` فیلترهای `All`، `Plate` و `KnownFace`، اجباری‌بودن componentها، unknown face، scope دوربین/ROI، پنجرهٔ association و cooldown را برای همان کلاینت اعمال می‌کند.

ارسال live به UI به‌تنهایی قابل‌اعتماد نیست. UI ممکن است خاموش، قطع شبکه یا در حال restart باشد. بنابراین سرویس باید مستقل از وضعیت UI تشخیص بدهد، event را ذخیره کند و بعداً امکان replay بدهد.

```text
Detection result
      ↓
Normalize / deduplicate / cooldown
      ↓
Commit event + outbox transaction
      ↓
Live broadcast
      ↓
Replay for reconnecting clients
```

قطع UI نباید باعث توقف Trigger یا حذف event شود.

## 2. مدل event

هر event دو شناسه دارد:

- `EventId`: شناسهٔ GUID یکتا برای idempotency
- `Sequence`: شمارهٔ افزایشی و سراسری سرویس برای replay مرتب

نمونهٔ فیلدها:

```text
DetectionEvent
 ├── EventId
 ├── Sequence
 ├── EventType
 ├── OccurredAtUtc
 ├── CameraId / CameraName
 ├── TaskId
 ├── RoiId / RoiName
 ├── Kind: Face / Plate / ...
 ├── Label
 ├── IdentityId / PlateText
 ├── Confidence / Similarity
 ├── TrackId
 ├── BoundsPixels
 ├── BoundsNormalized
 ├── SourceFrameSequence
 ├── CropReference
 └── PayloadVersion
```

`Sequence` در SQLite با `INTEGER PRIMARY KEY AUTOINCREMENT` یا مکانیزم معادل ایجاد می‌شود و پس از restart به عقب برنمی‌گردد.

## 3. event خام و event قابل ارسال

هر نتیجهٔ pipeline نباید الزاماً یک event عمومی باشد. برای جلوگیری از flood:

- نتیجهٔ خام inference داخلی باقی بماند.
- Face با identity/track و cooldown نرمال شود.
- Plate با plate text، track و cooldown نرمال شود.
- detectionهای زیر threshold event عمومی نشوند، مگر trigger مخصوص debug فعال باشد.
- برای هر task policy مشخص شود: `EveryAccepted`, `OnEnter`, `OnChange`, `Interval`.

کلید dedup پیشنهادی:

```text
CameraId + TaskId + RoiId + Kind + IdentityId/PlateText/TrackId
```

Cooldown فعلی Face حفظ می‌شود، اما در سرویس باید نتیجهٔ نهایی event policy در Event Store نیز قابل مشاهده باشد.

## 4. ثبت اتمیک

ثبت رخداد و ایجاد outbox باید در یک تراکنش انجام شود:

```text
BEGIN TRANSACTION
  INSERT DetectionEvents
  INSERT EventOutbox rows for enabled actions
COMMIT
```

بعد از commit:

- SignalR broadcast انجام می‌شود.
- Webhook dispatcher outbox را ارسال می‌کند.
- اگر process قبل از broadcast crash کند، event بعداً از outbox یا replay ارسال می‌شود.

هیچ eventی فقط در memory نگه داشته نشود.

## 5. اتصال مجدد و replay بدون race condition

کلاینت باید آخرین `Sequence` پردازش‌شده را به‌صورت durable در local storage خود نگه دارد. هنگام reconnect:

1. کلاینت با `lastSequence` به Hub متصل می‌شود.
2. سرور subscriber را ثبت می‌کند و یک `watermark` می‌گیرد.
3. eventهای `lastSequence < Sequence <= watermark` را replay می‌کند.
4. eventهای جدیدتر از watermark را به live stream می‌فرستد.
5. پس از هر event، کلاینت آن را با `EventId` idempotent پردازش و cursor را ذخیره می‌کند.

ترتیب ثبت subscriber قبل از replay ضروری است؛ در غیر این صورت eventی که بین query و subscribe ایجاد شود از دست می‌رود.

پروتکل پاسخ باید gap را نیز اعلام کند:

```json
{
  "type": "ReplayStarted",
  "requestedAfter": 1200,
  "availableFrom": 1201,
  "watermark": 1320,
  "retentionGap": false
}
```

اگر cursor کلاینت قدیمی‌تر از retention باشد:

```json
{
  "type": "CursorExpired",
  "requestedAfter": 10,
  "availableFrom": 9000,
  "requiresResync": true
}
```

در این حالت کلاینت باید resync کند و سرویس باید این وضعیت را در log/metrics ثبت کند. این سازوکار مشخص می‌کند که آیا رخدادی در فاصلهٔ قطع اتصال خارج از retention مانده است یا خیر.

## 6. Triggerها

Trigger یک تعریف پایدار در تنظیمات سرویس است:

```text
TriggerDefinition
 ├── Id
 ├── Name
 ├── Enabled
 ├── CameraIds یا TaskIds
 ├── EventKinds
 ├── Label / IdentityId / PlateText filter
 ├── MinimumConfidence
 ├── Cooldown
 ├── Condition expression محدود و validate‌شده
 └── Actions
```

Actionهای نسخهٔ اول:

- `LiveEvent`: ارسال به کلاینت‌های متصل
- `Webhook`: ارسال HTTP قابل retry
- `Store`: ثبت رخداد، که همیشه فعال است

Trigger باید در خود سرویس اجرا شود، نه در UI. بنابراین خاموش بودن UI اثری روی اجرای آن ندارد.

## 7. Webhook قابل‌اعتماد

برای هر ارسال Webhook این موارد ذخیره شود:

- EventId
- TriggerId
- endpoint
- attempt count
- status
- last error
- next attempt time
- delivered time

ارسال باید at-least-once باشد. تضمین exactly-once در سمت شبکه ممکن نیست؛ بنابراین `EventId` به‌عنوان idempotency key در header و body ارسال می‌شود.

retry با backoff محدود انجام شود و بعد از سقف تلاش به dead-letter برسد. UI باید بتواند dead-letterها را مشاهده و retry دستی کند.

## 8. API مصرف event

دو روش لازم است:

### Replay/Query

برای همگام‌سازی و گزارش:

```text
GET /api/v1/events?afterSequence=1200&limit=500
GET /api/v1/events/{eventId}
GET /api/v1/events/{eventId}/image
GET /api/v1/events/gaps
```

### Live Hub

برای مصرف لحظه‌ای و reconnect protocol. پیام‌های `ReplayStarted`، `DetectionEvent`، `ReplayCompleted` و `CursorExpired` بخشی از قرارداد Hub هستند.

## 9. retention

تنظیمات retention باید برای event metadata، crop image و outbox جدا باشد. پاک‌سازی فقط eventهایی را حذف کند که:

- از زمان retention عبور کرده‌اند؛
- در وضعیت pending webhook نیستند؛
- backup یا export موردنیازشان تکمیل شده است.

پاک‌سازی باید batchای و کم‌فشار باشد و روی inference lock نگیرد.

## 10. قرارداد کامل تشخیص و تصاویر

مدل کامل payload و artifactهای تصویری در [07-DETECTION-EVENT-CONTRACT.md](07-DETECTION-EVENT-CONTRACT.md) تعریف شده است. آن سند سه سناریوی رسمی `PlateOnly`، `FaceRecognition` و `PlateFaceAssociation` را پوشش می‌دهد و برای هر رخداد فریم کامل، ROI، crop تشخیص و جزئیات شناسایی را مشخص می‌کند.
