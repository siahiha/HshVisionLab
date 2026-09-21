# حفاظت از مدل و قابلیت‌ها

مرجع عملیات صدور در [LICENSING.md](../docs/LICENSING.md) و شرح کامل بازسازی در [REBUILD_GUIDE.md](../docs/REBUILD_GUIDE.md) است.

## آنچه پیاده‌سازی فعلی محافظت می‌کند

- مدل‌های runtime در `.hshmodel` قرار می‌گیرند و مدل خام از UI و publish مشتری جدا می‌شود.
- Engine `license.hshlic` کنار executable را با public key کامپایل‌شده بررسی می‌کند.
- لایسنس به MachineId، بازهٔ اعتبار و featureهای `Plate` و `Face` محدود است.
- APIهای اجرایی Plate internal هستند و فقط Engine از طریق friend assembly به آن‌ها دسترسی کامپایل دارد.
- ساخت `FacePipeline` به `LicenseValidationResult` دارای feature `Face` نیاز دارد.

## مرز واقعی امنیت

package مدل فعلی AES-encrypted است، نه یک مرز امنیتی مطلق. برای ساخت inference session، ONNX رمزگشایی‌شده به‌طور موقت روی دیسک نوشته و سپس حذف می‌شود. کاربری که کنترل کامل دستگاه مشتری، debugger یا امکان تغییر باینری را دارد می‌تواند این حفاظت را دور بزند.

`HSH_DETECTION_LICENSE` کلید رمزگشایی package است و با `license.hshlic` تفاوت دارد. هیچ private key صادرکننده، مسیر private key، database مشتریان صادرکننده یا مدل خامی نباید به دستگاه مشتری منتقل شود.

برای سطح بالاتر، package signing، obfuscation، تحویل کلید از license server و inference سمت سرور گزینه‌های قابل بررسی‌اند. تنها inference سمت سرور، مدل را به‌طور معنادار از دستگاه مشتری دور نگه می‌دارد.
