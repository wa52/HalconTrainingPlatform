using System.Text.Json;
using HalconTraining.App.Services;

var root = Path.Combine(Path.GetTempPath(), "halcon-al-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var imageA = Path.Combine(root, "a.png");
    var imageB = Path.Combine(root, "b.png");
    var imageC = Path.Combine(root, "c.png");
    await File.WriteAllBytesAsync(imageA, [137, 80, 78, 71]); // No imaging dependency in store tests.
    await File.WriteAllBytesAsync(imageB, [137, 80, 78, 71]);
    await File.WriteAllBytesAsync(imageC, [137, 80, 78, 71]);
    var predictions = new DetectionPredictionFile
    {
        ModelId = "smoke-detector-v1",
        Classes = [new DetectionCategory(1, "scratch"), new DetectionCategory(2, "dent")],
        Images =
        [
            new DetectionPredictionImage
            {
                ImagePath = "a.png", Width = 100, Height = 80,
                Boxes = [new DetectionBox(1, .97, 10, 12, 36, 45)]
            },
            new DetectionPredictionImage
            {
                ImagePath = "b.png", Width = 100, Height = 80, Boxes = []
            },
            new DetectionPredictionImage
            {
                ImagePath = "c.png", Width = 100, Height = 80,
                Boxes = [new DetectionBox(1, .54, 20, 20, 70, 60, .52)]
            }
        ]
    };
    var file = Path.Combine(root, "predictions.json");
    await File.WriteAllTextAsync(file, JsonSerializer.Serialize(predictions, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    var store = new DetectionActiveLearningStore(root);
    var session = await store.ImportAsync(file);
    Require(session.Images.Count == 3, "Import image count");
    var queue = DetectionActiveLearningStore.Pending(session);
    Require(Path.GetFileName(queue[0].ImagePath) == "c.png", "Margin uncertainty ranking");
    Require(queue.Any(image => image.PredictedBoxes.Count == 0), "Zero-detection image must be reviewed");
    var diversity = new DetectionReviewSession
    {
        Images =
        [
            new DetectionReviewImage { ImagePath = "g1-first", GroupId = "g1", Priority = .99 },
            new DetectionReviewImage { ImagePath = "g1-second", GroupId = "g1", Priority = .98 },
            new DetectionReviewImage { ImagePath = "g2-first", GroupId = "g2", Priority = .50 }
        ]
    };
    var diverseQueue = DetectionActiveLearningStore.Pending(diversity);
    Require(diverseQueue[0].ImagePath == "g1-first" && diverseQueue[1].ImagePath == "g2-first",
        "Group-aware sampling avoids consecutive same-batch reviews");
    var approved = session.Images.First(image => Path.GetFileName(image.ImagePath) == "a.png");
    approved.ReviewedBoxes = [new DetectionBox(2, .97, 15, 10, 40, 50)];
    approved.HasDraft = true;
    approved.Status = "approved";
    var rework = session.Images.First(image => Path.GetFileName(image.ImagePath) == "c.png");
    rework.Status = "rework";
    await store.SaveAsync(session);
    var restored = await store.LoadLatestAsync();
    Require(restored is not null && restored.Images.Count(i => i.Status == "approved") == 1, "Session restoration");
    var output = await store.ExportApprovedCocoAsync(restored!);
    using var coco = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "annotations.json")));
    Require(coco.RootElement.GetProperty("images").GetArrayLength() == 1, "Only approved images exported");
    Require(coco.RootElement.GetProperty("annotations").GetArrayLength() == 1, "Only approved boxes exported");
    var box = coco.RootElement.GetProperty("annotations")[0].GetProperty("bbox");
    Require(box[0].GetDouble() == 15 && box[1].GetDouble() == 10 &&
        box[2].GetDouble() == 25 && box[3].GetDouble() == 40, "COCO xywh conversion");
    Require(coco.RootElement.GetProperty("annotations")[0].GetProperty("category_id").GetInt32() == 2, "Human-edited class");
    var invalid = new DetectionBox(1, .8, 10, 10, 200, 30);
    var rejectedInvalid = false;
    try { DetectionActiveLearningStore.ValidateReviewedBoxes(approved, [invalid], restored!.Classes); }
    catch (InvalidDataException) { rejectedInvalid = true; }
    Require(rejectedInvalid, "Reject out-of-image boxes");
    var seed = await store.CreateSeedAsync(
        [(imageA, 100, 80), (imageB, 100, 80), (imageC, 100, 80)],
        ["scratch", "dent"]);
    Require(seed.Images.Count == 3 && seed.Images.All(image => image.PredictedBoxes.Count == 0),
        "Manual seed annotation without an existing detector");
    Require(seed.Images.All(image => image.Status == "pending"), "Seed is never implicitly approved");
    var seedExportRejected = false;
    try { await store.ExportApprovedCocoAsync(seed); }
    catch (InvalidOperationException) { seedExportRejected = true; }
    Require(seedExportRejected, "Unapproved manual seed must not export");
    seed.Images[0].ReviewedBoxes = [new DetectionBox(1, 0, 5, 5, 40, 30)];
    seed.Images[0].HasDraft = true;
    seed.Images[0].Status = "approved";
    await store.SaveAsync(seed);
    var seedExport = await store.ExportApprovedCocoAsync(seed);
    using var seedCoco = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(seedExport, "annotations.json")));
    Require(seedCoco.RootElement.GetProperty("images").GetArrayLength() == 1,
        "Seed COCO export contains only hand-labelled approved images");
    var imageD = Path.Combine(root, "d.png");
    var imageE = Path.Combine(root, "e.png");
    await File.WriteAllBytesAsync(imageD, [137, 80, 78, 71]);
    await File.WriteAllBytesAsync(imageE, [137, 80, 78, 71]);
    var countAdded = await store.AppendSeedImagesAsync(seed,
        [(imageA, 100, 80), (imageD, 100, 80), (imageE, 100, 80)]);
    Require(countAdded == 2 && seed.Images.Count == 5, "Append deduplicates images");
    Require(seed.Images[0].Status == "approved" && seed.Images[0].ReviewedBoxes.Count == 1,
        "Adding more images must not erase verified labels");
    Require(!DetectionActiveLearningStore.ReadyForBootstrap(seed), "One approved image cannot train");
    for (var i = 1; i < 4; i++)
    {
        seed.Images[i].Status = "approved";
        seed.Images[i].HasDraft = true;
        seed.Images[i].ReviewedBoxes = i == 1 ? [new DetectionBox(2, 0, 10, 15, 40, 50)] : [];
    }
    Require(!DetectionActiveLearningStore.ReadyForBootstrap(seed), "Four approved images must not train");
    seed.Images[4].Status = "approved";
    seed.Images[4].HasDraft = true;
    Require(DetectionActiveLearningStore.ReadyForBootstrap(seed),
        "Exactly five approved images with a box per class may train");
    await store.SaveAsync(seed);
    var fiveImageCoco = await store.ExportApprovedCocoAsync(seed);
    using var five = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(fiveImageCoco, "annotations.json")));
    Require(five.RootElement.GetProperty("images").GetArrayLength() == 5,
        "COCO export uses all five approved images, including human-verified negatives");
    Console.WriteLine("PASS: detection seed/rank/review/reload/COCO/5-image eligibility");
}
finally
{
    Directory.Delete(root, true);
}
static void Require(bool value, string name)
{
    if (!value) throw new Exception("FAILED: " + name);
}
