# مشخصات مرجع بازسازی UI وب

این سند قرارداد بصری و رفتاری `DetectionManagerUi` است. هدف آن این است که یک
پیاده‌سازی مستقل، با مطالعهٔ همین سند و بدون حدس‌زدن از روی UI قدیمی WinForms،
به رفتار و ظاهر فعلی وب نزدیک شود. در اختلاف بین این سند و هر سند قدیمی‌تر،
کد منبع فعلی ملاک است.

## ۱. منبع حقیقت و محدوده

منبع حقیقت UI این فایل‌ها هستند:

| فایل | چیزی که باید از آن خوانده شود |
| --- | --- |
| `DetectionManagerUi/src/App.tsx` | درخت کامپوننت‌ها، routeها، متن‌ها، state و رفتار کلیک/ذخیره |
| `DetectionManagerUi/src/api.ts` | مسیرهای HTTP و شکل درخواست‌های UI |
| `DetectionManagerUi/src/hooks.ts` | polling، cache، mutation و SignalR |
| `DetectionManagerUi/src/types.ts` | مدل دادهٔ TypeScript |
| `DetectionManagerUi/src/styles.css` | layout، اندازه‌ها، breakpointها و ظاهر |
| `HshDetectionService/ServiceApi.cs` | endpointهای واقعی سرویس و catalog مدل‌ها |

این سند مربوط به وب است. `README.md`، `docs/ARCHITECTURE.md` و
`docs/REBUILD_GUIDE.md` علاوه بر وب، رفتار برنامهٔ WinForms را نیز شرح می‌دهند؛
عبارت‌هایی مثل `CameraManagerForm`، `DockStyle`، دکمهٔ حذف بالای tile و نوار
زبان، به‌طور خودکار به وب تعمیم داده نمی‌شوند.

## ۲. قرارداد کلی ظاهر

UI یک shell تاریک با حس برنامهٔ دسکتاپ ویندوزی دارد، اما در مرورگر اجرا می‌شود.
جهت پیش‌فرض متن فارسی است و URLها، modelها، confidence و زمان‌ها در جاهایی که
در کد `dir="ltr"` دارند چپ‌به‌راست نمایش داده می‌شوند.

ساختار ثابت shell:

```text
app-shell
└─ app-body
   ├─ sidebar                       عرض 248px در دسکتاپ
   └─ main-area
      ├─ topbar                     ارتفاع 56px
      └─ page-content               حداکثر عرض 1660px، padding 27/30/58px
```

`window-titlebar` در نسخهٔ فعلی وجود ندارد؛ shell مستقیماً با `app-body` شروع
می‌شود. صفحه از جریان عادی مرورگر استفاده می‌کند و اجزایی مثل جدول‌ها، لیست‌ها
و گریدها در صورت نیاز می‌توانند اسکرول داخلی داشته باشند.

### ۲.۱ توکن‌های بصری نهایی

در `styles.css` یک theme اولیه وجود دارد، اما بلوک `Windows desktop shell` در
ادامهٔ همان فایل theme نهایی را override می‌کند. برای بازسازی، مقادیر نهایی
زیر را استفاده کنید:

```text
body background: #111214 با radial-gradient آبی در بالا-راست
surface: #1d1e20
surface-2: #242527
surface-3: #2b2d30
surface-hover: #323438
primary: #4cc2ff / primary-strong: #0078d4
green: #6ccb8d / amber: #f8c36b / red: #ff7b84
font: Segoe UI Variable Text, Segoe UI, Vazirmatn, Tahoma
panel: linear-gradient(145deg, rgba(35,36,38,.98), rgba(29,30,32,.98))
panel border radius: 7px
button radius: 4px
```

کارت‌ها و کنترل‌ها باید compact و خاکستری-تیره باشند؛ رنگ آبی فقط برای active،
دکمهٔ primary، لینک و statusهای زنده به‌کار می‌رود. پنل تشخیص پس‌زمینهٔ
`#151619`، کارت رخداد `#222429` و crop بدون تصویر `#0b0c0e` دارد.

### ۲.۲ sidebar

sidebar شامل این ترتیب است:

1. brand با آیکون Zap، متن `HSH VISION` و `DETECTION MANAGER`؛
2. برچسب `محیط مدیریت سرویس`؛
3. navigation با routeها و متن دقیق زیر؛
4. footer اتصال با نقطهٔ سبز/زرد، متن وضعیت سرویس و
   `HshDetectionService · .NET 8`.

