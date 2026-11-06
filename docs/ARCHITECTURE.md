# معماری

سند کامل بازسازی در [REBUILD_GUIDE.md](REBUILD_GUIDE.md) است. این فایل مرزهای معماری را کوتاه و صریح نگه می‌دارد.

```text
HshVisionLab (WinForms)
  ├─ HshDetectionEngin.Abstractions  قراردادها و settings
  ├─ HshDetectionEngin               capture، Motion، ROI، runtime و overlay
  ├─ HshDetectionEngin.Plate         YOLO/OCR/tracker/package مدل Plate
  ├─ HshDetectionEngin.Face          YuNet/SFace/tracker/FaceDatabase/package مدل Face
  ├─ HshDetectionEngin.Palm          BlazePalm/RTMDet/CCNet و شناسایی Palm
  ├─ HshDetectionEngin.Identity      دیتابیس مرکزی People و modalityها
  └─ HshDetectionEngin.Licensing     fingerprint و RSA license validation

HshDetectionEngin.LicenseRequest    ابزار مشتری برای درخواست فعال‌سازی
HshDetectionEngin.LicenseIssuer     ابزار امن مدیریت مشتری و صدور/آرشیو لایسنس
```

## مرز قطعی Windows و وب

`HshVisionLab` برنامهٔ مستقل Windows است. این برنامه engineهای داخل solution را reference می‌کند و خودش مسئول اتصال مستقیم به دوربین، capture، inference، ROI، overlay، history و دیتابیس است. `HshVisionLab` نباید هیچ‌وقت به `HshDetectionService`، API، SignalR، WHEP یا Overlay سرویس وابسته شود.

`DetectionManagerUi` برنامهٔ وب مستقل دیگری است که فقط برای `HshDetectionService` ساخته شده است. دوربین، inference، state و دیتابیس این UI متعلق به سرویس است و UI وب تصویر/نتیجه را از API، SignalR و WHEP/Overlay می‌گیرد. `DetectionManagerUi` و `HshVisionLab` هیچ ارتباط runtime یا data-sharing مستقیم با یکدیگر ندارند.

هدف مشترک این دو برنامه، ارائهٔ امکانات مشابه و تجربهٔ کاربری هم‌سطح است؛ این شباهت نباید به اشتراک الگوریتم runtime، process یا مسیر capture تعبیر شود.

## مرز مسئولیت

- UI صاحب نمایش، مدیریت دوربین، ویرایش ROI، history، هماهنگ‌کردن enrollment با `FaceModule` و مدیریت صفحهٔ مرکزی `مدیریت افراد` است؛ ساخت pipelineهای runtime در ماژول‌های پردازشی انجام می‌شود.
- Engine صاحب چرخهٔ دریافت فریم، Motion Gate و lifecycle دوربین است؛ `CameraPipelineCoordinator` ساخت graph پردازش، اجرای pipelineهای هر ROI، انتقال metadata و dispose آن‌ها را جداگانه مدیریت می‌کند. overlay و history هنوز policy خروجی دوربین هستند.
- Abstractions هیچ منطق قابلیت خاص ندارد و قراردادهای پایدار را نگه می‌دارد.
- Plate، Face و Palm منطق domain و مدل خود را نگه می‌دارند؛ runtime مدل هر قابلیت را فقط از packageهای `.hshmodel` در پوشهٔ `Models/<Capability>` کنار executable می‌خواند. Palm نیز مانند Plate و Face فقط package می‌پذیرد و مدل خام ONNX یا مدل نصب‌شدهٔ خارج از قرارداد را اجرا نمی‌کند. پوشهٔ flat `Models` و مسیر قدیمی `Modules/<Capability>/Models` نیز برای سازگاری packageها پشتیبانی می‌شوند.
- Licensing تنها مرجع صدور و اعتبارسنجی license است؛ `HshVisionLab` و ماژول‌های runtime نباید private key را دریافت کنند. `LicenseIssuer` یک UI جداگانه و فقط برای workstation امن صادرکننده است و private key را مصرف می‌کند.
- `IdentityDatabase` مرجع SQLite برای `People`، `PersonPlates`، `FaceSamples` و `PalmSamples` است. هر شخص یک `PersonId` مشترک دارد و هر modality نمونه‌های یک‌به‌چند خود را نگه می‌دارد؛ UI فقط snapshot رکوردها را برای گرید و مدیریت دریافت می‌کند. `FaceDatabase` و `PalmDatabase` adapterهای سازگار با API قبلی هستند.
- آیکون‌های دکمه‌های UI از Material Icons رسمی انتخاب می‌شوند و فونت آن به‌صورت resource داخلی در `Assets/MaterialIcons/MaterialIcons-Regular.ttf` embed شده است؛ در runtime وابستگی به سایت یا اینترنت وجود ندارد.

### چیدمان UI اصلی

این بخش دربارهٔ `HshVisionLab` WinForms است. برای UI وب، سند مستقل
[مشخصات مرجع بازسازی UI وب](WEB-UI-RECONSTRUCTION-SPEC.md) ملاک است؛ وب shell
و routeهای `/`، `/cameras`، `/faces`، `/events`، `/triggers` و `/settings`
دارد و `CameraManagerForm` یا `DockStyle` ندارد.

پنجرهٔ اصلی از یک ناحیهٔ preview در سمت چپ و ستون ثابت ۳۹۰ پیکسلی در سمت راست تشکیل می‌شود. دکمهٔ `Cameras` در نوار بالایی فرم مستقل و modeless `CameraManagerForm` را با `Show(this)` باز می‌کند؛ این فرم فهرست دوربین‌ها و دکمهٔ افزودن دوربین را نمایش می‌دهد و callbackهای آن عملیات runtime را در `MainForm` اجرا می‌کنند. هر tile در نمای چنددوربینه نیز در نوار بالایی خود وضعیت و دکمه‌های `Start/Stop`، `Edit` و `Delete` را دارد. ستون سمت راست همیشه `Detected events` را دارد و فقط در حالت بزرگ‌نمایی یک دوربین، پنل کامل ROI را نیز در ردیف زیر آن نشان می‌دهد؛ پنل آیکنِ تنها نیست و روی تصویر overlay نمی‌شود.

