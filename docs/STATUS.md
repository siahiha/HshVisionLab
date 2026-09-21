# وضعیت پیاده‌سازی

مرجع بازسازی WinForms: [REBUILD_GUIDE.md](REBUILD_GUIDE.md). قرارداد دقیق
رابط وب در [WEB-UI-RECONSTRUCTION-SPEC.md](WEB-UI-RECONSTRUCTION-SPEC.md) است.
این فایل فقط وضعیت فعلی و مرزهای آگاهانه را نگه می‌دارد.

## پیاده‌سازی‌شده

- solution هشت‌پروژه‌ای با UI، Engine، Abstractions، Plate، Face، Licensing، LicenseRequest و LicenseIssuer.
- چنددوربینه، RTSP/Webcam/file، reconnect، newest-frame capture، ROI چندضلعی و Motion Gate.
- Plate با YOLO، OCR ایرانی، tracker، overlay و history.
- Face با YuNet، IoU tracking، preprocessing مستقل، SFace اختیاری و FaceDatabase؛ Unknownهای runtime به‌صورت افراد `Unknown #NNNN` با crop و embedding در database دائمی ذخیره می‌شوند و برای هر نفر حداکثر ۱۰ نمونه ثبت می‌شود.
- Face database پیشرفته با SQLite، ذخیرهٔ BLOB تصویر crop‌شده و embedding، تاریخ ایجاد، `PersonNumber` مشترک، سقف 10 نمونه برای هر نفر، گرید تصویری، ورود پوشه‌ای، بررسی تشابه، ادغام اشخاص و export تصاویر جفت‌شده/گزارش CSV.
- settings چنددوربینه با ROIهای نام‌گذاری‌شده و فهرست مستقل `NamedRoi.Processing` برای هر دوربین.
- packageهای `.hshmodel`، لایسنس RSA دستگاه‌محور و featureهای Plate/Face.
- LicenseRequest با save/copy درخواست فعال‌سازی و entry point STA.
- LicenseIssuer با مدیریت مشتری، مشخصات تماس/توضیحات، نگهداری مسیر آخرین private key، صدور آرشیوشده، export `Save As...` و جلوگیری از حذف مشتری دارای لایسنس.
- UI اصلی با دکمهٔ `Cameras` و فرم جداگانهٔ مدیریت دوربین‌ها، کنترل‌های Start/Stop/Edit/Delete بالای هر tile در نمای چنددوربینه، نمای بزرگ دوربین و پنل ROI زیر `Detected events`؛ ROI در حالت عادی مخفی است و روی تصویر overlay نمی‌شود.
- پنل وب `DetectionManagerUi` با نوار اکشن بالایی، tileهای دوربین، پنل تشخیص شامل crop و مشخصات متنی، نمای کامل دوربین و کنترل‌های ROI مستقل برای view/edit/new/delete/save/cancel/back.
- تنظیمات پردازش وب با دسته‌بندی Plate detection، Face detection، Face identification و Tracking/recording؛ انتخاب مدل‌ها از ComboBox و catalog مدل سرویس، بدون ورود متنی model file.
- سه backend دریافت `FFmpeg`، `LibVLC` و `MediaMTX`؛ مدل‌ها از `Models/Plate`، `Models/Face`، `Models` و مسیرهای legacy/Debug fallback فهرست می‌شوند.
- مسیر کم‌تاخیر MediaMTX در وب با WHEP خام و Overlay جداگانه: ویدئو مستقیماً از MediaMTX به مرورگر می‌رود و ROI، کادر تشخیص، label، confidence و primitiveهای پردازشی سمت کلاینت رسم می‌شوند؛ ویدئو برای Drawing دوباره encode نمی‌شود.
- endpoint سبک `GET /api/v1/streams/{cameraId}/overlay` برای هماهنگ‌کردن Overlay با WHEP و endpointهای WHEP خام برای path MediaMTX؛ مسیر `WebRtcGateway` کامپوزیت‌شده برای legacy حفظ شده است.
- UI اصلی دو زبانهٔ فارسی/انگلیسی است؛ دکمهٔ زبان در نوار بالا انتخاب را در `ui-language.txt` ذخیره می‌کند و برای اعمال کامل زبان، برنامه را restart می‌کند. متن فارسی نام چهره روی preview با رسم Unicode/GDI+ نمایش داده می‌شود.
- مرجع مستندات یکپارچه برای بازسازی و اسناد تخصصی کوتاه‌تر.

## محدودیت‌ها و backlog آگاهانه

- `CameraPipelineCoordinator` برای هر ROI فعال فهرست pipeline مستقل می‌سازد و آن‌ها را طبق `NamedRoi.Processing` اجرا می‌کند؛ `CameraRuntime` capture، Motion، lifecycle و policy خروجی را نگه می‌دارد.
- history تازهٔ Runtime محدود به ۱۵ رخداد در حافظه است؛ رخدادهای خارج‌شده با crop در `history-archive.json` کنار executable آرشیو می‌شوند و UI برای بازسازی تا ۱۰۰ کارت از آرشیو و حافظه استفاده می‌کند.
- discovery خودکار DLL وجود ندارد؛ registration قابلیت‌ها در Composition Root به‌صورت مرکزی انجام می‌شود. UI نوع‌ها و descriptorها را از registry می‌گیرد و برای Plate/Face یا قابلیت جدید branch اختصاصی ندارد؛ فقط editor اختصاصی نیازمند wiring جداگانه است.
- `BufferCount = 0` حالت newest-frame است؛ مقدار مثبت صف محدود فریم را برای هر دو pipeline همان دوربین فعال می‌کند و در صورت پرشدن قدیمی‌ترین فریم حذف می‌شود.
- تنظیمات Face، از جمله record confidence، cooldown، مدل، input size و thresholdها، برای هر آیتم در `CameraProcessingSettings` نگه‌داری و در Runtime از همان آیتم اعمال می‌شوند؛ مقدارهای سطح دوربین فقط default ساخت آیتم جدید هستند. `Threads` نیز برای هر آیتم به‌صورت `IntraOpNumThreads` اعمال می‌شود و UI کنترل‌های Plate و Face را برای آیتم انتخاب‌شده sync می‌کند.
- محافظت مدل client-side مطلق نیست؛ session از مدل موقت رمزگشایی‌شده استفاده می‌کند.
- LicenseIssuer فایل‌های دادهٔ خود را کنار executable نگه می‌دارد؛ برای نصب در مسیر غیرقابل‌نوشتن، مسیر storage باید به LocalAppData منتقل شود.
- `face-database.json` قدیمی در اولین اجرا به `face-database.db` مهاجرت می‌شود؛ رکوردهای legacy تصویر crop‌شده ندارند و در گرید به‌عنوان تصویر گمشده دیده می‌شوند.
