# معماری

سند کامل بازسازی در [REBUILD_GUIDE.md](REBUILD_GUIDE.md) است. این فایل مرزهای معماری را کوتاه و صریح نگه می‌دارد.

```text
HshVisionLab (WinForms)
  ├─ HshDetectionEngin.Abstractions  قراردادها و settings
  ├─ HshDetectionEngin               capture، Motion، ROI، runtime و overlay
  ├─ HshDetectionEngin.Plate         YOLO/OCR/tracker/package مدل Plate
  ├─ HshDetectionEngin.Face          YuNet/SFace/tracker/FaceDatabase/package مدل Face
  └─ HshDetectionEngin.Licensing     fingerprint و RSA license validation

HshDetectionEngin.LicenseRequest    ابزار مشتری برای درخواست فعال‌سازی
HshDetectionEngin.LicenseIssuer     ابزار امن مدیریت مشتری و صدور/آرشیو لایسنس
```

## مرز مسئولیت

- UI صاحب نمایش، مدیریت دوربین، ویرایش ROI، history، هماهنگ‌کردن enrollment با `FaceModule` و مدیریت Face database است؛ ساخت pipelineهای runtime در ماژول‌های پردازشی انجام می‌شود.
- Engine صاحب چرخهٔ دریافت فریم، Motion Gate و lifecycle دوربین است؛ `CameraPipelineCoordinator` ساخت graph پردازش، اجرای pipelineهای هر ROI، انتقال metadata و dispose آن‌ها را جداگانه مدیریت می‌کند. overlay و history هنوز policy خروجی دوربین هستند.
- Abstractions هیچ منطق قابلیت خاص ندارد و قراردادهای پایدار را نگه می‌دارد.
- Plate و Face منطق domain و مدل خود را نگه می‌دارند؛ مدل‌های package شده در زمان اجرا از پوشهٔ `Models/<Capability>` کنار executable خوانده می‌شوند. پوشهٔ flat `Models` و مسیر قدیمی `Modules/<Capability>/Models` نیز برای سازگاری پشتیبانی می‌شوند.
- Licensing تنها مرجع صدور و اعتبارسنجی license است؛ `HshVisionLab` و ماژول‌های runtime نباید private key را دریافت کنند. `LicenseIssuer` یک UI جداگانه و فقط برای workstation امن صادرکننده است و private key را مصرف می‌کند.
- `FaceDatabase` مرجع SQLite برای `People` و `FaceSamples` است. هر نمونه embedding و تصویر crop‌شدهٔ aligned را داخل BLOB نگه می‌دارد؛ UI فقط snapshot رکوردها را برای گرید و مقایسه دریافت می‌کند.
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
Source → frame slot/queue → latest-preview slot → preview worker (≤15 FPS)
       → CameraRuntime processing worker: Motion روی ROI و polygon mask
       → CameraPipelineCoordinator: registry → اجرای موازی ROIها و اجرای taskهای هر ROI
       → detection / overlay / history / UI event
```

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

`NamedRoi.Processing` فهرست آیتم‌های هر ROI و `NamedRoi.ProcessingMode` نحوهٔ اجرای آن‌ها را نگه می‌دارد. ROIهای فعال یک دوربین به‌صورت موازی اجرا می‌شوند. مقدار `Sequential` (پیش‌فرض برای سازگاری) taskهای همان ROI را به‌ترتیب فهرست اجرا می‌کند و `PreviousDetections`/`NextImage` را زنجیره می‌کند؛ مقدار `Parallel` هر task را روی یک کپی مستقل از تصویر ROI اجرا می‌کند و زنجیرهٔ بین taskها را فعال نمی‌کند. هر آیتم فیلدهای مشترک `Enabled`، `MaxFps` و `Threads` را دارد و تنظیمات اختصاصی ماژول را در `Options` نگه می‌دارد؛ Plate از `PlateProcessingOptions` و Face از `FaceProcessingOptions` استفاده می‌کند. ماژول گزینه‌های typed خود را هنگام ساخت pipeline یک‌بار از JSON می‌خواند و مسیر `Process()` به JSON یا reflection دسترسی ندارد. `ProcessingModuleDescriptor.OptionsType` قرارداد UI برای deserialize کردن گزینه‌هاست و نوع‌های جدید بدون تغییر فرم، editor عمومی options دریافت می‌کنند؛ `EditorKey` فقط برای انتخاب editor اختصاصی قابلیت‌های موجود است. `CameraProcessingSettings.Type` فقط مقدار serialized سازگار با فایل‌های قدیمی است؛ کد اجرایی باید از `CameraProcessingSettings.Kind` و `ProcessingType` استفاده کند. فیلدهای Face/Plate در `CameraSettings` فقط default سازگار با فایل‌های قدیمی و template ساخت آیتم تازه هستند؛ آیتم موجود از تغییرات بعدی camera default مستقل می‌ماند. دوربین بدون ROI و ROI بدون آیتم پردازش فقط تصویر/overlay ROI را نمایش می‌دهند و inference انجام نمی‌دهند. `ProcessingRegistry` مرجع ثبت قابلیت‌هاست و UI descriptorهای آن را برای نمایش نوع‌ها مصرف می‌کند.

## Face database

ساختار database از یک رابطهٔ یک‌به‌چند تشکیل می‌شود: `People(PersonId, PersonNumber, Name, IsUnknown, CreatedAtUtc, UpdatedAtUtc)` و `FaceSamples(SampleId, PersonId, SampleNumber, FaceImage, Embedding, CreatedAtUtc, OriginalFileName, FileExtension)`. کلید خارجی حذف آبشاری دارد و `UNIQUE(PersonId, SampleNumber)` از تکرار شمارهٔ نمونه جلوگیری می‌کند. سقف نمونه در لایهٔ database enforce می‌شود و مقدار آن 10 است. شناسایی زنده با بیشترین similarity بین نمونه‌های هر شخص انجام می‌شود. چهرهٔ ناشناس جدید به یک شخص `Unknown #NNNN` تبدیل و همراه اولین crop/embedding ذخیره می‌شود؛ مشاهدات مشابه هر 10 ثانیه حداکثر یک نمونهٔ جدید به همان شخص اضافه می‌کنند. Rename، `IsUnknown` را خاموش می‌کند تا نمونه‌ها در شناسایی نام‌دار استفاده شوند.

