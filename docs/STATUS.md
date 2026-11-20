# وضعیت پیاده‌سازی

مرجع بازسازی WinForms: [REBUILD_GUIDE.md](REBUILD_GUIDE.md). قرارداد دقیق
رابط وب در [WEB-UI-RECONSTRUCTION-SPEC.md](WEB-UI-RECONSTRUCTION-SPEC.md) است.
این فایل فقط وضعیت فعلی و مرزهای آگاهانه را نگه می‌دارد.

## پیاده‌سازی‌شده

- در بررسی تأخیر باقی‌مانده با ROI غیرفعال، بافر مستقل decoder شناسایی شد: پیش‌فرض OpenCV می‌تواند تا ۱۶ thread رمزگشایی داشته باشد. RTSP در Windows و سرویس اکنون مستقیماً با FFmpeg، `CAP_PROP_N_THREADS=1` و گزینه‌های کم‌تأخیر `nobuffer`/`max_delay=0` باز می‌شود و تعداد واقعی thread، FPS و ابعاد منبع در diagnostics گزارش می‌شوند. این تنظیم مستقل از threadهای ONNX است. کاربر پس از اصلاح، رفع تأخیر تصویر Windows را تأیید کرده است؛ عدد تأخیر کامل به میلی‌ثانیه اندازه‌گیری نشده است.

  بررسی کوتاه دریافت از دوربین محلی H.264 با ابعاد `1280×720` و نرخ اسمی ۲۰ FPS، تعداد واقعی decoder را قبل از اصلاح ۱۶ و پس از اصلاح ۱ گزارش کرد؛ نرخ دریافت پس از اولین فریم به‌ترتیب حدود `20.12` و `19.94` FPS بود. زمان اولین فریم پس از open در این دو مشاهده حدود `1921` و `1166` میلی‌ثانیه شد. این مقایسهٔ کوتاه بدون inference و UI است و تأخیر حرکت دست تا نمایش، درصد کاهش CPU یا کاهش کل RAM را اندازه نمی‌گیرد.

- دریافت RTSP در Windows و سرویس فقط FFmpeg مستقیم با یک thread decoder و نگه‌داری جدیدترین فریم است؛ TCP/UDP قابل انتخاب است. ظرفیت و بودجهٔ بافر تکمیل تشخیص مستقل باقی می‌مانند. preview Windows سقف ۱۵ FPS، اندازهٔ متناسب با محل نمایش تا `1280×720`، Mat قابل استفادهٔ مجدد و انتقال مالکیت Bitmap بدون clone دارد. برای هر دوربین قابل مشاهده یک Bitmap منتظر و یک تصویر نمایشی نگه داشته می‌شود؛ دوربین مخفی یا پنجرهٔ minimizeشده preview نمی‌سازد و capture/inference ادامه دارد. آمار محلی پس از decode تا رسم UI به ۱۲۰ نمونه محدود است. مقایسهٔ کمی CPU/RAM قبل/بعد هنوز گزارش نشده است.

- فیلتر قابل‌خاموش‌کردن Median ۳×۳ روی crop رنگ پیش از resize مدل اضافه شده؛ فریم اصلی و OCR دست‌نخورده می‌مانند. کنترل فارسی/انگلیسی و ذخیره/بازخوانی تنظیمات وجود دارد. کاهش نویز مصنوعی آزموده شده؛ بهبود دقت واقعی هنوز اندازه‌گیری نشده است.

- کادر زندهٔ رنگ با موقعیت فعلی پلاک حرکت می‌کند؛ مختصات نمونهٔ واقعی اولیه جدا حفظ می‌شود. پیش‌بینی White روی ناحیهٔ عمدتاً تیره به Unknown با علت مشخص رد می‌شود، بدون حدس رنگ جایگزین یا تکرار inference. آزمون حرکت کادر و مثال مصنوعی تیره با بازتاب اضافه شده‌اند.

طرح بعدی، هنوز پیاده‌سازی‌نشده: [تشخیص رنگ با مدل سبک از یک فریم](VEHICLE-COLOR-SINGLE-FRAME.fa.md)، یک inference برای نمونهٔ نهایی پلاک، صف مستقل و تکمیل همان رکورد بدون انتظار OCR. تهیه/آموزش مدل و ارزیابی واقعی جزء کار باقی‌مانده است؛ فهرست زیر رفتار فعلی را بیان می‌کند.

