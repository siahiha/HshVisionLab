# معماری کلان سرویس تشخیص

## 1. مسئولیت سرویس

> **وضعیت فعلی:** `EventStore`، artifact store، SignalR replay و trigger evaluation فعال هستند. نمودارهای دارای `Outbox`/Webhook در این سند معماری تکمیلی‌اند؛ در نسخهٔ فعلی event پس از ثبت در SQLite از طریق SignalR منتشر می‌شود و webhook dispatcher مستقل پیاده‌سازی نشده است.

`HshDetectionService` یک Windows Service برای اجرای دائمی و headless موتور تشخیص است. این پروژه نباید به WinForms یا هیچ UI خاصی وابسته باشد.

سرویس مسئول موارد زیر است:

- اتصال و reconnect مستقل برای دوربین‌ها
- اجرای pipelineهای Plate و Face فعلی
- زمان‌بندی و محدودکردن نرخ inference
- نگهداری آخرین فریم و آخرین وضعیت Drawing
- تولید و ذخیرهٔ رخدادهای معتبر
- اجرای Triggerها و تحویل قابل‌اعتماد رخداد
- API مدیریت تنظیمات و Face Database
- انتشار stream تصویری از طریق WebRTC
- احراز هویت، health check، logging و metrics

سرویس مسئول نمایش فرم، ویرایش مستقیم فایل‌ها یا نگهداری state مربوط به UI نیست.

## 2. اتصال به موتور فعلی

وابستگی پروژهٔ سرویس:

```text
HshDetectionService
 ├── HshDetectionEngin
 ├── HshDetectionEngin.Abstractions
 ├── HshDetectionEngin.Face
 ├── HshDetectionEngin.Plate
 └── HshDetectionEngin.Licensing
```

سرویس در startup:

1. مسیرهای داده را resolve می‌کند.
2. تنظیمات و license را validate می‌کند.
3. `FaceDatabase` را یک‌بار باز می‌کند.
4. `PlateModule` و `FaceModule` را در `ProcessingRegistry` ثبت می‌کند.
5. برای هر دوربین یک `CameraRuntime` می‌سازد.
6. از رویدادهای runtime یک adapter سرویس ایجاد می‌کند.
7. دوربین‌ها را مستقل start می‌کند.

در طراحی اولیه، `CameraSettings.Rois[].Processing[]` همان فهرست Detection Taskها است. شناسهٔ `CameraProcessingSettings.Id` شناسهٔ task خواهد بود و metadata فعلی `ProcessingItemId` و `RoiName` به event منتقل می‌شود.

## 3. جریان اصلی داده

```text
                         ┌─────────────────────────────┐
                         │       Service API / UI       │
                         └──────────────┬──────────────┘
                                        │ configuration/control
┌──────────────┐       ┌────────────────▼────────────────┐
│ Frame Source │──────▶│ Camera Runtime Manager           │
└──────┬───────┘       │ capture / reconnect / lifecycle  │
       │               └──────────────┬──────────────────┘
       │ latest frame                 │ scheduled inference
       ▼                             ▼
┌──────────────┐       ┌───────────────────────────────┐
│LatestFrame   │       │ Detection Pipelines            │
│Store         │       │ Plate / Face / future modules │
└──────┬───────┘       └──────────────┬────────────────┘
       │                              │ detections/overlays
       ▼                              ▼
┌──────────────┐       ┌───────────────────────────────┐
│Compositor    │◀──────▶│ Overlay State Store            │
│latest frame  │       │ latest completed result        │
│+latest state │       └──────────────┬────────────────┘
└──────┬───────┘                      │ accepted event
       │                              ▼
       │                 ┌─────────────────────────────┐
       │                 │ Event Normalizer + Trigger  │
       │                 └──────────────┬──────────────┘
       │                                ▼
       │                 ┌─────────────────────────────┐
       │                 │ events.db / Outbox          │
       │                 └───────┬───────────┬─────────┘
       │                         │           │
       ▼                         ▼           ▼
┌──────────────┐        ┌─────────────┐ ┌──────────────┐
│ WebRTC video │        │SignalR      │ │Webhook/Replay│
│ subscribers  │        │live clients │ │delivery      │
└──────────────┘        └─────────────┘ └──────────────┘
```

## 4. واحدهای اجرایی

### `DetectionHost`