| مسیر | متن ناوبری | active rule |
| --- | --- | --- |
| `/` | `نمای کلی` | فقط مسیر دقیق `/` |
| `/cameras` | `مدیریت دوربین‌ها` | مسیر و زیرمسیرهای آن |
| `/faces` | `پایگاه چهره` | مسیر و زیرمسیرهای آن |
| `/events` | `تاریخچه تشخیص` | `/events` و `/events/:eventId` |
| `/triggers` | `تریگرها و کلاینت‌ها` | مسیر و زیرمسیرهای آن |
| `/settings` | `تنظیمات سرویس` | مسیر و زیرمسیرهای آن |

وقتی `service.status.ready` درست باشد، کنار route نمای کلی یک pulse سبز دیده
می‌شود. footer در حالت آماده `سرویس آماده و متصل` و در غیر این صورت
`سرویس نیازمند بررسی` را نشان می‌دهد.

### ۲.۳ topbar

topbar در سمت راست‌به‌چپ شامل breadcrumb با آیکون Server، متن `HSH Vision /`
و عنوان route فعلی است. در موبایل دکمهٔ Menu اضافه می‌شود. سمت دیگر یک
`service-chip` با نقطهٔ وضعیت و `Node <هشت کاراکتر اول serviceNodeId>` و avatar
حرف `H` قرار دارد.

در عرض حداکثر 820px sidebar به drawer ثابت از سمت راست تبدیل می‌شود، scrim نشان
داده می‌شود و دکمهٔ بستن داخل brand قرار می‌گیرد. در عرض حداکثر 500px service
chip مخفی می‌شود.

## ۳. route اصلی و داشبورد

### ۳.۱ ساختار دقیق `/`

داشبورد به همین ترتیب render می‌شود:

1. `dashboard-commandbar` در ابتدای صفحه؛
2. `hero-card`؛
3. `stats-grid` با چهار کارت؛
4. `dashboard-live-layout` با دو ستون؛
5. `dashboard-runtime-grid` در زیر دو ستون بالا.

commandbar عنوان `مرکز کنترل دوربین‌ها` و زیرعنوان `نمای شبکه و کنترل سریع
سرویس` دارد و اکشن‌های آن دقیقاً این‌ها هستند:

| متن | رفتار |
| --- | --- |
| `نمای شبکه` | دکمهٔ ظاهری/soft؛ action جداگانه ندارد |
| `شروع همه` | همهٔ دوربین‌های متوقف را start می‌کند |
| `توقف همه` | همهٔ دوربین‌های درحال اجرا را stop می‌کند |
| `تازه‌سازی` | فقط فهرست دوربین‌ها را refetch می‌کند |
| `تنظیمات` | به `/settings` می‌رود |

در hero، badge `سرویس آنلاین` یا `در انتظار سرویس`، عنوان
`مرکز مدیریت تشخیص`، متن توضیحی، تعداد دوربین، `Sequence #...` و تعداد افراد
پایگاه چهره نمایش داده می‌شود. چهار stat card عبارت‌اند از:
`دوربین‌های فعال`، `رخدادهای پایدار`، `افراد پایگاه چهره` و `میانگین inference`.

### ۳.۲ دیوار دوربین و پنل تشخیص

`dashboard-live-layout` در دسکتاپ `grid-template-columns:
minmax(0, 1fr) minmax(0, 250px)` دارد؛ ستون دوربین در سمت چپ و پنل تشخیص در
سمت راست است. `DetectionHistoryPanel` نیز `max-width: 250px` دارد تا فضای
اصلی برای دیوار دوربین باقی بماند. در عرض 820px یا کمتر، دو بخش زیر هم قرار
می‌گیرند و پنل تشخیص عرض کامل می‌گیرد. بنابراین پنل تشخیص نباید با یک کارت
متنی ساده یا پنل تاریخچهٔ جداگانه جایگزین شود.

سمت چپ `dashboard-workspace` است:

- عنوان عادی: `نمای زندهٔ همهٔ دوربین‌ها`؛
- توضیح: `همان الگوی چنددوربینهٔ HshVisionLab؛ برای بزرگ‌نمایی روی تصویر کلیک کنید.`؛
- دکمهٔ `مدیریت دوربین‌ها` به `/cameras`؛
- `camera-wall` با حداکثر ۶ tile در هر صفحه؛ صفحه‌بندی با دکمه‌های `قبلی` و
  `بعدی` و شمارندهٔ بازهٔ نمایش داده می‌شود. دوربین‌های صفحات دیگر mount
  نمی‌شوند تا WHEP، snapshot و overlay آن‌ها فعال نماند؛
- برای 1 تا 4 tile تعداد ستون متناسب با تعداد tile است و برای 5 یا 6 tile در
  دسکتاپ سه ستون استفاده می‌شود؛
- برای 0 دوربین، Empty با عنوان `دوربینی تعریف نشده` نمایش داده می‌شود.

