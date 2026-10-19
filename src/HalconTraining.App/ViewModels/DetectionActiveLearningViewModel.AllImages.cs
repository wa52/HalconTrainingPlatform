using HalconTraining.App.Services;

namespace HalconTraining.App.ViewModels;

public sealed partial class DetectionActiveLearningViewModel
{
    private async Task SaveCurrentTrainingEditsAsync()
    {
        if(_session is null||SelectedRow is null)return;
        if(Boxes.Select(b=>b.ToBox()).SequenceEqual(DetectionActiveLearningStore.ReviewBoxes(SelectedRow.Image,PredictionMaxBoxes)))return;
        var next=DetectionActiveLearningStore.CloneSession(_session);
        var image=next.Images.Single(i=>i.ImagePath==SelectedRow.Image.ImagePath);
        image.ReviewedBoxes=Boxes.Select(b=>b.ToBox()).ToList();image.HasDraft=true;image.Status="pending";
        await _store.SaveAsync(next);_session=next;
    }
}
