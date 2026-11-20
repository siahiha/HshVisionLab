# HshDetectionEngin.Plate

ماژول تشخیص پلاک این راهکار است. قراردادهای عمومی آن در `HshDetectionEngin.Abstractions` قرار دارند و راهنمای بازسازی کامل در [راهنمای اصلی](../docs/REBUILD_GUIDE.md) نگهداری می‌شود.

## مسئولیت‌ها

| جزء | سطح دسترسی | مسئولیت |
| --- | --- | --- |
| `PlateModule` | عمومی | metadata هویت و مسیر استقرار ماژول و factory ثبت آن؛ registration خودکار انجام نمی‌دهد |
| `YoloDetector` | داخلی | اجرای ONNX و تبدیل خروجی مدل به تشخیص‌ها |
| `PlatePipeline` | داخلی | اجرای مسیر تشخیص پلاک و آماده‌سازی نتیجهٔ نمایشی |
| `SecureModelLoader` | داخلی | بازکردن موقت بستهٔ مدل برای ایجاد نشست ONNX |

این پروژه نباید UI، تنظیمات سراسری برنامه یا منطق صدور/اعتبارسنجی لایسنس را مالک شود؛ منطق اعتبارسنجی در `HshDetectionEngin.Licensing` است و wiring قابلیت در Engine و برنامهٔ میزبان انجام می‌شود.

## مدل‌ها

در خروجی برنامه، مدل‌های بسته‌بندی‌شده از مسیر زیر بارگذاری می‌شوند:

```text
Models\\Plate\\<ModelFile>.hshmodel
```

برای نصب ساده‌تر، می‌توان همهٔ packageها را به‌صورت flat در `Models\\` کنار executable نیز قرار داد. مسیر قدیمی `Modules\\Plate\\Models` فقط برای سازگاری بررسی می‌شود. در اجرای Debug/Visual Studio، `HshDetectionEngin.Plate\\Models` نیز fallback است تا مدل‌های repository بدون کپی‌شدن به `bin` قابل تست باشند. مسیر خام ONNX هرگز fallback اجرایی نیست و از خروجی build/publish کپی نمی‌شود. فایل‌های OCR فقط به‌صورت package و manifest هم‌نام آن‌ها در runtime قرار می‌گیرند. نام منطقی پیش‌فرض detector در تنظیمات `best.onnx` است و runtime بستهٔ `best.hshmodel` را پیدا می‌کند.

### قرارداد استاندارد OCR

هر مدل OCR کنار فایل package یک manifest با پسوند `.ocr.json` دارد. این manifest
نوع decoder، alphabet و اندازهٔ ورودی را اعلام می‌کند؛ نمونه:

```json
{
  "task": "plate_ocr",
  "decoder": "yolo_character",
  "alphabet": "persian_plate",
  "inputWidth": 416,
  "inputHeight": 416
}
```

خروجی همهٔ decoderها به `PlateOcrResult` مشترک تبدیل می‌شود. decoderهای فعلی
`crnn_ctc`، `cnn_glyph` و `yolo_character` هستند. بنابراین مدل تشخیص کادر
(`ModelFile`) و مدل OCR (`CharacterModelFile`) کاملاً مستقل انتخاب می‌شوند؛
اضافه‌کردن یک خانوادهٔ جدید فقط به ثبت decoder آن خانواده نیاز دارد.

### EOS_IDR

مدل ترکیبی `EOS_IDR` از منبع `RCDATA/IDR_BESTO_MODEL` در `LPServer.dll` استخراج شده است؛ خود DLL به برنامه اضافه نشده است. فایل خام استخراج‌شده برای ابزارهای توسعه در `HshDetectionEngin.Tools/RawModels/Plate/EOS_IDR.onnx` قرار دارد و مانند سایر مدل‌های خام وارد Git یا خروجی اجرایی نمی‌شود. نسخهٔ قابل استقرار `Models/Plate/EOS_IDR.hshmodel` با ابزار موجود `Protect-Models.ps1` بسته‌بندی شده و همراه build/publish کپی می‌شود.