سمت راست `DetectionHistoryPanel` است و عنوان فارسی آن `پنل تشخیص` است. دکمهٔ
`همه` به `/events` می‌رود. حداکثر 8 رخداد با ترتیب نزولی `sequence` در این
پنل دیده می‌شود. هر کارت رخداد:

```text
detected-event-card
├─ crop تصویر، 88×88px در دسکتاپ
├─ نام دوربین
├─ عنوان تشخیص (face.label یا plate.plateText یا eventType)
├─ Confidence: NN%
├─ زمان با Intl.DateTimeFormat('fa-IR')
└─ نام ROI یا scenario
```

crop از اولین artifactای انتخاب می‌شود که type آن شامل یکی از این عبارت‌ها
باشد: `platecrop`، `detectioncrop`، `facealignedcrop`، `roiraw` یا
`roiannotated`. در مسیر فعلی آرشیو، `RoiRaw` برای crop خام استفاده می‌شود.
اگر چنین artifactای نباشد، به‌جای تصویر آیکون Database نمایش داده می‌شود.
کلیک روی کارت به `/events/<eventId>` می‌رود.

### ۳.۳ وضعیت runtime داشبورد

در زیر `dashboard-live-layout` یک پنل واقعی با عنوان `وضعیت runtime` وجود دارد؛
این پنل نباید از بازسازی حذف شود. زیرعنوان آن
`منبع، فریم، inference و فریم‌های حذف‌شده` است و برای حداکثر پنج دوربین ردیف
نمایش می‌دهد. هر ردیف نام دوربین، نقطهٔ وضعیت و یکی از این دو مقدار را دارد:

- دوربین فعال: `<FPS> FPS · <inferenceMs> ms`؛
- دوربین متوقف: `متوقف`.

## ۴. tile دوربین و نمای متمرکز

### ۴.۱ CameraTile

هر tile شامل تصویر، نوار ابزار بالایی و shade پایینی است. tile در حالت فعال
border-top سبز و در حالت متوقف border-top زرد دارد. تصویر حدود 184px ارتفاع
دارد و object-fit آن cover است.

نوار بالای هر tile فقط این کنترل‌ها را دارد:

1. Start/Stop با tooltip `شروع دوربین` یا `توقف دوربین`؛
2. ویرایش با tooltip `ویرایش دوربین` و navigation به
   `/cameras?camera=<id>`؛
3. ذره‌بین/نمای کامل با tooltip `نمای تمام‌صفحه و ROI`.

در tile وب فعلی دکمهٔ Delete وجود ندارد. حذف دوربین فقط باید در جایی مستند شود
که واقعاً در کد وب برای آن control وجود دارد؛ `api.deleteCamera` در API تعریف
شده، اما در کد فعلی صفحهٔ وب دکمهٔ قابل مشاهدهٔ حذف دوربین render نمی‌شود.

تصویر tile بر اساس `camera.captureBackend` انتخاب می‌شود:

- `MediaMTX`: کامپوننت فعال `RawMediaMtxStream`؛
- هر مقدار دیگر: `SnapshotImage` با polling تصویری.

### ۴.۲ کلیک ذره‌بین: حالت view مستقل

در Dashboard، هم کلیک روی تصویر و هم دکمهٔ ذره‌بین state
`focusedCameraId` را تنظیم می‌کند و `CameraFocusWorkspace` را در همان پنل
میانی جایگزین wall می‌کند. این modal جداگانه نیست و پنل تشخیص سمت راست همچنان
در کنار آن باقی می‌ماند.

حالت اولیه همیشه `view` است؛ ورود به نمای متمرکز نباید خودکار حالت edit باشد.
سرصفحهٔ آن این موارد را دارد:

- `بازگشت به همهٔ دوربین‌ها`؛
- نام دوربین و وضعیت `در حال دریافت`/`متوقف`؛
- `شروع` یا `توقف`؛
- `ذخیره تنظیمات` که فقط در `roiMode === view` فعال است.

پایین تصویر، ROIهای موجود به‌صورت tab با نام و تعداد نقطه دیده می‌شوند. در
حالت `view` این سه ابزار نمایش داده می‌شوند:

- `ویرایش ROI`؛
- `ROI جدید`؛
- `حذف ROI`.

`ویرایش ROI` یک clone از ROI انتخاب‌شده می‌سازد و به edit می‌رود. `ROI جدید`
یک ROI با نام `ROI N`، فعال و بدون نقطه می‌سازد و به new می‌رود. حذف با تأیید
`window.confirm` انجام می‌شود و حذف فوری از draft است.

در حالت edit یا new فقط این ابزارها نمایش داده می‌شوند:

- `ذخیره ROI`؛
- `لغو`؛
- متن راهنما برای افزودن نقطه.

ذخیره ROI حداقل سه نقطه می‌خواهد، draft دوربین را اعتبارسنجی می‌کند و سپس
`PUT /api/v1/cameras/{id}` می‌فرستد. کلیک روی تصویر به انتهای polygon نقطهٔ
نرمال‌شدهٔ `0..1` اضافه می‌کند. دکمهٔ Undo آخرین نقطه را حذف می‌کند.

### ۴.۳ CameraFullscreen غیرفعال

`CameraFullscreen` در `App.tsx` برای مسیر/کامپوننتی قدیمی باقی مانده اما در
درخت فعلی Dashboard استفاده نمی‌شود؛ `onFullscreen` فعلی به
`setFocusedCameraId` وصل است. بازسازی UI فعال باید `CameraFocusWorkspace` را
مبنای نمای ذره‌بین قرار دهد، نه کامپوننت بلااستفادهٔ modal.

## ۵. صفحهٔ دوربین‌ها `/cameras`

صفحهٔ `Cameras` با `PageHead` عنوان `دوربین‌ها و تشخیص`، توضیح مدیریت
چنددوربینه و دکمهٔ `افزودن دوربین` دارد.

در حالت عادی، layout دو ستون با عرض ستون فهرست 285px (در override نهایی CSS
270px) و محتوای باقی‌مانده است:

- ستون فهرست: عنوان `دوربین‌ها`، تعداد منبع تصویر، جست‌وجوی `جست‌وجوی دوربین`
  و ردیف‌های دوربین با نام، FPS/inference یا `متوقف` و status dot؛
- ستون editor: اگر دوربین انتخاب نشده باشد placeholder
  `یک دوربین را انتخاب کنید`؛ در غیر این صورت `CameraEditor`.

فرم افزودن، `نام`، `Source URL` با placeholder `rtsp://... یا 0` و دکمه‌های
`انصراف` و `ساخت دوربین` دارد. تا وقتی نام یا source خالی است ساخت غیرفعال
است.

### ۵.۱ CameraEditor

سرصفحهٔ editor نام، source، وضعیت و دکمهٔ Start/Stop و `ذخیره تغییرات` دارد.
اگر license معتبر نباشد error box با پیام runtime نمایش داده می‌شود. تب‌ها
دقیقاً این سه موردند:

1. `پیش‌نمایش و Drawing`؛
2. `General / Motion`؛
3. `Processing / ROI`.

در تب preview، دو ستون وجود دارد: live panel و `وضعیت لحظه‌ای`. برای
MediaMTX عنوان تصویر `پخش کم‌تاخیر MediaMTX` و زیرعنوان
`WHEP خام MediaMTX + Overlay سبک سمت کلاینت` است؛ تصویر دوباره encode یا با Drawing پردازش نمی‌شود. برای backend
دیگر عنوان `آخرین فریم کامپوزیت‌شده` و زیرعنوان
`ROI و Drawing روی آخرین تصویر سرویس رسم می‌شوند.` است. همین تب دکمهٔ
`ROI جدید` و `حذف ROI` را دارد.

`RuntimePanel` مقادیر `نرخ دریافت`، `زمان inference`، `رزولوشن`، `ROI / task`،
`pipeline فعال` و `Dropped frames` را نشان می‌دهد.

تفاوت مهم با نمای متمرکز این است که `CameraEditor` در تب preview،
`RoiCanvas` را با مقدار پیش‌فرض `editable=true` render می‌کند؛ یعنی کلیک روی
تصویر در این تب مستقیماً نقطه به ROI draft اضافه می‌کند. کنترل‌های صریح view/edit
و ذخیره/لغو مربوط به `CameraFocusWorkspace` داشبورد هستند.

## ۶. General / Motion و پروفایل‌ها

تب General دو پنل اول و یک پنل سراسری پروفایل دارد.

### General و Capture

فیلدها و نوع کنترل:

- `نام دوربین` — input؛
- `Source URL` — input چپ‌به‌راست و wide؛
- `RTSP receiver` — select با گزینه‌های `FFmpeg`، `LibVLC`، `MediaMTX`؛
- `Transport` — select با `TCP` و `UDP`؛
- `Reconnect delay (sec)` — number؛
- `Buffer count` — number با hint `0 یعنی فقط آخرین فریم`؛
- checkbox `نمایش Drawing، ROI و کادر تشخیص`.

### Motion Gate و نرخ‌ها

عنوان پنل `Motion Gate و نرخ‌ها`، toggle اصلی `motionGateEnabled` و فیلدهای
`Motion FPS`، `Motion threshold`، `Changed percent`، `ROI scale %`،
`Motion hold (ms)`، `Active detection FPS` و `Idle detection FPS` دارد.
hint فیلد آخری `0 یعنی توقف inference` است.

