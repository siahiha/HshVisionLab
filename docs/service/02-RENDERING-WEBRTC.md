# Rendering و WebRTC با Drawing پایدار

## 1. مشکل اصلی

اگر پس از هر inference یک تصویر کامل render و همان تصویر برای WebRTC ارسال شود، زمان inference مستقیماً وارد نرخ فریم می‌شود. در این حالت، وقتی inference کند یا متناوب باشد، حرکت تصویر برای کاربر نرم نیست.

راه‌حل این است که سه نرخ کاملاً مستقل باشند:

1. نرخ capture دوربین
2. نرخ inference
3. نرخ render/stream

WebRTC باید همیشه از آخرین تصویر قابل‌دسترس تغذیه شود، نه از آخرین inference.

## 2. معماری دو مخزن

### `LatestFrameStore`

برای هر دوربین فقط آخرین فریم معتبر را نگه می‌دارد:

```text
FrameSnapshot
 ├── CameraId
 ├── Sequence
 ├── CapturedAtUtc
 ├── Width / Height
 └── PixelBuffer یا Mat مالکیت‌دار
```

Capture thread فریم جدید را در این store جایگزین می‌کند. اگر renderer یا encoder عقب باشد، فریم‌های قدیمی حذف می‌شوند؛ صف بی‌نهایت ساخته نمی‌شود.

قواعد:

- writer هرگز منتظر reader نمی‌ماند.
- reader یک snapshot مستقل می‌گیرد.
- dispose هر buffer باید مشخص و قابل‌ردیابی باشد.
- برای هر دوربین فقط یک latest slot وجود دارد، مگر برای تست و recording.

### `OverlayStateStore`

آخرین نتیجهٔ موفق inference را نگه می‌دارد:

```text
OverlayState
 ├── Version
 ├── SourceFrameSequence
 ├── UpdatedAtUtc
 ├── StaticOverlays: ROIها
 ├── DynamicOverlays: detectionها
 ├── Detections metadata
 └── ExpiresAtUtc برای هر overlay
```

این state با replace اتمیک عوض می‌شود. renderer نباید در زمان inference روی آن lock طولانی بگیرد.

## 3. Compositor

در هر tick ثابت، مثلاً 15 یا 20 FPS، compositor این کار را انجام می‌دهد:

```text
latestFrame = LatestFrameStore.Read()
overlayState = OverlayStateStore.Read()

output = Clone(latestFrame)
DrawStaticRois(output, overlayState)
DrawDynamicDetections(output, overlayState)
Publish(output)
```

بنابراین وقتی inference هنوز در حال اجراست:

- تصویر جدید دوربین همچنان نمایش داده می‌شود.
- ROIها روی هر فریم رسم می‌شوند.
- آخرین کادر تشخیص معتبر روی آخرین تصویر رسم می‌شود.
- پس از تکمیل inference، overlay state یک‌باره با نتیجهٔ جدید replace می‌شود.

این دقیقاً همان رفتار مطلوب برنامهٔ Windows فعلی است: تصویر زنده مستقل حرکت می‌کند و Drawing از state آخرین نتیجه استفاده می‌کند.

## 4.1 پیاده‌سازی فعلی MediaMTX در وب

برای رسیدن به latency نزدیک به مسیر خام MediaMTX، رابط وب در حالت
`CaptureBackend = MediaMTX` از دو کانال مستقل استفاده می‌کند:

```text
MediaMTX WHEP خام ───────────────► Browser <video>
Overlay API / polling ───────────► Browser SVG overlay
```

موتور تشخیص همچنان ورودی خود را از local RTSP path می‌گیرد:

```text
MediaMTX path → local RTSP → MediaMtxFrameSource → CameraRuntime
```

بنابراین `FrameReady` و `WebRtcGateway` برای نمایش اصلی MediaMTX در مسیر نیستند
و ویدئو برای اضافه‌کردن Drawing دوباره encode نمی‌شود. endpoint سبک
`/api/v1/streams/{cameraId}/overlay` ROIها، bounds تشخیص، label، confidence،
track id و primitiveهای پردازشی را می‌دهد. کلاینت این داده را روی همان ابعاد
فریم خام رسم می‌کند. این مسیر برای inference خاموش یا کند نیز latency ویدئو را
تغییر نمی‌دهد؛ فقط تازگی Overlay تابع آخرین نتیجهٔ پردازش است.

مسیر compositor و `WebRtcGateway` هنوز برای snapshot کامپوزیت‌شده، کلاینت‌های
legacy و مصرف‌کننده‌هایی که ویدئوی burn-in شده می‌خواهند قابل استفاده است، اما
نباید به‌عنوان مسیر پیش‌فرض WHEP MediaMTX استفاده شود.