در پیاده‌سازی فعلی، `MainForm` هنوز گرید داخلی قدیمی (`_cameraGrid`) و پنل مرتبط با آن را در زمان ساخت ایجاد می‌کند تا منطق انتخاب و به‌روزرسانی state موجود حفظ شود؛ این پنل از layout قابل مشاهده حذف شده و در رابط کاربر نمایش داده نمی‌شود. فهرست قابل مشاهده فقط گرید `CameraManagerForm` است.

## جریان پردازش

```text
Source → frame slot/queue
       ├─ latest-preview slot → preview worker مستقل (≤15 FPS)
       └─ CameraRuntime processing worker
            → Motion روی Motion ROIها → وضعیت Active/Idle مشترک دوربین → کنترل نرخ inference
            → CameraPipelineCoordinator: registry → استخراج و ماسک ROIها
            → اجرای موازی ROIها و اجرای taskهای هر ROI طبق ProcessingMode
            → detection / overlay / history / UI event
```

Motion Gate پیش از اجرای Coordinator ارزیابی می‌شود. آشکارسازهای حرکت ROIها مستقل‌اند، اما نتیجهٔ آن‌ها با OR ترکیب می‌شود؛ حرکت در هر Motion ROI کل دوربین را Active می‌کند و در نوبت inference همهٔ ROIهای معتبر دارای پردازش فعال اجرا می‌شوند. مسیر preview مستقل ادامه دارد. ماسک ورودی پایپ‌لاین‌ها در Coordinator از ماسک مورد استفاده برای سنجش Motion جداست.

در Windows، مسیر ثبت رخداد از مسیر پردازش تصویر جداست: `MainForm` نتیجهٔ هر دوربین
را به `DesktopHistorySubscriptionMatcher` می‌دهد و فقط وقتی profile آن دوربین/ROI
و محور/requiredها را پاس کند، رکورد نهایی را به `DesktopEventStore` می‌فرستد.
برای requiredهای ترکیبی، component خام در رکورد staging نگه‌داری می‌شود؛ مثلاً
`Mode=Plate` با `FaceRequired=true` و `PlateRequired=true` تا زمان pair شدن در
`WindowMs` صبر می‌کند و سپس یک رکورد `PlateFace` می‌سازد. staging هرگز در query،
پنل تشخیص یا تاریخچهٔ Windows نمایش داده نمی‌شود. پنل تشخیص و بازسازی تاریخچه از
رکوردهای نهایی Event Store می‌خوانند، در حالی که `TriggerDefinition` مسیر مستقل
ارسال payload کامل موجود به کلاینت است و `includePalm` را نیز حفظ می‌کند.

هر دوربین یک `CameraRuntime`، یک `FrameSource`، setting، state Motion و یک `CameraPipelineCoordinator` مستقل دارد. reader مربوط به RTSP فقط فریم را دریافت و در slotهای latest-frame می‌گذارد؛ ساخت preview، resize، overlay و callback UI در worker مستقل و با سقف ۱۵ FPS انجام می‌شود تا کندی UI یا تبدیل `Bitmap` خواندن stream را متوقف نکند. `FrameSource` با `BufferCount = 0` فقط جدیدترین فریم را نگه می‌دارد؛ مقدار مثبت صف محدود همان دوربین را فعال می‌کند.

## مسیرهای capture و کلاینت وب

Backend دوربین با `CaptureBackend` انتخاب می‌شود:

```text
FFmpeg:  Camera RTSP → FrameSource/FFmpeg → CameraRuntime
LibVLC:  Camera RTSP → VlcFrameSource/LibVLC → CameraRuntime
MediaMTX:
         Camera RTSP → MediaMTX path → local RTSP/FrameSource → CameraRuntime
                              └──────→ WHEP خام → Browser
```

در حالت MediaMTX، مسیر WHEP خام برای ویدئوی مرورگر از مسیر ورودی موتور تشخیص
جداست. مرورگر تصویر را مستقیماً از MediaMTX می‌گیرد و endpoint
`/api/v1/streams/{cameraId}/overlay` را برای وضعیت سبک ROI و Drawingهای پویا
poll می‌کند. نمای متمرکز با فاصلهٔ پیش‌فرض 180ms و thumbnailهای داشبورد با
فاصلهٔ 400ms درخواست بعدی را، پس از پایان درخواست قبلی، زمان‌بندی می‌کنند.
Overlay فعال در کلاینت با `LiveOverlaySvg` روی `<video>` رسم می‌شود؛ بنابراین نمایش زندهٔ
MediaMTX دوباره از FFmpeg، Bitmap یا encoder کامپوزیت‌شده عبور نمی‌کند.

داشبورد thumbnailها را در صفحه‌های حداکثر شش‌تایی نشان می‌دهد و فقط tileهای
صفحهٔ جاری را mount می‌کند. با تغییر صفحه، WHEP و polling tileهای قبلی با
unmount شدن متوقف می‌شود. `RawMediaMtxStream` و `SnapshotImage` همچنین به
`visibilitychange` مرورگر واکنش می‌دهند و هنگام hidden بودن تب، session یا
timer فعال نگه نمی‌دارند. این محدودسازی فقط مصرف نمایش در UI/API است؛ دوربینی
که در سرویس start شده، مستقل از صفحهٔ UI همچنان capture و inference می‌کند.

`/api/v1/streams/{cameraId}/webrtc/offer` و `WebRtcGateway` برای سازگاری با
کلاینت‌های legacy که خروجی کامپوزیت‌شده می‌خواهند باقی مانده‌اند، اما مسیر اصلی
نمایش MediaMTX در وب نیست. برای FFmpeg/LibVLC، Snapshot آخرین فریم سرویس همچنان
خروجی fallback است.

## قرارداد pipeline

