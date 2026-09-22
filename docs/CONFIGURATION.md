# پیکربندی runtime

## زبان رابط کاربری

رابط برنامه از فارسی و انگلیسی پشتیبانی می‌کند. دکمهٔ زبان در نوار بالای پنجرهٔ اصلی بین دو زبان جابه‌جا می‌شود؛ پس از انتخاب زبان، برنامه برای اعمال کامل تغییرات دوباره راه‌اندازی می‌شود. انتخاب زبان در فایل `ui-language.txt` کنار executable ذخیره می‌شود. مقادیر فنی تنظیمات مانند `TCP`، `FFmpeg`، نام مدل‌ها و کلیدهای JSON تغییر نمی‌کنند.

قانون فونت رابط: در زبان فارسی خانوادهٔ فونت `Tahoma` با اندازهٔ پیش‌فرض ۸ استفاده می‌شود؛ در زبان انگلیسی خانوادهٔ `Segoe UI` و اندازهٔ پیش‌فرض فعلی حفظ می‌شود. اندازه‌های اختصاصی تیترها، دکمه‌های کوچک و آیکون‌ها طبق طراحی همان کنترل باقی می‌مانند.

آیکون دکمه‌های اصلی، عملیات دوربین، ROI و بانک اطلاعات چهره از Material Icons رسمی انتخاب شده‌اند؛ فونت در resource داخلی برنامه قرار دارد و برنامه برای نمایش آیکون‌ها به دسترسی اینترنت یا نصب Material Icons روی سیستم مقصد نیاز ندارد.

دکمه‌های `Start`، `Stop`، `Edit` و `Delete` روی کارت‌های دوربین به‌صورت آیکون‌محور و کم‌عرض هستند؛ متن آن‌ها در Tooltip نمایش داده می‌شود.

راهنمای بازسازی رفتار و سازگاری فایل‌ها در [REBUILD_GUIDE.md](REBUILD_GUIDE.md) است. این فایل مرجع سریع `settings.json` است.

## محل و ساختار

`settings.json` کنار `HshVisionLab.exe` ذخیره می‌شود. ساختار ریشه:

```json
{
  "Cameras": [ /* CameraSettings مستقل */ ],
  "SelectedCameraId": "camera-id"
}
```

نمونهٔ کامل و قابل‌ویرایش در [settings.optimized.example.json](../settings.optimized.example.json) قرار دارد. نقطه‌های ROI نرمال‌شده‌اند: `0..1` نسبت به عرض و ارتفاع تصویر اصلی.

## Face database

`face-database.db` کنار `HshVisionLab.exe` ساخته می‌شود و یک SQLite database واحد است. جدول‌های `People` و `FaceSamples` اطلاعات شخص، `PersonNumber`، تاریخ ایجاد، تصویر crop‌شدهٔ aligned، confidence تشخیص و embedding را نگه می‌دارند. تصویر در خود database به‌صورت BLOB ذخیره می‌شود و فایل تصویر جداگانه‌ای برای رکورد ایجاد نمی‌شود. هر شخص حداکثر ۱۰ نمونه دارد؛ گرید Face database هر نمونه را همراه thumbnail، شمارهٔ نفر، شمارهٔ نمونه، confidence تشخیص، نام فایل ورودی و تاریخ ایجاد نمایش می‌دهد. گزینهٔ `Group by person` برای هر شخص ردیف سرگروه با علامت `-`/`+` می‌سازد و با کلیک روی آن، نمونه‌های گروه را جمع یا باز می‌کند. `Add to selected person` برای افزودن یک فایل تصویر جدید به شخص انتخاب‌شده است؛ `Move selected sample` نیز نمونهٔ موجود در گرید، ازجمله نمونهٔ ناشناس، را با تصویر، embedding و confidence به شخص نام‌دار انتخابی منتقل می‌کند. اگر نسخهٔ قدیمی `face-database.json` وجود داشته باشد و database جدید ساخته نشده باشد، در اولین اجرا embeddingهای قدیمی به SQLite منتقل می‌شوند؛ تصویر رکوردهای قدیمی قابل بازیابی نیست و به‌صورت missing نمایش داده می‌شود.

