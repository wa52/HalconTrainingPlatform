using System.Windows.Controls;
using HalconTraining.App.ViewModels;

namespace HalconTraining.App.Views;

public partial class DetectionActiveLearningView : UserControl
{
    public DetectionActiveLearningView()
    {
        InitializeComponent();
        var errors = new HashSet<ValidationError>();
        Validation.AddErrorHandler(TrainingParameters, (_, args) =>
        {
            if (args.Action == ValidationErrorEventAction.Added) errors.Add(args.Error); else errors.Remove(args.Error);
            if (DataContext is DetectionActiveLearningViewModel vm) vm.HasParameterErrors = errors.Count > 0;
        });
        var predictionErrors = new HashSet<ValidationError>();
        EventHandler<ValidationErrorEventArgs> trackPredictionErrors = (_, args) =>
        {
            if (args.Action == ValidationErrorEventAction.Added) predictionErrors.Add(args.Error); else predictionErrors.Remove(args.Error);
            if (DataContext is DetectionActiveLearningViewModel vm) vm.HasPredictionParameterErrors = predictionErrors.Count > 0;
        };
        Validation.AddErrorHandler(PredictionOptions, trackPredictionErrors);
        Validation.AddErrorHandler(PredictionThresholdParameters, trackPredictionErrors);
    }
}
