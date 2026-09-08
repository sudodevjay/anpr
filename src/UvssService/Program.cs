using Microsoft.Extensions.Options;
using UvssService.Components;
using UvssService.Config;
using UvssService.Lanes;
using UvssService.Licensing;

if (args.Contains("--test-ctc"))
{
    var idx0 = Array.IndexOf(args, "--test-ctc");
    var cropsDir = args.ElementAtOrDefault(idx0 + 1) ?? "../../../model_conversion/test_crops";
    var ctc = new UvssService.Ocr.OnnxCtcOcr("weights/ctc_backbone_and_head.onnx", "weights/en_dict.txt");
    Console.WriteLine($"CTC available: {ctc.Available}");
    foreach (var file in Directory.GetFiles(cropsDir, "*.jpg"))
    {
        var expected = Path.GetFileNameWithoutExtension(file);
        using var img = OpenCvSharp.Cv2.ImRead(file);
        var (text, confidence) = ctc.Recognize(img);
        Console.WriteLine($"{Path.GetFileName(file),-20} expected={expected,-14} got={text,-14} conf={confidence:F3}");
    }
    return;
}

if (args.Contains("--test-grammar"))
{
    var testCases = new[]
    {
        "DL5CY3199", "OL5CY3199", "D111CT0820", "AP39BD0606", "HR26FT2618",
        "DL5CY3099", "HR3BAC6737", "HR38AC6737", "GJ01AB1234", "MH12AB1234A",
        "", "ABCDEFGH", "DL05AB99B9", "22BH0432C", "26BH2916B", "TS09EA1234",
        "DL7CY7997X", "KL65G4218", "KL65C1241",
    };
    Console.WriteLine("=== choose_corrected_plate ===");
    foreach (var t in testCases)
    {
        var (text, tag) = UvssService.Ocr.PlateGrammar.ChooseCorrectedPlate(t);
        Console.WriteLine($"{$"\"{t}\"",-20} -> {$"\"{text}\"",-15} tag={tag}");
    }

    Console.WriteLine();
    Console.WriteLine("=== consensus_plate_text ===");
    var groups = new List<string[]>
    {
        new[] { "DL5CY3199", "DL5CY3199", "DL5CY3199" },
        new[] { "DL5CY3199", "DL5CY31999", "DL5CY3199" },
        new[] { "HR26FT2618", "HR26FT2618", "BR26FT2618" },
    };
    foreach (var g in groups)
    {
        Console.WriteLine($"[{string.Join(", ", g)}] -> \"{UvssService.Ocr.PlateGrammar.ConsensusPlateText(g)}\"");
    }
    return;
}

if (args.Contains("--test-aggregation"))
{
    UvssService.Ocr.PlateFrameResult Make(OpenCvSharp.Rect? bbox, double detConf, string text, double conf, double quality, double sharpness) =>
        new() { PlateBBox = bbox, PlateDetectionConfidence = detConf, PlateText = text, PlateConfidence = conf, PlateCropQuality = quality, PlateCropSharpness = sharpness };

    var bbox = new OpenCvSharp.Rect(0, 0, 10, 10);
    var groups = new List<List<UvssService.Ocr.PlateFrameResult>>
    {
        Enumerable.Range(0, 7).Select(_ => Make(bbox, 0.9, "DL5CY3199", 0.95, 0.8, 150)).Concat(
        Enumerable.Range(0, 3).Select(_ => Make(bbox, 0.7, "DL5CY3099", 0.85, 0.7, 140))).ToList(),

        Enumerable.Range(0, 4).Select(_ => Make(bbox, 0.9, "HR26FT2618", 0.9, 0.8, 150)).Concat(
        Enumerable.Range(0, 3).Select(_ => Make(bbox, 0.6, "BR26FT2618", 0.6, 0.5, 100))).Concat(
        Enumerable.Range(0, 3).Select(_ => Make(bbox, 0.5, "HR26FI2618", 0.5, 0.4, 90))).ToList(),

        Enumerable.Range(0, 5).Select(_ => Make(bbox, 0.9, "DL5CY3199", 0.9, 0.8, 150)).Concat(
        Enumerable.Range(0, 5).Select(_ => Make(bbox, 0.8, "DL5CY31999", 0.8, 0.7, 140))).ToList(),

        Enumerable.Range(0, 5).Select(_ => Make(null, 0.0, "", 0.0, 0.0, 0)).ToList(),
    };

    for (var i = 0; i < groups.Count; i++)
    {
        var r = UvssService.Ocr.PlateAggregation.SelectAggregatedResult(groups[i], minCandidates: 7);
        Console.WriteLine(
            $"Group {i}: text=\"{r.PlateText}\" conf={r.PlateConfidence:F4} valid={r.PlateTextValid} "
            + $"votes={r.AggregationTextVotes} clusters={r.AggregationClusterCount} candidates={r.AggregationCandidateCount} "
            + $"score={r.AggregationScore:F4} min_met={r.AggregationMinCandidatesMet}");
    }
    return;
}

