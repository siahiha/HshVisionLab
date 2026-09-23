# راهنمای مرجع بازسازی HshVisionLab

این سند قرارداد بازسازی پروژه است: عامل یا توسعه‌دهنده‌ای که سامانه را از صفر می‌سازد باید رفتار قابل مشاهده، مرز ماژول‌ها، مسیرهای فایل و قیود امنیتی زیر را حفظ کند. مستندات موضوعی کنار این فایل خلاصه و عملیاتی‌اند؛ اگر میان آن‌ها یا این سند با کد اختلافی بود، رفتار کد مرجع نهایی است.

## 1. خروجی مورد انتظار

`HshVisionLab` یک برنامهٔ WinForms ویندوزی برای مدیریت هم‌زمان چند دوربین مستقل است. هر دوربین باید بتواند از RTSP، Webcam یا فایل ویدئویی فریم بگیرد، یک یا چند ROI چندضلعی را تحلیل کند، Motion Gate داشته باشد و قابلیت Plate، Face یا هر دو را اجرا کند.

رفتارهای ضروری محصول:

- دریافت و پردازش هر دوربین مستقل است؛ توقف یا خطای یک دوربین نباید دوربین دیگر را متوقف کند.
- فریم‌های قدیمی برای حفظ latency حذف می‌شوند؛ سیستم برای پردازش همهٔ فریم‌ها طراحی نشده است.
- Plate شامل YOLO، OCR پلاک ایرانی، track و history است.
- Face شامل YuNet، track مبتنی بر IoU، شناسایی اختیاری SFace و پایگاه‌دادهٔ embedding محلی است.
- قابلیت‌ها فقط با لایسنس معتبر همان feature اجرا می‌شوند.
- UI و بستهٔ مشتری نباید مدل خام ONNX/PyTorch، ابزار Python یا کلید خصوصی صادرکننده را داشته باشند.

تمام entry pointهای WinForms باید `Main` با `[STAThread]` داشته باشند؛ `SaveFileDialog`، Clipboard و سایر OLE APIها در thread غیر-STA خطا می‌دهند.

## 2. ساختار Solution و وابستگی‌ها

فایل solution، `HshVisionLab.sln`، هشت پروژه دارد:

| پروژه | خروجی | مسئولیت |
| --- | --- | --- |
| `HshVisionLab` | `HshVisionLab.exe` | UI چنددوربینه، تنظیمات، ROI editor، preview، history و Face database |
| `HshDetectionEngin.Abstractions` | `HshDetectionEngin.Abstractions.dll` | قراردادها، مدل‌های تنظیمات و DTOها |
| `HshDetectionEngin` | `HshDetectionEngin.dll` | capture، Motion، ROI، runtime loop، `CameraPipelineCoordinator` و policyهای render/overlay/history |
| `HshDetectionEngin.Plate` | `HshDetectionEngin.Plate.dll` | YOLO، OCR، preprocessing، tracker و loader مدل Plate |
| `HshDetectionEngin.Face` | `HshDetectionEngin.Face.dll` | YuNet، SFace، Face tracker، preprocessing و FaceDatabase |
| `HshDetectionEngin.Licensing` | `HshDetectionEngin.Licensing.dll` | fingerprint دستگاه، درخواست، صدور و اعتبارسنجی لایسنس RSA |
| `HshDetectionEngin.LicenseRequest` | `HshDetectionEngin.LicenseRequest.exe` | ابزار مشتری برای ساخت درخواست فعال‌سازی |
| `HshDetectionEngin.LicenseIssuer` | `HshDetectionEngin.LicenseIssuer.exe` | ابزار امن صادرکننده برای مدیریت مشتری و صدور/آرشیو لایسنس |

```text
HshVisionLab ─────────> Engin, Plate, Face, Abstractions
Engin ────────────────> Abstractions, Licensing
Plate ────────────────> Abstractions
Face ─────────────────> Abstractions, Licensing
LicenseRequest ───────> Licensing
LicenseIssuer ────────> Licensing
```

پوشهٔ `HshDetectionEngin.LicenseTool` یک artifact قدیمی است و عضو solution نیست؛ برای بازسازی یا انتشار از آن استفاده نکنید.

## 3. قراردادهای پایدار Engine

قراردادهای عمومی در `HshDetectionEngin.Abstractions` هستند:

- `IFrameSource`: رویدادهای وضعیت، خطا و `FrameAvailable`، متدهای `Configure`/`Start`/`Stop` و دریافت فریم با `TryDequeueFrame`. فریم ارسال‌شده در `FrameAvailable` borrowed است؛ مصرف‌کننده فقط در طول callback آن را می‌خواند و نباید آن را dispose کند.
- `IProcessingPipeline`: متد `Process(ProcessingContext)` و نام pipeline.
- `IProcessingModule`: قرارداد ثبت یک قابلیت شامل `ProcessingType`، descriptor UI، options type و factory ساخت pipeline.
- `ProcessingRegistry`: registry مرکزی capabilityها؛ Composition Root آن را یک‌بار می‌سازد و runtime فقط از آن مصرف می‌کند.
- `IFrameRenderer`: مسیر جایگزین برای تبدیل فریم و detectionها به `Bitmap`.
- `AnalysisDetection`: شامل `Kind`، `Label`، `Confidence`، `Bounds`، `TrackId` اختیاری و metadata.
- `PipelineResult`: detectionها و در صورت نیاز `NextImage`؛ Runtime با `TakeNextImage()` مالکیت تصویر را به pipeline بعدی منتقل می‌کند و اگر تصویر منتقل نشود، `Dispose()` آن را آزاد می‌کند.

