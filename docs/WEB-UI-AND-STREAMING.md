# رابط وب و مسیرهای دریافت/نمایش تصویر

در توسعه، UI مستقل `DetectionManagerUi` با Vite روی پورت `5173` اجرا می‌شود. برای
دسترسی از شبکهٔ محلی باید Vite روی همهٔ interfaceها bind شود:

```powershell
cd DetectionManagerUi
npx vite --host 0.0.0.0 --port 5173
```

آدرس LAN فعلی این محیط `http://192.168.10.172:5173/` است؛ این IP ممکن است با
تغییر شبکه عوض شود. سرویس تشخیص باید جداگانه روی `http://127.0.0.1:5080` فعال
باشد و برای پخش WebRTC از دستگاه دیگر، پورت UDP WebRTC در Firewall مجاز باشد.

برای مشخصات بازسازی route، component، اندازه‌ها و breakpointها، ابتدا
[مشخصات مرجع بازسازی UI وب](WEB-UI-RECONSTRUCTION-SPEC.md) را بخوانید. این سند
رفتار نهایی `DetectionManagerUi` و مسیر تصویر بین دوربین، موتور
تشخیص، MediaMTX/LibVLC و کلاینت مرورگر را ثبت می‌کند. این سند مکمل
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

در بخش `Processing / ROI`، هر ROI می‌تواند آیتم‌های مستقل داشته باشد. ادیتور
مطابق کد فعلی این بخش‌ها را جدا می‌کند:

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
   known/unknown matching و اتصال به Face Database.
5. `ردیابی و ثبت سابقه` (`Tracking and recording`): IoU، حداکثر miss، نرخ
   پردازش، record confidence و cooldown رخداد.

مدل‌ها در UI به‌صورت input متنی وارد نمی‌شوند. هر فیلد مدل یک ComboBox است و
گزینه‌ها از catalog سرویس (`GET /api/v1/service/models`) بارگذاری می‌شوند.
catalog مسیرهای مدل زیر را بررسی می‌کند:

```text
<service-base>/Models/Plate
<service-base>/Models/Face
<service-base>/Models
<service-base>/Modules/<Capability>/Models
<project module>/Models            (Debug fallback؛ در زنجیرهٔ parentها)
```

در انتشار مشتری، مدل‌ها معمولاً در `Models/Plate` و `Models/Face` کنار خروجی
سرویس قرار می‌گیرند؛ flat `Models` و مسیر legacy نیز بررسی می‌شوند و fallback
پروژه برای Debug است.

فایل‌های runtime packageهای `.hshmodel` هستند، اما catalog سرویس نام نمایشی
آن‌ها را با پسوند `.onnx` و capability مربوطه مانند
`Plate`، `FaceDetection` و `FaceRecognition` به UI می‌دهد. مقدار ذخیره‌شده در
select از `model.name` می‌آید؛ `relativePath` مسیر نسبی package را برای catalog
نگه می‌دارد و مسیر absolute به UI داده نمی‌شود.

## ۴. مسیر MediaMTX با latency کم

MediaMTX برای هر دوربین یک path مستقل دارد. دو مصرف‌کنندهٔ path از هم جدا هستند:

```text
                         ┌─ WHEP خام ───────────────► Browser video
دوربین RTSP ► MediaMTX ──┤
                         └─ RTSP داخلی ► Engine/FFmpeg ► Detection
```

### ۴.۱ مسیر ورودی موتور تشخیص

```text
Camera RTSP
  → MediaMTX path
  → rtsp://127.0.0.1:8554/camera-{id}
  → MediaMtxFrameSource
  → FrameSource/OpenCV FFmpeg
  → PreviewLoop و ProcessLoop
```

برای receiver داخلی MediaMTX، `LowLatencyMode` فعال است و capture با TCP و
گزینه‌های `nobuffer`، `low_delay` و `max_delay=0` ساخته می‌شود. در حالت
MediaMTX، `BufferCount=0` اجباری است تا فقط جدیدترین فریم باقی بماند.

### ۴.۲ مسیر خروجی کلاینت

```text
Camera RTSP
  → MediaMTX
  → WHEP/WebRTC خام
  → <video> در Browser
  → SVG Overlay جداگانه (`LiveOverlaySvg`)
```

ویدئوی MediaMTX در UI از مسیر `FrameReady`، تبدیل Bitmap یا
`WebRtcGateway` کامپوزیت‌شده عبور نمی‌کند. این تصمیم برای جلوگیری از تأخیر
تجمیعی ناشی از RTSP داخلی، resize، تبدیل Bitmap و encode مجدد VP8 است.

Overlay با endpoint زیر دریافت می‌شود و روی ویدئو رسم می‌گردد:

```text
GET /api/v1/streams/{cameraId}/overlay?ts=<cache-buster>
```

پاسخ شامل ابعاد فریم، ROIهای ثابت، `motionRois`، detectionهای فعال، confidence،
track id، bounds و primitiveهای پردازشی مانند polygon، polyline، point، circle و
rectangle است. ROI ثابت در SVG با خط نارنجی solid و motion ROI با خط زرد dashed
رسم می‌شود. TTL دادهٔ پویا در سرویس تعیین می‌شود؛ نمای متمرکز UI poll بعدی را
هر 180ms و thumbnailهای داشبورد هر 400ms زمان‌بندی می‌کنند. زمان‌بندی بعدی پس
از پایان پاسخ انجام می‌شود، ویدئو مستقل از refresh حرکت می‌کند و درخواست‌ها
روی هم انباشته نمی‌شوند.