هر قابلیت قابل توسعه باید `IProcessingPipeline` را پیاده‌سازی کند. `ProcessingContext.Image` تصویر محلی ROI است و `SourceBounds` محل آن در فریم اصلی. Runtime فقط offset ROI را به detectionها و overlayهای هندسی اضافه می‌کند؛ pipelineی که ابعاد تصویر را تغییر می‌دهد باید نگاشت مختصات را خودش حفظ کند. اگر pipeline تصویر بعدی برمی‌گرداند، Runtime با `PipelineResult.TakeNextImage()` مالکیت آن را منتقل می‌کند. `AnalysisDetection` برای نتیجهٔ معنایی مانند Face/Plate و `ProcessingOverlay` برای رسم هندسهٔ بصری مانند polyline، polygon، point، circle و rectangle است؛ overlay وارد history یا event تشخیص نمی‌شود.

 `NamedRoi.Processing` فهرست آیتم‌های هر ROI و `NamedRoi.ProcessingMode` نحوهٔ اجرای آن‌ها را نگه می‌دارد. ROIهای فعال یک دوربین به‌صورت موازی اجرا می‌شوند. مقدار `Sequential` (پیش‌فرض برای سازگاری) taskهای همان ROI را به‌ترتیب فهرست اجرا می‌کند و `PreviousDetections`/`NextImage` را زنجیره می‌کند؛ مقدار `Parallel` هر task را روی یک کپی مستقل از تصویر ROI اجرا می‌کند و زنجیرهٔ بین taskها را فعال نمی‌کند. هر آیتم فیلدهای مشترک `Enabled`، `MaxFps` و `Threads` را دارد و تنظیمات اختصاصی ماژول را در `Options` نگه می‌دارد؛ Plate از `PlateProcessingOptions`، Face از `FaceProcessingOptions` و Palm از `PalmProcessingOptions` استفاده می‌کنند. در UI وب taskها کارت detailed کرکره‌ای و تب‌های detection، recognition/identification و tracking/recording دارند. Palm در WinForms و وب editor اختصاصی detection، identification و tracking دارد و `DetectorKind` از مدل انتخاب‌شده به‌صورت خودکار تعیین می‌شود؛ کنترل جداگانهٔ DetectorKind وجود ندارد. مدل‌های catalog بر اساس قابلیت فیلتر می‌شوند تا ComboBox هر modality مدل نامرتبط نشان ندهد. ماژول گزینه‌های typed خود را هنگام ساخت pipeline یک‌بار از JSON می‌خواند و مسیر `Process()` به JSON یا reflection دسترسی ندارد. `ProcessingModuleDescriptor.OptionsType` قرارداد UI برای deserialize کردن گزینه‌هاست و نوع‌های جدید بدون تغییر فرم، editor عمومی options دریافت می‌کنند؛ `EditorKey` فقط برای انتخاب editor اختصاصی قابلیت‌های موجود است. `CameraProcessingSettings.Type` فقط مقدار serialized سازگار با فایل‌های قدیمی است؛ کد اجرایی باید از `CameraProcessingSettings.Kind` و `ProcessingType` استفاده کند. فیلدهای Face/Plate در `CameraSettings` فقط default سازگار با فایل‌های قدیمی و template ساخت آیتم تازه هستند؛ آیتم موجود از تغییرات بعدی camera default مستقل می‌ماند. دوربین بدون ROI و ROI بدون آیتم پردازش فقط تصویر/overlay ROI را نمایش می‌دهند و inference انجام نمی‌دهند. `ProcessingRegistry` مرجع ثبت قابلیت‌هاست و UI descriptorهای آن را برای نمایش نوع‌ها مصرف می‌کند.

## طراحی حالت سوم: Tracking و تکمیل تشخیص با بافر دوربین

**وضعیت: در برنامهٔ Windows برای Face و Plate پیاده‌سازی شده است؛ اتصال این حالت به API/UI وب هنوز انجام نشده است.** میزبان Windows هنگام ساخت `Camera` گزینهٔ `enableTrackingCompletion` را فعال می‌کند. حالت پیش‌فرض Inline رفتار قبلی را حفظ می‌کند؛ در TrackAndQueue، ROI فقط localization/tracking انجام می‌دهد و `TrackingCompletionWorker` تکمیل را مستقل اجرا می‌کند. میزبان‌های دیگر بدون فعال‌سازی صریح، شروع دوربین دارای این حالت را با پیام روشن رد می‌کنند. Palm مسیر موجود خود را حفظ می‌کند.

### دو محور مستقل اجرای پردازش

`Rois[].ProcessingMode` ترتیب اجرای taskهای یک ROI را تعیین می‌کند و همان `Sequential` یا `Parallel` باقی می‌ماند. حالت سوم جایگزین این فیلد نیست؛ محل انجام مرحلهٔ تکمیل تشخیص را تغییر می‌دهد:

| مسیر | پردازش داخل ROI | اعلام نتیجه |
| --- | --- | --- |
| Inline + Parallel | taskهای ROI مستقل و موازی، همراه recognition/OCR | پس از جمع‌آوری نتایج همان نوبت فریم |
| Inline + Sequential | taskهای ROI به‌ترتیب؛ انتقال `PreviousDetections` و در صورت وجود `NextImage` | پس از جمع‌آوری نتایج همان نوبت فریم؛ مصرف خروجی قبلی بر عهدهٔ خود task است |
| TrackAndQueue | یافتن کادر و landmark لازم، پذیرش اولیه و tracking؛ سپس ارسال نمونه به بافر دوربین | تکمیل هر نمونه به‌صورت ناهمزمان و مستقل از پایان فریم جاری |

ROIها در هر سه مسیر موازی‌اند. در مسیر جدید، ترتیب taskهای مرحلهٔ سریع همچنان تابع `ProcessingMode` است؛ اما task بعدی نمی‌تواند نتیجهٔ recognition در حال انتظار را از `PreviousDetections` دریافت کند. زنجیره‌ای که واقعاً به هویت یا متن نهایی مرحلهٔ قبل نیاز دارد باید Inline بماند؛ اعتبارسنجی تنظیمات باید ترکیب ناسازگار را رد کند.