- رنگ و جهت صرفاً اطلاعات تکمیلی‌اند: Sampling/Unknown رنگ یا جهت نامعلوم، مانع رویداد مستقیم یا ورود به صف OCR نیست؛ خطای تنظیمات یا محاسبهٔ این قابلیت‌ها نیز pipeline اصلی را متوقف نمی‌کند. ثبت پلاک با تنها یک فریم در حالت مستقیم و بافر و با رنگ در حال جمع‌آوری، نامشخص یا غیرفعال، آزموده شده است.
- مدل ازپیش‌آموزش‌دیدهٔ Intel OMZ 0039 برای رنگ روی کادر بالای پلاک، بستهٔ محافظت‌شده و تبدیل عددی بررسی‌شده به ONNX اضافه شد؛ خوشه‌بندی حذف شده است. هفت رنگ و Unknown، امتیاز/فاصلهٔ دو رنگ و cache تک‌مشاهده‌ای دارد. مدل هنگام ساخت graph آماده می‌شود و خطای آن پذیرش پلاک را متوقف نمی‌کند. ارزیابی دقت روی برش‌های واقعی و worker مستقل باقی است.
- جهت حرکت مشترک برای همهٔ taskهای دارای TrackId پایدار، شامل Plate/Face/Palm: جهت هشت‌گانه، جهت و بردار صفر برای جسم ثابت، تفکیک Unknown، تنظیم بازه/آستانهٔ لرزش برای هر task در Windows، خط ورود/خروج اختیاری با حاشیهٔ تحمل، preview پیکان/خط و حفظ metadata زمان نمونه در تکمیل ناهمزمان. مسیرهای Sequential/Parallel، ماژول سفارشی، ذخیره/بازخوانی، رابط فارسی/انگلیسی و عبور از خط با ورودی مصنوعی آزموده شده‌اند؛ کیفیت دوربین واقعی و اندازه‌گیری CPU هنوز ارزیابی نشده است.
- حالت Tracking/بافر تکمیل تشخیص در برنامهٔ Windows برای Face/Plate: تعریف تشخیص در تنظیمات عمومی دوربین، انتخاب مقصد هم‌نوع در task ROI، ظرفیت و حافظهٔ مستقل هر نوع، یک worker ناهمزمان SFace/OCR برای هر دوربین، حفظ فریم/زمان نمونه، retry/TTL/cache و پنجرهٔ وضعیت بافر. تنظیمات قدیمی Inline می‌مانند. صف، تنظیمات Windows، runtime با منبع مصنوعی و recognizerهای package‌شده پیش‌تر آزموده شده‌اند؛ benchmark و ارزیابی کیفیت روی دوربین واقعی همچنان لازم است.
- solution شامل UI، Engine، Abstractions، Identity، Plate، Face، Palm، Licensing، Service، LicenseRequest و LicenseIssuer است.
- چنددوربینه، RTSP/Webcam/file، reconnect، newest-frame capture، ROI چندضلعی و Motion Gate.
- Plate با YOLO، OCR ایرانی، tracker، overlay و history.
- Plate با detector و OCR مستقل: مدل کادر از `ModelFile` و مدل OCR از `CharacterModelFile` انتخاب می‌شود. مدل‌های OCR با manifest استاندارد `.ocr.json` در catalog مشترک وب و WinForms ثبت می‌شوند و CRNN، CNN و YOLO character به خروجی `PlateOcrResult` مشترک normalize می‌شوند.
- Face با YuNet، IoU tracking، preprocessing مستقل، SFace اختیاری و FaceDatabase؛ Unknownهای runtime به‌صورت افراد `Unknown #NNNN` با crop و embedding در database دائمی ذخیره می‌شوند و برای هر نفر حداکثر ۱۰ نمونه ثبت می‌شود.
- Face database پیشرفته با SQLite، ذخیرهٔ BLOB تصویر crop‌شده و embedding، تاریخ ایجاد، `PersonNumber` مشترک، سقف 10 نمونه برای هر نفر، گرید تصویری، ورود پوشه‌ای، بررسی تشابه، ادغام اشخاص و export تصاویر جفت‌شده/گزارش CSV.
- Identity database مرکزی با `People`، `PersonPlates`، `FaceSamples` و `PalmSamples`؛ هر شخص یک `PersonId` مشترک دارد و فرم Windows تب‌های مشخصات، پلاک، چهره و کف دست را مدیریت می‌کند.
- Event Store و automation محلی Windows: رخدادهای Plate/Face/Palm با crop، فریم خام، metadata و payload در فایل‌های local ذخیره می‌شوند؛ فرم‌های `Detection history` و `Triggers and workflows` جست‌وجو، preview، حذف، trigger، Webhook/HTTP، SQLite، retry و log را بدون اتصال به سرویس وب ارائه می‌کنند. تب `Application subscription` در Windows همان مدل چندپروفایلی را برای ثبت داخلی، camera/ROI scope، محور تشخیص، required/includeهای Face/Plate/Palm، unknown policy، artifact، window و cooldown فراهم می‌کند.
- Palm با BlazePalm/RTMDet، tracker، enrollment و شناسایی اختیاری CCNet/PPNet؛ هر کف‌دست پذیرفته‌شده در `identity-database.db` جست‌وجو می‌شود، match شناخته‌شده با نام/metadata به overlay و history می‌رود و match‌نشده به‌صورت `Unknown Palm #NNNN` ذخیره می‌شود. اگر مدل recognition موجود نباشد، crop تشخیص‌داده‌شده به‌صورت رکورد ناشناس بدون embedding ذخیره می‌شود تا در فرم Windows یا صفحهٔ وب `مدیریت افراد` قابل مشاهده و انتساب به شخص باشد؛ همهٔ مدل‌های انتخاب‌پذیر Palm در `Models/Palm` به‌صورت `.hshmodel` قرار می‌گیرند.
- settings چنددوربینه با ROIهای نام‌گذاری‌شده و فهرست مستقل `NamedRoi.Processing` برای هر دوربین.
- packageهای `.hshmodel` برای همهٔ قابلیت‌ها، مدل‌های خام توسعه فقط در `HshDetectionEngin.Tools/RawModels/{Plate,Face,Palm}`، لایسنس RSA دستگاه‌محور و featureهای Plate/Face/Palm.
- LicenseRequest با save/copy درخواست فعال‌سازی و entry point STA.
- LicenseIssuer با مدیریت مشتری، مشخصات تماس/توضیحات، نگهداری مسیر آخرین private key، صدور آرشیوشده، export `Save As...` و جلوگیری از حذف مشتری دارای لایسنس.
- UI اصلی با دکمهٔ `Cameras` و فرم جداگانهٔ مدیریت دوربین‌ها، کنترل‌های Start/Stop/Edit/Delete بالای هر tile در نمای چنددوربینه، نمای بزرگ دوربین و پنل ROI زیر `Detected events`؛ ROI در حالت عادی مخفی است و روی تصویر overlay نمی‌شود.
- پنل وب `DetectionManagerUi` با نوار اکشن بالایی، tileهای دوربین، پنل تشخیص شامل crop و مشخصات متنی، نمای کامل دوربین و کنترل‌های ROI مستقل برای view/edit/new/delete/save/cancel/back.
- تنظیمات پردازش وب برای Plate، Face و Palm با تب‌های detection، recognition/identification و tracking/recording؛ کارت‌های detailed کرکره‌ای، انتخاب مدل از catalog سرویس و فیلتر سخت‌گیرانهٔ مدل بر اساس modality، بدون ورود متنی model file. `DetectorKind` در UI کنترل جدا ندارد و برای Palm از مدل انتخابی تعیین می‌شود.
- `InputSize` مدل‌محور است: مدل ثابت اندازهٔ tensor خودش را اعلام می‌کند و مدل YOLO پویا گزینه‌های stride-aligned استاندارد `320/416/480/512/640` را در catalog سرویس اعلام می‌کند؛ WinForms و UI وب همین منبع مشترک را مصرف می‌کنند. YuNetهای فعلی `640×640` هستند.
- موتور Windows و سرویس هر دو RTSP را مستقیماً با FFmpeg می‌گیرند. پخش MediaMTX/WHEP برای ورودی RTSP فقط در سرویس مدیریت می‌شود و از مسیر دریافت تشخیص مستقل است. مدل‌ها از `Models/Plate`، `Models/Face`، `Models` و مسیرهای legacy/Debug fallback فهرست می‌شوند.
- UI وب فعلی tile، focus و ROI زنده را با `CompositeWebRtcStream` و `WebRtcGateway` کامپوزیت‌شده نمایش می‌دهد. در CameraEditor، قابلیت `canWhep` برای source فعال RTSP تعیین می‌کند preview در حال اجرا زنده باشد؛ sourceهای دیگر snapshot دارند.
- نمایش کامپوزیت‌شدهٔ وب ابعاد CSS viewer را بدون DPR می‌گیرد و با سقف
  `1280×720`، ابعاد مضرب ۱۶، حفظ نسبت منبع در حد گردکردن و حداکثر ۱۵ FPS
  آماده می‌کند. ورودی inference با ابعاد اصلی باقی می‌ماند. هر دوربین یک worker
  و encoder مستقل دارد و خروجی فشرده بین viewerهای همان دوربین مشترک است.
  حداکثر یک Bitmap در حال encode و یک newest pending نگه داشته می‌شود؛
  duplicate/old sequence حذف می‌شود و I420 در ابعاد ثابت دوباره استفاده می‌شود.
  تغییر ابعاد codec را کنترل‌شده جایگزین و keyframe تولید می‌کند؛ آزمون native
  کاهش/افزایش اندازه، ابعاد header VP8 و ادامهٔ خروجی را پوشش می‌دهد.