if (args.Contains("--eval-recognize-best"))
{
    var idx = Array.IndexOf(args, "--eval-recognize-best");
    var tsvPath = args.ElementAtOrDefault(idx + 1) ?? "../../../model_conversion/val_list.tsv";
    var cropsDir = args.ElementAtOrDefault(idx + 2) ?? "../../../model_conversion/val_crops";
    var outTsv = args.ElementAtOrDefault(idx + 3) ?? "../../../model_conversion/csharp_recognize_best_report.tsv";

    var ctcOcr = new UvssService.Ocr.OnnxCtcOcr("weights/ctc_backbone_and_head.onnx", "weights/en_dict.txt");
    var plateOcr = new UvssService.Ocr.OnnxPlateOcr(
        "weights/ctc_backbone_and_head.onnx", "weights/sar_decoder_step.onnx",
        "weights/sar_embedding_99x512.bin", "weights/en_dict.txt");
    var search = new UvssService.Ocr.PlateCandidateSearch(ctcOcr, plateOcr);
    Console.WriteLine($"CTC available: {ctcOcr.Available}, SAR available: {plateOcr.Available}");

    var total = 0;
    var correct = 0;
    using var writer = new StreamWriter(outTsv);
    writer.WriteLine("filename\tlabel\ttext\tconfidence\tvariant\tcorrect");

    foreach (var line in File.ReadLines(tsvPath))
    {
        var parts = line.Split('\t');
        if (parts.Length != 2) continue;
        var relPath = parts[0];
        var label = parts[1];
        var fname = Path.GetFileName(relPath);
        var fullPath = Path.Combine(cropsDir, fname);
        if (!File.Exists(fullPath)) continue;

        using var img = OpenCvSharp.Cv2.ImRead(fullPath);
        var (text, confidence, variant) = search.RecognizeBest(img);
        total++;
        var isCorrect = text == label;
        if (isCorrect) correct++;
        writer.WriteLine($"{fname}\t{label}\t{text}\t{confidence:F4}\t{variant}\t{isCorrect}");

        if (total % 25 == 0)
        {
            Console.WriteLine($"  [{total}] running accuracy so far: {100.0 * correct / total:F2}%");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"=== C# recognize_best (full pipeline) -- {correct}/{total} = {(total > 0 ? 100.0 * correct / total : 0):F2}% ===");
    Console.WriteLine($"Report: {outTsv}");
    return;
}

if (args.Contains("--eval-ocr"))
{
    var idx = Array.IndexOf(args, "--eval-ocr");
    var tsvPath = args.ElementAtOrDefault(idx + 1) ?? "../../../model_conversion/val_list.tsv";
    var cropsDir = args.ElementAtOrDefault(idx + 2) ?? "../../../model_conversion/val_crops";

    var ocr = new UvssService.Ocr.OnnxPlateOcr(
        "weights/sar_backbone_and_encoder.onnx",
        "weights/sar_decoder_step.onnx",
        "weights/sar_embedding_99x512.bin",
        "weights/en_dict.txt");
    Console.WriteLine($"OCR available: {ocr.Available}");

    var total = 0;
    var correct = 0;
    var bySource = new Dictionary<string, (int correct, int total)>
    {
        ["new"] = (0, 0),
        ["old"] = (0, 0),
        ["other"] = (0, 0),
    };
    var mismatches = new List<string>();

    foreach (var line in File.ReadLines(tsvPath))
    {
        var parts = line.Split('\t');
        if (parts.Length != 2) continue;
        var relPath = parts[0];
        var label = parts[1];
        var fname = Path.GetFileName(relPath);
        var source = fname.StartsWith("new_") ? "new" : fname.StartsWith("old_") ? "old" : "other";

        var fullPath = Path.Combine(cropsDir, fname);
        if (!File.Exists(fullPath))
        {
            continue;
        }
        using var img = OpenCvSharp.Cv2.ImRead(fullPath);
        var (text, confidence) = ocr.Recognize(img);

        total++;
        var (c, t) = bySource[source];
        bySource[source] = (c + (text == label ? 1 : 0), t + 1);
        if (text == label)
        {
            correct++;
        }
        else
        {
            mismatches.Add($"{fname}\texpected={label}\tgot={text}\tconf={confidence:F3}");
        }

        if (total % 50 == 0)
        {
            Console.WriteLine($"  [{total}]");
        }
    }

    Console.WriteLine();
    Console.WriteLine("=== C# ONNX SAR OCR -- held-out accuracy ===");
    foreach (var (source, (c, t)) in bySource)
    {
        if (t == 0) continue;
        Console.WriteLine($"{source,-6}: {c}/{t} exact match = {100.0 * c / t:F2}%");
    }
    Console.WriteLine($"{"overall",-6}: {correct}/{total} exact match = {(total > 0 ? 100.0 * correct / total : 0):F2}%");
    Console.WriteLine();
    Console.WriteLine($"First 20 mismatches:");
    foreach (var m in mismatches.Take(20))
    {
        Console.WriteLine("  " + m);
    }
    return;
}

if (args.Contains("--test-ocr"))
{
    var cropsDir = args.SkipWhile(a => a != "--test-ocr").Skip(1).FirstOrDefault() ?? "../../../model_conversion/test_crops";
    var ocr = new UvssService.Ocr.OnnxPlateOcr(
        "weights/sar_backbone_and_encoder.onnx",
        "weights/sar_decoder_step.onnx",
        "weights/sar_embedding_99x512.bin",
        "weights/en_dict.txt");
    Console.WriteLine($"OCR available: {ocr.Available}");
    foreach (var file in Directory.GetFiles(cropsDir, "*.jpg"))
    {
        var expected = Path.GetFileNameWithoutExtension(file);
        using var img = OpenCvSharp.Cv2.ImRead(file);
        var (text, confidence) = ocr.Recognize(img);
        var match = text == expected ? "MATCH" : "MISMATCH";
        Console.WriteLine($"{Path.GetFileName(file),-20} expected={expected,-14} got={text,-14} conf={confidence:F3} {match}");
    }
    return;
}

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("appsettings.Gates.json", optional: false, reloadOnChange: true);

builder.Services.Configure<GateDefaultsOptions>(builder.Configuration.GetSection("GateDefaults"));
builder.Services.Configure<CameraDefaultsOptions>(builder.Configuration.GetSection("CameraDefaults"));
builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection("Storage"));
builder.Services.Configure<BackendEventOptions>(builder.Configuration.GetSection("BackendEvent"));
builder.Services.Configure<PlateDetectorOptions>(builder.Configuration.GetSection("PlateDetector"));
builder.Services.Configure<PlateOcrOptions>(builder.Configuration.GetSection("PlateOcr"));