### Performance profiles

سه button در یک grid سه‌ستونه وجود دارد:

- `Weak / virtual 6-core` — `INT8 · ۵ FPS · یک thread`؛
- `Balanced / normal system` — `پیشنهادی · ۸ FPS · دو thread` و کلاس
  `recommended`؛
- `High / realtime` — `۱۵ FPS · چهار thread`.

انتخاب profile نرخ‌ها، threads، buffer، active/idle FPS، اندازهٔ ورودی Face و
TopK را به draft اعمال می‌کند و thresholdها را تغییر نمی‌دهد. پایین آن
`مدل‌های قابل انتخاب سرویس` و حداکثر 8 مدل اول catalog نمایش داده می‌شود.

## ۷. Processing / ROI

این تب با عنوان `Processing tree` و توضیح ترتیب اجرای ROI/taskها، دکمه‌های
`Plate` و `Face` برای افزودن task دارد. سمت چپ درخت ROI با نام، تعداد task و
فعال/غیرفعال‌بودن ROI است؛ سمت دیگر نام ROI، checkbox `ROI فعال` و task editor
را نشان می‌دهد.

هر task یک کارت با نام، type، Toggle فعال، حذف، ویرایش نام و options دارد.

### ۷.۱ چهار بخش بصری task چهره/پلاک

کد فعلی چهار بخش بصری دارد؛ سه بخش اول شماره‌گذاری شده‌اند و بخش چهارم
عنوان مستقل دارد:

1. `۱. تشخیص پلاک` — `Plate detection`؛
2. `۲. تشخیص چهره` — `Face detection`؛
3. `۳. شناسایی چهره` — `Face identification`؛
4. `ردیابی و ثبت سابقه` — `Tracking and recording`.

بخش Plate شامل ComboBox `Model`، `Input size`، `Preprocessing`، `Confidence`,
`NMS IoU`، `Max processing FPS`، `Threads`، `Buffer count` و
`Track max misses` است.

بخش Face detection شامل ComboBox `Detection model`، `Input size`,
`Preprocessing`، `Detection confidence`، `NMS IoU` و `Max candidate faces` است.

بخش Face identification شامل ComboBox `Recognition model (SFace)`،
`Known-person threshold` و `Unknown-person match threshold` است و Toggle آن
`recognitionEnabled` را کنترل می‌کند.

بخش Tracking and recording شامل `Max processing FPS`، `Threads`، `Buffer
count`، `History record confidence`، `History event cooldown (sec)`،
`Tracking IoU` و `Track max misses` است.

### ۷.۲ مدل‌ها

هیچ‌یک از modelها input متنی اصلی نیستند. `ModelSelect` یک HTML `select` است.
داده از `GET /api/v1/service/models` می‌آید و برای قابلیت‌های `plate`،
`faceDetection` و `faceRecognition` فیلتر می‌شود. متن option از `model.name`
است؛ اگر مقدار فعلی در catalog نبود، یک option موقت `Current` ساخته می‌شود.
اگر فیلتر capability هیچ گزینه‌ای ندهد، `ModelSelect` کل catalog را به‌عنوان
fallback نشان می‌دهد.

سرویس فقط فایل‌های `*.hshmodel` را از این مسیرهای top-level جست‌وجو می‌کند:

```text
<service-base>/Models/Plate
<service-base>/Models/Face
<service-base>/Models
<service-base>/Modules/Plate/Models
<service-base>/Modules/Face/Models
Models پوشهٔ پروژهٔ HshDetectionEngin.Plate در زنجیرهٔ parentها (Debug)
Models پوشهٔ پروژهٔ HshDetectionEngin.Face در زنجیرهٔ parentها (Debug)
```

catalog برای نمایش، نام فایل `.hshmodel` را با پسوند `.onnx` به `name` تبدیل
می‌کند، اما `relativePath` همچنان مسیر نسبی فایل package است و `packaged` در
وضعیت فعلی `true` برگردانده می‌شود. capability از module و نام فایل به‌صورت
`Plate`، `FaceDetection` یا `FaceRecognition` تعیین می‌شود. فهرست‌سازی
پوشه‌ای است و به یک یا دو model ثابت hard-code نشده است.

## ۸. صفحهٔ Face Database `/faces`

`PageHead` عنوان `پایگاه دادهٔ چهره`، search با placeholder
`جست‌وجوی نام یا شماره` و دکمهٔ `Similar samples` دارد. layout دو ستون است:

- `People / Samples`: تعداد افراد و نمونه‌ها، checkbox `Group by person`،
  فهرست person، input `نام شخص جدید` و دکمهٔ `افزودن`؛