هماهنگ‌کنندهٔ lifecycle سرویس است. تغییر تنظیمات را به شکل desired state دریافت می‌کند، تفاوت را محاسبه می‌کند و فقط دوربین‌ها یا taskهای متاثر را restart/rebuild می‌کند.

### `CameraRuntimeManager`

برای هر دوربین یک runtime مستقل نگه می‌دارد. خطای یک دوربین نباید باعث توقف دوربین‌های دیگر یا API شود.

### `DetectionScheduler`

فریم‌ها را با توجه به `MaxFps`، Motion Gate و وضعیت task به pipeline می‌دهد. scheduler نباید فریم‌ها را برای مدت طولانی در صف نگه دارد؛ برای تصویر زنده سیاست اصلی `latest frame wins` است.

`CameraPipelineCoordinator` graph فعال را بدون نگه‌داشتن قفل در طول inference snapshot می‌کند. بنابراین endpointهای وضعیت دوربین برای خواندن تعداد pipeline یا وضعیت runtime منتظر پایان ONNX نمی‌مانند. graph قبلی هنگام تغییر تنظیمات تا پایان leaseهای فعال زنده می‌ماند و سپس sessionهای آن آزاد می‌شوند.

### `OverlayStateStore`

نتیجهٔ آخرین inference کامل‌شده را به‌شکل immutable نگه می‌دارد. این store شامل ROIهای ثابت، detectionهای پویا، زمان انقضا، sequence فریم مبنا و version وضعیت است.

### `EventNormalizer`

نتایج خام pipeline را به eventهای معنی‌دار تبدیل می‌کند. Event خام هر inference الزاماً event قابل ارسال نیست؛ cooldown، identity، plate، track و trigger policy در این لایه اعمال می‌شوند.

### `EventStore`

event را قبل از broadcast ذخیره می‌کند و cursor ترتیبی می‌سازد. این بخش مستقل از اتصال UI کار می‌کند.

ورودی ذخیره‌سازی یک Channel محدود دارد (`Service.Runtime.MaxEventQueueLength`). اگر مصرف‌کننده عقب بماند، رخداد جدید با آزادسازی artifactهایش حذف می‌شود و تعداد حذف‌شده‌ها در `GET /api/v1/service/status` با نام `droppedEventCount` قابل مشاهده است.

### UI مستقل از سرویس

`DetectionManagerUi` یک پروژهٔ مستقل Vite/React است و سرویس تشخیص هیچ فایل UI
یا route fallback مربوط به آن را host نمی‌کند. سرویس فقط API، SignalR، stream و
inference را ارائه می‌کند و UI با `VITE_HSH_API_BASE_URL` به آن وصل می‌شود.
`http.corsOrigins` برای fetch، snapshot، WHEP و SignalR originهای UI را کنترل
می‌کند.

### `StreamSessionManager`

برای هر دوربین یک منبع تصویری composited دارد و چند کلاینت WebRTC را به همان منبع متصل می‌کند. اتصال یک کلاینت نباید باعث اجرای inference یا compositing جداگانه برای کلاینت‌های دیگر شود.

## 5. رفتار تغییر تنظیمات

تغییرات از API با `revision` و optimistic concurrency دریافت می‌شوند:

1. API تنظیمات جدید را deserialize و validate می‌کند.
2. اگر revision کلاینت قدیمی باشد، پاسخ `409 Conflict` برمی‌گردد.
3. تنظیمات جدید در فایل موقت نوشته و atomic replace می‌شوند.
4. backup نسخهٔ قبلی نگه داشته می‌شود.
5. runtime متاثر به‌شکل کنترل‌شده stop/rebuild/start می‌شود.
6. تا پایان rebuild، وضعیت جدید `Applying` اعلام می‌شود.

تغییرات مربوط به یک دوربین نباید کل سرویس را restart کند؛ مگر تغییر در مدل‌های shared یا مسیر دادهٔ اصلی.

## 6. اجرای Windows Service

پروژه با Worker/Generic Host ساخته می‌شود و یک HTTP host نیز در همان process اجرا می‌شود. حالت Console برای توسعه و حالت Windows Service برای production هر دو باید فعال باشند.

تنظیمات service account، دسترسی به `ProgramData`، دسترسی به مدل‌ها و دسترسی شبکهٔ RTSP بخشی از deployment است. سرویس نباید به desktop session یا UI کاربر وابسته باشد.