API مصرف‌کنندهٔ Engine کلاس `Camera` است:

```csharp
using HshDetectionEngin;

var camera = new Camera(new CameraSettings { Name = "Entrance", SourceUrl = "0" });
camera.Motion.Enabled = true;
camera.Roi.Add(new NamedRoi
{
    Name = "Road",
    Points = [new RoiPoint(0, 0), new RoiPoint(1, 0), new RoiPoint(1, 1), new RoiPoint(0, 1)]
});
camera.PipelineResultsReady += (_, detections) => { /* consume detections */ };
camera.Start();
```

نقاط ROI نرمال‌شده‌اند (`0..1`). `Camera.Source`، `Camera.Motion`، `Camera.Roi` و `Camera.Configuration` viewهای عمومی روی همان `CameraSettings` هستند.

Pipelineهای فعال هر ROI به ترتیب مستقل اجرا می‌شوند. `PreviousDetections` خروجی pipeline قبلی همان ROI است و `NextImage` ورودی pipeline بعدی می‌شود؛ این تصویر باید با `TakeNextImage()` منتقل شود تا دوباره توسط `PipelineResult.Dispose()` آزاد نشود. خطای یک pipeline به همان pipeline محدود می‌ماند و اجرای pipelineهای بعدی همان ROI و ROIهای دیگر را متوقف نمی‌کند. Runtime offset ROI را به `Bounds` detection و هندسهٔ `ProcessingOverlay` اضافه می‌کند؛ pipelineی که تصویر را resize یا crop می‌کند باید bounds و overlayهای خود را با ابعاد ROI اصلی هم‌مقیاس نگه دارد. `AnalysisDetection` برای نتایج معنایی است و overlayهایی مانند polyline، polygon، point، circle و rectangle باید از `PipelineResult.Overlays` برگردند تا وارد history تشخیص نشوند.

`PlatePipeline` داخل ماژول Plate وجود دارد اما internal است و `PlateModule.CreateRegistration` registration آن را فراهم می‌کند. `FaceModule` نیز با وابستگی‌های لازم مانند database و license registration خود را فراهم می‌کند. Composition Root برنامه هر دو registration را در یک `ProcessingRegistry` مشترک ثبت می‌کند؛ `CameraPipelineCoordinator` برای هر ROI فعال از registry pipeline می‌سازد و اجرا می‌کند. `CameraRuntime` به concrete type یا factory مخصوص Face/Plate وابسته نیست. هر دو قابلیت از قرارداد مشترک `IProcessingPipeline` و چرخهٔ اجرای همان ROI استفاده می‌کنند؛ خود Engine به ماژول Face یا Plate وابستگی مستقیم ندارد.

## 4. جریان فریم، Motion و ROI

```text
FrameSource (به‌صورت پیش‌فرض فقط جدیدترین Mat؛ با BufferCount مثبت صف محدود)
  → latest-preview slot و preview worker با سقف 15 FPS
  → CameraRuntime capture/Motion/ROI loop
  → CameraPipelineCoordinator برای ساخت graph و اجرای هر ROI
  → MotionDetector و mask کردن بخش خارج polygon ROI
  → processing pipelines به‌ترتیب فهرست (Plate و Face، در صورت فعال و مجاز)
  → overlay، history و UI events
```

`FrameSource` با `BufferCount = 0` فقط یک `Mat` جدیدترین را نگه می‌دارد و قبلی را dispose می‌کند. با مقدار مثبت، صفی محدود به همان ظرفیت ایجاد می‌شود و برای حفظ latency، هنگام پرشدن قدیمی‌ترین فریم حذف می‌شود؛ این صف بین pipelineهای Plate و Face همان دوربین مشترک است. Webcam با source عددی و backend `DShow` باز می‌شود. RTSP در حالت پیش‌فرض با FFmpeg باز می‌شود؛ تنظیم process-wide `OPENCV_FFMPEG_CAPTURE_OPTIONS` در یک gate سراسری، فقط تا پایان ساخت همان capture اعمال و سپس restore می‌شود تا دوربین‌های TCP و UDP هم‌زمان با هم تداخل نکنند. transport انتخاب‌شده در تنظیمات ثابت می‌ماند و در reconnect خودکار به transport مقابل تغییر نمی‌کند. اگر `CaptureBackend` روی `LibVLC` باشد، `VlcFrameSource` با libVLC/Live555 فریم‌ها را دریافت می‌کند؛ این حالت برای RTSPهایی است که در VLC پایدارند ولی در OpenCV FFmpeg stall می‌کنند و به VLC 3.x x64 نصب‌شده (یا متغیر `VLC_HOME`) نیاز دارد. اگر `CaptureBackend` روی `MediaMTX` باشد، runtime path دوربین را در MediaMTX مدیریت می‌کند و engine از `rtsp://127.0.0.1:8554/camera-{id}` با TCP، `nobuffer`، `low_delay` و `max_delay=0` می‌خواند؛ مرورگر همان path را از WHEP خام می‌گیرد و Overlay را جداگانه از سرویس دریافت می‌کند. در قطع کوتاه stream، reader تا زمان تعیین‌شدهٔ backend فرصت بازیابی می‌دهد؛ سپس reconnect با `ReconnectDelaySec` انجام می‌شود. منبع non-RTSP ابتدا با FFmpeg و سپس با backend پیش‌فرض OpenCV امتحان می‌شود.