در تنظیمات تشخیص پلاک، `EOS_IDR.onnx` را انتخاب کنید. ورودی مدل ثابت و برابر `640×640` و خروجی آن `[1, 33, 8400]` است. مدل ۲۹ کلاس شامل کادر پلاک، ارقام و حروف دارد. با `CharacterRecognitionEnabled=false` فقط حروف خود مدل استفاده می‌شوند. با فعال‌کردن این گزینه، متن کامل و مطمئن خود مدل همچنان اولویت دارد و OCR انتخاب‌شده فقط هنگام ناکافی بودن حروف به‌عنوان پشتیبان اجرا می‌شود.

در این مدل، کلاس `PLATE` شناسهٔ ۲۲ دارد؛ detector با استفاده از نام کلاس‌ها، کادر و حروف را به شناسه‌های داخلی برنامه تبدیل می‌کند. برچسب `TAXI` به «ت» تبدیل می‌شود؛ نماد ویژهٔ `ACC` در قرارداد فعلی پلاک معمولی پشتیبانی نمی‌شود و به یک حرف دیگر تبدیل نخواهد شد. استخراج مدل، مراحل تکمیلی اختصاصی DLL را منتقل نمی‌کند؛ پیش‌پردازش، NMS، tracking و اعتبارسنجی متن همچنان از مسیر فعلی برنامه انجام می‌شوند.

SHA-256 فایل ONNX استخراج‌شده:

```text
1a3465d833cc3c4eebe2f913bf63fb7d9dd6b680b0b20f885b4caa947f4ca6c6
```

### chars_best_v26

