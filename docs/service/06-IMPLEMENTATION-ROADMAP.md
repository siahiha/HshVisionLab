# نقشهٔ پیاده‌سازی و معیار پذیرش

این سند ترتیب پیشنهادی توسعه را مشخص می‌کند. هیچ مرحله‌ای به‌معنای اجرای تغییر در این مرحله نیست.

## مرحلهٔ صفر: تثبیت قراردادها

خروجی:

- تأیید data root
- تأیید مالکیت service روی settings و Face Database
- تأیید مدل `Processing Item = Detection Task`
- تأیید cursor/replay
- تأیید API versioning و authentication
- انتخاب نام پروژه و service name

## مرحلهٔ یک: host و runtime

خروجی:

- پروژهٔ `HshDetectionService`
- اجرای Console برای توسعه
- اجرای Windows Service برای production
- startup validation
- logging، health و graceful shutdown
- اجرای چند `CameraRuntime` مستقل
- ثبت Plate/Face module و license

معیار پذیرش:

- توقف یا خطای یک دوربین روی دوربین‌های دیگر اثر نگذارد.
- service بدون UI اجرا شود.
- restart سرویس تنظیمات و database را سالم بازیابی کند.

## مرحلهٔ دو: frame/overlay compositor

خروجی:

- `LatestFrameStore`
- `OverlayStateStore`
- compositor با نرخ مستقل
- snapshot API
- تست فشار برای capture کند، inference کند و encoder کند

معیار پذیرش:

- FPS تصویر تابع inference نباشد.
- ROI و آخرین detection روی آخرین frame رسم شوند.
- memory با drop کردن فریم‌های قدیمی محدود بماند.

## مرحلهٔ سه: Event Store و replay

خروجی:

- `events.db`
- sequence و event id
- event normalizer
- قرارداد کامل `PlateOnly`، `FaceRecognition` و `PlateFaceAssociation`
- ساخت و نگهداری artifactهای فریم، ROI، detection و face/plate crop
- transaction event/outbox
- query API
- reconnect protocol با watermark
- `CursorExpired` و gap reporting

معیار پذیرش:

- UI در حالت قطع هیچ eventی را از دست ندهد، مشروط به retention.
- reconnect بین replay و live eventی را گم نکند.
- دریافت تکراری با EventId قابل idempotent processing باشد.

## مرحلهٔ چهار: API مدیریت

خروجی:

- API تنظیمات عمومی
- CRUD دوربین
- CRUD ROI با `RoiId` پایدار
- CRUD task
- module/model catalog
- service operations
- revision/ETag و conflict handling

معیار پذیرش:

- تمام تغییرات از API validate شوند.
- apply فقط runtime متاثر را rebuild کند.
- دو client هم‌زمان باعث overwrite خاموش نشوند.

## مرحلهٔ پنج: Face Database API

خروجی:

- people/sample CRUD
- enrollment از image
- move/delete/rename
- similarity search
- backup/restore maintenance flow

معیار پذیرش:

- embedding در سرویس تولید شود.
- UI database را مستقیم باز نکند.
- restore خراب یا ناسازگار database فعال را overwrite نکند.

## مرحلهٔ شش: SignalR و WebRTC PoC

خروجی:

- detection hub
- signaling hub
- یک stream annotated برای یک دوربین
- یک web client آزمایشی
- snapshot و WebRTC از compositor مشترک

معیار پذیرش:

- در inference کند، تصویر همچنان نرم بماند.
- چند subscriber باعث چند inference نشود.
- disconnect یک subscriber روی دیگران اثر نگذارد.

## مرحلهٔ هفت: Trigger و Webhook

خروجی:

- trigger CRUD
- cooldown/dedup policy
- webhook outbox
- retry/backoff
- dead-letter و retry دستی

معیار پذیرش:

- Trigger در نبود UI همچنان اجرا شود.
- delivery شکست‌خورده قابل مشاهده و retry باشد.
- consumer بتواند EventId را idempotent کند.

## مرحلهٔ هفت‌ب: association پلاک و چهره

خروجی:

- domain و storage مستقل `VehiclePersonAssociation`
- ارتباط plate component و face component در یک source frame یا time window
- وضعیت‌های `MatchedPreviousRecord`، `NewAssociation`، `Conflict` و `NoPreviousRecord`
- API مدیریت association و evidence eventها

معیار پذیرش:

- event ارتباطی، جزئیات کامل هر دو detection را داشته باشد.
- reference رکورد قبلی و eventهای evidence قابل بازیابی باشد.
- تشخیص پلاک تنها و چهرهٔ ناشناس بدون association معتبر، به‌اشتباه match اعلام نشود.

## مرحلهٔ هشت: تبدیل HshVisionLab به Service Manager

خروجی:

- انتخاب Local Mode یا Service Mode
- اتصال به API
- import اولیهٔ settings و Face Database
- مدیریت دوربین، task، Face Database و trigger از UI
- نمایش رخدادهای replayشده و live

در Service Mode، UI نباید فایل‌های اصلی سرویس را با instance جداگانهٔ `FaceDatabase` باز کند.

## تست‌های ضروری پیش از production

- قطع و وصل RTSP
- توقف ناگهانی سرویس در زمان inference
- قطع UI برای چند دقیقه و replay رخدادها
- تولید event هم‌زمان با reconnect
- پر شدن retention و expired cursor
- کند شدن encoder
- اتصال هم‌زمان چند WebRTC client
- تغییر تنظیمات هم‌زمان از دو UI
- restore نامعتبر Face Database
- عدم دسترسی service account به مدل یا data root
- عدم مجوز license برای Face یا Plate