- Preview وب فقط برای viewer متصل یا تقاضای snapshot تولید می‌شود؛ signaling
  ناقص render/encode را فعال نمی‌کند. Resize همان session را به‌روز می‌کند؛ UI
  هر ۱۰ ثانیه heartbeat می‌فرستد و در صفحهٔ مخفی یا خارج‌شدن viewer از دید
  session را می‌بندد. Session دارای viewport پس از ۴۵ ثانیه بدون تمدید و pending
  پس از ۳۰ ثانیه منقضی می‌شود. Snapshot تقاضای سه‌ثانیه‌ای با حداکثر ۵ FPS
  ایجاد می‌کند. `GET /api/v1/streams/diagnostics` آمار encode، سن فریم، FPS،
  ابعاد و صف را می‌دهد. بررسی‌های پیشین با ورودی مصنوعی انجام شده‌اند؛
  benchmark واقعی CPU/RAM و استقرار سرویس از این کار نتیجه‌گیری نمی‌شوند. جزئیات در [Rendering و WebRTC](service/02-RENDERING-WEBRTC.md) است.
- درگاه مستقل MediaMTX/WHEP برای source فعال RTSP و endpoint سبک `GET /api/v1/streams/{cameraId}/overlay` موجود است. مصرف‌کنندهٔ خام می‌تواند ROI، motion ROI، کادر، label، confidence و primitiveهای پردازشی را سمت کلاینت رسم کند؛ ویدئوی خام برای Drawing دوباره encode نمی‌شود. کامپوننت `RawMediaMtxStream` در سورس موجود است و مسیر فعال tile/focus فعلی نیست.
- association پلاک/چهره در runtime فعال است و از همان فریم یا پنجرهٔ زمانی حداکثر `Association.MaxWindowMs` (پیش‌فرض 1500ms) استفاده می‌کند؛ خروجی با `SameFrame`، `TemporalAssociation` یا `Standalone` مشخص می‌شود. در حالت پیش‌فرض `RequireSameRoi=true`، هر دو component باید در یک ROI باشند؛ با خاموش‌کردن آن، pair شدن بین ROIهای متفاوت همان دوربین ممکن است. این منطق association مکانی/مالکیتی نیست و برای حالت مبهمِ چند پلاک یا چند چهره pair قطعی تولید نمی‌کند.
- `ServiceSettingsDocument.HistorySubscription` سناریوهای چندپروفایلی ثبت داخلی را
  نگه می‌دارد؛ هر profile می‌تواند محور، دوربین، ROI، requiredها، includeهای Face/
  Plate/Palm، unknown policy، artifact و cooldown خودش را داشته باشد. این policy
  فقط هنگام ساخت event canonical اعمال می‌شود؛ پنل تشخیص و تاریخچهٔ عمومی بر اساس
  آن فیلتر نمی‌شوند و رخدادهای همهٔ دوربین‌ها را نشان می‌دهند. `TriggerDefinition`
  مسیر مستقل ارسال به کلاینت است و trigger match‌شده payload کامل موجود، از جمله
  Palm، را ارسال می‌کند.