## گروه‌های تنظیمات هر دوربین

| گروه | فیلدها | نکتهٔ رفتاری |
| --- | --- | --- |
| هویت و اتصال | `Id`، `Name`، `SourceUrl`، `Transport`، `CaptureBackend`، `ReconnectDelaySec` | source عددی Webcam/DShow است؛ منبع غیرعددی با backend انتخاب‌شده دریافت می‌شود. `CaptureBackend` یکی از `FFmpeg`، `LibVLC` یا `MediaMTX` است؛ مقدار پیش‌فرض `FFmpeg` است. MediaMTX برای دریافت داخلی از local RTSP و برای مرورگر از WHEP استفاده می‌کند. |
| فریم | `BufferCount` | مقدار `0` فقط جدیدترین فریم را نگه می‌دارد؛ مقدار مثبت ظرفیت صف محدود فریم را برای هر دو pipeline همان دوربین تعیین می‌کند. |
| مسیرهای پردازش | `Rois[].Processing[]`، `PlateEnabled`، `FaceEnabled`، `ProcessingSchemaVersion` | فقط ROIهایی که زیرمجموعهٔ Processing فعال دارند تحلیل می‌شوند؛ Booleanها برای سازگاری و خلاصهٔ فعال‌بودن نگه داشته می‌شوند. `CameraSettings.Processing` فقط legacy است. |
| Plate | `Options: PlateProcessingOptions` به‌همراه `MaxFps` و `Threads` | شامل `ModelFile`، `InputSize`، `Preprocessing`، `Confidence`، `NmsIoU` و `TrackMaxMisses` است. در UI مدل به‌صورت ComboBox از catalog سرویس انتخاب می‌شود، نه text input. پس از انتخاب مدل، ComboBox سایز از `inputSizes` همان مدل پر می‌شود: مدل ثابت فقط اندازهٔ tensor خودش را دارد؛ مدل YOLO پویا با توجه به stride مدل، گزینه‌های امن `320/416/480/512/640` را ارائه می‌کند. `ModelFile` نام منطقی ONNX است؛ runtime ابتدا مدل package شدهٔ هم‌نام را در `Models/Plate` و سپس در `Models` کنار executable جست‌وجو می‌کند و مسیر قدیمی `Modules/Plate/Models` را نیز برای سازگاری می‌پذیرد. در Debug، `HshDetectionEngin.Plate/Models` نیز fallback است. |
| Face | `Options: FaceProcessingOptions` به‌همراه `MaxFps` و `Threads` | شامل مدل YuNet، preprocessing، confidence، recognition، NMS، TopK، tracking، record confidence و cooldown است و در UI در گروه‌های جداگانهٔ Face detection، Face identification و Tracking/recording نمایش داده می‌شود. `FacePreprocessing` در مدل typed با نام `Preprocessing` نگه‌داری می‌شود. Face به‌صورت pipeline ساخته می‌شود؛ هنگام فعال‌بودن SFace مقدار `None` توصیه می‌شود. SFace به‌صورت مستقل landmark alignment و ورودی `112×112` خود را دارد. `Threads` در تنظیمات همان آیتم پردازش ذخیره می‌شود و به `IntraOpNumThreads` sessionهای ONNX Runtime وصل می‌شود؛ `MaxFps` فقط سقف همان آیتم Face است. مدل‌ها ابتدا از `Models/Face`، سپس `Models` و در نهایت مسیر قدیمی `Modules/Face/Models` خوانده می‌شوند؛ در Debug، `HshDetectionEngin.Face/Models` نیز fallback است. |
| Motion و نمایش | `DrawBoxes`، `MotionGateEnabled`، `MotionFps`، `MotionThreshold`، `MotionChangedPercent`، `MotionRoiScalePercent`، `MotionHoldMs`، `ActiveDetectionFps`، `IdleDetectionFps` | در idle با مقدار `0` برای `IdleDetectionFps` inference متوقف می‌شود. |
| ROI و هدف پردازش | `Rois[]`، `Rois[].Points`، `Rois[].Processing[]` و `RoiEnabled` | بدون ROI هیچ inferenceای انجام نمی‌شود. هر ROI می‌تواند صفر، یک یا چند آیتم Plate/Face با مدل و ورودی مستقل داشته باشد. |

