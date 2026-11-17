# مدل‌ها و بهینه‌سازی

مرجع بازسازی کلی در [REBUILD_GUIDE.md](REBUILD_GUIDE.md) است.

## تفکیک assetها

| محل | محتوا | قابل توزیع به مشتری |
| --- | --- | --- |
| `HshDetectionEngin.Plate/Models` | packageهای `.hshmodel` پلاک | Release: جداگانه در `Models/Plate` یا `Models` کنار executable؛ Debug: fallback از پوشهٔ پروژه |
| `HshDetectionEngin.Face/Models` | YuNet FP32/INT8 و SFace package شده | Release: جداگانه در `Models/Face` یا `Models` کنار executable؛ Debug: fallback از پوشهٔ پروژه |
| `HshDetectionEngin.Palm/Models` | packageهای `.hshmodel` کف دست | Release: در `Models/Palm`؛ Debug: fallback از پوشهٔ پروژه |
| `HshDetectionEngin.Tools/RawModels/{Plate,Face,Palm}` | ONNX/PyTorch خام با تفکیک قابلیت | خیر |
| `HshDetectionEngin.Tools/ModelTools` | اسکریپت‌های Python quantization | خیر |
| `HshDetectionEngin.Tools/BuildArtifacts` | خروجی‌های آزمایشی و legacy | خیر |

## اجرای Plate

YOLO با ONNX Runtime و CPU اجرا می‌شود. ورودی با letterbox به `InputSize` تبدیل می‌شود، threshold و NMS از تنظیمات می‌آید و `Threads` به `SessionOptions.IntraOpNumThreads` در ONNX Runtime وصل است. `ModelOptimizer` فقط ابزار توسعه برای graph خام است؛ runtime و خروجی publish فقط packageهای `.hshmodel` را مصرف می‌کنند. package برای ساخت session موقتاً materialize می‌شود و فایل موقت پس از ایجاد session حذف می‌گردد.

برای latency زنده، مهم‌تر از بالا بردن FPS این موارد هستند:

- دریافت در Windows و سرویس همیشه newest-frame است و فقط آخرین فریم منتظر پردازش را نگه می‌دارد.
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
- اگر با `threads=4` هنوز CPU یا زمان inference بالا می‌رود، راه کنترل فشار کم‌کردن `MaxFps`/`ActiveDetectionFps`، کوچک‌ترکردن ROI و در صورت امکان استفاده از مدل سبک‌تر است.

### تأخیر، CPU و حافظهٔ نمایش Windows

دریافت RTSP در Windows و سرویس مستقیماً با FFmpeg، تنظیمات کم‌تأخیر و فقط جدیدترین فریم انجام می‌شود. TCP/UDP و فاصلهٔ reconnect قابل تنظیم‌اند؛ بافر بودجه‌بندی‌شدهٔ تکمیل تشخیص مستقل است. گزینه‌های کم‌تأخیر FFmpeg به کنترل بافر reader کمک می‌کنند، اما تأخیر encoder دوربین و شبکه را حذف نمی‌کنند.

برای کنترل هزینهٔ CPU و RAM، سقف داخلی preview همچنان ۱۵ FPS است و زمان‌بندی به نوبت‌های ثابت متصل است. فریم preview با حفظ نسبت ابعاد و متناسب با فضای نمایش تا حداکثر `1280×720` آماده می‌شود؛ اندازهٔ ورودی inference حفظ می‌شود. Matهای مسیر preview قابل استفادهٔ مجددند و Bitmap به‌جای clone به UI منتقل می‌شود. هر دوربین قابل مشاهده حداکثر یک Bitmap منتظر و یک Bitmap نمایشی دارد؛ تصاویر جایگزین‌شده آزاد می‌شوند. دوربین مخفی یا پنجرهٔ minimizeشده preview تولید نمی‌کند، اما capture و inference ادامه دارند.

