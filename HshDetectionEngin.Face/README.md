# HshDetectionEngin.Face

ماژول Face شامل تشخیص YuNet، tracker مبتنی بر IoU، preprocessing اختیاری و شناسایی اختیاری SFace است. SFace مسیر آماده‌سازی اختصاصی خود را دارد و صرفاً resize سادهٔ تصویر نیست. برای رفتار end-to-end به [REBUILD_GUIDE.md](../docs/REBUILD_GUIDE.md) مراجعه کنید.

## مدل‌ها و تنظیمات

- YuNet packageها: `Models/face_yunet_2023mar.hshmodel` (FP32) و `Models/face_yunet_2023mar_int8.hshmodel` (CPU سبک‌تر).
- SFace package: `Models/face_recognition_sface_2021dec.hshmodel` با embedding ورودی 112×112.
- مدل‌ها باید در Release جداگانه در `Models/Face` یا به‌صورت flat در `Models` کنار executable قرار گیرند؛ build آن‌ها را خودکار کپی نمی‌کند. مسیر قدیمی `Modules/Face/Models` برای سازگاری پشتیبانی می‌شود. در اجرای Debug/Visual Studio، `HshDetectionEngin.Face/Models` نیز fallback است تا مدل‌های repository بدون کپی‌شدن به `bin` قابل تست باشند.
- UI برای `FaceInputSize` مقدارهای 320، 416، 480، 512 و 640 عرضه می‌کند. این مقدار اندازهٔ میانی preprocessing است؛ package فعلی YuNet در نهایت ورودی ثابت `640×640` دریافت می‌کند.
- `Threads` در `SessionOptions.IntraOpNumThreads` برای sessionهای YuNet و SFace اعمال می‌شود و از تنظیم `Threads` همان آیتم Face می‌آید؛ UI کنترل Threads را هنگام انتخاب آیتم بین بخش‌های Plate و Face sync می‌کند. مقدار سطح دوربین فقط هنگام ساخت آیتم Face جدید به‌عنوان default استفاده می‌شود.

`FacePreprocessing` پیش‌پردازش عمومیِ مسیر تشخیص YuNet است، نه پیش‌نیاز SFace. مقدار پیش‌فرض و مقدار توصیه‌شده هنگام فعال‌بودن recognition، `None` است. حالت `Standard` روشنایی/کنتراست را تغییر می‌دهد و `Advanced` تصویر را خاکستری و equalize می‌کند؛ این تغییرات می‌توانند embedding SFace را ناپایدار کنند.

`FaceModule` registration و factory ساخت `FacePipeline` را در اختیار Composition Root می‌گذارد؛ `CameraPipelineCoordinator` مانند Plate آن را برای هر ROI مستقل اجرا می‌کند. `FacePipeline` هنگام ساخت، feature معتبر `Face` می‌خواهد. تصویر ROI پس از preprocessing ابتدا به `FaceInputSize` و سپس به ورودی ثابت `640×640` resize می‌شود و bounds خروجی به مختصات ROI اصلی برمی‌گردد. در مسیر SFace، پنج landmark چهره با تبدیل similarity به crop هم‌تراز `112×112` تبدیل می‌شوند؛ سپس مدل SFace ورودی RGB و embedding را تولید می‌کند و مقایسه با cosine similarity انجام می‌شود. بنابراین `FaceInputSize` اندازهٔ میانی YuNet است و اندازهٔ ورودی SFace نیست. نتیجه `AnalysisKind.Face` با confidence، bounds، TrackId، label و metadata هویت است.

## شناسایی و database

با SFace و `FaceDatabase`، embedding همهٔ نمونه‌های یک شخص مقایسه می‌شود و بالاترین similarity نتیجهٔ آن شخص است. database فعلی SQLite (`face-database.db`) است و اطلاعات شخص، حداکثر 10 نمونهٔ هر شخص، تاریخ ایجاد، embedding و تصویر crop‌شدهٔ aligned را در یک فایل نگه می‌دارد. چهرهٔ ناشناس جدید به‌صورت `Unknown #NNNN` در database ثبت می‌شود؛ برای جلوگیری از ثبت تکراری، از هر فرد ناشناس حداکثر هر 10 ثانیه یک نمونهٔ جدید ذخیره می‌شود. Rename از گرید، فرد را از حالت ناشناس خارج می‌کند و نمونه‌هایش در شناسایی نام‌دار استفاده می‌شوند. `PersonNumber` بین نمونه‌های یک شخص مشترک و `SampleNumber` برای هر عکس مستقل است.

```csharp
using HshDetectionEngin.Face;
using HshDetectionEngin.Licensing;

var license = LicenseValidator.Load(
    Path.Combine(AppContext.BaseDirectory, "license.hshlic"));
var database = FaceDatabase.Load(
    Path.Combine(AppContext.BaseDirectory, "face-database.db"));

using var pipeline = new FacePipeline(
    Path.Combine(AppContext.BaseDirectory, "Models", "Face", "face_yunet_2023mar.hshmodel"),
    recognitionModelPath: Path.Combine(AppContext.BaseDirectory, "Models", "Face", "face_recognition_sface_2021dec.hshmodel"),
    database: database,
    license: license);
```

برای enrollment با تصویر، از `FacePipeline.RegisterIdentity(name, faceImage, databasePath, sourceFileName)` استفاده کنید. API تصویر را بررسی می‌کند، دقیقاً یک چهره می‌خواهد، landmarkها را هم‌تراز می‌کند، crop JPEG و embedding را داخل SQLite ثبت می‌کند و سقف 10 نمونه را enforce می‌کند. اگر `databasePath` تهی یا `null` باشد، `FaceDatabase.Save` فایل `face-database.db` را کنار application checkpoint می‌کند؛ اگر مسیر دیگری داده شود، database با `VACUUM INTO` به فایل `.db` مقصد export می‌شود. تصویر بسیار کوچک، تار یا شدیداً پوشیده ممکن است برای ثبت مناسب نباشد؛ اگر SFace برای pipeline فعال نباشد، enrollment خطا می‌دهد. `FaceDatabase.GetSamples(includeImages: true)` برای نمایش تصویر در گرید و `FindSimilar` برای مقایسهٔ نمونه‌ها استفاده می‌شوند.

## رفتار UI/Runtime

Overlay صورت حدود 2.5 ثانیه باقی می‌ماند. confidence پایین‌تر از `FaceConfidence` در بازهٔ تصویری ۱۰٪ مجاز قرمز است و وارد tracking، recognition یا history نمی‌شود؛ مقدار پذیرفته‌شده سبز است. ثبت history علاوه بر پذیرش detector، عبور از `FaceRecordConfidence` را نیز لازم دارد. رخداد همان Track تا 5 ثانیه و همان identity تا cooldown تنظیم‌شده دوباره ثبت نمی‌شود.