## مقادیر پیش‌فرض مهم

| تنظیم | مقدار |
| --- | --- |
| Plate model / input | `best.onnx` / `416` |
| Plate confidence / NMS / max FPS | `0.35` / `0.45` / `8` |
| Face model / input | `face_yunet_2023mar.onnx` / `640` |
| Face confidence / record threshold | `0.80` / `0.80` |
| Face preprocessing | `None` |
| Face recognition / unknown threshold | `0.40` / `0.35` |
| Face match IoU / max misses | `0.25` / `10` |
| Face event cooldown | `60` ثانیه |
| Motion FPS / threshold / changed percent | `8` / `20` / `0.7` |
| Motion hold / ROI scale | `1200` ms / `85%` |
| Active / idle detection FPS | `8` / `0` |
| Buffer count | `0` |
| RTSP receiver / CaptureBackend | `FFmpeg` |

مقادیر بالا default کد یا migration هستند و فقط هنگام ساخت تنظیمات جدید یا نبودن مقدار معتبر اعمال می‌شوند؛ بازکردن برنامه مقدارهای موجود در `settings.json` را خودکار بازنویسی نمی‌کند. در schema فعلی مقدار `ProcessingSchemaVersion` برابر `3` است و آیتم‌های مؤثر باید در `Cameras[].Rois[].Processing[]` قرار داشته باشند؛ `Cameras[].Processing` فقط فهرست legacy است و برای فایل جدید باید خالی باشد. تنظیمات قدیمیِ flat مربوط به هر آیتم هنگام Load به `Options` typed مهاجرت می‌کنند.

`FaceInputSize` برای YuNetهای فعلی از metadata مدل به `640` محدود می‌شود؛ فرم ویندوزی و UI وب همین مقدار را از catalog مشترک می‌گیرند. `FacePreprocessing` برای تشخیص YuNet است و SFace پس از تشخیص، crop را با پنج landmark به `112×112` هم‌تراز می‌کند و آماده‌سازی ورودی خودش را انجام می‌دهد. با فعال‌بودن recognition مقدار `None` توصیه می‌شود؛ `Advanced` با grayscale و equalization ممکن است embedding را تغییر دهد. در تب `Processing`، لیبل‌های Plate و Face شامل `Model`، `Input size`، `Preprocessing`، `Confidence`، `Max processing FPS`، `Threads (ONNX Runtime IntraOpNumThreads)` و `Buffer count (0 = newest only)` هستند. کنترل‌های `Threads` در بخش تشخیص Plate و tracking/recording Face برای آیتم انتخاب‌شده به‌صورت دوطرفه sync می‌شوند و مقدار همان آیتم را ویرایش می‌کنند؛ `Buffer count` در سطح دوربین مشترک است. فیلدهای Face در سطح `CameraSettings` فقط template پیش‌فرض برای ساخت processing item جدید هستند؛ Save تنظیمات آیتم را به camera default برنمی‌گرداند و تغییر camera default آیتم‌های موجود را overwrite نمی‌کند.

### پروفایل‌های کارایی در تنظیمات دوربین

> نکتهٔ به‌روز: `InputSize` در UI از catalog مدل انتخاب می‌شود و مقدار دلخواه پذیرفته نمی‌شود. مدل‌های YuNet فعلی (`face_yunet_2023mar` و `int8`) ورودی `640×640` دارند و مقدار پیش‌فرض Face نیز `640` است؛ runtime اندازهٔ واقعی مدل را از metadata بررسی می‌کند.

