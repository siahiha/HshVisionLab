# Identity database

`HshDetectionEngin.Identity` مالک SQLite مشترک هویت‌هاست. یک فایل
`identity-database.db` جدول اصلی `People` را نگه می‌دارد و هر modality اطلاعات
اختصاصی خودش را در جدول جدا ذخیره می‌کند:

- `People`: نام، شمارهٔ شخص، وضعیت ناشناس و زمان ایجاد/ویرایش.
- `PersonPlates`: پلاک‌های متعلق به شخص؛ هر پلاک normalized و یکتا است.
- `FaceSamples`: تصویر crop‌شده، embedding، confidence و metadata مدل چهره.
- `PalmSamples`: تصویر normalized، embedding، confidence و metadata مدل کف دست.

هر شخص می‌تواند چند پلاک، چند نمونهٔ چهره و چند نمونهٔ کف دست داشته باشد و
ارتباط همهٔ آن‌ها با `PersonId` انجام می‌شود. برای modality جدید باید جدول و
adapter مستقل اضافه شود؛ ستون‌های modality-specific نباید به `People` اضافه
شوند.

برنامهٔ Windows و `HshDetectionService` این فایل را باز می‌کنند. در اولین اجرا
اگر `identity-database.db` وجود نداشته باشد، داده‌های legacy از
`face-database.db` و `palm-database.db` با حفظ نمونه‌ها وارد می‌شوند. افراد
هم‌نام در migration به یک شخص مرکزی متصل می‌شوند و ادغام بعدی از فرم مدیریت
هویت انجام می‌شود.
