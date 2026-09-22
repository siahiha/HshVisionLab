# تنظیمات، دیتابیس و مالکیت داده

## 1. اصل مالکیت

سرویس تنها processای است که configuration فعال و Face Database را برای runtime باز نگه می‌دارد. `HshVisionLab` در حالت مدیریت از API استفاده می‌کند و هم‌زمان فایل‌های سرویس را با `File.ReadAllText` یا یک `FaceDatabase` دوم باز نمی‌کند.

این تصمیم به‌خصوص برای Face Database مهم است، چون implementation فعلی علاوه بر SQLite snapshotهای in-memory دارد و چند process مستقل می‌توانند viewهای ناسازگار داشته باشند.

## 2. data root

مسیر پیشنهادی:

```text
%ProgramData%\HshVision\DetectionService\
 ├── config\
 │    ├── settings.json
 │    ├── service-settings.json
 │    └── settings.backup.*.json
 ├── database\
 │    ├── face-database.db
 │    ├── events.db
 │    └── backups\
 ├── models\
 │    ├── Face\
 │    └── Plate\
 ├── license\
 │    └── license.hshlic
 ├── media\
 │    ├── event-crops\
 │    └── exports\
 └── logs\
```

مسیر باید قابل override برای development و deploymentهای خاص باشد، ولی `AppContext.BaseDirectory` نباید تنها منبع مسیر در service باشد.

## 3. تقسیم فایل‌ها

### `settings.json`

همان schema فعلی `AppSettings` و `CameraSettings` را نگه می‌دارد تا migration و compatibility ساده بماند:

- دوربین‌ها
- ROIها
- processing itemها
- گزینه‌های Plate/Face
- motion و capture

### `service-settings.json`

در implementation فعلی علاوه بر موارد بالا، بخش `association` شامل `maxWindowMs` و `requireSameRoi` است. این دو مقدار policy مشترک ارتباط پلاک/چهره را تعیین می‌کنند؛ فیلترهای client در `ClientSubscription` نگه‌داری می‌شوند و داخل تنظیمات دوربین ذخیره نمی‌شوند.

تنظیمات خاص سرویس را نگه می‌دارد:

- HTTP/HTTPS
- `http.listenUrls`: آدرس‌های bind سرویس؛ برای دسترسی از ماشین دیگر باید آدرس شبکه یا `0.0.0.0` تنظیم شود.
- `http.serveUi`: به‌صورت پیش‌فرض `false`؛ در این حالت UI مستقل host می‌شود و سرویس فقط API، SignalR و stream را ارائه می‌کند.
- `http.corsOrigins`: فهرست دقیق originهای UI مستقل، برای fetch و SignalR.
- authentication
- WebRTC/ICE
- stream profiles
- event retention
- triggerها
- webhookها
- logging و metrics

تغییر `serveUi` و `corsOrigins` در زمان start سرویس خوانده می‌شود و پس از ذخیره‌سازی
نیازمند restart سرویس است. برای حالت embedded علاوه بر `serveUi=true` باید build
سرویس با `-p:EmbedUi=true` انجام شود.

### `face-database.db`

همان schema فعلی `People` و `FaceSamples` باقی می‌ماند. schema جدیدی برای جایگزینی Face Database فعلی لازم نیست؛ فقط ownership به سرویس منتقل می‌شود.

### `events.db`

برای رخداد و عملیات سرویس جداول جدا داشته باشد:

```text
DetectionEvents
EventArtifacts
EventOutbox
WebhookDeliveries
EventCursorsAudit
ServiceOperations
VehiclePersonAssociations
```

دیتابیس رخداد نباید در transactionهای inference یا Face Database قفل ایجاد کند.

## 4. revision و atomic write

هر دو فایل JSON یک envelope یا metadata داخلی برای revision داشته باشند:

```json
{
  "schemaVersion": 1,
  "revision": 42,
  "updatedAtUtc": "2026-09-20T10:00:00Z",
  "data": { }
}
```

اگر حفظ root فعلی `AppSettings` برای compatibility ضروری باشد، revision در `service-settings.json` یا header metadata نگه داشته شود؛ اما API همچنان revision را به‌صورت رسمی expose کند.

نوشتن:

1. JSON جدید در فایل `.tmp` نوشته شود.
2. JSON دوباره deserialize و validate شود.
3. فایل قبلی با timestamp backup شود.
4. replace اتمیک انجام شود.
5. revision افزایش یابد.

در صورت خراب بودن فایل جدید، سرویس باید آخرین backup معتبر را امتحان کند و وضعیت degraded اعلام کند.

## 5. migration از HshVisionLab فعلی

روند امن انتقال:

1. UI فایل‌های محلی را صرفاً برای Import انتخاب می‌کند.
2. سرویس schema و model referenceها را validate می‌کند.
3. `settings.json` در config root سرویس ذخیره می‌شود.
4. Face Database از طریق backup/checkpoint یا import کنترل‌شده منتقل می‌شود.
5. سرویس database را باز می‌کند و تعداد People/Samples را گزارش می‌دهد.
6. UI پس از موفقیت به Service Mode تغییر می‌کند.

کپی مستقیم SQLite در حالی که process دیگری WAL فعال دارد مجاز نباشد؛ برای import باید source database بسته یا با مکانیزم backup امن export شود.

## 6. تنظیمات API و تغییر هم‌زمان

API باید روی همهٔ تغییرات write این موارد را برگرداند:

- revision جدید
- زمان apply
- operationId در صورت asynchronous بودن
- وضعیت runtime متاثر

اگر دو UI هم‌زمان تنظیمات را ویرایش کنند:

- UI اول revision 10 را به 11 تبدیل می‌کند.
- UI دوم با revision 10 درخواست می‌فرستد.
- سرویس `409 Conflict` می‌دهد.
- UI دوم ابتدا diff/reload می‌کند و سپس patch جدید می‌فرستد.

merge خودکار JSON در سرویس انجام نشود؛ چون برای ROI و task می‌تواند نتیجهٔ غیرقابل‌پیش‌بینی بسازد.

## 7. Face Database API و consistency

تمام عملیات Face Database از یک service-owned instance انجام شوند:

- rename
- delete person
- add sample
- move sample
- similarity search
- unknown management
- backup/restore

برای add sample، سرویس باید image را بگیرد و detection/alignment/embedding را خودش انجام دهد. این کار مانع اختلاف نسخهٔ مدل و preprocessing بین UI و service می‌شود.

restore باید عملیاتی جدا باشد:

1. ورود به maintenance mode برای Face recognition
2. validate backup
3. ساخت temporary database
4. اجرای migration/schema check
5. swap اتمیک
6. reload FaceModule/pipelines
7. خروج از maintenance mode

## 8. retention و فضای دیسک

metadata event، crop، backup و log retention مستقل باشند. فضای آزاد قبل از ذخیرهٔ crop و backup کنترل شود. پاک‌سازی در worker کم‌اولویت اجرا شود و هیچ lock سراسری روی inference نگیرد.

## 9. license و مدل

license و مدل‌ها بخشی از deployment هستند، نه داده‌ای که UI در هر لحظه تغییر دهد. API فقط موارد زیر را انجام دهد:

- وضعیت license را گزارش کند.
- featureهای مجاز را اعلام کند.
- مدل‌های موجود و معتبر را فهرست کند.
- تنظیمات را در برابر مدل موجود validate کند.

رمز یا credential داخل JSON عمومی API برگردانده نشود. در صورت نیاز به credential، DPAPI/Windows Credential Manager یا secret store نصب‌کننده استفاده شود.