دکمهٔ `Restore Defaults` در فرم تنظیمات دوربین سه پروفایل دارد:

- `Weak / virtual 6-core`: برای سیستم‌های مجازی یا ضعیف؛ مدل‌های INT8 برای Plate و Face، یک thread، حدود ۵ FPS و `TopK` پایین‌تر برای Face. برای قطع‌نشدن تشخیص روی تصویر کم‌تحرک، idle inference نیز با ۲ FPS فعال می‌ماند. نام مدل‌های فعلی `best_416_int8_qdq_experimental.onnx` و `face_yunet_2023mar_int8.onnx` هستند؛ اگر در بستهٔ نصب موجود نباشند، مدل موجود انتخاب می‌شود.
- `Balanced / normal system`: حالت پیشنهادی عمومی؛ دو thread و حدود ۸ FPS، با مصرف متعادل CPU.
- `High performance / realtime`: برای CPU قوی؛ ۴ thread، حدود ۱۵ FPS، ورودی Face بزرگ‌تر و `TopK` بالاتر.

پروفایل انتخاب‌شده روی تنظیمات عمومی دوربین و تمام processing itemهای ROI اعمال می‌شود؛ thresholdهای تشخیص و شناسایی عمداً تغییر نمی‌کنند. بعد از انتخاب پروفایل باید روی `Save` کلیک شود. `Buffer count` در هر سه حالت صفر می‌ماند تا پردازش روی جدیدترین فریم انجام شود و latency تجمعی ایجاد نشود.

### قرارداد ترسیم خروجی پردازش

وقتی `DrawBoxes` روشن باشد، خروجی هر `AnalysisDetection` روی تصویر preview رسم می‌شود. کادر سبز یعنی detection معتبر/پذیرفته‌شده و کادر قرمز یعنی confidence در بازهٔ حداکثر ۱۰٪ پایین‌تر از آستانهٔ نمایش است؛ confidenceهای ضعیف‌تر اصلاً منتشر نمی‌شوند. وقتی `DrawBoxes` خاموش باشد، هیچ‌یک از این ترسیم‌ها روی تصویر انجام نمی‌شود. هر processing module باید در metadata نتیجه، `OverlayThreshold` را به‌عنوان آستانهٔ نمایش قرار دهد؛ در صورت وجود `Accepted`، این وضعیت بر مقایسهٔ عددی اولویت دارد. کلید قدیمی `Threshold` نیز برای سازگاری پشتیبانی می‌شود. کاندید قرمز فقط برای بازخورد تصویری است و نباید وارد tracking، recognition یا history شود. بنابراین پردازش‌های آینده با همین قرارداد، بدون منطق رنگ‌بندی اختصاصی در UI، به‌صورت یکسان سبز/قرمز نمایش داده می‌شوند.

## رفتار UI مرتبط با دوربین و ROI

این بخش دربارهٔ فرم‌ها و کنترل‌های WinForms است و عبارت‌هایی مانند
`CameraManagerForm`، `DockStyle`، دکمهٔ `Delete` روی tile و `Thumbnails` را فقط
برای همان برنامه توضیح می‌دهد. برای بازسازی UI وب، ملاک
[WEB-UI-RECONSTRUCTION-SPEC.md](WEB-UI-RECONSTRUCTION-SPEC.md) است؛ وب routeها،
پنل تشخیص crop+متن، پنل runtime داشبورد و `CameraFocusWorkspace` خود را دارد.