- detail: با انتخاب شخص، نام/شماره/نوع/تاریخ، Rename، حذف شخص، آپلود چند فایل
  (`افزودن تصویر / folder`)، نمونه‌ها، انتقال sample و حذف sample.

Unknown با avatar علامت `?` دارد و شخص عادی حرف اول نام را به‌عنوان avatar
می‌گیرد. Similarity در modal با threshold عددی، checkbox `Only different
people`، نتایج جفت تصویر، similarity، `ادغام در اولی` و `بستن` است.

## ۹. تاریخچه و جزئیات event

`/events` عنوان `تاریخچه تشخیص و evidence` دارد، search
`پلاک، نام، دوربین...`، select سناریوهای `همهٔ سناریوها`، `پلاک`، `چهره` و
`پلاک + چهره` و دکمهٔ `تازه‌سازی` دارد. کنار آن کنترل حذف تاریخچه با گزینه‌های
`همهٔ تاریخچه`، `امروز`، `۷ روز اخیر`، `۳۰ روز اخیر` و `بازهٔ سفارشی` قرار دارد؛
حذف با تأیید کاربر انجام می‌شود. گرید با تغییر این انتخاب همان لحظه به‌عنوان
پیش‌نمایش حذف فیلتر می‌شود و تعداد رخدادهای محدوده را نشان می‌دهد.

در حالت عادی، عنوان صفحه، توضیح کوتاه و فیلترها تا حد امکان در یک ردیف
فشرده قرار می‌گیرند. پیش از انتخاب رخداد، preview فقط یک نوار کوتاه با پیام
`یک رخداد را انتخاب کنید` است تا فضای عمودی برای گرید تاریخچه حفظ شود.

جدول `Event Store` در پایین صفحه و تمام‌عرض قرار دارد تا ستون‌های بیشتری از
`رخداد`، `دوربین / ROI`، `Trigger` و `زمان` هم‌زمان دیده شوند؛ در صورت کمبود
عرض، خود جدول اسکرول افقی دارد. انتخاب ردیف، `EventPreview` را در بالای جدول
نشان می‌دهد. preview شامل title و یک ردیف فشردهٔ اطلاعات رکورد شامل event type،
دوربین، ROI، sequence، زمان، وضعیت trigger و component summary با confidence است.
تمام artifactها شامل frame/crop در یک ردیف افقی قرار می‌گیرند و در صورت زیاد
بودن تعدادشان همان ردیف اسکرول افقی دارد. payload کامل تریگر و components به‌صورت
پیش‌فرض بسته است و با دکمهٔ `نمایش payload` باز و با `بستن payload` بسته می‌شود.
فریم کامل و cropهای آرشیوی از snapshot خام قبل از رسم ROI و detection می‌آیند؛
Drawingهای زندهٔ SVG نباید داخل فایل artifact ذخیره‌شده burn-in شوند.
`/events/:eventId` همین preview را با PageHead مستقل `جزئیات رخداد` نشان می‌دهد.

## ۱۰. تریگرها `/triggers`

در ابتدای همین صفحه، `ClientSubscriptionTester` برای تست policy همان اتصال وب قرار دارد. این بخش تنظیمات دوربین یا trigger سرویس را تغییر نمی‌دهد و فقط با `sessionStorage` ذخیره می‌شود و هنگام اعمال، subscription SignalR و query تاریخچهٔ همان صفحه را به‌روزرسانی می‌کند.

گزینه‌های تست شامل `All`، `Plate` و `KnownFace`، پنجرهٔ association، اجباری‌بودن Face/Plate، ارسال چهرهٔ ناشناس و محدودکردن به دوربین‌های انتخابی است. این تست برای شبیه‌سازی سه client مستقل استفاده می‌شود؛ هر client واقعی باید subscription خودش را هنگام `Subscribe` ارسال کند.

عنوان صفحه `تریگرها و کلاینت‌ها` و دکمهٔ `تریگر جدید` است. ستون چپ فهرست
تعریف‌های سرویس و cooldown را نشان می‌دهد؛ ستون راست فرم انتخاب‌شده را دارد.

فرم شامل نام، Toggle فعال، سناریوهای `PlateRecognition`، `FaceRecognition` و
`PlateFaceMatch`، cooldown، `Label equals`، `Plate text equals`، حداقل
confidence، Face identity، scope دوربین‌ها و کانال اعلان است. خالی‌بودن scope
یعنی همهٔ دوربین‌ها. کانال‌ها از نوع `LiveEvent`، `Webhook` یا
`WindowsEvent` هستند و target/URL، Toggle و حذف دارند. دکمه‌های پایانی
`حذف تریگر` و `ذخیره تریگر` هستند.