ROI معتبر حداقل سه نقطه و bounding box حداقل `32×32` دارد. Motion روی تصویر خاکستری `320×180` اجرا می‌شود و بیرون polygon را mask می‌کند. پس از تشخیص حرکت، وضعیت active تا `MotionHoldMs` می‌ماند؛ در حالت idle، `IdleDetectionFps = 0` یعنی inference متوقف است. نرخ فراخوانی runtime با نرخ Active/Idle کنترل می‌شود و هر Plate/Face نیز سقف `MaxFps` مستقل خودش را روی همان آیتم اعمال می‌کند؛ `CameraSettings.MaxFps` فقط default سازگاری برای ساخت آیتم جدید است و سقف پنهان آیتم‌های موجود نیست. با خاموش‌بودن Motion Gate، وضعیت همیشه active است.

`History event cooldown (sec)` سه کاربرد مستقل دارد. در هر task Plate/Face زیر ROI
با `EventCooldownSeconds` ثبت canonical history برای همان دوربین، ROI و مقدار
تشخیص کنترل می‌شود؛ اگر تریگری match نشده باشد، رکورد تکراری در `DetectionHistory`
باعث insert دوبارهٔ `DetectionEvents` نمی‌شود. در `ClientSubscription` همین فیلد
با `CooldownSeconds` فقط replay/live و گرید تاریخچهٔ همان اتصال را فیلتر می‌کند.
در `TriggerDefinition` با `CooldownSeconds`، Runtime قبل از match کردن تریگر، Event
Store را برای همان trigger و هویت‌های لازم بررسی می‌کند. برای `PlateRecognition`
کلید شامل پلاک است و چهرهٔ اختیاری وارد آن نمی‌شود؛ برای `PlateFaceMatch` یا
`PlateFaceAssociation` کلید شامل هر دو پلاک و چهره است. اگر رکورد موفق همان تریگر
در بازه وجود داشته باشد، تریگر در `suppressedTriggerIds` قرار می‌گیرد و اگر تنها
نتیجهٔ ارزیابی suppression باشد، رخداد تکراری جدید نیز ساخته نمی‌شود.

`MotionRoiScalePercent` polygon Motion را حول مرکز ROI در بازهٔ `25..300` درصد scale می‌کند. در پیاده‌سازی فعلی با پایان حرکت، Plate tracker به‌صورت صریح پاک نمی‌شود؛ اگر `IdleDetectionFps = 0` باشد inference متوقف می‌شود و tracker تا reset/dispose شدن pipeline باقی می‌ماند.

## 5. قابلیت Plate

مدل منطقی تنظیمات مانند `best.onnx` است. Runtime ابتدا package هم‌نام `.hshmodel` را از `Models/Plate` و سپس از `Models` کنار executable می‌خواند؛ برای سازگاری، مسیر قدیمی `Modules/Plate/Models` نیز بررسی می‌شود. در اجرای Debug/Visual Studio، اگر مدل خارجی پیدا نشود، پوشهٔ `HshDetectionEngin.Plate/Models` نیز به‌عنوان fallback بررسی می‌شود تا build بدون کپی مدل‌ها قابل تست باشد. اگر package پیدا نشود، مسیر خام `Models/<ModelFile>` نیز بررسی می‌شود. فایل‌های مدل هنگام build به‌صورت خودکار کپی نمی‌شوند؛ در Release باید مدل‌های موردنیاز جداگانه کنار executable قرار گیرند.

برای هر ROI، detector روی تصویر mask‌شده اجرا می‌شود. detectionها deduplicate می‌شوند، tracker برای پلاک `TrackId` می‌سازد و OCR کاراکترهای داخل کادر را به ترتیب پلاک ایرانی تبدیل می‌کند. در preprocessing غیر `None`، crop پلاک دوباره برای خواندن کاراکترها پردازش می‌شود.

پلاک پذیرفته‌شده باید confidence کافی و فرمت معتبر ایرانی `NNLNNNNN` داشته باشد؛ یعنی دو رقم، یک حرف فارسی و پنج رقم، در مجموع ۸ کاراکتر. مقدار `Accepted=false` برای متن نامعتبر authoritative است و چنین پلاکی وارد event history، trigger یا ارسال client نمی‌شود؛ فقط می‌تواند به‌صورت کاندید قرمز برای بازخورد تصویری overlay دیده شود. Overlay حدود 2.5 ثانیه باقی می‌ماند. deduplication داخلی history دوربین برای متن یکسان در همان ROI و processing item کمتر از 5 ثانیه انجام می‌شود؛ UI ثبت دوبارهٔ همان متن را برای همان ROI و processing item همان دوربین تا 30 ثانیه suppress می‌کند. Runtime فقط ۱۵ رخداد تازهٔ هر دوربین/قابلیت را در حافظه نگه می‌دارد؛ موارد خارج‌شده در `history-archive.json` کنار executable با crop تصویر ذخیره می‌شوند و هنگام بازسازی history همراه رخدادهای حافظه تا سقف ۱۰۰ کارت نمایش داده می‌شوند.

## 6. قابلیت Face

`FaceModule` تنظیمات typed را دریافت می‌کند، مسیر مدل‌ها را resolve می‌کند و `FacePipeline` را با feature لایسنس `Face` می‌سازد؛ enrollment نیز از همین ماژول استفاده می‌کند و factory جداگانه‌ای در `CameraRuntime` ندارد. ترتیب اجرا:

1. اجرای `FacePreprocessor` اختیاری پیش از YuNet با `None`، `Standard` یا `Advanced`؛ مقدار پیش‌فرض `None` است.
2. تشخیص اندازهٔ مربع ورودی مدل از metadata و resize تصویر ROI به همان اندازه؛ مدل‌های فعلی YuNet ورودی ثابت `640×640` دارند.
3. inference YuNet روی CPU.
4. بازگرداندن bounds و landmarkها به مقیاس ROI اصلی.
5. tracker مبتنی بر IoU.
6. در صورت فعال‌بودن SFace و database، هم‌ترازسازی crop با پنج landmark و تبدیل به `112×112`.
7. تبدیل crop به RGB و ارسال tensor float با مقادیر پیکسلی خام به SFace، دریافت embedding و مقایسهٔ cosine similarity. مقایسهٔ cosine نرمال‌سازی ریاضیِ دو بردار را انجام می‌دهد، اما در مسیر فعلی نرمال‌سازی جداگانهٔ ورودی یا L2-normalize کردن embedding انجام نمی‌شود.
8. انتخاب هویت نام‌دار در صورت عبور از `FaceRecognitionThreshold`؛ در غیر این صورت تطبیق با افراد ناشناس ذخیره‌شده با `FaceUnknownMatchThreshold` انجام می‌شود. اگر تطبیق پیدا نشود، یک شخص با نام اولیهٔ `Unknown #NNNN` در database ساخته و crop/embedding آن ذخیره می‌شود. برای جلوگیری از ثبت یک فریم در هر بار inference، از یک شخص ناشناس حداکثر هر 10 ثانیه یک نمونهٔ جدید و تا سقف 10 نمونه ذخیره می‌شود.

UI مقدار `InputSize` را از catalog مدل می‌گیرد و فقط اندازه‌های اعلام‌شدهٔ همان مدل را در ComboBox نشان می‌دهد؛ مدل‌های فعلی YuNet مقدار `640` را اعلام می‌کنند. `Threads` در `SessionOptions.IntraOpNumThreads` برای sessionهای YuNet و SFace تنظیم می‌شود و دیگر به `CvInvoke.NumThreads` سراسری وابسته نیست.

`FacePreprocessing` یک گزینهٔ عمومی برای آماده‌سازی تصویر detector است و پیش‌پردازش اختصاصی SFace محسوب نمی‌شود. حالت `Advanced` تصویر را grayscale و equalize می‌کند و در پیاده‌سازی فعلی همان تصویر آماده‌شده به مسیر alignment/embedding نیز می‌رسد؛ به همین دلیل هنگام فعال‌بودن recognition مقدار `None` توصیه می‌شود. پیش‌فرض‌های شناسایی SFace عبارت‌اند از: `FaceRecognitionThreshold = 0.40`، `FaceUnknownMatchThreshold = 0.35` و `FaceProcessingOptions.EventCooldownSeconds = 60`؛ فیلد قدیمی `FaceEventCooldownSeconds` فقط برای سازگاری و migration نگه‌داری می‌شود.

`FaceConfidence` threshold پذیرش detector و `FaceRecordConfidence` threshold ثبت رخداد/رنگ overlay است. Face فقط وقتی وارد tracking، recognition و history می‌شود که هم از `FaceConfidence` عبور کرده باشد و هم `FaceRecordConfidence` را پاس کند؛ بنابراین confidence پایین‌تر از threshold تشخیص، حتی اگر از record threshold بیشتر باشد، در لیست ثبت نمی‌شود. پیش‌فرض هر دو مقدار `0.80` است. تنظیمات موجود در `settings.json` برای حفظ انتخاب کاربر خودکار overwrite نمی‌شوند و در صورت نیاز باید Detection confidence از UI تنظیم شود. Face پایین‌تر از threshold پذیرش، در بازهٔ تصویری مجاز قرمز و Face قابل ثبت سبز نمایش داده می‌شود. متن شامل نام یا Unknown، TrackId و confidence است و زیر کادر قرار می‌گیرد؛ فقط در نزدیکی لبهٔ پایین به بالای آن منتقل می‌شود. برچسب Face با رسم Unicode/GDI+ و فونت `Segoe UI` روی bitmap preview نوشته می‌شود تا نام‌های فارسی به `????` تبدیل نشوند. overlayهای Face پس از حدود 2.5 ثانیه حذف می‌شوند.

`face-database.db` کنار executable نگهداری می‌شود و یک SQLite database واحد برای اطلاعات شخص، نمونه‌های چهره، تصویر crop‌شده و embedding است؛ فایل تصویر جداگانه برای رکوردها استفاده نمی‌شود. هر شخص `PersonNumber` ثابت دارد و هر نمونه `SampleNumber` مستقل؛ برای هر شخص حداکثر 10 نمونه پذیرفته می‌شود. `CreatedAtUtc`، نام فایل ورودی و فرمت نیز در رکورد نمونه نگه‌داری می‌شوند. تصویر enrollment ابتدا با YuNet تشخیص داده، با پنج landmark هم‌تراز و سپس به‌صورت JPEG در BLOB ذخیره می‌شود؛ تصویر بدون چهره یا چندچهره‌ای قابل ثبت نیست. ورود پوشه‌ای همهٔ فایل‌های تصویری را پردازش می‌کند و بعد از رسیدن به سقف 10 نمونه، موارد باقی‌مانده را گزارش می‌دهد. چهرهٔ ناشناس جدید در مسیر دوربین به‌صورت شخص `Unknown #NNNN` در database دائمی ثبت می‌شود و در اجراهای بعدی نیز با `FaceUnknownMatchThreshold` قابل تطبیق است؛ پس از تغییر نام، `IsUnknown` خاموش می‌شود و نمونه‌ها در شناسایی افراد نام‌دار نیز استفاده می‌شوند. برای جلوگیری از پرشدن سریع database، از یک فرد ناشناس حداکثر هر 10 ثانیه یک نمونهٔ جدید ذخیره می‌شود. مقایسهٔ database بین نمونه‌ها بر اساس cosine similarity embedding انجام می‌شود و UI نمونه‌های مشابه را کنار هم نشان می‌دهد، امکان ادغام دو شخص را دارد و تصاویر جفت‌ها را همراه گزارش CSV به پوشهٔ انتخاب‌شده export می‌کند. `FaceDatabase.Save(null)` یا `Save` با مسیر خالی، database پیش‌فرض کنار application را checkpoint می‌کند؛ مسیر غیرخالی به فایل `.db` مقصد export می‌شود. برای مهاجرت، اگر `face-database.db` وجود نداشته باشد، `face-database.json` قدیمی خوانده می‌شود؛ رکوردهای قدیمی که تصویر ندارند با وضعیت تصویر گمشده حفظ می‌شوند.

