using System.Text.Json;
using System.Text.Json.Nodes;
using System.Collections.Concurrent;
using System.Drawing;
using HshDetectionEngin;
using HshDetectionEngin.Face;
using HshDetectionEngin.Plate;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.SignalR;

namespace HshDetectionService;

public static class ServiceApi
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/", () => Results.Content(ServiceHealthPage.Render(), "text/html; charset=utf-8"));
        app.MapGet("/health/live", () => Results.Ok(new { status = "live", utc = DateTime.UtcNow }));
        app.MapGet("/health/ready", (DetectionRuntimeHost host) => host.IsReady && host.License.IsValid
            ? Results.Ok(new { status = "ready", license = host.License.Message })
            : Results.Json(new { status = "not_ready", error = host.ReadinessError ?? host.License.Message }, statusCode: StatusCodes.Status503ServiceUnavailable));

        app.MapGet("/api/v1/service/status", (DetectionRuntimeHost host, ServiceSettingsStore store) => Results.Ok(new
        {
            ready = host.IsReady && host.License.IsValid,
            revision = store.Service.Revision,
            serviceNodeId = store.Service.ServiceNodeId,
            license = new { host.License.IsValid, host.License.Message, features = host.License.Claims?.Features.ToString() },
            cameras = host.GetCameraStatuses(),
            eventSequence = host.Events.CurrentSequence(),
            droppedEventCount = host.DroppedEventCount
        }));

        app.MapGet("/api/v1/service/capabilities", (DetectionRuntimeHost host) => Results.Ok(host.ProcessingModules.Modules.Select(module => new
        {
            type = module.Type.Value,
            displayName = module.DisplayName,
            kind = module.Kind.ToString(),
            optionsType = module.OptionsType?.FullName,
            editorKey = module.EditorKey,
            available = string.IsNullOrWhiteSpace(module.AvailabilityMessage),
            availabilityMessage = module.AvailabilityMessage
        })));
        app.MapGet("/api/v1/service/models", () =>
        {
            // Keep the web catalog in sync with CameraSettingsForm. Models can live
            // beside the executable, in the legacy Modules folder, or in the
            // project module folders while running from a Debug output directory.
            var files = EnumerateModelFiles("Plate", "HshDetectionEngin.Plate")
                .Select(path => new { path, module = "Plate" })
                .Concat(EnumerateModelFiles("Face", "HshDetectionEngin.Face")
                    .Select(path => new { path, module = "Face" }));

            var models = files
                .Select(item =>
                {
                    string name = Path.ChangeExtension(Path.GetFileName(item.path), ".onnx");
                    string capability = item.module == "Plate"
                        ? "Plate"
                        : name.Contains("sface", StringComparison.OrdinalIgnoreCase)
                            ? "FaceRecognition"
                            : name.Contains("yunet", StringComparison.OrdinalIgnoreCase)
                                ? "FaceDetection"
                                : "Face";
                    int[] inputSizes;
                    if (item.module == "Plate")
                    {
                        // Do not construct an ONNX InferenceSession inside an
                        // HTTP request. Model catalog metadata is requested by
                        // the UI during startup and must remain responsive
                        // while cameras are running.
                        inputSizes = PlateModelInspector.GetCatalogSquareInputSizes(name).ToArray();
                    }
                    else if (capability == "FaceDetection")
                    {
                        inputSizes = [FaceModelInspector.GetCatalogSquareInputSize(name)];
                    }
                    else
                    {
                        inputSizes = [];
                    }
                    return new
                    {
                        name,
                        relativePath = Path.GetRelativePath(AppContext.BaseDirectory, item.path).Replace('\\', '/'),
                        module = item.module,
                        capability,
                        inputSizes,
                        packaged = true
                    };
                })
                .GroupBy(item => $"{item.module}:{item.name}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.module)
                .ThenBy(item => item.name)
                .ToArray();
            return Results.Ok(models);
        });
        app.MapPost("/api/v1/service/reload", (DetectionRuntimeHost host, ServiceSettingsStore store) =>
        {
            host.ApplyDetectionSettings(store.Detection, store.Service.Runtime.AutoStartCameras);
            return Results.Ok(new ServiceOperationResult(true, "Runtime reloaded.", store.Service.Revision));
        });

        app.MapGet("/api/v1/settings", (ServiceSettingsStore store) => Results.Ok(new
        {
            revision = store.Service.Revision,
            detection = store.Detection,
            service = store.Service
        }));

        app.MapPut("/api/v1/settings", (ConfigurationUpdateRequest request, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            try
            {
                long expectedRevision = request.Revision;
                if (request.Detection is not null) store.SaveDetection(request.Detection, expectedRevision);
                if (request.Service is not null) store.SaveService(request.Service, request.Detection is null ? expectedRevision : store.Service.Revision);
                host.ApplyDetectionSettings(store.Detection, store.Service.Runtime.AutoStartCameras);
                return Results.Ok(new ServiceOperationResult(true, "Settings applied.", store.Service.Revision));
            }
            catch (ConfigurationConflictException ex)
            {
                return Results.Conflict(new { code = "configuration_conflict", expectedRevision = ex.ExpectedRevision, actualRevision = ex.ActualRevision });
            }
        });

        app.MapPost("/api/v1/settings/validate", (AppSettings settings, DetectionRuntimeHost host) =>
        {
            var errors = new List<string>();
            foreach (CameraSettings camera in settings.Cameras ?? [])
            {
                camera.EnsureProcessingDefaults();
                if (string.IsNullOrWhiteSpace(camera.Id)) errors.Add("Camera id is required.");
                if (string.IsNullOrWhiteSpace(camera.SourceUrl)) errors.Add($"Camera '{camera.Name}' source is empty.");
                foreach (NamedRoi roi in camera.Rois)
                    if (roi.Points.Count is > 0 and < 3) errors.Add($"Camera '{camera.Name}' ROI '{roi.Name}' has fewer than three points.");
            }
            return Results.Ok(new { valid = errors.Count == 0, errors, modules = host.ProcessingModules.Descriptors });
        });

        app.MapGet("/api/v1/cameras", (DetectionRuntimeHost host) => Results.Ok(host.GetCameraStatuses()));
        app.MapPost("/api/v1/cameras", (CameraSettings settings, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            settings.Id = string.IsNullOrWhiteSpace(settings.Id) ? Guid.NewGuid().ToString("N") : settings.Id;
            AppSettings all = store.Detection;
            all.Cameras.RemoveAll(camera => camera.Id.Equals(settings.Id, StringComparison.OrdinalIgnoreCase));
            all.Cameras.Add(settings);
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.Created($"/api/v1/cameras/{settings.Id}", settings);
        });

        app.MapGet("/api/v1/cameras/{cameraId}", (string cameraId, ServiceSettingsStore store) =>
        {
            CameraSettings? camera = store.Detection.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            return camera is null ? Results.NotFound() : Results.Ok(camera);
        });

        app.MapPut("/api/v1/cameras/{cameraId}", (string cameraId, CameraSettings settings, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            if (!cameraId.Equals(settings.Id, StringComparison.OrdinalIgnoreCase)) settings.Id = cameraId;
            AppSettings all = store.Detection;
            if (!all.Cameras.Any(camera => camera.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase))) return Results.NotFound();
            all.Cameras.RemoveAll(camera => camera.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            all.Cameras.Add(settings);
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.Ok(settings);
        });

        app.MapDelete("/api/v1/cameras/{cameraId}", (string cameraId, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            AppSettings all = store.Detection;
            if (all.Cameras.RemoveAll(camera => camera.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase)) == 0) return Results.NotFound();
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.NoContent();
        });

        app.MapPost("/api/v1/cameras/{cameraId}/start", (string cameraId, DetectionRuntimeHost host) => host.StartCamera(cameraId) ? Results.Ok() : Results.NotFound());
        app.MapPost("/api/v1/cameras/{cameraId}/stop", (string cameraId, DetectionRuntimeHost host) => host.StopCamera(cameraId) ? Results.Ok() : Results.NotFound());
        app.MapPost("/api/v1/cameras/{cameraId}/restart", (string cameraId, DetectionRuntimeHost host) =>
        {
            if (!host.StopCamera(cameraId) || !host.StartCamera(cameraId)) return Results.NotFound();
            return Results.Ok();
        });

        app.MapGet("/api/v1/cameras/{cameraId}/rois", (string cameraId, ServiceSettingsStore store) =>
        {
            CameraSettings? camera = store.Detection.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            return camera is null ? Results.NotFound() : Results.Ok(camera.Rois);
        });

        app.MapGet("/api/v1/cameras/{cameraId}/tasks", (string cameraId, ServiceSettingsStore store) =>
        {
            CameraSettings? camera = store.Detection.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            return camera is null ? Results.NotFound() : Results.Ok(camera.Rois.SelectMany(roi => roi.Processing.Select(item => new { roi.Name, RoiId = roi.Id, Task = item })));
        });

        app.MapPost("/api/v1/cameras/{cameraId}/rois", (string cameraId, NamedRoi roi, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            CameraSettings? camera = store.Detection.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            if (camera is null) return Results.NotFound();
            roi.Id = string.IsNullOrWhiteSpace(roi.Id) ? Guid.NewGuid().ToString("N") : roi.Id;
            camera.Rois.Add(roi);
            AppSettings all = store.Detection;
            all.Cameras.RemoveAll(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            all.Cameras.Add(camera);
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.Created($"/api/v1/cameras/{cameraId}/rois/{roi.Id}", roi);
        });

        app.MapGet("/api/v1/cameras/{cameraId}/rois/{roiId}", (string cameraId, string roiId, ServiceSettingsStore store) =>
        {
            NamedRoi? roi = store.Detection.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase))?.Rois
                .FirstOrDefault(item => item.Id.Equals(roiId, StringComparison.OrdinalIgnoreCase));
            return roi is null ? Results.NotFound() : Results.Ok(roi);
        });

        app.MapPut("/api/v1/cameras/{cameraId}/rois/{roiId}", (string cameraId, string roiId, NamedRoi roi, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            AppSettings all = store.Detection;
            CameraSettings? camera = all.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            NamedRoi? existing = camera?.Rois.FirstOrDefault(item => item.Id.Equals(roiId, StringComparison.OrdinalIgnoreCase));
            if (camera is null || existing is null) return Results.NotFound();
            roi.Id = roiId;
            camera.Rois.Remove(existing);
            camera.Rois.Add(roi);
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.Ok(roi);
        });

        app.MapDelete("/api/v1/cameras/{cameraId}/rois/{roiId}", (string cameraId, string roiId, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            AppSettings all = store.Detection;
            CameraSettings? camera = all.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase));
            if (camera is null || camera.Rois.RemoveAll(item => item.Id.Equals(roiId, StringComparison.OrdinalIgnoreCase)) == 0) return Results.NotFound();
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.NoContent();
        });

        app.MapPost("/api/v1/cameras/{cameraId}/rois/{roiId}/tasks", (string cameraId, string roiId, CameraProcessingSettings task, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            AppSettings all = store.Detection;
            NamedRoi? roi = all.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase))?.Rois
                .FirstOrDefault(item => item.Id.Equals(roiId, StringComparison.OrdinalIgnoreCase));
            if (roi is null) return Results.NotFound();
            task.Id = string.IsNullOrWhiteSpace(task.Id) ? Guid.NewGuid().ToString("N") : task.Id;
            roi.Processing.Add(task);
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.Created($"/api/v1/cameras/{cameraId}/rois/{roiId}/tasks/{task.Id}", task);
        });

        app.MapPut("/api/v1/cameras/{cameraId}/rois/{roiId}/tasks/{taskId}", (string cameraId, string roiId, string taskId, CameraProcessingSettings task, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            AppSettings all = store.Detection;
            CameraProcessingSettings? existing = all.DetectionTask(cameraId, roiId, taskId);
            if (existing is null) return Results.NotFound();
            task.Id = taskId;
            existing.Type = task.Type; existing.Name = task.Name; existing.Enabled = task.Enabled;
            existing.MaxFps = task.MaxFps; existing.Threads = task.Threads; existing.Options = task.Options;
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.Ok(existing);
        });

        app.MapDelete("/api/v1/cameras/{cameraId}/rois/{roiId}/tasks/{taskId}", (string cameraId, string roiId, string taskId, ServiceSettingsStore store, DetectionRuntimeHost host) =>
        {
            AppSettings all = store.Detection;
            NamedRoi? roi = all.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase))?.Rois
                .FirstOrDefault(item => item.Id.Equals(roiId, StringComparison.OrdinalIgnoreCase));
            if (roi is null || roi.Processing.RemoveAll(item => item.Id.Equals(taskId, StringComparison.OrdinalIgnoreCase)) == 0) return Results.NotFound();
            store.SaveDetection(all, store.Service.Revision);
            host.ApplyDetectionSettings(all, store.Service.Runtime.AutoStartCameras);
            return Results.NoContent();
        });

        app.MapGet("/api/v1/events", (long? afterSequence, int? limit, string? cameraId, string? scenario, DateTime? fromUtc, DateTime? toUtc, string? clientMode, bool? faceRequired, bool? plateRequired, bool? includeFace, bool? includePlate, bool? includeUnknownFace, int? windowMs, int? clientCooldownSeconds, string? clientCameraIds, string? clientRoiIds, DetectionRuntimeHost host) =>
        {
            int requestedLimit = Math.Clamp(limit ?? 200, 1, 2000);
            ClientSubscription? subscription = null;
            if (!string.IsNullOrWhiteSpace(clientMode))
            {
                subscription = new ClientSubscription
                {
                    Mode = clientMode,
                    FaceRequired = faceRequired ?? false,
                    PlateRequired = plateRequired ?? false,
                    IncludeFace = includeFace ?? true,
                    IncludePlate = includePlate ?? true,
                    IncludeUnknownFace = includeUnknownFace ?? true,
                    WindowMs = windowMs ?? 1500,
                    CooldownSeconds = clientCooldownSeconds ?? 0,
                    CameraIds = SplitList(clientCameraIds),
                    RoiIds = SplitList(clientRoiIds)
                };
            }

            bool hasFilter = !string.IsNullOrWhiteSpace(cameraId) ||
                !string.IsNullOrWhiteSpace(scenario) ||
                fromUtc is not null ||
                toUtc is not null ||
                subscription is not null;
            if (!hasFilter)
                return Results.Ok(host.Events.ReadAfter(afterSequence ?? 0, requestedLimit).ToArray());

            DateTime from = fromUtc?.ToUniversalTime() ?? DateTime.MinValue;
            DateTime to = toUtc?.ToUniversalTime() ?? DateTime.MaxValue;
            var matched = new List<DetectionEventEnvelope>(requestedLimit);
            var clientHistory = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
            long cursor = afterSequence ?? 0;
            const int pageSize = 2000;
            while (matched.Count < requestedLimit)
            {
                IReadOnlyList<DetectionEventEnvelope> page = host.Events.ReadAfter(cursor, pageSize);
                if (page.Count == 0) break;
                cursor = page[^1].Sequence;
                foreach (DetectionEventEnvelope item in page)
                {
                    if (!string.IsNullOrWhiteSpace(cameraId) && !string.Equals(item.Source["cameraId"]?.GetValue<string>(), cameraId, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!string.IsNullOrWhiteSpace(scenario) && !string.Equals(item.Scenario, scenario, StringComparison.OrdinalIgnoreCase)) continue;
                    if (item.OccurredAtUtc < from || item.OccurredAtUtc > to) continue;
                    if (subscription is not null && (!DetectionHub.Matches(item, subscription) || !DetectionHub.PassesHistoryCooldown(clientHistory, item, subscription))) continue;
                    matched.Add(item);
                    if (matched.Count >= requestedLimit) break;
                }
                if (page.Count < pageSize) break;
            }
            return Results.Ok(matched.ToArray());
        });
        app.MapDelete("/api/v1/events", (string? fromUtc, string? toUtc, EventStore events, ArtifactStore artifacts) =>
        {
            if (!TryParseUtc(fromUtc, out DateTime? from) || !TryParseUtc(toUtc, out DateTime? to))
                return Results.BadRequest(new { error = "fromUtc and toUtc must be valid ISO-8601 timestamps." });
            if (from is not null && to is not null && from > to)
                return Results.BadRequest(new { error = "fromUtc must be earlier than or equal to toUtc." });

            IReadOnlyList<string> deleted = events.Delete(from, to);
            artifacts.DeleteEventArtifacts(deleted);
            return Results.Ok(new { deletedCount = deleted.Count });
        });
        app.MapGet("/api/v1/events/{eventId}", (string eventId, DetectionRuntimeHost host) =>
        {
            DetectionEventEnvelope? item = host.Events.Get(eventId);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        app.MapGet("/api/v1/events/{eventId}/artifacts", (string eventId, DetectionRuntimeHost host) =>
        {
            DetectionEventEnvelope? item = host.Events.Get(eventId);
            return item is null ? Results.NotFound() : Results.Ok(item.Artifacts);
        });

        app.MapGet("/api/v1/events/{eventId}/artifacts/{artifactId}", (string eventId, string artifactId, DetectionRuntimeHost host) =>
        {
            DetectionEventEnvelope? item = host.Events.Get(eventId);
            EventArtifactDescriptor? artifact = item?.Artifacts.FirstOrDefault(value => value.ArtifactId.Equals(artifactId, StringComparison.OrdinalIgnoreCase));
            if (artifact is null) return Results.NotFound();
            string path = host.Artifacts.Resolve(artifact);
            return File.Exists(path) ? Results.File(path, artifact.ContentType) : Results.NotFound();
        });

        app.MapGet("/api/v1/streams/{cameraId}/snapshot", (string cameraId, DetectionRuntimeHost host) =>
        {
            using Bitmap? frame = host.GetLatestFrame(cameraId);
            if (frame is null) return Results.NotFound();
            using var stream = new MemoryStream();
            frame.Save(stream, System.Drawing.Imaging.ImageFormat.Jpeg);
            return Results.File(stream.ToArray(), "image/jpeg");
        });

        app.MapGet("/api/v1/streams/{cameraId}/overlay", (string cameraId, DetectionRuntimeHost host) =>
        {
            if (!host.TryGetCamera(cameraId, out Camera? camera) || camera is null)
                return Results.NotFound();
            return Results.Ok(camera.GetLiveOverlaySnapshot());
        });

        app.MapPost("/api/v1/streams/{cameraId}/webrtc/offer", async (string cameraId, WebRtcOfferRequest request, DetectionRuntimeHost host, WebRtcGateway gateway, CancellationToken cancellationToken) =>
        {
            if (!host.TryGetCamera(cameraId, out _)) return Results.NotFound(new { error = "Camera was not found." });
            try
            {
                WebRtcOfferResult answer = await gateway.AcceptOfferAsync(cameraId, request.Sdp, cancellationToken);
                return Results.Ok(answer);
            }
            catch (ArgumentException ex) { return Results.BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Results.BadRequest(new { error = ex.Message }); }
        });

        app.MapDelete("/api/v1/streams/webrtc/{sessionId}", (string sessionId, WebRtcGateway gateway) =>
            gateway.Close(sessionId) ? Results.NoContent() : Results.NotFound());

        app.MapMethods("/api/v1/streams/{cameraId}/webrtc/whep/{viewerId}", ["OPTIONS", "POST", "PATCH", "DELETE"],
            (HttpContext context, string cameraId, string viewerId, ServiceSettingsStore store, MediaMtxWebRtcProxy proxy, CancellationToken cancellationToken)
                => proxy.HandleAsync(context, cameraId, viewerId, store, cancellationToken));

        app.MapGet("/api/v1/face/people", (DetectionRuntimeHost host) => Results.Ok(host.FaceDatabase.Identities));
        app.MapPost("/api/v1/face/people", (CreatePersonRequest request, DetectionRuntimeHost host) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Name is required." });
            try
            {
                FaceIdentity person = host.FaceDatabase.CreatePerson(request.Name);
                return Results.Created($"/api/v1/face/people/{person.Id}", person);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { error = ex.Message });
            }
        });
        app.MapGet("/api/v1/face/people/{personId}", (string personId, DetectionRuntimeHost host) =>
        {
            FaceIdentity? person = host.FaceDatabase.Identities.FirstOrDefault(item => item.Id == personId);
            return person is null ? Results.NotFound() : Results.Ok(person);
        });

        app.MapPatch("/api/v1/face/people/{personId}", (string personId, RenamePersonRequest request, DetectionRuntimeHost host) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name)) return Results.BadRequest(new { error = "Name is required." });
            FaceIdentity? person = host.FaceDatabase.Identities.FirstOrDefault(item => item.Id == personId);
            if (person is null) return Results.NotFound();
            string normalizedName = request.Name.Trim();
            if (host.FaceDatabase.Identities.Any(item => item.Id != personId &&
                item.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)))
                return Results.Conflict(new { error = $"A person named '{normalizedName}' already exists." });
            return host.FaceDatabase.Rename(personId, normalizedName)
                ? Results.NoContent()
                : Results.Conflict(new { error = "The person name could not be changed." });
        });
        app.MapDelete("/api/v1/face/people/{personId}", (string personId, DetectionRuntimeHost host) =>
            host.FaceDatabase.Remove(personId) ? Results.NoContent() : Results.NotFound());
        app.MapGet("/api/v1/face/people/{personId}/samples", (string personId, DetectionRuntimeHost host) =>
            Results.Ok(host.FaceDatabase.GetSamples().Where(sample => sample.PersonId == personId)));
        app.MapGet("/api/v1/face/samples/{sampleId}", (string sampleId, DetectionRuntimeHost host) =>
        {
            FaceSample? sample = host.FaceDatabase.GetSamples().FirstOrDefault(item => item.Id == sampleId);
            return sample is null ? Results.NotFound() : Results.Ok(sample);
        });
        app.MapGet("/api/v1/face/samples/{sampleId}/image", (string sampleId, DetectionRuntimeHost host) =>
        {
            FaceSample? sample = host.FaceDatabase.GetSamples().FirstOrDefault(item => item.Id == sampleId);
            if (sample is null) return Results.NotFound();
            byte[] image = host.FaceDatabase.GetFaceImage(sampleId);
            return image.Length == 0 ? Results.NotFound() : Results.File(image, "image/jpeg");
        });
        app.MapDelete("/api/v1/face/samples/{sampleId}", (string sampleId, DetectionRuntimeHost host) =>
            host.FaceDatabase.RemoveSample(sampleId) ? Results.NoContent() : Results.NotFound());
        app.MapPost("/api/v1/face/samples/{sampleId}/move", (string sampleId, MoveSampleRequest request, DetectionRuntimeHost host) =>
        {
            try { return host.FaceDatabase.MoveSample(sampleId, request.TargetPersonId) ? Results.Ok() : Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapPost("/api/v1/face/people/{targetPersonId}/merge/{sourcePersonId}", (string targetPersonId, string sourcePersonId, DetectionRuntimeHost host) =>
        {
            try { return host.FaceDatabase.MergePeople(targetPersonId, sourcePersonId) ? Results.Ok() : Results.NotFound(); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { error = ex.Message }); }
        });
        app.MapGet("/api/v1/face/similar", (float? minimumSimilarity, bool? onlyDifferentPeople, DetectionRuntimeHost host) =>
            Results.Ok(host.FaceDatabase.FindSimilar(minimumSimilarity ?? 0.40f, onlyDifferentPeople ?? false)));
        app.MapGet("/api/v1/face/database/health", (DetectionRuntimeHost host) => Results.Ok(new
        {
            databasePath = host.FaceDatabase.DatabasePath,
            people = host.FaceDatabase.Identities.Count,
            samples = host.FaceDatabase.GetSamples().Count,
            sizeBytes = File.Exists(host.FaceDatabase.DatabasePath) ? new FileInfo(host.FaceDatabase.DatabasePath).Length : 0
        }));
        app.MapPost("/api/v1/face/database/backup", (DetectionRuntimeHost host) =>
        {
            string path = Path.Combine(host.Paths.BackupDirectory, $"face-database-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
            host.FaceDatabase.Save(path);
            return Results.Ok(new { path, sizeBytes = new FileInfo(path).Length });
        });

        app.MapPost("/api/v1/face/people/{personId}/samples", async (HttpRequest request, string personId, DetectionRuntimeHost host, CancellationToken cancellationToken) =>
        {
            IFormCollection form = await request.ReadFormAsync(cancellationToken);
            IFormFile? file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0) return Results.BadRequest(new { error = "An image file is required." });
            await using var stream = new MemoryStream();
            await file.CopyToAsync(stream, cancellationToken);
            FaceSample sample = await host.EnrollFaceSampleAsync(personId, null, stream.ToArray(), file.FileName, cancellationToken);
            return Results.Ok(sample);
        });

        app.MapGet("/api/v1/triggers", (ServiceSettingsStore store) => Results.Ok(new { revision = store.Service.Revision, items = store.Service.Triggers }));
        app.MapGet("/api/v1/triggers/{triggerId}", (string triggerId, ServiceSettingsStore store) =>
        {
            TriggerDefinition? trigger = store.Service.Triggers.FirstOrDefault(item => item.Id.Equals(triggerId, StringComparison.OrdinalIgnoreCase));
            return trigger is null ? Results.NotFound() : Results.Ok(trigger);
        });
        app.MapPost("/api/v1/triggers", (TriggerDefinition trigger, ServiceSettingsStore store) =>
        {
            ServiceSettingsDocument settings = store.Service;
            settings.Triggers.Add(trigger);
            store.SaveService(settings, settings.Revision);
            return Results.Created($"/api/v1/triggers/{trigger.Id}", trigger);
        });
        app.MapPatch("/api/v1/triggers/{triggerId}", (string triggerId, TriggerDefinition update, ServiceSettingsStore store) =>
        {
            ServiceSettingsDocument settings = store.Service;
            TriggerDefinition? current = settings.Triggers.FirstOrDefault(item => item.Id.Equals(triggerId, StringComparison.OrdinalIgnoreCase));
            if (current is null) return Results.NotFound();
            update.Id = triggerId;
            settings.Triggers.Remove(current);
            settings.Triggers.Add(update);
            store.SaveService(settings, settings.Revision);
            return Results.Ok(update);
        });
        app.MapDelete("/api/v1/triggers/{triggerId}", (string triggerId, ServiceSettingsStore store) =>
        {
            ServiceSettingsDocument settings = store.Service;
            if (settings.Triggers.RemoveAll(item => item.Id.Equals(triggerId, StringComparison.OrdinalIgnoreCase)) == 0) return Results.NotFound();
            store.SaveService(settings, settings.Revision);
            return Results.NoContent();
        });

        app.MapHub<DetectionHub>("/hubs/detections");
    }

    private static IEnumerable<string> EnumerateModelFiles(string capability, string projectDirectory)
    {
        var directories = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Models", capability),
            Path.Combine(AppContext.BaseDirectory, "Models"),
            Path.Combine(AppContext.BaseDirectory, "Modules", capability, "Models")
        };

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && directory is not null; i++, directory = directory.Parent)
        {
            directories.Add(Path.Combine(directory.FullName, projectDirectory, "Models"));
        }

        return directories
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .SelectMany(path => Directory.EnumerateFiles(path, "*.hshmodel", SearchOption.TopDirectoryOnly))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> SplitList(string? value) => string.IsNullOrWhiteSpace(value)
        ? []
        : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private static bool TryParseUtc(string? value, out DateTime? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateTimeOffset.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTimeOffset parsed))
            return false;
        result = parsed.UtcDateTime;
        return true;
    }
}