- نوار بالایی شامل دکمه‌های آیکونی `Cameras`، `Save`، `Face database`، `Start All`، `Stop All` و `Thumbnails` است؛ متن عملکرد آن‌ها در Tooltip قرار دارد و `Thumbnails` فقط در نمای بزرگ دوربین نمایش داده می‌شود. دکمهٔ `Cameras` فرم جداگانهٔ مدیریت دوربین‌ها را باز می‌کند؛ در این فرم دکمهٔ `Add camera` و جدول ستون‌های نام، source، status، FPS و کنترل‌های Start/Stop، Edit و Delete قرار دارند.
- نمای چنددوربینه بر اساس تعداد دوربین‌ها شبکهٔ ۱، ۲، ۳ یا ۴ ستونه می‌سازد. بالای هر tile نوار وضعیت و دکمه‌های `Start/Stop`، `Edit` و `Delete` قرار دارد و عملیات دوربین را بدون بازکردن جدول ممکن می‌کند. کلیک روی tile، تصویر یا نوار وضعیت، دوربین را انتخاب می‌کند و دوبارکلیک روی tile، تصویر یا نوار وضعیت نمای دوربین را بزرگ می‌کند. عنوان نمای بزرگ نام دوربین و راهنمای دوبارکلیک برای برگشت را نشان می‌دهد.
- چیدمان اصلی دو ستون دارد: preview در سمت چپ و ستون ثابت ۳۹۰ پیکسلی در سمت راست. `Detected events` در ستون راست قرار دارد و هر رخداد را با crop تصویر و مشخصات متنی نمایش می‌دهد؛ پنل وضعیت تکراری دوربین‌ها از این ناحیه حذف شده است. در نمای بزرگ، پنل ROI کامل در ردیف زیر آن باز می‌شود و روی تصویر overlay نمی‌شود.
- پنل ROI پنج عمل اصلی دارد: افزودن ROI، ویرایش نقاط، تغییر نام، حذف و پاک‌کردن همه. دکمه‌ها در یک نوار افقی با `DockStyle.Left` قرار دارند و هرکدام اندازهٔ `30×27`، فونت `Segoe UI` با اندازهٔ 7.5 و حالت Bold و آیکونِ وسط‌چین‌شده از طریق `Button.Image` دارند؛ این استایل با دکمه‌های کارت دوربین یکسان است. نمای ذره‌بین فقط تصویر دوربین را بزرگ می‌کند و خودکار وارد حالت edit نمی‌شود؛ ابزارهای `ویرایش`، `ROI جدید` و `حذف` حالت جدا دارند و برای هر حالت `ذخیره` و `لغو` نمایش داده می‌شود. مختصات به بازهٔ نرمال `0..1` clamp می‌شوند و پایان ویرایش حداقل سه نقطه می‌خواهد.
- تب `Processing` درختی است: ROIها نمایش داده می‌شوند و زیر هرکدام می‌توان صفر، یک یا چند آیتم Plate/Face اضافه کرد. انتخاب ROI خواص ROI و انتخاب آیتم خواص همان پردازش را نشان می‌دهد؛ مدل، Input Size و پارامترهای پردازش برای هر آیتم قابل تنظیم است. تنظیمات چهره به چهار بخش Plate detection، Face detection، Face identification و Tracking/recording تقسیم شده‌اند. مدل‌ها در ComboBox از `GET /api/v1/service/models` بارگذاری می‌شوند و فیلد text برای model file وجود ندارد. ROI بدون آیتم فقط روی تصویر ترسیم می‌شود و inference ندارد.

### پنجرهٔ تنظیمات دوربین

