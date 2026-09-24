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
- ROIهای یک دوربین به‌صورت موازی اجرا می‌شوند؛ این latency کلی را کم می‌کند اما
  مصرف CPU و حافظه را بالا می‌برد. در هر ROI، `ProcessingMode = Parallel` فقط
  وقتی مناسب است که taskها به خروجی یکدیگر وابسته نباشند و CPU ظرفیت اجرای
  همزمان آن‌ها را داشته باشد. برای زنجیره‌هایی که به `PreviousDetections` یا
  `NextImage` نیاز دارند، `Sequential` را نگه دارید.
- `ActiveDetectionFps` و `MaxFps` را متناسب با CPU تنظیم کنید.
- `InputSize` بزرگ‌تر معمولاً دقت و هزینهٔ CPU را با هم افزایش می‌دهد.
- `Threads` تعداد threadهای داخلی ONNX (`IntraOpNumThreads`) است، نه تعداد دوربین یا worker مستقل. افزایش آن برای هر ROI یک مصرف CPU سنگین ایجاد می‌کند؛ ابتدا با `1` baseline بگیرید و فقط اگر latency لازم است به `2` یا بیشتر بروید.
- API و inference در همان سرویس اجرا می‌شوند، اما graph pipeline دیگر قفل طولانی نگه نمی‌دارد؛ status و تنظیمات باید در زمان inference نیز پاسخ‌گو بمانند. صف event نیز bounded است و مقدار `droppedEventCount` در `GET /api/v1/service/status` نشان می‌دهد که ذخیره‌سازی از پردازش عقب افتاده است.
- اگر با `threads=4` هنوز CPU یا زمان inference بالا می‌رود، راه کنترل فشار کم‌کردن `MaxFps`/`ActiveDetectionFps`، کوچک‌ترکردن ROI و در صورت امکان استفاده از مدل سبک‌تر است؛ افزایش `BufferCount` برای جبران مناسب نیست و latency را بیشتر می‌کند.

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
به مسیر absolute یا یک model file تایپ‌شده وابسته باشد. `inputSizes` نیز از همان
catalog می‌آید: مدل ثابت فقط سایز ثابت tensor را اعلام می‌کند و مدل YOLO با
ابعاد پویا، گزینه‌های stride-aligned استاندارد `320`، `416`، `480`، `512` و
`640` را اعلام می‌کند. endpoint سرویس برای پاسخ سریع، هنگام ساخت catalog یک
`InferenceSession` کامل باز نمی‌کند؛ برای مدل‌های ثابت سایز درج‌شده در نام و
برای مدل‌های پویا گزینه‌های امن stride-aligned را برمی‌گرداند. inspection دقیق
همچنان در cache ماژول و مسیر فرم WinForms موجود است. این منبع بین فرم WinForms و
UI وب مشترک است.

## اجرای Face

در پیاده‌سازی فعلی `InputSize` از catalog مدل می‌آید: مدل‌های ثابت فقط اندازهٔ واقعی tensor خود را ارائه می‌دهند و مدل‌های YOLO پویا فهرست گزینه‌های stride-aligned را می‌گیرند. مدل‌های YuNet موجود ورودی `640×640` دارند و `FaceModule` نیز هنگام ساخت pipeline metadata مدل را بررسی می‌کند؛ بنابراین مقدار قدیمی 320 دیگر نباید برای این مدل تنظیم شود.

برای جلوگیری از فشار حافظه، metadata مدل با cache process-level خوانده می‌شود و association قبل از ساخت artifact، تشخیص‌های تکراری همان track/component را در بازهٔ cooldown کنار می‌گذارد؛ در نتیجه برای هر inference فریم کامل Bitmap بی‌دلیل کپی نمی‌شود.

YuNet با ورودی مربعی اجرا می‌شود؛ ROI ابتدا به `FaceInputSize` resize و سپس برای package فعلی به ورودی ثابت `640×640` تبدیل می‌شود و bounds به ابعاد اصلی ROI برگردانده می‌شود. بنابراین `FaceInputSize` اندازهٔ میانی preprocessing است، نه اندازهٔ tensor نهایی مدل. مدل `face_yunet_2023mar_int8.hshmodel` گزینهٔ سبک‌تر CPU است و نسخهٔ FP32 برای مقایسه/دقت حفظ می‌شود. SFace پس از تشخیص، پنج landmark را برای similarity alignment به crop `112×112` تبدیل می‌کند و سپس embedding را از tensor RGB با مقادیر پیکسلی خام می‌سازد؛ cosine similarity بردارها را هنگام مقایسه نرمال می‌کند، اما نرمال‌سازی جداگانهٔ ورودی یا embedding در مسیر فعلی وجود ندارد. نیازی به resize یا alignment جداگانه در UI برای مسیر runtime نیست.