### ۴.۴ مقیاس‌پذیری دیوار دوربین

داشبورد دوربین‌ها را به صفحه‌های حداکثر ۶تایی تقسیم می‌کند. فقط tileهای صفحهٔ
فعلی mount می‌شوند؛ در نتیجه تغییر صفحه، WHEP و overlay دوربین‌های صفحهٔ قبلی
را با unmount کردن متوقف می‌کند و تعداد درخواست‌های فعال تقریباً متناسب با
۶ دوربین باقی می‌ماند، نه کل فهرست دوربین‌ها. در تب hidden نیز همهٔ snapshot
timerها و WebRTC sessionهای UI متوقف می‌شوند و پس از visible شدن دوباره فعال
می‌گردند. این راهکار از نظر API فشار را محدود می‌کند، اما پردازش و capture
خود سرویس برای همهٔ دوربین‌هایی که کاربر start کرده همچنان ادامه دارد؛ برای
مقیاس بسیار بزرگ باید در لایهٔ سرویس نیز سیاست active-camera یا اشتراک push
برای overlay اضافه شود.

مسیر خام WHEP سرویس:

```text
POST   /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
PATCH  /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
DELETE /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
```

### ۴.۳ پایش سلامت و بازیابی خودکار پخش

`RawMediaMtxStream` فقط به باز بودن اتصال اکتفا نمی‌کند. هر دو ثانیه وضعیت
`RTCPeerConnection`، `iceConnectionState` و آمار `inbound-rtp` را بررسی می‌کند.
اگر اتصال failed/closed شود، فریم‌های ویدئو بیشتر از حدود ۷ ثانیه جلو نروند، یا
میانگین `jitterBufferDelay` برای چند ثانیه از حدود ۱٫۵ ثانیه بالاتر بماند، فقط
همان tile با یک `viewerId` جدید WHEP را دوباره برقرار می‌کند. اتصال قبلی بسته و
DELETE می‌شود و `<video>` دوباره `play()` می‌شود؛ بنابراین برای رفع lag نیازی
به refresh کل صفحه نیست. خطای موقت دوربین نیز با retry خودکار همان tile دنبال
می‌شود. فاصلهٔ retryها پلکانی و محدود به ۲، ۵، ۱۰ و حداکثر ۳۰ ثانیه است و پس
از اتصال سالم دوباره از ۲ ثانیه شروع می‌شود.

## ۵. مسیر LibVLC/VLC

در حالت `LibVLC`، MediaMTX در مسیر capture دوربین قرار ندارد:

```text
Camera RTSP
  → VlcFrameSource
  → LibVLC/Live555
  → BGRA → BGR Mat
  → latest-frame slot یا buffer محدود
  → CameraRuntime PreviewLoop/ProcessLoop
```

خروجی پردازش‌شدهٔ حالت LibVLC در UIهای غیر MediaMTX از Snapshot آخرین فریم
استفاده می‌کند و مسیر مشترک Overlay/تاریخچه را حفظ می‌کند. `BufferCount=0`
جدیدترین فریم را نگه می‌دارد؛ مقدار مثبت صف محدود VLC را فعال می‌کند و می‌تواند
عمداً latency ایجاد کند.

`LibVLC` برای RTSPهایی مناسب است که در VLC پایدارتر از OpenCV/FFmpeg هستند و
به VLC 3.x x64 نصب‌شده یا متغیر `VLC_HOME` نیاز دارد.

## ۶. Snapshot و خروجی‌های دیگر

```text
GET /api/v1/streams/{cameraId}/snapshot
```

Snapshot از آخرین فریم نگهداری‌شدهٔ سرویس ساخته می‌شود. در حالتی که سرویس فریم
کامپوزیت‌شده تولید کند، این endpoint شامل Drawing سرویس خواهد بود؛ اما مسیر
اصلی MediaMTX در UI برای کمترین latency از WHEP خام به‌همراه Overlay جداگانه
استفاده می‌کند.

endpoint زیر همچنان برای کلاینت‌های legacy یا مصرف‌کننده‌هایی که یک فریم
کامپوزیت‌شده می‌خواهند وجود دارد، ولی مسیر پیش‌فرض MediaMTX در UI نیست:

```text
POST /api/v1/streams/{cameraId}/webrtc/offer
DELETE /api/v1/streams/webrtc/{sessionId}
```

## ۷. راهنمای عیب‌یابی latency

| مشاهده | مسیر محتمل |
| --- | --- |
| تصویر خام MediaMTX سریع است ولی Drawing ندارد | WHEP خام فعال است و Overlay endpoint/کلاینت بررسی شود. |
| تصویر Drawing دارد ولی حدود دو ثانیه عقب است | مسیر قدیمی کامپوزیت‌شده یا `WebRtcGateway` به‌جای WHEP خام استفاده شده است. |
| latency فقط در LibVLC زیاد است | `BufferCount` مثبت، buffer داخلی LibVLC یا reconnect را بررسی کنید. |
| شروع اولیه کند است ولی بعد سریع می‌شود | `sourceOnDemand` و زمان اتصال اولیهٔ upstream MediaMTX است، نه latency هر فریم. |

برای حالت MediaMTX، ابتدا باید در Network مرورگر endpoint WHEP و endpoint
`overlay` دیده شوند؛ نباید برای نمایش tile یا ROI درخواست
`/webrtc/offer` ارسال شود.
