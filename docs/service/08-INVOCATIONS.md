# فراخوانی‌های خروجی

## 1. هدف

بخش «فراخوانی‌ها» برای ارسال رخدادهای ثبت‌شدهٔ تشخیص به مقصدهای بیرونی است. این قابلیت فقط Web API نیست و دو نوع مقصد فعلی را پوشش می‌دهد:

- `Web`: ارسال HTTP با متد `GET` یا `POST`
- `Sql`: اجرای دستور SQL یا Stored Procedure روی SQLite یا SQL Server

فراخوانی‌ها پس از ثبت موفق event در `events.db`، به‌صورت job پایدار ایجاد می‌شوند. بنابراین ارسال مقصد بیرونی باعث توقف مسیر تشخیص و ثبت event نمی‌شود و در صورت restart سرویس، jobهای ناتمام دوباره قابل پردازش هستند.

نام منوی UI عمداً «فراخوانی‌ها» است؛ چون مقصد می‌تواند Web API یا SQL باشد.

## 2. مدل تنظیمات

تنظیمات فراخوانی‌ها در `config/service-settings.json` و در آرایهٔ `Invocations` ذخیره می‌شوند. مدیریت آن‌ها از API زیر انجام می‌شود:

```text
GET    /api/v1/invocations
POST   /api/v1/invocations
PATCH  /api/v1/invocations/{invocationId}
DELETE /api/v1/invocations/{invocationId}
```

نمونهٔ کامل تنظیم Web:

```json
{
  "id": "eos-anpr-traffic",
  "name": "ارسال تردد پلاک به EOS",
  "enabled": true,
  "type": "Web",
  "workflowId": "",
  "stepOrder": 0,
  "dependsOnPrevious": false,
  "cameraIds": [],
  "eventTypes": ["PlateDetected"],
  "triggered": null,
  "triggerIds": [],
  "minimumConfidence": 0.7,
  "plateTextEquals": null,
  "timeoutSeconds": 15,
  "maxRetries": 3,
  "retryDelaySeconds": 30,
  "web": {
    "url": "http://127.0.0.1:3001/api/Anpr/PersistEosAnprTraffic",
    "method": "POST",
    "contentType": "application/json",
    "authenticationType": "None",
    "authenticationValue": "",
    "headers": {}
  },
  "sql": {
    "provider": "Sqlite",
    "connectionString": "",
    "commandText": "",
    "commandType": "Text"
  },
  "mappings": [
    { "target": "plate", "source": "components.plate.plateText" },
    { "target": "cameraId", "source": "source.cameraCode" },
    { "target": "PersistOn", "source": "receivedAtLocal" },
    { "target": "CarImage", "source": "image.frame" },
    { "target": "PlateImage", "source": "image.crop.plate" },
    { "target": "Confidence", "source": "components.plate.confidence" }
  ]
}
```

مقادیر `Type` فقط به `Web` یا `Sql` نرمال می‌شوند. مقدارهای نامعتبر به `Web` تبدیل می‌شوند. متدهای Web فقط `GET` و `POST` هستند؛ متدهای دیگر به `POST` نرمال می‌شوند.

## 3. فیلتر اجرای فراخوانی

هر فراخوانی می‌تواند روی همهٔ eventها یا بخشی از آن‌ها اجرا شود:

| تنظیم | رفتار |
| --- | --- |
| `enabled` | فعال یا غیرفعال بودن تعریف |
| `cameraIds` | محدودکردن به شناسهٔ داخلی دوربین؛ خالی یعنی همهٔ دوربین‌ها |
| `eventTypes` | محدودکردن به نوع رخداد، مانند `PlateDetected` یا `FaceRecognized` |
| `triggered` | `true` فقط eventهای همراه trigger، `false` فقط eventهای معمولی، `null` هر دو |
| `triggerIds` | حداقل یکی از triggerهای منطبق باید در این فهرست باشد |
| `minimumConfidence` | بیشترین confidence componentهای event باید حداقل این مقدار باشد |
| `plateTextEquals` | متن پلاک باید برابر این مقدار باشد |

