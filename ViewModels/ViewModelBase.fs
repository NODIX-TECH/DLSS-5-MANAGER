namespace DLSS_5_MANAGER.ViewModels

open CommunityToolkit.Mvvm.ComponentModel

[<AbstractClass>]
type ViewModelBase() =
    inherit ObservableObject()

    member this.RaisePropertyChanged(propName: string) =
        this.OnPropertyChanged(propName)