## گلوگاه‌های طراحی که باید حفظ شوند

- `Threads` تعداد threadهای `IntraOp` در sessionهای ONNX Runtime است و در `CameraProcessingSettings` همان آیتم ذخیره می‌شود. UI کنترل Plate و Face را برای آیتم انتخاب‌شده sync می‌کند؛ `CameraSettings.Threads` فقط default ساخت آیتم جدید است و از آیتم موجود دوباره مقداردهی نمی‌شود.
- نرخ مؤثر Face از دو سقف عبور می‌کند: حلقهٔ دوربین با `ActiveDetectionFps`/`IdleDetectionFps` تعیین می‌کند چه زمانی pipeline فراخوانی شود و خود آیتم Face با `Rois[].Processing[].MaxFps` سقف مستقل دارد؛ بنابراین نرخ عملی حداکثر `min(active-or-idle-rate, item.MaxFps)` است. `FaceMaxFps` در سطح دوربین فقط default ساخت آیتم جدید یا migration است و تغییر آن آیتم Face موجود را overwrite نمی‌کند.
- وقتی `RecognitionEnabled` فعال است، برای هر چهرهٔ پذیرفته‌شده در هر اجرای Face، SFace embedding ساخته و با نمونه‌های database مقایسه می‌شود؛ `EventCooldownSeconds` فقط ثبت رخداد را محدود می‌کند و هزینهٔ inference را کم نمی‌کند. برای CPU محدود، Motion Gate، `BufferCount = 0`، مدل INT8 و `Threads = 1` در هر دوربین اولویت دارند.
- `CameraPipelineCoordinator` برای هر ROI فعال فهرست pipeline مستقل می‌سازد و ROIها را موازی اجرا می‌کند؛ Plate و Face هر دو از `ProcessingRegistry` و registration ماژول خود ساخته می‌شوند. `ProcessingMode` در هر ROI تعیین می‌کند pipelineهای داخل آن زنجیره‌ای (`Sequential`) یا همزمان (`Parallel`) باشند. در حالت موازی، هر pipeline ورودی مستقل دارد و به خروجی/تصویر pipeline دیگر وابسته نیست. `CameraRuntime` فقط lifecycle/capture، Motion، هندسهٔ ROI و policy خروجی را نگه می‌دارد و به concrete type یا factory مخصوص قابلیت‌ها وابسته نیست. state، tracker و محدودیت FPS بین ROIها مشترک نیستند.
- graph پردازش در `CameraPipelineCoordinator` به‌صورت immutable و قابل تعویض نگه‌داری می‌شود. اجرای inference یک lease کوتاه روی graph می‌گیرد، اما قفل coordinator را در طول inference نگه نمی‌دارد؛ بنابراین status/API پشت اجرای ONNX منتظر نمی‌مانند. هنگام rebuild، graph قبلی فقط پس از پایان inferenceهای فعال dispose می‌شود.
- صف event ذخیره‌سازی bounded است و ظرفیت آن از `Service.Runtime.MaxEventQueueLength` می‌آید. در صورت پرشدن صف، artifactهای رخداد جدید آزاد و شمارندهٔ `droppedEventCount` افزایش می‌یابد تا فشار ذخیره‌سازی باعث رشد بی‌نهایت حافظه نشود.
- سه سطح cooldown از هم مستقل‌اند: `EventCooldownSeconds` در هر processing item برای ثبت canonical history، `ClientSubscription.CooldownSeconds` برای فیلتر replay/live همان اتصال، و `TriggerDefinition.CooldownSeconds` برای اجرای همان تریگر. برای سطح اول، `DetectionHistory(HistoryKey, OccurredAtUtc)` با ایندکس lookup بررسی می‌شود و در صورت تکراری‌بودن، `DetectionEvents` جدید ساخته نمی‌شود؛ برای سطح trigger، `TriggerHistory(TriggerId, TriggerKey, OccurredAtUtc)` فقط رکوردهای موفق همان تریگر را بررسی می‌کند. هر دو ردیف در transaction رخداد ثبت و هنگام حذف رخداد پاک می‌شوند.
- لایسنس featureمحور جلوی ساخت detector/Pipeline غیرمجاز را می‌گیرد، اما مانع مطلق مهندسی معکوس روی دستگاه مشتری نیست.
