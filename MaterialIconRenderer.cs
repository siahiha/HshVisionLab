using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

namespace HshVisionLab;

/// <summary>Renders the official Material Icons font bundled as an application resource.</summary>
internal static class MaterialIconRenderer
{
    private static readonly Lazy<PrivateFontCollection> Fonts = new(LoadFonts);
    private static readonly Dictionary<string, char> Glyphs = new(StringComparer.Ordinal)
    {
        ["add"] = '\ue145',
        ["edit"] = '\ue3c9',
        ["delete"] = '\ue872',
        ["clear_all"] = '\ue0b8',
        ["check"] = '\ue5ca',
        ["play_arrow"] = '\ue037',
        ["stop"] = '\ue047',
        ["save"] = '\ue161',
        ["face"] = '\ue87c',
        ["language"] = '\ue894',
        ["view_module"] = '\ue8f0',
        ["settings"] = '\ue8b8',
        ["refresh"] = '\ue5d5',
        ["close"] = '\ue5cd'
    };

    public static Image Create(string iconName, Color color, int size = 22)
    {
        if (!Glyphs.TryGetValue(iconName, out char glyph))
            glyph = Glyphs["settings"];

        FontFamily family = Fonts.Value.Families[0];
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        using (var font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(color))
        using (var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoClip
        })
        {
            graphics.Clear(Color.Transparent);
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            graphics.DrawString(glyph.ToString(), font, brush, new RectangleF(0, 0, size, size), format);
        }

        return new Bitmap(bitmap);
    }

    public static Image? CreateForAction(string text, int size = 18)
    {
        string? icon = text switch
        {
            "Save" or "ذخیره" => "save",
            "Cancel" or "انصراف" => "close",
            "Close" or "بستن" => "close",
            "Delete person" or "Delete sample" or "حذف شخص" or "حذف نمونه" => "delete",
            "Rename person" or "تغییر نام شخص" => "edit",
            "Add image" or "افزودن تصویر" => "add",
            "Import folder" or "واردکردن پوشه" => "view_module",
            "Check similarity" or "بررسی شباهت" => "refresh",
            "Export to folder" or "خروجی در پوشه" => "save",
            "Merge into first person" or "ادغام در شخص اول" => "refresh",
            _ => null
        };
        return icon is null ? null : Create(icon, Color.White, size);
    }

    private static PrivateFontCollection LoadFonts()
    {
        Assembly assembly = typeof(MaterialIconRenderer).Assembly;
        using Stream stream = assembly.GetManifestResourceStream("HshVisionLab.MaterialIcons.ttf")
            ?? throw new InvalidOperationException("Material Icons resource is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var fonts = new PrivateFontCollection();
        byte[] bytes = memory.ToArray();
        GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            fonts.AddMemoryFont(handle.AddrOfPinnedObject(), bytes.Length);
        }
        finally
        {
            handle.Free();
        }
        return fonts;
    }
}
