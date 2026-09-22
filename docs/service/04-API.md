# قرارداد API سرویس

## 1. اصول API

- همهٔ endpointها زیر `/api/v1` باشند.
- زمان‌ها UTC و ISO-8601 باشند.
- شناسه‌ها GUID یا string پایدار باشند.
- پاسخ خطا ساختار ثابت داشته باشد.
- تغییرات از `revision` و `ETag` استفاده کنند.
- API تنظیمات را validate کند و هیچ JSON ناشناخته‌ای را بی‌صدا حذف نکند.
- عملیات طولانی مانند import، restore و reload با `operationId` انجام شوند.

نمونهٔ خطا:

```json
{
  "code": "configuration_conflict",
  "message": "Configuration revision is stale.",
  "details": {
    "expectedRevision": 8,
    "actualRevision": 9
  },
  "traceId": "..."
}
```

## 2. Health و وضعیت سرویس

```text
GET  /health/live
GET  /health/ready
GET  /api/v1/service/status
GET  /api/v1/service/capabilities
POST /api/v1/service/reload
POST /api/v1/service/validate-configuration
```

`ready` فقط وقتی موفق باشد که configuration، license، مدل‌های لازم و Face Database آماده باشند. status باید وضعیت هر camera، task، pipeline، stream و queue را جداگانه نشان دهد.

## 3. تنظیمات عمومی سرویس

```text
GET  /api/v1/settings
PUT  /api/v1/settings
PATCH /api/v1/settings
GET  /api/v1/settings/revisions
GET  /api/v1/settings/export
POST /api/v1/settings/import
POST /api/v1/settings/validate
```

این بخش شامل موارد زیر است:

- مسیر data root
- HTTP/HTTPS و پورت‌ها
- authentication policy
- WebRTC و ICE serverها
- profileهای ویدئو
- retention
- logging/metrics
- تنظیمات event و webhook

تنظیمات دوربین و task بهتر است endpoint مجزای خود را داشته باشند تا UI مجبور به ارسال کل فایل بزرگ نباشد.

## 4. دوربین‌ها

```text
GET    /api/v1/cameras
POST   /api/v1/cameras
GET    /api/v1/cameras/{cameraId}
PUT    /api/v1/cameras/{cameraId}
PATCH  /api/v1/cameras/{cameraId}
DELETE /api/v1/cameras/{cameraId}

POST /api/v1/cameras/{cameraId}/start
POST /api/v1/cameras/{cameraId}/stop
POST /api/v1/cameras/{cameraId}/restart
GET  /api/v1/cameras/{cameraId}/status
GET  /api/v1/cameras/{cameraId}/statistics
```

`PUT/PATCH` باید قبل از apply، source، backend، model reference و processing schema را validate کند.

## 5. ROIها و Detection Taskها

```text
GET    /api/v1/cameras/{cameraId}/rois
POST   /api/v1/cameras/{cameraId}/rois
GET    /api/v1/cameras/{cameraId}/rois/{roiId}
PATCH  /api/v1/cameras/{cameraId}/rois/{roiId}
DELETE /api/v1/cameras/{cameraId}/rois/{roiId}

GET    /api/v1/cameras/{cameraId}/tasks
POST   /api/v1/cameras/{cameraId}/rois/{roiId}/tasks
GET    /api/v1/tasks/{taskId}
PATCH  /api/v1/tasks/{taskId}
DELETE /api/v1/tasks/{taskId}
POST   /api/v1/tasks/{taskId}/enable
POST   /api/v1/tasks/{taskId}/disable
```

هر task شامل این موارد باشد:

```json
{
  "id": "stable-task-id",
  "type": "Face",
  "name": "Face recognition",
  "enabled": true,
  "maxFps": 8,
  "threads": 1,
  "options": { }
}
```

`options` با `ProcessingModuleDescriptor.OptionsType` validate می‌شود. API نباید به typeهای concrete ماژول در UI وابسته باشد.

### نیاز به شناسهٔ پایدار ROI

