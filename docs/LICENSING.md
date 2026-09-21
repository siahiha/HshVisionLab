# عملیات لایسنس

این سند راهنمای عملی صدور است. برای ساخت دوبارهٔ کل سامانه، [REBUILD_GUIDE.md](REBUILD_GUIDE.md) مرجع اصلی است.

## مدل اعتبارسنجی

`license.hshlic` یک JSON شامل `Payload` و `Signature` است. Payload شامل اطلاعات زیر است:

- `LicenseId`، نام مشتری و `MachineId`
- بازهٔ `NotBeforeUtc` تا `ExpiresUtc`
- featureهای `Plate` و `Face`

امضا با RSA و SHA-256 ساخته می‌شود. `LicenseValidator` کلید عمومی کامپایل‌شده در [Licensing.cs](../HshDetectionEngin.Licensing/Licensing.cs) را استفاده می‌کند، سپس امضا، بازهٔ تاریخ و شناسهٔ دستگاه را بررسی می‌کند. نبود فایل، امضای نامعتبر، انقضا یا دستگاه متفاوت، لایسنس را نامعتبر می‌کند.

لایسنس اجرای قابلیت با کلید AES package مدل یکسان نیست. متغیر `HSH_DETECTION_LICENSE` فقط برای رمزگشایی `.hshmodel` کاربرد دارد و جایگزین `license.hshlic` نیست.

## گردش کار مشتری

1. مشتری `HshDetectionEngin.LicenseRequest.exe` را روی همان دستگاه مقصد اجرا می‌کند.
2. کد درخواست را copy یا آن را به‌صورت `*.hshrequest` ذخیره می‌کند.
3. مشتری فایل/کد را برای صادرکننده می‌فرستد.
4. صادرکننده در `HshDetectionEngin.LicenseIssuer.exe` مشتری را انتخاب می‌کند، request، تاریخ انقضا و قابلیت‌ها را وارد می‌کند و صدور را انجام می‌دهد.
5. صادرکننده فایل `.hshlic` خروجی را با `Save selected license as...` به مسیر دلخواه export می‌کند و فقط همان فایل را به مشتری می‌دهد.
6. مشتری فایل را کنار `HshVisionLab.exe` قرار می‌دهد.

درخواست فعال‌سازی فقط fingerprint دستگاه، نام کامپیوتر و زمان درخواست را حمل می‌کند؛ private key یا راز صدور در آن نیست.

## رفتار UI ابزارهای لایسنس

### LicenseRequest

- فرم با عنوان `HshDetection - Activation Request` کد درخواست را در یک کادر فقط‌خواندنی نشان می‌دهد و نام کامپیوتر و device signature را زیر آن نمایش می‌دهد.
- `Copy request code` کد را در Clipboard می‌گذارد و `Save request file` آن را با پسوند `.hshrequest` ذخیره می‌کند. دکمهٔ `Close` فرم را می‌بندد. فرم هیچ private key یا license key را دریافت یا نمایش نمی‌دهد.

### LicenseIssuer

- فرم اصلی مشتریان جدول شرکت، رابط، تلفن، موبایل و تعداد لایسنس‌ها را نشان می‌دهد. `New customer`، `Edit`، `Manage licenses`، `Delete` و کادر `Search customers...` عملیات اصلی آن هستند؛ دوبارکلیک ردیف نیز مدیریت لایسنس همان مشتری را باز می‌کند. مشتری دارای لایسنس قابل حذف نیست.
- پنجرهٔ ساخت/ویرایش مشتری فیلدهای نام شرکت اجباری، نام رابط، تلفن، موبایل، آدرس و توضیحات دارد. ذخیره بدون نام شرکت با پیام هشدار متوقف می‌شود.
- پنجرهٔ `Licenses - <company>` دو بخش دارد: فرم صدور و آرشیو. فرم صدور مسیر private key، درخواست مشتری، تاریخ انقضا و checkboxهای `Plate detection` و `Face detection and recognition` را دارد. `Browse...` کلید PEM را انتخاب و مسیر آخرین انتخاب را نگه می‌دارد؛ `Generate pair...` private/public PEM تولید می‌کند و مسیر public key را برای جایگزینی در `LicenseValidator.PublicKeyPem` گزارش می‌دهد.
- `Issue and archive license` ابتدا request معتبر، کلید موجود و حداقل یک feature را بررسی می‌کند، سپس فایل را در آرشیو می‌سازد و metadata را ذخیره می‌کند. جدول آرشیو زمان صدور، featureها، تاریخ انقضا، device و مسیر فایل را نشان می‌دهد؛ `Save selected license as...` فقط ردیف انتخاب‌شده را به مسیر دلخواه با پسوند `.hshlic` کپی می‌کند.