فیلترها قبل از اجرای مقصد بررسی می‌شوند. eventی که با فیلتر منطبق نباشد با وضعیت `Skipped` ثبت می‌شود و به مقصد ارسال نمی‌شود.

## 4. روندهای وابسته

برای ساخت زنجیره، چند فراخوانی را با `workflowId` یکسان و `stepOrder` متفاوت تعریف کنید:

```text
workflowId = "plate-flow"   stepOrder = 1   dependsOnPrevious = false
workflowId = "plate-flow"   stepOrder = 2   dependsOnPrevious = true
workflowId = "plate-flow"   stepOrder = 3   dependsOnPrevious = true
```

وقتی `dependsOnPrevious=true` باشد، مرحلهٔ جاری فقط پس از موفقیت مرحلهٔ قبلی اجرا می‌شود. مرحلهٔ قبلی، آخرین فراخوانی فعال همان workflow با `stepOrder` کمتر است.

- اگر مرحلهٔ قبلی `Pending` یا `Running` باشد، اجرای مرحلهٔ جاری به دور بعد موکول می‌شود.
- اگر مرحلهٔ قبلی موفق نباشد، مرحلهٔ جاری `Skipped` می‌شود.
- اگر مرحلهٔ قبلی وجود نداشته باشد، مرحلهٔ جاری بدون وابستگی اجرا می‌شود.
- وابستگی برای همان event برقرار است و به موفقیت eventهای دیگر وابسته نیست.

## 5. Mapping منابع

در UI منبع با ComboBox انتخاب می‌شود و مقدار فنی آن پشت‌صحنه در `source` ذخیره می‌گردد. فیلد `target` نام فیلد مقصد است و باید دقیقاً با نام property یا پارامتر مقصد مطابقت داشته باشد. `defaultValue` فقط وقتی استفاده می‌شود که منبع مقدار `null` داشته باشد.

به‌جز رفتار مشخص تصاویر و زمان‌های local، نوع مقدار منبع بدون تبدیل عمومی حفظ
می‌شود؛ بنابراین نوع property مقصد باید با نوع مقدار event سازگار باشد. برای
مثال `confidence` عددی است، `cameraId` رشته‌ای است و `receivedAtLocal` رشته‌ای
با قالب DateTime محلی است.

مسیرهای عمومی با نقطه جدا می‌شوند و مقایسهٔ نام آن‌ها case-insensitive است. پیشوند `event.` اختیاری است؛ بنابراین `event.components.plate.plateText` و `components.plate.plateText` یک معنا دارند.

منابع اصلی:

| منبع | مقدار |
| --- | --- |
| `eventId` | شناسهٔ event |
| `sequence` | شمارهٔ ترتیبی event |
| `eventType` | نوع رخداد |
| `scenario` | سناریوی رخداد |
| `occurredAtUtc` | زمان وقوع به UTC |
| `receivedAtUtc` | زمان ثبت event به UTC |
| `occurredAtLocal` | زمان وقوع با ساعت محلی سیستم سرویس |
| `receivedAtLocal` | زمان ثبت event با ساعت محلی سیستم سرویس |
| `source.cameraId` | شناسهٔ داخلی دوربین؛ از نوع string/GUID |
| `source.cameraCode` | کد اختیاری دوربین، در صورت تعریف |
| `source.cameraName` | نام دوربین |
| `source.taskId` / `source.taskName` | پردازش ایجادکنندهٔ event |
| `source.roiId` / `source.roiName` | ناحیهٔ تشخیص |
| `source.sourceFrameSequence` | شمارهٔ فریم منبع |
| `source.frameWidth` / `source.frameHeight` | ابعاد فریم |
| `source.associationType` | نوع ارتباط، مانند `SameFrame` یا `TemporalAssociation` |
| `trigger.matched` | آیا event با trigger منطبق شده است؟ |
| `trigger.matchingTriggerIds` | شناسهٔ triggerهای منطبق |