در RTSP، تعداد threadهای decoder هنگام بازکردن FFmpeg با `CAP_PROP_N_THREADS=1` محدود می‌شود؛ این تنظیم با `Threads` مربوط به ONNX تفاوت دارد. مقدار پیش‌فرض OpenCV می‌تواند تا ۱۶ thread باشد و frame threading چند فریم را پیش از خروجی نگه دارد. تنظیمات format مانند `nobuffer` این صف decoder را حذف نمی‌کنند. محدودیت صریح thread از ساخت contextها و بافرهای اضافی decoder جلوگیری می‌کند؛ با این حال توان decode تک‌thread برای streamهای سنگین باید بررسی شود. مقدار واقعی thread و FPS/ابعاد منبع پس از بازشدن در diagnostics ثبت می‌شود. منابع: [OpenCV 4.13](https://github.com/opencv/opencv/blob/4.13.0/modules/videoio/src/cap_ffmpeg_impl.hpp)، [قرارداد propertyهای VideoCapture](https://docs.opencv.org/4.13.0/d4/d15/group__videoio__flags__base.html).

تأخیر محلی از دریافت فریم decodeشده تا آماده‌شدن preview و رسم UI با پنجرهٔ ثابت ۱۲۰ نمونه ثبت می‌شود. این عدد زمان encoder، انتقال و decode را شامل نمی‌شود. بررسی کوتاه H.264 با `1280×720` و نرخ اسمی ۲۰ FPS، تعداد واقعی threadهای decoder را از ۱۶ به ۱ و نرخ دریافت پس از اولین فریم را حدود `20.12` به `19.94` FPS نشان داد. کاربر پس از اصلاح، رفع تأخیر نمایش Windows را تأیید کرده است. درصد کاهش CPU/RAM و عدد تأخیر کامل دوربین اندازه‌گیری نشده‌اند؛ برای مقایسهٔ کمی، همان stream را با inference روشن/خاموش و نمای visible/minimized، CPU، working set/private bytes و ساعت داخل صحنه ارزیابی کنید.

### latency مسیر نمایش سرویس/وب

UI وب فعلی tile، focus و ROI زنده را از `CompositeWebRtcStream` و خروجی
کامپوزیت‌شدهٔ `WebRtcGateway` می‌گیرد. هر دوربین دارای viewer متصل، یک worker
encode مستقل و یک encoder مشترک برای همهٔ viewerهای همان دوربین دارد. دوربین
کند حلقهٔ encode دوربین دیگر را متوقف نمی‌کند. gateway حداکثر یک Bitmap در
حال encode و یک Bitmap منتظر نگه می‌دارد؛ فریم منتظر با جدیدترین فریم جایگزین
و آزاد می‌شود. sequence تکراری یا قدیمی دوباره encode نمی‌شود و task به‌ازای
هر فریم ساخته نمی‌شود.

مرورگر اندازهٔ فضای نمایش در CSS را بدون ضرب در `devicePixelRatio` می‌فرستد.
Preview با سقف `1280×720` و حداکثر ۱۵ FPS تولید می‌شود؛
`Service.Runtime.PreviewFps` اکنون در بازهٔ `1..15` اعمال می‌شود. ابعاد encode
در هر دو محور به مضرب ۱۶ گرد می‌شوند و کل تصویر بدون letterbox یا crop resize
می‌شود؛ نسبت ابعاد در حد این گردکردن حفظ می‌شود. ورودی inference و ROI تشخیص
با ابعاد اصلی باقی می‌مانند. بزرگ‌ترین viewport متصل هر دوربین اندازهٔ خروجی
مشترک را تعیین می‌کند؛ افزایش تعداد viewer همان دوربین encoder دیگری نمی‌سازد.

تبدیل تصویر به I420 از Bitmap، Mat و آرایهٔ قابل استفادهٔ مجدد روی worker
encoder استفاده می‌کند. آرایهٔ چندمگابایتی RGB به‌ازای هر viewer و هر tick ساخته
نمی‌شود. نمونهٔ فشردهٔ VP8 و fragmentهای آن بین peerها مشترک‌اند. timestamp RTP
از زمان محلی فریم با clock نودکیلوهرتز می‌آید تا حذف فریم، زمان ویدئو را عقب
نیندازد؛ viewer تازه و تغییر اندازه keyframe می‌خواهند.

در تغییر ابعاد واقعی، codec به‌صورت کنترل‌شده جایگزین می‌شود؛ نگه‌داشتن codec
با planeهای داخلی بزرگ‌تر پس از کوچک‌شدن viewport در `Vpx.Net` خطای اندازه
ایجاد می‌کند و صرفاً درخواست keyframe آن را برطرف نمی‌کند. در ابعاد ثابت،
همان codec و bufferها حفظ می‌شوند. آزمون native کاهش/افزایش اندازه، keyframe
و ابعاد header خروجی را بررسی می‌کند.

Preview سرویس هنگام نبود viewer متصل یا تقاضای snapshot خاموش است و bufferهای
آن آزاد می‌شوند. درخواست snapshot تقاضای سه‌ثانیه‌ای با حداکثر ۵ FPS ایجاد
می‌کند و نخستین درخواست پس از حالت بی‌نمایش تا ۷۵۰ میلی‌ثانیه منتظر فریم
می‌ماند. Sessionهای signaling ناقص پس از ۳۰ ثانیه و viewerهای دارای viewport
پس از ۴۵ ثانیه بدون heartbeat پاک می‌شوند؛ UI هر ۱۰ ثانیه heartbeat می‌فرستد.
Capture و inference با مخفی‌شدن preview متوقف نمی‌شوند.

`GET /api/v1/streams/diagnostics` ابعاد واقعی، تعداد encode، replacement و
duplicate، زمان encode، سن فریم، FPS و اندازهٔ صف هر دوربین را می‌دهد. هدف
پذیرش، حذف توقف مشترک دوربین‌ها و محدودبودن حافظه است؛ عدد بهبود CPU/RAM و
روانی تصویر باید با بار واقعی قبل/بعد مقایسه شود. آزمون کنسول و browser مصنوعی
در `tests/WebRtcStreaming.Tests` بدون دوربین واقعی، دیتابیس یا trigger موجود
است؛ جزئیات در [Rendering و WebRTC](service/02-RENDERING-WEBRTC.md) آمده است.

برای sourceهای فعال RTSP، سرویس درگاه مستقل پخش خام نیز فراهم می‌کند:

```text
MediaMTX WHEP خام → Browser <video>
Overlay JSON       → SVG (`LiveOverlaySvg`) روی ویدئو
```

در این درگاه مستقل، ویدئوی خام از MediaMTX/WHEP می‌آید و کادرها با Overlay سمت کلاینت
رسم می‌شوند؛ تبدیل Bitmap و encode مجدد برای اضافه‌کردن Drawing انجام نمی‌شود.
موتور تشخیص هم‌زمان مستقیماً از دوربین با FFmpeg و یک thread decoder می‌خواند
و state سبک Overlay را به مصرف‌کننده می‌دهد. CameraEditor برای sourceهای غیر
RTSP از latest snapshot سرویس استفاده می‌کند.

 catalog مدل UI نیز فقط packageهای `.hshmodel` قابل استفاده را از `Models/Plate`، `Models/Face`،
`Models/Palm`، مسیرهای legacy و fallbackهای Debug فهرست می‌کند؛ ComboBox مدل نباید
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

1. `MotionGateEnabled = true` را حفظ کنید تا inference بی‌دلیل ایجاد نشود؛
   دریافت در هر دو میزبان فقط جدیدترین فریم را نگه می‌دارد.
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