builder.Services.AddSingleton<GateStateStore>();
builder.Services.AddSingleton<UvssService.Detection.IPlateDetector>(sp =>
{
    var o = sp.GetRequiredService<IOptions<PlateDetectorOptions>>().Value;
    return new UvssService.Detection.OnnxPlateDetector(o.ModelPath, o.Confidence);
});
builder.Services.AddSingleton<UvssService.Ocr.IPlateOcr>(sp =>
{
    var o = sp.GetRequiredService<IOptions<PlateOcrOptions>>().Value;
    return new UvssService.Ocr.OnnxPlateOcr(o.BackboneModelPath, o.DecoderModelPath, o.EmbeddingPath, o.CharDictPath);
});
builder.Services.AddSingleton<UvssService.Ocr.ICtcOcr>(sp =>
{
    var o = sp.GetRequiredService<IOptions<PlateOcrOptions>>().Value;
    return new UvssService.Ocr.OnnxCtcOcr(o.BackboneModelPath, o.CharDictPath);
});
builder.Services.AddSingleton<UvssService.Ocr.PlateCandidateSearch>(sp =>
    new UvssService.Ocr.PlateCandidateSearch(
        sp.GetRequiredService<UvssService.Ocr.ICtcOcr>(),
        sp.GetRequiredService<UvssService.Ocr.IPlateOcr>()));