## ۱۱. تنظیمات سرویس `/settings`

در tab `Runtime و retention` فیلدهای association نیز وجود دارند: `Max association window (ms)` برای پنجرهٔ زمانی اتصال پلاک/چهره و `Association فقط داخل همان ROI` برای الزام تطابق ROI.

PageHead عنوان `تنظیمات سرویس` و دو اکشن `Reload runtime` و
`ذخیره همهٔ تنظیمات` دارد. tabهای دقیق:

1. `Runtime و retention` — auto-start، Preview FPS، Max event queue، Event
   retention، Artifact retention و Webhook retry؛
2. `HTTP و امنیت` — Listen URLs چندخطی، Allow loopback without API key و API
   key با header `X-Hsh-Api-Key`؛
3. `قابلیت و مدل` — Processing modules با وضعیت available/unavailable و
   Model inventory با نام، module و `HSH package` یا `legacy ONNX`.

## ۱۲. دریافت تصویر و Overlay فعال

### ۱۲.۱ مسیر MediaMTX

در UI فعلی `RawMediaMtxStream` یک WebRTC peer connection می‌سازد، video
transceiver را `recvonly` می‌کند و SDP را از WHEP می‌گیرد:

```text
Camera RTSP
  ├─ MediaMTX path → local RTSP → Engine/CameraRuntime → inference/state
  └─ MediaMTX path → POST WHEP → Browser <video>
                                      └→ GET overlay → LiveOverlaySvg
```

در شروع stream این endpoint استفاده می‌شود:

```text
POST /api/v1/streams/{cameraId}/webrtc/whep/{viewerId}
Accept: application/sdp
Content-Type: application/sdp
```

با unmount شدن component، DELETE همان viewer ارسال می‌شود. مسیر `PATCH` برای
trickle ICE در API helper وجود دارد، ولی start فعلی offer کامل را با POST
ارسال می‌کند.

برای جلوگیری از عقب‌افتادن تدریجی پخش، این component هر دو ثانیه سلامت اتصال و
پیشرفت فریم را پایش می‌کند. حالت‌های `failed/closed` در PeerConnection یا ICE،
توقف فریم برای حدود ۷ ثانیه، و jitter buffer بالاتر از حدود ۱٫۵ ثانیه برای چند
ثانیه، recovery را فعال می‌کنند. recovery فقط همان stream را می‌بندد و با
`viewerId` جدید WHEP را می‌سازد؛ در نتیجه refresh صفحه یا قطع stream دوربین‌های
دیگر لازم نیست. retry خطای handshake نیز scoped به همان tile است و با backoff
۲، ۵، ۱۰ و حداکثر ۳۰ ثانیه انجام می‌شود؛ پس از اتصال سالم شمارنده reset می‌شود.

`useLiveOverlay` برای هر stream فعال endpoint زیر را ابتدا بلافاصله و سپس با
`setTimeout` poll می‌کند. در thumbnailهای داشبورد فاصلهٔ poll برابر 400ms است؛
نمای متمرکز برای latency کمتر از مقدار پیش‌فرض 180ms استفاده می‌کند. زمان‌بندی
بعدی بعد از پایان درخواست انجام می‌شود، بنابراین درخواست‌های overlay روی هم
انباشته نمی‌شوند:

```text
GET /api/v1/streams/{cameraId}/overlay?ts=<Date.now()>
```

`LiveOverlaySvg` روی video یک SVG هم‌اندازهٔ فریم می‌گذارد و این موارد را رسم
می‌کند: `rois` معمولی، `motionRois`، primitiveهای
Rectangle/Circle/Points/Polyline/Polygon با رنگ و ضخامت، و detection box با
رنگ سبز برای accepted و قرمز برای rejected، label/text، track id و confidence.
ROI معمولی خط نارنجی solid و نام ROI دارد؛ motion ROI با هندسهٔ مقیاس‌شده حول
مرکز و خط زرد dashed نمایش داده می‌شود. پس در مسیر MediaMTX، تصویر raw است و
Drawing به‌صورت burn-in داخل ویدئو encode نمی‌شود.

### ۱۲.۲ مسیر غیر MediaMTX

`SnapshotImage` در thumbnailهای داشبورد از endpoint زیر استفاده می‌کند و پس از
هر load با تأخیر 400ms درخواست بعدی می‌سازد؛ خطا با تأخیر 500ms دوباره تلاش
می‌شود. با مخفی‌شدن تب مرورگر، timerها متوقف می‌شوند و با visible شدن دوباره
از سر گرفته می‌شوند:

```text
GET /api/v1/streams/{cameraId}/snapshot?ts=<Date.now()>
```