- در Windows مستقل، هر profile فقط برای دوربین‌ها/ROIهای انتخاب‌شده تصمیم می‌گیرد
  چه رخداد نهایی در `desktop-events.json` ساخته شود. در سناریوی ترکیبی مانند
  `Mode=Plate` همراه `FaceRequired=true` و `PlateRequired=true`، componentهای خام
  موقتاً staging می‌شوند و پس از pair شدن در `WindowMs` فقط رکورد `PlateFace`
  وارد Event Store و پنل تشخیص می‌شود؛ staging در query، پنل و تاریخچه نمایش داده
  نمی‌شود. `Detection history` از همین رکوردهای نهایی بازسازی می‌شود و حذف همه/بازه
  علاوه بر Event Store، archive و history حافظهٔ دوربین را نیز پاک می‌کند.
- برای کنترل مصرف حافظه، بررسی metadata مدل‌ها cache می‌شود و association از کپی تکراری فریم کامل برای تشخیص‌های تکراری جلوگیری می‌کند.
- UI اصلی دو زبانهٔ فارسی/انگلیسی است؛ دکمهٔ زبان در نوار بالا انتخاب را در `ui-language.txt` ذخیره می‌کند و برای اعمال کامل زبان، برنامه را restart می‌کند. متن فارسی نام چهره روی preview با رسم Unicode/GDI+ نمایش داده می‌شود.
- مرجع مستندات یکپارچه برای بازسازی و اسناد تخصصی کوتاه‌تر.