## 7. تنظیمات و سازگاری

`settings.json` کنار executable است. ریشه شامل `Cameras` و `SelectedCameraId` است. هر `CameraSettings` مستقل است.

| حوزه | فیلدهای مهم |
| --- | --- |
| اتصال | `Id`، `Name`، `SourceUrl`، `Transport`، `BufferCount`، `ReconnectDelaySec` |
| قابلیت‌ها و schema | `ProcessingSchemaVersion`، `PlateEnabled`، `FaceEnabled`، `Processing` legacy |
| Plate | `Options: PlateProcessingOptions` در `Rois[].Processing[]` به‌همراه `MaxFps` و `Threads` |
| Face | `Options: FaceProcessingOptions` در `Rois[].Processing[]` به‌همراه `MaxFps` و `Threads` |
| Motion و UI | `DrawBoxes`، `DetectionOverlayHoldMs`، `MotionGateEnabled`، `MotionFps`، `MotionThreshold`، `MotionChangedPercent`، `MotionRoiScalePercent`، `MotionHoldMs`، `ActiveDetectionFps`، `IdleDetectionFps` |
| ROI و پردازش | `Rois[].Name`، `Rois[].Enabled`، `Rois[].Points`، `Rois[].Processing[]` و `RoiEnabled` |

در schema فعلی، `ProcessingSchemaVersion = 3` است و هر پردازش باید داخل `Rois[].Processing[]` قرار بگیرد. هر `CameraProcessingSettings` علاوه بر `Id`، `Type`، `Name`، `Enabled` و تنظیمات مشترک `MaxFps`/`Threads`، فقط `Options` متعلق به همان ماژول را نگه می‌دارد. `CameraSettings.Processing` فقط فیلد legacy برای migration است و در فایل جدید باید خالی باشد. هنگام ساخت آیتم جدید، مقدارهای camera-level به‌عنوان default clone می‌شوند؛ پس از ایجاد، آیتم مرجع مستقل runtime است و تغییرات camera-level یا آیتم‌های دیگر آن را overwrite نمی‌کنند. فایل‌های flat قدیمی هنگام Load به options typed مهاجرت می‌شوند.

نمونهٔ قابل ویرایش در `settings.optimized.example.json` است. UI فهرست نوع‌ها و descriptor تنظیمات را از `ProcessingRegistry` می‌گیرد؛ بنابراین برای قابلیت جدید branch مربوط به Plate/Face در `CameraSettingsForm` لازم نیست. Composition Root باید registration قابلیت جدید را اضافه کند و فقط در صورت نیاز به editor اختصاصی، `EditorKey` و wiring همان editor تغییر می‌کند.

### رفتار رابط کاربری اصلی