منابع تشخیص پلاک شامل `components.plate.plateText`، `confidence`، `plateConfidence`، `plateThreshold`، `isValidIranianPlate`، `recognitionConfidence`، `recognitionModel`، `label` و `trackId` هستند.

منابع تشخیص چهره شامل `components.face.label`، `confidence`، `recognitionStatus`، `recognition.personId`، `recognition.name`، `recognition.personNumber`، `recognition.isUnknown`، `recognition.similarity` و `trackId` هستند.

برای فیلدهایی که در ComboBox وجود ندارند، گزینهٔ «مسیر سفارشی» استفاده شود.

## 6. تصاویر و تفاوت JSON و multipart

منابع تصویری فعلی:

| منبع | رفتار |
| --- | --- |
| `image.frame` یا `image.fullFrame` | تصویر فریم کامل به‌صورت باینری |
| `image.crop.plate` | کراپ پلاک به‌صورت باینری |
| `image.crop.face` | کراپ تشخیص چهره به‌صورت باینری |
| `image.faceAlignedCrop` | کراپ تراز شدهٔ چهره به‌صورت باینری |
| منبع مشابه با `.rawBase64` | رشتهٔ Base64 خام، بدون `data:` |
| منبع مشابه با `.base64` | Data URI مانند `data:image/jpeg;base64,...` |

### 6.1 مقصد JSON و DTO دارای `byte[]`

اگر endpoint مقصد مانند نمونهٔ زیر DTO دارد:

```csharp
public long? CameraId { get; set; }
public byte[] CarImage { get; set; }
public byte[] PlateImage { get; set; }
public DateTime PersistOn { get; set; }
```

باید `Content-Type` برابر `application/json` باشد. در این حالت انتخاب منبع باینری مانند `image.frame` باعث ارسال multipart نمی‌شود؛ برنامهٔ سرویس بایت‌ها را در JSON به رشتهٔ Base64 استاندارد تبدیل می‌کند. JSON ارسالی به شکل زیر است:

```json
{
  "plate": "12الف34567",
  "cameraId": 12,
  "PersistOn": "2026-09-25T14:32:10.123",
  "CarImage": "/9j/4AAQSkZJRgABAQ...",
  "PlateImage": "/9j/4AAQSkZJRgABAQ...",
  "Confidence": 0.92
}
```

برای `DateTime` بدون timezone، `receivedAtLocal` زمان ثبت event را با قالب `yyyy-MM-ddTHH:mm:ss.fff` تولید می‌کند. `occurredAtLocal` زمان وقوع تشخیص را تولید می‌کند. timezone از تنظیمات سیستم‌عاملی گرفته می‌شود که سرویس روی آن اجرا می‌شود.

### 6.2 مقصد multipart

اگر Content-Type مقصد JSON نباشد و mapping تصویری باینری داشته باشد، درخواست POST به‌صورت `multipart/form-data` ساخته می‌شود:

- فیلد تصویری به‌عنوان فایل باینری با نام target ارسال می‌شود.
- فیلدهای غیرتصویری به‌عنوان رشته ارسال می‌شوند.
- برای `GET` ارسال باینری مجاز نیست؛ برای GET از `.rawBase64` یا `.base64` استفاده کنید.

endpointهای ASP.NET Web API که پارامتر خود را با `[FromBody]` به‌صورت DTO می‌خوانند معمولاً multipart را برای `PlateDetectionRecordDto` نمی‌پذیرند. برای چنین endpointهایی باید JSON و mapping باینری بالا استفاده شود؛ استفاده از `.base64` در این حالت Data URI می‌فرستد و برای property نوع `byte[]` مناسب نیست.

### 6.3 نکتهٔ cameraId و cameraCode

