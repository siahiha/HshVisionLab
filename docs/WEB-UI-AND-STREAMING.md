# رابط وب و مسیرهای دریافت/نمایش تصویر

در توسعه، UI مستقل `DetectionManagerUi` با Vite روی پورت `5081` اجرا می‌شود. این
UI فقط client وب `HshDetectionService` است و هیچ ارتباطی با `HshVisionLab` ندارد.
برای
دسترسی از شبکهٔ محلی باید Vite روی همهٔ interfaceها bind شود:

```powershell
cd DetectionManagerUi
npx vite --host 0.0.0.0 --port 5081
```

آدرس LAN فعلی این محیط `http://192.168.10.172:5081/` است؛ این IP ممکن است با
تغییر شبکه عوض شود. سرویس تشخیص باید جداگانه روی `http://127.0.0.1:5080` فعال
باشد و برای پخش WebRTC از دستگاه دیگر، پورت UDP WebRTC در Firewall مجاز باشد.

برای اجرای سریع محلی می‌توان از `npm run dev` استفاده کرد؛ مقدار پورت در
`vite.config.ts` روی `5081` تنظیم شده است. اگر origin رابط وب با originهای مجاز
سرویس یکی نباشد، همان origin را در `http.corsOrigins` قرار دهید و سرویس را restart
کنید؛ این policy برای REST و SignalR با endpoint `/hubs/detections` مشترک است.

برای مشخصات بازسازی route، component، اندازه‌ها و breakpointها، ابتدا
[مشخصات مرجع بازسازی UI وب](WEB-UI-RECONSTRUCTION-SPEC.md) را بخوانید. این سند
رفتار نهایی `DetectionManagerUi` و مسیر تصویر بین دوربین، موتور
تشخیص، خروجی کامپوزیت‌شدهٔ وب و درگاه مستقل WHEP را ثبت می‌کند. این سند مکمل
[معماری کلی](ARCHITECTURE.md)، [پیکربندی](CONFIGURATION.md) و
[MediaMTX/WebRTC](MediaMTX-WebRTC.md) است.

## ۱. ساختار صفحهٔ اصلی

صفحهٔ اصلی از commandbar، hero/stat، layout دو ستونهٔ تصویر و پنل تشخیص، و
پنل runtime زیر آن تشکیل می‌شود:

```text
┌──────────────────────────────────────────────────────────────┐
│ نوار بالایی: اکشن‌های عمومی، refresh، شروع/توقف و تنظیمات    │
├───────────────────────────────────────────────┬──────────────┤
│ دیوار دوربین‌ها / تصویر زنده                  │ پنل تشخیص   │
│ tileهای دوربین و کنترل‌های هر دوربین          │ crop + متن  │
└───────────────────────────────────────────────┴──────────────┘
│ پنل وضعیت runtime دوربین‌ها (زیر layout زنده)                │
└──────────────────────────────────────────────────────────────┘
```

- پنل سمت چپ/راست با عنوان `Detected events` خودِ تاریخچهٔ تشخیص است؛ در
  دسکتاپ حداکثر ۲۵۰ پیکسل عرض دارد و در عرض‌های کوچک‌تر از ۸۲۰ پیکسل تمام‌عرض
  می‌شود. پنل تشخیصی جداگانه‌ای که فقط متن یا کارت ساده نشان دهد وجود ندارد.
- هر کارت تشخیص شامل crop تصویر، نام دوربین، نوع/label تشخیص، confidence و
  زمان رخداد است.
- زیر layout اصلی یک پنل واقعی با عنوان `وضعیت runtime` وجود دارد که برای پنج
  دوربین اول FPS، زمان inference و وضعیت توقف را نشان می‌دهد؛ این پنل با پنل
  تشخیص یکی نیست و نباید حذف شود.
- بالای هر tile فقط دکمه‌های `Start/Stop`، `Edit` و بازکردن نمای کامل وجود دارد.
  دکمهٔ حذف دوربین در `CameraTile` وب render نمی‌شود. نمای کامل فعال در کد
  `CameraFocusWorkspace` است، نه کامپوننت قدیمی `CameraFullscreen` که در درخت
  فعلی استفاده نشده است.

## ۲. نمای کامل و ROI

با دکمهٔ ذره‌بین/نمای کامل:

1. تصویر دوربین از حالت thumbnail خارج می‌شود و workspace میانی را می‌پوشاند؛
   پنل تشخیص سمت راست باقی می‌ماند.
2. تصویر وارد حالت خودکار `Edit` نمی‌شود.
3. ابزارهای ROI شامل `ویرایش`، `حذف` و `ROI جدید` نمایش داده می‌شوند.
4. هر حالت، دکمه‌های `ذخیره ROI` و `لغو` دارد.
5. دکمهٔ بازگشت/بستن نمای کامل همیشه در دسترس است.
6. در حالت ویرایش، نقاط polygon روی تصویر انتخاب می‌شوند؛ حالت `جدید` یک ROI
   مستقل می‌سازد و حالت `حذف` فقط ROI انتخاب‌شده را حذف می‌کند.

