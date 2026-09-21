# مدل‌ها و بهینه‌سازی

مرجع بازسازی کلی در [REBUILD_GUIDE.md](REBUILD_GUIDE.md) است.

## تفکیک assetها

| محل | محتوا | قابل توزیع به مشتری |
| --- | --- | --- |
| `HshDetectionEngin.Plate/Models` | packageهای `.hshmodel` پلاک | Release: جداگانه در `Models/Plate` یا `Models` کنار executable؛ Debug: fallback از پوشهٔ پروژه |
| `HshDetectionEngin.Face/Models` | YuNet FP32/INT8 و SFace package شده | Release: جداگانه در `Models/Face` یا `Models` کنار executable؛ Debug: fallback از پوشهٔ پروژه |
| `HshDetectionEngin.Tools/RawModels` | ONNX/PyTorch خام | خیر |
| `HshDetectionEngin.Tools/ModelTools` | اسکریپت‌های Python quantization | خیر |
| `HshDetectionEngin.Tools/BuildArtifacts` | خروجی‌های آزمایشی و legacy | خیر |

## اجرای Plate

YOLO با ONNX Runtime و CPU اجرا می‌شود. ورودی با letterbox به `InputSize` تبدیل می‌شود، threshold و NMS از تنظیمات می‌آید و `Threads` به `SessionOptions.IntraOpNumThreads` در ONNX Runtime وصل است. برای مدل خام، `ModelOptimizer` ممکن است artifact بهینه‌شده بسازد؛ packageهای رمزگذاری‌شده در مسیر فعلی به‌صورت مستقیم و موقت materialize می‌شوند و بهینه‌سازی خودکار خام ندارند.

برای latency زنده، مهم‌تر از بالا بردن FPS این موارد هستند:

- برای کمترین latency، `BufferCount = 0` و newest-frame semantics را حفظ کنید؛ پردازش فریم‌های صف‌شده ممکن است باعث عقب‌افتادن تصویر زنده شود.
- ROI دقیق و Motion Gate فعال، تعداد inferenceهای بی‌فایده را کم می‌کند.
- `ActiveDetectionFps` و `MaxFps` را متناسب با CPU تنظیم کنید.
- `InputSize` بزرگ‌تر معمولاً دقت و هزینهٔ CPU را با هم افزایش می‌دهد.

### latency مسیر نمایش

در حالت MediaMTX، کم‌تاخیرترین مسیر نمایش این است:

```text
MediaMTX WHEP خام → Browser <video>
Overlay JSON       → SVG (`LiveOverlaySvg`) روی ویدئو
```

ویدئو نباید برای اضافه‌کردن کادر تشخیص به `FrameReady`، تبدیل Bitmap یا
`WebRtcGateway` کامپوزیت‌شده فرستاده شود؛ این کار یک RTSP داخلی، resize، encode
مجدد و jitter buffer اضافی وارد مسیر می‌کند. موتور تشخیص می‌تواند هم‌زمان از
local RTSP MediaMTX استفاده کند و فقط state سبک Overlay را به UI بدهد.

در MediaMTX، `BufferCount = 0` اجباری است و local reader با TCP و گزینه‌های
FFmpeg `nobuffer`، `low_delay` و `max_delay=0` باز می‌شود. در LibVLC، مقدار
مثبت `BufferCount` صف واقعی می‌سازد و برای کاهش latency باید صفر بماند.

catalog مدل UI نیز فقط فایل‌های قابل استفاده را از `Models/Plate`، `Models/Face`،
`Models`، مسیرهای legacy و fallbackهای Debug فهرست می‌کند؛ ComboBox مدل نباید
به مسیر absolute یا یک model file تایپ‌شده وابسته باشد.

## اجرای Face

YuNet با ورودی مربعی اجرا می‌شود؛ ROI ابتدا به `FaceInputSize` resize و سپس برای package فعلی به ورودی ثابت `640×640` تبدیل می‌شود و bounds به ابعاد اصلی ROI برگردانده می‌شود. بنابراین `FaceInputSize` اندازهٔ میانی preprocessing است، نه اندازهٔ tensor نهایی مدل. مدل `face_yunet_2023mar_int8.hshmodel` گزینهٔ سبک‌تر CPU است و نسخهٔ FP32 برای مقایسه/دقت حفظ می‌شود. SFace پس از تشخیص، پنج landmark را برای similarity alignment به crop `112×112` تبدیل می‌کند و سپس embedding را از tensor RGB با مقادیر پیکسلی خام می‌سازد؛ cosine similarity بردارها را هنگام مقایسه نرمال می‌کند، اما نرمال‌سازی جداگانهٔ ورودی یا embedding در مسیر فعلی وجود ندارد. نیازی به resize یا alignment جداگانه در UI برای مسیر runtime نیست.

`FacePreprocessing` پیش‌پردازش عمومی YuNet است و برای SFace لازم نیست. مقدار `None` پیش‌فرض و انتخاب توصیه‌شده هنگام فعال‌بودن recognition است؛ `Advanced` با grayscale و equalization ممکن است کیفیت embedding را کاهش دهد، چون در پیاده‌سازی فعلی تصویر آماده‌شده به مسیر SFace نیز می‌رسد.

`Threads` در `SessionOptions.IntraOpNumThreads` برای sessionهای YuNet و SFace تنظیم می‌شود و از `CameraProcessingSettings` همان آیتم می‌آید. UI کنترل Plate و Face را برای آیتم انتخاب‌شده sync می‌کند؛ مقدار camera-level فقط default ساخت آیتم جدید است. در چند دوربین، هر session تنظیم خودش را دارد و دیگر `CvInvoke.NumThreads` سراسری تغییر نمی‌کند.

## محافظت از مدل

فرمت `.hshmodel` فعلی رمزگذاری AES با header `HSHM0001` و IV 16-byte است. برای ساخت session، مدل ONNX رمزگشایی‌شده موقتاً در temp نوشته و سپس حذف می‌شود. بنابراین package صرفاً مانع دسترسی ساده است؛ شخص دارای کنترل کامل دستگاه می‌تواند حافظه، فایل موقت یا باینری را بررسی کند.

برای سطح بالاتر، امضای package، obfuscation، دریافت کلید از server و در نهایت inference سمت سرور گزینه‌های تکمیلی هستند. هیچ‌یک نباید private key صدور را داخل برنامهٔ مشتری قرار دهد.