در `RoiCanvas` نیز برای backend غیر MediaMTX snapshot هر 250ms refresh می‌شود.
برای MediaMTX همان `RawMediaMtxStream` داخل ROI canvas قرار می‌گیرد و ابعاد
video برای aspect ratio ثبت می‌شود.

### ۱۲.۳ مسیر legacy

`api.webRtcOffer` و `api.closeWebRtc` و کامپوننت‌های
`CompositeWebRtcStream`/`LivePreview` برای مسیر قدیمی offer/answer در سورس
وجود دارند، اما در درخت route فعلی Dashboard و CameraEditor استفاده نمی‌شوند.
مستندات بازسازی UI نباید آن‌ها را مسیر پیش‌فرض تصویر معرفی کند.

## ۱۳. API و polling مورد نیاز

Endpointهای اصلی که UI واقعاً استفاده می‌کند:

```text
GET  /api/v1/service/status              هر 5 ثانیه
GET  /api/v1/cameras                     هر 5 ثانیه
GET  /api/v1/cameras/{id}
PUT  /api/v1/cameras/{id}
POST /api/v1/cameras
POST /api/v1/cameras/{id}/start|stop|restart
GET  /api/v1/service/models              stale time = 60 ثانیه
GET  /api/v1/service/capabilities        stale time = 60 ثانیه
GET  /api/v1/events?limit=2000...         هر 5 ثانیه
GET  /api/v1/events/{id}
GET  /api/v1/face/people
GET  /api/v1/face/people/{id}/samples
GET  /api/v1/triggers
GET  /api/v1/settings
```

علاوه بر polling، `useDetectionStream` به `/hubs/detections` با SignalR وصل
می‌شود، sequence آخر را در `sessionStorage` با کلید
`hsh-detection-last-sequence` نگه می‌دارد و پس از reconnect از همان sequence
درخواست replay می‌کند. اگر SignalR در دسترس نباشد polling رخداد fallback است.

## ۱۴. اندازه‌ها و responsive acceptance

برای حفظ شباهت بصری، این مقادیر مهم‌اند:

| عنصر | دسکتاپ | responsive |
| --- | --- | --- |
| sidebar | 248px | drawer 270px در ≤820 |
| topbar | 56px | 52px در ≤820 |
| dashboard detection panel | حداکثر 250px | تمام‌عرض در ≤820 |
| camera tile | 184px | 170px در ≤500 |
| ROI canvas | حداقل 345px | 300px در focus در ≤820 |
| fullscreen legacy/modal | حداکثر 1480×900 و 96vh | یک ستون در ≤900 |

در ≤1100، stats دو ستونه و settings/preview تک‌ستونه می‌شوند. در ≤820،
layoutهای camera/faces/events/triggers تک‌ستونه، formها تک‌ستونه و taskها
دوستونه می‌شوند. در ≤500، stats تک‌ستونه و sample grid دو ستونه است.

## ۱۵. چک‌لیست پذیرش بازسازی

بازسازی فقط زمانی با این سند هم‌خوان است که همهٔ موارد زیر قابل مشاهده و قابل
تست باشند:

- shell شامل sidebar، topbar و routeهای شش‌گانه باشد؛
- داشبورد commandbar، hero، چهار stat، wall، پنل crop+متن تشخیص و پنل runtime
  را به همین ترتیب داشته باشد؛
- tile فقط Start/Stop، Edit و Fullscreen داشته باشد و حذف به آن نسبت داده نشود؛
- ذره‌بین workspace میانی را با `CameraFocusWorkspace` عوض کند و خودکار edit
  نشود؛
- در focus ابزارهای view/edit/new/delete و در edit/new ذخیره/لغو وجود داشته
  باشد؛
- MediaMTX از WHEP خام و Overlay SVG با polling حدود 180ms استفاده کند؛
- MediaMTX در UI پایش سلامت WebRTC و recovery خودکار per-tile داشته باشد تا
  lag تدریجی با refresh کل صفحه برطرف نشود؛
- backendهای دیگر snapshot باشند؛
- تنظیمات CameraEditor سه tab گفته‌شده و چهار بخش پردازش گفته‌شده را داشته باشد؛
- همهٔ modelها از catalog سرویس و پوشه‌های مدل، نه از یک input متنی ثابت، بیایند؛
- صفحات Faces و Triggers با layout دو ستونه و editor سمت مقابل فهرست پیاده
  شوند؛ Events باید preview را بالای history تمام‌عرض نشان دهد و Settings
  باید grid تنظیمات و tabهای واقعی خود را داشته باشد؛
- در موبایل breakpointهای 1100، 820 و 500 رفتار ذکرشده را رعایت کنند.