نقاط ROI در API و تنظیمات به‌صورت نرمال‌شدهٔ `0..1` نسبت به ابعاد فریم ذخیره
می‌شوند.

## ۳. دسته‌بندی تنظیمات پردازش

در بخش `Processing / ROI`، هر ROI می‌تواند آیتم‌های مستقل داشته باشد. کارت
هر task در وب کرکره‌ای است و با کلیک باز می‌شود؛ تب‌های همان کارت مطابق کد فعلی
این بخش‌ها را جدا می‌کنند:

1. `تشخیص پلاک` (`Plate detection`): مدل، input size، confidence، NMS، نرخ
   پردازش و tracking پلاک. مقدار `preprocessing` در تنظیمات، مسیر legacy
   استخراج character را کنترل می‌کند؛ detector در runtime letterbox و
   نرمال‌سازی ثابت خودش را اجرا می‌کند.
2. `خواندن کاراکتر پلاک` (`Plate character recognition`): فعال‌سازی مستقل OCR،
   مدل OCR، confidence و نرخ پردازش OCR برای crop هر پلاک. در حالت OCR مستقل،
   crop خام پلاک به مدل داده می‌شود.
3. `تشخیص چهره` (`Face detection`): مدل YuNet، input size، preprocessing،
   confidence و NMS/TopK تشخیص چهره.
4. `شناسایی چهره` (`Face identification`): مدل SFace، threshold شناسایی،
   known/unknown matching و اتصال به دیتابیس مرکزی هویت.
5. `تشخیص کف دست` و `شناسایی کف دست` (`Palm detection/recognition`): مدل
   detector و recognition، input size، confidence، NMS، حداکثر دست و thresholdهای
   known/unknown. `DetectorKind` کنترل جدا ندارد و از مدل انتخابی تعیین می‌شود.
6. `ردیابی و ثبت سابقه` (`Tracking and recording`): IoU، حداکثر miss، نرخ
   پردازش، record confidence و cooldown رخداد.

مدل‌ها در UI به‌صورت input متنی وارد نمی‌شوند. هر فیلد مدل یک ComboBox است و
گزینه‌ها از catalog سرویس (`GET /api/v1/service/models`) بارگذاری می‌شوند و
هر ComboBox فقط مدل‌های capability خودش را نشان می‌دهد؛ catalog کامل هرگز
به‌عنوان fallback نمایش داده نمی‌شود.
catalog مسیرهای مدل زیر را بررسی می‌کند:

```text
<service-base>/Models/Plate
<service-base>/Models/Face
<service-base>/Models/Palm
<service-base>/Modules/<Capability>/Models
<project module>/Models            (Debug fallback؛ در زنجیرهٔ parentها)
```

در انتشار مشتری، مدل‌ها معمولاً در `Models/Plate`، `Models/Face` و
`Models/Palm` کنار خروجی سرویس قرار می‌گیرند؛ flat `Models` و مسیر legacy برای
lookup داخلی runtime پشتیبانی می‌شوند و fallback پروژه فقط برای Debug است.

فایل‌های runtime packageهای `.hshmodel` هستند، اما catalog سرویس نام نمایشی
آن‌ها را با capability مربوطه مانند `Plate`، `FaceDetection`،
`FaceRecognition`، `PalmDetection` و `PalmRecognition` به UI می‌دهد. برای
Palm detector فیلد `detectorKind` نیز اعلام می‌شود، اما کنترل مستقلی در UI
وجود ندارد. مقدار ذخیره‌شده در
select از نام منطقی `model.name` می‌آید؛ `relativePath` مسیر نسبی package را برای catalog
نگه می‌دارد و مسیر absolute به UI داده نمی‌شود.

## ۴. دریافت مستقیم و نمایش فعلی وب

دریافت موتور تشخیص در Windows و سرویس یک مسیر دارد:

```text
Camera RTSP → FrameSource/FFmpeg (1 decoder thread) → newest-frame → CameraRuntime
```

TCP/UDP و فاصلهٔ reconnect قابل تنظیم‌اند. source فقط جدیدترین فریم منتظر
پردازش را نگه می‌دارد. Webcam و فایل نیز ورودی‌های پشتیبانی‌شده باقی می‌مانند.

در UI فعلی، tileهای داشبورد و نمای متمرکز از `CompositeWebRtcStream` و خروجی
کامپوزیت‌شدهٔ سرویس استفاده می‌کنند. `RoiCanvas` هنگام `live=true` همان component
را نمایش می‌دهد و در حالت دیگر latest snapshot را می‌گیرد. در preview صفحهٔ
ویرایش دوربین، `status.canWhep` قابلیت source فعال RTSP را اعلام می‌کند؛
RTSP در حال اجرا preview زندهٔ کامپوزیت‌شده دارد و sourceهای دیگر snapshot دارند.
این Boolean اطلاعات قابلیت ورودی است.