- برنامه با عنوان `Camera Management` (در فارسی: «مدیریت دوربین‌ها») به‌صورت maximized و با حداقل اندازهٔ ۹۶۰×۶۲۰ اجرا می‌شود. نوار بالایی شامل دکمه‌های آیکونی `Cameras`، `Save`، `Face database`، `Start All`، `Stop All`، `Thumbnails` و تغییر زبان است؛ متن عملکرد هر دکمه در Tooltip قرار دارد و دکمهٔ `Thumbnails` فقط در نمای بزرگ دوربین ظاهر می‌شود. UI فارسی و انگلیسی دارد؛ انتخاب زبان در `ui-language.txt` کنار executable ذخیره می‌شود و برای اعمال کامل، برنامه restart می‌شود. در فارسی فونت رابط `Tahoma` با اندازهٔ پیش‌فرض ۸ و در انگلیسی `Segoe UI` با اندازهٔ فعلی استفاده می‌شود؛ تیترها و کنترل‌های اختصاصی می‌توانند اندازهٔ خود را حفظ کنند؛ مقادیر فنی مانند `TCP`، `FFmpeg`، نام مدل‌ها و کلیدهای JSON ترجمه نمی‌شوند.
- دکمهٔ `Cameras` فرم مستقل و modeless به نام `CameraManagerForm` را باز می‌کند. این فرم دکمهٔ `Add camera` و جدول نام، source، status، FPS و دکمه‌های Start/Stop، Edit و Delete را نشان می‌دهد و عملیات آن را از طریق callback به runtimeهای `MainForm` می‌سپارد. بالای هر tile در نمای چنددوربینه نیز نوار وضعیت و دکمه‌های Start/Stop، Edit و Delete قرار دارد. کلیک روی ردیف، tile، تصویر یا status دوربین را انتخاب می‌کند؛ دوبارکلیک روی tile، تصویر یا status نمای همان دوربین را بزرگ می‌کند.
- شبکهٔ preview در نمای چنددوربینه بر اساس تعداد دوربین‌ها ۱ ستونه برای یک دوربین، ۲ ستونه تا چهار دوربین، ۳ ستونه تا ۹ دوربین و ۴ ستونه برای تعداد بیشتر است. در نبود دوربین، کد پیام `No cameras configured` و راهنمای قدیمی `Click '+ Add camera' to create the first camera.` را نمایش می‌دهد؛ با توجه به رابط فعلی، افزودن دوربین عملاً از طریق دکمهٔ `Cameras` و سپس `Add camera` انجام می‌شود. عملیات `Start`، `Stop`، `Edit` و `Delete` روی هر tile با دکمه‌های آیکون‌محور کم‌عرض انجام می‌شوند و توضیح آن‌ها در Tooltip قرار دارد.
- محوطهٔ کاری دو ستون دارد: ستون چپ برای preview و ستون راست با عرض ثابت ۳۹۰ پیکسل برای `Detected events`. بنابراین نسبت ۷۲/۲۸ درصدی بخشی از قرارداد UI نیست. رخدادها به‌صورت کارت شامل تصویر، دوربین، label، confidence و زمان نمایش داده می‌شوند و history UI حداکثر ۱۰۰ کارت دارد. نوار وضعیت نیز state، FPS، زمان inference، resolution و dropped frames را نشان می‌دهد.
- پنل ROI در نمای چنددوربینه مخفی است. هنگام بزرگ‌نمایی، پنل کامل ROI بلافاصله در ستون راست و زیر `Detected events` با ارتفاع حدود ۲۳۰ پیکسل باز می‌شود؛ دکمهٔ آیکنِ تنها برای بازکردن پنل وجود ندارد و پنل نباید روی تصویر دوربین overlay شود. دوبارکلیک تصویر بزرگ یا دکمهٔ `Thumbnails` به نمای چنددوربینه برمی‌گردد و پنل ROI را مخفی می‌کند.
- پنل ROI شامل فهرست ROI و پنج کنترل افزودن، ویرایش نقاط، تغییر نام، حذف و پاک‌کردن همه است. نوار این پنج دکمه به‌صورت افقی و با `DockStyle.Left` چیده می‌شود؛ اندازهٔ هر دکمه `30×27`، فونت `Segoe UI` با اندازهٔ 7.5 و حالت Bold و تراز آیکون `MiddleCenter` است و آیکون‌ها از طریق ویژگی `Button.Image` تنظیم می‌شوند تا با دکمه‌های عملیات کارت دوربین هماهنگ باشند. نام ROI در همان دوربین باید یکتا باشد. حالت ویرایش با یک polygon خالی شروع می‌شود؛ با کلیک چپ روی تصویر نقاط جدید اضافه و با کلیک راست آخرین نقطه حذف می‌شود، کلیک خارج از ناحیهٔ واقعی تصویر letterbox نادیده گرفته می‌شود، مختصات در `0..1` محدود می‌شوند و پایان ویرایش حداقل سه نقطه لازم دارد. polygon جدید جایگزین نقاط قبلی همان ROI می‌شود.
- پنجرهٔ تنظیمات دوربین modal و دارای تب‌های `General` و `Processing` است. `General` تنظیمات نام، source، transport، `CaptureBackend`، reconnect، رسم overlay، Motion Gate و نرخ‌های Active/Idle را دارد. `CaptureBackend` بین `FFmpeg` (پیش‌فرض)، `LibVLC` و `MediaMTX` انتخاب می‌شود؛ `LibVLC` برای RTSPهایی است که در VLC پایدارتر از OpenCV FFmpeg هستند و به VLC 3.x x64 نصب‌شده یا `VLC_HOME` نیاز دارد و `MediaMTX` path مستقل و WHEP خام مرورگر را فراهم می‌کند. در `Processing` درخت ROIها و آیتم‌های Plate/Face زیر هر ROI دیده می‌شود؛ ترتیب درخت ترتیب اجراست و انتخاب هر ROI یا آیتم، property editor مربوط به همان موجودیت را نشان می‌دهد. هر آیتم model، input size و پارامترهای مستقل خود را دارد؛ تنظیمات Face در زمان اجرای pipeline، overlay و history از همان آیتم خوانده می‌شوند. کنترل Threads در بخش‌های Plate و Face برای آیتم انتخاب‌شده sync است، درحالی‌که Buffer count در سطح دوربین مشترک است. تنظیمات سطح دوربین فقط default ساخت آیتم جدید هستند. ROI بدون آیتم inference اجرا نمی‌کند.
- `Save` تنظیمات پنجره را پس از اعتبارسنجی نام و source و یکتا بودن نام ROIها در همان دوربین اعمال می‌کند و `Cancel` تغییرات را کنار می‌گذارد. ویرایش دوربین روی clone تنظیمات انجام می‌شود تا تغییرات قبل از تأیید به runtime اعمال نشوند. فهرست مدل‌ها از packageهای `.hshmodel` در `Models` کنار executable تغذیه می‌شود؛ در اجرای Debug، پوشه‌های مدل پروژه نیز برای تست شناسایی می‌شوند.
- پنجرهٔ `Face database` گرید هر نمونه را با crop، شمارهٔ شخص، نام، نوع، شمارهٔ نمونه، confidence تشخیص، تاریخ، فایل منبع و وضعیت `Ready` یا `Image missing` نشان می‌دهد. ستون نام با دوبارکلیک یا `F2` مستقیماً قابل ویرایش است؛ خروج از سلول نام را پس از کنترل خالی‌نبودن و یکتا بودن در SQLite ذخیره می‌کند و نام همهٔ نمونه‌های همان شخص را تغییر می‌دهد. `Group by person` برای هر شخص یک ردیف سرگروه با علامت `-`/`+` ایجاد می‌کند و با کلیک، نمونه‌های آن گروه را جمع یا باز می‌کند. `Move selected sample` نمونهٔ موجود، ازجمله نمونهٔ ناشناس، را به شخص نام‌دار مقصد منتقل می‌کند و `Add to selected person` برای فایل تصویر جدید است. عملیات افزودن تصویر برای شخص جدید، import پوشه، حذف نمونه، حذف شخص و rename شخص در همین پنجره‌اند. این پنجره و پنجرهٔ Similarity نیز با زبان انتخاب‌شده نمایش داده می‌شوند.
- دکمهٔ `Restore Defaults` در فرم تنظیمات دوربین سه پروفایل `Weak / virtual 6-core`، `Balanced / normal system` و `High performance / realtime` دارد. پروفایل حداقل، مدل‌های INT8 موجود برای Plate و Face را نیز انتخاب می‌کند و در صورت نبود مدل در بسته fallback دارد. پروفایل High Performance دقیقاً ۴ thread استفاده می‌کند. پروفایل‌ها FPS، threads، input size، Face `TopK`، motion و buffer را برای دوربین و processing itemهای ROI تنظیم می‌کنند، thresholdهای تشخیص/شناسایی را تغییر نمی‌دهند و برای ذخیره باید `Save` زده شود.
- نسخهٔ وب همین جریان عملیاتی را در `DetectionManagerUi` ارائه می‌کند؛ قرارداد دقیق آن در [WEB-UI-RECONSTRUCTION-SPEC.md](WEB-UI-RECONSTRUCTION-SPEC.md) است: پنل `Detected events` تشخیص را با crop و جزئیات متنی نشان می‌دهد، tile فقط Start/Stop/Edit/Fullscreen دارد، پنل `وضعیت runtime` زیر layout زندهٔ داشبورد قرار دارد و نمای متمرکز ذره‌بین فقط workspace میانی را با تصویر دوربین جایگزین می‌کند. ابزارهای ROI (`ویرایش`، `ROI جدید`، `حذف`، `ذخیره`، `لغو` و بازگشت) حالت خودکار edit ندارند. در حالت MediaMTX، تصویر WHEP خام است و Drawingهای ROI/تشخیص با SVG Overlay سمت کلاینت می‌آیند.
- پنجرهٔ `Similar face samples` threshold پیش‌فرض `0.40`، هماهنگ با threshold شناسایی SFace، و گزینهٔ `Only different people` که به‌صورت پیش‌فرض خاموش است دارد. در حالت پیش‌فرض جفت‌های مشابه متعلق به یک `PersonId` نیز نمایش داده می‌شوند؛ با فعال‌کردن گزینه فقط افراد متفاوت مقایسه می‌شوند. برای هر جفت similarity و اطلاعات دو نمونه نمایش داده می‌شود؛ ادغام شخص دوم در اول فقط تا سقف ۱۰ نمونه انجام می‌شود. export پوشهٔ `FaceSimilarity_yyyyMMdd_HHmmss`، زیرپوشهٔ `Pairs` و فایل `similarity-report.csv` می‌سازد.

