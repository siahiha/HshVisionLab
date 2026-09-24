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

برای نصب ساده‌تر، می‌توان همهٔ packageها را به‌صورت flat در `Models\\` کنار executable نیز قرار داد. مسیر قدیمی `Modules\\Plate\\Models` فقط برای سازگاری بررسی می‌شود. در اجرای Debug/Visual Studio، `HshDetectionEngin.Plate\\Models` نیز fallback است تا مدل‌های repository بدون کپی‌شدن به `bin` قابل تست باشند. اگر package وجود نداشته باشد، مسیر خام `Models\\<ModelFile>` به‌عنوان fallback بررسی می‌شود. مدل‌های ONNX و manifestهای OCR که در project ثبت شده‌اند هنگام build به خروجی کپی می‌شوند؛ مدل‌های package بزرگ باید جداگانه در deployment قرار گیرند. نام منطقی پیش‌فرض detector در تنظیمات `best.onnx` است.

### قرارداد استاندارد OCR

هر مدل OCR کنار فایل ONNX یک manifest با پسوند `.ocr.json` دارد. این manifest
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

### chars_best_v26

مدل `chars_best_v26` از [مخزن عمومی Persian Plate Recognition](https://github.com/shahabbai/Persian_Plate_Recognition)
به پروژه اضافه شده است.
این مدل فقط روی crop پلاک اجرا می‌شود و خروجی end-to-end مدل YOLO26 را با شکل
`[1, 300, 6]` پردازش می‌کند؛ هر ردیف شامل مختصات `xyxy`، confidence و class است.
دو نسخهٔ قابل انتخاب وجود دارد:

```text
Models\\Plate\\chars_best_v26.onnx       # FP32، دقت مرجع
Models\\Plate\\chars_best_v26_int8.onnx # INT8، حجم و مصرف حافظه کمتر
```

نسخهٔ INT8 با calibration نمونه‌های crop پلاک ساخته شده و برای مقایسهٔ سرعت/دقت
ارائه می‌شود؛ مدل پیش‌فرض پروژه همچنان `ocr_crnn.onnx` باقی می‌ماند تا پس از تست
روی دیتاست دوربین، بهترین گزینه انتخاب شود.


فایل‌های `.hshmodel` با سرآیند `HSHM0001` و AES رمزگذاری شده‌اند. برای ایجاد نشست ONNX، محتوای مدل موقتاً روی دیسک باز می‌شود و پس از ساخت نشست حذف می‌گردد. کلید توسعه از `HSH_DETECTION_LICENSE` خوانده می‌شود و فقط برای توسعه fallback دارد؛ آن را راهکار امنیتی کامل تلقی نکنید.

## رفتار پردازش

ورودی ماژول، تصویر ROI اصلی است و مختصات `AnalysisDetection.Bounds` باید نسبت به همان ROI باقی بماند. Engine هنگام نمایش، offset مربوط به ROI را اضافه می‌کند. اگر یک pipeline تصویر مقیاس‌خورده تولید می‌کند، خود آن pipeline باید مختصات را به فضای ROI اصلی بازگرداند.

پارامترهای مهم detector در `CameraSettings` عبارت‌اند از `ModelFile`، `InputSize`، `Confidence`، `NmsIoU`، `MaxFps` و `Threads`. تنظیمات OCR مستقل شامل `CharacterRecognitionEnabled`، `CharacterModelFile`، `CharacterConfidence` و `CharacterMaxFps` است؛ مقدار پیش‌فرض `CharacterRecognitionEnabled=false` است. `Preprocessing` ورودی detector را تغییر نمی‌دهد: detector letterbox و نرمال‌سازی ثابت خودش را اجرا می‌کند و این فیلد فقط برای مسیر legacy استخراج character کاربرد دارد.

## اتصال به برنامه

`PlatePipeline` API عمومی برای مصرف‌کننده‌های بیرونی نیست؛ `PlateModule.CreateRegistration` factory آن را در `ProcessingRegistry` ثبت می‌کند و `CameraPipelineCoordinator` برای هر ROI فعال که `Plate` در `NamedRoi.Processing` آن فعال باشد، نمونهٔ مستقل می‌سازد. فعال یا غیرفعال بودن پلاک از تنظیم ROI و قابلیت `Plate` در لایسنس کنترل می‌شود.

پس از تولید متن، `PlatePipeline` فقط پلاک ایرانی با الگوی `NNLNNNNN` را accepted می‌کند: دو رقم، یک حرف فارسی و پنج رقم. در حالت OCR مستقل، crop خام پلاک مستقیماً به مدل OCR داده می‌شود؛ در حالت خاموش بودن OCR، اگر detector character class تولید کند از مسیر legacy استفاده می‌شود. نتیجهٔ نامعتبر با `Accepted=false` برای overlay قرمز قابل مشاهده است، اما `DetectionRuntimeHost` آن را به history، trigger یا client ارسال نمی‌کند.

برای ساخت یا تغییر ماژول، قراردادهای [Abstractions](../HshDetectionEngin.Abstractions) را نشکنید و پیش از انتشار با مدل بسته‌بندی‌شده و یک جریان دوربین واقعی تست کنید.
