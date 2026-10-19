using System.Text.Json;
using HalconTraining.App.Services;
using HalconTraining.Application.Workers;

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
    var modelDirectory = Path.Combine(root, "previous-model");
    Directory.CreateDirectory(modelDirectory);
    var priorModel = Path.Combine(modelDirectory,"model.hdl");
    await File.WriteAllTextAsync(priorModel,"metadata-only test fixture");
    var modelScope = new DetectionReviewSession { Classes = [new(5,"P1"),new(6,"P2")], LastTrainedModelPath=priorModel };
    var modelManifest = Path.Combine(modelDirectory,"manifest.json");
    await File.WriteAllTextAsync(modelManifest,JsonSerializer.Serialize(new {task="detection",width=256,height=256,classIds=new[]{5,6},classNames=new[]{"P1","P2"}}));
    Require(HalconDetectionModelSource.Resolve(modelScope,256,true).InitialModelFile==priorModel,"Compatible HALCON weights reused");
    Require(HalconDetectionModelSource.Resolve(modelScope,256,false).InitialModelFile is null,"User can force a fresh backbone");
    Require(HalconDetectionModelSource.Resolve(modelScope,512,true).InitialModelFile is null,"Resize never silently reuses a detector");
    modelScope.Classes=[new(5,"P2"),new(6,"P1")];
    Require(HalconDetectionModelSource.Resolve(modelScope,256,true).InitialModelFile is null,"Changed names reject same IDs");
    modelScope.Classes=[new(6,"P2"),new(5,"P1")];
    Require(HalconDetectionModelSource.Resolve(modelScope,256,true).InitialModelFile is null,"Changed class order rejects model");
    modelScope.Classes=[new(5,"P1"),new(6,"P2")];
    await File.WriteAllTextAsync(modelManifest,"broken json");
    Require(HalconDetectionModelSource.Resolve(modelScope,256,true).InitialModelFile is null,"Corrupt manifest falls back explicitly");
    Require(HalconDetectionModelSource.Resolve(modelScope,256,true,priorModel).InitialModelFile==priorModel,"Explicit checkpoint needs no completed manifest; native worker validates it");
    File.Delete(modelManifest);
    Require(HalconDetectionModelSource.Resolve(modelScope,256,true).InitialModelFile is null,"Incomplete checkpoint is never automatically selected");
    modelScope.LastTrainedModelPath=Path.ChangeExtension(priorModel,"pt");
    await File.WriteAllTextAsync(modelScope.LastTrainedModelPath,"YOLO test fixture");
    Require(HalconDetectionModelSource.Resolve(modelScope,256,true).InitialModelFile is null,"YOLO weights cannot become HALCON weights");
    var rejectedSource=false;
    try { HalconDetectionModelSource.Resolve(modelScope,256,true,modelScope.LastTrainedModelPath); }
    catch(InvalidDataException) {rejectedSource=true;}
    Require(rejectedSource,"Explicit non-HALCON source is rejected");
    Require(DetectionProgressMessage.Parse("{\"stage\":\"训练\",\"detail\":{\"epoch\":1,\"epochs\":3,\"imagesPerSecond\":6.4}}") is {ImagesPerSecond:6.4},"Measured throughput reaches progress DTO");
    Require(DetectionProgressMessage.Parse("{\"stage\":\"训练\",\"detail\":{\"imagesPerSecond\":\"unknown\"}}") is {ImagesPerSecond:null},"Unknown throughput is not fabricated");
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
    var folderRoot = Path.Combine(root, "文件夹数据");
    var folderA = Path.Combine(folderRoot, "零件A");
    var folderB = Path.Combine(folderRoot, "零件B");
    Directory.CreateDirectory(folderA);
    Directory.CreateDirectory(Path.Combine(folderB, "批次"));
    var folderImageA = Path.Combine(folderA, "same.JPG");
    var folderImageB = Path.Combine(folderB, "批次", "same.png");
    await File.WriteAllBytesAsync(folderImageA, [1]);
    await File.WriteAllBytesAsync(folderImageB, [2]);
    await File.WriteAllTextAsync(Path.Combine(folderA, "broken.jpg"), "broken");
    await File.WriteAllTextAsync(Path.Combine(folderRoot, "unclassified.jpg"), "ignored");
    await File.WriteAllTextAsync(Path.Combine(folderA, "notes.txt"), "ignored");
    (int Width, int Height) ReadSize(string path) => Path.GetFileName(path) == "broken.jpg"
        ? throw new InvalidDataException("bad image fixture") : (100, 80);
    var folderSession = await store.ImportFolderAsync(folderRoot, ReadSize);
    Require(folderSession.DatasetName == "文件夹数据" && folderSession.SourceDirectory == folderRoot,
        "Dataset name and source directory come from selected root");
    Require(folderSession.Classes.Select(category => category.Name).SequenceEqual(new[] { "零件A", "零件B" }),
        "Direct child folders define deterministic classes");
    Require(folderSession.Images.Count == 2 && folderSession.Images.All(image => image.Status == "pending" &&
        image.FolderClassId is not null && image.ReviewedBoxes.Count == 0), "Folder classes do not invent boxes or approval");
    Require(folderSession.ImportIssues.Count == 2, "Bad images isolated and unclassified root images reported");
    Require(DetectionActiveLearningStore.PredictionCandidates(folderSession).Count == 2, "All untouched images may be predicted");
    var reviewedFolderImage = folderSession.Images.Single(image => image.ImagePath == folderImageA);
    reviewedFolderImage.Status = "approved";
    reviewedFolderImage.HasDraft = true;
    reviewedFolderImage.ReviewedBoxes = [new DetectionBox(reviewedFolderImage.FolderClassId!.Value, 0, 5, 5, 50, 60)];
    var draftFolderImage = folderSession.Images.Single(image => image.ImagePath == folderImageB);
    draftFolderImage.HasDraft = true;
    draftFolderImage.ReviewedBoxes = [new DetectionBox(draftFolderImage.FolderClassId!.Value, 0, 6, 6, 40, 50)];
    Require(DetectionActiveLearningStore.PredictionCandidates(folderSession).Count == 0,
        "Approved labels and manually saved drafts are excluded from remaining prediction");
    await store.SaveAsync(folderSession);
    var another = Path.Combine(folderA, "new.jpg");
    await File.WriteAllBytesAsync(another, [3]);
    var rescanned = await store.ImportFolderAsync(folderRoot, ReadSize, folderSession);
    Require(rescanned.Id == folderSession.Id && rescanned.Images.Count == 3 &&
        rescanned.Classes.SequenceEqual(folderSession.Classes), "Reimport appends and preserves stable class IDs");
    Require(rescanned.Images.Single(image => image.ImagePath == folderImageA).Status == "approved" &&
        rescanned.Images.Single(image => image.ImagePath == folderImageB).HasDraft, "Folder reimport preserves labels and drafts");
    var proposals = new DetectionReviewSession
    {
        ModelId = "new-detector", SourcePredictionFile = "proposals.json", Classes = rescanned.Classes,
        Images = [new DetectionReviewImage { ImagePath = another, Width = 100, Height = 80,
            PredictedBoxes = [new DetectionBox(rescanned.Classes[0].Id, .8, 10, 10, 50, 50)], Priority = .2 }]
    };
    var mergedFolder = DetectionActiveLearningStore.MergePredictions(rescanned, proposals);
    Require(mergedFolder.Id == folderSession.Id && mergedFolder.DatasetName == "文件夹数据" && mergedFolder.Images.Count == 3,
        "Partial prediction merges into original dataset without dropping other images");
    Require(mergedFolder.Images.Single(image => image.ImagePath == another).PredictedBoxes.Count == 1 &&
        mergedFolder.Images.Single(image => image.ImagePath == another).Status == "pending" &&
        mergedFolder.Images.Single(image => image.ImagePath == another).FolderClassId == rescanned.Classes[0].Id,
        "Prediction stays pending and retains folder association");
    Require(mergedFolder.Images.Single(image => image.ImagePath == folderImageA).ReviewedBoxes[0].Col1 == 5 &&
        mergedFolder.Images.Single(image => image.ImagePath == folderImageB).ReviewedBoxes[0].Col1 == 6,
        "Merging never overwrites human labels or saved drafts");
    var mismatchedRejected = false;
    proposals.Classes = [new DetectionCategory(99, "wrong")];
    try { DetectionActiveLearningStore.MergePredictions(rescanned, proposals); }
    catch (InvalidDataException) { mismatchedRejected = true; }
    Require(mismatchedRejected && rescanned.Images.Count == 3, "Mismatched model rejects without changing dataset");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    var importCancelled = false;
    try { await store.ImportFolderAsync(folderRoot, ReadSize, rescanned, cancellationToken: cancelled.Token); }
    catch (OperationCanceledException) { importCancelled = true; }
    Require(importCancelled && rescanned.Images.Count == 3, "Cancelled import leaves current session untouched");
    Require((await File.ReadAllBytesAsync(folderImageA)).SequenceEqual(new byte[] { 1 }), "Import never modifies source image");
    var nonfolderRejected = false;
    try { await store.ImportFolderAsync(folderA, ReadSize); }
    catch (InvalidDataException) { nonfolderRejected = true; }
    Require(nonfolderRejected, "A class folder is not silently treated as a root dataset");
    var classA = rescanned.Classes[0].Id;
    var classB = rescanned.Classes[1].Id;
    rescanned.FolderModels[classA] = "models/detection/零件A/model.pt";
    rescanned.FolderModels[classB] = "models/detection/零件B/model.pt";
    var scopeA = DetectionActiveLearningStore.FolderScope(rescanned, classA);
    var scopeB = DetectionActiveLearningStore.FolderScope(rescanned, classB);
    Require(scopeA.Classes.Count == 1 && scopeA.Classes[0].Id == classA &&
        scopeA.Images.All(image => image.FolderClassId == classA) && scopeA.DatasetName == "零件A",
        "Training scope contains only one folder and one class");
    Require(scopeA.LastTrainedModelPath != scopeB.LastTrainedModelPath && scopeB.DatasetName == "零件B",
        "Each folder resolves its own model, without a shared detector");
    var scopedExport = await store.ExportApprovedCocoAsync(scopeA);
    using var scopedCoco = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(scopedExport, "annotations.json")));
    Require(scopedCoco.RootElement.GetProperty("categories").GetArrayLength() == 1 &&
        scopedCoco.RootElement.GetProperty("images").GetArrayLength() == 1 &&
        scopedCoco.RootElement.GetProperty("categories")[0].GetProperty("id").GetInt32() == classA,
        "Training snapshot does not contain other folder classes or images");
    var fiveAcrossFolders = new DetectionReviewSession
    {
        SourceDirectory = folderRoot, Classes = rescanned.Classes,
        Images = Enumerable.Range(0, 5).Select(i => new DetectionReviewImage
        {
            ImagePath = "fixture-" + i, FolderClassId = i < 3 ? classA : classB, Status = "approved", HasDraft = true,
            ReviewedBoxes = [new DetectionBox(i < 3 ? classA : classB, 0, 1, 1, 20, 20)]
        }).ToList()
    };
    Require(!DetectionActiveLearningStore.ReadyForBootstrap(DetectionActiveLearningStore.FolderScope(fiveAcrossFolders, classA)) &&
        !DetectionActiveLearningStore.ReadyForBootstrap(DetectionActiveLearningStore.FolderScope(fiveAcrossFolders, classB)),
        "Five images spread across folders cannot satisfy either folder's independent threshold");
    proposals.Classes = [rescanned.Classes[0]];
    var scopedMerge = DetectionActiveLearningStore.MergeFolderPredictions(rescanned, classA, proposals);
    Require(scopedMerge.Images.Count == rescanned.Images.Count &&
        scopedMerge.Images.Single(image => image.ImagePath == another).PredictedBoxes.Count == 1 &&
        scopedMerge.Images.Single(image => image.ImagePath == folderImageB).ReviewedBoxes[0].Col1 == 6 &&
        scopedMerge.FolderModels[classB] == rescanned.FolderModels[classB], "Folder prediction leaves other folder images and models untouched");
    proposals.Images[0].ImagePath = folderImageB;
    var crossFolderRejected = false;
    try { DetectionActiveLearningStore.MergeFolderPredictions(rescanned, classA, proposals); }
    catch (InvalidDataException) { crossFolderRejected = true; }
    Require(crossFolderRejected, "Cross-folder prediction is rejected before merging");
    var multipleCandidates = new DetectionReviewImage
    {
        PredictedBoxes = [new DetectionBox(classA, .06, 10, 10, 50, 50),
            new DetectionBox(classA, .27, 11, 11, 51, 51), new DetectionBox(classA, .07, 12, 12, 52, 52)]
    };
    var singleTarget = DetectionActiveLearningStore.ReviewBoxes(multipleCandidates, 1);
    Require(singleTarget.Count == 1 && singleTarget[0].Confidence == .27,
        "Single-object review shows the strongest prediction, not every duplicate candidate");
    Require(multipleCandidates.PredictedBoxes.Count == 3, "Review filtering preserves raw prediction evidence");
    Require(DetectionActiveLearningStore.ReviewBoxes(multipleCandidates, 2).Count == 2,
        "Prediction limit remains configurable for genuine multiple targets");
    multipleCandidates.HasDraft = true;
    multipleCandidates.ReviewedBoxes = multipleCandidates.PredictedBoxes.ToList();
    Require(DetectionActiveLearningStore.ReviewBoxes(multipleCandidates, 1).Count == 3,
        "Single-object prediction limit never discards saved manual drafts");
    multipleCandidates.HasDraft = false;
    multipleCandidates.Status = "approved";
    Require(DetectionActiveLearningStore.ReviewBoxes(multipleCandidates, 1).Count == 3,
        "Approved human annotations remain intact regardless of prediction limit");
    multipleCandidates.Status = "pending";
    multipleCandidates.PredictedBoxes.Clear();
    Require(DetectionActiveLearningStore.ReviewBoxes(multipleCandidates, 1).Count == 0,
        "Single-object review does not invent a box when detection finds nothing");
    var bulkSession = new DetectionReviewSession
    {
        Id = "bulk-confirm-test", SourceDirectory = folderRoot, Classes = rescanned.Classes,
        Images =
        [
            new DetectionReviewImage { ImagePath = folderImageA, FolderClassId = classA, Width = 100, Height = 80,
                PredictedBoxes = [new DetectionBox(classA, .06, 10, 10, 30, 30), new DetectionBox(classA, .27, 11, 11, 31, 31)] },
            new DetectionReviewImage { ImagePath = another, FolderClassId = classA, Width = 100, Height = 80, HasDraft = true,
                ReviewedBoxes = [new DetectionBox(classA, 0, 1, 2, 20, 30), new DetectionBox(classA, 0, 30, 2, 50, 30)] },
            new DetectionReviewImage { ImagePath = folderImageB, FolderClassId = classB, Width = 100, Height = 80,
                PredictedBoxes = [new DetectionBox(classB, .9, 1, 1, 20, 20)] },
            new DetectionReviewImage { ImagePath = imageA, FolderClassId = classA, Width = 100, Height = 80 },
            new DetectionReviewImage { ImagePath = imageB, FolderClassId = classA, Width = 100, Height = 80,
                PredictedBoxes = [new DetectionBox(classA, .9, 0, 0, 150, 150)] },
            new DetectionReviewImage { ImagePath = imageC, FolderClassId = classA, Width = 100, Height = 80, Status = "rework",
                PredictedBoxes = [new DetectionBox(classA, .9, 1, 1, 20, 20)] },
            new DetectionReviewImage { ImagePath = "missing-image.png", FolderClassId = classA, Width = 100, Height = 80,
                PredictedBoxes = [new DetectionBox(classA, .9, 1, 1, 20, 20)] }
        ]
    };
    var bulkBefore = JsonSerializer.Serialize(bulkSession);
    Require(DetectionActiveLearningStore.BulkApprovalCandidates(bulkSession, classA, 1).Count == 2,
        "Bulk confirmation excludes other folders, no-box images, invalid boxes, rework and missing files");
    var bulkResult = await store.ApproveAllAsync(bulkSession, classA, 1);
    Require(bulkResult.Count == 2 && JsonSerializer.Serialize(bulkSession) == bulkBefore,
        "Bulk confirmation uses an isolated snapshot and leaves original state intact until saved");
    Require(bulkResult.Session.Images[0].ReviewedBoxes is [{ Confidence: .27 }] &&
        bulkResult.Session.Images[0].PredictedBoxes.Count == 2 && bulkResult.Session.Images[1].ReviewedBoxes.Count == 2,
        "Bulk confirmation agrees with the displayed top candidate and preserves manual draft boxes and raw predictions");
    Require(bulkResult.Session.Images.Skip(2).All(image => image.Status != "approved"),
        "All skipped images retain their previous status");
    Require(File.Exists(bulkResult.BackupFile) &&
        JsonSerializer.Serialize(JsonSerializer.Deserialize<DetectionReviewSession>(await File.ReadAllTextAsync(bulkResult.BackupFile),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))) == bulkBefore,
        "Bulk confirmation saves a restorable pre-operation backup");
    var repeatBulk = await store.ApproveAllAsync(bulkResult.Session, classA, 1);
    Require(repeatBulk.Count == 0 && repeatBulk.BackupFile == "", "Repeated bulk confirmation is idempotent");
    var mixedBulkRejected = false;
    try { DetectionActiveLearningStore.BulkApprovalCandidates(bulkSession, null, 1); }
    catch (InvalidOperationException) { mixedBulkRejected = true; }
    Require(mixedBulkRejected, "Folder mode cannot bulk-confirm across all categories");
    var emptyPredictionSummary = DetectionPredictionSummary.From([
        new DetectionReviewImage(), new DetectionReviewImage()], 0);
    Require(emptyPredictionSummary is { Processed: 2, DetectedImages: 0, EmptyImages: 2, TotalBoxes: 0, Failed: 0 } &&
        emptyPredictionSummary.Description.Contains("有框 0 张") && emptyPredictionSummary.Description.Contains("未检出 2 张"),
        "Successful processing of empty detections is not reported as having prediction boxes");
    var mixedPredictionSummary = DetectionPredictionSummary.From([
        new DetectionReviewImage { PredictedBoxes = [new DetectionBox(classA, .5, 1, 1, 20, 20)] },
        new DetectionReviewImage()], 1);
    Require(mixedPredictionSummary is { Processed: 3, DetectedImages: 1, EmptyImages: 1, TotalBoxes: 1, Failed: 1 },
        "Prediction summary distinguishes detections, empty results and failed inputs");
    var gesture = new DetectionBoxGesture();
    var originalBox = new DetectionBox(7, .85, 20, 20, 80, 70);
    gesture.Begin(45, 45, 200, 150, [originalBox], 4);
    gesture.Update(65, 55);
    Require(gesture.Kind == DetectionGestureKind.Move && gesture.Index == 0 &&
        gesture.Preview is { Col1: 40, Row1: 30, Col2: 100, Row2: 80, ClassId: 7, Confidence: .85 },
        "Dragging box interior moves the same box, preserving size, class and model confidence");
    Require(originalBox.Col1 == 20, "Live move preview does not mutate annotations before mouse release");
    gesture.Update(500, 500);
    Require(gesture.Preview is { Col1: 140, Col2: 200, Row1: 100, Row2: 150 }, "Move clamps box at image boundaries without shrinking");
    gesture.Begin(80, 70, 200, 150, [originalBox], 4);
    gesture.Update(110, 90);
    Require(gesture.Kind == DetectionGestureKind.Resize && gesture.Index == 0 && gesture.Preview is { Col1: 20, Row1: 20, Col2: 110, Row2: 90 },
        "Dragging corner resizes the existing box with a live preview");
    gesture.Begin(20, 45, 200, 150, [originalBox], 4);
    gesture.Update(5, 60);
    Require(gesture.Preview is { Col1: 5, Row1: 20, Col2: 80, Row2: 70 }, "Edge drag changes only that edge");
    gesture.Begin(20, 20, 200, 150, [originalBox], 4);
    gesture.Update(90, 100);
    Require(gesture.Preview is { Col1: 80, Row1: 70, Col2: 90, Row2: 100 }, "Resize supports dragging across the opposite corner");
    gesture.Begin(150, 120, 200, 150, [originalBox], 4);
    gesture.Update(100, 80);
    Require(gesture.Kind == DetectionGestureKind.Draw && gesture.Preview is { Col1: 100, Row1: 80, Col2: 150, Row2: 120 }, "New boxes can be drawn in reverse direction");
    gesture.Begin(45, 45, 200, 150, [originalBox], 4, forceDraw: true);
    Require(gesture.Kind == DetectionGestureKind.Draw, "Shift explicitly permits overlapping new boxes");
    gesture.Cancel();
    Require(!gesture.IsActive && gesture.Preview is null, "Escape/capture loss cancels preview without changing boxes");
    var priorEncoding = Environment.GetEnvironmentVariable("PYTHONIOENCODING");
    try
    {
        Environment.SetEnvironmentVariable("PYTHONIOENCODING", "gbk");
        var messages = new List<string>();
        var protocol = await new YoloProcessRunner().RunAsync([], new InlineProgress(messages.Add), CancellationToken.None);
        Require(protocol.ExitCode == 0, "Python subprocess starts without GPU dependencies");
        var stages = messages.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("stage").GetString()).ToArray();
        Require(stages.SequenceEqual(new[] { "训练", "预测" }) && protocol.Error.Contains("中文错误详情"),
            "Chinese progress and errors survive Python-to-.NET transport even with a legacy system encoding");
        Require(DetectionProgressMessage.Parse(messages[0]) is { Stage: "训练", Current: 1, Total: 2 } &&
            DetectionProgressMessage.Parse(messages[1]) is { Stage: "预测", Current: 1, Total: 2 }, "Actual Python messages become visible training and prediction progress");
        Require(DetectionProgressMessage.Parse("{\"stage\":\"轮次完成\",\"detail\":{\"epoch\":2,\"meanLoss\":0.25}}") is { Current: 2, Loss: .25 }, "HALCON nested epoch/loss messages are supported");
        Require(DetectionProgressMessage.Parse("ordinary training log") is null, "Plain logs do not interrupt progress parsing");
    }
    finally { Environment.SetEnvironmentVariable("PYTHONIOENCODING", priorEncoding); }
    var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseJobs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var started = new System.Collections.Concurrent.ConcurrentQueue<int>();
    var runningJobs = 0; var maxRunning = 0;
    var bounded = BoundedJobRunner.RunAsync(new[] { 1, 2, 3 }, 2, async (item, token) =>
    {
        var active = Interlocked.Increment(ref runningJobs);
        lock (started) maxRunning = Math.Max(maxRunning, active);
        started.Enqueue(item); if (started.Count == 2) twoStarted.TrySetResult();
        await releaseJobs.Task.WaitAsync(token);
        Interlocked.Decrement(ref runningJobs);
    }, CancellationToken.None);
    await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Require(started.Count == 2 && !started.Contains(3), "Two category jobs overlap while the third waits for a slot");
    releaseJobs.SetResult(); var boundedResults = await bounded;
    Require(maxRunning == 2 && boundedResults.All(item => !item.Cancelled && item.Error is null), "Scheduler enforces the configured maximum");
    var failureIsolation = await BoundedJobRunner.RunAsync(new[] { 1, 2, 3 }, 1,
        (item, _) => item == 1 ? Task.FromException(new IOException("worker failure")) : Task.CompletedTask, CancellationToken.None);
    Require(failureIsolation[0].Error is IOException && failureIsolation.Skip(1).All(item => item.Error is null),
        "One failed category releases its slot and does not suppress other categories");
    using var cancelBatch = new CancellationTokenSource();
    var activeTwo = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var cancelStarted = 0;
    var cancelRun = BoundedJobRunner.RunAsync(new[] { 1, 2, 3 }, 2, async (_, token) =>
    {
        if (Interlocked.Increment(ref cancelStarted) == 2) activeTwo.TrySetResult();
        await Task.Delay(Timeout.Infinite, token);
    }, cancelBatch.Token);
    await activeTwo.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelBatch.Cancel();
    var cancelledJobs = await cancelRun;
    Require(cancelStarted == 2 && cancelledJobs.All(item => item.Cancelled), "Stop cancels running jobs and queued jobs never start");
    Console.WriteLine("PASS: detection review/folder isolation/gestures/UTF8/progress/bulk approval/bounded concurrency/failure isolation/cancellation");
}
finally
{
    Directory.Delete(root, true);
}
static void Require(bool value, string name)
{
    if (!value) throw new Exception("FAILED: " + name);
}

sealed class InlineProgress(Action<string> report) : IProgress<string>
{
    public void Report(string value) => report(value);
}