در مدل فعلی `NamedRoi` نام دارد اما شناسهٔ مستقل ندارد. برای API granular، rename کردن ROI نباید resource ID را عوض کند. قبل از فعال‌کردن API جزئی ROI، باید `RoiId` پایدار به مدل اضافه و برای فایل‌های قدیمی هنگام migration تولید شود. نام ROI فقط display name باقی بماند.

## 6. ماژول‌ها و مدل‌ها

```text
GET /api/v1/processing/modules
GET /api/v1/models
GET /api/v1/models/{capability}
```

این endpointها module type، display name، options schema، availability/license status و مدل‌های قابل انتخاب را برمی‌گردانند.

آپلود مدل در نسخهٔ اول از API انجام نشود؛ مدل‌ها بخشی از deployment هستند و API فقط آن‌ها را فهرست و validate می‌کند.

## 7. Face Database

سرویس تنها مالک `FaceDatabase` است. API باید به جای دادن embedding خام به UI، enrollment را در خود سرویس انجام دهد تا مدل، alignment و threshold یکسان بمانند.

```text
GET    /api/v1/face/people
POST   /api/v1/face/people
GET    /api/v1/face/people/{personId}
PATCH  /api/v1/face/people/{personId}
DELETE /api/v1/face/people/{personId}

GET    /api/v1/face/people/{personId}/samples
POST   /api/v1/face/people/{personId}/samples
GET    /api/v1/face/samples/{sampleId}
GET    /api/v1/face/samples/{sampleId}/image
DELETE /api/v1/face/samples/{sampleId}
POST   /api/v1/face/samples/{sampleId}/move

POST   /api/v1/face/enrollment/preview
POST   /api/v1/face/people/{personId}/samples/import
POST   /api/v1/face/similarity/search
GET    /api/v1/face/database/health
POST   /api/v1/face/database/backup
POST   /api/v1/face/database/restore
```

`POST /samples` باید multipart image بگیرد، یک چهرهٔ معتبر را detect و align کند، embedding را در سرویس تولید و سپس با `FaceDatabase.RegisterSample` ذخیره کند. embedding ارسالی از UI فقط برای migration کنترل‌شده پذیرفته شود.

## 8. Trigger و Webhook

```text
GET    /api/v1/triggers
POST   /api/v1/triggers
GET    /api/v1/triggers/{triggerId}
PATCH  /api/v1/triggers/{triggerId}
DELETE /api/v1/triggers/{triggerId}
POST   /api/v1/triggers/{triggerId}/test

GET    /api/v1/webhooks
POST   /api/v1/webhooks
PATCH  /api/v1/webhooks/{webhookId}
DELETE /api/v1/webhooks/{webhookId}
GET    /api/v1/webhooks/dead-letter
POST   /api/v1/webhooks/dead-letter/{deliveryId}/retry
```

## 9. Event Query و replay

```text
GET /api/v1/events
GET /api/v1/events/{eventId}
GET /api/v1/events/{eventId}/image
GET /api/v1/events/gaps
GET /api/v1/events/export
GET /api/v1/events/{eventId}/artifacts
GET /api/v1/events/{eventId}/artifacts/{artifactId}
```

پارامترهای query:

- `afterSequence`
- `beforeSequence`
- `fromUtc` و `toUtc`
- `cameraId`
- `taskId`
- `kind`
- `label`
- `minimumConfidence`
- `pageSize`

برای مصرف زنده، Hub فعال رخداد:

در implementation فعلی endpointهای `/api/v1/events/{eventId}/image`، `/api/v1/events/gaps` و `/api/v1/events/export` وجود ندارند. Queryهای واقعی شامل `afterSequence`، `limit`، `cameraId`، `scenario`، `fromUtc`، `toUtc` و فیلترهای subscription شامل `clientMode`، `faceRequired`، `plateRequired`، `includeUnknownFace`، `windowMs`، `clientCameraIds` و `clientRoiIds` هستند.

```text
/hubs/detections
```