public sealed record ConfigurationUpdateRequest(long Revision, AppSettings? Detection, ServiceSettingsDocument? Service);
public sealed record CreatePersonRequest(string Name);
public sealed record RenamePersonRequest(string Name);
public sealed record MoveSampleRequest(string TargetPersonId);
public sealed record WebRtcOfferRequest(string Type, string Sdp);

internal static class AppSettingsApiExtensions
{
    public static CameraProcessingSettings? DetectionTask(this AppSettings settings, string cameraId, string roiId, string taskId)
        => settings.Cameras.FirstOrDefault(item => item.Id.Equals(cameraId, StringComparison.OrdinalIgnoreCase))?.Rois
            .FirstOrDefault(item => item.Id.Equals(roiId, StringComparison.OrdinalIgnoreCase))?.Processing
            .FirstOrDefault(item => item.Id.Equals(taskId, StringComparison.OrdinalIgnoreCase));
}

public sealed class DetectionHub : Hub
{
    public const string LiveGroup = "detection-live";
    private static readonly SemaphoreSlim ReplayGate = new(1, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, ClientSubscription> Subscriptions = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>> LastDelivered = new(StringComparer.Ordinal);
    private readonly EventStore _events;

    public DetectionHub(EventStore events) => _events = events;

    public override Task OnConnectedAsync()
    {
        Subscriptions[Context.ConnectionId] = new ClientSubscription();
        LastDelivered[Context.ConnectionId] = new System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        return base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        Subscriptions.TryRemove(Context.ConnectionId, out _);
        LastDelivered.TryRemove(Context.ConnectionId, out _);
        return base.OnDisconnectedAsync(exception);
    }

    public async Task Subscribe(long lastSequence = 0, ClientSubscription? subscription = null)
    {
        Subscriptions[Context.ConnectionId] = (subscription ?? new ClientSubscription()).Normalize();
        await ReplayGate.WaitAsync(Context.ConnectionAborted);
        try
        {
            long? oldest = _events.OldestSequence();
            if (oldest is long first && lastSequence < first - 1)
            {
                await Clients.Caller.SendAsync("cursorExpired", new { requestedAfter = lastSequence, availableFrom = first, requiresResync = true }, Context.ConnectionAborted);
                await Groups.RemoveFromGroupAsync(Context.ConnectionId, LiveGroup);
                return;
            }

            long watermark = _events.CurrentSequence();
            await Clients.Caller.SendAsync("replayStarted", new { requestedAfter = lastSequence, watermark }, Context.ConnectionAborted);
            long cursor = lastSequence;
            while (cursor < watermark)
            {
                IReadOnlyList<DetectionEventEnvelope> batch = _events.ReadAfter(cursor, 500, watermark);
                if (batch.Count == 0) break;
                foreach (DetectionEventEnvelope item in batch)
                {
                    if (Matches(item, Subscriptions[Context.ConnectionId]) &&
                        PassesCooldown(Context.ConnectionId, item, Subscriptions[Context.ConnectionId]))
                        await Clients.Caller.SendAsync("detection", item, Context.ConnectionAborted);
                    cursor = item.Sequence;
                }
            }
            await Clients.Caller.SendAsync("replayCompleted", new { lastSequence = cursor, watermark }, Context.ConnectionAborted);
        }
        finally { ReplayGate.Release(); }
    }

    public static async Task PublishAsync(IHubContext<DetectionHub> hub, DetectionEventEnvelope item, CancellationToken cancellationToken)
    {
        await ReplayGate.WaitAsync(cancellationToken);
        try
        {
            foreach ((string connectionId, ClientSubscription subscription) in Subscriptions.ToArray())
            {
                if (!Matches(item, subscription) || !PassesCooldown(connectionId, item, subscription)) continue;
                await hub.Clients.Client(connectionId).SendAsync("detection", item, cancellationToken);
            }
        }
        finally { ReplayGate.Release(); }
    }

    internal static bool Matches(DetectionEventEnvelope item, ClientSubscription subscription)
    {
        subscription.Normalize();
        string? cameraId = item.Source["cameraId"]?.GetValue<string>();
        string? roiId = item.Source["roiId"]?.GetValue<string>();
        if (subscription.CameraIds.Count > 0 &&
            !subscription.CameraIds.Contains(cameraId ?? string.Empty, StringComparer.OrdinalIgnoreCase)) return false;
        if (subscription.RoiIds.Count > 0 &&
            !subscription.RoiIds.Contains(roiId ?? string.Empty, StringComparer.OrdinalIgnoreCase)) return false;
        int associationAgeMs = item.Source["associationAgeMs"]?.GetValue<int>() ?? 0;
        if (subscription.WindowMs > 0 && associationAgeMs > subscription.WindowMs) return false;

        bool hasPlate = item.Components.ContainsKey("plate");
        bool hasFace = item.Components.TryGetValue("face", out JsonObject? face);
        bool knownFace = hasFace && string.Equals(
            face?["recognitionStatus"]?.GetValue<string>(), "Matched", StringComparison.OrdinalIgnoreCase);
        if (!subscription.IncludePlate && hasPlate && !hasFace) return false;
        if (!subscription.IncludeFace && hasFace && !hasPlate) return false;
        if (subscription.FaceRequired && !hasFace) return false;
        if (subscription.PlateRequired && !hasPlate) return false;

        if (subscription.Mode.Equals("Plate", StringComparison.OrdinalIgnoreCase))
        {
            if (!hasPlate) return false;
            if (!subscription.IncludeUnknownFace && hasFace && !knownFace) return false;
            return true;
        }

        if (subscription.Mode.Equals("KnownFace", StringComparison.OrdinalIgnoreCase))
        {
            if (!knownFace) return false;
            return true;
        }

        if (!subscription.IncludeUnknownFace && hasFace && !knownFace && !hasPlate) return false;
        return hasPlate || hasFace;
    }

    private static bool PassesCooldown(string connectionId, DetectionEventEnvelope item, ClientSubscription subscription)
    {
        if (subscription.CooldownSeconds <= 0) return true;
        ConcurrentDictionary<string, DateTime> delivered = LastDelivered.GetOrAdd(
            connectionId,
            _ => new ConcurrentDictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase));
        bool allowed = PassesCooldown(delivered, item, subscription);
        DateTime now = DateTime.UtcNow;
        foreach (string oldKey in delivered.Where(pair => (now - pair.Value).TotalHours > 24).Select(pair => pair.Key).ToArray())
            delivered.TryRemove(oldKey, out _);
        return allowed;
    }