در event، `source.cameraId` شناسهٔ داخلی دوربین و رشته‌ای است. اگر DTO مقصد `long? CameraId` دارد، مقدار `source.cameraId` قابل تبدیل به long نیست. در این حالت مقدار عددی را در تنظیمات اختیاری `CameraCode` دوربین قرار دهید و mapping را به شکل زیر بسازید:

```text
cameraId ← source.cameraCode
```

اگر DTO مقصد string است، می‌توان از `source.cameraId`، `source.cameraCode` یا `source.cameraName` استفاده کرد.

## 7. مقصد Web

تنظیمات Web عبارت‌اند از:

- `url`: مقصد HTTP
- `method`: فقط `GET` یا `POST`
- `contentType`: معمولاً `application/json` یا `application/x-www-form-urlencoded`
- `authenticationType`: `None`، `Bearer`، `ApiKey` یا `Basic`
- `authenticationValue`: مقدار token/key/credential
- `headers`: headerهای دلخواه

برای `Bearer` هدر `Authorization: Bearer ...`، برای `ApiKey` هدر `X-Api-Key` و برای `Basic` هدر Authorization ساخته می‌شود. headerهای تعریف‌شده نیز به درخواست اضافه می‌شوند.

در GET mappingها به query string تبدیل می‌شوند. در POST:

- اگر mapping باینری و Content-Type غیر JSON باشد: multipart
- اگر Content-Type شامل JSON باشد: JSON object با مسیرهای nested مقصد
- اگر Content-Type شامل `form` باشد: form URL encoded

اگر target شامل نقطه باشد، JSON nested ساخته می‌شود؛ مثلاً target برابر `vehicle.plate` بدنهٔ `{ "vehicle": { "plate": "..." } }` تولید می‌کند.

## 8. مقصد SQL

تنظیمات SQL عبارت‌اند از:

```json
{
  "provider": "SqlServer",
  "connectionString": "Server=.;Database=Traffic;Trusted_Connection=True;TrustServerCertificate=True",
  "commandText": "EXEC dbo.SaveDetection @plate, @PersistOn, @CarImage",
  "commandType": "Text"
}
```

`provider` می‌تواند `Sqlite` یا `SqlServer` و `commandType` می‌تواند `Text` یا `StoredProcedure` باشد. هر mapping به یک parameter تبدیل می‌شود؛ اگر target با `@` شروع نشده باشد، سرویس آن را به شکل `@target` به command اضافه می‌کند. mapping تصویری باینری برای SQL به‌صورت `byte[]` parameter ارسال می‌شود و mappingهای `.rawBase64` رشته هستند.

اجرای SQL با `ExecuteNonQueryAsync` انجام می‌شود. نتیجهٔ موفق شامل تعداد rows affected در log است و result set خوانده نمی‌شود.

## 9. صف، retry و وضعیت‌ها

در همان transaction که event در `DetectionEvents` ثبت می‌شود، برای هر فراخوانی فعال یک رکورد در `InvocationJobs` ساخته می‌شود. کلید یکتا `(EventSequence, InvocationId)` از ایجاد job تکراری جلوگیری می‌کند.

worker ارسال:

- jobهای pending را حداکثر ۵۰ مورد در هر batch می‌خواند.
- وقتی صف خالی باشد تقریباً هر ۵۰۰ میلی‌ثانیه بررسی می‌کند.
- هنگام start، jobهای `Running` را به `Pending` برمی‌گرداند.
- `timeoutSeconds` بین ۱ تا ۳۰۰ محدود می‌شود.
- `maxRetries` بین ۰ تا ۲۰ است؛ تعداد کل تلاش‌ها برابر initial attempt به‌علاوهٔ retryهاست.
- retry با backoff نمایی انجام می‌شود: `retryDelaySeconds × 2^(attempt-1)` و توان حداکثر ۵.

وضعیت‌های job و log:

```text
Pending   در صف انتظار
Running   در حال ارسال/اجرا
Succeeded مقصد با موفقیت پاسخ داده یا SQL اجرا شده است
Failed    همهٔ تلاش‌ها ناموفق بوده‌اند
Skipped   فیلتر یا وابستگی اجازهٔ اجرا نداده است
```

موفقیت Web با `2xx` تعیین می‌شود. پاسخ غیر `2xx` خطا محسوب شده و در صورت باقی‌ماندن retry دوباره تلاش می‌شود.

## 10. لاگ و retry دستی

لاگ‌ها در جدول `InvocationLogs` از `events.db` ذخیره می‌شوند و شامل نام فراخوانی، event، مرحله، شمارهٔ تلاش، مقصد، payload درخواست، کد پاسخ، body پاسخ و خطا هستند.

```text
GET  /api/v1/invocations/logs?limit=200
GET  /api/v1/invocations/logs?invocationId=...&status=Failed
POST /api/v1/invocations/jobs/{jobId}/retry
```

retry دستی job را دوباره در وضعیت `Pending` قرار می‌دهد. payload و response ممکن است طولانی باشند؛ مقدارهای لاگ‌شده برای هر فیلد تا ۱۰۰۰۰۰ کاراکتر محدود می‌شوند.

## 11. تست آخرین رکورد

برای تست از آخرین event ثبت‌شده استفاده می‌شود:

```text
POST /api/v1/invocations/{invocationId}/test
```

این endpoint:

- آخرین event را از `events.db` می‌خواند.
- همان mapping و مقصد واقعی را اجرا می‌کند.
- job جدید ایجاد نمی‌کند.
- در `InvocationLogs` چیزی ثبت نمی‌کند.
- نتیجه، target، method، status code، response و request payload را برمی‌گرداند.

اگر eventی وجود نداشته باشد پاسخ `404` با کد `no_detection_events` است. در UI، در صورت خطا Request، Payload و Response برای عیب‌یابی نمایش داده می‌شوند.

نمونهٔ پاسخ:

```json
{
  "latestEventId": "event-guid",
  "latestSequence": 18420,
  "result": {
    "success": false,
    "invocationId": "eos-anpr-traffic",
    "invocationName": "ارسال تردد پلاک به EOS",
    "method": "POST",
    "target": "http://127.0.0.1:3001/api/Anpr/PersistEosAnprTraffic",
    "responseStatusCode": 415,
    "responseBody": "...",
    "error": "HTTP 415 Unsupported Media Type",
    "requestPayload": "{ ... }"
  }
}
```

## 12. عیب‌یابی خطای MediaTypeFormatter

خطای زیر:

```text
No MediaTypeFormatter is available to read an object of type
'PlateDetectionRecordDto' from content with media type 'multipart/form-data'.
```

یعنی endpoint مقصد DTO را با JSON می‌خواهد اما درخواست multipart دریافت کرده است. برای رفع آن:

1. در تعریف فراخوانی، نوع مقصد را `Web` و متد را `POST` بگذارید.
2. `Content-Type` را `application/json` انتخاب کنید.
3. برای تصویر از `image.frame` و `image.crop.plate` استفاده کنید؛ سرویس آن‌ها را برای JSON به Base64 استاندارد تبدیل می‌کند.
4. برای `PersistOn` از `receivedAtLocal` استفاده کنید.
5. برای DTO با `long? CameraId` از `source.cameraCode` عددی استفاده کنید.
6. سرویس را restart کنید تا worker نسخهٔ جدید را اجرا کند.
7. دکمهٔ «تست آخرین رکورد» را بزنید و Request/Payload/Response را بررسی کنید.

اگر مقصد واقعاً multipart می‌خواهد، Content-Type را JSON نگذارید و mapping تصویری باینری را همان‌طور نگه دارید؛ در این حالت endpoint مقصد باید multipart را با `MultipartFormDataStreamProvider` یا مدل binding مناسب دریافت کند، نه `[FromBody] PlateDetectionRecordDto` مستقیم.
