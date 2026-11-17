using System.Reflection;
using System.Text.Json;
using HshDetectionEngin;
using HshVisionLab;

internal static partial class Program
{
    private static void DesktopCaptureMigration()
    {
        var legacy = JsonSerializer.Deserialize<CameraSettings>(
            """{"Name":"Old camera","CaptureBackend":"retired-receiver","BufferCount":8,"Transport":"UDP"}""")!;
        legacy.TrackingCompletion.SampleTypes[0].Capacity = 17;
        legacy.TrackingCompletion.SampleTypes[1].Capacity = 23;
        string completionBefore = JsonSerializer.Serialize(legacy.TrackingCompletion);

        legacy.EnsureProcessingDefaults();
        CameraSettings persisted = legacy.Clone();
        string saved = JsonSerializer.Serialize(persisted);
        Check(!saved.Contains("CaptureBackend", StringComparison.Ordinal) &&
            !saved.Contains("BufferCount", StringComparison.Ordinal) && persisted.Transport == "UDP",
            "Retired capture fields survived serialization or changed the original transport.");
        Check(JsonSerializer.Serialize(legacy.TrackingCompletion) == completionBefore,
            "Capture migration changed recognition completion capacity or memory limits.");
    }

    private static void DesktopCaptureSettings()
    {
        var source = new CameraSettings { Name = "Old camera", Transport = "UDP" };
        source.TrackingCompletion.SampleTypes[0].Capacity = 17;
        string completionBefore = JsonSerializer.Serialize(source.TrackingCompletion);
        using (var cancelled = new CameraSettingsForm(source, false))
        {
            Check(cancelled.Result.Transport == "UDP",
                "Opening Windows camera settings changed the RTSP transport.");
        }
        Check(source.Transport == "UDP" && JsonSerializer.Serialize(source.TrackingCompletion) == completionBefore,
            "Opening or cancelling camera settings changed the live camera configuration.");

        using var form = new CameraSettingsForm(source, false);
        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        var transport = (ComboBox)typeof(CameraSettingsForm).GetField("_cmbTransport", flags)!.GetValue(form)!;
        Check(transport.Items.Cast<string>().SequenceEqual(new[] { "TCP", "UDP" }) && (string?)transport.SelectedItem == "UDP",
            "Windows settings removed or changed the RTSP transport selection.");
        foreach (string removed in new[] { "_cmbCaptureBackend", "_numPlateBuffer", "_numFaceBuffer", "_numPalmBuffer" })
            Check(typeof(CameraSettingsForm).GetField(removed, flags) is null,
                "Windows settings still create a capture receiver or frame queue control.");
        var completionModes = (ComboBox)typeof(CameraSettingsForm).GetField("_completionMode", flags)!.GetValue(form)!;
        Check(completionModes.Items.Count == 2, "Capture settings removed recognition completion modes.");

        transport.SelectedItem = "TCP";
        typeof(CameraSettingsForm).GetMethod("SaveAndClose", flags)!.Invoke(form, null);
        Check(form.DialogResult == DialogResult.OK && form.Result.Transport == "TCP",
            "Saving Windows camera settings did not preserve transport selection.");
        Check(JsonSerializer.Serialize(form.Result.TrackingCompletion) == completionBefore,
            "Saving Windows camera settings changed recognition completion buffers.");
    }
}