مدل `chars_best_v26` از [مخزن عمومی Persian Plate Recognition](https://github.com/shahabbai/Persian_Plate_Recognition)
به پروژه اضافه شده است.
این مدل فقط روی crop پلاک اجرا می‌شود و خروجی end-to-end مدل YOLO26 را با شکل
`[1, 300, 6]` پردازش می‌کند؛ هر ردیف شامل مختصات `xyxy`، confidence و class است.
دو نسخهٔ قابل انتخاب وجود دارد:

```text
Models\\Plate\\chars_best_v26.hshmodel       # FP32، دقت مرجع
Models\\Plate\\chars_best_v26_int8.hshmodel # INT8، حجم و مصرف حافظه کمتر
```

نسخهٔ INT8 با calibration نمونه‌های crop پلاک ساخته شده و برای مقایسهٔ سرعت/دقت
ارائه می‌شود؛ نام منطقی پیش‌فرض پروژه همچنان `ocr_crnn.onnx` باقی می‌ماند تا پس از تست
روی دیتاست دوربین، بهترین گزینه انتخاب شود.


فایل‌های `.hshmodel` با سرآیند `HSHM0001` و AES رمزگذاری شده‌اند. برای ایجاد نشست ONNX، محتوای مدل موقتاً روی دیسک باز می‌شود و پس از ساخت نشست حذف می‌گردد. کلید توسعه از `HSH_DETECTION_LICENSE` خوانده می‌شود و فقط برای توسعه fallback دارد؛ آن را راهکار امنیتی کامل تلقی نکنید.

## رفتار پردازش

ورودی ماژول، تصویر ROI اصلی است و مختصات `AnalysisDetection.Bounds` باید نسبت به همان ROI باقی بماند. Engine هنگام نمایش، offset مربوط به ROI را اضافه می‌کند. اگر یک pipeline تصویر مقیاس‌خورده تولید می‌کند، خود آن pipeline باید مختصات را به فضای ROI اصلی بازگرداند.

پارامترهای مهم detector در `CameraSettings` عبارت‌اند از `ModelFile`، `InputSize`، `Confidence`، `NmsIoU`، `MaxFps` و `Threads`. تنظیمات OCR مستقل شامل `CharacterRecognitionEnabled`، `CharacterModelFile`، `CharacterConfidence` و `CharacterMaxFps` است؛ مقدار پیش‌فرض `CharacterRecognitionEnabled=false` است. `Preprocessing` ورودی detector را تغییر نمی‌دهد: detector letterbox و نرمال‌سازی ثابت خودش را اجرا می‌کند و این فیلد فقط برای مسیر legacy استخراج character کاربرد دارد.

در مدل ترکیبی مانند `EOS_IDR`، `Confidence` فقط آستانهٔ کادر پلاک است و `CharacterConfidence` هم آستانهٔ تشخیص تک‌تک کاراکترهای خود مدل و هم حداقل امتیاز خروجی OCR است؛ خاموش بودن OCR مستقل این آستانهٔ حروف را غیرفعال نمی‌کند. افزایش آستانهٔ کادر نباید کاراکترهای معتبر با امتیاز پایین‌تر را حذف کند. عدد `Score` روی تصویر امتیاز واقعی کادر و `Min` آستانهٔ تنظیم‌شده است. نتیجهٔ قرمز علاوه بر این دو عدد، علت رد شدن مانند متن ناقص یا قالب نامعتبر را نمایش می‌دهد؛ پذیرش همچنان به متن معتبر ایرانی نیاز دارد.

### OCR پشتیبان برای مدل ترکیبی

در پردازش مستقیم، قابلیت خواندن حروف از metadata کلاس‌های مدل تشخیص داده می‌شود. اگر کادر از `Confidence` عبور کند و هشت کاراکتر معتبر با امتیاز حداقل `CharacterConfidence` متن ایرانی معتبری بسازند، همان نتیجهٔ detector استفاده می‌شود؛ OCR اضافه اجرا نمی‌شود. اگر متن ناقص، نامعتبر یا کم‌اطمینان باشد و `CharacterRecognitionEnabled=true` باشد، crop خام همان پلاک به OCR انتخاب‌شده داده می‌شود. برای مدل صرفاً کادر، OCR فعال طبق قبل روی پلاک‌ها اجرا می‌شود. در حالت `TrackAndQueue` مسیر تکمیل مستقل قبلی حفظ می‌شود.

مدل OCR یک‌بار بارگذاری می‌شود؛ `CharacterMaxFps` و cache محدود به Trackهای فعال، نرخ فراخوانی پشتیبان را کنترل می‌کنند. نتیجهٔ OCR فقط با اطمینان کافی و قالب معتبر پذیرفته می‌شود. metadataهای `RecognitionSource` (`Detector`، `OcrFallback` یا `Ocr`)، `RecognitionModel` و `OcrFallbackReason` منبع متن نهایی را مشخص می‌کنند؛ روی پیش‌نمایش دسکتاپ، عبارت `OCR fallback` اجرای مسیر پشتیبان را نشان می‌دهد. وقتی OCR فعال است، حروف مطمئن از خروجی همان inference اولیه استفاده می‌شوند و برای تصمیم‌گیری پشتیبان، inference اضافهٔ detector روی crop انجام نمی‌شود.

## اتصال به برنامه

`PlatePipeline` API عمومی برای مصرف‌کننده‌های بیرونی نیست؛ `PlateModule.CreateRegistration` factory آن را در `ProcessingRegistry` ثبت می‌کند و `CameraPipelineCoordinator` برای هر ROI فعال که `Plate` در `NamedRoi.Processing` آن فعال باشد، نمونهٔ مستقل می‌سازد. فعال یا غیرفعال بودن پلاک از تنظیم ROI و قابلیت `Plate` در لایسنس کنترل می‌شود.

پس از تولید متن، `PlatePipeline` فقط پلاک ایرانی با الگوی `NNLNNNNN` را accepted می‌کند: دو رقم، یک حرف فارسی و پنج رقم. در حالت OCR مستقل، crop خام پلاک مستقیماً به مدل OCR داده می‌شود؛ در حالت خاموش بودن OCR، اگر detector character class تولید کند از مسیر legacy استفاده می‌شود. نتیجهٔ نامعتبر با `Accepted=false` برای overlay قرمز قابل مشاهده است، اما `DetectionRuntimeHost` آن را به history، trigger یا client ارسال نمی‌کند.

برای ساخت یا تغییر ماژول، قراردادهای [Abstractions](../HshDetectionEngin.Abstractions) را نشکنید و پیش از انتشار با مدل بسته‌بندی‌شده و یک جریان دوربین واقعی تست کنید.