    internal static bool PassesHistoryCooldown(
        IDictionary<string, DateTime> delivered,
        DetectionEventEnvelope item,
        ClientSubscription subscription) => PassesCooldown(delivered, item, subscription);

    private static bool PassesCooldown(
        IDictionary<string, DateTime> delivered,
        DetectionEventEnvelope item,
        ClientSubscription subscription)
    {
        if (subscription.CooldownSeconds <= 0) return true;
        string key = BuildCooldownKey(item);
        DateTime now = DateTime.UtcNow;
        if (delivered.TryGetValue(key, out DateTime previous) &&
            (now - previous).TotalSeconds < subscription.CooldownSeconds) return false;
        delivered[key] = now;
        return true;
    }

    private static string BuildCooldownKey(DetectionEventEnvelope item)
    {
        string plate = item.Components.TryGetValue("plate", out JsonObject? plateComponent)
            ? plateComponent?["plateText"]?.GetValue<string>() ?? string.Empty
            : string.Empty;
        string face = item.Components.TryGetValue("face", out JsonObject? faceComponent)
            ? faceComponent?["recognition"]?["personId"]?.GetValue<string>() ?? faceComponent?["label"]?.GetValue<string>() ?? string.Empty
            : string.Empty;
        string camera = item.Source["cameraId"]?.GetValue<string>() ?? string.Empty;
        return $"{camera}:{plate}:{face}:{item.EventType}";
    }
}