`FacePreprocessing` پیش‌پردازش عمومی YuNet است و برای SFace لازم نیست. مقدار `None` پیش‌فرض و انتخاب توصیه‌شده هنگام فعال‌بودن recognition است؛ `Advanced` با grayscale و equalization ممکن است کیفیت embedding را کاهش دهد، چون در پیاده‌سازی فعلی تصویر آماده‌شده به مسیر SFace نیز می‌رسد.

`Threads` در `SessionOptions.IntraOpNumThreads` برای sessionهای YuNet و SFace تنظیم می‌شود و از `CameraProcessingSettings` همان آیتم می‌آید. UI کنترل Plate و Face را برای آیتم انتخاب‌شده sync می‌کند؛ مقدار camera-level فقط default ساخت آیتم جدید است. در چند دوربین، هر session تنظیم خودش را دارد و دیگر `CvInvoke.NumThreads` سراسری تغییر نمی‌کند.

برای Face، نرخ مؤثر از ترکیب `ActiveDetectionFps` یا `IdleDetectionFps` در
سطح دوربین و `MaxFps` در `Rois[].Processing[]` به‌دست می‌آید و برابر کمینهٔ
آن‌هاست. در نتیجه افزایش `FaceMaxFps` عمومی به‌تنهایی نرخ آیتم موجود را بالا
نمی‌برد. اجرای `FacePipeline.Process` شامل YuNet است و اگر SFace فعال باشد،
برای هر چهرهٔ پذیرفته‌شده در همان اجرا یک embedding و مقایسهٔ database نیز
انجام می‌شود؛ در نسخهٔ فعلی cache مستقلِ «شناسایی هر track هر چند فریم» وجود
ندارد. `EventCooldownSeconds` فقط ذخیرهٔ event/unknown sample را محدود می‌کند
و هزینهٔ محاسبهٔ SFace را حذف نمی‌کند.

ترتیب پیشنهادی tuning برای near-real-time با CPU متعادل:

1. `BufferCount = 0` و `MotionGateEnabled = true` را حفظ کنید تا صف فریم و
   inference بی‌دلیل ایجاد نشود.
2. مدل `face_yunet_2023mar_int8` را انتخاب کنید، سپس `ActiveDetectionFps` و
   Face item `MaxFps` را هر دو روی `8` بگذارید؛ در حالت idle مقدار `0` یا `1`
   مناسب است.
3. برای چند دوربین `Threads = 1` را نگه دارید. فقط اگر زمان inference از
   budget هر فریم بیشتر است و CPU ظرفیت دارد، `Threads = 2` را تست کنید؛
   افزایش thread برای هر دوربین می‌تواند با چند session باعث oversubscription
   شود.
4. اگر پس از این مراحل هنوز نرخ لازم حاصل نشد، ابتدا به‌صورت کنترل‌شده هر دو
   نرخ را به `10` افزایش دهید. پروفایل High performance با `15 FPS` و `4`
   thread برای CPU قوی و تعداد کم دوربین است، نه سناریوی چنددوربینهٔ عمومی.

## محافظت از مدل

فرمت `.hshmodel` فعلی رمزگذاری AES با header `HSHM0001` و IV 16-byte است. برای ساخت session، مدل ONNX رمزگشایی‌شده موقتاً در temp نوشته و سپس حذف می‌شود. بنابراین package صرفاً مانع دسترسی ساده است؛ شخص دارای کنترل کامل دستگاه می‌تواند حافظه، فایل موقت یا باینری را بررسی کند.

برای سطح بالاتر، امضای package، obfuscation، دریافت کلید از server و در نهایت inference سمت سرور گزینه‌های تکمیلی هستند. هیچ‌یک نباید private key صدور را داخل برنامهٔ مشتری قرار دهد.