```text
CameraRuntime preview + overlay state
  → LatestFrameStore
  → WebRtcGateway
  → CompositeWebRtcStream / Browser <video>
```

رندر، encoding و دریافت مرورگر مستقل از inference زمان‌بندی می‌شوند؛ کندی
inference فقط تازگی نتیجهٔ تشخیص را تغییر می‌دهد. قرارداد stream فعلی:

```text
POST   /api/v1/streams/{cameraId}/webrtc/offer
DELETE /api/v1/streams/webrtc/{sessionId}
```

داشبورد دوربین‌ها را به صفحه‌های حداکثر ۶تایی تقسیم می‌کند و فقط tileهای صفحهٔ
جاری mount می‌شوند. unmount شدن component، peer connection و session متعلق
به همان viewer را می‌بندد. دریافت و inference دوربین startشده در سرویس مستقل
از صفحهٔ باز UI ادامه دارند.

## ۵. درگاه مستقل WHEP مرورگر

سرویس برای هر source معتبر RTSP و فعال، API پخش خام MediaMTX/WHEP را نیز
ارائه می‌کند. `MediaMtxRuntime` فقط در سرویس است و path مستقل هر دوربین را
هنگام درخواست WHEP می‌سازد. درگاه پخش، اتصال on-demand جداگانه‌ای به دوربین
دارد؛ ورودی موتور تشخیص مستقیماً از دوربین به FFmpeg می‌رود.

```text
Camera RTSP ──┬─ FrameSource/FFmpeg → CameraRuntime → Overlay API
              └─ Service MediaMTX path → WHEP → Browser
```

```text
POST   /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
PATCH  /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
DELETE /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
GET    /api/v1/streams/{cameraId}/overlay?ts=<cache-buster>
```

`viewerId` برای هر viewer مستقل است. endpoint سبک Overlay شامل ابعاد اصلی،
ROIهای ثابت، `motionRois`، detectionهای فعال، label، confidence، track id،
bounds و primitiveهای polygon، polyline، point، circle و rectangle است.
مصرف‌کنندهٔ WHEP خام می‌تواند این state را روی ویدئو رسم کند.

کامپوننت `RawMediaMtxStream` و `LiveOverlaySvg` این مسیر را در سورس نگه
می‌دارند، اما در tile، نمای متمرکز و preview فعال فعلی mount نمی‌شوند.
پایش WebRTC و retry داخلی این component، رفتار مسیر خام در صورت استفاده از آن
است؛ به `CompositeWebRtcStream` نسبت داده نمی‌شود.

جزئیات پورت‌ها و lifecycle در [راهنمای MediaMTX/WHEP](MediaMTX-WebRTC.md) است.

## ۶. Snapshot

```text
GET /api/v1/streams/{cameraId}/snapshot
```

Snapshot از آخرین فریم کامپوزیت‌شدهٔ سرویس ساخته می‌شود. در `RoiCanvas` حالت
snapshot از `<img>` با `imageSource` و `refreshTimer` مستقل استفاده می‌کند؛
`scheduleRefresh` پس از load یا خطا، درخواست بعدی را 250ms بعد زمان‌بندی
می‌کند و ابعاد تصویر در `onLoad` ثبت می‌شوند. helper جداگانهٔ `SnapshotImage`
هنگام hidden شدن تب refresh را متوقف می‌کند؛ این رفتار به timer مستقل
`RoiCanvas` نسبت داده نمی‌شود. مختصات ROI نسبت به ابعاد واقعی تصویر نرمال می‌شوند و قابلیت‌های
ویرایش ROI از نوع دریافت دوربین مستقل‌اند.

## ۷. بررسی تأخیر

| مشاهده | مسیر بررسی |
| --- | --- |
| تصویر Windows با ROI خاموش هم عقب می‌ماند | تعداد واقعی decoder، FPS و ابعاد منبع در diagnostics؛ RTSP باید یک thread decoder داشته باشد. |
| تصویر وب Drawing دارد | مسیر فعال کامپوزیت‌شده از `/webrtc/offer` استفاده می‌کند. |
| مصرف‌کنندهٔ WHEP خام Drawing نمی‌بیند | state endpoint `overlay` و رسم سمت کلاینت بررسی شود. |
| اولین اتصال WHEP کند است و ادامهٔ پخش سریع است | راه‌اندازی on-demand درگاه و زمان اتصال upstream بررسی شود. |

سنجهٔ دریافت decodeشده تا آماده‌شدن preview یا رسم UI فقط بخش محلی را
پوشش می‌دهد. اندازه‌گیری تأخیر کامل دوربین، encoder، شبکه و decode باید با
همان stream و ساعت داخل صحنه انجام شود.