## 4. سیاست overlay پویا

هر detection باید این metadata را داشته باشد:

- `SourceFrameSequence`
- `UpdatedAtUtc`
- `ExpiresAtUtc`
- `TrackId` در صورت وجود
- `Accepted`
- `Confidence`
- مختصات در فضای فریم اصلی، نه فقط ROI

تا زمانی که نتیجهٔ جدید نیامده است، کادر قبلی روی فریم‌های جدید رسم می‌شود. برای جلوگیری از کادرهای قدیمی:

- expiry پیش‌فرض کوتاه باشد؛ مثلاً 1 تا 3 ثانیه
- در صورت ادامهٔ track، tracker بتواند TTL را تمدید کند
- پس از expiry، کادر حذف شود
- ROIهای ثابت expiry نداشته باشند

مختصات detection از نتیجهٔ قدیمی مستقیماً روی latest frame رسم می‌شود، مشابه UI فعلی. در صورت نیاز به حرکت سریع، مرحلهٔ بعدی می‌تواند prediction سادهٔ tracker بین دو inference اضافه کند؛ این prediction نباید جای نتیجهٔ واقعی را بگیرد.

## 5. استفاده از خروجی فعلی موتور

`CameraRuntime` فعلی نیز مفهوم latest preview و overlayهای آخرین نتیجه را دارد. سرویس باید همین رفتار را به یک component مستقل تبدیل کند، نه اینکه WebRTC را به event پایان inference وصل کند.

مرز پیشنهادی:

```text
Engine result  →  OverlayStateAdapter  →  OverlayStateStore
FrameReady    →  LatestFrameStore      →  Compositor
Compositor    →  VideoSourceAdapter    →  WebRTC encoder
```

`FrameReady` یا renderer نباید ownership مشترک Bitmap/Mat را بدون قرارداد واضح بین چند thread پخش کند. بهترین قرارداد، snapshot immutable یا buffer دارای reference counting است.

## 6. WebRTC و دو قرارداد خروجی

برای خروجی کامپوزیت‌شده، WebRTC فقط مصرف‌کنندهٔ compositor است:

```text
Compositor 15/20 FPS
       ↓
VideoSourceAdapter
       ↓
Encoder
       ↓
WebRTC Peer Connections
```

قواعد performance:

- encoder هرگز capture یا inference را block نکند.
- اگر encoder عقب افتاد، قدیمی‌ترین فریم drop شود و latest frame حفظ شود.
- برای هر دوربین یک composited source و برای هر subscriber یک session داشته باشیم.
- تعداد subscriber، bitrate و resolution محدود و قابل تنظیم باشد.
- snapshot HTTP کامپوزیت‌شده از همان state خروجی گرفته می‌شود.
- در مسیر MediaMTX، متادیتای detection جدا از ویدئوی WHEP ارسال و در کلاینت
  به‌صورت SVG در `LiveOverlaySvg` رسم می‌شود؛ burn-in فقط برای مسیر
  compositor/legacy لازم است.

## 7. Signaling و شبکه

WebRTC خودش signaling را تعریف نمی‌کند. سرویس باید endpoint یا Hub برای offer، answer و ICE candidate داشته باشد. برای LAN معمولاً host/local candidate کافی است؛ برای شبکه‌های پیچیده یا اتصال بیرونی، STUN/TURN باید در تنظیمات قابل تعریف باشد.

مدل پیشنهادی:

```text
Client → POST/Hub offer
Service → answer
Client ↔ Service → ICE candidates
Client ← WebRTC annotated video
```

تنظیمات stream:

- `CameraId`
- `Profile` مثل low/medium/high
- width/height
- max FPS
- bitrate
- codec ترجیحی
- audio disabled در نسخهٔ اول
- فهرست ICE serverها

کتابخانهٔ WebRTC تا زمان PoC نباید در لایهٔ engine پخش شود؛ با interfaceای مثل `IWebRtcVideoPublisher` محصور شود تا انتخاب library قابل تعویض بماند.

## 8. معیار پذیرش

- inference کند نباید FPS تصویر را به صفر برساند.
- هنگام نبود detection جدید، latest image با آخرین overlay معتبر نمایش داده شود.
- وقتی فریم جدید می‌آید، ROI روی همان فریم جدید رسم شود.
- در مسیر compositor، WebRTC و snapshot یک تصویر compositing‌شدهٔ یکسان داشته
  باشند؛ در مسیر MediaMTX، WHEP خام و Overlay API باید با یک مختصات فریم منطبق
  باشند.
- قطع یک کلاینت نباید capture، inference یا stream کل دوربین را متوقف کند.
- حافظه با تعداد کلاینت‌ها و مدت اجرا به‌صورت نامحدود رشد نکند.