قرارداد کامل event، اطلاعات characterهای پلاک، وضعیت ناشناس/شناخته‌شدهٔ چهره، artifactهای فریم و ROI و association پلاک/چهره در [07-DETECTION-EVENT-CONTRACT.md](07-DETECTION-EVENT-CONTRACT.md) آمده است.

## 10. Stream و WebRTC

```text
GET  /api/v1/streams/{cameraId}/snapshot
GET  /api/v1/streams/{cameraId}/overlay
POST /api/v1/streams/{cameraId}/webrtc/offer
POST/PATCH/DELETE /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
```

برای دوربین `MediaMTX`، endpointهای WHEP خروجی خام و کم‌تاخیر path را به مرورگر
می‌دهند. endpoint `overlay` خروجی ویدئویی نیست و فقط state لازم برای رسم سمت
کلاینت را برمی‌گرداند:

```json
{
  "width": 1920,
  "height": 1080,
  "rois": [
    { "id": "roi-id", "name": "ROI 1", "enabled": true,
      "points": [{ "x": 0.1, "y": 0.2 }, { "x": 0.9, "y": 0.2 }] }
  ],
  "detections": [
    {
      "kind": "Face",
      "label": "Unknown #12",
      "text": null,
      "confidence": 0.91,
      "bounds": { "x": 100, "y": 120, "width": 240, "height": 300 },
      "trackId": 12,
      "accepted": true
    }
  ],
  "processingOverlays": []
}
```

`bounds` و نقاط primitiveها در فضای پیکسلی فریم اصلی هستند و نقاط ROI در فضای
نرمال‌شدهٔ `0..1`. Overlayهای پویا حدود 2.5 ثانیه TTL دارند؛ ROI ثابت expiry
ندارد. کلاینت باید این داده را با refresh کوتاه بخواند و روی WHEP خام رسم کند.

offer/answer اختصاصی و `WebRtcGateway` برای clientهای legacy که خروجی
کامپوزیت‌شده می‌خواهند باقی می‌ماند؛ endpoint POST برای clientهای ساده و تست
نیز حفظ شده است. برای client فعلی MediaMTX، نباید به `/webrtc/offer` متصل شد.

## 11. ارتباط پلاک و چهره و subscription کلاینت

ارتباط پلاک و چهره در همان event canonical انجام می‌شود و در نسخهٔ فعلی endpoint جداگانهٔ association وجود ندارد. پنجرهٔ اتصال از `service.Association.MaxWindowMs` (پیش‌فرض 1500ms) و `RequireSameRoi` کنترل می‌شود.


در نسخهٔ فعلی، policy هر اتصال با متد SignalR به نام `Subscribe(lastSequence, subscription)` تعیین می‌شود و تنظیمات inference دوربین را تغییر نمی‌دهد. فیلدهای اصلی subscription عبارت‌اند از `mode` (`All`، `Plate`، `KnownFace`)، `cameraIds`، `roiIds`، `faceRequired`، `plateRequired`، `includeFace`، `includePlate`، `includeUnknownFace`، `includeArtifacts`، `windowMs` و `cooldownSeconds`. اگر `faceRequired` یا `plateRequired` برابر false باشد، component اختیاری است و در صورت شناسایی به همان client ارسال می‌شود.

endpointهای `/api/v1/associations` در نسخهٔ فعلی پیاده‌سازی نشده‌اند و نباید توسط client فراخوانی شوند؛ client باید eventهای `PlateOnly`، `FaceRecognition` یا `PlateFaceAssociation` را از Event API/SignalR مصرف کند.

## 12. امنیت

- endpoint مدیریتی loopback با Windows Integrated Authentication یا token نصب‌شدهٔ محلی محافظت شود.
- دسترسی remote فقط با HTTPS و bearer token/role انجام شود.
- scopeهای جداگانه تعریف شود: `service.admin`, `configuration.write`, `face.write`, `events.read`, `stream.read`.
- رمز RTSP در response عمومی برگردانده نشود.
- path مدل، export و restore به root مجاز محدود شود.
- snapshot، crop و event image نیز authorization داشته باشند.