### جریان اجرای Windows

```text
Camera FrameSource
  ├─ preview مستقل
  └─ Motion Gate و زمان‌بندی دوربین
       └─ پردازش موازی ROIها
            ├─ Face: localization + landmarks → tracking → انتخاب نمونه
            └─ Plate: localization → tracking → انتخاب نمونه
                              ↓
                 مدیریت بافر همان دوربین
                    ├─ Face: ظرفیت و سقف حافظهٔ مستقل
                    └─ Plate: ظرفیت و سقف حافظهٔ مستقل
                 یک نمونهٔ منتظر برای هر کلید کار track
                              ↓
                 یک Completion Worker برای همان دوربین
                    ├─ تعریف تشخیص Face انتخاب‌شده → alignment / SFace / تطبیق هویت
                    └─ تعریف تشخیص Plate انتخاب‌شده → OCR / اعتبارسنجی متن و confidence
                              ↓
                 CompletedDetection با شواهد همان نمونه
                              ↓
                 association / history policy / trigger / event store
                              ↓
                 اعلام رخداد نهایی و تحویل به کلاینت
```

Tracking برای شروع و اصلاح موقعیت همچنان به detector کادر نیاز دارد؛ جداکردن این مراحل به معنی حذف YuNet یا detector پلاک نیست. صرفه‌جویی هدف، کاهش اجرای تکراری SFace/OCR روی مشاهدات متعدد یک track و آزادشدن حلقهٔ ROI از انتظار تکمیل تشخیص است. میزان بهبود باید با اندازه‌گیری مشخص شود.

«یک موتور تکمیل» یعنی یک worker زمان‌بندی و dispatch برای هر دوربین، نه یک مدل مشترک Face/Plate. worker بر اساس شناسهٔ تعریف تشخیص دوربین، recognizer و تنظیمات نهایی آن را انتخاب می‌کند. session هر تعریف مورد استفاده در طول عمر worker نگه‌داری می‌شود و در هر برداشت از بافر دوباره ساخته نمی‌شود. tracking state همچنان متعلق به task و ROI خودش است. یک صف و یک worker سراسری بین همهٔ دوربین‌ها تعریف نمی‌شود.

### تعریف تشخیص‌ها در تنظیمات عمومی دوربین

در تنظیمات عمومی هر دوربین، بخش مستقل «تشخیص‌ها» مانند فهرست پردازش‌های ROI، افزودن، ویرایش، فعال/غیرفعال و حذف آیتم دارد. هر تعریف تشخیص شناسهٔ پایدار، نام، نوع Face یا Plate و Options اختصاصی دارد. افزودن چند تعریف هم‌نوع با مدل/آستانهٔ متفاوت مجاز است؛ انتخاب مقصد از روی نوع به‌تنهایی انجام نمی‌شود.

| محل تنظیم | مسئولیت در TrackAndQueue |
| --- | --- |
| پردازش ROI | مدل localization، confidence کادر، landmark، tracking، نرخ مرحلهٔ سریع و انتخاب تعریف تشخیص مقصد |
| تشخیص عمومی Face | مدل شناسایی SFace، آستانهٔ similarity، آستانهٔ تطبیق Unknown و تنظیمات اجرای recognizer |
| تشخیص عمومی Plate | مدل OCR، آستانه‌های OCR و قواعد پذیرش متن، تنظیمات اجرای recognizer |
| بافر دوربین برای هر نوع نمونه | ظرفیت Face و Plate به‌صورت جدا، سقف حافظه، TTL و سیاست enqueue/retry/refresh هر نوع |

هر task از نوع TrackAndQueue دقیقاً به یک تعریف تشخیص فعال و هم‌نوع در همان دوربین متصل می‌شود. چند ROI می‌توانند از یک تعریف استفاده کنند؛ تولید خودکار چند کار برای تمام تعریف‌های هم‌نوع مجاز نیست. فهرست تعریف‌ها به‌خودی‌خود capture یا tracking تازه‌ای ایجاد نمی‌کند. تعریف بدون producer می‌تواند ذخیره شود؛ recognizer آن تا اتصال یک task فعال ساخته نمی‌شود.

در این مسیر، مدل و آستانهٔ recognition/OCR از تعریف تشخیص عمومی خوانده می‌شوند و مقدارهای قدیمی recognition داخل task ROI بازنویسی یا به‌عنوان fallback پنهان مصرف نمی‌شوند. در Inline تنظیمات موجود task همچنان معتبرند. تعریف دارای فیلدهای `Id`، `Name`، `Enabled`، `Revision`، `Kind`، `ModelFile`، `Confidence`، `UnknownMatchThreshold` و `Threads` است؛ فعلاً Options به‌صورت شیء JSON جداگانه ندارد. تشخیص عمومی و task مبدا دو resource متفاوت‌اند؛ شناسهٔ تشخیص مقصد نباید جای `ProcessingItemId`/`taskId` مبدا در نتیجه را بگیرد.

حذف یا غیرفعال‌کردن تعریفی که task فعال به آن ارجاع دارد باید با نمایش فهرست وابستگی‌ها رد شود تا کاربر ابتدا مقصد taskها را تغییر دهد یا آن‌ها را غیرفعال کند. مقصد ناموجود، نوع ناسازگار یا نبود مدل معتبر پیش از start خطای روشن دارد. ویرایش مدل/آستانه/مقصد با تغییر نسل تنظیمات، پاک‌کردن cache نتیجه و لغو کارهای نسخهٔ قبلی اعمال می‌شود؛ نمونهٔ قبلی با تنظیمات جدید بی‌صدا تکمیل نمی‌شود.

### طرح UI ویندوز و نمایش ظرفیت‌ها