builder.Services.AddSingleton<UvssService.Sensors.BarrierControllerRegistry>();
builder.Services.AddSingleton<UvssService.Streaming.CameraStreamRegistry>();
builder.Services.AddSingleton<AnprFrameHub>();
builder.Services.AddSingleton<UvssService.Cameras.SimulatedVehicleSlotStore>();
builder.Services.AddSingleton<EventLogStore>();
builder.Services.AddSingleton(_ =>
    new VehicleRegistryStore(Path.Combine(builder.Environment.ContentRootPath, "data", "vehicle_registry.json")));
builder.Services.AddSingleton(_ =>
    new GateConfigStore(Path.Combine(builder.Environment.ContentRootPath, "data", "gates.json")));
builder.Services.AddSingleton(_ =>
    new CameraConfigStore(Path.Combine(builder.Environment.ContentRootPath, "data", "cameras.json")));
builder.Services.AddSingleton(_ =>
    new ControllerConfigStore(Path.Combine(builder.Environment.ContentRootPath, "data", "controllers.json")));
builder.Services.AddSingleton(_ =>
    new DisplayConfigStore(Path.Combine(builder.Environment.ContentRootPath, "data", "displays.json")));
builder.Services.AddSingleton(_ =>
    new DisplayBusSettingsStore(Path.Combine(builder.Environment.ContentRootPath, "data", "display_bus.json")));
builder.Services.AddSingleton<UvssService.Sensors.VehicleDisplayBus>();
builder.Services.AddSingleton<LicenseValidator>();
builder.Services.AddSingleton(sp =>
    new LicenseStore(
        Path.Combine(builder.Environment.ContentRootPath, "data", "license.lic"),
        builder.Environment.ContentRootPath,
        sp.GetRequiredService<LicenseValidator>()));
builder.Services.AddHostedService<UvssService.Cameras.CameraStreamingHostedService>();
builder.Services.AddHostedService<GateWorkerHostedService>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Pre-populate GateStateStore synchronously with every enabled gate so
// Home.razor never renders before a gate exists in the store -- avoids a
// startup race against GateWorkerHostedService.ExecuteAsync, which only
// gets to create these lazily once its own async loop runs.
{
    var gateConfigStore = app.Services.GetRequiredService<GateConfigStore>();
    var stateStore = app.Services.GetRequiredService<GateStateStore>();
    foreach (var gate in gateConfigStore.All.Where(g => g.Enabled))
    {
        stateStore.GetOrCreate(gate.Name, gate.DisplayName, gate.Kind);
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseAntiforgery();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// Real continuous video for the dashboard's camera tiles: a plain MJPEG
// (multipart/x-mixed-replace) stream, which every browser already renders
// natively inside a bare <img> tag as live video -- no WebRTC/signaling,
// no extra JS. One instance of this per camera the browser has open.
app.MapGet("/stream/{gate}/{camera}", async (string gate, string camera, HttpContext http, UvssService.Streaming.CameraStreamRegistry registry) =>
{
    var broadcaster = registry.GetOrCreate(gate, camera);
    http.Response.ContentType = "multipart/x-mixed-replace; boundary=frame";
    var ct = http.RequestAborted;
    var lastVersion = -1;
    try
    {
        while (!ct.IsCancellationRequested)
        {
            var next = await broadcaster.WaitForFrameAfterAsync(lastVersion, ct);
            if (next == null) break;
            var (jpeg, version) = next.Value;
            lastVersion = version;

            var header = System.Text.Encoding.ASCII.GetBytes(
                $"--frame\r\nContent-Type: image/jpeg\r\nContent-Length: {jpeg.Length}\r\n\r\n");
            await http.Response.Body.WriteAsync(header, ct);
            await http.Response.Body.WriteAsync(jpeg, ct);
            await http.Response.Body.WriteAsync(new byte[] { 13, 10 }, ct); // \r\n
            await http.Response.Body.FlushAsync(ct);
        }
    }
    catch (OperationCanceledException)
    {
        // Client navigated away / closed the tab -- expected, not an error.
    }
});

app.Run();