## محدودیت‌ها و backlog آگاهانه

- حالت TrackAndQueue فعلاً فقط در میزبان Windows فعال است. پشتیبانی API/UI وب و مصرف completion با قرارداد source/artifact سرویس هنوز انجام نشده است؛ Service بدون opt-in این حالت را start نمی‌کند. قرارداد مشترک و معیار ادامهٔ توسعه در [ARCHITECTURE.md](ARCHITECTURE.md) است.
- `CameraPipelineCoordinator` برای هر ROI فعال فهرست pipeline مستقل می‌سازد و آن‌ها را طبق `NamedRoi.Processing` اجرا می‌کند؛ `CameraRuntime` capture، Motion، lifecycle و policy خروجی را نگه می‌دارد.
- history تازهٔ Runtime محدود به ۱۵ رخداد در حافظه است؛ اما پنل Windows برای بازسازی
  از رکوردهای نهایی Event Store استفاده می‌کند و staging را کنار می‌گذارد. حذف تاریخچه
  از فرم `Detection history` هم Event Store و هم history/archive runtime را پاک می‌کند.
- discovery خودکار DLL وجود ندارد؛ registration قابلیت‌ها در Composition Root به‌صورت مرکزی انجام می‌شود. UI نوع‌ها و descriptorها را از registry می‌گیرد و برای Plate/Face یا قابلیت جدید branch اختصاصی ندارد؛ فقط editor اختصاصی نیازمند wiring جداگانه است.
- دریافت در هر دو میزبان همیشه newest-frame است؛ بافر تکمیل تشخیص Windows ظرفیت، حافظه و طول عمر مستقلی از فریم منتظر دریافت دارد.
- تنظیمات Face، از جمله record confidence، cooldown، مدل، input size و thresholdها، برای هر آیتم در `CameraProcessingSettings` نگه‌داری و در Runtime از همان آیتم اعمال می‌شوند؛ مقدارهای سطح دوربین فقط default ساخت آیتم جدید هستند. `Threads` نیز برای هر آیتم به‌صورت `IntraOpNumThreads` اعمال می‌شود و UI کنترل‌های Plate و Face را برای آیتم انتخاب‌شده sync می‌کند.
- محافظت مدل client-side مطلق نیست؛ session از مدل موقت رمزگشایی‌شده استفاده می‌کند.
- LicenseIssuer فایل‌های دادهٔ خود را کنار executable نگه می‌دارد؛ برای نصب در مسیر غیرقابل‌نوشتن، مسیر storage باید به LocalAppData منتقل شود.
- `identity-database.db` دیتابیس مرکزی People، PersonPlates، FaceSamples و PalmSamples است. صفحهٔ وب `مدیریت افراد` سه تب چهره/پالم/پلاک برای هر فرد، نمایش افراد Palm-only و حذف گروهی با متعلقات را دارد. در load، `face-database.db` و `palm-database.db` قدیمی به‌صورت مستقل migration می‌شوند؛ پس از آن runtime فقط دیتابیس مرکزی را باز نگه می‌دارد.