در فرم تنظیمات دوربین، بخش «تشخیص‌ها» دکمهٔ «افزودن تشخیص» و فهرست/کارت‌های نام، نوع، مدل، وضعیت، ویرایش و حذف دارد. فرم افزودن ابتدا نوع Face/Plate را می‌گیرد و editor متناسب با آن را نشان می‌دهد؛ فهرست مدل‌ها از catalog همان قابلیت پر می‌شود. در editor پردازش ROI، انتخاب TrackAndQueue کنترل «تشخیص مقصد» را با فهرست تعریف‌های هم‌نوع همان دوربین نمایش می‌دهد. آستانهٔ detector در ROI باقی می‌ماند و مدل/آستانهٔ recognition در editor تشخیص عمومی تنظیم می‌شود.

بخش «بافر نمونه‌ها» برای Face و Plate ردیف مستقل با editor ظرفیت، حافظه، TTL و retry دارد. پیش‌فرض Windows، Face با ظرفیت ۳۲ و Plate با ظرفیت ۶۴ نمونهٔ منتظر است. این اعداد جمع نمی‌شوند تا یک ظرفیت قابل مصرف توسط هر دو نوع بسازند. چند تعریف Face از سهم Face و چند تعریف Plate از سهم Plate استفاده می‌کنند؛ افزودن تعریف تشخیص هم‌نوع ظرفیت آن نوع را افزایش نمی‌دهد.

روی کارت دوربین، pending/capacity و وضعیت پردازش هر نوع جدا نمایش داده می‌شود؛ مثلاً «چهره: ۸/۳۲، در حال پردازش: ۱» و «پلاک: ۱۲/۶۴، در حال پردازش: ۰». مجموع in-flight برای worker دوربین حداکثر ۱ است. جدول جزئیات بافر نوع نمونه، ROI، track، تشخیص مقصد، سن نمونه و تعداد تلاش را نشان می‌دهد. ظرفیت فقط pending را می‌شمارد؛ in-flight جدا گزارش می‌شود و حافظهٔ آن در سقف همان نوع حساب می‌شود. UI وب باید همین تنظیمات و معانی را از API سرویس ارائه کند.

### قرارداد نمونه و مالکیت تصویر

کلید track باید حداقل `(CameraId, RuntimeGeneration, RoiId, ProcessingItemId, Kind, TrackId)` باشد. کلید کار با افزودن `RecognitionDefinitionId` به کلید track ساخته می‌شود؛ cache و dedup completion به تعریف مقصد و نسخهٔ آن وابسته‌اند. `RuntimeGeneration` در start/reconnect/reset/rebuild تغییر می‌کند تا استفادهٔ دوباره از شمارهٔ track با هویت قبلی اشتباه نشود.

هر نمونه باید این اطلاعات را به‌صورت snapshot مستقل حمل کند:

- `SampleId`، کلید track، `RecognitionDefinitionId`، نسخهٔ تعریف تشخیص/تنظیمات و مرجع recognizer سازگار با آن نسخه.
- شماره و زمان دریافت واقعی فریم منبع، ابعاد اصلی، هندسهٔ ROI و bounds محلی و سراسری detection.
- crop مستقل، confidence آشکارساز، کیفیت/اندازهٔ نمونه و برای Face، landmarkهای تبدیل‌شده به مختصات crop.
- مرجع دارای مالکیت به فریم خام همان نمونه، در صورت نیاز سیاست artifact؛ چند crop از یک فریم می‌توانند یک مرجع مشترک با شمارش مالکیت داشته باشند.

borrow کردن `Mat` حلقهٔ capture بعد از پایان callback مجاز نیست. بافر مالک clone/crop یا reference دارای طول عمر مشخص است؛ replacement، eviction، expiry، خطا، stop و اتمام کار باید منابع را آزاد کنند. هزینهٔ فریم‌های خام مشترک و نمونهٔ در حال پردازش نیز باید در سقف حافظه حساب شود.

در نسخهٔ Windows، crop هر نمونه مستقل است و فریم خام نمونه‌های پذیرفته‌شده در یک نوبت Offer فقط یک‌بار clone می‌شود و با reference count بین آن‌ها به اشتراک می‌رود. سهم حافظهٔ هر نمونه هزینهٔ کامل فریم را به‌صورت محافظه‌کارانه شارژ می‌کند؛ بنابراین ظرفیت حافظه یک سقف امن است و عدد شارژشده ممکن است از حافظهٔ فیزیکی واقعی بیشتر باشد. `RuntimeInstanceId` بین workerهای ساخته‌شده در startهای مختلف تمایز می‌گذارد و `RuntimeGeneration` اتصال مجدد/ابطال کارها را در همان worker مشخص می‌کند.

نتیجهٔ worker نباید تصویر، شمارهٔ فریم یا زمان خود را از latest-frame دوربین بخواند. همان نمونهٔ پذیرفته‌شده باید crop و artifact رخداد نهایی را تأمین کند. `OccurredAtUtc` زمان مشاهدهٔ نمونه است؛ زمان تکمیل جدا ثبت می‌شود. Association بر مبنای زمان و فریم مشاهده انجام می‌شود، نه ترتیب پایان کار worker.

### سیاست بافر و زمان‌بندی

