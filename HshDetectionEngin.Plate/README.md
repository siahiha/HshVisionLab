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

برای نصب ساده‌تر، می‌توان همهٔ packageها را به‌صورت flat در `Models\\` کنار executable نیز قرار داد. مسیر قدیمی `Modules\\Plate\\Models` فقط برای سازگاری بررسی می‌شود. در اجرای Debug/Visual Studio، `HshDetectionEngin.Plate\\Models` نیز fallback است تا مدل‌های repository بدون کپی‌شدن به `bin` قابل تست باشند. اگر package وجود نداشته باشد، مسیر خام `Models\\<ModelFile>` به‌عنوان fallback بررسی می‌شود. نام منطقی پیش‌فرض در تنظیمات `best.onnx` است؛ مدل‌ها هنگام build خودکار کپی نمی‌شوند.

فایل‌های `.hshmodel` با سرآیند `HSHM0001` و AES رمزگذاری شده‌اند. برای ایجاد نشست ONNX، محتوای مدل موقتاً روی دیسک باز می‌شود و پس از ساخت نشست حذف می‌گردد. کلید توسعه از `HSH_DETECTION_LICENSE` خوانده می‌شود و فقط برای توسعه fallback دارد؛ آن را راهکار امنیتی کامل تلقی نکنید.

## رفتار پردازش

ورودی ماژول، تصویر ROI اصلی است و مختصات `AnalysisDetection.Bounds` باید نسبت به همان ROI باقی بماند. Engine هنگام نمایش، offset مربوط به ROI را اضافه می‌کند. اگر یک pipeline تصویر مقیاس‌خورده تولید می‌کند، خود آن pipeline باید مختصات را به فضای ROI اصلی بازگرداند.

پارامترهای مهم مدل در `CameraSettings` عبارت‌اند از `ModelFile`، `InputSize`، `Confidence`، `NmsIoU`، `MaxFps` و `Threads`.

## اتصال به برنامه

`PlatePipeline` API عمومی برای مصرف‌کننده‌های بیرونی نیست؛ `PlateModule.CreateRegistration` factory آن را در `ProcessingRegistry` ثبت می‌کند و `CameraPipelineCoordinator` برای هر ROI فعال که `Plate` در `NamedRoi.Processing` آن فعال باشد، نمونهٔ مستقل می‌سازد. فعال یا غیرفعال بودن پلاک از تنظیم ROI و قابلیت `Plate` در لایسنس کنترل می‌شود.

OCR فقط پلاک ایرانی با الگوی `NNLNNNNN` را accepted می‌کند: دو رقم، یک حرف فارسی و پنج رقم. نتیجهٔ نامعتبر با `Accepted=false` برای overlay قرمز قابل مشاهده است، اما `DetectionRuntimeHost` آن را به history، trigger یا client ارسال نمی‌کند.

برای ساخت یا تغییر ماژول، قراردادهای [Abstractions](../HshDetectionEngin.Abstractions) را نشکنید و پیش از انتشار با مدل بسته‌بندی‌شده و یک جریان دوربین واقعی تست کنید.
