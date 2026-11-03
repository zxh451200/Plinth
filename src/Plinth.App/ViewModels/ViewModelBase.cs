using CommunityToolkit.Mvvm.ComponentModel;
using Plinth.App.Services;

namespace Plinth.App.ViewModels;

public abstract class ViewModelBase : ObservableObject
{
    // Set only by the host after explicit optional page construction succeeds.
    internal OptionalModuleState? OptionalModule { get; set; }
}