1. بافر متعلق به دوربین است، ولی هر نوع نمونه سهم مستقل دارد: Face و Plate هرکدام ظرفیت pending و سقف حافظهٔ خود را دارند. ظرفیت خالی یک نوع توسط نوع دیگر قرض گرفته نمی‌شود؛ شمارنده‌ها، TTL و سیاست retry هر نوع جدا هستند. producer دوربین برای فضای خالی منتظر نمی‌ماند. سقف‌های حافظهٔ نوع‌ها باید هنگام تنظیم با بودجهٔ کل حافظهٔ بافر دوربین سازگار باشند؛ فریم مشترک در سهم هر نوع مصرف‌کننده به‌صورت محافظه‌کارانه حساب و در مصرف واقعی دوربین فقط یک بار شمرده می‌شود.
2. برای هر کلید کار track حداکثر یک نمونهٔ منتظر و یک نمونهٔ در حال پردازش وجود دارد. مشاهدات تازه نمونهٔ منتظر را با نمونهٔ مناسب‌تر جایگزین می‌کنند؛ confidence/کیفیت بالاتر اولویت دارد و در کیفیت برابر، نمونهٔ جدیدتر انتخاب می‌شود. پس از انقضای نمونهٔ بهتر قدیمی، نمونهٔ تازه ملاک است.
3. replacement جای track را در صف عقب نمی‌اندازد. انتخاب کار بین trackها و Face/Plate باید منصفانه باشد تا یک track پرتکرار، بقیه را گرسنه نگذارد. worker در هر لحظه فقط یک نمونه را تکمیل می‌کند.
4. فاصلهٔ حداقل enqueue و retry برای هر track اعمال می‌شود. نمونهٔ منقضی یا متعلق به نسل/تنظیمات قدیمی پیش از اجرا و پیش از انتشار نتیجه کنار گذاشته می‌شود.
5. در پرشدن ظرفیت یا سقف حافظهٔ یک نوع، ابتدا نمونه‌های منقضی همان نوع حذف می‌شوند؛ سپس به‌اندازهٔ نیاز قدیمی‌ترین نمونه‌های منتظر همان نوع حذف و منابعشان آزاد می‌شود. ورود Face هرگز Plate را eviction نمی‌کند و برعکس. نمونهٔ در حال پردازش eviction نمی‌شود؛ اگر آزادکردن pendingهای همان نوع کافی نباشد، نمونهٔ ورودی رد می‌شود. نمونه‌ای که حتی به‌تنهایی از سقف حافظهٔ نوع عبور کند نیز رد می‌شود؛ حذف و رد باید شمارنده و دلیل جدا برای هر نوع داشته باشند.
6. نتیجهٔ پذیرفته‌شده روی track cache می‌شود. نمونهٔ منتظر همان track پس از موفقیت حذف می‌شود و مشاهدات بعدی تا پایان track یا رسیدن موعد refresh دوباره recognition اجرا نمی‌کنند. refresh تنها طبق policy صریح انجام می‌شود و cooldown ثبت رخداد را دور نمی‌زند.
7. confidence/کیفیت ناکافی، شکست OCR یا خطای مدل به معنی تکمیل موفق نیست. retry با نمونهٔ جدید و مناسب‌تر، فاصلهٔ زمانی و سقف تلاش محدود انجام می‌شود؛ خطای مدل نیز باید وضعیت خطا بدهد و به Unknown جعلی تبدیل نشود.
8. `MinTrackObservations` شرط اختیاری پایداری track است؛ مقدار ۱ اجازهٔ بررسی یک مشاهده را می‌دهد. `MaxSampleAgeMs` عمر کار را محدود می‌کند. تمام‌شدن track بدون reset دوربین می‌تواند آخرین نمونهٔ معتبر را تا پایان TTL قابل تکمیل نگه دارد؛ اما نتیجه به track جدید متصل نمی‌شود و روی کادر track جدید رسم نمی‌شود.

### وضعیت‌ها و معیار تکمیل

```text
Tracking → Pending → Processing → Completed
                       ├─ کیفیت یا confidence ناکافی → RetryWaiting → Pending با نمونهٔ جدید
                       ├─ خطای recognizer → Failed یا retry محدود
                       └─ TTL / نسل قدیمی / توقف → Expired یا Cancelled
```

`TrackUpdate` فقط موقعیت/وضعیت موقت برای نمایش است؛ نباید به‌عنوان رخداد تشخیص، history یا trigger مصرف شود. `CompletedDetection` نتیجهٔ تکمیل موفق روی یک نمونه است و شناسهٔ نمونه/نسل دارد تا انتشار تکراری نتیجهٔ یک کار حذف شود. عبور آن از policyهای history و trigger همچنان الزامی است.

| نوع | شرط تکمیل |
| --- | --- |
| Plate | پذیرش کادر، عبور خروجی OCR از آستانه‌های decoder و معتبر بودن فرمت پلاک؛ confidence کادر به‌تنهایی کافی نیست |
| Face شناخته‌شده | پذیرش detection، crop/alignment معتبر، embedding موفق و similarity کافی نسبت به هویت نام‌دار |
| Face ناشناس | detection و استخراج embedding موفق، بدون تطبیق نام‌دار کافی؛ نتیجهٔ Unknown فقط مطابق سیاست unknown موجود ثبت/ارسال می‌شود و به معنی هویت تأییدشده نیست |

نبودن مدل recognition باید وضعیت `NotAttempted`/عدم دسترسی به تکمیل را روشن کند؛ آن را نباید شناسایی موفق حساب کرد. در TrackAndQueue پلاک، OCR مستقل لازم است و اتکا به characterهای مسیر legacy کافی نیست؛ تنظیم ناسازگار باید پیش از start رد شود. ثبت یا به‌روزرسانی Unknown در دیتابیس فقط در مرحلهٔ تکمیل انجام می‌شود، نه برای هر TrackUpdate.

### اتصال به runtime، خروجی و lifecycle

قرارداد توسعه باید مرحلهٔ localization/tracking و مرحلهٔ completion را صریحاً جدا کند؛ انتقال `PreviousDetections` به‌تنهایی چنین تفکیکی ایجاد نمی‌کند. تشخیص‌های موقت نباید به consumerهای فعلی `PipelineResultsReady`، `PlateDetected` یا `AnalysisDetected` با معنای رخداد کامل فرستاده شوند. برای نتیجهٔ نهایی، قرارداد دارای sample/frame context لازم است و consumerهای Windows و Service باید با آن تطبیق داده شوند؛ استفاده از latest snapshot فعلی برای خروجی ناهمزمان کافی نیست.

Capture، preview و تولید track نباید منتظر OCR/SFace بمانند. consumerهای history/trigger نیز نباید پردازش capture را قفل کنند. هزینهٔ detector/tracking، انتظار صف و تکمیل recognizer جدا اندازه‌گیری می‌شوند؛ `EventCooldownSeconds` همچنان policy ثبت/تحویل است و جایگزین سیاست enqueue/cache نیست.