- `CameraManagerForm` به‌صورت modeless باز می‌شود. دکمهٔ `Add camera` در آن فرم و دکمهٔ `Edit` در جدول، هر دو فرم تنظیمات دوربین را به‌صورت modal با تب‌های `General` و `Processing` باز می‌کنند. در `General` نام، source (RTSP، Webcam index یا فایل)، transport TCP/UDP، `CaptureBackend`، reconnect delay، رسم box/label، Motion Gate، نرخ نمونه‌برداری Motion، threshold، changed percent، مقیاس Motion ROI، hold و نرخ Active/Idle تنظیم می‌شوند. گیرندهٔ پیش‌فرض `FFmpeg` است؛ `LibVLC` برای RTSPهایی است که در VLC پایدارتر از OpenCV FFmpeg هستند و به VLC 3.x x64 نصب‌شده یا متغیر محیطی `VLC_HOME` نیاز دارد. `MediaMTX` برای path مستقل، RTSP داخلی موتور و WHEP خام مرورگر استفاده می‌شود.
- در `Processing` برای هر ROI می‌توان با انتخاب نوع `Plate` یا `Face` آیتم اضافه یا حذف کرد و ترتیب درخت، ترتیب اجرای پردازش‌هاست. بخش Plate شامل model، input size، preprocessing، confidence، NMS IoU، max FPS، threads، buffer count و track max misses است. بخش Face علاوه بر تشخیص، تنظیمات identification، مدل SFace، thresholdهای known/unknown و tracking/recording را دارد. دو کنترل Threads در بخش‌های Plate و Face برای آیتم انتخاب‌شده به‌صورت دوطرفه همگام‌اند؛ Buffer count در سطح دوربین مشترک است. تمام تنظیمات مؤثر Face، از جمله record confidence و event cooldown، از همان processing item خوانده می‌شوند؛ تنظیمات سطح دوربین فقط هنگام ساخت آیتم جدید default هستند.
- دکمهٔ `Save` فقط پس از معتبر بودن نام و source و یکتا بودن نام ROIها در همان دوربین تنظیمات را اعمال می‌کند؛ `Cancel` تغییرات ویرایش‌شده را نگه نمی‌دارد. مدل‌ها از فهرست packageهای `.hshmodel` موجود در `Models` کنار executable انتخاب می‌شوند و در Debug، پوشه‌های مدل پروژه نیز fallback هستند.

### مسیر نمایش زنده

- در `MediaMTX`، مرورگر مستقیماً به WHEP خام path وصل می‌شود. تصویر از مسیر
  `MediaMtxFrameSource` و pipeline تشخیص عبور نمی‌کند؛ فقط endpoint سبک
  `/api/v1/streams/{cameraId}/overlay` برای ROI، کادر، متن و primitiveهای
  پردازشی poll می‌شود و Overlay روی ویدئو قرار می‌گیرد.
- در `FFmpeg`/`LibVLC`، UI غیر MediaMTX از latest snapshot سرویس استفاده می‌کند.
  `BufferCount = 0` برای newest-frame است؛ مقدار مثبت در VLC یا FFmpeg صف محدود
  ایجاد می‌کند و برای latency تجمعی مناسب نیست.
- مسیر `/api/v1/streams/{cameraId}/webrtc/offer` برای کلاینت‌های legacy که
  فریم کامپوزیت‌شده می‌خواهند باقی می‌ماند و مسیر پیش‌فرض MediaMTX نیست.

### پنجرهٔ Face database و تشابه

- Face database گرید تصویری با ستون‌های `Face crop`، `Person #`، `Name`، `Type`، `Sample #`، `Detection confidence`، `Created`، `Source file` و `Status` دارد. ستون `Name` مستقیماً قابل ویرایش است؛ با دوبارکلیک یا کلید `F2` نام را تغییر دهید و با خروج از سلول، نام پس از اعتبارسنجی در SQLite ذخیره می‌شود. نام خالی یا تکراری پذیرفته نمی‌شود و تغییر نام برای همهٔ نمونه‌های همان شخص اعمال می‌شود. گزینهٔ `Group by person` برای هر شخص ردیف سرگروه با علامت `-`/`+` دارد؛ با کلیک روی سرگروه، نمونه‌های آن گروه باز یا جمع می‌شوند. عملیات `Add image` برای شخص جدید و `Add to selected person` برای افزودن تصویر ناشناس به شخص انتخاب‌شده، به‌همراه `Import folder`، `Delete sample`، `Delete person` و `Rename person` از همین پنجره انجام می‌شوند؛ تصویر حذف‌شده/قدیمی با وضعیت `Image missing` مشخص می‌شود.
- `Check similarity` پنجرهٔ جداگانه‌ای باز می‌کند که threshold پیش‌فرض آن `0.40` و هماهنگ با threshold شناسایی SFace است. به‌صورت پیش‌فرض نمونه‌های مشابه همان فرد نیز نمایش داده می‌شوند؛ با فعال‌کردن `Only different people` جفت‌هایی که `PersonId` یکسان دارند حذف می‌شوند. هر جفت امکان `Merge into first person` دارد؛ اگر مجموع نمونه‌ها از سقف ۱۰ عبور کند ادغام انجام نمی‌شود. `Export to folder` پوشهٔ زمان‌دار، تصاویر جفت‌ها و `similarity-report.csv` تولید می‌کند.