## 8. مدل‌ها، package و انتشار

| قابلیت | فایل‌های package در پروژه | مسیر runtime |
| --- | --- | --- |
| Plate | `HshDetectionEngin.Plate/Models/*.hshmodel` | Release: `Models/Plate` یا `Models` کنار executable؛ Debug: پوشهٔ مدل پروژه نیز fallback است |
| Face detection | `face_yunet_2023mar.hshmodel` و `face_yunet_2023mar_int8.hshmodel` | Release: `Models/Face` یا `Models` کنار executable؛ Debug: پوشهٔ مدل پروژه نیز fallback است |
| Face recognition | `face_recognition_sface_2021dec.hshmodel` | Release: `Models/Face` یا `Models` کنار executable؛ Debug: پوشهٔ مدل پروژه نیز fallback است |

مدل خام و ابزار تبدیل فقط برای توسعه در `HshDetectionEngin.Tools/RawModels` و `HshDetectionEngin.Tools/ModelTools` هستند. `HshDetectionEngin.Tools/BuildArtifacts` خروجی‌های آزمایشی/قدیمی را نگه می‌دارد و جزو runtime نیست.

package فعلی مدل header `HSHM0001`، IV شانزده‌بایتی و ciphertext AES دارد. loader مدل رمزگشایی‌شده را برای ساخت session موقتاً در temp می‌نویسد و سپس حذف می‌کند. کلید AES از `HSH_DETECTION_LICENSE` یا fallback توسعه می‌آید. این سازوکار با لایسنس RSA مستقل است و تنها مانع استخراج ساده است، نه حفاظت مطلق در برابر شخص دارای کنترل کامل دستگاه.

## 9. لایسنس و ابزارهای عملیاتی

`license.hshlic` شامل payload JSON Base64شده و امضای RSA-SHA256 است. `LicenseClaims` شامل `Version`، `LicenseId`، `CustomerName`، `MachineId`، `NotBeforeUtc`، `ExpiresUtc` و featureهای `Plate`/`Face` است. Runtime public key کامپایل‌شده در `HshDetectionEngin.Licensing/Licensing.cs`، مقدار `LicenseValidator.PublicKeyPem`، را بررسی می‌کند و MachineId و بازهٔ اعتبار را validate می‌کند.

`LicenseRequest` روی دستگاه مشتری fingerprint را از `HKLM\\SOFTWARE\\Microsoft\\Cryptography:MachineGuid` (یا fallback نام دستگاه/OS) می‌سازد و درخواست Base64 را به‌صورت کد یا فایل `.hshrequest` ذخیره می‌کند. این درخواست راز نیست.

`LicenseIssuer` فقط باید روی سیستم امن صادرکننده اجرا شود:

1. در فرم اصلی مشتری بسازید یا ویرایش کنید: نام شرکت، نام رابط، تلفن شرکت، موبایل رابط، آدرس و توضیحات.
2. مشتری را انتخاب و `Manage licenses` را باز کنید.
3. درخواست مشتری، تاریخ انقضا و featureها را وارد کنید؛ private key ذخیره‌شدهٔ قبلی خودکار استفاده می‌شود و در صورت نیاز قابل تغییر است.
4. `Issue and archive license` فایل را در `LicenseArchive/<CustomerId>/` می‌سازد و metadata را در `customers.json` ثبت می‌کند.
5. از جدول archive می‌توان فایل انتخاب‌شده را با `Save As...` به محل دیگری کپی کرد.

`customers.json`، `issuer-settings.json` (فقط مسیر آخرین private key) و `LicenseArchive` کنار executable Issuer ذخیره می‌شوند. حذف مشتری دارای حتی یک رکورد لایسنس مجاز نیست. `Generate pair...` یک private PEM و public PEM می‌سازد؛ public key جدید را در `LicenseValidator.PublicKeyPem` قرار دهید و همهٔ خروجی‌های محصول را rebuild کنید. با این کار لایسنس‌های امضاشده با کلید قبلی نامعتبر می‌شوند.

کلید خصوصی هرگز نباید همراه مشتری، publish عمومی یا repository عمومی باشد. مسیر پیش‌فرض توسعه ممکن است `HshDetectionEngin.Tools/LicenseKeys/vendor-private.pem` را پیدا کند؛ این فقط برای workstation امن صادرکننده است و نباید به‌عنوان asset محصول توزیع شود.

## 10. Build، اجرا و publish

```powershell
dotnet restore HshVisionLab.sln
dotnet build HshVisionLab.sln -m:1
dotnet run --project HshVisionLab.csproj --no-build

dotnet run --project HshDetectionEngin.LicenseRequest\HshDetectionEngin.LicenseRequest.csproj
dotnet run --project HshDetectionEngin.LicenseIssuer\HshDetectionEngin.LicenseIssuer.csproj

dotnet publish HshVisionLab.csproj -c Release -r win-x64 --self-contained false -o .\publish\win-x64
```

پیش از build Debug، همهٔ برنامه‌های درحال اجرای مربوط را ببندید؛ ویندوز ممکن است `.exe` یا `.dll` خروجی را قفل کند. خروجی مشتری باید کل خروجی `dotnet publish` را شامل `HshVisionLab.exe`، `HshVisionLab.dll`، فایل‌های `.deps.json` و `.runtimeconfig.json`، DLLهای پروژه و dependencyها، DLLهای native Emgu/ONNX، پوشهٔ `Models` حاوی packageهای انتخاب‌شده و `license.hshlic` مشتری داشته باشد؛ فهرست فایل‌ها را به تعداد ثابت DLL محدود نکنید. مدل‌ها باید برای Release جداگانه در `Models/Plate` و `Models/Face` کپی شوند؛ build آن‌ها را خودکار به خروجی منتقل نمی‌کند. fallback مدل‌های داخل پروژه فقط برای اجرای Debug/Visual Studio است و نباید به بستهٔ مشتری وابسته باشد. `license.hshlic` موجود در workspace فقط برای Debug کپی می‌شود؛ برای Release باید لایسنس مشتری را دستی کنار executable بگذارید.

LicenseIssuer، private key، Tools، مدل‌های خام و فایل‌های database داخلی صادرکننده نباید در publish مشتری باشند. LicenseRequest را می‌توان جداگانه برای دریافت درخواست فعال‌سازی به مشتری داد.

## 11. معیار پذیرش بازسازی

- solution شامل همان هشت پروژه باشد و هر سه برنامهٔ WinForms با STA اجرا شوند.
- RTSP، Webcam index `0` و فایل ویدئویی با reconnect کار کنند.
- newest-frame semantics برقرار باشد و preview از 15 FPS بیشتر نشود.
- هر دوربین ROI، Motion، settings و pipelineهای مستقل داشته باشد.
- Plate-only، Face-only و Plate+Face از UI قابل تنظیم باشند.
- Face در هر اندازهٔ ROI بدون خطای `FaceDetectorYN.InputSize` کار کند.
- Unknownهای Face به‌عنوان افراد `Unknown #NNNN` در database دائمی با crop و embedding ذخیره شوند، در اجراهای بعدی برای مشاهدات مشابه قابل تطبیق باشند و cooldown/سقف نمونه رعایت شود.
- UI برای همان متن پلاک در همان دوربین تا 30 ثانیه suppression دارد؛ deduplication داخلی `CameraRuntime.History` برای پلاک یکسان کمتر از 5 ثانیه است. Face بر اساس cooldown تنظیمی و محدودیت track deduplicate می‌شود.
- نبود مدل یا feature یک قابلیت، قابلیت دیگر یا کل دوربین را متوقف نکند.
- LicenseRequest بتواند کد و فایل ذخیره کند؛ LicenseIssuer مشتری، آرشیو، صدور و Save As را مدیریت کند.
- بستهٔ مشتری فاقد private key و مدل خام باشد.

## 12. نقشهٔ مستندات

- [پیکربندی](CONFIGURATION.md): مرجع فیلدها و نمونهٔ JSON.
- [معماری](ARCHITECTURE.md): مرز ماژول‌ها و جریان مسئولیت.
- [مدل و بهینه‌سازی](OPTIMIZATION.md): مدل‌ها و قیود عملکرد/امنیت.
- [عملیات لایسنس](LICENSING.md): گردش کار امن درخواست و صدور.
- [وضعیت پیاده‌سازی](STATUS.md): مرزهای فعلی و backlog آگاهانه.