در stop/reconnect/reset/reload، پذیرش نمونه متوقف، نسل عوض، کارهای منتظر آزاد و کار در حال اجرا لغو یا تا پایان drain می‌شود؛ خروجی نسل قبلی پس از این مرز منتشر نمی‌شود. recognizer/session تا پایان استفاده dispose نمی‌شود. بافر حافظه‌ای است؛ restart کارهای تکمیل‌نشده را بازیابی نمی‌کند و تضمین replay فقط از زمان ثبت رخداد نهایی در Event Store شروع می‌شود.

Windows و Service باید الگوریتم و قرارداد یکسان این حالت را مصرف کنند، ولی runtime، بافر و دادهٔ مستقل داشته باشند. UI وب از طریق تنظیمات سرویس و UI ویندوز از تنظیمات محلی، تعریف‌های تشخیص عمومی دوربین، مقصد هر task و پارامترهای بافر هر نوع را تعیین می‌کنند. Palm و taskهای Inline در همان دوربین می‌توانند مسیر فعلی خود را ادامه دهند.

### معیار پذیرش و ادامهٔ توسعه برای Service/Web

- یک track در چندین فریم، صفی از cropهای تکراری نسازد؛ موفقیت completion تا موعد refresh اجرای recognizer را برای آن track متوقف کند.
- با worker کند، capture/preview ادامه یابند؛ سقف آیتم، حافظه، TTL و fairness رعایت و دلیل drop/expiry قابل مشاهده باشد.
- Face و Plate از چند ROI در بافر یک دوربین پردازش شوند، بدون تداخل TrackId یا state با دوربین/ROI/task دیگر.
- پرشدن سهم Face ظرفیت یا نمونه‌های Plate را مصرف/حذف نکند و برعکس؛ افزودن چند تعریف هم‌نوع ظرفیت را چندبرابر نکند. زمان‌بندی بین نوع‌ها و تعریف‌های فعال منصفانه باشد.
- تعریف تشخیص از تنظیمات عمومی دوربین قابل افزودن/ویرایش/حذف باشد؛ چند ROI به مقصد انتخاب‌شده وصل شوند و مدل/آستانهٔ همان مقصد اعمال شود. مقصد ناسازگار، حذف مقصد در حال استفاده و خروجی نسخهٔ قدیمی هنگام تغییر تعریف آزموده شوند.
- confidence ناکافی و خطای مدل رخداد موفق نسازند؛ Unknown معتبر با خطای recognition تفکیک شود.
- crop، bounds، frame sequence و زمان نتیجه پس از چند فریم تأخیر همچنان مربوط به نمونهٔ واقعی باشند؛ association با زمان مشاهده آزموده شود.
- stop/reconnect/reload در حین inference، انتشار نتیجهٔ قدیمی، use-after-dispose و نشت منابع ایجاد نکند.
- تاریخچه، trigger، invocation، artifact و نمایش هر دو UI فقط نتیجهٔ تکمیل‌شده را با قرارداد صحیح مصرف کنند؛ هر دو حالت Inline قبلی نیز همچنان کار کنند.
- شمارنده‌های pending/in-flight/replaced/dropped/expired/retried/completed و زمان detector/queue/completion ارائه شوند؛ نرخ اجرای SFace/OCR و CPU/حافظه نسبت به Inline روی ورودی یکسان مقایسه شود.

## Identity database and Windows management

فایل `identity-database.db` مرجع مرکزی اشخاص است. `People` اطلاعات پایهٔ فرد
را نگه می‌دارد و `PersonPlates`، `FaceSamples` و `PalmSamples` با کلید خارجی
`PersonId` به آن متصل هستند. یک شخص می‌تواند چند پلاک، چند نمونهٔ چهره و چند
نمونهٔ کف دست داشته باشد. فرم Windows با نام `Identity database` و صفحهٔ وب با
نام `مدیریت افراد` همین چهار بخش را مدیریت می‌کنند. صفحهٔ وب برای هر شخص تب‌های
`چهره`، `پالم` و `پلاک` دارد، افراد Palm-only و `Unknown Palm #…` را نیز
فهرست می‌کند و حذف گروهی با حذف متعلقات را ارائه می‌دهد.

دیتابیس هنگام load از `face-database.db` و `palm-database.db` قدیمی به‌صورت
مستقل import می‌کند. افراد هم‌نام یا هم‌شماره در migration به یک شخص مرکزی
متصل می‌شوند، نمونهٔ تکراری دوباره وارد نمی‌شود و ادغام بعدی باید از مدیریت
هویت انجام شود. حذف `People` با `ON DELETE CASCADE` نمونه‌های Face/Palm و
پلاک‌های همان شخص را نیز پاک می‌کند.

## Face sample compatibility

ساختار database از یک رابطهٔ یک‌به‌چند تشکیل می‌شود: `People(PersonId, PersonNumber, Name, IsUnknown, CreatedAtUtc, UpdatedAtUtc)` و `FaceSamples(SampleId, PersonId, SampleNumber, FaceImage, Embedding, CreatedAtUtc, OriginalFileName, FileExtension)`. کلید خارجی حذف آبشاری دارد و `UNIQUE(PersonId, SampleNumber)` از تکرار شمارهٔ نمونه جلوگیری می‌کند. سقف نمونه در لایهٔ database enforce می‌شود و مقدار آن 10 است. شناسایی زنده با بیشترین similarity بین نمونه‌های هر شخص انجام می‌شود. چهرهٔ ناشناس جدید به یک شخص `Unknown #NNNN` تبدیل و همراه اولین crop/embedding ذخیره می‌شود؛ مشاهدات مشابه هر 10 ثانیه حداکثر یک نمونهٔ جدید به همان شخص اضافه می‌کنند. Rename، `IsUnknown` را خاموش می‌کند تا نمونه‌ها در شناسایی نام‌دار استفاده شوند.