## ابزار صادرکننده و آرشیو

LicenseIssuer فقط روی workstation امن صادرکننده استفاده می‌شود. فرم اصلی مشتریان را در `customers.json` کنار executable نگه می‌دارد. هر مشتری این اطلاعات را دارد: نام شرکت، رابط، تلفن شرکت، موبایل رابط، آدرس و توضیحات.

هر صدور به‌طور خودکار در `LicenseArchive/<CustomerId>/` قرار می‌گیرد و metadata آن در رکورد مشتری ذخیره می‌شود: LicenseId، MachineId، مسیر فایل، تاریخ صدور، تاریخ انقضا و قابلیت‌ها. مشتری دارای رکورد لایسنس حذف نمی‌شود تا تاریخچهٔ صدور ناقص نماند.

مسیر آخرین private key انتخاب‌شده در `issuer-settings.json` ذخیره می‌شود. اگر انتخابی وجود نداشته باشد، برنامه در مسیرهای پیش‌فرض توسعه به دنبال `vendor-private.pem` می‌گردد. این convenience فقط برای سیستم امن صادرکننده است؛ private key هرگز نباید در پوشهٔ مشتری یا publish عمومی قرار گیرد.

## کلیدها و rotation

`Generate pair...` یک جفت PEM می‌سازد:

- private key: فقط نزد صادرکننده بماند؛ با آن لایسنس صادر می‌شود.
- public key: متن آن باید جایگزین مقدار `LicenseValidator.PublicKeyPem` در [Licensing.cs](../HshDetectionEngin.Licensing/Licensing.cs) شود.

بعد از تغییر public key، همهٔ DLLهای محصول را دوباره build و منتشر کنید. لایسنس‌هایی که با private key قبلی امضا شده‌اند دیگر معتبر نخواهند بود. اگر می‌خواهید لایسنس‌های قبلی معتبر بمانند، public key فعلی را تغییر ندهید و از private key متناظر فعلی استفاده کنید.

## مرز توزیع

بستهٔ اولیهٔ مشتری باید runtime، مدل‌های package شده و `license.hshlic` را داشته باشد. `face-database.db` فایل SQLite دادهٔ runtime است و در صورت استفاده از ثبت هویت، کنار executable ایجاد و نگهداری می‌شود؛ تصویر crop‌شده و embedding نیز داخل همین فایل هستند و این فایل بخشی از ابزار صادرکننده نیست. `face-database.json` قدیمی در اولین اجرای نسخهٔ جدید فقط برای مهاجرت خوانده می‌شود. این موارد نباید به مشتری داده شوند:

- `vendor-private.pem` یا هر private key دیگر
- `HshDetectionEngin.LicenseIssuer.exe`
- `HshDetectionEngin.Tools`، مدل‌های خام و ابزارهای Python
- `customers.json`، `issuer-settings.json` و آرشیو صادرکننده

حفاظت کلاینتی مطلق نیست: مدل رمزگشایی‌شده موقتاً برای ساخت session روی دیسک نوشته می‌شود و شخصی که کنترل کامل دستگاه دارد می‌تواند باینری را تحلیل یا تغییر دهد. برای سطح بالاتر باید obfuscation، دریافت کلید از سرویس لایسنس یا inference سمت سرور در نظر گرفته شود.