## سازگاری نسخه‌های قدیمی

فایل‌های قدیمی دوربین و processing item هنگام Load به ساختار `Cameras`، `Rois` و `NamedRoi.Processing` مهاجرت می‌شوند. فیلدهای flat قدیمی Face/Plate نیز یک‌بار به `Options` typed همان item تبدیل می‌شوند تا مدل و thresholdهای موجود از بین نروند.

## افزودن قابلیت جدید

افزودن یک DLL یا یک `IProcessingPipeline` کافی نیست. قابلیت جدید باید با `ProcessingModuleRegistration` در `ProcessingRegistry` ثبت شود؛ `CameraPipelineCoordinator` با `ProcessingType` آن را پیدا می‌کند و UI descriptor ثبت‌شده را برای فهرست نوع‌ها نشان می‌دهد. ماژول جدید باید یک options class typed خودش داشته باشد، آن را از `CameraProcessingSettings.Options` هنگام ساخت pipeline بخواند و در `Process()` دوباره JSON را parse نکند. برای editor خودکار، `OptionsType` را در `ProcessingModuleDescriptor`/registration معرفی کنید؛ فرم تنظیمات برای هر `EditorKey` ناشناخته یک editor عمومی نشان می‌دهد و نیازی به تغییر `CameraSettingsForm` نیست. فقط اگر ماژول editor اختصاصی می‌خواهد باید `EditorKey` اختصاصی و wiring UI آن را اضافه کند. مقدار `CameraProcessingSettings.Type` در JSON همچنان string است تا فایل‌های قبلی سازگار بمانند، اما مقایسه‌های کد باید با `CameraProcessingSettings.Kind` انجام شوند. برای ROIهای موجود که فیلد `Processing` ندارند، migration از مسیرهای پردازش فعال دوربین مقدار اولیه می‌سازد.

الگوی ثبت یک قابلیت جدید مانند Human:

```csharp
public sealed class HumanProcessingOptions
{
    public string ModelFile { get; set; } = "human.onnx";
    public float Confidence { get; set; } = 0.5f;
}

registry.Register(new ProcessingModuleRegistration(
    ProcessingType.Parse("Human"),
    "Human detection",
    AnalysisKind.Object,
    (context, item) =>
    {
        HumanProcessingOptions options = item.GetOptions<HumanProcessingOptions>();
        return [new HumanPipeline(options, item.MaxFps, item.Threads)];
    },
    typeof(HumanProcessingOptions)));
```

در Composition Root نیز همین registration را کنار registrationهای Plate و Face ثبت کنید؛ descriptor از همان registry به UI می‌رسد و editor عمومی فرم properties این کلاس را نمایش و ذخیره می‌کند. اگر قابلیت خروجی بصری هندسی دارد، آن را در `PipelineResult.Overlays` برگردانید و `AnalysisDetection` را فقط برای نتایج معنایی نگه دارید. در نتیجه برای Human نیازی به تغییر `CameraRuntime` یا افزودن branch جدید در `CameraSettingsForm` نیست.