## گلوگاه‌های طراحی که باید حفظ شوند

- `Threads` تعداد threadهای `IntraOp` در sessionهای ONNX Runtime است و در `CameraProcessingSettings` همان آیتم ذخیره می‌شود. UI کنترل Plate و Face را برای آیتم انتخاب‌شده sync می‌کند؛ `CameraSettings.Threads` فقط default ساخت آیتم جدید است و از آیتم موجود دوباره مقداردهی نمی‌شود.
- نرخ مؤثر Face از دو سقف عبور می‌کند: حلقهٔ دوربین با `ActiveDetectionFps`/`IdleDetectionFps` تعیین می‌کند چه زمانی pipeline فراخوانی شود و خود آیتم Face با `Rois[].Processing[].MaxFps` سقف مستقل دارد؛ بنابراین نرخ عملی حداکثر `min(active-or-idle-rate, item.MaxFps)` است. `FaceMaxFps` در سطح دوربین فقط default ساخت آیتم جدید یا migration است و تغییر آن آیتم Face موجود را overwrite نمی‌کند.
- وقتی `RecognitionEnabled` فعال است، برای هر چهرهٔ پذیرفته‌شده در هر اجرای Face، SFace embedding ساخته و با نمونه‌های database مقایسه می‌شود؛ `EventCooldownSeconds` فقط ثبت رخداد را محدود می‌کند و هزینهٔ inference را کم نمی‌کند. برای CPU محدود، Motion Gate، `BufferCount = 0`، مدل INT8 و `Threads = 1` در هر دوربین اولویت دارند.
- `CameraPipelineCoordinator` برای هر ROI فعال فهرست pipeline مستقل می‌سازد و ROIها را موازی اجرا می‌کند؛ Plate، Face و Palm هر سه از `ProcessingRegistry` و registration ماژول خود ساخته می‌شوند. `ProcessingMode` در هر ROI تعیین می‌کند pipelineهای داخل آن زنجیره‌ای (`Sequential`) یا همزمان (`Parallel`) باشند. در حالت موازی، هر pipeline ورودی مستقل دارد و به خروجی/تصویر pipeline دیگر وابسته نیست. `CameraRuntime` فقط lifecycle/capture، Motion، هندسهٔ ROI و policy خروجی را نگه می‌دارد و به concrete type یا factory مخصوص قابلیت‌ها وابسته نیست. state، tracker و محدودیت FPS بین ROIها مشترک نیستند.
- graph پردازش در `CameraPipelineCoordinator` به‌صورت immutable و قابل تعویض نگه‌داری می‌شود. اجرای inference یک lease کوتاه روی graph می‌گیرد، اما قفل coordinator را در طول inference نگه نمی‌دارد؛ بنابراین status/API پشت اجرای ONNX منتظر نمی‌مانند. هنگام rebuild، graph قبلی فقط پس از پایان inferenceهای فعال dispose می‌شود.
- صف event ذخیره‌سازی bounded است و ظرفیت آن از `Service.Runtime.MaxEventQueueLength` می‌آید. در صورت پرشدن صف، artifactهای رخداد جدید آزاد و شمارندهٔ `droppedEventCount` افزایش می‌یابد تا فشار ذخیره‌سازی باعث رشد بی‌نهایت حافظه نشود.
- cooldownها مستقل‌اند: `EventCooldownSeconds` در هر processing item و `HistorySubscription.Profiles[].CooldownSeconds` برای ثبت داخلی canonical history، `ClientSubscription.CooldownSeconds` برای تحویل replay/live همان اتصال، و `TriggerDefinition.CooldownSeconds` برای اجرای همان تریگر. در سرویس، `HistorySubscription` فقط سناریوی ثبت داخلی است و فیلتر نمایش عمومی نیست؛ در Windows نیز filter پس از query نیست، بلکه قبل از ساخت رکورد نهایی اعمال می‌شود و پنل از همان رکوردهای نهایی Event Store می‌خواند. برای ثبت داخلی، `DetectionHistory(HistoryKey, OccurredAtUtc)` با ایندکس lookup بررسی می‌شود؛ برای trigger، `TriggerHistory(TriggerId, TriggerKey, OccurredAtUtc)` فقط رکوردهای موفق همان تریگر را بررسی می‌کند. هر دو ردیف در transaction رخداد ثبت و هنگام حذف رخداد پاک می‌شوند.
- در Windows مستقل نیز `DesktopHistorySubscriptionMatcher` قبل از append داخلی همین جداسازی را اعمال می‌کند، staging را از query کنار می‌گذارد و `DesktopAutomationEngine` trigger را مستقل ارزیابی می‌کند؛ اگر trigger match شود، subscription داخلی مانع ارسال payload کامل نمی‌شود. حذف از `Detection history` علاوه بر Event Store، archive و history حافظهٔ runtime را پاک می‌کند.
- لایسنس featureمحور جلوی ساخت detector/Pipeline غیرمجاز را می‌گیرد، اما مانع مطلق مهندسی معکوس روی دستگاه مشتری نیست.
### استقلال detector پلاک و OCR

در pipeline پلاک، `ModelFile` فقط مسئول یافتن کادر پلاک است. در صورت فعال‌بودن
`CharacterRecognitionEnabled`، crop هر کادر به مدل مستقل `CharacterModelFile`
داده می‌شود. این دو انتخاب در UI وب و WinForms مستقل هستند و تغییر مدل OCR مدل
detector یا `InputSize` آن را تغییر نمی‌دهد.

مدل OCR با manifest هم‌نام `.ocr.json` در catalog مشترک ثبت می‌شود. manifest
نوع decoder و مشخصات ورودی را اعلام می‌کند و factory خروجی decoder را به
`PlateOcrResult` استاندارد شامل متن، confidence، کاراکترها و bounds اختیاری
تبدیل می‌کند. decoderهای فعلی `crnn_ctc`، `cnn_glyph` و `yolo_character` هستند.
