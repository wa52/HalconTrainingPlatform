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
    Console.WriteLine("PASS: detection active-learning import/rank/review/reload/COCO/validation");
}
finally
{
    Directory.Delete(root, true);
}
static void Require(bool value, string name)
{
    if (!value) throw new Exception("FAILED: " + name);
}
